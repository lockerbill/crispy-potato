using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using FacelessStudio.Core.Abstractions;
using FacelessStudio.Providers.Auth;
using FacelessStudio.Providers.Http;

namespace FacelessStudio.Providers.Llm.Xai;

/// <summary>Đăng nhập xAI Grok bằng tài khoản SuperGrok / X Premium+ (OAuth device code, RFC 8628).</summary>
public sealed class XaiAccount(IHttpClientFactory httpFactory) : IOAuthAccount
{
    private const string Issuer = "https://auth.x.ai";
    private const string ClientId = "b1a00492-073a-47ea-816f-4c329264a828";
    private const string Scope = "openid profile email offline_access grok-cli:access api:access";
    private const string DeviceEndpoint = Issuer + "/oauth2/device/code";
    private const string DiscoveryUrl = Issuer + "/.well-known/openid-configuration";
    private const string SubscriptionMessage = "xAI: gói hiện tại không có quyền dùng API (cần SuperGrok hoặc X Premium+).";
    private const string ExpiredMessage = "Phiên đăng nhập xAI đã hết hạn, vào Cài đặt để đăng nhập lại.";

    private string? _tokenEndpoint;

    public string Id => "xai";
    public string Name => "xAI Grok (SuperGrok / X Premium+)";
    public string Help => "Đăng nhập bằng tài khoản có gói SuperGrok hoặc X Premium+. Không cần API key.";

    /// <summary>Token xAI sống ngắn: chỉ refresh khi gần hết hạn (nửa đời còn lại, tối đa 1 giờ, tối thiểu 1 phút).</summary>
    public TimeSpan RefreshSkew(OAuthTokens tokens)
    {
        var half = tokens.ExpiresAt is { } exp ? (exp - DateTimeOffset.UtcNow) / 2 : TimeSpan.FromMinutes(5);
        if (half > TimeSpan.FromHours(1)) half = TimeSpan.FromHours(1);
        return half < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : half;
    }

    public async Task<DeviceLogin> StartAsync(CancellationToken ct)
    {
        var tokenEndpoint = await DiscoverAsync(ct);
        var (status, json) = await PostFormAsync(DeviceEndpoint, new()
        {
            ["client_id"] = ClientId,
            ["scope"] = Scope,
        }, ct);
        var deviceCode = Str(json, "device_code");
        var userCode = Str(json, "user_code");
        var url = Str(json, "verification_uri_complete") ?? Str(json, "verification_uri");
        if (status != HttpStatusCode.OK || deviceCode == null || userCode == null || url == null)
            throw new ProviderException($"xAI không cấp được mã đăng nhập ({(int)status}): {Describe(json)}");

        var interval = TimeSpan.FromSeconds(Math.Max(1, Num(json, "interval") ?? 5));
        var expiresIn = Num(json, "expires_in") ?? 600;
        return new DeviceLogin(url, userCode, interval, DateTimeOffset.UtcNow.AddSeconds(expiresIn), new JsonObject
        {
            ["device_code"] = deviceCode,
            ["token_endpoint"] = tokenEndpoint,
        });
    }

    public async Task<OAuthTokens?> PollAsync(DeviceLogin login, CancellationToken ct)
    {
        var deviceCode = login.State["device_code"]?.GetValue<string>()
                         ?? throw new ProviderException("Thiếu mã thiết bị xAI, hãy đăng nhập lại.");
        var endpoint = await ValidEndpointAsync(login.State["token_endpoint"]?.GetValue<string>(), ct);
        var (status, json) = await PostFormAsync(endpoint, new()
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["client_id"] = ClientId,
            ["device_code"] = deviceCode,
        }, ct);

        if (Str(json, "access_token") != null) return ToTokens(json!, endpoint, null);
        switch (Str(json, "error"))
        {
            case "authorization_pending":
                return null;
            case "slow_down":
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return null;
            case "access_denied":
                throw new ProviderException("Anh đã từ chối đăng nhập xAI.");
            case "expired_token":
                throw new ProviderException("Mã đăng nhập xAI đã hết hạn, hãy bấm đăng nhập lại.");
            default:
                throw new ProviderException(status == HttpStatusCode.Forbidden
                    ? SubscriptionMessage
                    : $"Đăng nhập xAI thất bại ({(int)status}): {Describe(json)}");
        }
    }

    public async Task<OAuthTokens> RefreshAsync(OAuthTokens tokens, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(tokens.RefreshToken)) throw new ProviderException(ExpiredMessage);
        var endpoint = await ValidEndpointAsync(tokens.Get("token_endpoint"), ct);
        var (status, json) = await PostFormAsync(endpoint, new()
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["refresh_token"] = tokens.RefreshToken,
        }, ct);

        if (status == HttpStatusCode.OK && Str(json, "access_token") != null)
            return ToTokens(json!, endpoint, tokens.RefreshToken);
        throw new ProviderException(status switch
        {
            HttpStatusCode.Forbidden => SubscriptionMessage,
            HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized => ExpiredMessage,
            _ => $"Không làm mới được đăng nhập xAI ({(int)status}): {Describe(json)}",
        });
    }

    private static OAuthTokens ToTokens(JsonNode json, string endpoint, string? previousRefresh)
    {
        var access = Str(json, "access_token")!;
        var expiresAt = JwtHelper.Expiry(access)
                        ?? (Num(json, "expires_in") is { } secs ? DateTimeOffset.UtcNow.AddSeconds(secs) : (DateTimeOffset?)null);
        return new OAuthTokens(access, Str(json, "refresh_token") ?? previousRefresh, expiresAt,
            JwtHelper.Email(Str(json, "id_token")) ?? JwtHelper.Email(access),
            new Dictionary<string, string> { ["token_endpoint"] = endpoint });
    }

    /// <summary>Lấy token_endpoint từ discovery; chỉ nhận host x.ai để endpoint giả không lấy được refresh token.</summary>
    private async Task<string> DiscoverAsync(CancellationToken ct)
    {
        if (_tokenEndpoint != null) return _tokenEndpoint;
        var json = await HttpHelpers.GetJsonAsync(httpFactory.CreateClient("providers"), "xAI", DiscoveryUrl, ct);
        return _tokenEndpoint = Validate(Str(json, "token_endpoint"));
    }

    private async Task<string> ValidEndpointAsync(string? stored, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(stored) ? await DiscoverAsync(ct) : Validate(stored);

    private static string Validate(string? endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !(uri.Host.Equals("x.ai", StringComparison.OrdinalIgnoreCase)
                 || uri.Host.EndsWith(".x.ai", StringComparison.OrdinalIgnoreCase)))
            throw new ProviderException("Địa chỉ đăng nhập xAI không hợp lệ (không thuộc x.ai), đã dừng để bảo vệ tài khoản.");
        return uri.ToString();
    }

    private async Task<(HttpStatusCode Status, JsonNode? Json)> PostFormAsync(string url, Dictionary<string, string> form,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        try
        {
            using var resp = await httpFactory.CreateClient("providers").SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            JsonNode? json;
            try
            {
                json = JsonNode.Parse(text);
            }
            catch (System.Text.Json.JsonException)
            {
                json = new JsonObject { ["error_description"] = text.Length > 300 ? text[..300] : text };
            }
            return (resp.StatusCode, json);
        }
        catch (HttpRequestException e)
        {
            throw new ProviderException($"Không kết nối được xAI: {e.Message}", e);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException("xAI phản hồi quá lâu (timeout).", e);
        }
    }

    private static string? Str(JsonNode? json, string key) =>
        json?[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static int? Num(JsonNode? json, string key)
    {
        if (json?[key] is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        return v.TryGetValue<string>(out var s) && int.TryParse(s, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static string Describe(JsonNode? json)
    {
        var text = Str(json, "error_description") ?? Str(json, "error") ?? json?.ToJsonString() ?? "";
        return text.Length > 300 ? text[..300] : text;
    }
}
