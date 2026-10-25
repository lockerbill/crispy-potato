using Microsoft.Extensions.DependencyInjection;

namespace FacelessStudio.Providers.Llm.Codex;

public static class CodexRegistration
{
    /// <summary>ChatGPT / Codex (đăng nhập tài khoản ChatGPT).</summary>
    public static IServiceCollection AddCodexProviders(this IServiceCollection services)
    {
        return services;
    }
}
