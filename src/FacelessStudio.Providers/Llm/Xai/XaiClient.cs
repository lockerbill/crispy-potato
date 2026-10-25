using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Settings;
using FacelessStudio.Providers.Auth;

namespace FacelessStudio.Providers.Llm.Xai;

/// <summary>
/// xAI Grok qua Responses API. Hai chế độ: API key (account = null) hoặc đăng nhập SuperGrok / X Premium+
/// (truyền account và session).
/// </summary>
public sealed class XaiClient(IHttpClientFactory httpFactory, XaiAccount? account = null, OAuthSession? session = null) : ILlmClient
{
    private const string Url = "https://api.x.ai/v1/responses";
    private bool OAuth => account != null && session != null;

    public string Service => OAuth ? "xAI (SuperGrok)" : "xAI";

    public IReadOnlyList<string> RequiredSettings => OAuth ? [SettingKeys.XaiOAuthModel] : [SettingKeys.XaiApiKey];

    public bool IsConfigured(AppSettings settings) =>
        OAuth ? session!.IsLoggedIn(account!, settings) && settings.Has(SettingKeys.XaiOAuthModel)
              : RequiredSettings.All(settings.Has);

    public async Task<string> GenerateJsonAsync(LlmRequest request, StepContext context)
    {
        var s = context.Settings;
        var ct = context.CancellationToken;
        var model = s.Get(OAuth ? SettingKeys.XaiOAuthModel : SettingKeys.XaiModel);
        var body = ResponsesApi.Body(model, request, jsonSchema: !request.WebSearch);
        if (request.WebSearch)
        {
            body["tools"] = new JsonArray(
                new JsonObject { ["type"] = "web_search" },
                new JsonObject { ["type"] = "x_search" });
        }

        var token = OAuth ? (await session!.GetTokensAsync(account!, s, ct)).AccessToken : s.Get(SettingKeys.XaiApiKey);
        var (status, text) = await PostAsync(body, token, ct);
        if (status == HttpStatusCode.Unauthorized && OAuth)
        {
            token = (await session!.GetTokensAsync(account!, s, ct, forceRefresh: true)).AccessToken;
            (status, text) = await PostAsync(body, token, ct);
        }
        if (!(status >= HttpStatusCode.OK && status < HttpStatusCode.MultipleChoices)) throw Error(status, text);

        JsonNode? json;
        try
        {
            json = JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new ProviderException("xAI trả về dữ liệu không phải JSON.", e);
        }
        return ResponsesApi.ExtractText(json ?? throw new ProviderException("xAI trả về dữ liệu rỗng."), Service);
    }

    private async Task<(HttpStatusCode Status, string Body)> PostAsync(JsonObject body, string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var resp = await httpFactory.CreateClient("providers").SendAsync(req, ct);
            return (resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
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

    private ProviderException Error(HttpStatusCode status, string body)
    {
        if (body.Length > 400) body = body[..400];
        return new ProviderException(status switch
        {
            HttpStatusCode.Forbidden when OAuth => "xAI: gói hiện tại không có quyền dùng API (cần SuperGrok hoặc X Premium+).",
            HttpStatusCode.Unauthorized when OAuth => "xAI: phiên đăng nhập không còn hiệu lực, vào Cài đặt để đăng nhập lại.",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                $"xAI: API key không hợp lệ hoặc không có quyền ({(int)status}). {body}",
            HttpStatusCode.TooManyRequests => $"{Service}: vượt giới hạn gọi API (429). Thử lại sau. {body}",
            HttpStatusCode.PaymentRequired => $"{Service}: tài khoản hết credit hoặc chưa thanh toán (402). {body}",
            _ => $"{Service} lỗi {(int)status}: {body}",
        });
    }
}
