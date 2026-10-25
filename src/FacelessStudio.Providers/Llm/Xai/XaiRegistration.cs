using Microsoft.Extensions.DependencyInjection;

namespace FacelessStudio.Providers.Llm.Xai;

public static class XaiRegistration
{
    /// <summary>xAI Grok (API key và đăng nhập SuperGrok).</summary>
    public static IServiceCollection AddXaiProviders(this IServiceCollection services)
    {
        return services;
    }
}
