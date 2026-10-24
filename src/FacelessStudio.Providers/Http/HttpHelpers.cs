using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FacelessStudio.Core.Abstractions;

namespace FacelessStudio.Providers.Http;

/// <summary>Gọi API JSON và đổi lỗi HTTP thành thông báo tiếng Việt dễ hiểu.</summary>
public static class HttpHelpers
{
    public static async Task<JsonNode> PostJsonAsync(HttpClient http, string service, string url, JsonNode body,
        CancellationToken ct, params (string Name, string Value)[] headers)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        foreach (var (name, value) in headers) req.Headers.TryAddWithoutValidation(name, value);
        return await SendJsonAsync(http, service, req, ct);
    }

    public static async Task<JsonNode> GetJsonAsync(HttpClient http, string service, string url, CancellationToken ct,
        params (string Name, string Value)[] headers)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (name, value) in headers) req.Headers.TryAddWithoutValidation(name, value);
        return await SendJsonAsync(http, service, req, ct);
    }

    public static async Task<JsonNode> SendJsonAsync(HttpClient http, string service, HttpRequestMessage req, CancellationToken ct)
    {
        using var resp = await SendAsync(http, service, req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            return JsonNode.Parse(text) ?? throw new ProviderException($"{service} trả về dữ liệu rỗng.");
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new ProviderException($"{service} trả về dữ liệu không phải JSON.", e);
        }
    }

    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, string service, HttpRequestMessage req,
        CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException e)
        {
            throw new ProviderException($"Không kết nối được {service}: {e.Message}", e);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException($"{service} phản hồi quá lâu (timeout).", e);
        }
        if (resp.IsSuccessStatusCode) return resp;

        var body = await resp.Content.ReadAsStringAsync(ct);
        resp.Dispose();
        if (body.Length > 400) body = body[..400];
        throw new ProviderException(resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                $"{service}: API key không hợp lệ hoặc không có quyền ({(int)resp.StatusCode}). {body}",
            HttpStatusCode.TooManyRequests =>
                $"{service}: vượt giới hạn gọi API / hết quota miễn phí (429). Thử lại sau. {body}",
            HttpStatusCode.PaymentRequired => $"{service}: tài khoản hết tiền hoặc chưa thanh toán (402). {body}",
            _ => $"{service} lỗi {(int)resp.StatusCode}: {body}",
        });
    }

    public static async Task DownloadAsync(HttpClient http, string service, string url, string path, CancellationToken ct,
        params (string Name, string Value)[] headers)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (name, value) in headers) req.Headers.TryAddWithoutValidation(name, value);
        using var resp = await SendAsync(http, service, req, ct);
        var tmp = path + ".part";
        await using (var fs = File.Create(tmp))
            await resp.Content.CopyToAsync(fs, ct);
        File.Move(tmp, path, overwrite: true);
    }

    public static AuthenticationHeaderValue Bearer(string token) => new("Bearer", token);
}
