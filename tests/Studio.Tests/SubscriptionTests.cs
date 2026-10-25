using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Studio.Domain;
using Studio.Providers;
using Studio.Services;

namespace Studio.Tests;

public sealed class SubscriptionTests
{
    private static HttpResponseMessage Body(string value, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static IConfiguration Config() => new ConfigurationBuilder().Build();
    private static Dictionary<string, string> Query(string url) => new Uri(url).Query.TrimStart('?').Split('&').Select(x => x.Split('=', 2)).ToDictionary(x => Uri.UnescapeDataString(x[0]), x => Uri.UnescapeDataString(x[1]));
    [Theory]
    [InlineData("valid")]
    [InlineData("nonce")]
    [InlineData("audience")]
    [InlineData("signature")]
    public async Task ChatGptValidatesSignedIdentityAndKeepsTokensOutOfMetadata(string variant)
    {
        using var host = new TestHost(); var settings = host.Get<SettingsService>();
        using var rsa = RSA.Create(2048); var key = new RsaSecurityKey(rsa) { KeyId = "test-key" };
        using var otherRsa = RSA.Create(2048); var otherKey = new RsaSecurityKey(otherRsa) { KeyId = key.KeyId };
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(rsa.ExportParameters(false)) { KeyId = key.KeyId });
        string nonce = ""; string? exchange = null;
        using var client = new HttpClient(new RecordingHandler(async req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("openid-configuration")) return Body("{\"issuer\":\"https://auth.openai.com\",\"jwks_uri\":\"https://auth.openai.com/jwks\"}");
            if (req.RequestUri.AbsolutePath == "/jwks") return Body(new JsonObject { ["keys"] = new JsonArray(JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(jwk))) }.ToJsonString());
            exchange = await req.Content!.ReadAsStringAsync();
            var jwt = new JwtSecurityToken("https://auth.openai.com", variant == "audience" ? "wrong" : "issued-client", [new Claim("sub", "account-1"), new Claim("nonce", variant == "nonce" ? "wrong" : nonce)], DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5), new SigningCredentials(variant == "signature" ? otherKey : key, SecurityAlgorithms.RsaSha256));
            return Body(Json.Write(new { access_token = "private-access", refresh_token = "private-refresh", expires_in = 3600, scope = "chatgpt.tokens.use.direct", id_token = new JwtSecurityTokenHandler().WriteToken(jwt) }));
        }));
        var auth = new SubscriptionAuthService(settings, new ClientFactory(client), Config());
        var start = await auth.Begin("chatgpt-subscription", "session", default); var query = Query(start.AuthorizationUrl!); nonce = query["nonce"];
        Assert.Equal("dynamic_agent_client", query["client_id"]); Assert.Equal("S256", query["code_challenge_method"]);
        if (variant != "valid")
        {
            await Assert.ThrowsAsync<ProviderException>(() => auth.CompleteChatGpt(start.FlowId, "code", "issued-client", "session", default));
            Assert.Null(await settings.Secret("oauth:chatgpt-subscription")); return;
        }
        await auth.CompleteChatGpt(start.FlowId, "code", "issued-client", "session", default);
        Assert.Equal("private-access", await auth.AccessToken("chatgpt-subscription", false, default));
        Assert.Contains("code_verifier=", exchange); Assert.DoesNotContain("dynamic_agent_client", exchange);
        var metadata = Json.Write(await settings.Get<SubscriptionAuthService.Metadata>("oauth:chatgpt-subscription"));
        Assert.DoesNotContain("private-", metadata); Assert.Contains("account-1", metadata);
        await Assert.ThrowsAsync<ProviderException>(() => auth.CompleteChatGpt(start.FlowId, "code", "issued-client", "session", default));
    }
    [Fact]
    public async Task CallbackBindingAndCancellationPreserveExistingConnection()
    {
        using var host = new TestHost(); var settings = host.Get<SettingsService>();
        await Seed(settings, "chatgpt-subscription", DateTimeOffset.UtcNow.AddHours(1));
        using var client = new HttpClient(new RecordingHandler(_ => throw new Exception("Must not contact token endpoint")));
        var auth = new SubscriptionAuthService(settings, new ClientFactory(client), Config());
        var start = await auth.Begin("chatgpt-subscription", "correct", default);
        await Assert.ThrowsAsync<ProviderException>(() => auth.CompleteChatGpt(start.FlowId, "code", "issued", "wrong", default));
        await auth.CancelPending("chatgpt-subscription", start.FlowId, "correct", default);
        Assert.True((await auth.Status("chatgpt-subscription", default)).Connected);
        await Assert.ThrowsAsync<ProviderException>(() => auth.CompleteChatGpt(start.FlowId, "code", "issued", "correct", default));
        await auth.Disconnect("chatgpt-subscription", default);
        Assert.False((await auth.Status("chatgpt-subscription", default)).Connected);
    }
    [Fact]
    public async Task RefreshIsSerializedAndDisconnectWinsAgainstInFlightRefresh()
    {
        using var host = new TestHost(); var settings = host.Get<SettingsService>(); await Seed(settings, "supergrok", DateTimeOffset.UtcNow.AddMinutes(-1));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        using var client = new HttpClient(new RecordingHandler(async _ => { calls++; started.TrySetResult(); await release.Task; return Body("{\"access_token\":\"new-access\",\"refresh_token\":\"new-refresh\",\"expires_in\":3600}"); }));
        var auth = new SubscriptionAuthService(settings, new ClientFactory(client), Config());
        var first = auth.AccessToken("supergrok", false, default); await started.Task;
        var second = auth.AccessToken("supergrok", false, default); release.SetResult();
        await Task.WhenAll(first, second); Assert.Equal(1, calls);
        var stored = Json.Read<SubscriptionAuthService.Credential>((await settings.Secret("oauth:supergrok"))!); Assert.Equal("new-refresh", stored.RefreshToken);
        var forced = auth.AccessToken("supergrok", true, default); var disconnect = auth.Disconnect("supergrok", default); await forced; await disconnect;
        Assert.Null(await settings.Secret("oauth:supergrok"));
    }
    [Fact]
    public async Task SuperGrokUsesDeviceFlowAndHonorsInitialPollingInterval()
    {
        using var host = new TestHost(); var calls = 0;
        using var client = new HttpClient(new RecordingHandler(req => { calls++; Assert.Equal("https://auth.x.ai/oauth2/device/code", req.RequestUri!.ToString()); return Task.FromResult(Body("{\"device_code\":\"private-device\",\"user_code\":\"ABCD\",\"verification_uri\":\"https://accounts.x.ai/oauth2/device\",\"expires_in\":600,\"interval\":30}")); }));
        var auth = new SubscriptionAuthService(host.Get<SettingsService>(), new ClientFactory(client), Config());
        var begin = await auth.Begin("supergrok", "session", default);
        Assert.Equal("ABCD", begin.UserCode); Assert.DoesNotContain("private-device", Json.Write(begin));
        Assert.False((await auth.Poll("supergrok", begin.FlowId, "session", default)).Connected); Assert.Equal(1, calls);
        await Assert.ThrowsAsync<ProviderException>(() => auth.Poll("supergrok", begin.FlowId, "wrong", default));
    }
    [Fact]
    public async Task DeviceDenialIsTerminalAndDoesNotReplaceExistingCredential()
    {
        using var host = new TestHost(); var settings = host.Get<SettingsService>(); await Seed(settings, "supergrok", DateTimeOffset.UtcNow.AddHours(1));
        using var client = new HttpClient(new RecordingHandler(req => Task.FromResult(req.RequestUri!.AbsolutePath.EndsWith("/device/code")
            ? Body("{\"device_code\":\"private-device\",\"user_code\":\"ABCD\",\"verification_uri\":\"https://accounts.x.ai/oauth2/device\",\"expires_in\":600,\"interval\":1}")
            : Body("{\"error\":\"access_denied\",\"error_description\":\"private-diagnostic\"}", HttpStatusCode.BadRequest))));
        var auth = new SubscriptionAuthService(settings, new ClientFactory(client), Config()); var flow = await auth.Begin("supergrok", "session", default);
        await Task.Delay(1100);
        var error = await Assert.ThrowsAsync<ProviderException>(() => auth.Poll("supergrok", flow.FlowId, "session", default));
        Assert.DoesNotContain("private-diagnostic", error.Message); Assert.True((await auth.Status("supergrok", default)).Connected);
        await Assert.ThrowsAsync<ProviderException>(() => auth.Poll("supergrok", flow.FlowId, "session", default));
    }
    [Theory]
    [InlineData("data: {\"type\":\"response.output_text.delta\",\"delta\":\"{\\\"topic\\\":\\\"Ocean\\\"}\"}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}\n\n", true)]
    [InlineData("data: {\"type\":\"response.output_text.delta\",\"delta\":\"{\\\"topic\\\":\\\"Ocean\\\"}\"}\n\n", false)]
    [InlineData("data: {\"type\":\"response.failed\",\"response\":{\"error\":{\"message\":\"private-server-error\"}}}\n\n", false)]
    public async Task ResponsesRequireTerminalSuccess(string stream, bool succeeds)
    {
        string? request = null;
        using var client = new HttpClient(new RecordingHandler(async req => { request = await req.Content!.ReadAsStringAsync(); return Body(stream); }));
        var provider = new SubscriptionResponsesProvider(new FakeAuth(), new ClientFactory(client)); var context = Context("chatgpt-subscription");
        if (succeeds) { var result = await provider.Execute(context, default); Assert.Equal("Ocean", result.Project.Topic); }
        else { var e = await Assert.ThrowsAsync<ProviderException>(() => provider.Execute(context, default)); Assert.True(e.MayHaveCharged); Assert.DoesNotContain("private-server", e.Message); Assert.Equal("", context.Project.Topic); }
        Assert.Contains("\"stream\": true", request); Assert.Contains("\"store\": false", request); Assert.DoesNotContain("max_output_tokens", request);
    }
    [Theory]
    [InlineData(401, 2, 1)] [InlineData(403, 1, 0)]
    public async Task ResponsesRetryOnly401BeforeOutput(int code, int expectedCalls, int refreshes)
    {
        var calls = 0; var auth = new FakeAuth();
        using var client = new HttpClient(new RecordingHandler(_ => { calls++; return Task.FromResult(Body("private-error", (HttpStatusCode)code)); }));
        var provider = new SubscriptionResponsesProvider(auth, new ClientFactory(client));
        var error = await Assert.ThrowsAsync<ProviderException>(() => provider.Execute(Context("supergrok"), default));
        Assert.Equal(expectedCalls, calls); Assert.Equal(refreshes, auth.Refreshes); Assert.DoesNotContain("private-error", error.Message);
    }
    [Fact]
    public async Task ResponsesBoundUnterminatedSseLines()
    {
        using var client = new HttpClient(new RecordingHandler(_ => Task.FromResult(Body("data: " + new string('x', 4 * 1024 * 1024)))));
        var provider = new SubscriptionResponsesProvider(new FakeAuth(), new ClientFactory(client));
        var error = await Assert.ThrowsAsync<ProviderException>(() => provider.Execute(Context("supergrok"), default)); Assert.True(error.MayHaveCharged);
    }
    [Fact]
    public async Task ModelDiscoveryCancelsStalledResponseBody()
    {
        using var client = new HttpClient(new RecordingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) })));
        var provider = new SubscriptionResponsesProvider(new FakeAuth(), new ClientFactory(client));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetModels(new() { Id = "supergrok" }, null, cancel.Token));
    }
    [Fact]
    public async Task ClaudeBridgeRunsOnlyValidatedSubscriptionWithDisabledTools()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), "studio-fake-claude-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(path, "#!/bin/sh\ncase \"$1\" in\n--version) echo '2.1.300 (Claude Code)';;\n--help) echo '--safe-mode --tools --disallowedTools --no-session-persistence --output-format --input-format --max-turns';;\nauth) echo '{\"authMethod\":\"claude.ai\"}';;\n*) [ -z \"$ANTHROPIC_API_KEY\" ] || exit 2\ncat >/dev/null\nprintf '%s' '{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"{\\\"topic\\\":\\\"Ocean\\\"}\"}';;\nesac\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var oldKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "test-only-key-must-not-reach-cli");
        try { var context = Context("claude-code"); context.Config.Executable = path; var result = await new ClaudeCodeProvider(new ClaudeProcessRunner(Config())).Execute(context, default); Assert.Equal("Ocean", result.Project.Topic); }
        finally { Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", oldKey); File.Delete(path); }
    }
    private static Task Seed(SettingsService s, string id, DateTimeOffset expiry) => s.Save("oauth:" + id, new SubscriptionAuthService.Metadata("account", expiry), Json.Write(new SubscriptionAuthService.Credential("access", "refresh", expiry, "issued", "account")));
    private static ProviderContext Context(string id)
    {
        var project = new Project(); ProviderCatalog.ApplyPreset(project, "free");
        return new() { JobId = Guid.NewGuid(), Project = project, Stage = Stage.Topic, WorkDirectory = Path.GetTempPath(), Config = new() { Id = id, Model = "model" }, SaveCheckpoint = _ => Task.CompletedTask };
    }
    private sealed class FakeAuth : ISubscriptionAuth
    {
        public int Refreshes; public string ChatGptRedirectUri => "http://127.0.0.1:5080/auth/callback";
        public Task<string> AccessToken(string id, bool refresh, CancellationToken ct) { if (refresh) Refreshes++; return Task.FromResult("test-token"); }
        public Task<ProviderConnectionStatus> Status(string id, CancellationToken ct) => Task.FromResult(new ProviderConnectionStatus(true, "Connected"));
        public Task<OAuthStartResult> Begin(string id, string binding, CancellationToken ct) => throw new NotImplementedException();
        public Task<ProviderConnectionStatus> Poll(string id, string flow, string binding, CancellationToken ct) => throw new NotImplementedException();
        public Task CompleteChatGpt(string state, string code, string issued, string binding, CancellationToken ct) => throw new NotImplementedException();
        public Task Disconnect(string id, CancellationToken ct) => Task.CompletedTask;
        public Task CancelPending(string id, string flow, string binding, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { await Task.Delay(Timeout.Infinite, ct); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
