using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Settings;
using FacelessStudio.Providers.Auth;
using FacelessStudio.Providers.Llm.Codex;
using Microsoft.Extensions.DependencyInjection;

namespace FacelessStudio.Tests;

internal static class CodexFixtures
{
    public const string ScriptJson = """
        {"title":"Bí ẩn đại dương","description":"Mô tả","tags":["biển"],"thumbnail_text":"BIỂN SÂU",
         "scenes":[{"narration":"Câu một.","footage_query":"ocean waves","image_prompt":"deep blue ocean"},
                   {"narration":"Câu hai.","footage_query":"coral reef","image_prompt":"coral reef"}]}
        """;

    private static string B64(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>JWT giả (chữ ký "x") với claim email, exp và thông tin tài khoản ChatGPT.</summary>
    public static string Jwt(DateTimeOffset exp, string accountId = "acc-123", string email = "me@example.com",
        string? residency = null)
    {
        var auth = new JsonObject { ["chatgpt_account_id"] = accountId };
        if (residency != null) auth["chatgpt_data_residency"] = residency;
        var payload = new JsonObject
        {
            ["exp"] = exp.ToUnixTimeSeconds(),
            ["email"] = email,
            ["https://api.openai.com/auth"] = auth,
        };
        return $"{B64("""{"alg":"none"}""")}.{B64(payload.ToJsonString())}.x";
    }

    public static string Sse(string scriptJson) =>
        "event: response.created\ndata: {\"type\":\"response.created\",\"response\":{}}\n\n" +
        "event: response.completed\ndata: " + new JsonObject
        {
            ["type"] = "response.completed",
            ["response"] = new JsonObject
            {
                ["status"] = "completed",
                ["output"] = new JsonArray(new JsonObject
                {
                    ["type"] = "message",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = scriptJson }),
                }),
            },
        }.ToJsonString() + "\n\n";

    public static Dictionary<string, string> Form(string body) =>
        body.Split('&').Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
}

public class CodexTests
{
    private const string ResponsesUrl = "chatgpt.com/backend-api/codex/responses";

    [Fact]
    public async Task Start_parses_usercode_with_string_interval()
    {
        var http = new FakeHttp().On("deviceauth/usercode", """{"device_auth_id":"dev-1","user_code":"ABCD-EFGH","interval":"5"}""");
        using var sp = http.BuildServices();
        var login = await sp.GetRequiredService<CodexAccount>().StartAsync(CancellationToken.None);

        Assert.Equal("ABCD-EFGH", login.UserCode);
        Assert.Equal("https://auth.openai.com/codex/device", login.VerificationUrl);
        Assert.Equal(TimeSpan.FromSeconds(5), login.Interval);
        Assert.Equal("dev-1", login.State["device_auth_id"]!.GetValue<string>());
        Assert.True(login.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(10));
        var req = Assert.Single(http.Requests);
        Assert.Equal("app_EMoamEEZ73f0CkXaXp7hrann", JsonNode.Parse(req.Body)!["client_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Start_accepts_numeric_interval_with_minimum_of_three_seconds()
    {
        var http = new FakeHttp().On("deviceauth/usercode", """{"device_auth_id":"d","user_code":"U","interval":1}""");
        using var sp = http.BuildServices();
        var login = await sp.GetRequiredService<CodexAccount>().StartAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(3), login.Interval);
    }

    [Fact]
    public async Task Start_404_explains_how_to_enable_device_code_login()
    {
        var http = new FakeHttp().On("deviceauth/usercode", "{}", HttpStatusCode.NotFound);
        using var sp = http.BuildServices();
        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            sp.GetRequiredService<CodexAccount>().StartAsync(CancellationToken.None));
        Assert.Contains("Device code login", ex.Message);
    }

    [Fact]
    public async Task Poll_returns_null_while_pending()
    {
        var http = new FakeHttp().On("deviceauth/token", "{}", HttpStatusCode.Forbidden);
        using var sp = http.BuildServices();
        var account = sp.GetRequiredService<CodexAccount>();
        var login = new DeviceLogin("u", "ABCD", TimeSpan.FromSeconds(5), DateTimeOffset.UtcNow.AddMinutes(1),
            new JsonObject { ["device_auth_id"] = "dev-1", ["user_code"] = "ABCD" });

        Assert.Null(await account.PollAsync(login, CancellationToken.None));
        var body = JsonNode.Parse(Assert.Single(http.Requests).Body)!;
        Assert.Equal("dev-1", body["device_auth_id"]!.GetValue<string>());
        Assert.Equal("ABCD", body["user_code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Poll_exchanges_authorization_code_with_form_encoded_request()
    {
        var access = CodexFixtures.Jwt(DateTimeOffset.UtcNow.AddHours(1), "acc-xyz", residency: "us");
        var idToken = CodexFixtures.Jwt(DateTimeOffset.UtcNow.AddHours(1), "acc-xyz", "user@example.com");
        var http = new FakeHttp()
            .On("deviceauth/token", """{"authorization_code":"code-1","code_verifier":"verif-1"}""")
            .On("oauth/token", new JsonObject { ["access_token"] = access, ["refresh_token"] = "rt-1", ["id_token"] = idToken }.ToJsonString());
        using var sp = http.BuildServices();
        var login = new DeviceLogin("u", "ABCD", TimeSpan.FromSeconds(5), DateTimeOffset.UtcNow.AddMinutes(1),
            new JsonObject { ["device_auth_id"] = "dev-1", ["user_code"] = "ABCD" });

        var tokens = await sp.GetRequiredService<CodexAccount>().PollAsync(login, CancellationToken.None);

        Assert.NotNull(tokens);
        Assert.Equal(access, tokens.AccessToken);
        Assert.Equal("rt-1", tokens.RefreshToken);
        Assert.Equal("user@example.com", tokens.AccountLabel);
        Assert.Equal("acc-xyz", tokens.Get("account_id"));
        Assert.Equal("us", tokens.Get("residency"));
        Assert.Equal(idToken, tokens.Get("id_token"));
        Assert.True(tokens.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(50));

        var exchange = http.Requests.Last();
        Assert.Contains("auth.openai.com/oauth/token", exchange.Url);
        Assert.StartsWith("application/x-www-form-urlencoded", exchange.Headers["content-type"]);
        var form = CodexFixtures.Form(exchange.Body);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("code-1", form["code"]);
        Assert.Equal("verif-1", form["code_verifier"]);
        Assert.Equal("https://auth.openai.com/deviceauth/callback", form["redirect_uri"]);
        Assert.Equal("app_EMoamEEZ73f0CkXaXp7hrann", form["client_id"]);
    }

    [Fact]
    public async Task Refresh_sends_refresh_token_form_and_keeps_old_refresh_token_when_none_returned()
    {
        var access = CodexFixtures.Jwt(DateTimeOffset.UtcNow.AddHours(1), "acc-new");
        var http = new FakeHttp().On("oauth/token", new JsonObject { ["access_token"] = access }.ToJsonString());
        using var sp = http.BuildServices();
        var old = new OAuthTokens("old", "rt-old", DateTimeOffset.UtcNow.AddMinutes(-1), "me@example.com");

        var tokens = await sp.GetRequiredService<CodexAccount>().RefreshAsync(old, CancellationToken.None);

        Assert.Equal(access, tokens.AccessToken);
        Assert.Equal("rt-old", tokens.RefreshToken);
        Assert.Equal("acc-new", tokens.Get("account_id"));
        var form = CodexFixtures.Form(Assert.Single(http.Requests).Body);
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal("rt-old", form["refresh_token"]);
        Assert.Equal("app_EMoamEEZ73f0CkXaXp7hrann", form["client_id"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""")]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":{"code":"refresh_token_reused"}}""")]
    public async Task Refresh_rejected_grant_gives_friendly_relogin_message(HttpStatusCode status, string body)
    {
        var http = new FakeHttp().On("oauth/token", body, status);
        using var sp = http.BuildServices();
        var ex = await Assert.ThrowsAsync<ProviderException>(() => sp.GetRequiredService<CodexAccount>()
            .RefreshAsync(new OAuthTokens("a", "rt"), CancellationToken.None));
        Assert.Equal("Phiên đăng nhập ChatGPT đã hết hạn, vào Cài đặt để đăng nhập lại.", ex.Message);
    }

    [Fact]
    public async Task Client_sends_codex_request_with_identity_headers_and_parses_script()
    {
        var http = new FakeHttp().On(ResponsesUrl, CodexFixtures.Sse(CodexFixtures.ScriptJson), contentType: "text/event-stream");
        using var sp = http.BuildServices();
        var ctx = FakeHttp.Context(new());
        sp.GetRequiredService<OAuthTokenStore>().Save(ctx.Settings.BaseDir, "codex", new OAuthTokens(
            "at-1", "rt-1", DateTimeOffset.UtcNow.AddHours(1), "me@example.com",
            new() { ["account_id"] = "acc-123", ["residency"] = "eu" }));

        var script = await FakeHttp.Provider<IScriptProvider>(sp, "script-codex").WriteAsync(ctx);

        Assert.Equal(2, script.Scenes.Count);
        Assert.Equal("ocean waves", script.Scenes[0].FootageQuery);
        var req = Assert.Single(http.Requests);
        Assert.Equal("https://chatgpt.com/backend-api/codex/responses", req.Url);
        Assert.Equal("Bearer at-1", req.Headers["authorization"]);
        Assert.Equal("acc-123", req.Headers["chatgpt-account-id"]);
        Assert.Equal("eu", req.Headers["x-openai-internal-codex-residency"]);
        Assert.Equal("faceless-studio", req.Headers["originator"]);
        Assert.StartsWith("FacelessStudio/", req.Headers["user-agent"]);
        Assert.Equal("text/event-stream", req.Headers["accept"]);

        var body = JsonNode.Parse(req.Body)!.AsObject();
        Assert.Equal("gpt-5.5", body["model"]!.GetValue<string>());
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.False(body["store"]!.GetValue<bool>());
        Assert.False(string.IsNullOrWhiteSpace(body["instructions"]!.GetValue<string>()));
        Assert.Equal("medium", body["reasoning"]!["effort"]!.GetValue<string>());
        Assert.Empty(body["include"]!.AsArray());
        Assert.Equal("json_schema", body["text"]!["format"]!["type"]!.GetValue<string>());
        foreach (var banned in new[] { "max_output_tokens", "temperature", "top_p" })
            Assert.False(body.ContainsKey(banned), banned);
    }

    [Fact]
    public async Task Client_refreshes_expired_token_before_calling_backend()
    {
        var fresh = CodexFixtures.Jwt(DateTimeOffset.UtcNow.AddHours(1), "acc-fresh");
        var http = new FakeHttp()
            .On(ResponsesUrl, CodexFixtures.Sse(CodexFixtures.ScriptJson), contentType: "text/event-stream")
            .On("auth.openai.com/oauth/token", new JsonObject { ["access_token"] = fresh, ["refresh_token"] = "rt-2" }.ToJsonString());
        using var sp = http.BuildServices();
        var ctx = FakeHttp.Context(new());
        var store = sp.GetRequiredService<OAuthTokenStore>();
        store.Save(ctx.Settings.BaseDir, "codex", new OAuthTokens("stale", "rt-1", DateTimeOffset.UtcNow.AddMinutes(-5)));

        await FakeHttp.Provider<IScriptProvider>(sp, "script-codex").WriteAsync(ctx);

        Assert.Equal(2, http.Requests.Count);
        Assert.Contains("oauth/token", http.Requests[0].Url);
        Assert.Equal("Bearer " + fresh, http.Requests[1].Headers["authorization"]);
        Assert.Equal("acc-fresh", http.Requests[1].Headers["chatgpt-account-id"]);
        var saved = store.Load(ctx.Settings.BaseDir, "codex")!;
        Assert.Equal("rt-2", saved.RefreshToken);
        Assert.Equal(fresh, saved.AccessToken);
    }

    [Fact]
    public async Task Client_unauthorized_forces_one_refresh_then_asks_to_login_again()
    {
        var fresh = CodexFixtures.Jwt(DateTimeOffset.UtcNow.AddHours(1));
        var http = new FakeHttp()
            .On(ResponsesUrl, "{}", HttpStatusCode.Unauthorized)
            .On("auth.openai.com/oauth/token", new JsonObject { ["access_token"] = fresh }.ToJsonString());
        using var sp = http.BuildServices();
        var ctx = FakeHttp.Context(new());
        sp.GetRequiredService<OAuthTokenStore>().Save(ctx.Settings.BaseDir, "codex",
            new OAuthTokens("at", "rt", DateTimeOffset.UtcNow.AddHours(1)));

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            FakeHttp.Provider<IScriptProvider>(sp, "script-codex").WriteAsync(ctx));

        Assert.Contains("đăng nhập lại", ex.Message);
        Assert.Equal(2, http.Requests.Count(r => r.Url.Contains("backend-api")));
        Assert.Equal(1, http.Requests.Count(r => r.Url.Contains("oauth/token")));
    }

    [Fact]
    public async Task Client_rate_limit_gives_usage_limit_message()
    {
        var http = new FakeHttp().On(ResponsesUrl, """{"error":{"type":"usage_limit_reached"}}""", HttpStatusCode.TooManyRequests);
        using var sp = http.BuildServices();
        var ctx = FakeHttp.Context(new());
        sp.GetRequiredService<OAuthTokenStore>().Save(ctx.Settings.BaseDir, "codex",
            new OAuthTokens("at", "rt", DateTimeOffset.UtcNow.AddHours(1)));

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            FakeHttp.Provider<IScriptProvider>(sp, "script-codex").WriteAsync(ctx));
        Assert.Contains("usage limit", ex.Message);
    }

    [Fact]
    public async Task Client_response_failed_event_throws_provider_exception()
    {
        const string sse = "event: response.failed\ndata: {\"type\":\"response.failed\",\"response\":{\"error\":{\"message\":\"model overloaded\"}}}\n\n";
        var http = new FakeHttp().On(ResponsesUrl, sse, contentType: "text/event-stream");
        using var sp = http.BuildServices();
        var ctx = FakeHttp.Context(new());
        sp.GetRequiredService<OAuthTokenStore>().Save(ctx.Settings.BaseDir, "codex",
            new OAuthTokens("at", "rt", DateTimeOffset.UtcNow.AddHours(1)));

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            FakeHttp.Provider<IScriptProvider>(sp, "script-codex").WriteAsync(ctx));
        Assert.Contains("model overloaded", ex.Message);
    }

    [Fact]
    public void Providers_are_configured_only_when_logged_in()
    {
        using var sp = new FakeHttp().BuildServices();
        var ctx = FakeHttp.Context(new());
        var script = FakeHttp.Provider<IScriptProvider>(sp, "script-codex");
        var topic = FakeHttp.Provider<ITopicProvider>(sp, "topic-codex");
        Assert.False(script.IsConfigured(ctx.Settings));
        Assert.False(topic.IsConfigured(ctx.Settings));

        sp.GetRequiredService<OAuthTokenStore>().Save(ctx.Settings.BaseDir, "codex", new OAuthTokens("at", "rt"));
        Assert.True(script.IsConfigured(ctx.Settings));
        Assert.True(topic.IsConfigured(ctx.Settings));
        Assert.Contains(sp.GetServices<IOAuthAccount>(), a => a.Id == "codex");
    }
}

/// <summary>Đụng tới biến môi trường CODEX_HOME nên chạy tuần tự với nhau.</summary>
[Collection("CodexHome")]
public class CodexImportTests
{
    private static async Task<OAuthTokens?> ImportFrom(string? authJson)
    {
        var dir = Directory.CreateTempSubdirectory("codex-home").FullName;
        if (authJson != null) File.WriteAllText(Path.Combine(dir, "auth.json"), authJson);
        var previous = Environment.GetEnvironmentVariable("CODEX_HOME");
        Environment.SetEnvironmentVariable("CODEX_HOME", dir);
        try
        {
            using var sp = new FakeHttp().BuildServices();
            return await sp.GetRequiredService<CodexAccount>().ImportAsync(CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previous);
        }
    }

    [Fact]
    public async Task Import_reads_codex_cli_auth_json()
    {
        var access = CodexFixtures.Jwt(DateTimeOffset.UtcNow.AddHours(1), "acc-cli");
        var idToken = CodexFixtures.Jwt(DateTimeOffset.UtcNow.AddHours(1), "acc-cli", "cli@example.com");
        var json = new JsonObject
        {
            ["tokens"] = new JsonObject
            {
                ["access_token"] = access,
                ["refresh_token"] = "rt-cli",
                ["id_token"] = idToken,
                ["account_id"] = "acc-file",
            },
        }.ToJsonString();

        var tokens = await ImportFrom(json);

        Assert.NotNull(tokens);
        Assert.Equal(access, tokens.AccessToken);
        Assert.Equal("rt-cli", tokens.RefreshToken);
        Assert.Equal("cli@example.com", tokens.AccountLabel);
        Assert.Equal("acc-cli", tokens.Get("account_id"));
    }

    [Fact]
    public async Task Import_uses_account_id_from_file_when_jwt_has_none()
    {
        var tokens = await ImportFrom("""{"tokens":{"access_token":"opaque","refresh_token":"r","account_id":"acc-file"}}""");
        Assert.NotNull(tokens);
        Assert.Equal("acc-file", tokens.Get("account_id"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("""{"tokens":{"refresh_token":"r"}}""")]
    [InlineData("""{"OPENAI_API_KEY":"sk-x"}""")]
    public async Task Import_returns_null_when_missing_or_unusable(string? authJson) =>
        Assert.Null(await ImportFrom(authJson));
}
