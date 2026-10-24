using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Models;
using FacelessStudio.Core.Settings;

namespace FacelessStudio.Providers.Thumbnail;

/// <summary>Lấy khung hình từ footage làm nền rồi chèn chữ. Chạy hoàn toàn trên máy.</summary>
public sealed class LocalThumbnailProvider : ProviderBase, IThumbnailProvider
{
    public override string Id => "thumb-local";
    public override string Name => "Khung hình + chữ (trên máy)";
    public override PipelineStep Step => PipelineStep.Thumbnail;
    public override ProviderCategory Category => ProviderCategory.FreeSelfHosted;
    public override string Description => "Dùng một khung hình trong video làm nền, chèn tiêu đề chữ lớn màu vàng.";

    public Task CreateAsync(ThumbnailRequest request, StepContext context)
    {
        ThumbnailComposer.Compose(request.FramePath, ThumbnailText(request.Script), request.OutputPath,
            request.Width, request.Height, context.Settings.ResolvePath(SettingKeys.ThumbnailFontFile));
        return Task.CompletedTask;
    }

    public static string ThumbnailText(VideoScript script) =>
        string.IsNullOrWhiteSpace(script.ThumbnailText) ? script.Title : script.ThumbnailText;
}
