using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Studio.Domain;
using Studio.Providers;
using Studio.Services;

namespace Studio.Tests;

public sealed class MediaAdapterTests
{
    private static HttpResponseMessage JsonResponse(object value) => new(HttpStatusCode.OK) { Content = new StringContent(Json.Write(value), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Bytes(byte[] bytes, string type) { var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }; r.Content.Headers.ContentType = new(type); return r; }
    private static ProviderContext Context(Project p, Stage stage, string provider, string dir, string? checkpoint = null) => new()
    {
        Project = p, Stage = stage, JobId = Guid.NewGuid(), WorkDirectory = dir, Config = new ProviderConfig { Id = provider, Model = ProviderCatalog.Get(provider).Model, Voice = "voice-1", Region = "southeastasia", AccountId = new string('a', 32), Endpoint = "http://localhost:8188" },
        Secret = "not-a-real-key", Checkpoint = checkpoint, SaveCheckpoint = _ => Task.CompletedTask
    };
    private static async Task<byte[]> Image(TestHost host, string dir)
    {
        var file = Path.Combine(dir, "fixture.png");
        await host.Get<ProcessRunner>().Run("ffmpeg", ["-y", "-v", "error", "-f", "lavfi", "-i", "color=c=green:s=320x180", "-frames:v", "1", "-threads", "1", file], CancellationToken.None);
        return await File.ReadAllBytesAsync(file);
    }
    private static async Task<byte[]> Audio(TestHost host, string dir)
    {
        var file = Path.Combine(dir, "fixture.wav");
        await host.Get<ProcessRunner>().Run("ffmpeg", ["-y", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-ar", "16000", "-ac", "1", file], CancellationToken.None);
        return await File.ReadAllBytesAsync(file);
    }
    [Theory]
    [InlineData("azure-speech")] [InlineData("elevenlabs")]
    public async Task VoiceAdaptersSendLanguageAndCredentialsAndMeasureRealAudio(string id)
    {
        using var host = new TestHost(); var store = host.Get<AssetStore>(); var dir = store.WorkDir(Guid.NewGuid()); var audio = await Audio(host, dir);
        using var client = new HttpClient(new RecordingHandler(async req =>
        {
            var body = await req.Content!.ReadAsStringAsync();
            Assert.True(req.Headers.Contains(id == "azure-speech" ? "Ocp-Apim-Subscription-Key" : "xi-api-key"));
            if (id == "azure-speech") { Assert.Contains("vi-VN", body); Assert.Contains("&amp;", body); }
            else { var json = JsonNode.Parse(body)!; Assert.Equal("vi", json["language_code"]!.GetValue<string>()); Assert.Equal("eleven_v3", json["model_id"]!.GetValue<string>()); }
            return Bytes(audio, "audio/wav");
        }));
        var provider = new VoiceProvider(new ProviderHttp(new ClientFactory(client)), host.Get<ProcessRunner>(), host.Get<MediaEngine>(), store);
        var p = new Project { Scenes = [new Scene { Narration = "Xin chào & cảm ơn" }] }; ProviderCatalog.ApplyPreset(p, "free"); p.Steps[Stage.Voice].Model = "eleven_v3";
        var result = await provider.Execute(Context(p, Stage.Voice, id, dir), CancellationToken.None);
        Assert.InRange(result.Project.Scenes[0].AudioSeconds, .99, 1.01); Assert.NotNull(result.Project.Scenes[0].AudioAssetId);
    }
    [Theory]
    [InlineData("cloudflare")] [InlineData("openai-image")]
    public async Task ImageAdaptersDecodeBackgroundAndCompositeVietnameseThumbnail(string id)
    {
        using var host = new TestHost(); var store = host.Get<AssetStore>(); var dir = store.WorkDir(Guid.NewGuid()); var png = await Image(host, dir);
        using var client = new HttpClient(new RecordingHandler(async req =>
        {
            Assert.NotNull(req.Headers.Authorization); var body = JsonNode.Parse(await req.Content!.ReadAsStringAsync())!; Assert.Contains("no text", body["prompt"]!.GetValue<string>());
            return id == "cloudflare" ? JsonResponse(new { result = new { image = Convert.ToBase64String(png) } }) : JsonResponse(new { data = new[] { new { b64_json = Convert.ToBase64String(png) } } });
        }));
        var p = new Project { Title = "Khám phá đại dương" }; ProviderCatalog.ApplyPreset(p, "free"); p.Steps[Stage.Thumbnail].Model = ProviderCatalog.Get(id).Model;
        var provider = new ImageProvider(new ProviderHttp(new ClientFactory(client)), store, host.Get<MediaEngine>());
        var result = await provider.Execute(Context(p, Stage.Thumbnail, id, dir), CancellationToken.None);
        var asset = await store.Get(result.Project.ThumbnailAssetId!, p.Id); Assert.Equal("image/jpeg", asset.ContentType); Assert.True(asset.Bytes > 1000);
    }
    [Fact]
    public async Task PexelsPersistsCreditAndNeverForwardsApiKeyToImageHost()
    {
        using var host = new TestHost(); var store = host.Get<AssetStore>(); var dir = store.WorkDir(Guid.NewGuid()); var png = await Image(host, dir);
        using var client = new HttpClient(new RecordingHandler(req =>
        {
            if (req.RequestUri!.Host == "api.pexels.com")
            {
                Assert.NotNull(req.Headers.Authorization);
                return Task.FromResult(JsonResponse(new { photos = new[] { new { src = new { large2x = "https://images.pexels.com/test.jpg" }, photographer = "Test photographer", url = "https://www.pexels.com/photo/1" } } }));
            }
            Assert.Null(req.Headers.Authorization); return Task.FromResult(Bytes(png, "image/png"));
        }));
        var p = new Project { Scenes = [new Scene { VisualPrompt = "ocean" }] }; ProviderCatalog.ApplyPreset(p, "free");
        var provider = new ImageProvider(new ProviderHttp(new ClientFactory(client)), store, host.Get<MediaEngine>());
        var result = await provider.Execute(Context(p, Stage.Media, "pexels", dir), CancellationToken.None);
        Assert.Contains("Test photographer", result.Project.Scenes[0].Attribution); Assert.NotNull(result.Project.Scenes[0].MediaAssetId);
    }
    [Theory]
    [InlineData("groq")] [InlineData("openai-whisper")]
    public async Task TranscriptionUsesWordTimestampsAndOffsetsForScenePadding(string id)
    {
        using var host = new TestHost(); var store = host.Get<AssetStore>(); var dir = store.WorkDir(Guid.NewGuid()); await Audio(host, dir);
        var p = new Project(); ProviderCatalog.ApplyPreset(p, "free"); p.Steps[Stage.Captions].Model = ProviderCatalog.Get(id).Model;
        var audio = await store.Add(p.Id, Path.Combine(dir, "fixture.wav"), "voice.wav", "audio/wav", CancellationToken.None);
        p.Scenes = [new Scene { AudioAssetId = audio.Id, AudioSeconds = 1, DurationSeconds = 2 }, new Scene { AudioAssetId = audio.Id, AudioSeconds = 1, DurationSeconds = 2 }];
        using var client = new HttpClient(new RecordingHandler(async req =>
        {
            var body = await req.Content!.ReadAsStringAsync(); Assert.Contains("timestamp_granularities[]", body); Assert.Contains("verbose_json", body);
            return JsonResponse(new { words = new[] { new { word = "xin", start = 0.1, end = 0.5 }, new { word = "chào", start = 0.5, end = 0.9 } } });
        }));
        var provider = new CaptionProvider(new ProviderHttp(new ClientFactory(client)), host.Get<ProcessRunner>(), store);
        var result = await provider.Execute(Context(p, Stage.Captions, id, dir), CancellationToken.None);
        Assert.Equal(4, result.Project.Captions.Count); Assert.Equal(2.1, result.Project.Captions[2].Start); Assert.Equal(2.9, result.Project.Captions[3].End);
    }
    [Fact]
    public async Task ComfyCancellationTargetsQueuedPromptWithoutGlobalInterrupt()
    {
        using var host = new TestHost();
        using var client = new HttpClient(new RecordingHandler(async req =>
        {
            Assert.Equal("/queue", req.RequestUri!.AbsolutePath);
            var body = JsonNode.Parse(await req.Content!.ReadAsStringAsync())!;
            Assert.Equal("prompt-owned-by-this-job", body["delete"]![0]!.GetValue<string>());
            return JsonResponse(new { });
        }));
        var provider = new ImageProvider(new ProviderHttp(new ClientFactory(client)), host.Get<AssetStore>(), host.Get<MediaEngine>());
        await provider.Cancel(Context(new Project(), Stage.Media, "comfyui", Path.GetTempPath(), Json.Write(new { kind = "comfy", promptId = "prompt-owned-by-this-job" })), CancellationToken.None);
    }
    [Fact]
    public async Task ComfyResumePollsExistingPromptInsteadOfGeneratingAgain()
    {
        using var host = new TestHost(); var store = host.Get<AssetStore>(); var dir = store.WorkDir(Guid.NewGuid()); var png = await Image(host, dir);
        var requests = new List<string>();
        using var client = new HttpClient(new RecordingHandler(req =>
        {
            requests.Add(req.Method + " " + req.RequestUri!.AbsolutePath);
            if (req.RequestUri.AbsolutePath.StartsWith("/history/", StringComparison.Ordinal)) return Task.FromResult(JsonResponse(new Dictionary<string, object> { ["prompt-1"] = new { outputs = new Dictionary<string, object> { ["9"] = new { images = new[] { new { filename = "result.png", subfolder = "" } } } } } }));
            return Task.FromResult(Bytes(png, "image/png"));
        }));
        var p = new Project { Scenes = [new Scene { VisualPrompt = "Ocean" }] }; ProviderCatalog.ApplyPreset(p, "local");
        var provider = new ImageProvider(new ProviderHttp(new ClientFactory(client)), store, host.Get<MediaEngine>());
        var result = await provider.Execute(Context(p, Stage.Media, "comfyui", dir, Json.Write(new { kind = "comfy", index = 0, promptId = "prompt-1", project = p })), CancellationToken.None);
        Assert.NotNull(result.Project.Scenes[0].MediaAssetId); Assert.Equal(new[] { "GET /history/prompt-1", "GET /view" }, requests);
    }
}
