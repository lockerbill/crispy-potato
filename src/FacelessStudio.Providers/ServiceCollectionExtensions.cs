using FacelessStudio.Core.Abstractions;
using FacelessStudio.Providers.BuiltIn;
using FacelessStudio.Providers.Llm;
using FacelessStudio.Providers.Media;
using FacelessStudio.Providers.Render;
using FacelessStudio.Providers.Thumbnail;
using FacelessStudio.Providers.Timing;
using FacelessStudio.Providers.Tts;
using Microsoft.Extensions.DependencyInjection;

namespace FacelessStudio.Providers;

public static class ServiceCollectionExtensions
{
    /// <summary>Đăng ký tất cả provider. Thêm provider mới: tạo class rồi thêm một dòng ở đây.</summary>
    public static IServiceCollection AddFacelessProviders(this IServiceCollection services)
    {
        services.AddHttpClient("providers", c => c.Timeout = TimeSpan.FromMinutes(10));
        services.AddHttpClient("anthropic", c => c.Timeout = TimeSpan.FromMinutes(10));

        // Client dùng chung
        services.AddSingleton<GeminiClient>();
        services.AddSingleton<GroqClient>();
        services.AddSingleton<OllamaClient>();
        services.AddSingleton<ClaudeClient>();
        services.AddSingleton<GoogleTrendsClient>();

        // Chủ đề
        services.AddSingleton<IStepProvider, ManualTopicProvider>();
        services.AddSingleton<IStepProvider, GeminiTopicProvider>();
        services.AddSingleton<IStepProvider, GroqTopicProvider>();
        services.AddSingleton<IStepProvider, OllamaTopicProvider>();
        services.AddSingleton<IStepProvider, ClaudeTopicProvider>();
        // Kịch bản
        services.AddSingleton<IStepProvider, CustomScriptProvider>();
        services.AddSingleton<IStepProvider, GeminiScriptProvider>();
        services.AddSingleton<IStepProvider, GroqScriptProvider>();
        services.AddSingleton<IStepProvider, OllamaScriptProvider>();
        services.AddSingleton<IStepProvider, ClaudeScriptProvider>();
        // Giọng đọc
        services.AddSingleton<IStepProvider, EdgeTtsProvider>();
        services.AddSingleton<IStepProvider, PiperTtsProvider>();
        services.AddSingleton<IStepProvider, ElevenLabsTtsProvider>();
        // Hình ảnh / footage
        services.AddSingleton<IStepProvider, GradientMediaProvider>();
        services.AddSingleton<IStepProvider, PexelsMediaProvider>();
        services.AddSingleton<IStepProvider, PixabayMediaProvider>();
        services.AddSingleton<IStepProvider, ComfyUiMediaProvider>();
        services.AddSingleton<IStepProvider, OpenAiImageMediaProvider>();
        // Timing phụ đề
        services.AddSingleton<IStepProvider, TtsTimingProvider>();
        services.AddSingleton<IStepProvider, GroqWhisperTimingProvider>();
        services.AddSingleton<IStepProvider, WhisperNetTimingProvider>();
        services.AddSingleton<IStepProvider, OpenAiWhisperTimingProvider>();
        // Dựng video
        services.AddSingleton<IStepProvider, FfmpegRenderProvider>();
        services.AddSingleton<IStepProvider, ShotstackSandboxRenderProvider>();
        services.AddSingleton<IStepProvider, ShotstackProductionRenderProvider>();
        // Thumbnail
        services.AddSingleton<IStepProvider, LocalThumbnailProvider>();
        services.AddSingleton<IStepProvider, CloudflareThumbnailProvider>();
        services.AddSingleton<IStepProvider, OpenAiThumbnailProvider>();
        return services;
    }
}
