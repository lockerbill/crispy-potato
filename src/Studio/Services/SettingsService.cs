using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Studio.Data;
using Studio.Domain;
using Studio.Providers;

namespace Studio.Services;

public sealed class SettingsService(IDbContextFactory<StudioDb> dbFactory, IDataProtectionProvider protection)
{
    private readonly IDataProtector protector = protection.CreateProtector("Studio.Secrets.v1");
    public async Task<ProviderConfig> GetProvider(string id)
    {
        var def = ProviderCatalog.Get(id);
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.Settings.FindAsync("provider:" + id);
        var cfg = row is null ? new ProviderConfig { Id = id, Model = def.Model } : Json.Read<ProviderConfig>(row.Json);
        cfg.Configured = !def.NeedsKey || row?.ProtectedSecret is not null;
        return cfg;
    }
    public async Task<string?> Secret(string key)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.Settings.FindAsync(key);
        return row?.ProtectedSecret is null ? null : protector.Unprotect(row.ProtectedSecret);
    }
    public async Task SaveProvider(ProviderConfig cfg, string? newKey = null, bool clearSecret = false)
    {
        var def = ProviderCatalog.Get(cfg.Id);
        if (cfg.UsdPerUnit < 0 || cfg.OutputUsdPerUnit < 0) throw new InvalidOperationException("Giá không được âm.");
        if (def.Tier == ProviderTier.SelfHosted && cfg.Endpoint.Length > 0)
        {
            if (!Uri.TryCreate(cfg.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
                throw new InvalidOperationException("Endpoint phải là HTTP(S), không chứa credentials.");
        }
        await Save("provider:" + cfg.Id, cfg, newKey, clearSecret);
    }
    public async Task<T?> Get<T>(string key)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.Settings.FindAsync(key);
        return row is null ? default : Json.Read<T>(row.Json);
    }
    public async Task Save<T>(string key, T data, string? secret = null, bool clearSecret = false)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.Settings.FindAsync(key);
        if (row is null) { row = new SettingRow { Key = key }; db.Settings.Add(row); }
        row.Json = Json.Write(data);
        if (clearSecret) row.ProtectedSecret = null;
        else if (!string.IsNullOrWhiteSpace(secret)) row.ProtectedSecret = protector.Protect(secret.Trim());
        await db.SaveChangesAsync();
    }
}
