using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FacelessStudio.Core.Abstractions;
using FacelessStudio.Providers.Auth;

namespace FacelessStudio.Providers.Llm.Codex;

/// <summary>Đăng nhập tài khoản ChatGPT (Plus/Pro/Business) bằng OAuth device code để dùng model Codex.</summary>
public sealed class CodexAccount(IHttpClientFactory httpFactory) : IOAuthAccount
{
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string Issuer = "https://auth.openai.com";
    private const string TokenUrl = Issuer + "/oauth/token";
    private const string RelogMessage = "Phiên đăng nhập ChatGPT đã hết hạn, vào Cài đặt để đăng nhập lại.";

    public string Id => "codex";
    public string Name => "ChatGPT / Codex";

    public string Help => "Cần gói ChatGPT Plus/Pro/Business. Nếu báo lỗi khi xin mã: bật Device code login trong " +
                          "ChatGPT → Settings → Security. Hoặc cài Codex CLI, chạy codex login rồi bấm Nhập từ CLI " +
                          "(sau đó Codex CLI có thể phải đăng nhập lại vì hai bên dùng chung refresh token).";

    public bool CanImport => true;

    public async Task<DeviceLogin> StartAsync(CancellationToken ct)
    {
        var (status, text) = await PostAsync(Issuer + "/api/accounts/deviceauth/usercode",
            new StringContent(new JsonObject { ["client_id"] = ClientId }.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        if (status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
            throw new ProviderException("ChatGPT chưa cho đăng nhập bằng mã thiết bị. Hãy bật \"Device code login\" trong " +
                                        "ChatGPT → Settings → Security (hoặc nhờ quản trị viên workspace bật).");
        if (status == HttpStatusCode.TooManyRequests)
            throw new ProviderException("ChatGPT: xin mã đăng nhập quá nhiều lần (429). Đợi vài phút rồi thử lại.");
        if (status != HttpStatusCode.OK)
            throw new ProviderException($"ChatGPT không cấp mã đăng nhập (lỗi {(int)status}). {Trim(text)}");

        var json = ParseObject(text);
        var deviceId = Str(json?["device_auth_id"]);
        var userCode = Str(json?["user_code"]);
        if (deviceId == null || userCode == null)
            throw new ProviderException("ChatGPT trả về mã đăng nhập thiếu thông tin. Thử lại sau.");

        var seconds = int.TryParse(Str(json?["interval"]), out var i) ? Math.Max(3, i) : 5;
        return new DeviceLogin($"{Issuer}/codex/device", userCode, TimeSpan.FromSeconds(seconds),
            DateTimeOffset.UtcNow.AddMinutes(15),
            new JsonObject { ["device_auth_id"] = deviceId, ["user_code"] = userCode });
    }

    public async Task<OAuthTokens?> PollAsync(DeviceLogin login, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["device_auth_id"] = Str(login.State["device_auth_id"]),
            ["user_code"] = Str(login.State["user_code"]) ?? login.UserCode,
        };
        var (status, text) = await PostAsync(Issuer + "/api/accounts/deviceauth/token",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        // 403/404: người dùng chưa đăng nhập xong.
        if (status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound) return null;
        if (status != HttpStatusCode.OK)
            throw new ProviderException($"ChatGPT báo lỗi khi chờ đăng nhập ({(int)status}). {Trim(text)}");

        var json = ParseObject(text);
        var code = Str(json?["authorization_code"]);
        var verifier = Str(json?["code_verifier"]);
        if (code == null || verifier == null)
            throw new ProviderException("ChatGPT trả về kết quả đăng nhập thiếu authorization_code. Thử đăng nhập lại.");

        var (tStatus, tText) = await PostAsync(TokenUrl, Form(
            ("grant_type", "authorization_code"), ("code", code),
            ("redirect_uri", Issuer + "/deviceauth/callback"), ("client_id", ClientId), ("code_verifier", verifier)), ct);
        if (tStatus == HttpStatusCode.TooManyRequests)
            throw new ProviderException("ChatGPT: đổi mã đăng nhập bị giới hạn (429). Đợi vài phút rồi thử lại.");
        if (tStatus != HttpStatusCode.OK)
            throw new ProviderException($"ChatGPT không đổi được mã đăng nhập lấy token (lỗi {(int)tStatus}). {Trim(tText)}");
        return BuildTokens(ParseObject(tText), null)
               ?? throw new ProviderException("ChatGPT không trả về access token. Thử đăng nhập lại.");
    }

    public async Task<OAuthTokens> RefreshAsync(OAuthTokens tokens, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(tokens.RefreshToken)) throw new ProviderException(RelogMessage);
        var (status, text) = await PostAsync(TokenUrl, Form(
            ("grant_type", "refresh_token"), ("refresh_token", tokens.RefreshToken), ("client_id", ClientId)), ct);
        if (status == HttpStatusCode.TooManyRequests)
            throw new ProviderException("ChatGPT: làm mới phiên đăng nhập bị giới hạn (429). Thử lại sau.");
        if (status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new ProviderException(RelogMessage);
        if (status != HttpStatusCode.OK)
            throw new ProviderException($"ChatGPT không làm mới được phiên đăng nhập (lỗi {(int)status}). {Trim(text)}");
        return BuildTokens(ParseObject(text), tokens)
               ?? throw new ProviderException("ChatGPT không trả về access token khi làm mới. Hãy đăng nhập lại trong Cài đặt.");
    }

    /// <summary>Đọc (không ghi) auth.json của Codex CLI trong $CODEX_HOME hoặc ~/.codex.</summary>
    public Task<OAuthTokens?> ImportAsync(CancellationToken ct)
    {
        var home = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(home))
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var file = Path.Combine(home, "auth.json");
        try
        {
            if (!File.Exists(file)) return Task.FromResult<OAuthTokens?>(null);
            var tokens = JsonNode.Parse(File.ReadAllText(file))?["tokens"];
            var access = Str(tokens?["access_token"]);
            if (access == null) return Task.FromResult<OAuthTokens?>(null);
            return Task.FromResult<OAuthTokens?>(MakeTokens(access, Str(tokens?["refresh_token"]),
                Str(tokens?["id_token"]), null, Str(tokens?["account_id"]), null));
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Task.FromResult<OAuthTokens?>(null);
        }
    }

    private static OAuthTokens? BuildTokens(JsonObject? json, OAuthTokens? previous)
    {
        var access = Str(json?["access_token"]);
        if (access == null) return null;
        double? expiresIn = double.TryParse(Str(json?["expires_in"]), out var s) ? s : null;
        return MakeTokens(access, Str(json?["refresh_token"]) ?? previous?.RefreshToken, Str(json?["id_token"]),
            expiresIn, previous?.Get("account_id"), previous?.AccountLabel);
    }

    private static OAuthTokens MakeTokens(string access, string? refresh, string? idToken, double? expiresIn,
        string? fallbackAccountId, string? fallbackLabel)
    {
        var expiry = JwtHelper.Expiry(access)
                     ?? DateTimeOffset.UtcNow.AddSeconds(expiresIn is > 0 ? expiresIn.Value : 3600);
        var label = JwtHelper.Email(idToken) ?? JwtHelper.Email(access) ?? fallbackLabel;

        var extra = new Dictionary<string, string>();
        var auth = AuthClaims(access);
        var idAuth = AuthClaims(idToken);
        var accountId = Str(auth?["chatgpt_account_id"]) ?? Str(idAuth?["chatgpt_account_id"]) ?? fallbackAccountId;
        if (accountId != null) extra["account_id"] = accountId;
        var residency = Str(auth?["chatgpt_data_residency"]) ?? Str(auth?["chatgpt_compute_residency"])
                        ?? Str(idAuth?["chatgpt_data_residency"]) ?? Str(idAuth?["chatgpt_compute_residency"]);
        if (residency != null) extra["residency"] = residency;
        if (idToken != null) extra["id_token"] = idToken;
        return new OAuthTokens(access, refresh, expiry, label, extra);
    }

    private static JsonObject? AuthClaims(string? jwt) => JwtHelper.Payload(jwt)?["https://api.openai.com/auth"] as JsonObject;

    private static string? Str(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        var s = v.TryGetValue<string>(out var str) ? str : v.ToString();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static JsonObject? ParseObject(string text)
    {
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Trim(string text) => text.Length > 300 ? text[..300] : text;

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));

    /// <summary>POST và trả về cả mã trạng thái lẫn nội dung, không ném lỗi theo mã HTTP (poll cần 403/404).</summary>
    private async Task<(HttpStatusCode Status, string Body)> PostAsync(string url, HttpContent content, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("User-Agent", CodexClient.UserAgent);
        try
        {
            using var resp = await httpFactory.CreateClient("providers").SendAsync(req, ct);
            return (resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
        }
        catch (HttpRequestException e)
        {
            throw new ProviderException($"Không kết nối được ChatGPT: {e.Message}", e);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException("ChatGPT phản hồi quá lâu (timeout).", e);
        }
    }
}
