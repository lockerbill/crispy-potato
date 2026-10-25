using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.Tokens;
using Studio.Domain;
using Studio.Services;

namespace Studio.Providers;

/// <summary>Owns subscription grants. Tokens never enter ProviderConfig or job snapshots.</summary>
public sealed class SubscriptionAuthService(SettingsService settings, IHttpClientFactory clients, IConfiguration configuration) : ISubscriptionAuth
{
    private const string OpenAi = "https://auth.openai.com";
    private const string Grok = "https://auth.x.ai";
    private const string ChatScope = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
    private const string GrokScope = "openid profile email offline_access grok-cli:access api:access";
    // Experimental compatibility reference: NousResearch/hermes-agent@850efff14f0d2a6de205eb1363c35dac35906763.
    // This is the public Grok CLI identity, not an application registered to this project.
    private string GrokClient => configuration["Studio:SuperGrokClientId"] ?? "b1a00492-073a-47ea-816f-4c329264a828";
    private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new();
    private readonly ConcurrentDictionary<string, Attempt> attempts = new();
    public string ChatGptRedirectUri
    {
        get
        {
            var value = configuration["Studio:ChatGptRedirectUri"] ?? "http://127.0.0.1:5080/auth/callback";
            if (!Uri.TryCreate(value, UriKind.Absolute, out var u) || u.Scheme != "http" || u.Host != "127.0.0.1" || u.AbsolutePath != "/auth/callback" || u.Query.Length != 0 || u.Fragment.Length != 0 || u.UserInfo.Length != 0)
                throw new ProviderException("ChatGPT callback phải là http://127.0.0.1:<port>/auth/callback.");
            return value;
        }
    }
    private sealed class Attempt
    {
        public required string Id { get; init; }
        public required string Binding { get; init; }
        public required DateTimeOffset Expires { get; init; }
        public string Verifier { get; init; } = "";
        public string Nonce { get; init; } = "";
        public string ClientId { get; init; } = "";
        public string Account { get; init; } = "";
        public string Redirect { get; init; } = "";
        public string DeviceCode { get; init; } = "";
        public int Interval { get; set; } = 5;
        public DateTimeOffset NextPoll { get; set; }
        public bool Completed { get; set; }
    }
    public sealed record Credential(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string ClientId, string Account, string IdToken = "", string Scope = "");
    public sealed record Metadata(string? Account, DateTimeOffset? ExpiresAt);
    private static string Random() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
    private SemaphoreSlim Gate(string id)
    {
        if (id is not ("chatgpt-subscription" or "supergrok")) throw new ProviderException("Không hỗ trợ OAuth cho provider này.");
        return locks.GetOrAdd(id, _ => new(1, 1));
    }
    private async Task<Credential?> Read(string id)
    {
        var value = await settings.Secret("oauth:" + id);
        if (value is null) return null;
        try { return Json.Read<Credential>(value); }
        catch { throw new ProviderException("Thông tin đăng nhập không hợp lệ. Hãy kết nối lại."); }
    }
    private Task Save(string id, Credential c) => settings.Save("oauth:" + id, new Metadata(c.Account, c.ExpiresAt), Json.Write(c));
    public async Task<ProviderConnectionStatus> Status(string providerId, CancellationToken ct)
    {
        _ = Gate(providerId); ct.ThrowIfCancellationRequested();
        var c = await Read(providerId);
        return c is null ? new(false, "Chưa kết nối.") : new(true, "Đã lưu kết nối; kiểm tra model để xác minh quyền sử dụng.", c.Account);
    }
    public async Task<OAuthStartResult> Begin(string providerId, string binding, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(binding)) throw new ProviderException("Phiên đăng nhập không hợp lệ.");
        var gate = Gate(providerId); await gate.WaitAsync(ct);
        try
        {
            attempts.TryRemove(providerId, out _);
            var flow = Random(); var expires = DateTimeOffset.UtcNow.AddMinutes(10);
            if (providerId == "chatgpt-subscription")
            {
                var old = await Read(providerId);
                var hostId = await settings.Get<string>("oauth-host-id");
                if (hostId is null) { hostId = "urn:uuid:" + Guid.NewGuid(); await settings.Save("oauth-host-id", hostId); }
                var verifier = Random(); var nonce = Random(); var redirect = ChatGptRedirectUri;
                var args = new Dictionary<string, string> {
                    ["client_id"] = old?.ClientId ?? "dynamic_agent_client", ["ext_agent_host_id"] = hostId,
                    ["response_type"] = "code", ["redirect_uri"] = redirect, ["scope"] = ChatScope,
                    ["resource"] = "https://api.openai.com/v1", ["state"] = flow, ["nonce"] = nonce,
                    ["code_challenge_method"] = "S256", ["code_challenge"] = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) };
                if (old is null) args["agent_name_hint"] = "YouTube Video Studio";
                else if (old.IdToken.Length > 0) args["id_token_hint"] = old.IdToken;
                var a = new Attempt { Id = flow, Binding = binding, Expires = expires, Verifier = verifier, Nonce = nonce, ClientId = old?.ClientId ?? "", Account = old?.Account ?? "", Redirect = redirect };
                attempts[providerId] = a;
                return new(flow, OpenAi + "/api/accounts/authorize?" + string.Join('&', args.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value))), null, null, expires);
            }
            var data = await Form(Grok + "/oauth2/device/code", new() { ["client_id"] = GrokClient, ["scope"] = GrokScope }, ct);
            var seconds = Integer(data, "expires_in", 600);
            var interval = Math.Clamp(Integer(data, "interval", 5), 1, 60);
            if (seconds is < 1 or > 3600) throw new ProviderException("Thời hạn đăng nhập không hợp lệ.");
            var verify = Required(data, "verification_uri_complete", false);
            if (verify.Length == 0) verify = Required(data, "verification_uri");
            if (!Uri.TryCreate(verify, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host is not ("accounts.x.ai" or "auth.x.ai" or "x.ai") || uri.UserInfo.Length > 0)
                throw new ProviderException("Provider trả địa chỉ xác minh không hợp lệ.");
            expires = DateTimeOffset.UtcNow.AddSeconds(seconds);
            attempts[providerId] = new Attempt { Id = flow, Binding = binding, Expires = expires, DeviceCode = Required(data, "device_code"), Interval = interval, NextPoll = DateTimeOffset.UtcNow.AddSeconds(interval), ClientId = GrokClient };
            return new(flow, null, verify, Required(data, "user_code"), expires, interval);
        }
        finally { gate.Release(); }
    }
    private Attempt Pending(string id, string flow, string binding)
    {
        if (!attempts.TryGetValue(id, out var a) || a.Id != flow || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a.Binding), Encoding.UTF8.GetBytes(binding)))
            throw new ProviderException("Phiên đăng nhập không hợp lệ hoặc đã bị hủy.");
        if (a.Expires <= DateTimeOffset.UtcNow) { attempts.TryRemove(id, out _); throw new ProviderException("Phiên đăng nhập hết hạn. Hãy bắt đầu lại."); }
        return a;
    }
    public async Task<ProviderConnectionStatus> Poll(string providerId, string flowId, string binding, CancellationToken ct)
    {
        var gate = Gate(providerId); await gate.WaitAsync(ct);
        try
        {
            var a = Pending(providerId, flowId, binding);
            if (a.Completed) return await Status(providerId, ct);
            if (providerId != "supergrok" || DateTimeOffset.UtcNow < a.NextPoll) return new(false, "Đang chờ xác nhận trong trình duyệt.");
            a.NextPoll = DateTimeOffset.UtcNow.AddSeconds(a.Interval);
            var data = await Form(Grok + "/oauth2/token", new() { ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code", ["client_id"] = a.ClientId, ["device_code"] = a.DeviceCode }, ct, true);
            var error = Required(data, "error", false);
            if (error == "authorization_pending") return new(false, "Đang chờ xác nhận.");
            if (error == "slow_down") { a.Interval = Math.Min(120, a.Interval + 5); a.NextPoll = DateTimeOffset.UtcNow.AddSeconds(a.Interval); return new(false, "Đang chờ xác nhận."); }
            if (error.Length > 0) { attempts.TryRemove(providerId, out _); throw new ProviderException("Đăng nhập bị từ chối hoặc hết hạn. Hãy bắt đầu lại."); }
            var c = Tokens(data, a.ClientId, "SuperGrok");
            ct.ThrowIfCancellationRequested(); await Save(providerId, c); a.Completed = true;
            return new(true, "Đã kết nối SuperGrok; quyền inference còn phụ thuộc tài khoản.", c.Account);
        }
        finally { gate.Release(); }
    }
    public async Task CompleteChatGpt(string state, string code, string issuedClientId, string binding, CancellationToken ct)
    {
        const string id = "chatgpt-subscription";
        var gate = Gate(id); await gate.WaitAsync(ct);
        try
        {
            var a = Pending(id, state, binding);
            if (a.Completed) throw new ProviderException("Mã đăng nhập đã được sử dụng.");
            attempts.TryRemove(id, out _); // Authorization codes and attempts are single-use, including failures.
            if (string.IsNullOrWhiteSpace(code) || code.Length > 8192) throw new ProviderException("Thiếu mã đăng nhập.");
            var client = a.ClientId.Length > 0 ? a.ClientId : issuedClientId;
            if (string.IsNullOrWhiteSpace(client) || client == "dynamic_agent_client" || (a.ClientId.Length > 0 && issuedClientId.Length > 0 && client != issuedClientId)) throw new ProviderException("Client đăng nhập không hợp lệ.");
            var data = await Form(OpenAi + "/api/accounts/oauth/token", new() { ["grant_type"] = "authorization_code", ["client_id"] = client, ["code"] = code, ["code_verifier"] = a.Verifier, ["redirect_uri"] = a.Redirect, ["resource"] = "https://api.openai.com/v1" }, ct);
            var scope = Required(data, "scope");
            if (!scope.Split(' ').Contains("chatgpt.tokens.use.direct")) throw new ProviderException("Tài khoản chưa cho phép sử dụng ChatGPT plan.");
            var account = await ValidateIdentity(Required(data, "id_token"), client, a.Nonce, ct);
            if (a.Account.Length > 0 && a.Account != account) throw new ProviderException("Tài khoản trả về khác tài khoản đã chọn.");
            var credential = Tokens(data, client, account);
            ct.ThrowIfCancellationRequested(); await Save(id, credential);
        }
        finally { gate.Release(); }
    }
    private async Task<string> ValidateIdentity(string token, string client, string nonce, CancellationToken ct)
    {
        try
        {
            var metadata = await GetJson(OpenAi + "/.well-known/openid-configuration", ct);
            var issuer = Required(metadata, "issuer"); var jwksUrl = Required(metadata, "jwks_uri");
            if (issuer != OpenAi || !Uri.TryCreate(jwksUrl, UriKind.Absolute, out var u) || u.Scheme != "https" || u.Host != "auth.openai.com" || u.UserInfo.Length > 0) throw new InvalidDataException();
            var keys = new JsonWebKeySet((await GetJson(jwksUrl, ct)).ToJsonString());
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false, MaximumTokenSizeInBytes = 32768 };
            var principal = handler.ValidateToken(token, new TokenValidationParameters {
                ValidateIssuer = true, ValidIssuer = OpenAi, ValidateAudience = true, ValidAudience = client,
                ValidateIssuerSigningKey = true, IssuerSigningKeys = keys.GetSigningKeys(), ValidateLifetime = true,
                RequireExpirationTime = true, RequireSignedTokens = true, ClockSkew = TimeSpan.FromSeconds(30),
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha256] }, out _);
            if (principal.FindFirst("nonce")?.Value != nonce) throw new InvalidDataException();
            return principal.FindFirst("sub")?.Value is { Length: > 0 } subject ? subject : throw new InvalidDataException();
        }
        catch (OperationCanceledException) { throw; }
        catch { throw new ProviderException("Không xác minh được danh tính ChatGPT. Hãy đăng nhập lại."); }
    }
    public async Task Disconnect(string providerId, CancellationToken ct)
    {
        var gate = Gate(providerId); await gate.WaitAsync(ct);
        try { attempts.TryRemove(providerId, out _); await settings.Save("oauth:" + providerId, new Metadata(null, null), clearSecret: true); }
        finally { gate.Release(); }
    }
    public async Task CancelPending(string providerId, string flowId, string binding, CancellationToken ct)
    {
        var gate = Gate(providerId); await gate.WaitAsync(ct);
        try { if (attempts.TryGetValue(providerId, out var a) && a.Id == flowId && a.Binding == binding) attempts.TryRemove(providerId, out _); }
        finally { gate.Release(); }
    }
    public async Task<string> AccessToken(string providerId, bool forceRefresh, CancellationToken ct)
    {
        var gate = Gate(providerId); await gate.WaitAsync(ct);
        try
        {
            var old = await Read(providerId) ?? throw new ProviderException("Cần đăng nhập subscription trước khi chạy.");
            if (!forceRefresh && old.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(120)) return old.AccessToken;
            var form = new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["client_id"] = old.ClientId, ["refresh_token"] = old.RefreshToken };
            if (providerId == "chatgpt-subscription") form["resource"] = "https://api.openai.com/v1";
            var data = await Form(providerId == "supergrok" ? Grok + "/oauth2/token" : OpenAi + "/api/accounts/oauth/token", form, ct);
            var updated = Tokens(data, old.ClientId, old.Account, old);
            // Persist rotated refresh tokens even when the caller cancels after a successful exchange.
            await Save(providerId, updated); ct.ThrowIfCancellationRequested(); return updated.AccessToken;
        }
        finally { gate.Release(); }
    }
    private static Credential Tokens(JsonNode d, string client, string account, Credential? old = null)
    {
        var access = Required(d, "access_token"); var refresh = Required(d, "refresh_token", false);
        if (refresh.Length == 0) refresh = old?.RefreshToken ?? throw new ProviderException("Provider không trả refresh token.");
        var expires = Integer(d, "expires_in", 3600);
        if (expires is <= 0 or > 2592000) throw new ProviderException("Thời hạn token không hợp lệ.");
        return new(access, refresh, DateTimeOffset.UtcNow.AddSeconds(expires), client, account, Required(d, "id_token", false) is { Length: > 0 } ident ? ident : old?.IdToken ?? "", Required(d, "scope", false) is { Length: > 0 } scope ? scope : old?.Scope ?? "");
    }
    private async Task<JsonNode> Form(string url, Dictionary<string, string> form, CancellationToken ct, bool polling = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        return await SendJson(request, ct, polling);
    }
    private async Task<JsonNode> GetJson(string url, CancellationToken ct)
    { using var req = new HttpRequestMessage(HttpMethod.Get, url); return await SendJson(req, ct); }
    private async Task<JsonNode> SendJson(HttpRequestMessage req, CancellationToken ct, bool polling = false)
    {
        try
        {
            var client = clients.CreateClient("subscription-auth");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!res.IsSuccessStatusCode && !(polling && res.StatusCode == System.Net.HttpStatusCode.BadRequest))
                throw new ProviderException(res.StatusCode == System.Net.HttpStatusCode.Forbidden ? "Subscription không có quyền API (403). Đổi sang API key nếu muốn; đăng nhập lại không giải quyết quyền tài khoản." : $"Xác thực subscription thất bại (HTTP {(int)res.StatusCode}). Hãy kiểm tra tài khoản hoặc kết nối lại.");
            var bytes = await BoundedBody.Read(res.Content, 128 * 1024, timeout.Token);
            return JsonNode.Parse(bytes) as JsonObject ?? throw new ProviderException("Provider trả dữ liệu đăng nhập không hợp lệ.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ProviderException("Kết nối xác thực hết thời gian chờ."); }
        catch (HttpRequestException) { throw new ProviderException("Không kết nối được máy chủ xác thực."); }
        catch (System.Text.Json.JsonException) { throw new ProviderException("Provider trả dữ liệu đăng nhập không hợp lệ."); }
    }
    private static string Required(JsonNode d, string field, bool required = true)
    {
        var node = d[field];
        if (node is not null && (node is not JsonValue scalar || !scalar.TryGetValue<string>(out _))) throw new ProviderException("Provider trả trường đăng nhập không hợp lệ.");
        var value = node?.GetValue<string>() ?? "";
        if ((required && string.IsNullOrWhiteSpace(value)) || value.Length > 32768) throw new ProviderException("Provider thiếu trường đăng nhập hợp lệ.");
        return value;
    }
    private static int Integer(JsonNode d, string field, int fallback)
    {
        if (d[field] is null) return fallback;
        if (d[field] is JsonValue scalar && scalar.TryGetValue<int>(out var value)) return value;
        throw new ProviderException("Provider trả thời hạn đăng nhập không hợp lệ.");
    }
}

internal static class BoundedBody
{
    public static async Task<string> Read(HttpContent content, int limit, CancellationToken ct)
    {
        if (content.Headers.ContentLength > limit) throw new ProviderException("Phản hồi provider vượt giới hạn.");
        await using var stream = await content.ReadAsStreamAsync(ct); using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true) { var n = await stream.ReadAsync(buffer, ct); if (n == 0) break; if (output.Length + n > limit) throw new ProviderException("Phản hồi provider vượt giới hạn."); await output.WriteAsync(buffer.AsMemory(0, n), ct); }
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
