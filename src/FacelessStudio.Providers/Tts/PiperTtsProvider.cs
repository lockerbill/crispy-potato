using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Media;
using FacelessStudio.Core.Models;
using FacelessStudio.Core.Settings;

namespace FacelessStudio.Providers.Tts;

/// <summary>Piper TTS chạy trên máy (offline). Giọng đọc là file .onnx tải từ huggingface rhasspy/piper-voices.</summary>
public sealed class PiperTtsProvider : ProviderBase, ITtsProvider
{
    public override string Id => "tts-piper";
    public override string Name => "Piper (trên máy)";
    public override PipelineStep Step => PipelineStep.Voice;
    public override ProviderCategory Category => ProviderCategory.FreeSelfHosted;
    public override string Description =>
        "TTS offline, nhanh, chạy được trên CPU. Có giọng tiếng Việt (vais1000) và tiếng Anh. Không có timing từng chữ " +
        "(nên chọn Whisper ở bước phụ đề).";
    public override IReadOnlyList<string> RequiredSettings => [SettingKeys.PiperPath];

    public static string VoiceKey(string language) => language == "vi" ? SettingKeys.PiperVoiceVi : SettingKeys.PiperVoiceEn;

    public override bool IsConfigured(AppSettings settings) =>
        settings.Has(SettingKeys.PiperPath) &&
        (File.Exists(settings.ResolvePath(SettingKeys.PiperVoiceVi)) || File.Exists(settings.ResolvePath(SettingKeys.PiperVoiceEn)));

    public async Task<TtsResult> SynthesizeAsync(string text, string outputBase, StepContext context)
    {
        var s = context.Settings;
        var model = s.ResolvePath(VoiceKey(context.Request.Language));
        if (!File.Exists(model))
            throw new ProviderException($"Không thấy file giọng Piper: {model}. Chạy scripts/setup-windows.ps1 " +
                                        "hoặc tải .onnx + .onnx.json từ https://huggingface.co/rhasspy/piper-voices");
        var wav = outputBase + ".wav";
        // Piper không có sẵn giọng nam/nữ cho mọi ngôn ngữ; với model nhiều giọng thì chọn speaker 0/1.
        var args = new List<string> { "--model", model, "--output_file", wav };
        if (context.Request.Voice == VoiceGender.Male && File.ReadAllText(model + ".json").Contains("\"num_speakers\": 2"))
            args.AddRange(["--speaker", "1"]);

        (int Code, string Out, string Err) result;
        try
        {
            result = await FfmpegTool.ExecAsync(s.ResolvePath(SettingKeys.PiperPath), args, null,
                context.CancellationToken, stdin: text);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new ProviderException("Không chạy được piper. Kiểm tra đường dẫn PIPER_PATH trong Cài đặt.", e);
        }
        if (result.Code != 0 || !File.Exists(wav))
            throw new ProviderException($"Piper lỗi: {result.Err.Trim()}");
        return new TtsResult(wav, null);
    }
}
