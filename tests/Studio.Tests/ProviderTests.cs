using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Studio.Domain;
using Studio.Providers;

namespace Studio.Tests;

public sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request);
}
public sealed class ClientFactory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
public sealed class ProviderTests
{
    private static HttpResponseMessage Body(string value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    [Theory]
    [InlineData("gemini", "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"{\\\"topic\\\":\\\"Đại dương\\\"}\"}]}}]}")]
    [InlineData("ollama", "{\"response\":\"{\\\"topic\\\":\\\"Đại dương\\\"}\"}")]
    [InlineData("openai-text", "{\"choices\":[{\"message\":{\"content\":\"{\\\"topic\\\":\\\"Đại dương\\\"}\"}}]}")]
    public async Task TextAdaptersSendStructuredRequestsAndApplyValidatedResults(string id, string response)
    {
        string? body = null; Uri? uri = null;
        using var client = new HttpClient(new RecordingHandler(async req => { uri = req.RequestUri; body = await req.Content!.ReadAsStringAsync(); return Body(response); }));
        var provider = new TextProvider(new ProviderHttp(new ClientFactory(client)));
        var p = new Project(); ProviderCatalog.ApplyPreset(p, "free");
        var c = new ProviderContext { JobId = Guid.NewGuid(), Project = p, Stage = Stage.Topic, WorkDirectory = Path.GetTempPath(), Config = new ProviderConfig { Id = id, Model = "test", Endpoint = "http://localhost:11434" }, Secret = "test-only", SaveCheckpoint = _ => Task.CompletedTask };
        var result = await provider.Execute(c, CancellationToken.None);
        Assert.Equal("Đại dương", result.Project.Topic); Assert.NotNull(body); Assert.Contains("Vietnamese", body); Assert.DoesNotContain("test-only", body); Assert.NotNull(uri);
        if (id == "openai-text") Assert.Contains("max_completion_tokens", body);
        if (id == "gemini") Assert.Contains("maxOutputTokens", body);
    }
    [Theory]
    [InlineData(401, false)] [InlineData(403, false)] [InlineData(429, false)] [InlineData(500, true)]
    public async Task HttpFailuresPreserveChargeUncertaintyAndRedactRemoteBodies(int code, bool uncertain)
    {
        var calls = 0; using var client = new HttpClient(new RecordingHandler(_ => { calls++; return Task.FromResult(Body("sensitive-provider-body", (HttpStatusCode)code)); }));
        var http = new ProviderHttp(new ClientFactory(client)); using var req = ProviderHttp.Request(HttpMethod.Post, "https://api.example.test/action", new { input = "test" });
        var ex = await Assert.ThrowsAsync<ProviderException>(() => http.Json(req, CancellationToken.None, true));
        Assert.Equal(uncertain, ex.MayHaveCharged); Assert.DoesNotContain("sensitive", ex.Message); Assert.Equal(1, calls);
    }
    [Fact]
    public async Task MalformedResponsesFailInsteadOfProducingPlaceholderContent()
    {
        using var client = new HttpClient(new RecordingHandler(_ => Task.FromResult(Body("not-json"))));
        using var req = ProviderHttp.Request(HttpMethod.Get, "https://api.example.test/read");
        await Assert.ThrowsAsync<ProviderException>(() => new ProviderHttp(new ClientFactory(client)).Json(req, CancellationToken.None));
        Assert.Throws<ProviderException>(() => TextProvider.Apply(new Project(), Stage.Script, "{\"scenes\":[]}"));
    }
    [Fact]
    public void ShotstackTimelineUsesSameSceneAndWordTimesAndEscapesText()
    {
        var p = new Project { Scenes = [new Scene { AudioSeconds = 2, DurationSeconds = 3, AudioAssetId = "audio", MediaAssetId = "media" }], Captions = [new("<xin>", .2, .6), new("chào", .6, 1.2)] };
        var timeline = JsonNode.Parse(Json.Write(RenderProvider.Timeline(p, new Dictionary<string, string> { ["audio"] = "https://example.test/a.wav", ["media"] = "https://example.test/i.png" }, _ => false)))!;
        Assert.Equal(3, timeline["timeline"]!["tracks"]![1]!["clips"]![0]!["length"]!.GetValue<double>());
        Assert.Equal(.2, timeline["timeline"]!["tracks"]![0]!["clips"]![0]!["start"]!.GetValue<double>());
        Assert.Contains("&lt;xin&gt;", timeline["timeline"]!["tracks"]![0]!["clips"]![0]!["asset"]!["html"]!.GetValue<string>());
        Assert.Equal("9:16", timeline["output"]!["aspectRatio"]!.GetValue<string>());
    }
    [Fact]
    public void AllStagesHaveRealFreeLocalAndPaidAdaptersInCatalog()
    {
        foreach (var stage in Pipeline.Order)
        {
            var defs = ProviderCatalog.For(stage).ToList(); Assert.Contains(defs, p => p.Tier is ProviderTier.FreeCloud or ProviderTier.Sandbox); Assert.Contains(defs, p => p.Tier == ProviderTier.SelfHosted); Assert.Contains(defs, p => p.Tier == ProviderTier.Paid);
        }
        using var host = new TestHost(); var registry = host.Get<ProviderRegistry>(); foreach (var def in ProviderCatalog.All) Assert.NotNull(registry.Get(def.Id));
    }
}
