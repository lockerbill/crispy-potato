using System.Text.Json;
using System.Text.Json.Serialization;

namespace Studio.Domain;

public enum Stage { Topic, Script, Title, Description, Tags, Voice, Media, Captions, Thumbnail, Render }
public enum StepStatus { Pending, Queued, Running, AwaitingReview, Approved, Stale, Failed, NeedsAttention, Cancelled }
public enum ProviderTier { FreeCloud, SelfHosted, Paid, Sandbox }
public enum RunMode { Review, Automatic }
public enum VideoFormat { Short, Long }
public enum JobStatus { Queued, Running, Completed, Failed, NeedsAttention, Cancelled }
public enum ChargeStatus { Reserved, Estimated, Confirmed, Uncertain, Released }

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options) ?? throw new InvalidDataException("Empty data.");
    public static T Clone<T>(T value) => Read<T>(Write(value));
}

public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Video mới";
    public string Language { get; set; } = "vi";
    public VideoFormat Format { get; set; } = VideoFormat.Short;
    public int TargetSeconds { get; set; } = 60;
    public RunMode Mode { get; set; } = RunMode.Review;
    public string Brief { get; set; } = "";
    public string Audience { get; set; } = "Người xem phổ thông";
    public string Style { get; set; } = "Tự nhiên, cuốn hút, rõ ràng";
    public string Currency { get; set; } = "USD";
    public decimal Budget { get; set; } = 5;
    public decimal VndPerUsd { get; set; } = 25000;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public int Revision { get; set; }
    public string Topic { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public List<Scene> Scenes { get; set; } = [];
    public List<WordCue> Captions { get; set; } = [];
    public string CaptionStyle { get; set; } = "karaoke";
    public Dictionary<Stage, Step> Steps { get; set; } = [];
    public string? VideoAssetId { get; set; }
    public string? ThumbnailAssetId { get; set; }
    public string? YoutubeVideoId { get; set; }
    [JsonIgnore] public decimal BudgetUsd => Currency == "VND" ? Budget / VndPerUsd : Budget;
    [JsonIgnore] public double ActualSeconds => Scenes.Sum(s => Math.Max(s.AudioSeconds, s.DurationSeconds));
    [JsonIgnore] public int MaxSeconds => Format == VideoFormat.Short ? 180 : 1800;
    [JsonIgnore] public int Width => Format == VideoFormat.Short ? 1080 : 1920;
    [JsonIgnore] public int Height => Format == VideoFormat.Short ? 1920 : 1080;
}

public class Step
{
    public Stage Stage { get; set; }
    public string ProviderId { get; set; } = "";
    public string Model { get; set; } = "";
    public string Voice { get; set; } = "";
    public StepStatus Status { get; set; }
    public string Output { get; set; } = "";
    public string? Error { get; set; }
    public int Version { get; set; }
}

public class Scene
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Narration { get; set; } = "";
    public string VisualPrompt { get; set; } = "";
    public double DurationSeconds { get; set; } = 6;
    public double AudioSeconds { get; set; }
    public string Transition { get; set; } = "cut";
    public string? AudioAssetId { get; set; }
    public string? MediaAssetId { get; set; }
    public string? Attribution { get; set; }
}

public record WordCue(string Text, double Start, double End);
public record ProviderDefinition(string Id, string Name, ProviderTier Tier, Stage[] Stages, string Model, string Unit, string Notice, bool NeedsKey = true);

public class ProviderConfig
{
    public string Id { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string Model { get; set; } = "";
    public string Voice { get; set; } = "";
    public string Region { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string Executable { get; set; } = "";
    public string ModelPath { get; set; } = "";
    public string EnglishModelPath { get; set; } = "";
    public decimal? UsdPerUnit { get; set; }
    public decimal? OutputUsdPerUnit { get; set; }
    public DateTimeOffset? PriceAsOf { get; set; }
    public string PriceVersion { get; set; } = "";
    public bool Configured { get; set; }
    public bool FreePlanConfirmed { get; set; }
    public string? LastCheck { get; set; }
    public DateTimeOffset? CheckedAt { get; set; }
}

public static class Pipeline
{
    public static readonly Stage[] Order = Enum.GetValues<Stage>();
    public static Stage[] Dependencies(Stage stage) => stage switch
    {
        Stage.Topic => [], Stage.Script => [Stage.Topic],
        Stage.Title or Stage.Description or Stage.Tags or Stage.Voice or Stage.Media => [Stage.Script],
        Stage.Captions => [Stage.Voice], Stage.Thumbnail => [Stage.Title],
        Stage.Render => [Stage.Voice, Stage.Media, Stage.Captions], _ => []
    };
    public static bool DependsOn(Stage stage, Stage changed) => Dependencies(stage).Any(d => d == changed || DependsOn(d, changed));
    public static bool Usable(Step s) => s.Status == StepStatus.Approved;
    public static void Validate(Project p)
    {
        if (string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 160) throw new InvalidOperationException("Tên dự án cần có 1–160 ký tự.");
        if (p.Language is not ("vi" or "en")) throw new InvalidOperationException("Unsupported language.");
        if (p.TargetSeconds < (p.Format == VideoFormat.Short ? 15 : 60) || p.TargetSeconds > p.MaxSeconds) throw new InvalidOperationException("Thời lượng ngoài giới hạn định dạng.");
        if (p.Currency is not ("USD" or "VND") || p.Budget < 0 || p.VndPerUsd <= 0) throw new InvalidOperationException("Ngân sách hoặc tỷ giá không hợp lệ.");
        if (p.Brief.Length > 12000 || p.Audience.Length > 1000 || p.Style.Length > 1000) throw new InvalidOperationException("Brief quá dài.");
    }
    public static void Invalidate(Project p, Stage changed)
    {
        foreach (var stage in Order.Where(s => DependsOn(s, changed)))
            if (p.Steps[stage].Status != StepStatus.Pending) p.Steps[stage].Status = StepStatus.Stale;
        if (changed is Stage.Script or Stage.Voice or Stage.Media or Stage.Captions) p.VideoAssetId = null;
        if (changed is Stage.Topic or Stage.Script or Stage.Title) p.ThumbnailAssetId = null;
    }
}
