using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Models;

namespace FacelessStudio.Providers;

/// <summary>Khai báo thông tin hiển thị chung để các provider chỉ cần viết phần xử lý.</summary>
public abstract class ProviderBase : IStepProvider
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract PipelineStep Step { get; }
    public abstract ProviderCategory Category { get; }
    public abstract string Description { get; }
    public virtual string CostNote => Category == ProviderCategory.PaidCloud ? "Trả phí theo lượng dùng" : "Miễn phí";
    public virtual IReadOnlyList<string> RequiredSettings => [];
    public virtual IReadOnlyCollection<string>? SupportedLanguages => null;
    public virtual double EstimateCostUsd(VideoRequest request) => 0;
}
