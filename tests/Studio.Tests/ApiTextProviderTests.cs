using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Studio.Domain;
using Studio.Providers;
using Studio.Services;

namespace Studio.Tests;

public sealed class ApiTextProviderTests
{
    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static string Completion(string content = "{\"topic\":\"Đại dương\"}", string finish = "stop") => Json.Write(new { choices = new[] { new { finish_reason = finish, message = new { content } } } });
    private static ProviderContext Context(string id, Stage stage = Stage.Topic, string? secret = "test-only-secret")
    {
        var project = new Project { Topic = "original" };
        ProviderCatalog.ApplyPreset(project, "free");
        project.Steps[stage].Model = "manual/model";
        return new ProviderContext { JobId = Guid.NewGuid(), Project = project, Stage = stage, Config = new ProviderConfig { Id = id, Endpoint = "http://localhost:4321/proxy/v1/", Model = "saved-model" }, WorkDirectory = Path.GetTempPath(), Secret = secret, SaveCheckpoint = _ => Task.CompletedTask };
    }
    private static ApiTextProvider Provider(HttpClient client) => new(new ProviderHttp(new ClientFactory(client)));

    [Theory]
    [InlineData("openrouter", "https://openrouter.ai/api/v1/chat/completions", "max_completion_tokens")]
    [InlineData("zai", "https://api.z.ai/api/paas/v4/chat/completions", "max_tokens")]
    [InlineData("moonshot", "https://api.moonshot.ai/v1/chat/completions", "max_tokens")]
    [InlineData("minimax", "https://api.minimax.io/v1/chat/completions", "max_completion_tokens")]
    [InlineData("deepseek", "https://api.deepseek.com/chat/completions", "max_tokens")]
    [InlineData("xai", "https://api.x.ai/v1/chat/completions", "max_completion_tokens")]
    [InlineData("custom-text", "http://localhost:4321/proxy/v1/chat/completions", "max_tokens")]
    public async Task UsesCorrectFixedEndpointAndLimitsWithManualModel(string id, string url, string limitField)
    {
        var context = Context(id);
        using var client = new HttpClient(new RecordingHandler(async request =>
        {
            Assert.Equal(url, request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(context.Secret, request.Headers.Authorization.Parameter);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            Assert.Equal("manual/model", body["model"]!.GetValue<string>());
            Assert.Equal(BudgetService.OutputTokenLimit(context.Project, context.Stage), body[limitField]!.GetValue<int>());
            Assert.False(body["stream"]!.GetValue<bool>());
            Assert.Null(body["temperature"]);
            Assert.DoesNotContain(context.Secret!, body.ToJsonString());
            return Response(Completion());
        }));
        var result = await Provider(client).Execute(context, CancellationToken.None);
        Assert.Equal("Đại dương", result.Project.Topic);
        Assert.Equal("original", context.Project.Topic);
    }

    [Fact]
    public async Task AnthropicUsesMessagesHeadersAndOnlyFinalTextBlocks()
    {
        using var client = new HttpClient(new RecordingHandler(async request =>
        {
            Assert.Equal("https://api.anthropic.com/v1/messages", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("test-only-secret", Assert.Single(request.Headers.GetValues("x-api-key")));
            Assert.Equal("2023-06-01", Assert.Single(request.Headers.GetValues("anthropic-version")));
            Assert.NotNull(JsonNode.Parse(await request.Content!.ReadAsStringAsync())!["max_tokens"]);
            return Response("{\"stop_reason\":\"end_turn\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"hidden\"},{\"type\":\"text\",\"text\":\"{\\\"topic\\\":\\\"Ocean\\\"}\"}]}");
        }));
        var result = await Provider(client).Execute(Context("anthropic"), CancellationToken.None);
        Assert.Equal("Ocean", result.Project.Topic);
        Assert.DoesNotContain("hidden", result.Output);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("content_filter")]
    [InlineData("tool_calls")]
    [InlineData("")]
    public async Task RejectsIncompleteCompletionsEvenWhenContentIsValidJson(string finish)
    {
        using var client = new HttpClient(new RecordingHandler(_ => Task.FromResult(Response(Completion(finish: finish)))));
        var ex = await Assert.ThrowsAsync<ProviderException>(() => Provider(client).Execute(Context("deepseek"), CancellationToken.None));
        Assert.True(ex.MayHaveCharged);
    }

    [Theory]
    [InlineData("{\"stop_reason\":\"max_tokens\",\"content\":[{\"type\":\"text\",\"text\":\"{\\\"topic\\\":\\\"Ocean\\\"}\"}]}")]
    [InlineData("{\"stop_reason\":\"tool_use\",\"content\":[]}")]
    public async Task AnthropicRejectsIncompleteOutput(string body)
    {
        using var client = new HttpClient(new RecordingHandler(_ => Task.FromResult(Response(body))));
        var ex = await Assert.ThrowsAsync<ProviderException>(() => Provider(client).Execute(Context("anthropic"), CancellationToken.None));
        Assert.True(ex.MayHaveCharged);
    }

    [Theory]
    [InlineData("<think>internal</think>\n{\"topic\":\"Ocean\"}", "Ocean")]
    [InlineData("```json\n{\"topic\":\"Ocean\"}\n```", "Ocean")]
    [InlineData("```json\r\n{\"topic\":\"Ocean\"}\r\n```", "Ocean")]
    [InlineData("{\"topic\":\"Literal <think> tag\"}", "Literal <think> tag")]
    public async Task RemovesOnlyLeadingReasoningAndCompleteJsonFences(string content, string expected)
    {
        using var client = new HttpClient(new RecordingHandler(_ => Task.FromResult(Response(Completion(content)))));
        var result = await Provider(client).Execute(Context("minimax"), CancellationToken.None);
        Assert.Equal(expected, result.Project.Topic);
    }

    [Theory]
    [InlineData("<think>unfinished")]
    [InlineData("{\"topic\":123}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("{not json secret-test}")]
    public async Task InvalidOutputIsSanitizedAndNeverApplied(string content)
    {
        var context = Context("minimax");
        using var client = new HttpClient(new RecordingHandler(_ => Task.FromResult(Response(Completion(content)))));
        var ex = await Assert.ThrowsAsync<ProviderException>(() => Provider(client).Execute(context, CancellationToken.None));
        Assert.True(ex.MayHaveCharged);
        Assert.DoesNotContain("secret-test", ex.Message);
        Assert.Equal("original", context.Project.Topic);
    }

    [Fact]
    public async Task InvalidSceneCannotMutateProject()
    {
        var context = Context("moonshot", Stage.Script);
        context.Project.Scenes.Add(new Scene { Narration = "original scene" });
        using var client = new HttpClient(new RecordingHandler(_ => Task.FromResult(Response(Completion("{\"scenes\":[{\"narration\":\"new\",\"visualPrompt\":\"ocean\",\"durationSeconds\":-1}]}")))));
        await Assert.ThrowsAsync<ProviderException>(() => Provider(client).Execute(context, CancellationToken.None));
        Assert.Equal("original scene", Assert.Single(context.Project.Scenes).Narration);
    }

    [Theory]
    [InlineData("http://user:password@localhost/v1")]
    [InlineData("https://example.test/v1?key=secret")]
    [InlineData("https://example.test/v1#fragment")]
    [InlineData("file:///tmp/model")]
    [InlineData("")]
    public async Task CustomEndpointValidationRejectsUnsafeOrAmbiguousUrls(string endpoint)
    {
        using var client = new HttpClient(new RecordingHandler(_ => throw new Exception("Network should not run")));
        await Assert.ThrowsAsync<ProviderException>(() => Provider(client).Validate(new ProviderConfig { Id = "custom-text", Endpoint = endpoint }, Stage.Topic, CancellationToken.None));
    }

    [Fact]
    public async Task CustomEndpointSupportsNoApiKey()
    {
        using var client = new HttpClient(new RecordingHandler(request =>
        {
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(Response(Completion()));
        }));
        Assert.Equal("Đại dương", (await Provider(client).Execute(Context("custom-text", secret: null), CancellationToken.None)).Project.Topic);
    }

    [Theory]
    [InlineData("zai")]
    [InlineData("minimax")]
    public async Task UnsupportedDiscoveryIsHonestAndNeverRunsInference(string id)
    {
        using var client = new HttpClient(new RecordingHandler(_ => throw new Exception("No network needed")));
        var provider = Provider(client);
        Assert.Empty(await provider.GetModels(Context(id).Config, "key", CancellationToken.None));
        Assert.Contains("Chưa xác minh", await provider.CheckConnection(Context(id).Config, "key", CancellationToken.None));
    }

    [Fact]
    public async Task OptionalDiscovery404DoesNotPreventManualGeneration()
    {
        var calls = 0;
        using var client = new HttpClient(new RecordingHandler(request =>
        {
            calls++;
            return Task.FromResult(request.Method == HttpMethod.Get ? Response("secret body", HttpStatusCode.NotFound) : Response(Completion()));
        }));
        var provider = Provider(client);
        Assert.Contains("Chưa xác minh", await provider.CheckConnection(Context("custom-text").Config, null, CancellationToken.None));
        Assert.Equal(1, calls);
        Assert.Equal("Đại dương", (await provider.Execute(Context("custom-text"), CancellationToken.None)).Project.Topic);
    }

    [Theory]
    [InlineData("openrouter", "https://openrouter.ai/api/v1/models")]
    [InlineData("anthropic", "https://api.anthropic.com/v1/models")]
    [InlineData("moonshot", "https://api.moonshot.ai/v1/models")]
    [InlineData("deepseek", "https://api.deepseek.com/models")]
    [InlineData("xai", "https://api.x.ai/v1/models")]
    [InlineData("custom-text", "http://localhost:4321/proxy/v1/models")]
    public async Task DiscoveryUsesGetAndPreservesProviderPaths(string id, string url)
    {
        using var client = new HttpClient(new RecordingHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Content);
            Assert.Equal(url, request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Response("{\"data\":[{\"id\":\"test/model\",\"display_name\":\"Test Model\"},{\"id\":\"test/model\"}]}"));
        }));
        var model = Assert.Single(await Provider(client).GetModels(Context(id).Config, "key", CancellationToken.None));
        Assert.Equal("test/model", model.Id);
        Assert.Equal("Test Model", model.Name);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task DiscoveryFailureDoesNotExposeProviderBodyOrClaimCharges(int status)
    {
        using var client = new HttpClient(new RecordingHandler(_ => Task.FromResult(Response("sensitive-remote-body", (HttpStatusCode)status))));
        var ex = await Assert.ThrowsAsync<ProviderException>(() => Provider(client).GetModels(Context("xai").Config, "key", CancellationToken.None));
        Assert.False(ex.MayHaveCharged);
        Assert.DoesNotContain("sensitive", ex.Message);
    }

    [Fact]
    public async Task CancellationPropagatesBeforeSending()
    {
        using var client = new HttpClient(new RecordingHandler(_ => throw new Exception("Should not send")));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(client).Execute(Context("deepseek"), cts.Token));
    }

    [Fact]
    public async Task MissingKeyFailsBeforeSending()
    {
        using var client = new HttpClient(new RecordingHandler(_ => throw new Exception("Should not send")));
        await Assert.ThrowsAsync<ProviderException>(() => Provider(client).Execute(Context("deepseek", secret: null), CancellationToken.None));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":123}}]}")]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"reasoning_content\":\"private thoughts\"}}]}")]
    public async Task MalformedEnvelopesAndReasoningOnlyRepliesAreRejected(string body)
    {
        using var client = new HttpClient(new RecordingHandler(_ => Task.FromResult(Response(body))));
        var ex = await Assert.ThrowsAsync<ProviderException>(() => Provider(client).Execute(Context("deepseek"), CancellationToken.None));
        Assert.True(ex.MayHaveCharged);
        Assert.DoesNotContain("private thoughts", ex.Message);
    }

    [Fact]
    public async Task NonFiniteSceneDurationIsRejected()
    {
        using var client = new HttpClient(new RecordingHandler(_ => Task.FromResult(Response(Completion("{\"scenes\":[{\"narration\":\"new\",\"visualPrompt\":\"ocean\",\"durationSeconds\":1e999}]}")))));
        var ex = await Assert.ThrowsAsync<ProviderException>(() => Provider(client).Execute(Context("moonshot", Stage.Script), CancellationToken.None));
        Assert.True(ex.MayHaveCharged);
    }

    [Fact]
    public async Task TimeoutIsReportedAsPossiblyChargedWithoutLeakingDetails()
    {
        using var client = new HttpClient(new RecordingHandler(_ => throw new TaskCanceledException("sensitive timeout details")));
        var ex = await Assert.ThrowsAsync<ProviderException>(() => Provider(client).Execute(Context("deepseek"), CancellationToken.None));
        Assert.True(ex.MayHaveCharged);
        Assert.DoesNotContain("sensitive", ex.Message);
    }
}
