using System.Text.Json.Nodes;

namespace FacelessStudio.Providers.Auth;

/// <summary>Token đăng nhập của một tài khoản (ChatGPT, SuperGrok...). Lưu ở data/oauth/&lt;id&gt;.json.</summary>
public sealed record OAuthTokens(
    string AccessToken,
    string? RefreshToken = null,
    DateTimeOffset? ExpiresAt = null,
    string? AccountLabel = null,
    Dictionary<string, string>? Extra = null)
{
    public string? Get(string key) => Extra?.GetValueOrDefault(key);
}

/// <summary>Phiên đăng nhập kiểu device code: người dùng mở VerificationUrl rồi nhập UserCode.</summary>
public sealed record DeviceLogin(
    string VerificationUrl,
    string UserCode,
    TimeSpan Interval,
    DateTimeOffset ExpiresAt,
    JsonObject State);

/// <summary>Một loại tài khoản đăng nhập bằng OAuth (device code), hiện ở mục "Tài khoản đăng nhập" trong Cài đặt.</summary>
public interface IOAuthAccount
{
    /// <summary>Id ngắn, cũng là tên file token (vd. "codex", "xai").</summary>
    string Id { get; }

    string Name { get; }

    /// <summary>Hướng dẫn ngắn hiện dưới nút đăng nhập.</summary>
    string Help { get; }

    /// <summary>Có nhập được token từ CLI chính chủ đã đăng nhập trên máy không (vd. ~/.codex/auth.json).</summary>
    bool CanImport => false;

    /// <summary>Refresh token trước khi hết hạn bao lâu.</summary>
    TimeSpan RefreshSkew(OAuthTokens tokens) => TimeSpan.FromMinutes(5);

    /// <summary>Xin mã device code. Lỗi thì ném ProviderException.</summary>
    Task<DeviceLogin> StartAsync(CancellationToken ct);

    /// <summary>
    /// Hỏi server một lần xem người dùng đăng nhập xong chưa. Trả null nếu còn chờ (nếu server bảo
    /// "slow_down" thì tự chờ thêm trước khi trả null). Bị từ chối / hết hạn thì ném ProviderException.
    /// </summary>
    Task<OAuthTokens?> PollAsync(DeviceLogin login, CancellationToken ct);

    /// <summary>Đổi refresh token lấy access token mới. Không refresh được thì ném ProviderException.</summary>
    Task<OAuthTokens> RefreshAsync(OAuthTokens tokens, CancellationToken ct);

    /// <summary>Nhập token từ CLI chính chủ; null nếu không tìm thấy.</summary>
    Task<OAuthTokens?> ImportAsync(CancellationToken ct) => Task.FromResult<OAuthTokens?>(null);
}
