using System.Text.Json.Nodes;
using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Models;
using FacelessStudio.Core.Settings;
using FacelessStudio.Providers.Http;

namespace FacelessStudio.Providers.Media;

/// <summary>Tạo ảnh AI cho từng cảnh; renderer sẽ thêm hiệu ứng Ken Burns. Mỗi cảnh một ảnh để tiết kiệm.</summary>
public abstract class AiImageMediaProvider : ProviderBase, IMediaProvider
{
    public override PipelineStep Step => PipelineStep.Media;

    protected abstract Task GenerateAsync(string prompt, bool vertical, string outputPath, StepContext context);

    public static string PromptFor(MediaRequest r, VideoRequest video)
    {
        var subject = string.IsNullOrWhiteSpace(r.Scene.ImagePrompt) ? r.Scene.FootageQuery : r.Scene.ImagePrompt;
        if (string.IsNullOrWhiteSpace(subject)) subject = video.Topic;
        return $"{subject}. Cinematic, photorealistic, high detail, natural lighting. No text, no watermark, no logo.";
    }

    public async Task<IReadOnlyList<MediaAsset>> GetAsync(MediaRequest request, StepContext context)
    {
        var path = Path.Combine(context.WorkDir, $"ai_{request.SceneIndex:000}.png");
        await GenerateAsync(PromptFor(request, context.Request), request.Vertical, path, context);
        return [new MediaAsset(MediaKind.Image, path, null, Name)];
    }
}

/// <summary>OpenAI Images (gpt-image-1).</summary>
public sealed class OpenAiImageMediaProvider(IHttpClientFactory httpFactory) : AiImageMediaProvider
{
    public override string Id => "media-openai";
    public override string Name => "OpenAI Images";
    public override ProviderCategory Category => ProviderCategory.PaidCloud;
    public override string Description => "Ảnh AI chất lượng cao cho từng cảnh (gpt-image-1), có hiệu ứng Ken Burns khi dựng.";
    public override string CostNote => "~$0.06 / ảnh (1 ảnh mỗi cảnh)";
    public override IReadOnlyList<string> RequiredSettings => [SettingKeys.OpenAiApiKey];

    public override double EstimateCostUsd(VideoRequest request) => Math.Round(request.LengthPreset.SceneCount * 0.063, 2);

    protected override Task GenerateAsync(string prompt, bool vertical, string outputPath, StepContext context) =>
        OpenAiImages.GenerateAsync(httpFactory.CreateClient("providers"), context, prompt,
            vertical ? "1024x1536" : "1536x1024", outputPath);
}

public static class OpenAiImages
{
    public static async Task GenerateAsync(HttpClient http, StepContext context, string prompt, string size, string outputPath)
    {
        var s = context.Settings;
        var body = new JsonObject
        {
            ["model"] = s.Get(SettingKeys.OpenAiImageModel),
            ["prompt"] = prompt,
            ["size"] = size,
            ["quality"] = "medium",
            ["n"] = 1,
        };
        var json = await HttpHelpers.PostJsonAsync(http, "OpenAI Images", "https://api.openai.com/v1/images/generations",
            body, context.CancellationToken, ("Authorization", "Bearer " + s.Get(SettingKeys.OpenAiApiKey)));
        var data = json["data"]?[0];
        if (data?["b64_json"]?.GetValue<string>() is { } b64)
            await File.WriteAllBytesAsync(outputPath, Convert.FromBase64String(b64), context.CancellationToken);
        else if (data?["url"]?.GetValue<string>() is { } url)
            await HttpHelpers.DownloadAsync(http, "OpenAI Images", url, outputPath, context.CancellationToken);
        else
            throw new ProviderException("OpenAI Images không trả về ảnh.");
    }
}

/// <summary>ComfyUI chạy trên máy: gửi workflow (API format), chờ xong rồi tải ảnh về.</summary>
public sealed class ComfyUiMediaProvider(IHttpClientFactory httpFactory) : AiImageMediaProvider
{
    public override string Id => "media-comfyui";
    public override string Name => "ComfyUI (trên máy)";
    public override ProviderCategory Category => ProviderCategory.FreeSelfHosted;
    public override string Description =>
        "Tạo ảnh bằng Stable Diffusion/Flux trên máy anh qua ComfyUI. Cần GPU. Dùng workflow SDXL mặc định hoặc workflow riêng.";
    public override IReadOnlyList<string> RequiredSettings => [SettingKeys.ComfyUiUrl];

    // Workflow SDXL tối giản (API format). Workflow riêng dùng các placeholder giống hệt.
    private const string DefaultWorkflow = """
        {
          "3": {"class_type": "KSampler", "inputs": {"seed": {{SEED}}, "steps": 25, "cfg": 7, "sampler_name": "dpmpp_2m",
                "scheduler": "karras", "denoise": 1, "model": ["4", 0], "positive": ["6", 0], "negative": ["7", 0],
                "latent_image": ["5", 0]}},
          "4": {"class_type": "CheckpointLoaderSimple", "inputs": {"ckpt_name": "{{CHECKPOINT}}"}},
          "5": {"class_type": "EmptyLatentImage", "inputs": {"width": {{WIDTH}}, "height": {{HEIGHT}}, "batch_size": 1}},
          "6": {"class_type": "CLIPTextEncode", "inputs": {"text": "{{PROMPT}}", "clip": ["4", 1]}},
          "7": {"class_type": "CLIPTextEncode", "inputs": {"text": "text, watermark, logo, blurry, deformed, low quality", "clip": ["4", 1]}},
          "8": {"class_type": "VAEDecode", "inputs": {"samples": ["3", 0], "vae": ["4", 2]}},
          "9": {"class_type": "SaveImage", "inputs": {"filename_prefix": "faceless", "images": ["8", 0]}}
        }
        """;

    public static string BuildWorkflow(string template, string prompt, string checkpoint, int width, int height, long seed)
    {
        // Escape prompt để chèn an toàn vào chuỗi JSON.
        var escapedPrompt = JsonValue.Create(prompt)!.ToJsonString()[1..^1];
        var escapedCkpt = JsonValue.Create(checkpoint)!.ToJsonString()[1..^1];
        return template.Replace("{{PROMPT}}", escapedPrompt).Replace("{{CHECKPOINT}}", escapedCkpt)
            .Replace("{{WIDTH}}", width.ToString()).Replace("{{HEIGHT}}", height.ToString())
            .Replace("{{SEED}}", seed.ToString());
    }

    protected override async Task GenerateAsync(string prompt, bool vertical, string outputPath, StepContext context)
    {
        var s = context.Settings;
        var http = httpFactory.CreateClient("providers");
        var baseUrl = s.Get(SettingKeys.ComfyUiUrl).TrimEnd('/');
        var templatePath = s.ResolvePath(SettingKeys.ComfyUiWorkflow);
        var template = !string.IsNullOrEmpty(templatePath) && File.Exists(templatePath)
            ? await File.ReadAllTextAsync(templatePath, context.CancellationToken)
            : DefaultWorkflow;
        var (w, h) = vertical ? (768, 1344) : (1344, 768);
        var workflow = JsonNode.Parse(BuildWorkflow(template, prompt, s.Get(SettingKeys.ComfyUiCheckpoint), w, h,
            Random.Shared.NextInt64(1, 1_000_000_000)))!;

        var queued = await HttpHelpers.PostJsonAsync(http, Name, $"{baseUrl}/prompt",
            new JsonObject { ["prompt"] = workflow, ["client_id"] = "faceless-studio" }, context.CancellationToken);
        var promptId = queued["prompt_id"]?.GetValue<string>()
                       ?? throw new ProviderException($"ComfyUI không nhận workflow: {queued.ToJsonString()}");

        var deadline = DateTime.UtcNow.AddMinutes(10);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), context.CancellationToken);
            var history = await HttpHelpers.GetJsonAsync(http, Name, $"{baseUrl}/history/{promptId}", context.CancellationToken);
            var outputs = history[promptId]?["outputs"]?.AsObject();
            var image = outputs?.Select(o => o.Value?["images"]?[0]).FirstOrDefault(i => i != null);
            if (image == null) continue;
            var query = $"filename={Uri.EscapeDataString(image["filename"]!.GetValue<string>())}" +
                        $"&subfolder={Uri.EscapeDataString(image["subfolder"]?.GetValue<string>() ?? "")}" +
                        $"&type={Uri.EscapeDataString(image["type"]?.GetValue<string>() ?? "output")}";
            await HttpHelpers.DownloadAsync(http, Name, $"{baseUrl}/view?{query}", outputPath, context.CancellationToken);
            return;
        }
        throw new ProviderException("ComfyUI tạo ảnh quá 10 phút, đã dừng.");
    }
}
