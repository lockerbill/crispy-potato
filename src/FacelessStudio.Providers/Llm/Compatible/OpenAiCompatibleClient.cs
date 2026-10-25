using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Settings;
using FacelessStudio.Providers.Http;

namespace FacelessStudio.Providers.Llm.Compatible;

/// <summary>Cách ép model trả JSON qua tham số response_format.</summary>
public enum JsonMode
{
    /// <summary>{"type":"json_object"}: JSON hợp lệ, schema nằm trong prompt.</summary>
    JsonObject,

    /// <summary>{"type":"json_schema"}: gửi kèm JSON schema.</summary>
    JsonSchema,

    /// <summary>Không gửi response_format, chỉ dựa vào prompt.</summary>
    None,
}

/// <summary>
/// Mô tả một dịch vụ dùng API kiểu OpenAI Chat Completions. Chỉ cần dữ liệu này là có thêm một nhà cung cấp mới.
/// </summary>
/// <param name="Key">Mã vendor, thành "script-{Key}" / "topic-{Key}".</param>
/// <param name="Service">Tên dịch vụ trong thông báo lỗi.</param>
/// <param name="ApiKeySetting">Khoá setting chứa API key (null nếu không dùng key).</param>
/// <param name="BaseUrlSetting">Khoá setting chứa base URL; null thì dùng DefaultBaseUrl cố định.</param>
/// <param name="DefaultBaseUrl">Base URL dùng khi setting trống. Để trống nghĩa là người dùng bắt buộc tự nhập.</param>
/// <param name="ModelSetting">Khoá setting chứa tên model.</param>
/// <param name="Mode">Chế độ JSON mặc định.</param>
/// <param name="MaxTokens">Giới hạn token đầu ra (null: để server tự quyết).</param>
/// <param name="Headers">Header cố định gửi kèm mọi request.</param>
/// <param name="JsonModeSetting">Khoá setting cho phép người dùng đổi chế độ JSON (json_object | json_schema | none).</param>
/// <param name="ApiKeyOptional">API key không bắt buộc (server local thường không cần).</param>
public sealed record CompatibleVendor(
    string Key,
    string Service,
    string? ApiKeySetting,
    string? BaseUrlSetting,
    string DefaultBaseUrl,
    string ModelSetting,
    JsonMode Mode,
    int? MaxTokens,
    (string Name, string Value)[] Headers,
    string? JsonModeSetting = null,
    bool ApiKeyOptional = false);

/// <summary>Client Chat Completions dùng chung cho OpenRouter, z.ai, Kimi, MiniMax, DeepSeek và endpoint tự chọn.</summary>
public sealed partial class OpenAiCompatibleClient(CompatibleVendor vendor, IHttpClientFactory httpFactory) : ILlmClient
{
    public string Service => vendor.Service;

    public IReadOnlyList<string> RequiredSettings
    {
        get
        {
            var keys = new List<string>();
            if (vendor.ApiKeySetting != null && !vendor.ApiKeyOptional) keys.Add(vendor.ApiKeySetting);
            // Vendor không có base URL mặc định (endpoint tự chọn) thì bắt buộc nhập.
            if (vendor.BaseUrlSetting != null && vendor.DefaultBaseUrl.Length == 0) keys.Add(vendor.BaseUrlSetting);
            keys.Add(vendor.ModelSetting);
            return keys;
        }
    }

    public async Task<string> GenerateJsonAsync(LlmRequest request, StepContext context)
    {
        var s = context.Settings;
        var body = new JsonObject
        {
            ["model"] = s.Get(vendor.ModelSetting),
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = request.System },
                new JsonObject { ["role"] = "user", ["content"] = LlmPrompts.WithSchemaInstruction(request.User, request.Schema) }),
        };
        switch (ResolveMode(s))
        {
            case JsonMode.JsonObject:
                body["response_format"] = new JsonObject { ["type"] = "json_object" };
                break;
            case JsonMode.JsonSchema:
                body["response_format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject
                    {
                        ["name"] = "output",
                        ["schema"] = request.Schema.DeepClone(),
                        ["strict"] = false,
                    },
                };
                break;
        }
        if (vendor.MaxTokens is { } max) body["max_tokens"] = max;

        var headers = new List<(string Name, string Value)>();
        var apiKey = vendor.ApiKeySetting != null ? s.Get(vendor.ApiKeySetting) : "";
        if (apiKey.Length > 0) headers.Add(("Authorization", "Bearer " + apiKey));
        headers.AddRange(vendor.Headers);

        var json = await HttpHelpers.PostJsonAsync(httpFactory.CreateClient("providers"), Service, BuildUrl(s), body,
            context.CancellationToken, headers.ToArray());

        var choice = json["choices"]?[0];
        if (choice == null)
        {
            var error = json["error"]?["message"]?.ToString();
            throw new ProviderException(string.IsNullOrWhiteSpace(error)
                ? $"{Service} không trả về nội dung."
                : $"{Service} báo lỗi: {error}");
        }
        if (choice["finish_reason"]?.ToString() == "length")
            throw new ProviderException("Kịch bản quá dài bị cắt ngang. Thử chọn độ dài ngắn hơn.");

        var text = ThinkBlock().Replace(choice["message"]?["content"]?.ToString() ?? "", "").Trim();
        if (text.Length == 0) throw new ProviderException($"{Service} không trả về nội dung.");
        return text;
    }

    private string BuildUrl(AppSettings s)
    {
        var baseUrl = vendor.BaseUrlSetting != null ? s.Get(vendor.BaseUrlSetting) : "";
        if (baseUrl.Length == 0) baseUrl = vendor.DefaultBaseUrl;
        return baseUrl.TrimEnd('/') + "/chat/completions";
    }

    private JsonMode ResolveMode(AppSettings s)
    {
        if (vendor.JsonModeSetting == null) return vendor.Mode;
        return s.Get(vendor.JsonModeSetting).ToLowerInvariant() switch
        {
            "json_schema" => JsonMode.JsonSchema,
            "none" => JsonMode.None,
            _ => JsonMode.JsonObject,
        };
    }

    /// <summary>Một số model suy luận (MiniMax M2, ...) chèn phần suy nghĩ trong thẻ think vào content.</summary>
    [GeneratedRegex("<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlock();
}
