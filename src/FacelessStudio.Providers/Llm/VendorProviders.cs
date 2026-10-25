using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace FacelessStudio.Providers.Llm;

/// <summary>
/// Thông tin hiển thị của một nhà cung cấp LLM. Từ đây sinh ra cặp provider "script-{Key}" và "topic-{Key}"
/// nên thêm vendor mới chỉ cần một client và một dòng AddLlmVendor.
/// </summary>
public sealed record LlmVendorInfo(
    string Key,
    string Name,
    ProviderCategory Category,
    string Description,
    string? CostNote = null,
    string ScriptEffort = "medium",
    bool TopicWebSearch = false,
    bool TopicUsesTrends = true,
    string? TopicName = null,
    string? TopicDescription = null);

public sealed class VendorScriptProvider(LlmVendorInfo info, ILlmClient llm) : LlmScriptProvider(llm)
{
    public LlmVendorInfo Info => info;
    public override string Id => $"script-{info.Key}";
    public override string Name => info.Name;
    public override ProviderCategory Category => info.Category;
    public override string Description => info.Description;
    public override string CostNote => info.CostNote ?? base.CostNote;
    protected override string Effort => info.ScriptEffort;
}

public sealed class VendorTopicProvider(LlmVendorInfo info, ILlmClient llm, GoogleTrendsClient? trends)
    : LlmTopicProvider(llm, trends)
{
    public LlmVendorInfo Info => info;
    public override string Id => $"topic-{info.Key}";

    public override string Name => info.TopicName
                                   ?? (info.TopicWebSearch ? $"{info.Name} + Web search"
                                       : info.TopicUsesTrends ? $"Google Trends + {info.Name}" : info.Name);

    public override ProviderCategory Category => info.Category;

    public override string Description => info.TopicDescription
                                          ?? (info.TopicWebSearch
                                              ? $"{info.Name} tự tìm kiếm web để nắm xu hướng mới nhất rồi đề xuất chủ đề."
                                              : $"Lấy xu hướng tìm kiếm từ Google Trends rồi để {info.Name} chọn chủ đề hợp kênh.");

    public override string CostNote => info.CostNote ?? base.CostNote;
    protected override bool WebSearch => info.TopicWebSearch;
}

public static class LlmVendorRegistration
{
    /// <summary>Đăng ký provider kịch bản + gợi ý chủ đề cho một vendor. Client phải stateless (mỗi provider một bản).</summary>
    public static IServiceCollection AddLlmVendor(this IServiceCollection services, LlmVendorInfo info,
        Func<IServiceProvider, ILlmClient> client)
    {
        services.AddSingleton<IStepProvider>(sp => new VendorTopicProvider(info, client(sp),
            info.TopicUsesTrends && !info.TopicWebSearch ? sp.GetRequiredService<GoogleTrendsClient>() : null));
        services.AddSingleton<IStepProvider>(sp => new VendorScriptProvider(info, client(sp)));
        return services;
    }
}
