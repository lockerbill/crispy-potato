using System.Text.Json;

namespace FacelessStudio.Providers.Auth;

/// <summary>Lưu token đăng nhập vào &lt;thư mục làm việc&gt;/data/oauth/&lt;id&gt;.json trên máy.</summary>
public sealed class OAuthTokenStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly Lock _lock = new();

    public static string PathFor(string baseDir, string id) => Path.Combine(baseDir, "data", "oauth", id + ".json");

    public bool Exists(string baseDir, string id) => Load(baseDir, id) != null;

    public OAuthTokens? Load(string baseDir, string id)
    {
        var file = PathFor(baseDir, id);
        lock (_lock)
        {
            if (!File.Exists(file)) return null;
            try
            {
                var tokens = JsonSerializer.Deserialize<OAuthTokens>(File.ReadAllText(file));
                return string.IsNullOrWhiteSpace(tokens?.AccessToken) ? null : tokens;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    public void Save(string baseDir, string id, OAuthTokens tokens)
    {
        var file = PathFor(baseDir, id);
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(tokens, JsonOptions));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(tmp, file, overwrite: true);
        }
    }

    public void Delete(string baseDir, string id)
    {
        lock (_lock) File.Delete(PathFor(baseDir, id));
    }
}
