using FacelessStudio.Core.Abstractions;
using FacelessStudio.Providers.BuiltIn;
using FacelessStudio.Providers.Render;
using FacelessStudio.Providers.Thumbnail;
using FacelessStudio.Providers.Tts;
using Microsoft.Extensions.DependencyInjection;

namespace FacelessStudio.Providers;

public static class ServiceCollectionExtensions
{
    /// <summary>Đăng ký tất cả provider. Thêm provider mới: tạo class rồi thêm một dòng ở đây.</summary>
    public static IServiceCollection AddFacelessProviders(this IServiceCollection services)
    {
        services.AddHttpClient("providers", c => c.Timeout = TimeSpan.FromMinutes(10));

        // Chủ đề
        services.AddSingleton<IStepProvider, ManualTopicProvider>();
        // Kịch bản
        services.AddSingleton<IStepProvider, CustomScriptProvider>();
        // Giọng đọc
        services.AddSingleton<IStepProvider, EdgeTtsProvider>();
        // Hình ảnh / footage
        services.AddSingleton<IStepProvider, GradientMediaProvider>();
        // Timing phụ đề
        services.AddSingleton<IStepProvider, TtsTimingProvider>();
        // Dựng video
        services.AddSingleton<IStepProvider, FfmpegRenderProvider>();
        // Thumbnail
        services.AddSingleton<IStepProvider, LocalThumbnailProvider>();
        return services;
    }
}
