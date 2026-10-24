namespace FacelessStudio.Core.Models;

/// <summary>Bảy bước của quy trình tạo video; mỗi bước chọn được provider riêng.</summary>
public enum PipelineStep
{
    Topic,
    Script,
    Voice,
    Media,
    Timing,
    Render,
    Thumbnail,
}

/// <summary>Nhóm provider hiển thị trên giao diện.</summary>
public enum ProviderCategory
{
    BuiltIn,
    FreeCloud,
    FreeSelfHosted,
    PaidCloud,
}

public enum VoiceGender
{
    Female,
    Male,
}

public static class StepInfo
{
    public static readonly PipelineStep[] All = Enum.GetValues<PipelineStep>();

    public static string Label(PipelineStep step) => step switch
    {
        PipelineStep.Topic => "Chọn chủ đề",
        PipelineStep.Script => "Kịch bản, tiêu đề, mô tả, tag",
        PipelineStep.Voice => "Giọng đọc",
        PipelineStep.Media => "Hình ảnh / footage",
        PipelineStep.Timing => "Phụ đề chạy từng chữ",
        PipelineStep.Render => "Dựng video",
        PipelineStep.Thumbnail => "Thumbnail",
        _ => step.ToString(),
    };

    public static string Label(ProviderCategory category) => category switch
    {
        ProviderCategory.BuiltIn => "Có sẵn",
        ProviderCategory.FreeCloud => "Miễn phí (cloud)",
        ProviderCategory.FreeSelfHosted => "Miễn phí (self-hosted)",
        ProviderCategory.PaidCloud => "Trả phí (cloud)",
        _ => category.ToString(),
    };
}

public sealed record LanguageOption(string Code, string Label, string PromptName, int WordsPerMinute);

public static class Languages
{
    public static readonly LanguageOption Vietnamese = new("vi", "Tiếng Việt", "Vietnamese", 170);
    public static readonly LanguageOption English = new("en", "English", "English", 150);
    public static readonly IReadOnlyList<LanguageOption> All = [Vietnamese, English];

    public static LanguageOption Get(string code) =>
        All.FirstOrDefault(l => l.Code == code) ?? throw new ArgumentException($"Ngôn ngữ không hỗ trợ: {code}");
}

public sealed record LengthPreset(string Code, string Label, int Seconds, bool Vertical)
{
    public int Width => Vertical ? 1080 : 1920;
    public int Height => Vertical ? 1920 : 1080;

    /// <summary>Shorts cắt cảnh nhanh (~6s), video dài ~15s một cảnh.</summary>
    public int SceneCount => Math.Clamp((int)Math.Round(Seconds / (Vertical ? 6.0 : 15.0)), 4, 40);
}

public static class LengthPresets
{
    public static readonly IReadOnlyList<LengthPreset> All =
    [
        new("short30", "Shorts 30 giây (dọc 9:16)", 30, true),
        new("short60", "Shorts 60 giây (dọc 9:16)", 60, true),
        new("long3", "Video 3 phút (ngang 16:9)", 180, false),
        new("long5", "Video 5 phút (ngang 16:9)", 300, false),
        new("long10", "Video 10 phút (ngang 16:9)", 600, false),
    ];

    public static LengthPreset Get(string code) =>
        All.FirstOrDefault(l => l.Code == code) ?? throw new ArgumentException($"Độ dài không hỗ trợ: {code}");
}
