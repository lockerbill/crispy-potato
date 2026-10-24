using System.Text.Json.Nodes;
using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Models;
using FacelessStudio.Core.Settings;
using FacelessStudio.Providers.Http;
using FacelessStudio.Providers.Media;

namespace FacelessStudio.Providers.Thumbnail;

/// <summary>Tạo ảnh nền bằng AI rồi chèn chữ tiêu đề lên trên (chữ luôn vẽ bằng SkiaSharp cho đúng dấu tiếng Việt).</summary>
public abstract class AiThumbnailProvider : ProviderBase, IThumbnailProvider
{
    public override PipelineStep Step => PipelineStep.Thumbnail;

    protected abstract Task GenerateBackgroundAsync(string prompt, string outputPath, StepContext context);

    public static string BackgroundPrompt(VideoScript script, VideoRequest request)
    {
        var scene = script.Scenes.FirstOrDefault();
        var subject = scene?.ImagePrompt is { Length: > 0 } p ? p : scene?.FootageQuery ?? request.Topic;
        return $"Eye-catching YouTube thumbnail background: {subject}. Dramatic lighting, vivid colors, high contrast, " +
               "simple composition with empty space in the center. No text, no letters, no watermark.";
    }

    public async Task CreateAsync(ThumbnailRequest request, StepContext context)
    {
        var background = Path.Combine(context.WorkDir, "thumb_bg.png");
        try
        {
            await GenerateBackgroundAsync(BackgroundPrompt(request.Script, context.Request), background, context);
        }
        catch (ProviderException e)
        {
            // Không làm hỏng cả video chỉ vì thumbnail: dùng khung hình thay thế.
            context.Log($"{Name} lỗi ({e.Message}), dùng khung hình trong video làm nền thumbnail.");
            background = request.FramePath ?? "";
        }
        ThumbnailComposer.Compose(background, LocalThumbnailProvider.ThumbnailText(request.Script), request.OutputPath,
            request.Width, request.Height, context.Settings.ResolvePath(SettingKeys.ThumbnailFontFile));
    }
}

/// <summary>Cloudflare Workers AI (Flux schnell), gói miễn phí mỗi ngày.</summary>
public sealed class CloudflareThumbnailProvider(IHttpClientFactory httpFactory) : AiThumbnailProvider
{
    public override string Id => "thumb-cloudflare";
    public override string Name => "Cloudflare Workers AI (Flux)";
    public override ProviderCategory Category => ProviderCategory.FreeCloud;
    public override string Description => "Ảnh nền AI bằng Flux schnell trên Cloudflare, miễn phí trong hạn mức hằng ngày.";
    public override IReadOnlyList<string> RequiredSettings => [SettingKeys.CloudflareAccountId, SettingKeys.CloudflareApiToken];

    protected override async Task GenerateBackgroundAsync(string prompt, string outputPath, StepContext context)
    {
        var s = context.Settings;
        var url = $"https://api.cloudflare.com/client/v4/accounts/{s.Get(SettingKeys.CloudflareAccountId)}/ai/run/" +
                  s.Get(SettingKeys.CloudflareImageModel);
        var json = await HttpHelpers.PostJsonAsync(httpFactory.CreateClient("providers"), Name, url,
            new JsonObject { ["prompt"] = prompt, ["steps"] = 4 }, context.CancellationToken,
            ("Authorization", "Bearer " + s.Get(SettingKeys.CloudflareApiToken)));
        var image = json["result"]?["image"]?.GetValue<string>()
                    ?? throw new ProviderException($"Cloudflare không trả về ảnh: {json["errors"]?.ToJsonString()}");
        await File.WriteAllBytesAsync(outputPath, Convert.FromBase64String(image), context.CancellationToken);
    }
}

public sealed class OpenAiThumbnailProvider(IHttpClientFactory httpFactory) : AiThumbnailProvider
{
    public override string Id => "thumb-openai";
    public override string Name => "OpenAI Images";
    public override ProviderCategory Category => ProviderCategory.PaidCloud;
    public override string Description => "Ảnh nền AI chất lượng cao (gpt-image-1) rồi chèn tiêu đề.";
    public override string CostNote => "~$0.06 / thumbnail";
    public override IReadOnlyList<string> RequiredSettings => [SettingKeys.OpenAiApiKey];
    public override double EstimateCostUsd(VideoRequest request) => 0.063;

    protected override Task GenerateBackgroundAsync(string prompt, string outputPath, StepContext context) =>
        OpenAiImages.GenerateAsync(httpFactory.CreateClient("providers"), context, prompt, "1536x1024", outputPath);
}
