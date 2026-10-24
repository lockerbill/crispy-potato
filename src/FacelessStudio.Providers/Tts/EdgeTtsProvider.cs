using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Media;
using FacelessStudio.Core.Models;

namespace FacelessStudio.Providers.Tts;

public sealed class EdgeTtsProvider : ProviderBase, ITtsProvider
{
    private readonly EdgeTtsClient _client = new();

    public override string Id => "tts-edge";
    public override string Name => "Microsoft Edge TTS";
    public override PipelineStep Step => PipelineStep.Voice;
    public override ProviderCategory Category => ProviderCategory.FreeCloud;
    public override string Description =>
        "Giọng neural của Microsoft (HoaiMy, NamMinh, Aria, Guy). Miễn phí, không cần key, có timing từng chữ. " +
        "Dịch vụ không chính thức nên có thể thay đổi bất cứ lúc nào.";

    public static string VoiceFor(string language, VoiceGender gender) => (language, gender) switch
    {
        ("vi", VoiceGender.Male) => "vi-VN-NamMinhNeural",
        ("vi", _) => "vi-VN-HoaiMyNeural",
        (_, VoiceGender.Male) => "en-US-GuyNeural",
        _ => "en-US-AriaNeural",
    };

    public async Task<TtsResult> SynthesizeAsync(string text, string outputBase, StepContext context)
    {
        var voice = VoiceFor(context.Request.Language, context.Request.Voice);
        var mp3 = outputBase + ".mp3";
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var result = await _client.SynthesizeAsync(text, voice, "+0%", context.CancellationToken);
                await File.WriteAllBytesAsync(mp3, result.Audio, context.CancellationToken);
                return new TtsResult(mp3, result.Words);
            }
            catch (Exception e) when (e is not OperationCanceledException || !context.CancellationToken.IsCancellationRequested)
            {
                lastError = e;
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), context.CancellationToken);
            }
        }

        // Dự phòng: dùng CLI edge-tts (Python) nếu máy có cài.
        try
        {
            var (code, _, err) = await FfmpegTool.ExecAsync("edge-tts",
                ["--voice", voice, "--text", text, "--write-media", mp3], null, context.CancellationToken);
            if (code == 0 && File.Exists(mp3))
            {
                context.Log("Edge TTS: đã dùng CLI edge-tts dự phòng (không có timing từng chữ).");
                return new TtsResult(mp3, null);
            }
            context.Log($"CLI edge-tts lỗi: {err.Trim()}");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Máy không cài edge-tts.
        }
        throw new ProviderException($"Edge TTS lỗi: {lastError?.Message}", lastError);
    }
}
