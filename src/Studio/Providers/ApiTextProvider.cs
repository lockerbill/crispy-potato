using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Studio.Domain;
using Studio.Services;

namespace Studio.Providers;

/// <summary>API-key text providers. Subscription credentials use separate adapters.</summary>
public sealed class ApiTextProvider(ProviderHttp http) : IStageProvider, IProviderModelSource
{
    public bool Handles(string id) => id is "openrouter" or "anthropic" or "zai" or "moonshot" or "minimax" or "deepseek" or "xai" or "custom-text";

    public Task Validate(ProviderConfig config, Stage stage, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Handles(config.Id) || !ProviderCatalog.Get(config.Id).Stages.Contains(stage))
            throw new ProviderException("Provider không hỗ trợ bước này.");
        _ = Url(config, "");
        return Task.CompletedTask;
    }

    public async Task<string> CheckConnection(ProviderConfig config, string? secret, CancellationToken ct)
    {
        await Validate(config, Stage.Topic, ct);
        RequireKey(config, secret);
        var models = await GetModels(config, secret, ct);
        return models.Count == 0
            ? "Cấu hình hợp lệ; provider không cung cấp danh sách model. Chưa xác minh quyền chạy hoặc quota; nhập model thủ công."
            : "Đã xác thực và tải danh sách model. Quyền tạo nội dung và quota chưa được kiểm tra; không phát sinh lượt tạo nội dung.";
    }

    public async Task<IReadOnlyList<ProviderModel>> GetModels(ProviderConfig config, string? secret, CancellationToken ct)
    {
        await Validate(config, Stage.Topic, ct);
        RequireKey(config, secret);
        if (!ProviderCatalog.Get(config.Id).SupportsModelDiscovery) return [];

        // Discovery is optional: compatible servers may support inference without /models.
        // Do not substitute a billable generation request when discovery is unavailable.
        using var request = Request(config, secret, HttpMethod.Get, "models");
        HttpResponseMessage response;
        try { response = await http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException) { throw new ProviderException("Không kết nối được provider; kiểm tra mạng và endpoint."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ProviderException("Provider không phản hồi trong thời gian cho phép."); }
        using (response)
        {
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented) return [];
            if (!response.IsSuccessStatusCode) throw DiscoveryError(response.StatusCode);
            try
            {
                var data = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
                if (data?["data"] is not JsonArray list) throw new InvalidDataException();
                return list.Select(item => new ProviderModel(RequiredString(item, "id"), OptionalString(item, "display_name") ?? OptionalString(item, "name") ?? RequiredString(item, "id")))
                    .Where(model => !string.IsNullOrWhiteSpace(model.Id))
                    .DistinctBy(model => model.Id).OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch (Exception e) when (IsMalformed(e)) { throw new ProviderException("Provider trả danh sách model không hợp lệ; có thể nhập model thủ công."); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ProviderException("Provider không phản hồi trong thời gian cho phép."); }
        }
    }

    public async Task<ProviderResult> Execute(ProviderContext context, CancellationToken ct)
    {
        await Validate(context.Config, context.Stage, ct);
        RequireKey(context.Config, context.Secret);
        if (string.IsNullOrWhiteSpace(context.Model) || context.Model.Length > 256 || context.Model.Any(char.IsControl))
            throw new ProviderException("Cần chọn hoặc nhập model hợp lệ trước khi chạy.");
        var prompt = TextPrompts.Build(context.Project, context.Stage);
        var limit = BudgetService.OutputTokenLimit(context.Project, context.Stage);
        var anthropic = context.Config.Id == "anthropic";
        var body = new JsonObject
        {
            ["model"] = context.Model,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
            ["stream"] = false
        };
        // Optional controls differ across models. Prompt-enforced JSON keeps the common
        // transport usable without claiming universal JSON-schema/temperature support.
        body[context.Config.Id is "openrouter" or "xai" or "minimax" ? "max_completion_tokens" : "max_tokens"] = limit;
        using var request = Request(context.Config, context.Secret, HttpMethod.Post, anthropic ? "messages" : "chat/completions", body);
        JsonNode data;
        try { data = await http.Json(request, ct, true); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ProviderException("Provider hết thời gian phản hồi; yêu cầu có thể đã được tính phí.", true); }
        catch (Exception e) when (IsMalformed(e)) { throw new ProviderException("Provider trả kết quả không hợp lệ; yêu cầu có thể đã được tính phí.", true); }

        string raw;
        try
        {
            if (anthropic)
            {
                if (RequiredString(data, "stop_reason") != "end_turn") throw Incomplete();
                if (data["content"] is not JsonArray content) throw new InvalidDataException();
                raw = string.Concat(content.Where(block => OptionalString(block, "type") == "text").Select(block => RequiredString(block, "text")));
            }
            else
            {
                if (data["choices"] is not JsonArray { Count: 1 } choices) throw new InvalidDataException();
                var choice = choices[0];
                if (RequiredString(choice, "finish_reason") != "stop") throw Incomplete();
                var message = choice?["message"];
                if (message?["refusal"] is not null || message?["tool_calls"] is JsonArray { Count: > 0 }) throw Incomplete();
                raw = RequiredString(message, "content");
            }
            raw = CleanOutput(raw);
            var project = Json.Clone(context.Project);
            // Apply the same stage contract as the original text provider, on a copy so
            // invalid scenes/tags cannot leave the caller with a partially changed project.
            TextProvider.Apply(project, context.Stage, raw);
            return new ProviderResult(raw, project);
        }
        catch (Exception e) when (IsMalformed(e)) { throw new ProviderException("Nội dung AI không đúng cấu trúc yêu cầu; kết quả không được duyệt.", true); }
    }

    private static string CleanOutput(string raw)
    {
        var result = raw.Trim();
        // Strip only leading reasoning envelopes. Never remove tags inside JSON strings.
        while (result.StartsWith("<think>", StringComparison.OrdinalIgnoreCase))
        {
            var end = result.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
            if (end < 0) throw Incomplete();
            result = result[(end + "</think>".Length)..].TrimStart();
        }
        if (result.StartsWith("```json\n", StringComparison.OrdinalIgnoreCase) && result.EndsWith("```", StringComparison.Ordinal))
            result = result[8..^3].Trim();
        else if (result.StartsWith("```json\r\n", StringComparison.OrdinalIgnoreCase) && result.EndsWith("```", StringComparison.Ordinal))
            result = result[9..^3].Trim();
        if (result.Length == 0) throw Incomplete();
        return result;
    }

    private static string Url(ProviderConfig config, string relativePath)
    {
        var endpoint = config.Id == "custom-text" ? config.Endpoint.Trim() : ProviderCatalog.Get(config.Id).BaseUrl;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || string.IsNullOrWhiteSpace(uri.Host) || endpoint.Contains('\\'))
            throw new ProviderException("Cần API base URL HTTP(S) hợp lệ, không chứa tài khoản, query hoặc fragment.");
        return uri.AbsoluteUri.TrimEnd('/') + (relativePath.Length == 0 ? "" : "/" + relativePath);
    }

    private static HttpRequestMessage Request(ProviderConfig config, string? secret, HttpMethod method, string path, object? body = null)
    {
        var request = ProviderHttp.Request(method, Url(config, path), body, config.Id == "anthropic" ? null : secret);
        if (config.Id == "anthropic")
        {
            request.Headers.Add("x-api-key", secret);
            request.Headers.Add("anthropic-version", "2023-06-01");
        }
        return request;
    }

    private static void RequireKey(ProviderConfig config, string? secret)
    {
        if (config.Id != "custom-text" && string.IsNullOrWhiteSpace(secret)) throw new ProviderException("Cần lưu API key trước khi kết nối.");
        if (secret?.Any(char.IsControl) == true) throw new ProviderException("API key không hợp lệ.");
    }
    private static string RequiredString(JsonNode? node, string key) => OptionalString(node, key) ?? throw new InvalidDataException();
    private static string? OptionalString(JsonNode? node, string key) => node?[key]?.GetValue<string>();
    private static bool IsMalformed(Exception e) => e is JsonException or InvalidDataException or InvalidOperationException or FormatException or ArgumentException or OverflowException;
    private static ProviderException Incomplete() => new("Provider không hoàn tất câu trả lời (giới hạn token, bộ lọc hoặc tool call); kết quả không được duyệt.", true);
    private static ProviderException DiscoveryError(HttpStatusCode status) => new(status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Provider từ chối xác thực/quyền truy cập; kiểm tra API key và tài khoản.",
        HttpStatusCode.TooManyRequests => "Provider hết quota hoặc rate limit; thử lại sau.",
        _ => $"Provider HTTP {(int)status}; không tải được danh sách model."
    });
}
