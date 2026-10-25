using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Studio.Providers;

namespace Studio.Tests;

public sealed class ProviderEndpointTests
{
    private sealed class EndpointAuth : ISubscriptionAuth
    {
        public string ChatGptRedirectUri => "http://127.0.0.1:5080/auth/callback";
        public int Begins { get; private set; }
        public int Completions { get; private set; }
        private string? expectedBinding;
        public Task<ProviderConnectionStatus> Status(string id, CancellationToken ct) => Task.FromResult(new ProviderConnectionStatus(false, "Test not connected"));
        public Task<OAuthStartResult> Begin(string id, string binding, CancellationToken ct)
        {
            Begins++; expectedBinding = binding;
            return Task.FromResult(new OAuthStartResult("test-state", "https://auth.openai.com/api/accounts/authorize?state=test-state", null, null, DateTimeOffset.UtcNow.AddMinutes(10)));
        }
        public Task CompleteChatGpt(string state, string code, string issuedClientId, string binding, CancellationToken ct)
        {
            Assert.Equal(expectedBinding, binding);
            Assert.Equal("test-state", state);
            Assert.Equal("test-code", code);
            Assert.Equal("issued-client", issuedClientId);
            Completions++;
            return Task.CompletedTask;
        }
        public Task<ProviderConnectionStatus> Poll(string id, string flow, string binding, CancellationToken ct) => throw new NotSupportedException();
        public Task CancelPending(string id, string flow, string binding, CancellationToken ct) => Task.CompletedTask;
        public Task Disconnect(string id, CancellationToken ct) => Task.CompletedTask;
        public Task<string> AccessToken(string id, bool forceRefresh, CancellationToken ct) => throw new NotSupportedException();
    }

    private static string Token(string html) => WebUtility.HtmlDecode(Regex.Match(html, "name=\"?['\"]?__RequestVerificationToken['\"]?[^>]*value=['\"]([^'\"]+)['\"]").Groups[1].Value);

    private static async Task Login(HttpClient client)
    {
        var html = await client.GetStringAsync("/login");
        var token = Token(html);
        Assert.NotEmpty(token);
        using var response = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["password"] = TestHost.Password, ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task ChatGptUsesAuthenticatedCsrfProtectedPostAndBoundSingleUseCallbackCookie()
    {
        var auth = new EndpointAuth();
        using var host = new TestHost { ConfigureTestServices = s => { s.RemoveAll<ISubscriptionAuth>(); s.AddSingleton<ISubscriptionAuth>(auth); } };
        using var client = host.CreateClient(new() { BaseAddress = new Uri("http://127.0.0.1:5080"), AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/providers/chatgpt-subscription/connect", new StringContent(""))).StatusCode);
        Assert.Equal(0, auth.Begins);
        await Login(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/providers/chatgpt-subscription/connect", new StringContent(""))).StatusCode);
        Assert.Equal(0, auth.Begins);
        var html = await client.GetStringAsync("/settings?provider=chatgpt-subscription");
        var token = Token(html); Assert.NotEmpty(token);
        using var start = await client.PostAsync("/providers/chatgpt-subscription/connect", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Equal("auth.openai.com", start.Headers.Location!.Host);
        var cookie = Assert.Single(start.Headers.GetValues("Set-Cookie"), c => c.StartsWith("studio.chatgpt.oauth=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/auth/callback", cookie, StringComparison.OrdinalIgnoreCase);
        using var callback = await client.GetAsync("/auth/callback?state=test-state&code=test-code&client_id=issued-client");
        Assert.Contains("connection=connected", callback.Headers.Location!.OriginalString);
        Assert.Equal("no-store", callback.Headers.CacheControl!.ToString());
        Assert.Equal(1, auth.Completions);
        using var replay = await client.GetAsync("/auth/callback?state=test-state&code=test-code&client_id=issued-client");
        Assert.Contains("connection=failed", replay.Headers.Location!.OriginalString);
        Assert.Equal(1, auth.Completions);
    }

    [Fact]
    public async Task LocalhostSessionIsDirectedToCanonicalLoopbackBeforeOAuthStarts()
    {
        var auth = new EndpointAuth();
        using var host = new TestHost { ConfigureTestServices = s => { s.RemoveAll<ISubscriptionAuth>(); s.AddSingleton<ISubscriptionAuth>(auth); } };
        using var client = host.CreateClient(new() { BaseAddress = new Uri("http://localhost:5080"), AllowAutoRedirect = false });
        await Login(client);
        var token = Token(await client.GetStringAsync("/settings?provider=chatgpt-subscription"));
        using var start = await client.PostAsync("/providers/chatgpt-subscription/connect", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Equal("127.0.0.1", start.Headers.Location!.Host);
        Assert.Contains("local-required", start.Headers.Location.Query);
        Assert.Equal(0, auth.Begins);
    }
}
