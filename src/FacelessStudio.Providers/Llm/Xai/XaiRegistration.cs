using FacelessStudio.Core.Models;
using FacelessStudio.Providers.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace FacelessStudio.Providers.Llm.Xai;

public static class XaiRegistration
{
    /// <summary>xAI Grok (API key và đăng nhập SuperGrok).</summary>
    public static IServiceCollection AddXaiProviders(this IServiceCollection services)
    {
        services.AddSingleton<XaiAccount>();
        services.AddSingleton<IOAuthAccount>(sp => sp.GetRequiredService<XaiAccount>());

        services.AddLlmVendor(new LlmVendorInfo("xai", "xAI Grok", ProviderCategory.PaidCloud,
                "Grok của xAI qua API key (console.x.ai). Viết kịch bản tốt, gợi ý chủ đề có thể tìm web và X (Twitter) trực tiếp.",
                CostNote: "~$0.02–0.10 / video tuỳ model",
                TopicWebSearch: true, TopicName: "Grok + Web/X search"),
            sp => new XaiClient(sp.GetRequiredService<IHttpClientFactory>()));

        services.AddLlmVendor(new LlmVendorInfo("xai-oauth", "xAI Grok (SuperGrok)", ProviderCategory.PaidCloud,
                "Grok của xAI dùng tài khoản SuperGrok / X Premium+ đăng nhập trong Cài đặt, không cần API key.",
                CostNote: "Tính vào gói SuperGrok / X Premium+",
                TopicWebSearch: true, TopicName: "Grok SuperGrok + Web/X search"),
            sp => new XaiClient(sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<XaiAccount>(),
                sp.GetRequiredService<OAuthSession>()));
        return services;
    }
}
