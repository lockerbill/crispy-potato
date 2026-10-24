using FacelessStudio.Core.Media;
using FacelessStudio.Core.Models;
using FacelessStudio.Core.Settings;

namespace FacelessStudio.Core.Abstractions;

/// <summary>Thông tin chung của mọi provider, dùng để hiển thị và lọc trên giao diện.</summary>
public interface IStepProvider
{
    string Id { get; }
    string Name { get; }
    PipelineStep Step { get; }
    ProviderCategory Category { get; }
    string Description { get; }

    /// <summary>Ghi chú chi phí ngắn, ví dụ "Miễn phí" hoặc "~$0.30 / 1.000 ký tự".</summary>
    string CostNote { get; }

    /// <summary>Các khóa cấu hình bắt buộc (xem SettingCatalog).</summary>
    IReadOnlyList<string> RequiredSettings { get; }

    /// <summary>Ngôn ngữ hỗ trợ; null nghĩa là tất cả.</summary>
    IReadOnlyCollection<string>? SupportedLanguages => null;

    bool IsConfigured(AppSettings settings) => RequiredSettings.All(settings.Has);

    /// <summary>Ước tính chi phí (USD) của bước này cho một video.</summary>
    double EstimateCostUsd(VideoRequest request) => 0;
}

/// <summary>Ngữ cảnh truyền cho provider khi chạy một bước.</summary>
public sealed class StepContext
{
    public required VideoRequest Request { get; init; }
    public required AppSettings Settings { get; init; }
    public required string WorkDir { get; init; }
    public required FfmpegTool Ffmpeg { get; init; }
    public Action<string> Log { get; init; } = _ => { };
    public CancellationToken CancellationToken { get; init; }

    /// <summary>Trạng thái dùng chung trong một job (vd. danh sách clip đã dùng để không lặp).</summary>
    public Dictionary<string, object> Items { get; } = new();

    public T GetItem<T>(string key) where T : new()
    {
        if (Items.TryGetValue(key, out var v) && v is T t) return t;
        var created = new T();
        Items[key] = created;
        return created;
    }
}

public sealed record TopicRequest(string Seed, int Count = 8);

public interface ITopicProvider : IStepProvider
{
    Task<IReadOnlyList<TopicIdea>> SuggestAsync(TopicRequest request, StepContext context);
}

public interface IScriptProvider : IStepProvider
{
    /// <summary>Viết kịch bản. Nếu Request.CustomParagraphs có nội dung thì chỉ viết metadata và giữ nguyên lời.</summary>
    Task<VideoScript> WriteAsync(StepContext context);
}

public interface ITtsProvider : IStepProvider
{
    /// <summary>Tạo âm thanh. outputBase là đường dẫn không có đuôi file; provider tự thêm đuôi.</summary>
    Task<TtsResult> SynthesizeAsync(string text, string outputBase, StepContext context);
}

public sealed record MediaRequest(Scene Scene, int SceneIndex, int Count, double Duration, int Width, int Height)
{
    public bool Vertical => Height > Width;
}

public interface IMediaProvider : IStepProvider
{
    /// <summary>Trả về tối đa Count media cho cảnh. Danh sách rỗng thì pipeline dùng nền màu.</summary>
    Task<IReadOnlyList<MediaAsset>> GetAsync(MediaRequest request, StepContext context);
}

public sealed record TimingRequest(IReadOnlyList<SceneAudio> Scenes, IReadOnlyList<Scene> Script, string NarrationWav);

public interface ITimingProvider : IStepProvider
{
    /// <summary>Trả về thời điểm của từng chữ trong kịch bản, tính theo toàn bộ video.</summary>
    Task<IReadOnlyList<WordTiming>> AlignAsync(TimingRequest request, StepContext context);
}

public interface IRenderProvider : IStepProvider
{
    Task RenderAsync(RenderPlan plan, StepContext context);
}

public sealed record ThumbnailRequest(VideoScript Script, string? FramePath, string OutputPath, int Width = 1280, int Height = 720);

public interface IThumbnailProvider : IStepProvider
{
    Task CreateAsync(ThumbnailRequest request, StepContext context);
}

/// <summary>Lỗi có thông báo thân thiện để hiện lên giao diện.</summary>
public sealed class ProviderException(string message, Exception? inner = null) : Exception(message, inner);
