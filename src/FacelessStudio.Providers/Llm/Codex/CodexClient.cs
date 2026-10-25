using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Settings;
using FacelessStudio.Providers.Auth;

namespace FacelessStudio.Providers.Llm.Codex;

/// <summary>Gọi model Codex qua gói ChatGPT (backend-api/codex, Responses API dạng stream).</summary>
public sealed class CodexClient(IHttpClientFactory httpFactory, OAuthSession session, CodexAccount account) : ILlmClient
{
    private const string Url = "https://chatgpt.com/backend-api/codex/responses";

    /// <summary>OpenAI yêu cầu app bên thứ ba tự khai tên mình, không giả danh Codex CLI.</summary>
    public const string UserAgent = "FacelessStudio/1.0";

    public string Service => "ChatGPT/Codex";
    public IReadOnlyList<string> RequiredSettings => [SettingKeys.CodexModel];

    public bool IsConfigured(AppSettings settings) => session.IsLoggedIn(account, settings);

    public async Task<string> GenerateJsonAsync(LlmRequest request, StepContext context)
    {
        var ct = context.CancellationToken;
        var body = ResponsesApi.Body(context.Settings.Get(SettingKeys.CodexModel), request, jsonSchema: !request.WebSearch);
        body["stream"] = true;
        body["store"] = false;
        body["reasoning"] = new JsonObject { ["effort"] = request.Effort };
        body["include"] = new JsonArray();

        var tokens = await session.GetTokensAsync(account, context.Settings, ct);
        var http = httpFactory.CreateClient("providers");
        var resp = await SendAsync(http, body, tokens, ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            resp.Dispose();
            tokens = await session.GetTokensAsync(account, context.Settings, ct, forceRefresh: true);
            resp = await SendAsync(http, body, tokens, ct);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode) throw await ErrorAsync(resp, ct);
            return await ResponsesApi.ReadSseTextAsync(await resp.Content.ReadAsStreamAsync(ct), Service, ct);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpClient http, JsonObject body, OAuthTokens tokens, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + tokens.AccessToken);
        if (tokens.Get("account_id") is { Length: > 0 } accountId) req.Headers.TryAddWithoutValidation("ChatGPT-Account-ID", accountId);
        if (tokens.Get("residency") is { Length: > 0 } residency)
            req.Headers.TryAddWithoutValidation("x-openai-internal-codex-residency", residency);
        req.Headers.TryAddWithoutValidation("originator", "faceless-studio");
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "text/event-stream");

        try
        {
            return await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException e)
        {
            throw new ProviderException($"Không kết nối được {Service}: {e.Message}", e);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException($"{Service} phản hồi quá lâu (timeout).", e);
        }
    }

    private async Task<ProviderException> ErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (text.Length > 400) text = text[..400];
        return new ProviderException(resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "ChatGPT: phiên đăng nhập không còn hiệu lực, vào Cài đặt để đăng nhập lại.",
            HttpStatusCode.TooManyRequests => "ChatGPT: đã hết hạn mức của gói (usage limit). Thử lại sau.",
            HttpStatusCode.Forbidden => $"ChatGPT: tài khoản không có quyền dùng Codex (403). Cần gói Plus/Pro/Business. {text}",
            _ => $"{Service} lỗi {(int)resp.StatusCode}: {text}",
        });
    }
}
