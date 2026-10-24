namespace FacelessStudio.Core.Models;

/// <summary>Tham số người dùng nhập trên giao diện cho một video.</summary>
public sealed class VideoRequest
{
    public string Topic { get; set; } = "";
    public string Language { get; set; } = "vi";
    public string Length { get; set; } = "short60";
    public VoiceGender Voice { get; set; } = VoiceGender.Female;
    public string Tone { get; set; } = "";
    public string Notes { get; set; } = "";

    /// <summary>Kịch bản tự viết: mỗi đoạn (ngăn bởi dòng trống) là một cảnh. Để trống thì AI viết.</summary>
    public string CustomScript { get; set; } = "";

    /// <summary>Provider được chọn cho từng bước (id provider).</summary>
    public Dictionary<PipelineStep, string> Providers { get; set; } = new();

    public LanguageOption LanguageOption => Languages.Get(Language);
    public LengthPreset LengthPreset => LengthPresets.Get(Length);

    public int TargetWords => (int)Math.Round(LengthPreset.Seconds * LanguageOption.WordsPerMinute / 60.0);

    public IReadOnlyList<string> CustomParagraphs => ScriptText.SplitParagraphs(CustomScript);
}

public sealed record TopicIdea(string Title, string Angle);

public sealed record Scene(string Narration, string FootageQuery, string ImagePrompt);

public sealed class VideoScript
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public string ThumbnailText { get; set; } = "";
    public List<Scene> Scenes { get; set; } = [];
}

/// <summary>Thời điểm (giây) một chữ được đọc, tính từ đầu đoạn âm thanh tương ứng.</summary>
public sealed record WordTiming(string Word, double Start, double End);

public sealed record TtsResult(string AudioPath, IReadOnlyList<WordTiming>? Words);

public enum MediaKind
{
    Video,
    Image,
    Gradient,
}

/// <summary>Một clip hoặc ảnh cho cảnh. PublicUrl dùng cho renderer cloud (Shotstack).</summary>
public sealed record MediaAsset(MediaKind Kind, string? LocalPath, string? PublicUrl, string Credit = "");

public sealed class SceneAudio
{
    public required int Index { get; init; }
    public required string WavPath { get; init; }

    /// <summary>Vị trí bắt đầu của cảnh trong toàn bộ giọng đọc.</summary>
    public required double Start { get; init; }

    /// <summary>Thời lượng cảnh, đã gồm khoảng lặng cuối.</summary>
    public required double Duration { get; init; }

    /// <summary>Thời lượng phần có tiếng nói (không gồm khoảng lặng thêm vào).</summary>
    public required double SpeechDuration { get; init; }

    public IReadOnlyList<WordTiming>? TtsWords { get; init; }
}

public static class ScriptText
{
    public static IReadOnlyList<string> SplitParagraphs(string text) =>
        System.Text.RegularExpressions.Regex.Split(text ?? "", @"\r?\n\s*\r?\n")
            .Select(p => System.Text.RegularExpressions.Regex.Replace(p, @"\s+", " ").Trim())
            .Where(p => p.Length > 0)
            .ToList();

    public static string[] Words(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}
