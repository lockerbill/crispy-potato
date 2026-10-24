using System.Text.Json.Nodes;
using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Models;
using FacelessStudio.Core.Settings;
using FacelessStudio.Providers.Media;
using FacelessStudio.Providers.Tts;

namespace FacelessStudio.Tests;

public class MediaProviderTests
{
    private static readonly Scene Scene = new("Biển rất sâu.", "ocean waves", "deep blue ocean");

    [Fact]
    public void ElevenLabs_alignment_groups_characters_into_words()
    {
        var alignment = JsonNode.Parse("""
            {"characters":["H","i"," ","b","ạ","n"],
             "character_start_times_seconds":[0.0,0.1,0.2,0.3,0.4,0.5],
             "character_end_times_seconds":[0.1,0.2,0.3,0.4,0.5,0.6]}
            """);
        var words = ElevenLabsTtsProvider.WordsFromAlignment(alignment)!;
        Assert.Equal(["Hi", "bạn"], words.Select(w => w.Word));
        Assert.Equal(0.3, words[1].Start, 3);
        Assert.Equal(0.6, words[1].End, 3);
    }

    [Fact]
    public async Task ElevenLabs_writes_audio_and_returns_timings()
    {
        var audio = Convert.ToBase64String([1, 2, 3]);
        var http = new FakeHttp().On("api.elevenlabs.io",
            "{\"audio_base64\":\"" + audio + "\",\"alignment\":{\"characters\":[\"a\"]," +
            "\"character_start_times_seconds\":[0],\"character_end_times_seconds\":[0.2]}}");
        using var sp = http.BuildServices();
        var ctx = FakeHttp.Context(new() { [SettingKeys.ElevenLabsApiKey] = "el" });
        var res = await FakeHttp.Provider<ITtsProvider>(sp, "tts-elevenlabs")
            .SynthesizeAsync("a", Path.Combine(ctx.WorkDir, "v"), ctx);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(res.AudioPath));
        Assert.Single(res.Words!);
        var req = Assert.Single(http.Requests);
        Assert.Contains("/v1/text-to-speech/21m00Tcm4TlvDq8ikWAM/with-timestamps", req.Url);
        Assert.Equal("eleven_turbo_v2_5", JsonNode.Parse(req.Body)!["model_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Pexels_picks_sharp_small_file_and_skips_used_clips()
    {
        var search = """
            {"videos":[
              {"id":1,"user":{"name":"An"},"video_files":[
                 {"file_type":"video/mp4","width":720,"height":1280,"link":"https://cdn/1-sd.mp4"},
                 {"file_type":"video/mp4","width":1080,"height":1920,"link":"https://cdn/1-hd.mp4"},
                 {"file_type":"video/mp4","width":2160,"height":3840,"link":"https://cdn/1-4k.mp4"}]},
              {"id":2,"user":{"name":"Bình"},"video_files":[
                 {"file_type":"video/mp4","width":1080,"height":1920,"link":"https://cdn/2-hd.mp4"}]}]}
            """;
        var http = new FakeHttp().On("api.pexels.com", search).OnBytes("https://cdn/", [0, 1], "video/mp4");
        using var sp = http.BuildServices();
        var provider = FakeHttp.Provider<IMediaProvider>(sp, "media-pexels");
        var ctx = FakeHttp.Context(new() { [SettingKeys.PexelsApiKey] = "px" });

        var first = await provider.GetAsync(new MediaRequest(Scene, 0, 1, 5, 1080, 1920), ctx);
        var second = await provider.GetAsync(new MediaRequest(Scene, 1, 1, 5, 1080, 1920), ctx);

        Assert.Equal("https://cdn/1-hd.mp4", Assert.Single(first).PublicUrl);
        Assert.Equal("https://cdn/2-hd.mp4", Assert.Single(second).PublicUrl);
        Assert.Contains("orientation=portrait", http.Requests[0].Url);
        Assert.Equal("px", http.Requests[0].Headers["authorization"]);
        Assert.True(File.Exists(first[0].LocalPath));
    }

    [Fact]
    public async Task Pixabay_prefers_matching_orientation()
    {
        var search = """
            {"hits":[
              {"id":10,"user":"u1","videos":{"large":{"url":"https://px/10.mp4","width":1920,"height":1080}}},
              {"id":11,"user":"u2","videos":{"large":{"url":"https://px/11.mp4","width":1080,"height":1920}}}]}
            """;
        var http = new FakeHttp().On("pixabay.com/api/videos", search).OnBytes("https://px/", [0]);
        using var sp = http.BuildServices();
        var media = await FakeHttp.Provider<IMediaProvider>(sp, "media-pixabay").GetAsync(
            new MediaRequest(Scene, 0, 1, 5, 1080, 1920), FakeHttp.Context(new() { [SettingKeys.PixabayApiKey] = "k" }));
        Assert.Equal("https://px/11.mp4", Assert.Single(media).PublicUrl);
    }

    [Fact]
    public async Task OpenAI_images_saves_base64_png()
    {
        var http = new FakeHttp().On("api.openai.com/v1/images/generations",
            $$"""{"data":[{"b64_json":"{{Convert.ToBase64String([9, 9])}}"}]}""");
        using var sp = http.BuildServices();
        var media = await FakeHttp.Provider<IMediaProvider>(sp, "media-openai").GetAsync(
            new MediaRequest(Scene, 3, 2, 5, 1920, 1080), FakeHttp.Context(new() { [SettingKeys.OpenAiApiKey] = "o" }));
        var asset = Assert.Single(media);
        Assert.Equal(MediaKind.Image, asset.Kind);
        Assert.Equal([9, 9], await File.ReadAllBytesAsync(asset.LocalPath!));
        var body = JsonNode.Parse(http.Requests[0].Body)!;
        Assert.Equal("1536x1024", body["size"]!.GetValue<string>());
        Assert.Contains("deep blue ocean", body["prompt"]!.GetValue<string>());
    }

    [Fact]
    public async Task ComfyUI_queues_workflow_polls_history_and_downloads()
    {
        var http = new FakeHttp()
            .On("/prompt", """{"prompt_id":"p1"}""")
            .On("/history/p1", """{"p1":{"outputs":{"9":{"images":[{"filename":"a.png","subfolder":"","type":"output"}]}}}}""")
            .OnBytes("/view?filename=a.png", [7], "image/png");
        using var sp = http.BuildServices();
        var media = await FakeHttp.Provider<IMediaProvider>(sp, "media-comfyui").GetAsync(
            new MediaRequest(Scene, 0, 1, 5, 1080, 1920), FakeHttp.Context(new()));
        Assert.Equal([7], await File.ReadAllBytesAsync(Assert.Single(media).LocalPath!));
        var workflow = JsonNode.Parse(http.Requests[0].Body)!["prompt"]!;
        Assert.Equal(768, workflow["5"]!["inputs"]!["width"]!.GetValue<int>());
        Assert.Contains("deep blue ocean", workflow["6"]!["inputs"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ComfyUI_workflow_escapes_prompt_quotes()
    {
        var json = ComfyUiMediaProvider.BuildWorkflow("""{"t":"{{PROMPT}}","w":{{WIDTH}}}""", "say \"hi\"", "m", 5, 6, 1);
        Assert.Equal("say \"hi\"", JsonNode.Parse(json)!["t"]!.GetValue<string>());
    }
}
