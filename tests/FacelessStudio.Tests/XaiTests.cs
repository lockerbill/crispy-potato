using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Settings;
using FacelessStudio.Providers.Auth;
using FacelessStudio.Providers.Llm.Xai;
using Microsoft.Extensions.DependencyInjection;

namespace FacelessStudio.Tests;

public class XaiTests
{
    private const string ScriptJson = """
        {"title":"Bí ẩn đại dương","description":"Mô tả","tags":["biển"],"thumbnail_text":"BIỂN SÂU",
         "scenes":[{"narration":"Câu một.","footage_query":"ocean waves","image_prompt":"deep blue ocean"}]}
        """;

    private const string TopicJson = """{"ideas":[{"title":"Hố đen","angle":"Góc nhìn mới"}]}""";

    private static string Response(string json) =>
        new JsonObject
        {
            ["status"] = "completed",
            ["output"] = new JsonArray(
                new JsonObject { ["type"] = "web_search_call" },
                new JsonObject
                {
                    ["type"] = "message",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = json }),
                }),
        }.ToJsonString();

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Jwt(string payload) => $"{B64("{\"alg\":\"none\"}")}.{B64(payload)}.sig";

    private static FakeHttp DiscoveryRoutes(FakeHttp http) => http
        .On("auth.x.ai/.well-known/openid-configuration",
            """{"issuer":"https://auth.x.ai","token_endpoint":"https://auth.x.ai/oauth2/token"}""");

    [Fact]
    public async Task Api_key_mode_posts_responses_with_json_schema()
    {
        var http = new FakeHttp().On("api.x.ai/v1/responses", Response(ScriptJson));
        using var sp = http.BuildServices();
        var script = await FakeHttp.Provider<IScriptProvider>(sp, "script-xai")
            .WriteAsync(FakeHttp.Context(new() { [SettingKeys.XaiApiKey] = "xai-key" }));

        Assert.Equal("BIỂN SÂU", script.ThumbnailText);
        var req = Assert.Single(http.Requests);
        Assert.Equal("https://api.x.ai/v1/responses", req.Url);
        Assert.Equal("Bearer xai-key", req.Headers["authorization"]);
        var body = JsonNode.Parse(req.Body)!;
        Assert.Equal("grok-4.3", body["model"]!.GetValue<string>());
        Assert.Equal("json_schema", body["text"]!["format"]!["type"]!.GetValue<string>());
        Assert.Null(body["tools"]);
    }

    [Fact]
    public async Task Topic_with_web_search_sends_tools_and_no_text_format()
    {
        var http = new FakeHttp().On("api.x.ai/v1/responses", Response(TopicJson));
        using var sp = http.BuildServices();
        var ideas = await FakeHttp.Provider<ITopicProvider>(sp, "topic-xai")
            .SuggestAsync(new TopicRequest("không gian"), FakeHttp.Context(new() { [SettingKeys.XaiApiKey] = "k" }));

        Assert.Equal("Hố đen", Assert.Single(ideas).Title);
        var body = JsonNode.Parse(Assert.Single(http.Requests).Body)!;
        Assert.Null(body["text"]);
        var tools = body["tools"]!.AsArray().Select(t => t!["type"]!.GetValue<string>()).ToList();
        Assert.Equal(["web_search", "x_search"], tools);
    }

    [Fact]
    public async Task Api_key_mode_maps_http_errors_to_friendly_messages()
    {
        var http = new FakeHttp().On("api.x.ai/v1/responses", "{}", HttpStatusCode.Unauthorized);
        using var sp = http.BuildServices();
        var ex = await Assert.ThrowsAsync<ProviderException>(() => FakeHttp.Provider<IScriptProvider>(sp, "script-xai")
            .WriteAsync(FakeHttp.Context(new() { [SettingKeys.XaiApiKey] = "bad" })));
        Assert.Contains("API key", ex.Message);
    }

    [Fact]
    public async Task Oauth_mode_uses_stored_token()
    {
        var http = new FakeHttp().On("api.x.ai/v1/responses", Response(ScriptJson));
        using var sp = http.BuildServices();
        var ctx = FakeHttp.Context(new());
        sp.GetRequiredService<OAuthTokenStore>().Save(ctx.Settings.BaseDir, "xai",
            new OAuthTokens("stored-at", "rt", DateTimeOffset.UtcNow.AddHours(1)));

        await FakeHttp.Provider<IScriptProvider>(sp, "script-xai-oauth").WriteAsync(ctx);

        var req = Assert.Single(http.Requests);
        Assert.Equal("Bearer stored-at", req.Headers["authorization"]);
        Assert.Equal("grok-4.3", JsonNode.Parse(req.Body)!["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task Oauth_mode_forbidden_explains_subscription()
    {
        var http = new FakeHttp().On("api.x.ai/v1/responses", "{}", HttpStatusCode.Forbidden);
        using var sp = http.BuildServices();
        var ctx = FakeHttp.Context(new());
        sp.GetRequiredService<OAuthTokenStore>().Save(ctx.Settings.BaseDir, "xai",
            new OAuthTokens("at", "rt", DateTimeOffset.UtcNow.AddHours(1)));

        var ex = await Assert.ThrowsAsync<ProviderException>(
            () => FakeHttp.Provider<IScriptProvider>(sp, "script-xai-oauth").WriteAsync(ctx));
        Assert.Contains("SuperGrok", ex.Message);
    }

    [Fact]
    public async Task Oauth_mode_refreshes_expired_token_and_persists_rotated_refresh_token()
    {
        var http = DiscoveryRoutes(new FakeHttp())
            .On("auth.x.ai/oauth2/token",
                $$"""{"access_token":"new-at","refresh_token":"rt-new","expires_in":900,"id_token":"{{Jwt("""{"email":"me@x.com"}""")}}"}""")
            .On("api.x.ai/v1/responses", Response(ScriptJson));
        using var sp = http.BuildServices();
        var ctx = FakeHttp.Context(new());
        var store = sp.GetRequiredService<OAuthTokenStore>();
        store.Save(ctx.Settings.BaseDir, "xai", new OAuthTokens("old-at", "rt-old", DateTimeOffset.UtcNow.AddMinutes(-5)));

        await FakeHttp.Provider<IScriptProvider>(sp, "script-xai-oauth").WriteAsync(ctx);

        var refresh = http.Requests.Single(r => r.Url.EndsWith("/oauth2/token"));
        Assert.Contains("grant_type=refresh_token", refresh.Body);
        Assert.Contains("refresh_token=rt-old", refresh.Body);
        Assert.Contains("client_id=b1a00492-073a-47ea-816f-4c329264a828", refresh.Body);
        Assert.Equal("Bearer new-at", http.Requests.Last().Headers["authorization"]);

        var saved = store.Load(ctx.Settings.BaseDir, "xai")!;
        Assert.Equal("new-at", saved.AccessToken);
        Assert.Equal("rt-new", saved.RefreshToken);
        Assert.Equal("me@x.com", saved.AccountLabel);
        Assert.Equal("https://auth.x.ai/oauth2/token", saved.Get("token_endpoint"));
    }

    [Fact]
    public async Task Refresh_rejected_asks_user_to_login_again()
    {
        var http = DiscoveryRoutes(new FakeHttp()).On("auth.x.ai/oauth2/token", """{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
        using var sp = http.BuildServices();
        var ex = await Assert.ThrowsAsync<ProviderException>(() => sp.GetRequiredService<XaiAccount>()
            .RefreshAsync(new OAuthTokens("a", "r"), CancellationToken.None));
        Assert.Contains("đăng nhập lại", ex.Message);
    }

    [Fact]
    public async Task Device_flow_start_parses_fields()
    {
        var http = DiscoveryRoutes(new FakeHttp()).On("auth.x.ai/oauth2/device/code", """
            {"device_code":"dev-1","user_code":"WXYZ-1234","verification_uri":"https://accounts.x.ai/device",
             "verification_uri_complete":"https://accounts.x.ai/device?user_code=WXYZ-1234","expires_in":600,"interval":7}
            """);
        using var sp = http.BuildServices();

        var login = await sp.GetRequiredService<XaiAccount>().StartAsync(CancellationToken.None);

        Assert.Equal("WXYZ-1234", login.UserCode);
        Assert.Equal("https://accounts.x.ai/device?user_code=WXYZ-1234", login.VerificationUrl);
        Assert.Equal(TimeSpan.FromSeconds(7), login.Interval);
        Assert.True(login.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(9));
        Assert.Equal("dev-1", login.State["device_code"]!.GetValue<string>());
        var req = http.Requests.Single(r => r.Url.Contains("device/code"));
        Assert.Contains("client_id=b1a00492-073a-47ea-816f-4c329264a828", req.Body);
        Assert.Contains("offline_access", Uri.UnescapeDataString(req.Body.Replace('+', ' ')));
    }

    [Fact]
    public async Task Device_flow_poll_pending_then_tokens()
    {
        var http = DiscoveryRoutes(new FakeHttp())
            .On("auth.x.ai/oauth2/device/code", """{"device_code":"dev-1","user_code":"U","verification_uri":"https://accounts.x.ai/device","expires_in":600,"interval":5}""")
            .On("auth.x.ai/oauth2/token", """{"error":"authorization_pending"}""", HttpStatusCode.BadRequest);
        using var sp = http.BuildServices();
        var account = sp.GetRequiredService<XaiAccount>();
        var login = await account.StartAsync(CancellationToken.None);
        Assert.Equal("https://accounts.x.ai/device", login.VerificationUrl);

        Assert.Null(await account.PollAsync(login, CancellationToken.None));

        var access = Jwt($$"""{"exp":{{DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds()}},"email":"me@x.com"}""");
        http.On("auth.x.ai/oauth2/token", $$"""{"access_token":"{{access}}","refresh_token":"rt-1","expires_in":900}""");
        var tokens = await account.PollAsync(login, CancellationToken.None);

        Assert.NotNull(tokens);
        Assert.Equal("rt-1", tokens.RefreshToken);
        Assert.Equal("me@x.com", tokens.AccountLabel);
        Assert.True(tokens.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(10));
        var poll = http.Requests.Last();
        Assert.Contains("device_code=dev-1", poll.Body);
        Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", poll.Body);
    }

    [Theory]
    [InlineData("access_denied", "từ chối")]
    [InlineData("expired_token", "hết hạn")]
    public async Task Device_flow_poll_failures_are_friendly(string error, string expected)
    {
        var http = DiscoveryRoutes(new FakeHttp()).On("auth.x.ai/oauth2/token", $$"""{"error":"{{error}}"}""", HttpStatusCode.BadRequest);
        using var sp = http.BuildServices();
        var login = new DeviceLogin("u", "c", TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow.AddMinutes(1),
            new JsonObject { ["device_code"] = "d" });
        var ex = await Assert.ThrowsAsync<ProviderException>(
            () => sp.GetRequiredService<XaiAccount>().PollAsync(login, CancellationToken.None));
        Assert.Contains(expected, ex.Message);
    }

    [Theory]
    [InlineData("https://evil.example.com/token")]
    [InlineData("https://notx.ai/token")]
    [InlineData("http://auth.x.ai/oauth2/token")]
    public async Task Discovery_with_foreign_token_endpoint_is_rejected(string endpoint)
    {
        var http = new FakeHttp().On("auth.x.ai/.well-known/openid-configuration", $$"""{"token_endpoint":"{{endpoint}}"}""");
        using var sp = http.BuildServices();
        await Assert.ThrowsAsync<ProviderException>(() => sp.GetRequiredService<XaiAccount>().StartAsync(CancellationToken.None));
        Assert.DoesNotContain(http.Requests, r => r.Url.Contains("device/code"));
    }

    [Fact]
    public async Task Stored_foreign_token_endpoint_is_rejected_on_refresh()
    {
        var http = new FakeHttp();
        using var sp = http.BuildServices();
        var tokens = new OAuthTokens("a", "r", Extra: new() { ["token_endpoint"] = "https://evil.example.com/token" });
        await Assert.ThrowsAsync<ProviderException>(() => sp.GetRequiredService<XaiAccount>().RefreshAsync(tokens, CancellationToken.None));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public void Refresh_skew_is_half_of_remaining_lifetime_within_bounds()
    {
        using var sp = new FakeHttp().BuildServices();
        var account = sp.GetRequiredService<XaiAccount>();
        Assert.Equal(TimeSpan.FromHours(1), account.RefreshSkew(new OAuthTokens("a", ExpiresAt: DateTimeOffset.UtcNow.AddHours(24))));
        Assert.Equal(TimeSpan.FromMinutes(1), account.RefreshSkew(new OAuthTokens("a", ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(30))));
        var mid = account.RefreshSkew(new OAuthTokens("a", ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(14)));
        Assert.InRange(mid, TimeSpan.FromMinutes(6), TimeSpan.FromMinutes(7.1));
    }

    [Fact]
    public void Oauth_provider_is_configured_only_after_login()
    {
        using var sp = new FakeHttp().BuildServices();
        var ctx = FakeHttp.Context(new());
        var script = FakeHttp.Provider<IScriptProvider>(sp, "script-xai-oauth");
        Assert.False(script.IsConfigured(ctx.Settings));

        sp.GetRequiredService<OAuthTokenStore>().Save(ctx.Settings.BaseDir, "xai", new OAuthTokens("at", "rt"));
        Assert.True(script.IsConfigured(ctx.Settings));
        Assert.True(FakeHttp.Provider<ITopicProvider>(sp, "topic-xai-oauth").IsConfigured(ctx.Settings));

        var apiKey = FakeHttp.Provider<IScriptProvider>(sp, "script-xai");
        Assert.False(apiKey.IsConfigured(ctx.Settings));
    }

    [Fact]
    public void Account_is_registered_with_login_service()
    {
        using var sp = new FakeHttp().BuildServices();
        Assert.Contains(sp.GetServices<IOAuthAccount>(), a => a.Id == "xai");
        Assert.Contains(sp.GetRequiredService<OAuthLoginService>().Accounts, a => a.Id == "xai");
    }
}
