using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Models;
using FacelessStudio.Core.Settings;

namespace FacelessStudio.Core.Providers;

/// <summary>Danh sách tất cả provider đã đăng ký, tra cứu theo bước và id.</summary>
public sealed class ProviderRegistry
{
    private readonly List<IStepProvider> _all;

    public ProviderRegistry(IEnumerable<IStepProvider> providers)
    {
        _all = providers.ToList();
        var dup = _all.GroupBy(p => p.Id).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) throw new InvalidOperationException($"Trùng id provider: {dup.Key}");
    }

    public IReadOnlyList<IStepProvider> All => _all;

    public IReadOnlyList<IStepProvider> For(PipelineStep step) =>
        _all.Where(p => p.Step == step).OrderBy(p => p.Category).ToList();

    public IStepProvider? Find(string id) => _all.FirstOrDefault(p => p.Id == id);

    public T Get<T>(PipelineStep step, string? id) where T : class, IStepProvider
    {
        var p = (id != null ? Find(id) : null) ?? throw new ProviderException(
            $"Chưa chọn provider cho bước \"{StepInfo.Label(step)}\".");
        if (p.Step != step || p is not T typed)
            throw new ProviderException($"Provider {p.Name} không dùng được cho bước \"{StepInfo.Label(step)}\".");
        return typed;
    }

    public bool Supports(IStepProvider p, string language) =>
        p.SupportedLanguages == null || p.SupportedLanguages.Contains(language);
}

public sealed record BudgetPreset(string Code, string Label, string Description, ProviderCategory Category);

public static class BudgetPresets
{
    public static readonly IReadOnlyList<BudgetPreset> All =
    [
        new("free-cloud", "Miễn phí (cloud)", "Dùng các dịch vụ cloud có gói miễn phí. Cần đăng ký key miễn phí.",
            ProviderCategory.FreeCloud),
        new("free-self-hosted", "Miễn phí (self-hosted)",
            "Chạy trên máy anh: Ollama, Piper, ComfyUI, Whisper.net, FFmpeg. Không tốn phí, cần máy đủ mạnh.",
            ProviderCategory.FreeSelfHosted),
        new("paid", "Trả phí (chất lượng cao)", "Claude, ElevenLabs, OpenAI, Shotstack. Tốt nhất, tính phí theo lượng dùng.",
            ProviderCategory.PaidCloud),
    ];

    /// <summary>Thứ tự ưu tiên riêng cho bước Timing: TTS có timing sẵn thì chính xác hơn Whisper.</summary>
    private static readonly Dictionary<ProviderCategory, string[]> TimingPreference = new()
    {
        [ProviderCategory.FreeCloud] = ["timing-tts"],
        [ProviderCategory.PaidCloud] = ["timing-tts"],
    };

    /// <summary>
    /// Chọn provider cho từng bước theo gói ngân sách: ưu tiên provider đúng loại đã cấu hình,
    /// không có thì lùi về provider có sẵn rồi tới provider self-hosted (vd. FFmpeg).
    /// </summary>
    public static Dictionary<PipelineStep, string> Resolve(BudgetPreset preset, ProviderRegistry registry,
        AppSettings settings, string language)
    {
        var result = new Dictionary<PipelineStep, string>();
        foreach (var step in StepInfo.All)
        {
            var usable = registry.For(step)
                .Where(p => p.IsConfigured(settings) && registry.Supports(p, language))
                .ToList();
            IStepProvider? pick = null;
            if (step == PipelineStep.Timing && TimingPreference.TryGetValue(preset.Category, out var ids))
                pick = ids.Select(id => usable.FirstOrDefault(p => p.Id == id)).FirstOrDefault(p => p != null);
            pick ??= usable.FirstOrDefault(p => p.Category == preset.Category)
                     ?? usable.FirstOrDefault(p => p.Category == ProviderCategory.BuiltIn)
                     ?? usable.FirstOrDefault(p => p.Category == ProviderCategory.FreeSelfHosted)
                     ?? usable.FirstOrDefault()
                     ?? registry.For(step).FirstOrDefault();
            if (pick != null) result[step] = pick.Id;
        }
        return result;
    }
}
