using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Models;
using FacelessStudio.Core.Subtitles;

namespace FacelessStudio.Providers.BuiltIn;

/// <summary>Chủ đề do người dùng tự nhập; không gợi ý gì thêm.</summary>
public sealed class ManualTopicProvider : ProviderBase, ITopicProvider
{
    public override string Id => "topic-manual";
    public override string Name => "Tự nhập chủ đề";
    public override PipelineStep Step => PipelineStep.Topic;
    public override ProviderCategory Category => ProviderCategory.BuiltIn;
    public override string Description => "Anh tự nhập chủ đề, không dùng AI gợi ý.";

    public Task<IReadOnlyList<TopicIdea>> SuggestAsync(TopicRequest request, StepContext context) =>
        throw new ProviderException("Chưa nhập chủ đề. Hãy nhập chủ đề hoặc chọn một provider gợi ý chủ đề.");
}

/// <summary>Dùng nguyên kịch bản người dùng dán vào, mỗi đoạn một cảnh.</summary>
public sealed class CustomScriptProvider : ProviderBase, IScriptProvider
{
    public override string Id => "script-custom";
    public override string Name => "Tự viết kịch bản";
    public override PipelineStep Step => PipelineStep.Script;
    public override ProviderCategory Category => ProviderCategory.BuiltIn;
    public override string Description =>
        "Dùng kịch bản anh dán vào (mỗi đoạn là một cảnh). Tiêu đề lấy từ chủ đề, footage tìm theo chủ đề.";

    public Task<VideoScript> WriteAsync(StepContext context)
    {
        var r = context.Request;
        if (r.CustomParagraphs.Count == 0)
            throw new ProviderException("Provider \"Tự viết kịch bản\" cần anh dán kịch bản vào ô Kịch bản tự nhập.");
        return Task.FromResult(new VideoScript
        {
            Title = r.Topic,
            Description = r.Topic,
            ThumbnailText = r.Topic,
            Scenes = r.CustomParagraphs.Select(p => new Scene(p, r.Topic, r.Topic)).ToList(),
        });
    }
}

/// <summary>Nền màu chuyển động, không cần mạng hay key.</summary>
public sealed class GradientMediaProvider : ProviderBase, IMediaProvider
{
    public override string Id => "media-gradient";
    public override string Name => "Nền màu chuyển động";
    public override PipelineStep Step => PipelineStep.Media;
    public override ProviderCategory Category => ProviderCategory.BuiltIn;
    public override string Description => "Nền gradient chuyển động, chữ phụ đề là trọng tâm. Không cần mạng.";

    public Task<IReadOnlyList<MediaAsset>> GetAsync(MediaRequest request, StepContext context) =>
        Task.FromResult<IReadOnlyList<MediaAsset>>([new MediaAsset(MediaKind.Gradient, null, null)]);
}

/// <summary>Dùng timing từng chữ mà giọng đọc trả về (Edge TTS, ElevenLabs); không có thì ước lượng.</summary>
public sealed class TtsTimingProvider : ProviderBase, ITimingProvider
{
    public override string Id => "timing-tts";
    public override string Name => "Lấy từ giọng đọc";
    public override PipelineStep Step => PipelineStep.Timing;
    public override ProviderCategory Category => ProviderCategory.BuiltIn;
    public override string Description =>
        "Chính xác nhất khi giọng đọc có trả timing (Edge TTS, ElevenLabs). Giọng khác thì ước lượng theo độ dài chữ.";

    public Task<IReadOnlyList<WordTiming>> AlignAsync(TimingRequest request, StepContext context)
    {
        var all = new List<WordTiming>();
        var estimated = 0;
        foreach (var scene in request.Scenes)
        {
            var words = ScriptText.Words(request.Script[scene.Index].Narration);
            List<WordTiming> timings;
            if (scene.TtsWords is { Count: > 0 } tts)
            {
                timings = WordTimingMath.MapOnto(words, tts, 0, scene.SpeechDuration);
            }
            else
            {
                estimated++;
                timings = WordTimingMath.Estimate(words, 0, scene.SpeechDuration);
            }
            all.AddRange(WordTimingMath.Shift(timings, scene.Start));
        }
        if (estimated > 0)
            context.Log($"Giọng đọc không có timing cho {estimated} cảnh, đã ước lượng theo độ dài chữ.");
        return Task.FromResult<IReadOnlyList<WordTiming>>(all);
    }
}
