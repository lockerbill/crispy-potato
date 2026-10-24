using System.Text.Json.Nodes;
using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Settings;
using FacelessStudio.Providers.Http;

namespace FacelessStudio.Providers.Llm;

/// <summary>Yêu cầu gửi tới LLM: luôn mong nhận về JSON theo schema.</summary>
public sealed record LlmRequest(string System, string User, JsonObject Schema, string Effort = "medium", bool WebSearch = false);

public interface ILlmClient
{
    string Service { get; }
    IReadOnlyList<string> RequiredSettings { get; }
    Task<string> GenerateJsonAsync(LlmRequest request, StepContext context);
}

/// <summary>Google Gemini API (free tier qua Google AI Studio).</summary>
public sealed class GeminiClient(IHttpClientFactory httpFactory) : ILlmClient
{
    public string Service => "Gemini";
    public IReadOnlyList<string> RequiredSettings => [SettingKeys.GeminiApiKey];

    public async Task<string> GenerateJsonAsync(LlmRequest request, StepContext context)
    {
        var s = context.Settings;
        var model = s.Get(SettingKeys.GeminiModel);
        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = request.System }) },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = request.User }),
            }),
            ["generationConfig"] = new JsonObject
            {
                ["responseMimeType"] = "application/json",
                ["responseJsonSchema"] = request.Schema.DeepClone(),
            },
        };
        var json = await HttpHelpers.PostJsonAsync(httpFactory.CreateClient("providers"), Service,
            $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent", body,
            context.CancellationToken, ("x-goog-api-key", s.Get(SettingKeys.GeminiApiKey)));
        var candidate = json["candidates"]?[0];
        var finish = candidate?["finishReason"]?.GetValue<string>();
        if (finish is "SAFETY" or "PROHIBITED_CONTENT" or "BLOCKLIST")
            throw new ProviderException("Gemini từ chối nội dung này (bộ lọc an toàn). Hãy đổi chủ đề.");
        var text = string.Concat(candidate?["content"]?["parts"]?.AsArray()
            .Select(p => p?["text"]?.GetValue<string>() ?? "") ?? []);
        if (text.Length == 0) throw new ProviderException($"Gemini không trả về nội dung (finishReason: {finish}).");
        return text;
    }
}

/// <summary>Groq (API tương thích OpenAI), có free tier.</summary>
public sealed class GroqClient(IHttpClientFactory httpFactory) : ILlmClient
{
    public string Service => "Groq";
    public IReadOnlyList<string> RequiredSettings => [SettingKeys.GroqApiKey];

    public async Task<string> GenerateJsonAsync(LlmRequest request, StepContext context)
    {
        var s = context.Settings;
        var body = new JsonObject
        {
            ["model"] = s.Get(SettingKeys.GroqModel),
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = request.System },
                new JsonObject { ["role"] = "user", ["content"] = LlmPrompts.WithSchemaInstruction(request.User, request.Schema) }),
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
            ["max_completion_tokens"] = 16000,
        };
        var json = await HttpHelpers.PostJsonAsync(httpFactory.CreateClient("providers"), Service,
            "https://api.groq.com/openai/v1/chat/completions", body, context.CancellationToken,
            ("Authorization", "Bearer " + s.Get(SettingKeys.GroqApiKey)));
        return json["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
               ?? throw new ProviderException("Groq không trả về nội dung.");
    }
}

/// <summary>Ollama chạy trên máy (self-hosted). Dùng "format" = JSON schema để ép đúng định dạng.</summary>
public sealed class OllamaClient(IHttpClientFactory httpFactory) : ILlmClient
{
    public string Service => "Ollama";
    public IReadOnlyList<string> RequiredSettings => [SettingKeys.OllamaUrl, SettingKeys.OllamaModel];

    public async Task<string> GenerateJsonAsync(LlmRequest request, StepContext context)
    {
        var s = context.Settings;
        var body = new JsonObject
        {
            ["model"] = s.Get(SettingKeys.OllamaModel),
            ["stream"] = false,
            ["format"] = request.Schema.DeepClone(),
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = request.System },
                new JsonObject { ["role"] = "user", ["content"] = LlmPrompts.WithSchemaInstruction(request.User, request.Schema) }),
            ["options"] = new JsonObject { ["num_ctx"] = 16384 },
        };
        var url = s.Get(SettingKeys.OllamaUrl).TrimEnd('/') + "/api/chat";
        var json = await HttpHelpers.PostJsonAsync(httpFactory.CreateClient("providers"), Service, url, body,
            context.CancellationToken);
        return json["message"]?["content"]?.GetValue<string>()
               ?? throw new ProviderException("Ollama không trả về nội dung. Kiểm tra model đã được pull chưa.");
    }
}
