using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Studio.Domain;

namespace Studio.Providers;

public sealed class ProviderHttp(IHttpClientFactory factory)
{
    public HttpClient Client => factory.CreateClient("providers");
    public async Task<JsonNode> Json(HttpRequestMessage request, CancellationToken ct, bool billable = false)
    {
        using var response = await Send(request, ct, billable);
        try { return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) ?? throw new InvalidDataException(); }
        catch (System.Text.Json.JsonException) { throw new ProviderException("Provider trả dữ liệu JSON không hợp lệ.", billable); }
    }
    public async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct, bool billable = false)
    {
        HttpResponseMessage response;
        try { response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException) { throw new ProviderException("Không kết nối được provider. Kiểm tra endpoint, mạng và trạng thái tác vụ trước khi chạy lại.", billable); }
        if (response.IsSuccessStatusCode) return response;
        var status = response.StatusCode;
        var retry = response.Headers.RetryAfter?.ToString();
        response.Dispose();
        var message = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Provider từ chối xác thực/quyền truy cập. Kiểm tra key, model và tài khoản.",
            HttpStatusCode.TooManyRequests => "Provider hết quota hoặc rate limit." + (retry is null ? "" : $" Retry-After: {retry}."),
            _ => $"Provider HTTP {(int)status}. Kiểm tra request/model và bảng điều khiển provider."
        };
        throw new ProviderException(message, billable && (int)status >= 500);
    }
    public static HttpRequestMessage Request(HttpMethod method, string url, object? body = null, string? bearer = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = new StringContent(Domain.Json.Write(body), Encoding.UTF8, "application/json");
        if (!string.IsNullOrWhiteSpace(bearer)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return request;
    }
    public async Task Download(string url, string output, CancellationToken ct, long maxBytes = 500_000_000)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0 || uri.IsLoopback || IPAddress.TryParse(uri.Host, out _))
            throw new ProviderException("Provider trả URL media không an toàn hoặc không được hỗ trợ.");
        using var req = Request(HttpMethod.Get, url);
        using var response = await Send(req, ct);
        await SaveBody(response, output, ct, maxBytes);
    }
    public static async Task SaveBody(HttpResponseMessage response, string output, CancellationToken ct, long maxBytes = 500_000_000)
    {
        if (response.Content.Headers.ContentLength > maxBytes) throw new ProviderException("Media vượt giới hạn tải.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(output);
        var buffer = new byte[81920]; long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct); if (read == 0) break;
            total += read; if (total > maxBytes) throw new ProviderException("Media vượt giới hạn tải.");
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }
    public static string Required(JsonNode? node, params string[] keys)
    {
        foreach (var key in keys) node = node?[key];
        return node?.GetValue<string>() ?? throw new ProviderException("Provider thiếu trường kết quả bắt buộc.", true);
    }
    public static string LocalUrl(ProviderConfig config, string path)
    {
        if (!Uri.TryCreate(config.Endpoint, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || u.UserInfo.Length > 0)
            throw new InvalidOperationException("Cần cấu hình endpoint HTTP(S) của engine.");
        return config.Endpoint.TrimEnd('/') + path;
    }
}
