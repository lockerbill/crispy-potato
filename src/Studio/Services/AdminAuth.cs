using System.Security.Cryptography;
using System.Text;

namespace Studio.Services;

public record AdminCredential(string Salt, string Hash);
public sealed class AdminAuth(SettingsService settings, IConfiguration config)
{
    public async Task Initialize(bool reset = false)
    {
        var password = config["STUDIO_ADMIN_PASSWORD"];
        if (config["STUDIO_ADMIN_PASSWORD_FILE"] is { Length: > 0 } file) password = (await File.ReadAllTextAsync(file)).TrimEnd('\r', '\n');
        if (await settings.Get<AdminCredential>("admin") is not null && !reset) return;
        if (string.IsNullOrEmpty(password)) return;
        if (password.Length < 12) throw new InvalidOperationException("STUDIO_ADMIN_PASSWORD phải có ít nhất 12 ký tự.");
        var salt = RandomNumberGenerator.GetBytes(32);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210000, HashAlgorithmName.SHA256, 32);
        await settings.Save("admin", new AdminCredential(Convert.ToBase64String(salt), Convert.ToBase64String(hash)));
    }
    public async Task<bool> Verify(string password)
    {
        var record = await settings.Get<AdminCredential>("admin");
        if (record is null || password.Length > 1024) return false;
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(record.Salt), 210000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(record.Hash));
    }
}
