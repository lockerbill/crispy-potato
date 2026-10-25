using Microsoft.Extensions.DependencyInjection;

namespace FacelessStudio.Providers.Llm.ClaudeCli;

public static class ClaudeCliRegistration
{
    /// <summary>Claude Code CLI chính chủ (gói Claude Pro/Max).</summary>
    public static IServiceCollection AddClaudeCliProviders(this IServiceCollection services)
    {
        return services;
    }
}
