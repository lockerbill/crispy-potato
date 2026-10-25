using System.Security;
using Studio.Domain;
using Studio.Services;

namespace Studio.Providers;

public sealed class VoiceProvider(ProviderHttp http, ProcessRunner processes, MediaEngine media, AssetStore assets) : IStageProvider
{
    public bool Handles(string id) => id is "azure-speech" or "piper" or "elevenlabs";
    public Task Validate(ProviderConfig c, Stage stage, CancellationToken ct)
    {
        if (c.Id == "piper" && (string.IsNullOrWhiteSpace(c.Executable) || !File.Exists(c.ModelPath))) throw new InvalidOperationException("Cấu hình Piper executable và model tiếng Việt.");
        if (c.Id == "azure-speech" && !System.Text.RegularExpressions.Regex.IsMatch(c.Region, "^[a-z0-9-]{2,40}$")) throw new InvalidOperationException("Thiếu Azure Speech region.");
        return Task.CompletedTask;
    }
    public async Task<string> CheckConnection(ProviderConfig c, string? key, CancellationToken ct)
    {
        await Validate(c, Stage.Voice, ct);
        if (c.Id == "piper") { await processes.Run(c.Executable, ["--help"], ct); return "Piper CLI và model tồn tại; cần synthesis để xác minh voice."; }
        using var req = c.Id == "azure-speech"
            ? ProviderHttp.Request(HttpMethod.Get, $"https://{c.Region}.tts.speech.microsoft.com/cognitiveservices/voices/list")
            : ProviderHttp.Request(HttpMethod.Get, "https://api.elevenlabs.io/v1/voices");
        req.Headers.Add(c.Id == "azure-speech" ? "Ocp-Apim-Subscription-Key" : "xi-api-key", key);
        await http.Json(req, ct); return "Đọc danh sách voice thành công. Kiểm tra SKU/quota trong tài khoản provider.";
    }
    public async Task<ProviderResult> Execute(ProviderContext c, CancellationToken ct)
    {
        var p = c.Project;
        if (p.Scenes.Count == 0) throw new InvalidOperationException("Chưa có kịch bản.");
        for (var i = 0; i < p.Scenes.Count; i++)
        {
            var scene = p.Scenes[i]; var raw = Path.Combine(c.WorkDirectory, $"voice-{i}.wav");
            var voice = p.Steps[c.Stage].Voice;
            if (string.IsNullOrWhiteSpace(voice)) voice = c.Config.Voice;
            if (c.Config.Id == "piper")
            {
                var model = p.Language == "en" ? c.Config.EnglishModelPath : c.Config.ModelPath;
                if (!File.Exists(model)) throw new InvalidOperationException("Thiếu Piper model cho ngôn ngữ video.");
                await processes.Run(c.Config.Executable, ["--model", model, "--output_file", raw], ct, scene.Narration);
            }
            else if (c.Config.Id == "azure-speech")
            {
                if (string.IsNullOrWhiteSpace(voice)) voice = p.Language == "vi" ? "vi-VN-HoaiMyNeural" : "en-US-JennyNeural";
                var locale = p.Language == "vi" ? "vi-VN" : "en-US";
                using var req = ProviderHttp.Request(HttpMethod.Post, $"https://{c.Config.Region}.tts.speech.microsoft.com/cognitiveservices/v1");
                req.Headers.Add("Ocp-Apim-Subscription-Key", c.Secret); req.Headers.Add("X-Microsoft-OutputFormat", "riff-24khz-16bit-mono-pcm");
                req.Content = new StringContent($"<speak version='1.0' xml:lang='{locale}'><voice name='{SecurityElement.Escape(voice)}'>{SecurityElement.Escape(scene.Narration)}</voice></speak>", System.Text.Encoding.UTF8, "application/ssml+xml");
                using var response = await http.Send(req, ct, true); await ProviderHttp.SaveBody(response, raw, ct);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(voice)) throw new InvalidOperationException("Cần ElevenLabs voice ID.");
                using var req = ProviderHttp.Request(HttpMethod.Post, $"https://api.elevenlabs.io/v1/text-to-speech/{Uri.EscapeDataString(voice)}?output_format=mp3_44100_128", new { text = scene.Narration, model_id = c.Model, language_code = p.Language });
                req.Headers.Add("xi-api-key", c.Secret);
                using var response = await http.Send(req, ct, true); await ProviderHttp.SaveBody(response, raw, ct);
            }
            var wav = await media.NormalizeAudio(raw, Path.Combine(c.WorkDirectory, $"audio-{i}.wav"), ct);
            scene.AudioSeconds = await media.Duration(wav, ct);
            scene.DurationSeconds = scene.AudioSeconds; // Actual narration becomes the initial scene duration; editing can add silence.
            scene.AudioAssetId = (await assets.Add(p.Id, wav, $"scene-{i + 1}.wav", "audio/wav", ct)).Id;
        }
        return new(Json.Write(new { seconds = p.ActualSeconds, scenes = p.Scenes.Count, overLimit = p.ActualSeconds > p.MaxSeconds }), p);
    }
}
