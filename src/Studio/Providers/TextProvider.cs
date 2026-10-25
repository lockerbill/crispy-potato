using System.Text.Json.Nodes;
using Studio.Domain;
using Studio.Services;

namespace Studio.Providers;

public static class TextPrompts
{
    public static string Build(Project p, Stage stage)
    {
        var language = p.Language == "vi" ? "Vietnamese" : "English";
        var shape = stage switch
        {
            Stage.Topic => "{\"topic\":\"one specific compelling topic\"}",
            Stage.Script => "{\"scenes\":[{\"narration\":\"spoken words\",\"visualPrompt\":\"English visual description for stock search or image generation\",\"durationSeconds\":6}]}",
            Stage.Title => "{\"title\":\"YouTube title, at most 100 characters\"}",
            Stage.Description => "{\"description\":\"YouTube description, at most 5000 characters\"}",
            Stage.Tags => "{\"tags\":[\"relevant tag\"]}", _ => throw new InvalidOperationException("Not a text step.")
        };
        return $"You are a YouTube producer. Return ONLY valid JSON in this shape: {shape}. " +
               $"Write in {language}, except visualPrompt must be English. Audience: {p.Audience}. Style: {p.Style}. " +
               $"Format: {p.Format}; duration target: {p.TargetSeconds} seconds. Brief: {p.Brief}. Topic: {p.Topic}. " +
               (stage == Stage.Script ? "Write the FULL narration, no placeholders. Plan scenes of 6–15 seconds. Speak about 2.2 words/second. Avoid unsupported factual claims. " : "") +
               (stage is Stage.Title or Stage.Description or Stage.Tags ? "Script: " + string.Join(" ", p.Scenes.Select(s => s.Narration)) : "");
    }
}
public sealed class TextProvider(ProviderHttp http) : IStageProvider
{
    public bool Handles(string id) => id is "gemini" or "ollama" or "openai-text";
    public Task Validate(ProviderConfig c, Stage s, CancellationToken ct)
    {
        if (c.Id == "ollama") _ = ProviderHttp.LocalUrl(c, "/api/tags");
        return Task.CompletedTask;
    }
    public async Task<string> CheckConnection(ProviderConfig c, string? key, CancellationToken ct)
    {
        using var request = c.Id switch
        {
            "ollama" => ProviderHttp.Request(HttpMethod.Get, ProviderHttp.LocalUrl(c, "/api/tags")),
            "gemini" => ProviderHttp.Request(HttpMethod.Get, "https://generativelanguage.googleapis.com/v1beta/models"),
            _ => ProviderHttp.Request(HttpMethod.Get, "https://api.openai.com/v1/models", bearer: key)
        };
        if (c.Id == "gemini") request.Headers.Add("x-goog-api-key", key);
        await http.Json(request, ct); return "Kết nối thành công; quota và quyền chạy từng model vẫn cần xác minh.";
    }
    public async Task<ProviderResult> Execute(ProviderContext c, CancellationToken ct)
    {
        var prompt = TextPrompts.Build(c.Project, c.Stage);
        var limit = BudgetService.OutputTokenLimit(c.Project, c.Stage);
        string raw;
        if (c.Config.Id == "gemini")
        {
            using var req = ProviderHttp.Request(HttpMethod.Post, $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(c.Model)}:generateContent",
                new { contents = new[] { new { parts = new[] { new { text = prompt } } } }, generationConfig = new { responseMimeType = "application/json", maxOutputTokens = limit } });
            req.Headers.Add("x-goog-api-key", c.Secret);
            var data = await http.Json(req, ct, true);
            raw = data["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.GetValue<string>() ?? throw new ProviderException("Gemini không trả nội dung (có thể do giới hạn hoặc bộ lọc).", true);
        }
        else if (c.Config.Id == "ollama")
        {
            using var req = ProviderHttp.Request(HttpMethod.Post, ProviderHttp.LocalUrl(c.Config, "/api/generate"), new { model = c.Model, prompt, stream = false, format = "json", think = false, options = new { num_predict = limit } });
            raw = ProviderHttp.Required(await http.Json(req, ct), "response");
        }
        else
        {
            using var req = ProviderHttp.Request(HttpMethod.Post, "https://api.openai.com/v1/chat/completions",
                new { model = c.Model, messages = new[] { new { role = "user", content = prompt } }, response_format = new { type = "json_object" }, max_completion_tokens = limit }, c.Secret);
            var data = await http.Json(req, ct, true);
            raw = data["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? throw new ProviderException("OpenAI không trả nội dung.", true);
        }
        Apply(c.Project, c.Stage, raw);
        return new(raw, c.Project);
    }
    public static void Apply(Project p, Stage stage, string raw)
    {
        JsonNode d;
        try { d = JsonNode.Parse(raw) ?? throw new InvalidDataException(); }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidDataException) { throw new ProviderException("Nội dung AI không đúng JSON yêu cầu; kết quả không được duyệt.", true); }
        string Text(string key, int max) { var s = ProviderHttp.Required(d, key).Trim(); if (s.Length == 0 || s.Length > max) throw new ProviderException("Nội dung AI vượt giới hạn hoặc rỗng.", true); return s; }
        switch (stage)
        {
            case Stage.Topic: p.Topic = Text("topic", 1000); break;
            case Stage.Title: p.Title = Text("title", 100); break;
            case Stage.Description: p.Description = Text("description", 5000); break;
            case Stage.Tags:
                p.Tags = (d["tags"]?.AsArray() ?? throw new ProviderException("Thiếu tags.", true)).Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).Distinct().ToList();
                if (string.Join(',', p.Tags).Length > 450) throw new ProviderException("Tags vượt 450 ký tự.", true);
                break;
            case Stage.Script:
                var scenes = d["scenes"]?.AsArray() ?? throw new ProviderException("Thiếu scenes.", true);
                if (scenes.Count is < 1 or > 400) throw new ProviderException("Số cảnh phải từ 1–400.", true);
                p.Scenes = scenes.Select(s => new Scene { Narration = ProviderHttp.Required(s, "narration"), VisualPrompt = ProviderHttp.Required(s, "visualPrompt"), DurationSeconds = s?["durationSeconds"]?.GetValue<double>() ?? 8 }).ToList();
                if (p.Scenes.Any(s => string.IsNullOrWhiteSpace(s.Narration) || s.DurationSeconds <= 0 || !double.IsFinite(s.DurationSeconds) || s.Narration.Length > 5000)) throw new ProviderException("Cảnh không hợp lệ.", true);
                p.Captions.Clear(); p.VideoAssetId = null;
                break;
        }
    }
}
