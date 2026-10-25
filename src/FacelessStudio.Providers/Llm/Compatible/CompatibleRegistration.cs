using Microsoft.Extensions.DependencyInjection;

namespace FacelessStudio.Providers.Llm.Compatible;

public static class CompatibleRegistration
{
    /// <summary>OpenRouter, z.ai/GLM, Kimi, MiniMax, DeepSeek, Custom endpoint (API kiểu OpenAI).</summary>
    public static IServiceCollection AddCompatibleLlmProviders(this IServiceCollection services)
    {
        return services;
    }
}
