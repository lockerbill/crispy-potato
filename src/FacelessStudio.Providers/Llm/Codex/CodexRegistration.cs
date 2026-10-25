using FacelessStudio.Core.Models;
using FacelessStudio.Providers.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace FacelessStudio.Providers.Llm.Codex;

public static class CodexRegistration
{
    /// <summary>ChatGPT / Codex (đăng nhập tài khoản ChatGPT).</summary>
    public static IServiceCollection AddCodexProviders(this IServiceCollection services)
    {
        services.AddSingleton<CodexAccount>();
        services.AddSingleton<IOAuthAccount>(sp => sp.GetRequiredService<CodexAccount>());
        services.AddLlmVendor(new LlmVendorInfo("codex", "ChatGPT / Codex (gói ChatGPT)", ProviderCategory.PaidCloud,
                "Dùng gói ChatGPT Plus/Pro của bạn với model Codex/GPT-5, đăng nhập trong Cài đặt → Tài khoản đăng nhập.",
                CostNote: "Tính vào gói ChatGPT, không trả theo token"),
            sp => new CodexClient(sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<OAuthSession>(),
                sp.GetRequiredService<CodexAccount>()));
        return services;
    }
}
