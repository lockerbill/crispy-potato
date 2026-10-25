using System.Text;
using System.Text.Json.Nodes;

namespace FacelessStudio.Providers.Auth;

/// <summary>Đọc phần payload của JWT (không kiểm chữ ký, chỉ để lấy exp / email / account id).</summary>
public static class JwtHelper
{
    public static JsonObject? Payload(string? jwt)
    {
        var parts = jwt?.Split('.');
        if (parts is not { Length: >= 2 }) return null;
        try
        {
            var b64 = parts[1].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            return JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64))) as JsonObject;
        }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public static DateTimeOffset? Expiry(string? jwt) =>
        Payload(jwt)?["exp"] is JsonValue v && v.TryGetValue<long>(out var exp) ? DateTimeOffset.FromUnixTimeSeconds(exp) : null;

    public static string? Email(string? jwt) =>
        Payload(jwt)?["email"] is JsonValue v && v.TryGetValue<string>(out var email) ? email : null;
}
