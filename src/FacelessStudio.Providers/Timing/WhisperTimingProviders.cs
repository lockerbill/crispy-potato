using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Models;
using FacelessStudio.Core.Settings;
using FacelessStudio.Core.Subtitles;
using FacelessStudio.Providers.Http;
using Whisper.net;
using Whisper.net.Ggml;

namespace FacelessStudio.Providers.Timing;

/// <summary>Whisper qua API kiểu OpenAI (Groq, OpenAI): nhận dạng toàn bộ giọng đọc 1 lần, lấy timing từng chữ.</summary>
public abstract class WhisperApiTimingProvider(IHttpClientFactory httpFactory) : ProviderBase, ITimingProvider
{
    public override PipelineStep Step => PipelineStep.Timing;
    protected abstract string Endpoint { get; }
    protected abstract string ApiKeySetting { get; }
    protected abstract string ModelSetting { get; }

    public async Task<IReadOnlyList<WordTiming>> AlignAsync(TimingRequest request, StepContext context)
    {
        // Nén thành mp3 mono 16 kHz cho nhẹ (10 phút ~ 2.5 MB, dưới giới hạn 25 MB).
        var mp3 = Path.Combine(context.WorkDir, "narration_16k.mp3");
        await context.Ffmpeg.RunAsync(["-i", request.NarrationWav, "-ac", "1", "-ar", "16000", "-b:a", "32k", mp3],
            ct: context.CancellationToken);

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(await File.ReadAllBytesAsync(mp3, context.CancellationToken));
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        form.Add(file, "file", "narration.mp3");
        form.Add(new StringContent(context.Settings.Get(ModelSetting)), "model");
        form.Add(new StringContent("verbose_json"), "response_format");
        form.Add(new StringContent("word"), "timestamp_granularities[]");
        form.Add(new StringContent(context.Request.Language), "language");

        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = form };
        req.Headers.Authorization = HttpHelpers.Bearer(context.Settings.Get(ApiKeySetting));
        var json = await HttpHelpers.SendJsonAsync(httpFactory.CreateClient("providers"), Name, req, context.CancellationToken);
        var words = (json["words"]?.AsArray() ?? [])
            .Select(w => new WordTiming(w!["word"]!.GetValue<string>().Trim(), w["start"]!.GetValue<double>(), w["end"]!.GetValue<double>()))
            .ToList();
        if (words.Count == 0) context.Log($"{Name} không trả về timing từng chữ, dùng ước lượng.");
        return WordTimingMath.AssembleFromRecognized(request.Scenes, request.Script, words);
    }
}

public sealed class GroqWhisperTimingProvider(IHttpClientFactory httpFactory) : WhisperApiTimingProvider(httpFactory)
{
    public override string Id => "timing-groq";
    public override string Name => "Groq Whisper";
    public override ProviderCategory Category => ProviderCategory.FreeCloud;
    public override string Description => "Whisper large-v3-turbo trên Groq, gói miễn phí. Nhận dạng lại giọng đọc để lấy timing từng chữ.";
    public override IReadOnlyList<string> RequiredSettings => [SettingKeys.GroqApiKey];
    protected override string Endpoint => "https://api.groq.com/openai/v1/audio/transcriptions";
    protected override string ApiKeySetting => SettingKeys.GroqApiKey;
    protected override string ModelSetting => SettingKeys.GroqWhisperModel;
}

public sealed class OpenAiWhisperTimingProvider(IHttpClientFactory httpFactory) : WhisperApiTimingProvider(httpFactory)
{
    public override string Id => "timing-openai";
    public override string Name => "OpenAI Whisper";
    public override ProviderCategory Category => ProviderCategory.PaidCloud;
    public override string Description => "Whisper của OpenAI, timing từng chữ ổn định.";
    public override string CostNote => "$0.006 / phút âm thanh";
    public override IReadOnlyList<string> RequiredSettings => [SettingKeys.OpenAiApiKey];
    protected override string Endpoint => "https://api.openai.com/v1/audio/transcriptions";
    protected override string ApiKeySetting => SettingKeys.OpenAiApiKey;
    protected override string ModelSetting => SettingKeys.OpenAiTranscribeModel;

    public override double EstimateCostUsd(VideoRequest request) => Math.Round(request.LengthPreset.Seconds / 60.0 * 0.006, 3);
}

/// <summary>Whisper.net (whisper.cpp) chạy ngay trong ứng dụng, không cần mạng sau khi tải model.</summary>
public sealed class WhisperNetTimingProvider(IHttpClientFactory httpFactory) : ProviderBase, ITimingProvider
{
    private static readonly SemaphoreSlim DownloadLock = new(1, 1);

    public override string Id => "timing-whisper-net";
    public override string Name => "Whisper.net (trên máy)";
    public override PipelineStep Step => PipelineStep.Timing;
    public override ProviderCategory Category => ProviderCategory.FreeSelfHosted;
    public override string Description =>
        "Nhận dạng giọng đọc offline bằng whisper.cpp để lấy timing từng chữ. Lần đầu tự tải model (base ~150 MB).";
    public override IReadOnlyList<string> RequiredSettings => [SettingKeys.WhisperModel];

    public async Task<IReadOnlyList<WordTiming>> AlignAsync(TimingRequest request, StepContext context)
    {
        var modelPath = await EnsureModelAsync(context);
        var wav16 = Path.Combine(context.WorkDir, "narration_16k.wav");
        await context.Ffmpeg.ToWavAsync(request.NarrationWav, wav16, 0, 16000, 1, context.CancellationToken);

        using var factory = WhisperFactory.FromPath(modelPath);
        await using var processor = factory.CreateBuilder()
            .WithLanguage(context.Request.Language)
            .WithTokenTimestamps()
            .SplitOnWord()
            .WithMaxSegmentLength(1)
            .Build();
        await using var stream = File.OpenRead(wav16);
        var words = new List<WordTiming>();
        await foreach (var seg in processor.ProcessAsync(stream, context.CancellationToken))
        {
            var text = seg.Text.Trim();
            if (text.Length > 0) words.Add(new WordTiming(text, seg.Start.TotalSeconds, seg.End.TotalSeconds));
        }
        return WordTimingMath.AssembleFromRecognized(request.Scenes, request.Script, words);
    }

    private async Task<string> EnsureModelAsync(StepContext context)
    {
        var name = context.Settings.Get(SettingKeys.WhisperModel).ToLowerInvariant();
        var type = name switch
        {
            "tiny" => GgmlType.Tiny,
            "small" => GgmlType.Small,
            "medium" => GgmlType.Medium,
            "large" or "large-v3" => GgmlType.LargeV3,
            "large-v3-turbo" or "turbo" => GgmlType.LargeV3Turbo,
            _ => GgmlType.Base,
        };
        var dir = Path.Combine(context.Settings.BaseDir, "data", "models");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"ggml-{type.ToString().ToLowerInvariant()}.bin");
        if (File.Exists(path)) return path;

        await DownloadLock.WaitAsync(context.CancellationToken);
        try
        {
            if (File.Exists(path)) return path;
            context.Log($"Đang tải model Whisper {type} (chỉ lần đầu)...");
            var downloader = new WhisperGgmlDownloader(httpFactory.CreateClient("providers"));
            await using var model = await downloader.GetGgmlModelAsync(type, QuantizationType.NoQuantization, context.CancellationToken);
            var tmp = path + ".part";
            await using (var fs = File.Create(tmp)) await model.CopyToAsync(fs, context.CancellationToken);
            File.Move(tmp, path, overwrite: true);
            return path;
        }
        finally
        {
            DownloadLock.Release();
        }
    }
}
