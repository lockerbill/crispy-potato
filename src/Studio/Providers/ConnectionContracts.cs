using Studio.Domain;

namespace Studio.Providers;

public record ProviderModel(string Id, string Name);
public record ProviderConnectionStatus(bool Connected, string Message, string? Account = null);
public record OAuthStartResult(string FlowId, string? AuthorizationUrl, string? VerificationUrl, string? UserCode, DateTimeOffset ExpiresAt, int PollIntervalSeconds = 5);
public interface IProviderModelSource
{
    Task<IReadOnlyList<ProviderModel>> GetModels(ProviderConfig config, string? secret, CancellationToken ct);
}
public interface ISubscriptionAuth
{
    string ChatGptRedirectUri { get; }
    Task<ProviderConnectionStatus> Status(string providerId, CancellationToken ct);
    Task<OAuthStartResult> Begin(string providerId, string binding, CancellationToken ct);
    Task<ProviderConnectionStatus> Poll(string providerId, string flowId, string binding, CancellationToken ct);
    Task CancelPending(string providerId, string flowId, string binding, CancellationToken ct);
    Task CompleteChatGpt(string state, string code, string issuedClientId, string binding, CancellationToken ct);
    Task Disconnect(string providerId, CancellationToken ct);
    Task CancelPending(string providerId, string flowId, string binding, CancellationToken ct);
    Task<string> AccessToken(string providerId, bool forceRefresh, CancellationToken ct);
}
