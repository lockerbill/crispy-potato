using FacelessStudio.Core.Models;
using FacelessStudio.Core.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace FacelessStudio.Providers.Llm.Compatible;

public static class CompatibleRegistration
{
    private static readonly (string Name, string Value)[] NoHeaders = [];

    /// <summary>OpenRouter, z.ai/GLM, Kimi, MiniMax, DeepSeek, Custom endpoint (API kiểu OpenAI).</summary>
    public static IServiceCollection AddCompatibleLlmProviders(this IServiceCollection services)
    {
        services.AddCompatible(
            new CompatibleVendor("openrouter", "OpenRouter", SettingKeys.OpenRouterApiKey, null,
                "https://openrouter.ai/api/v1", SettingKeys.OpenRouterModel, JsonMode.JsonObject, 16000,
                [("HTTP-Referer", "https://github.com/lockerbill/crispy-potato"), ("X-Title", "Faceless Studio")]),
            new LlmVendorInfo("openrouter", "OpenRouter", ProviderCategory.PaidCloud,
                "Một API key dùng được hàng trăm model (Claude, GPT, Gemini, Llama...), có cả model miễn phí.",
                "Tuỳ model (có model miễn phí)"));

        services.AddCompatible(
            new CompatibleVendor("zai", "z.ai", SettingKeys.ZaiApiKey, SettingKeys.ZaiBaseUrl,
                "https://api.z.ai/api/paas/v4", SettingKeys.ZaiModel, JsonMode.JsonObject, 16000, NoHeaders),
            new LlmVendorInfo("zai", "z.ai (GLM)", ProviderCategory.PaidCloud,
                "Model GLM của Zhipu (z.ai), viết tốt tiếng Trung và tiếng Anh, giá rẻ.",
                "Rẻ (~$0.02 / video)"));

        services.AddCompatible(
            new CompatibleVendor("kimi", "Kimi", SettingKeys.MoonshotApiKey, SettingKeys.MoonshotBaseUrl,
                "https://api.moonshot.ai/v1", SettingKeys.MoonshotModel, JsonMode.JsonObject, 16000, NoHeaders),
            new LlmVendorInfo("kimi", "Kimi (Moonshot)", ProviderCategory.PaidCloud,
                "Model Kimi của Moonshot AI, ngữ cảnh dài, viết kịch bản mạch lạc.",
                "Rẻ (~$0.02 / video)"));

        // MiniMax không có tài liệu xác nhận response_format nên chỉ mô tả schema trong prompt.
        services.AddCompatible(
            new CompatibleVendor("minimax", "MiniMax", SettingKeys.MiniMaxApiKey, SettingKeys.MiniMaxBaseUrl,
                "https://api.minimax.io/v1", SettingKeys.MiniMaxModel, JsonMode.None, 16000, NoHeaders),
            new LlmVendorInfo("minimax", "MiniMax", ProviderCategory.PaidCloud,
                "Model MiniMax M-series, giá rẻ. Phần suy nghĩ trong thẻ think được tự động loại bỏ.",
                "Rẻ (~$0.02 / video)"));

        services.AddCompatible(
            new CompatibleVendor("deepseek", "DeepSeek", SettingKeys.DeepSeekApiKey, null,
                "https://api.deepseek.com", SettingKeys.DeepSeekModel, JsonMode.JsonObject, 8192, NoHeaders),
            new LlmVendorInfo("deepseek", "DeepSeek", ProviderCategory.PaidCloud,
                "Model DeepSeek, chất lượng tốt với giá rất thấp.",
                "Rất rẻ (~$0.01 / video)"));

        services.AddCompatible(
            new CompatibleVendor("openai-compat", "Custom endpoint", SettingKeys.CustomLlmApiKey, SettingKeys.CustomLlmBaseUrl,
                "", SettingKeys.CustomLlmModel, JsonMode.JsonObject, null, NoHeaders,
                JsonModeSetting: SettingKeys.CustomLlmJsonMode, ApiKeyOptional: true),
            new LlmVendorInfo("openai-compat", "Custom endpoint (OpenAI-compatible)", ProviderCategory.FreeSelfHosted,
                "Kết nối bất kỳ server API kiểu OpenAI nào: LM Studio, vLLM, LiteLLM, llama.cpp...",
                "Miễn phí (chạy trên máy bạn)"));

        return services;
    }

    private static void AddCompatible(this IServiceCollection services, CompatibleVendor vendor, LlmVendorInfo info) =>
        services.AddLlmVendor(info, sp => new OpenAiCompatibleClient(vendor, sp.GetRequiredService<IHttpClientFactory>()));
}
