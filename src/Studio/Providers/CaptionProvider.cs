using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Studio.Domain;
using Studio.Services;

namespace Studio.Providers;

public sealed class CaptionProvider(ProviderHttp http, ProcessRunner processes, AssetStore assets) : IStageProvider
{
    public bool Handles(string id) => id is "groq" or "openai-whisper" or "whisper";
    public Task Validate(ProviderConfig c, Stage stage, CancellationToken ct)
    {
        if (c.Id == "whisper" && (string.IsNullOrWhiteSpace(c.Executable) || !File.Exists(c.ModelPath))) throw new InvalidOperationException("Cấu hình whisper-cli và model đa ngôn ngữ.");
        return Task.CompletedTask;
    }
    public async Task<string> CheckConnection(ProviderConfig c, string? key, CancellationToken ct)
    {
        await Validate(c, Stage.Captions, ct);
        if (c.Id == "whisper") { await processes.Run(c.Executable, ["--help"], ct); return "whisper-cli và model tồn tại; kiểm tra word timestamps khi chạy."; }
        using var req = ProviderHttp.Request(HttpMethod.Get, c.Id == "groq" ? "https://api.groq.com/openai/v1/models" : "https://api.openai.com/v1/models", bearer: key);
        await http.Json(req, ct); return "Kết nối thành công; chưa sử dụng audio quota.";
    }
    public async Task<ProviderResult> Execute(ProviderContext c, CancellationToken ct)
    {
        var words = new List<WordCue>(); double offset = 0;
        for (var i = 0; i < c.Project.Scenes.Count; i++)
        {
            var scene = c.Project.Scenes[i];
            var audio = await assets.Get(scene.AudioAssetId ?? throw new InvalidOperationException("Cần giọng đọc trước."), c.Project.Id);
            List<WordCue> sceneWords;
            if (c.Config.Id == "whisper")
            {
                var prefix = Path.Combine(c.WorkDirectory, $"transcript-{i}");
                await processes.Run(c.Config.Executable, ["-m", c.Config.ModelPath, "-f", audio.Path, "-l", c.Project.Language, "-ojf", "-of", prefix, "-ml", "1", "-sow"], ct);
                var data = JsonNode.Parse(await File.ReadAllTextAsync(prefix + ".json", ct))!;
                sceneWords = ParseWhisper(data);
            }
            else
            {
                if (audio.Bytes > 24_000_000) throw new InvalidOperationException("Audio cảnh vượt giới hạn upload; chia cảnh nhỏ hơn.");
                using var req = ProviderHttp.Request(HttpMethod.Post, c.Config.Id == "groq" ? "https://api.groq.com/openai/v1/audio/transcriptions" : "https://api.openai.com/v1/audio/transcriptions", bearer: c.Secret);
                var form = new MultipartFormDataContent();
                var content = new StreamContent(File.OpenRead(audio.Path)); content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                form.Add(content, "file", "audio.wav"); form.Add(new StringContent(c.Model), "model"); form.Add(new StringContent(c.Project.Language), "language");
                form.Add(new StringContent("verbose_json"), "response_format"); form.Add(new StringContent("word"), "timestamp_granularities[]"); req.Content = form;
                var data = await http.Json(req, ct, true);
                sceneWords = (data["words"]?.AsArray() ?? throw new ProviderException("Provider không trả word timestamps.", true)).Select(w => new WordCue(ProviderHttp.Required(w, "word"), w!["start"]!.GetValue<double>(), w["end"]!.GetValue<double>())).ToList();
            }
            CaptionFiles.Validate(sceneWords, scene.AudioSeconds);
            words.AddRange(sceneWords.Select(w => w with { Start = w.Start + offset, End = w.End + offset }));
            offset += Math.Max(scene.AudioSeconds, scene.DurationSeconds);
        }
        CaptionFiles.Validate(words, c.Project.ActualSeconds); c.Project.Captions = words;
        return new(Json.Write(words), c.Project);
    }
    public static List<WordCue> ParseWhisper(JsonNode data)
    {
        var words = new List<WordCue>();
        // -ml 1 -sow emits word-level segments with measured offsets; never distribute words evenly.
        foreach (var segment in data["transcription"]?.AsArray() ?? throw new ProviderException("whisper.cpp thiếu transcription."))
        {
            var text = segment?["text"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(text) || text.StartsWith('[')) continue;
            var start = segment?["offsets"]?["from"]?.GetValue<double>() / 1000;
            var end = segment?["offsets"]?["to"]?.GetValue<double>() / 1000;
            if (start is null || end is null || end <= start) throw new ProviderException("whisper.cpp chưa trả word offsets hợp lệ. Dùng build hỗ trợ -ml 1 -sow -ojf.");
            words.Add(new(text, start.Value, end.Value));
        }
        return words;
    }
}
