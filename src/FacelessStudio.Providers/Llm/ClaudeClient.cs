using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Settings;

namespace FacelessStudio.Providers.Llm;

/// <summary>Claude qua SDK chính thức của Anthropic.</summary>
public sealed class ClaudeClient(IHttpClientFactory httpFactory) : ILlmClient
{
    public string Service => "Claude";
    public IReadOnlyList<string> RequiredSettings => [SettingKeys.AnthropicApiKey];

    public async Task<string> GenerateJsonAsync(LlmRequest request, StepContext context)
    {
        var s = context.Settings;
        var client = new AnthropicClient
        {
            ApiKey = s.Get(SettingKeys.AnthropicApiKey),
            HttpClient = httpFactory.CreateClient("anthropic"),
            Timeout = TimeSpan.FromMinutes(10),
        };

        var outputConfig = new BetaOutputConfig { Effort = ToEffort(request.Effort) };
        var parameters = new MessageCreateParams
        {
            Model = s.Get(SettingKeys.ClaudeModel),
            MaxTokens = 16000,
            // Nếu bộ lọc an toàn từ chối nhầm, server tự chạy lại bằng model dự phòng phù hợp.
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
            System = request.System,
            Messages = [new BetaUserMessageParam { Content = request.User }],
        };
        if (request.WebSearch)
        {
            // Web search sinh citation, không dùng chung được với structured output nên mô tả schema trong prompt.
            parameters = parameters with
            {
                Tools = [new BetaToolUnion(new BetaWebSearchTool20260209 { MaxUses = 5 })],
                Messages = [new BetaUserMessageParam { Content = LlmPrompts.WithSchemaInstruction(request.User, request.Schema) }],
                OutputConfig = outputConfig,
            };
        }
        else
        {
            var schema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(request.Schema.ToJsonString())!;
            outputConfig = outputConfig with { Format = new BetaJsonOutputFormat { Schema = schema } };
            parameters = parameters with { OutputConfig = outputConfig };
        }

        BetaMessage response;
        try
        {
            response = await client.Beta.Messages.Create(parameters, cancellationToken: context.CancellationToken);
        }
        catch (AnthropicUnauthorizedException e)
        {
            throw new ProviderException("Claude: API key không hợp lệ.", e);
        }
        catch (AnthropicRateLimitException e)
        {
            throw new ProviderException("Claude: vượt giới hạn gọi API (rate limit), thử lại sau ít phút.", e);
        }
        catch (AnthropicApiException e)
        {
            throw new ProviderException($"Claude API lỗi: {e.Message}", e);
        }

        var stop = response.StopReason;
        if (stop == "refusal") throw new ProviderException("Claude từ chối chủ đề này. Hãy thử diễn đạt khác.");
        if (stop == "max_tokens") throw new ProviderException("Kịch bản quá dài bị cắt ngang. Thử chọn độ dài ngắn hơn.");

        var texts = response.Content.Select(b => b.TryPickText(out var t) ? t.Text : null).Where(t => t != null);
        var text = string.Concat(texts);
        if (text.Length == 0) throw new ProviderException("Claude không trả về nội dung.");
        return text;
    }

    private static Effort ToEffort(string effort) => effort switch
    {
        "low" => Effort.Low,
        "high" => Effort.High,
        _ => Effort.Medium,
    };
}
