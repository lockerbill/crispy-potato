using FacelessStudio.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace FacelessStudio.Providers.Llm.ClaudeCli;

public static class ClaudeCliRegistration
{
    /// <summary>Claude Code CLI chính chủ (gói Claude Pro/Max).</summary>
    public static IServiceCollection AddClaudeCliProviders(this IServiceCollection services)
    {
        services.AddLlmVendor(new LlmVendorInfo("claude-cli", "Claude Code CLI (gói Pro/Max)", ProviderCategory.PaidCloud,
                "Dùng Claude Code chính chủ đã đăng nhập trên máy, tính vào hạn mức gói Claude Pro/Max. Cần cài Claude Code và chạy `claude login`.",
                CostNote: "Tính vào hạn mức gói Claude của anh"),
            _ => new ClaudeCliClient());
        return services;
    }
}
