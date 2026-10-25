using System.Collections.Concurrent;

namespace FacelessStudio.Providers.Auth;

public enum LoginStatus
{
    Idle,
    Starting,
    Waiting,
    Success,
    Error,
}

public sealed record LoginState(LoginStatus Status, string? VerificationUrl = null, string? UserCode = null,
    string? Message = null, DateTimeOffset? ExpiresAt = null);

/// <summary>Chạy luồng đăng nhập device code ở nền để trang Cài đặt chỉ việc hiển thị trạng thái.</summary>
public sealed class OAuthLoginService(IEnumerable<IOAuthAccount> accounts, OAuthTokenStore store, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, LoginState> _states = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();

    public IReadOnlyList<IOAuthAccount> Accounts { get; } = accounts.ToList();

    public event Action? Changed;

    public LoginState State(string id) => _states.GetValueOrDefault(id, new LoginState(LoginStatus.Idle));

    public OAuthTokens? Tokens(string baseDir, string id) => store.Load(baseDir, id);

    /// <summary>Bắt đầu đăng nhập; trả về Task của vòng chờ (giao diện không cần await).</summary>
    public Task Start(string id, string baseDir)
    {
        var account = Find(id);
        Cancel(id);
        var cts = new CancellationTokenSource();
        _running[id] = cts;
        Set(id, new LoginState(LoginStatus.Starting, Message: "Đang xin mã đăng nhập..."));
        return Task.Run(() => RunAsync(account, baseDir, cts));
    }

    public void Cancel(string id)
    {
        if (_running.TryRemove(id, out var cts))
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Vòng chờ vừa kết thúc.
            }
        }
        if (State(id).Status is LoginStatus.Starting or LoginStatus.Waiting) Set(id, new LoginState(LoginStatus.Idle));
    }

    public void Logout(string id, string baseDir)
    {
        Cancel(id);
        store.Delete(baseDir, id);
        Set(id, new LoginState(LoginStatus.Idle, Message: "Đã đăng xuất."));
    }

    public async Task ImportAsync(string id, string baseDir)
    {
        var account = Find(id);
        try
        {
            var tokens = await account.ImportAsync(CancellationToken.None);
            if (tokens == null)
            {
                Set(id, new LoginState(LoginStatus.Error, Message: "Không tìm thấy thông tin đăng nhập của CLI trên máy này."));
                return;
            }
            store.Save(baseDir, id, tokens);
            Set(id, new LoginState(LoginStatus.Success, Message: "Đã nhập đăng nhập từ CLI" + Label(tokens)));
        }
        catch (Exception e)
        {
            Set(id, new LoginState(LoginStatus.Error, Message: e.Message));
        }
    }

    private async Task RunAsync(IOAuthAccount account, string baseDir, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            var login = await account.StartAsync(ct);
            Set(account.Id, new LoginState(LoginStatus.Waiting, login.VerificationUrl, login.UserCode,
                "Mở link, đăng nhập rồi nhập mã bên dưới. Trang này tự cập nhật khi xong.", login.ExpiresAt));
            while (_time.GetUtcNow() < login.ExpiresAt)
            {
                await Task.Delay(login.Interval, _time, ct);
                var tokens = await account.PollAsync(login, ct);
                if (tokens == null) continue;
                store.Save(baseDir, account.Id, tokens);
                Set(account.Id, new LoginState(LoginStatus.Success, Message: "Đăng nhập thành công" + Label(tokens)));
                return;
            }
            Set(account.Id, new LoginState(LoginStatus.Error, Message: "Hết thời gian chờ đăng nhập. Bấm Đăng nhập để thử lại."));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Người dùng huỷ hoặc bắt đầu lại.
        }
        catch (Exception e)
        {
            Set(account.Id, new LoginState(LoginStatus.Error, Message: e.Message));
        }
        finally
        {
            _running.TryRemove(new KeyValuePair<string, CancellationTokenSource>(account.Id, cts));
            cts.Dispose();
        }
    }

    private IOAuthAccount Find(string id) =>
        Accounts.FirstOrDefault(a => a.Id == id) ?? throw new ArgumentException($"Không có tài khoản OAuth \"{id}\".");

    private static string Label(OAuthTokens t) => string.IsNullOrEmpty(t.AccountLabel) ? "." : $": {t.AccountLabel}.";

    private void Set(string id, LoginState state)
    {
        _states[id] = state;
        Changed?.Invoke();
    }
}
