using System.Text.Json.Nodes;
using Studio.Domain;
using Studio.Services;

namespace Studio.Providers;

public sealed class ImageProvider(ProviderHttp http, AssetStore assets, MediaEngine media) : IStageProvider
{
    public bool Handles(string id) => id is "pexels" or "cloudflare" or "library" or "comfyui" or "openai-image";
    public bool CanResume(string checkpoint) => JsonNode.Parse(checkpoint)?["kind"]?.GetValue<string>() == "comfy";
    public async Task Cancel(ProviderContext c, CancellationToken ct)
    {
        if (c.Config.Id != "comfyui" || c.Checkpoint is null || !CanResume(c.Checkpoint)) return;
        var promptId = JsonNode.Parse(c.Checkpoint)?["promptId"]?.GetValue<string>();
        if (promptId is null) return;
        // Delete only this queued prompt. A global /interrupt could stop another client's active job.
        using var req = ProviderHttp.Request(HttpMethod.Post, ProviderHttp.LocalUrl(c.Config, "/queue"), new { delete = new[] { promptId } });
        using var response = await http.Send(req, ct);
    }
    public Task Validate(ProviderConfig c, Stage stage, CancellationToken ct)
    {
        if (c.Id == "comfyui") _ = ProviderHttp.LocalUrl(c, "/system_stats");
        if (c.Id == "cloudflare" && !System.Text.RegularExpressions.Regex.IsMatch(c.AccountId, "^[a-fA-F0-9]{32}$")) throw new InvalidOperationException("Cần Cloudflare Account ID (32 ký tự hex).");
        return Task.CompletedTask;
    }
    public async Task<string> CheckConnection(ProviderConfig c, string? key, CancellationToken ct)
    {
        await Validate(c, Stage.Media, ct);
        if (c.Id == "library") return "Thư viện dùng media đã tải lên từng dự án.";
        using var req = c.Id switch
        {
            "comfyui" => ProviderHttp.Request(HttpMethod.Get, ProviderHttp.LocalUrl(c, "/system_stats")),
            "pexels" => ProviderHttp.Request(HttpMethod.Get, "https://api.pexels.com/v1/curated?per_page=1"),
            "cloudflare" => ProviderHttp.Request(HttpMethod.Get, $"https://api.cloudflare.com/client/v4/accounts/{c.AccountId}/ai/models/search", bearer: key),
            _ => ProviderHttp.Request(HttpMethod.Get, "https://api.openai.com/v1/models", bearer: key)
        };
        if (c.Id == "pexels") req.Headers.TryAddWithoutValidation("Authorization", key);
        await http.Json(req, ct); return "Kết nối thành công; quyền tạo ảnh và quota cần xác minh khi chạy.";
    }
    public async Task<ProviderResult> Execute(ProviderContext c, CancellationToken ct)
    {
        var p = c.Project;
        if (c.Config.Id == "library")
        {
            foreach (var s in p.Scenes) { if (s.MediaAssetId is null) throw new InvalidOperationException("Chọn media đã tải lên cho từng cảnh."); await assets.Get(s.MediaAssetId, p.Id); }
            return new("Đã dùng media của từng cảnh.", p);
        }
        var count = c.Stage == Stage.Thumbnail ? 1 : p.Scenes.Count;
        var start = 0; string? promptId = null;
        if (c.Checkpoint is not null && CanResume(c.Checkpoint))
        {
            var saved = JsonNode.Parse(c.Checkpoint)!;
            start = saved["index"]!.GetValue<int>(); promptId = saved["promptId"]?.GetValue<string>();
            p = Json.Read<Project>(saved["project"]!.ToJsonString());
        }
        for (var i = start; i < count; i++)
        {
            var prompt = c.Stage == Stage.Thumbnail ? $"YouTube thumbnail background for: {p.Title}. {p.Topic}. Dramatic composition, clear subject, leave space for a title, no text or letters." : p.Scenes[i].VisualPrompt;
            var file = Path.Combine(c.WorkDirectory, $"image-{i}.png"); string type = "image/png"; string? credit = null;
            if (c.Config.Id == "pexels")
            {
                var videos = c.Model == "videos"; var orientation = p.Format == VideoFormat.Short ? "portrait" : "landscape";
                using var req = ProviderHttp.Request(HttpMethod.Get, $"https://api.pexels.com/{(videos ? "videos/search" : "v1/search")}?query={Uri.EscapeDataString(prompt)}&orientation={orientation}&per_page=1");
                req.Headers.TryAddWithoutValidation("Authorization", c.Secret);
                var data = await http.Json(req, ct);
                var hit = data[videos ? "videos" : "photos"]?.AsArray().FirstOrDefault() ?? throw new ProviderException("Không tìm thấy stock media. Đổi visual prompt hoặc provider.");
                string url;
                if (videos)
                {
                    var candidates = hit["video_files"]!.AsArray().Where(x => x?["file_type"]?.GetValue<string>() == "video/mp4").OrderByDescending(x => Math.Min(x?["width"]?.GetValue<int>() ?? 0, 1920));
                    url = ProviderHttp.Required(candidates.FirstOrDefault(), "link"); type = "video/mp4"; file = Path.ChangeExtension(file, ".mp4");
                    credit = "Pexels · " + hit["user"]?["name"]?.GetValue<string>() + " · " + hit["url"]?.GetValue<string>();
                }
                else
                {
                    url = ProviderHttp.Required(hit, "src", "large2x"); type = "image/jpeg"; file = Path.ChangeExtension(file, ".jpg");
                    credit = "Pexels · " + hit["photographer"]?.GetValue<string>() + " · " + hit["url"]?.GetValue<string>();
                }
                await http.Download(url, file, ct);
            }
            else if (c.Config.Id == "cloudflare")
            {
                using var req = ProviderHttp.Request(HttpMethod.Post, $"https://api.cloudflare.com/client/v4/accounts/{c.Config.AccountId}/ai/run/{c.Model.TrimStart('/')}", new { prompt, num_steps = 4 }, c.Secret);
                using var response = await http.Send(req, ct, true);
                if (response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.Ordinal) == true) await ProviderHttp.SaveBody(response, file, ct);
                else
                {
                    var data = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
                    var encoded = data?["result"]?["image"]?.GetValue<string>() ?? throw new ProviderException("Cloudflare thiếu dữ liệu ảnh.", true);
                    await File.WriteAllBytesAsync(file, Convert.FromBase64String(encoded), ct);
                }
            }
            else if (c.Config.Id == "openai-image")
            {
                using var req = ProviderHttp.Request(HttpMethod.Post, "https://api.openai.com/v1/images/generations", new { model = c.Model, prompt, n = 1, size = c.Stage == Stage.Thumbnail || p.Format == VideoFormat.Long ? "1536x1024" : "1024x1536", quality = "low" }, c.Secret);
                var data = await http.Json(req, ct, true); var hit = data["data"]?[0];
                if (hit?["b64_json"]?.GetValue<string>() is { } b64) await File.WriteAllBytesAsync(file, Convert.FromBase64String(b64), ct);
                else await http.Download(ProviderHttp.Required(hit, "url"), file, ct);
            }
            else
            {
                if (promptId is null)
                {
                    await c.SaveCheckpoint(Json.Write(new { kind = "unresolved", message = "ComfyUI submission in progress" }));
                    using var req = ProviderHttp.Request(HttpMethod.Post, ProviderHttp.LocalUrl(c.Config, "/prompt"), new { prompt = Workflow(prompt, c.Model, c.Stage == Stage.Thumbnail || p.Format == VideoFormat.Long), client_id = c.JobId.ToString("N") });
                    promptId = ProviderHttp.Required(await http.Json(req, ct), "prompt_id");
                    await c.SaveCheckpoint(Json.Write(new { kind = "comfy", index = i, promptId, project = p }));
                }
                JsonNode? image = null;
                for (var attempt = 0; attempt < 1800; attempt++)
                {
                    using var req = ProviderHttp.Request(HttpMethod.Get, ProviderHttp.LocalUrl(c.Config, "/history/" + Uri.EscapeDataString(promptId)));
                    var history = (await http.Json(req, ct))[promptId];
                    if (history?["status"]?["status_str"]?.GetValue<string>() == "error") throw new ProviderException("ComfyUI workflow thất bại; kiểm tra checkpoint và VRAM.");
                    image = history?["outputs"]?["9"]?["images"]?.AsArray().FirstOrDefault();
                    if (image is not null) break;
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
                if (image is null) throw new ProviderException("ComfyUI chưa hoàn tất. Có thể tiếp tục kiểm tra job đã gửi.");
                var path = "/view?filename=" + Uri.EscapeDataString(ProviderHttp.Required(image, "filename")) + "&subfolder=" + Uri.EscapeDataString(image["subfolder"]?.GetValue<string>() ?? "") + "&type=output";
                using var download = ProviderHttp.Request(HttpMethod.Get, ProviderHttp.LocalUrl(c.Config, path));
                using var result = await http.Send(download, ct); await ProviderHttp.SaveBody(result, file, ct);
                promptId = null;
            }
            await media.ValidateMedia(file, ct);
            if (c.Stage == Stage.Thumbnail)
            {
                var thumbnail = await media.Thumbnail(file, p.Title, c.WorkDirectory, ct);
                p.ThumbnailAssetId = (await assets.Add(p.Id, thumbnail, "thumbnail.jpg", "image/jpeg", ct)).Id;
            }
            else
            {
                var asset = await assets.Add(p.Id, file, $"scene-{i + 1}" + Path.GetExtension(file), type, ct, credit);
                p.Scenes[i].MediaAssetId = asset.Id; p.Scenes[i].Attribution = credit;
            }
            if (c.Config.Id == "comfyui") await c.SaveCheckpoint(Json.Write(new { kind = "comfy", index = i + 1, promptId = (string?)null, project = p }));
        }
        return new(Json.Write(new { count, thumbnail = p.ThumbnailAssetId }), p);
    }
    public static Dictionary<string, object> Workflow(string prompt, string checkpoint, bool landscape) => new()
    {
        ["3"] = new { class_type = "KSampler", inputs = new { seed = Random.Shared.NextInt64(1, long.MaxValue), steps = 20, cfg = 7, sampler_name = "euler", scheduler = "normal", denoise = 1, model = new object[] { "4", 0 }, positive = new object[] { "6", 0 }, negative = new object[] { "7", 0 }, latent_image = new object[] { "5", 0 } } },
        ["4"] = new { class_type = "CheckpointLoaderSimple", inputs = new { ckpt_name = checkpoint } },
        ["5"] = new { class_type = "EmptyLatentImage", inputs = new { width = landscape ? 1024 : 576, height = landscape ? 576 : 1024, batch_size = 1 } },
        ["6"] = new { class_type = "CLIPTextEncode", inputs = new { text = prompt, clip = new object[] { "4", 1 } } },
        ["7"] = new { class_type = "CLIPTextEncode", inputs = new { text = "text, watermark, blurry, low quality", clip = new object[] { "4", 1 } } },
        ["8"] = new { class_type = "VAEDecode", inputs = new { samples = new object[] { "3", 0 }, vae = new object[] { "4", 2 } } },
        ["9"] = new { class_type = "SaveImage", inputs = new { filename_prefix = "studio", images = new object[] { "8", 0 } } }
    };
}
