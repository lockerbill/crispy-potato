using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Studio.Domain;

namespace Studio.Providers;

public sealed class SubscriptionResponsesProvider(ISubscriptionAuth auth, IHttpClientFactory clients) : IStageProvider, IProviderModelSource
{
    public bool Handles(string id) => id is "chatgpt-subscription" or "supergrok";
    private static string Base(string id) => id == "chatgpt-subscription" ? "https://api.openai.com/v1" : "https://api.x.ai/v1";
    public async Task Validate(ProviderConfig config, Stage stage, CancellationToken ct)
    {
        if (!Handles(config.Id) || stage is not (Stage.Topic or Stage.Script or Stage.Title or Stage.Description or Stage.Tags)) throw new ProviderException("Subscription chỉ hỗ trợ các bước văn bản.");
        if (!(await auth.Status(config.Id, ct)).Connected) throw new ProviderException("Hãy đăng nhập subscription trước khi chạy.");
    }
    public async Task<string> CheckConnection(ProviderConfig config, string? secret, CancellationToken ct)
    {
        var models = await GetModels(config, null, ct);
        return $"Đã xác thực / Authenticated: {models.Count} models. Quyền chạy và quota kiểm tra khi tạo nội dung / Access and quota checked at generation.";
    }
    public async Task<IReadOnlyList<ProviderModel>> GetModels(ProviderConfig config, string? secret, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await Send(config.Id, HttpMethod.Get, "/models", null, timeout.Token);
            var data = JsonNode.Parse(await BoundedBody.Read(response.Content, 2 * 1024 * 1024, timeout.Token));
            var list = (data?[config.Id == "chatgpt-subscription" ? "models" : "data"] as JsonArray) ?? throw new ProviderException("Provider không trả danh sách model.");
            return list.Where(x => x is not null && (config.Id != "chatgpt-subscription" || x["visibility"]?.GetValue<string>() == "list"))
                .Select(x => new ProviderModel(ProviderHttp.Required(x, config.Id == "chatgpt-subscription" ? "slug" : "id"), x?["display_name"]?.GetValue<string>() ?? x?["slug"]?.GetValue<string>() ?? x?["id"]?.GetValue<string>() ?? ""))
                .Where(x => x.Id.Length > 0).Take(2000).ToList();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ProviderException("Lấy danh sách model hết thời gian chờ / Model discovery timed out."); }
        catch (IOException) { throw new ProviderException("Kết nối danh sách model bị gián đoạn / Model discovery connection interrupted."); }
        catch (System.Text.Json.JsonException) { throw new ProviderException("Danh sách model không hợp lệ."); }
        catch (InvalidOperationException) { throw new ProviderException("Danh sách model không hợp lệ."); }
    }
    public async Task<ProviderResult> Execute(ProviderContext context, CancellationToken ct)
    {
        await Validate(context.Config, context.Stage, ct);
        if (string.IsNullOrWhiteSpace(context.Model)) throw new ProviderException("Cần chọn model subscription.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            // SIWC rejects temperature, max_output_tokens and system-role input. Keep this route stateless.
            using var response = await Send(context.Config.Id, HttpMethod.Post, "/responses", new {
                model = context.Model, input = new[] { new { role = "user", content = TextPrompts.Build(context.Project, context.Stage) } }, store = false, stream = true
            }, timeout.Token);
            var text = await ReadEvents(response.Content, timeout.Token);
            var project = Json.Clone(context.Project);
            TextProvider.Apply(project, context.Stage, text);
            return new(text, project);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ProviderException("Subscription hết thời gian chờ; kiểm tra usage trước khi chạy lại.", true); }
        catch (HttpRequestException) { throw new ProviderException("Kết nối subscription bị gián đoạn; có thể đã tính usage.", true); }
        catch (IOException) { throw new ProviderException("Luồng subscription bị gián đoạn; có thể đã tính usage.", true); }
        catch (System.Text.Json.JsonException) { throw new ProviderException("Subscription trả dữ liệu không hợp lệ.", true); }
        catch (InvalidOperationException) { throw new ProviderException("Subscription trả dữ liệu không hợp lệ.", true); }
    }
    private async Task<HttpResponseMessage> Send(string id, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var token = await auth.AccessToken(id, false, ct);
        for (var attempt = 0; ; attempt++)
        {
            using var request = ProviderHttp.Request(method, Base(id) + path, body, token);
            var client = clients.CreateClient("subscription-responses");
            HttpResponseMessage response;
            try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
            catch (HttpRequestException) { throw new ProviderException("Không kết nối được subscription.", method == HttpMethod.Post); }
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            { response.Dispose(); token = await auth.AccessToken(id, true, ct); continue; }
            if (response.IsSuccessStatusCode) return response;
            var code = (int)response.StatusCode; response.Dispose();
            throw new ProviderException(code switch {
                403 => "Subscription không có quyền chạy model/API (403). Kiểm tra gói tài khoản hoặc chọn kết nối API key; không tự chuyển tính phí.",
                401 => "Subscription hết phiên đăng nhập. Hãy kết nối lại.",
                429 => "Subscription hết quota hoặc bị rate limit. Hãy thử sau.",
                _ => $"Subscription trả HTTP {code}. Kiểm tra model và trạng thái dịch vụ."
            }, method == HttpMethod.Post && code >= 500);
        }
    }
    internal static async Task<string> ReadEvents(HttpContent content, CancellationToken ct)
    {
        const int maximum = 4 * 1024 * 1024;
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var buffer = new char[4096]; var line = new StringBuilder(); var eventData = new StringBuilder(); var output = new StringBuilder();
        var completed = false; var total = 0;
        void Event()
        {
            if (eventData.Length == 0) return;
            var value = eventData.ToString(); eventData.Clear();
            if (value.Trim() == "[DONE]") return;
            var item = JsonNode.Parse(value) ?? throw new ProviderException("SSE không hợp lệ.", true);
            var type = item["type"]?.GetValue<string>();
            if (type == "response.output_text.delta") { output.Append(item["delta"]?.GetValue<string>()); if (output.Length > 1024 * 1024) throw new ProviderException("Nội dung vượt giới hạn.", true); }
            if (type is "response.failed" or "response.incomplete" or "error") throw new ProviderException("Subscription chưa hoàn tất nội dung; có thể hết quota hoặc bị gián đoạn. Kiểm tra usage trước khi chạy lại.", true);
            if (type == "response.completed")
            {
                if (item["response"]?["status"]?.GetValue<string>() is { } status && status != "completed") throw new ProviderException("Subscription không hoàn tất yêu cầu.", true);
                completed = true;
                if (output.Length == 0 && item["response"]?["output"] is JsonArray items)
                    foreach (var message in items.Where(x => x?["type"]?.GetValue<string>() == "message"))
                        if (message?["content"] is JsonArray blocks)
                            foreach (var block in blocks.Where(x => x?["type"]?.GetValue<string>() == "output_text")) output.Append(block?["text"]?.GetValue<string>());
            }
        }
        void Line()
        {
            var value = line.ToString().TrimEnd('\r'); line.Clear();
            if (value.Length == 0) { Event(); return; }
            if (value.StartsWith("data:", StringComparison.Ordinal)) { if (eventData.Length > 0) eventData.Append('\n'); eventData.Append(value.AsSpan(5).TrimStart()); }
        }
        while (!completed)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0) break;
            total += read; if (total > maximum) throw new ProviderException("Luồng subscription vượt giới hạn.", true);
            foreach (var ch in buffer.AsSpan(0, read)) { if (ch == '\n') Line(); else line.Append(ch); }
        }
        if (line.Length > 0) Line(); if (eventData.Length > 0) Event();
        if (!completed || string.IsNullOrWhiteSpace(output.ToString()) || output.Length > 1024 * 1024) throw new ProviderException("Subscription chưa trả kết quả hoàn tất. Có thể đã sử dụng quota.", true);
        return output.ToString();
    }
}
