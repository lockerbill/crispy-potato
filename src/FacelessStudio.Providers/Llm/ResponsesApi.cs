using System.Text;
using System.Text.Json.Nodes;
using FacelessStudio.Core.Abstractions;

namespace FacelessStudio.Providers.Llm;

/// <summary>Dựng request và đọc kết quả OpenAI Responses API (dùng cho ChatGPT/Codex và xAI).</summary>
public static class ResponsesApi
{
    /// <summary>
    /// Body cơ bản. jsonSchema = true thì ép kết quả theo schema qua text.format; false thì mô tả schema
    /// trong prompt (khi dùng kèm tool như web search).
    /// </summary>
    public static JsonObject Body(string model, LlmRequest request, bool jsonSchema = true)
    {
        var user = jsonSchema ? request.User : LlmPrompts.WithSchemaInstruction(request.User, request.Schema);
        var body = new JsonObject
        {
            ["model"] = model,
            ["instructions"] = request.System,
            ["input"] = new JsonArray(new JsonObject
            {
                ["type"] = "message",
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = user }),
            }),
            ["store"] = false,
        };
        if (jsonSchema)
        {
            body["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = "output",
                    ["schema"] = request.Schema.DeepClone(),
                    ["strict"] = false,
                },
            };
        }
        return body;
    }

    /// <summary>Ghép chữ trong output[].content[] của một response hoàn chỉnh.</summary>
    public static string ExtractText(JsonNode response, string service)
    {
        var text = new StringBuilder();
        foreach (var item in response["output"]?.AsArray() ?? [])
        {
            if (item?["type"]?.GetValue<string>() != "message") continue;
            foreach (var part in item["content"]?.AsArray() ?? [])
            {
                switch (part?["type"]?.GetValue<string>())
                {
                    case "output_text":
                        text.Append(part["text"]?.GetValue<string>());
                        break;
                    case "refusal":
                        throw new ProviderException($"{service} từ chối chủ đề này. Hãy thử diễn đạt khác.");
                }
            }
        }
        if (text.Length > 0) return text.ToString();

        if (response["status"]?.GetValue<string>() == "incomplete")
        {
            var reason = response["incomplete_details"]?["reason"]?.GetValue<string>();
            throw new ProviderException(reason == "max_output_tokens"
                ? "Kịch bản quá dài bị cắt ngang. Thử chọn độ dài ngắn hơn."
                : $"{service} dừng giữa chừng ({reason ?? "incomplete"}).");
        }
        if (ErrorMessage(response) is { } error) throw new ProviderException($"{service} lỗi: {error}");
        throw new ProviderException($"{service} không trả về nội dung.");
    }

    /// <summary>
    /// Đọc luồng SSE của Responses API (stream: true) tới event response.completed rồi trả về chữ kết quả.
    /// </summary>
    public static async Task<string> ReadSseTextAsync(Stream stream, string service, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var deltas = new StringBuilder();
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith("data:"))
            {
                data.Append(line.AsSpan(5).TrimStart());
                continue;
            }
            if (line.Length != 0 || data.Length == 0) continue;

            var result = HandleEvent(data.ToString(), service, deltas);
            data.Clear();
            if (result != null) return result;
        }
        if (data.Length > 0 && HandleEvent(data.ToString(), service, deltas) is { } last) return last;
        if (deltas.Length > 0) return deltas.ToString();
        throw new ProviderException($"{service} đóng kết nối trước khi trả lời xong.");
    }

    private static string? HandleEvent(string data, string service, StringBuilder deltas)
    {
        if (data == "[DONE]") return deltas.Length > 0 ? deltas.ToString() : null;
        JsonNode? evt;
        try
        {
            evt = JsonNode.Parse(data);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        switch (evt?["type"]?.GetValue<string>())
        {
            case "response.output_text.delta":
                deltas.Append(evt["delta"]?.GetValue<string>());
                return null;
            case "response.completed":
            case "response.incomplete":
                var response = evt["response"];
                if (response == null) return deltas.Length > 0 ? deltas.ToString() : null;
                try
                {
                    return ExtractText(response, service);
                }
                catch (ProviderException) when (deltas.Length > 0 && response["status"]?.GetValue<string>() != "incomplete")
                {
                    return deltas.ToString();
                }
            case "response.failed":
                throw new ProviderException($"{service} lỗi: {ErrorMessage(evt["response"]) ?? "response.failed"}");
            case "error":
                throw new ProviderException($"{service} lỗi: {ErrorMessage(evt) ?? data}");
            default:
                return null;
        }
    }

    private static string? ErrorMessage(JsonNode? node)
    {
        var error = node?["error"];
        if (error is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        return error?["message"]?.GetValue<string>() ?? node?["message"]?.GetValue<string>();
    }
}
