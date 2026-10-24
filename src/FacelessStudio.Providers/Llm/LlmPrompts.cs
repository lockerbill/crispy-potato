using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Models;

namespace FacelessStudio.Providers.Llm;

/// <summary>Prompt và JSON schema dùng chung cho mọi LLM (Gemini, Groq, Ollama, Claude).</summary>
public static class LlmPrompts
{
    public const string ScriptSystem = """
        You are a scriptwriter for faceless YouTube channels. Videos are narrated by a text-to-speech voice
        over stock footage or AI images, so everything the viewer needs is in the narration.

        Write scripts that hold attention: open with a hook in the first sentence, deliver concrete and accurate
        information, vary sentence length so the voice sounds natural, and end with a short call to action.
        Write numbers the way they should be spoken. Never put stage directions, emojis, hashtags or speaker
        labels inside narration. Do not invent statistics or quotes; if you are unsure of a fact, leave it out.

        For every scene also write:
        - footage_query: 2-4 concrete English keywords to find matching stock video (e.g. "ocean waves aerial").
        - image_prompt: one English sentence describing a photorealistic image for the scene, no text in the image.
        """;

    public const string TopicSystem = """
        You plan content for faceless YouTube channels (narration over stock footage or AI images).
        Suggest topics that are specific, curiosity-driven and easy to illustrate with generic visuals.
        Avoid topics that need a real person on camera, breaking news you cannot verify, or medical/financial advice.
        """;

    public static readonly JsonObject ScriptSchema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "title": { "type": "string" },
            "description": { "type": "string" },
            "tags": { "type": "array", "items": { "type": "string" } },
            "thumbnail_text": { "type": "string" },
            "scenes": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "narration": { "type": "string" },
                  "footage_query": { "type": "string" },
                  "image_prompt": { "type": "string" }
                },
                "required": ["narration", "footage_query", "image_prompt"],
                "additionalProperties": false
              }
            }
          },
          "required": ["title", "description", "tags", "thumbnail_text", "scenes"],
          "additionalProperties": false
        }
        """)!.AsObject();

    public static readonly JsonObject TopicSchema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "ideas": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "title": { "type": "string" },
                  "angle": { "type": "string" }
                },
                "required": ["title", "angle"],
                "additionalProperties": false
              }
            }
          },
          "required": ["ideas"],
          "additionalProperties": false
        }
        """)!.AsObject();

    public static string ScriptUser(VideoRequest r)
    {
        var lang = r.LanguageOption;
        var length = r.LengthPreset;
        var lines = new List<string>
        {
            $"Topic: {r.Topic}",
            $"Language for title, description, tags, thumbnail_text and narration: {lang.PromptName}",
        };
        var paragraphs = r.CustomParagraphs;
        if (paragraphs.Count > 0)
        {
            lines.Add($"The channel owner already wrote the narration. Return exactly {paragraphs.Count} scenes in the " +
                      "same order, copy each paragraph below into narration unchanged, and write the other fields.");
            lines.AddRange(paragraphs.Select((p, i) => $"\n[Scene {i + 1}]\n{p}"));
        }
        else
        {
            var format = length.Vertical ? "YouTube Shorts (vertical, fast-paced)" : "a standard horizontal YouTube video";
            lines.Add($"Format: {format}, about {length.Seconds} seconds long.");
            lines.Add($"Total narration: about {r.TargetWords} words, split into about {length.SceneCount} scenes.");
        }
        lines.Add("Title under 90 characters. Description 2-4 short paragraphs. 8-15 tags. thumbnail_text 2-5 punchy words.");
        if (!string.IsNullOrWhiteSpace(r.Tone)) lines.Add($"Tone/style: {r.Tone}");
        if (!string.IsNullOrWhiteSpace(r.Notes)) lines.Add($"Extra requirements from the channel owner:\n{r.Notes}");
        return string.Join('\n', lines);
    }

    public static string TopicUser(VideoRequest r, TopicRequest t, IReadOnlyList<string> trends)
    {
        var lines = new List<string>
        {
            $"Suggest {t.Count} video topics in {r.LanguageOption.PromptName} for " +
            (r.LengthPreset.Vertical ? "YouTube Shorts." : "standard YouTube videos."),
            "title: the video idea as a short phrase; angle: one sentence on what makes it interesting.",
        };
        if (!string.IsNullOrWhiteSpace(t.Seed)) lines.Add($"Channel niche / keywords: {t.Seed}");
        if (!string.IsNullOrWhiteSpace(r.Tone)) lines.Add($"Channel tone: {r.Tone}");
        if (trends.Count > 0)
            lines.Add("Trending searches right now (use them only if they fit the niche):\n- " + string.Join("\n- ", trends));
        return string.Join('\n', lines);
    }

    /// <summary>Dành cho model không hỗ trợ JSON schema: mô tả schema ngay trong prompt.</summary>
    public static string WithSchemaInstruction(string user, JsonObject schema) =>
        $"{user}\n\nRespond with only a JSON object (no markdown) that matches this JSON schema:\n{schema.ToJsonString()}";

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Lấy phần JSON trong câu trả lời (bỏ ```json, chữ thừa trước/sau).</summary>
    public static string ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw new ProviderException("Model không trả về JSON hợp lệ, hãy thử lại.");
        return text[start..(end + 1)];
    }

    public static VideoScript ParseScript(string text)
    {
        ScriptDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ScriptDto>(ExtractJson(text), ReadOptions);
        }
        catch (JsonException e)
        {
            throw new ProviderException("Kịch bản trả về sai định dạng JSON, hãy thử lại.", e);
        }
        if (dto?.Scenes is not { Count: > 0 }) throw new ProviderException("Kịch bản trả về không có cảnh nào, hãy thử lại.");
        return new VideoScript
        {
            Title = dto.Title?.Trim() ?? "",
            Description = dto.Description?.Trim() ?? "",
            Tags = dto.Tags?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList() ?? [],
            ThumbnailText = dto.ThumbnailText?.Trim() ?? "",
            Scenes = dto.Scenes
                .Where(s => !string.IsNullOrWhiteSpace(s.Narration))
                .Select(s => new Scene(s.Narration!.Trim(), s.FootageQuery?.Trim() ?? "", s.ImagePrompt?.Trim() ?? ""))
                .ToList(),
        };
    }

    public static IReadOnlyList<TopicIdea> ParseTopics(string text)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<TopicsDto>(ExtractJson(text), ReadOptions);
            return dto?.Ideas?.Where(i => !string.IsNullOrWhiteSpace(i.Title))
                .Select(i => new TopicIdea(i.Title!.Trim(), i.Angle?.Trim() ?? "")).ToList() ?? [];
        }
        catch (JsonException e)
        {
            throw new ProviderException("Gợi ý chủ đề trả về sai định dạng JSON, hãy thử lại.", e);
        }
    }

    private sealed class ScriptDto
    {
        public string? Title { get; set; }
        public string? Description { get; set; }
        public List<string>? Tags { get; set; }
        [JsonPropertyName("thumbnail_text")] public string? ThumbnailText { get; set; }
        public List<SceneDto>? Scenes { get; set; }
    }

    private sealed class SceneDto
    {
        public string? Narration { get; set; }
        [JsonPropertyName("footage_query")] public string? FootageQuery { get; set; }
        [JsonPropertyName("image_prompt")] public string? ImagePrompt { get; set; }
    }

    private sealed class TopicsDto
    {
        public List<TopicDto>? Ideas { get; set; }
    }

    private sealed class TopicDto
    {
        public string? Title { get; set; }
        public string? Angle { get; set; }
    }
}
