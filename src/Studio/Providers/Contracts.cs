using Studio.Domain;
using Studio.Services;

namespace Studio.Providers;

public record ProviderResult(string Output, Project Project);
public sealed class ProviderContext
{
    public required Guid JobId { get; init; }
    public required Project Project { get; init; }
    public required Stage Stage { get; init; }
    public required ProviderConfig Config { get; init; }
    public required string WorkDirectory { get; init; }
    public string? Secret { get; init; }
    public string? Checkpoint { get; set; }
    public required Func<string, Task> SaveCheckpoint { get; init; }
    public string Model => string.IsNullOrWhiteSpace(Project.Steps[Stage].Model) ? Config.Model : Project.Steps[Stage].Model;
}
public interface IStageProvider
{
    bool Handles(string id);
    Task Validate(ProviderConfig config, Stage stage, CancellationToken ct);
    Task<string> CheckConnection(ProviderConfig config, string? secret, CancellationToken ct);
    Task<ProviderResult> Execute(ProviderContext context, CancellationToken ct);
    bool CanResume(string checkpoint) => false;
    Task Cancel(ProviderContext context, CancellationToken ct) => Task.CompletedTask;
}
public sealed class ProviderRegistry(IEnumerable<IStageProvider> providers)
{
    public IStageProvider Get(string id) => providers.SingleOrDefault(p => p.Handles(id)) ?? throw new InvalidOperationException("Provider adapter chưa được đăng ký.");
}
public sealed class ProviderException(string message, bool mayHaveCharged = false) : Exception(message)
{
    public bool MayHaveCharged { get; } = mayHaveCharged;
}
