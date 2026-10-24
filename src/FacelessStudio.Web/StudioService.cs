using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Jobs;
using FacelessStudio.Core.Media;
using FacelessStudio.Core.Models;
using FacelessStudio.Core.Providers;
using FacelessStudio.Core.Settings;

namespace FacelessStudio.Web;

/// <summary>Logic dùng chung cho các trang: provider khả dụng, kiểm tra dữ liệu, gợi ý chủ đề.</summary>
public sealed class StudioService(ProviderRegistry registry, SettingsStore settings, JobManager jobs)
{
    public ProviderRegistry Registry => registry;
    public AppSettings Settings => settings.Current;

    /// <summary>Lý do provider chưa dùng được (null nếu dùng được).</summary>
    public string? UnavailableReason(IStepProvider p, string language, AppSettings s)
    {
        if (!registry.Supports(p, language)) return "Không hỗ trợ ngôn ngữ này";
        if (p.IsConfigured(s)) return null;
        var missing = p.RequiredSettings.Where(k => !s.Has(k))
            .Select(k => SettingCatalog.Find(k)?.Label ?? k).ToList();
        return missing.Count > 0 ? "Cần cấu hình: " + string.Join(", ", missing) : "Chưa cấu hình đủ (xem Cài đặt)";
    }

    /// <summary>Request mặc định: lấy lựa chọn của video gần nhất, không có thì dùng gói miễn phí cloud.</summary>
    public VideoRequest DefaultRequest(string? fromJobId = null)
    {
        var source = fromJobId != null ? jobs.Get(fromJobId) : jobs.All.FirstOrDefault();
        if (source != null)
        {
            var copy = source.Request.Clone();
            if (fromJobId == null)
            {
                copy.Topic = "";
                copy.CustomScript = "";
            }
            return copy;
        }
        var request = new VideoRequest();
        request.Providers = BudgetPresets.Resolve(BudgetPresets.All[0], registry, Settings, request.Language);
        return request;
    }

    public List<string> Validate(VideoRequest r)
    {
        var errors = new List<string>();
        var s = Settings;
        var topicProvider = registry.Find(r.Providers.GetValueOrDefault(PipelineStep.Topic) ?? "");
        if (string.IsNullOrWhiteSpace(r.Topic) && (topicProvider == null || topicProvider.Category == ProviderCategory.BuiltIn))
            errors.Add("Nhập chủ đề, hoặc chọn một provider gợi ý chủ đề để AI tự chọn.");
        if (r.Providers.GetValueOrDefault(PipelineStep.Script) == "script-custom" && r.CustomParagraphs.Count == 0)
            errors.Add("Provider \"Tự viết kịch bản\" cần anh dán kịch bản vào ô Kịch bản tự viết.");
        foreach (var step in StepInfo.All)
        {
            var p = registry.Find(r.Providers.GetValueOrDefault(step) ?? "");
            if (p == null) errors.Add($"Chưa chọn provider cho bước \"{StepInfo.Label(step)}\".");
            else if (UnavailableReason(p, r.Language, s) is { } why)
                errors.Add($"{StepInfo.Label(step)} – {p.Name}: {why}.");
        }
        return errors;
    }

    public double EstimateCost(VideoRequest r) => jobs.EstimateCost(r);

    public async Task<IReadOnlyList<TopicIdea>> SuggestTopicsAsync(VideoRequest r, CancellationToken ct)
    {
        var provider = registry.Get<ITopicProvider>(PipelineStep.Topic, r.Providers.GetValueOrDefault(PipelineStep.Topic));
        var s = Settings;
        var context = new StepContext
        {
            Request = r,
            Settings = s,
            WorkDir = Path.GetTempPath(),
            Ffmpeg = FfmpegTool.FromSettings(s),
            CancellationToken = ct,
        };
        return await provider.SuggestAsync(new TopicRequest(r.Niche), context);
    }

    public VideoJob Start(VideoRequest r) => jobs.Enqueue(r.Clone());

    public static string Usd(double v) => v <= 0 ? "Miễn phí" : v < 0.01 ? "< $0.01" : $"~${v:0.00}";
}
