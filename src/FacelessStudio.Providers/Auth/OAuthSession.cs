using System.Collections.Concurrent;
using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Settings;

namespace FacelessStudio.Providers.Auth;

/// <summary>Lấy token còn hạn cho client gọi API, tự refresh và lưu lại token mới.</summary>
public sealed class OAuthSession(OAuthTokenStore store, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public bool IsLoggedIn(IOAuthAccount account, AppSettings settings) => store.Exists(settings.BaseDir, account.Id);

    public async Task<OAuthTokens> GetTokensAsync(IOAuthAccount account, AppSettings settings, CancellationToken ct,
        bool forceRefresh = false)
    {
        // Refresh token thường chỉ dùng được một lần nên không để hai job refresh cùng lúc.
        var gate = _locks.GetOrAdd(settings.BaseDir + "|" + account.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var tokens = store.Load(settings.BaseDir, account.Id)
                         ?? throw new ProviderException($"Chưa đăng nhập {account.Name}. Vào Cài đặt → Tài khoản đăng nhập.");
            var expiring = tokens.ExpiresAt is { } exp && exp - _time.GetUtcNow() < account.RefreshSkew(tokens);
            if (!forceRefresh && !expiring) return tokens;
            if (string.IsNullOrEmpty(tokens.RefreshToken))
                throw new ProviderException($"Phiên đăng nhập {account.Name} đã hết hạn. Vào Cài đặt để đăng nhập lại.");

            var refreshed = await account.RefreshAsync(tokens, ct);
            refreshed = refreshed with
            {
                RefreshToken = string.IsNullOrEmpty(refreshed.RefreshToken) ? tokens.RefreshToken : refreshed.RefreshToken,
                AccountLabel = refreshed.AccountLabel ?? tokens.AccountLabel,
                Extra = Merge(tokens.Extra, refreshed.Extra),
            };
            store.Save(settings.BaseDir, account.Id, refreshed);
            return refreshed;
        }
        finally
        {
            gate.Release();
        }
    }

    private static Dictionary<string, string>? Merge(Dictionary<string, string>? old, Dictionary<string, string>? fresh)
    {
        if (old == null) return fresh;
        var merged = new Dictionary<string, string>(old);
        foreach (var (k, v) in fresh ?? []) merged[k] = v;
        return merged;
    }
}
