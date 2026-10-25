using System.Text;
using System.Text.Json.Nodes;
using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Models;
using FacelessStudio.Core.Settings;
using FacelessStudio.Providers.Auth;
using FacelessStudio.Providers.Llm;
using Microsoft.Extensions.DependencyInjection;

namespace FacelessStudio.Tests;

/// <summary>Account giả: đếm số lần refresh, trả token mới có refresh token xoay vòng.</summary>
internal sealed class FakeAccount : IOAuthAccount
{
    public int Refreshes;
    public int PendingPolls = 1;
    public string Id => "fake";
    public string Name => "Fake";
    public string Help => "";

    public Task<DeviceLogin> StartAsync(CancellationToken ct) => Task.FromResult(new DeviceLogin(
        "https://example.com/device", "ABCD-1234", TimeSpan.FromMilliseconds(10), DateTimeOffset.UtcNow.AddMinutes(1), new JsonObject()));

    public Task<OAuthTokens?> PollAsync(DeviceLogin login, CancellationToken ct) =>
        Task.FromResult(PendingPolls-- > 0 ? null : new OAuthTokens("at-1", "rt-1", DateTimeOffset.UtcNow.AddHours(1), "me@example.com"));

    public Task<OAuthTokens> RefreshAsync(OAuthTokens tokens, CancellationToken ct)
    {
        Interlocked.Increment(ref Refreshes);
        return Task.FromResult(new OAuthTokens("at-" + (Refreshes + 1), "rt-" + (Refreshes + 1), DateTimeOffset.UtcNow.AddHours(1)));
    }
}

internal sealed class EchoLlm(string json) : ILlmClient
{
    public LlmRequest? Last;
    public string Service => "Echo";
    public IReadOnlyList<string> RequiredSettings => ["ECHO_KEY"];

    public Task<string> GenerateJsonAsync(LlmRequest request, StepContext context)
    {
        Last = request;
        return Task.FromResult(json);
    }
}

public class OAuthInfraTests
{
    private static AppSettings Settings(out string dir)
    {
        dir = Directory.CreateTempSubdirectory("faceless-oauth").FullName;
        return new AppSettings(new Dictionary<string, string>(), dir);
    }

    [Fact]
    public async Task Session_returns_stored_token_without_refresh_when_fresh()
    {
        var store = new OAuthTokenStore();
        var settings = Settings(out var dir);
        store.Save(dir, "fake", new OAuthTokens("at", "rt", DateTimeOffset.UtcNow.AddHours(2)));
        var account = new FakeAccount();

        var tokens = await new OAuthSession(store).GetTokensAsync(account, settings, CancellationToken.None);
        Assert.Equal("at", tokens.AccessToken);
        Assert.Equal(0, account.Refreshes);
    }

    [Fact]
    public async Task Session_refreshes_expiring_token_once_and_persists_rotated_refresh_token()
    {
        var store = new OAuthTokenStore();
        var settings = Settings(out var dir);
        store.Save(dir, "fake", new OAuthTokens("old", "rt-old", DateTimeOffset.UtcNow.AddMinutes(1), "me",
            new() { ["account_id"] = "acc" }));
        var account = new FakeAccount();
        var session = new OAuthSession(store);

        var results = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => session.GetTokensAsync(account, settings, CancellationToken.None)));

        Assert.Equal(1, account.Refreshes);
        Assert.All(results.Skip(1), t => Assert.Equal("at-2", t.AccessToken));
        var saved = store.Load(dir, "fake")!;
        Assert.Equal("rt-2", saved.RefreshToken);
        Assert.Equal("me", saved.AccountLabel);
        Assert.Equal("acc", saved.Get("account_id"));
    }

    [Fact]
    public async Task Session_without_login_throws_friendly_error()
    {
        var settings = Settings(out _);
        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            new OAuthSession(new OAuthTokenStore()).GetTokensAsync(new FakeAccount(), settings, CancellationToken.None));
        Assert.Contains("Chưa đăng nhập Fake", ex.Message);
    }

    [Fact]
    public async Task Login_service_polls_until_tokens_then_saves()
    {
        var store = new OAuthTokenStore();
        Settings(out var dir);
        var account = new FakeAccount { PendingPolls = 2 };
        var logins = new OAuthLoginService([account], store);

        await logins.Start("fake", dir);

        Assert.Equal(LoginStatus.Success, logins.State("fake").Status);
        Assert.Contains("me@example.com", logins.State("fake").Message);
        Assert.Equal("at-1", store.Load(dir, "fake")!.AccessToken);

        logins.Logout("fake", dir);
        Assert.Null(store.Load(dir, "fake"));
    }

    [Fact]
    public void Jwt_helper_reads_exp_and_email()
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var jwt = $"{B64("{\"alg\":\"none\"}")}.{B64("{\"exp\":1900000000,\"email\":\"a@b.c\"}")}.sig";
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1900000000), JwtHelper.Expiry(jwt));
        Assert.Equal("a@b.c", JwtHelper.Email(jwt));
        Assert.Null(JwtHelper.Expiry("not-a-jwt"));
    }

    [Fact]
    public void Responses_body_uses_json_schema_format()
    {
        var body = ResponsesApi.Body("m", new LlmRequest("sys", "user", LlmPrompts.ScriptSchema));
        Assert.Equal("sys", body["instructions"]!.GetValue<string>());
        Assert.Equal("input_text", body["input"]![0]!["content"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("json_schema", body["text"]!["format"]!["type"]!.GetValue<string>());
        Assert.False(body["store"]!.GetValue<bool>());

        var withTools = ResponsesApi.Body("m", new LlmRequest("sys", "user", LlmPrompts.TopicSchema), jsonSchema: false);
        Assert.Null(withTools["text"]);
        Assert.Contains("JSON schema", withTools["input"]![0]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void Responses_extract_text_and_errors()
    {
        var ok = JsonNode.Parse("""
            {"status":"completed","output":[{"type":"reasoning","summary":[]},
              {"type":"message","content":[{"type":"output_text","text":"{\"a\":"},{"type":"output_text","text":"1}"}]}]}
            """)!;
        Assert.Equal("{\"a\":1}", ResponsesApi.ExtractText(ok, "X"));

        var refusal = JsonNode.Parse("""{"output":[{"type":"message","content":[{"type":"refusal","refusal":"no"}]}]}""")!;
        Assert.Contains("từ chối", Assert.Throws<ProviderException>(() => ResponsesApi.ExtractText(refusal, "X")).Message);

        var cut = JsonNode.Parse("""{"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"output":[]}""")!;
        Assert.Contains("quá dài", Assert.Throws<ProviderException>(() => ResponsesApi.ExtractText(cut, "X")).Message);
    }

    [Fact]
    public async Task Responses_sse_reads_completed_event_and_failures()
    {
        var sse = """
            event: response.created
            data: {"type":"response.created","response":{"status":"in_progress"}}

            event: response.output_text.delta
            data: {"type":"response.output_text.delta","delta":"{\"a\""}

            event: response.completed
            data: {"type":"response.completed","response":{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"{\"a\":1}"}]}]}}


            """;
        Assert.Equal("{\"a\":1}", await ResponsesApi.ReadSseTextAsync(new MemoryStream(Encoding.UTF8.GetBytes(sse)), "X", default));

        var failed = """
            data: {"type":"response.failed","response":{"status":"failed","error":{"code":"usage_limit","message":"quota hết"}}}


            """;
        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            ResponsesApi.ReadSseTextAsync(new MemoryStream(Encoding.UTF8.GetBytes(failed)), "X", default));
        Assert.Contains("quota hết", ex.Message);
    }

    [Fact]
    public async Task Vendor_providers_get_ids_from_key_and_use_client()
    {
        var llm = new EchoLlm("""{"title":"T","description":"D","tags":[],"thumbnail_text":"X","scenes":[{"narration":"N","footage_query":"q","image_prompt":"p"}]}""");
        var info = new LlmVendorInfo("echo", "Echo AI", ProviderCategory.PaidCloud, "desc", "rẻ", ScriptEffort: "high");
        var services = new ServiceCollection().AddSingleton(new GoogleTrendsClient(null!)).AddLlmVendor(info, _ => llm);
        using var sp = services.BuildServiceProvider();
        var providers = sp.GetServices<IStepProvider>().ToList();

        var script = Assert.IsAssignableFrom<IScriptProvider>(providers.Single(p => p.Id == "script-echo"));
        var topic = providers.Single(p => p.Id == "topic-echo");
        Assert.Equal("Google Trends + Echo AI", topic.Name);
        Assert.Equal("rẻ", script.CostNote);
        Assert.False(script.IsConfigured(AppSettings.Empty()));
        Assert.True(script.IsConfigured(new AppSettings(new Dictionary<string, string> { ["ECHO_KEY"] = "k" }, ".")));

        var result = await script.WriteAsync(FakeHttp.Context(new()));
        Assert.Equal("T", result.Title);
        Assert.Equal("high", llm.Last!.Effort);
    }
}
