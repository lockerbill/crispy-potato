using System.Text.Json.Nodes;
using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Models;
using FacelessStudio.Core.Settings;
using FacelessStudio.Providers.Http;

namespace FacelessStudio.Providers.Tts;

/// <summary>ElevenLabs: giọng tự nhiên nhất, trả về timing theo ký tự (gom lại thành từng chữ).</summary>
public sealed class ElevenLabsTtsProvider(IHttpClientFactory httpFactory) : ProviderBase, ITtsProvider
{
    // Ước tính theo gói trả theo lượng dùng; giá thật xem https://elevenlabs.io/pricing
    private const double UsdPer1KChars = 0.18;

    public override string Id => "tts-elevenlabs";
    public override string Name => "ElevenLabs";
    public override PipelineStep Step => PipelineStep.Voice;
    public override ProviderCategory Category => ProviderCategory.PaidCloud;
    public override string Description =>
        "Giọng đọc tự nhiên nhất, có timing từng chữ. Model mặc định eleven_turbo_v2_5 hỗ trợ cả tiếng Việt.";
    public override string CostNote => "~$0.18 / 1.000 ký tự";
    public override IReadOnlyList<string> RequiredSettings => [SettingKeys.ElevenLabsApiKey];

    public override double EstimateCostUsd(VideoRequest request) =>
        Math.Round(request.TargetWords * 6 / 1000.0 * UsdPer1KChars, 3);

    public async Task<TtsResult> SynthesizeAsync(string text, string outputBase, StepContext context)
    {
        var s = context.Settings;
        var voice = context.Request.Voice == VoiceGender.Male
            ? s.Get(SettingKeys.ElevenLabsVoiceMale)
            : s.Get(SettingKeys.ElevenLabsVoiceFemale);
        var body = new JsonObject
        {
            ["text"] = text,
            ["model_id"] = s.Get(SettingKeys.ElevenLabsModel),
            ["language_code"] = context.Request.Language,
        };
        var json = await HttpHelpers.PostJsonAsync(httpFactory.CreateClient("providers"), "ElevenLabs",
            $"https://api.elevenlabs.io/v1/text-to-speech/{voice}/with-timestamps?output_format=mp3_44100_128",
            body, context.CancellationToken, ("xi-api-key", s.Get(SettingKeys.ElevenLabsApiKey)));

        var audio = json["audio_base64"]?.GetValue<string>() ?? throw new ProviderException("ElevenLabs không trả về âm thanh.");
        var mp3 = outputBase + ".mp3";
        await File.WriteAllBytesAsync(mp3, Convert.FromBase64String(audio), context.CancellationToken);
        return new TtsResult(mp3, WordsFromAlignment(json["alignment"] ?? json["normalized_alignment"]));
    }

    /// <summary>Gom timing theo ký tự thành timing theo chữ (ngăn bởi khoảng trắng).</summary>
    public static List<WordTiming>? WordsFromAlignment(JsonNode? alignment)
    {
        var chars = alignment?["characters"]?.AsArray();
        var starts = alignment?["character_start_times_seconds"]?.AsArray();
        var ends = alignment?["character_end_times_seconds"]?.AsArray();
        if (chars == null || starts == null || ends == null) return null;

        var words = new List<WordTiming>();
        var current = new System.Text.StringBuilder();
        double start = 0, end = 0;
        for (var i = 0; i < chars.Count; i++)
        {
            var c = chars[i]!.GetValue<string>();
            if (string.IsNullOrWhiteSpace(c))
            {
                Flush();
                continue;
            }
            if (current.Length == 0) start = starts[i]!.GetValue<double>();
            current.Append(c);
            end = ends[i]!.GetValue<double>();
        }
        Flush();
        return words;

        void Flush()
        {
            if (current.Length == 0) return;
            words.Add(new WordTiming(current.ToString(), start, end));
            current.Clear();
        }
    }
}
