using System.Text.Json.Nodes;
using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Models;
using FacelessStudio.Core.Settings;
using FacelessStudio.Providers.Http;

namespace FacelessStudio.Providers.Render;

/// <summary>
/// Dựng video trên cloud Shotstack. File local (giọng đọc, ảnh AI, phụ đề, nhạc) được upload qua Ingest API;
/// footage stock dùng thẳng URL công khai. Phụ đề hiện theo cụm chữ (caption asset), không tô màu từng chữ như FFmpeg.
/// </summary>
public abstract class ShotstackRenderProvider(IHttpClientFactory httpFactory) : ProviderBase, IRenderProvider
{
    public override PipelineStep Step => PipelineStep.Render;
    protected abstract string Stage { get; }
    protected abstract string ApiKeySetting { get; }
    public override IReadOnlyList<string> RequiredSettings => [ApiKeySetting];

    public static TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    private HttpClient Http => httpFactory.CreateClient("providers");

    public async Task RenderAsync(RenderPlan plan, StepContext context)
    {
        var key = context.Settings.Get(ApiKeySetting);
        var ct = context.CancellationToken;

        // Giọng đọc: nén mp3 cho nhẹ rồi upload.
        var narrationMp3 = Path.Combine(plan.WorkDir, "narration.mp3");
        await context.Ffmpeg.RunAsync(["-i", plan.NarrationPath, "-b:a", "192k", narrationMp3], ct: ct);
        context.Log("Shotstack: đang upload giọng đọc, phụ đề và media...");
        var narrationUrl = await UploadAsync(narrationMp3, key, ct);
        var srtUrl = await UploadAsync(plan.SrtPath, key, ct);
        var musicUrl = plan.MusicPath != null ? await UploadAsync(plan.MusicPath, key, ct) : null;

        var mediaUrls = new Dictionary<string, string>();
        foreach (var m in plan.Scenes.SelectMany(s => s.Media))
        {
            if (m.Kind == MediaKind.Gradient || m.PublicUrl != null || m.LocalPath == null || mediaUrls.ContainsKey(m.LocalPath))
                continue;
            mediaUrls[m.LocalPath] = await UploadAsync(m.LocalPath, key, ct);
        }

        var clipLengths = new Dictionary<string, double>();
        foreach (var m in plan.Scenes.SelectMany(s => s.Media).Where(m => m.Kind == MediaKind.Video && m.LocalPath != null))
            clipLengths.TryAdd(m.LocalPath!, await context.Ffmpeg.ProbeDurationAsync(m.LocalPath!, ct));

        var edit = BuildEdit(plan, narrationUrl, srtUrl, musicUrl,
            m => m.PublicUrl ?? (m.LocalPath != null ? mediaUrls.GetValueOrDefault(m.LocalPath) : null),
            path => clipLengths.GetValueOrDefault(path));
        var queued = await HttpHelpers.PostJsonAsync(Http, Name, $"https://api.shotstack.io/edit/{Stage}/render", edit, ct,
            ("x-api-key", key));
        var renderId = queued["response"]?["id"]?.GetValue<string>()
                       ?? throw new ProviderException($"Shotstack không nhận yêu cầu dựng: {queued.ToJsonString()}");
        context.Log($"Shotstack đang dựng (id {renderId})...");

        var deadline = DateTime.UtcNow.AddMinutes(30);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval, ct);
            var status = await HttpHelpers.GetJsonAsync(Http, Name, $"https://api.shotstack.io/edit/{Stage}/render/{renderId}",
                ct, ("x-api-key", key));
            var state = status["response"]?["status"]?.GetValue<string>();
            if (state == "failed")
                throw new ProviderException($"Shotstack dựng lỗi: {status["response"]?["error"]?.ToJsonString()}");
            if (state != "done") continue;
            var url = status["response"]?["url"]?.GetValue<string>()
                      ?? throw new ProviderException("Shotstack báo xong nhưng không có link video.");
            await HttpHelpers.DownloadAsync(Http, Name, url, plan.OutputPath, ct);
            return;
        }
        throw new ProviderException("Shotstack dựng quá 30 phút, đã dừng.");
    }

    /// <summary>Tạo JSON "edit" cho Shotstack: track trên cùng là phụ đề, rồi hình, cuối là giọng đọc.</summary>
    public static JsonObject BuildEdit(RenderPlan plan, string narrationUrl, string srtUrl, string? musicUrl,
        Func<MediaAsset, string?> urlOf, Func<string, double> clipLength)
    {
        var visual = new JsonArray();
        foreach (var scene in plan.Scenes)
        {
            var part = scene.Duration / scene.Media.Count;
            for (var j = 0; j < scene.Media.Count; j++)
            {
                var m = scene.Media[j];
                var src = urlOf(m);
                var start = scene.Start + j * part;
                if (m.Kind == MediaKind.Gradient || src == null) continue; // để lộ màu nền của timeline
                if (m.Kind == MediaKind.Image)
                {
                    visual.Add(Clip(new JsonObject { ["type"] = "image", ["src"] = src }, start, part,
                        effect: (scene.Index + j) % 2 == 0 ? "zoomIn" : "zoomOut"));
                    continue;
                }
                // Clip ngắn hơn đoạn cần thì lặp lại cho kín.
                var len = m.LocalPath != null ? clipLength(m.LocalPath) : 0;
                var remaining = part;
                var t = start;
                while (remaining > 0.01)
                {
                    var piece = len > 0.5 ? Math.Min(remaining, len) : remaining;
                    visual.Add(Clip(new JsonObject { ["type"] = "video", ["src"] = src, ["volume"] = 0 }, t, piece));
                    t += piece;
                    remaining -= piece;
                }
            }
        }

        var size = plan.Vertical ? 56 : 44;
        var captions = new JsonArray(Clip(new JsonObject
        {
            ["type"] = "caption",
            ["src"] = srtUrl,
            ["font"] = new JsonObject { ["family"] = "Montserrat ExtraBold", ["color"] = "#ffffff", ["size"] = size, ["lineHeight"] = 1.1 },
            ["background"] = new JsonObject { ["color"] = "#000000", ["opacity"] = 0.45, ["padding"] = 18, ["borderRadius"] = 12 },
            ["margin"] = new JsonObject { ["bottom"] = plan.Vertical ? 0.25 : 0.08 },
        }, 0, plan.Duration));
        var audio = new JsonArray(Clip(new JsonObject { ["type"] = "audio", ["src"] = narrationUrl }, 0, plan.Duration));

        var timeline = new JsonObject
        {
            ["background"] = "#141e30",
            ["tracks"] = new JsonArray(
                new JsonObject { ["clips"] = captions },
                new JsonObject { ["clips"] = visual },
                new JsonObject { ["clips"] = audio }),
        };
        if (musicUrl != null)
            timeline["soundtrack"] = new JsonObject { ["src"] = musicUrl, ["effect"] = "fadeOut", ["volume"] = 0.12 };

        return new JsonObject
        {
            ["timeline"] = timeline,
            ["output"] = new JsonObject
            {
                ["format"] = "mp4",
                ["fps"] = plan.Fps,
                ["size"] = new JsonObject { ["width"] = plan.Width, ["height"] = plan.Height },
            },
        };
    }

    private static JsonObject Clip(JsonObject asset, double start, double length, string? effect = null)
    {
        var clip = new JsonObject
        {
            ["asset"] = asset,
            ["start"] = Math.Round(start, 3),
            ["length"] = Math.Round(length, 3),
            ["fit"] = "cover",
        };
        if (effect != null) clip["effect"] = effect;
        return clip;
    }

    /// <summary>Upload file qua Ingest API: xin URL ký sẵn, PUT file lên, chờ Shotstack xử lý xong.</summary>
    private async Task<string> UploadAsync(string path, string key, CancellationToken ct)
    {
        var signed = await HttpHelpers.PostJsonAsync(Http, Name, $"https://api.shotstack.io/ingest/{Stage}/upload",
            new JsonObject(), ct, ("x-api-key", key));
        var id = signed["data"]?["id"]?.GetValue<string>();
        var uploadUrl = signed["data"]?["attributes"]?["url"]?.GetValue<string>();
        if (id == null || uploadUrl == null) throw new ProviderException("Shotstack Ingest không trả về URL upload.");

        using (var put = new HttpRequestMessage(HttpMethod.Put, uploadUrl))
        {
            put.Content = new StreamContent(File.OpenRead(path));
            // URL ký sẵn của S3 không chấp nhận Content-Type khác lúc ký.
            put.Content.Headers.ContentType = null;
            using var _ = await HttpHelpers.SendAsync(Http, Name, put, ct);
        }

        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (DateTime.UtcNow < deadline)
        {
            var source = await HttpHelpers.GetJsonAsync(Http, Name, $"https://api.shotstack.io/ingest/{Stage}/sources/{id}", ct,
                ("x-api-key", key));
            var attrs = source["data"]?["attributes"];
            var status = attrs?["status"]?.GetValue<string>();
            if (status == "failed") throw new ProviderException($"Shotstack không xử lý được file {Path.GetFileName(path)}.");
            if (status == "ready" && attrs?["source"]?.GetValue<string>() is { } url) return url;
            await Task.Delay(PollInterval, ct);
        }
        throw new ProviderException($"Shotstack xử lý file {Path.GetFileName(path)} quá lâu.");
    }
}

public sealed class ShotstackSandboxRenderProvider(IHttpClientFactory httpFactory) : ShotstackRenderProvider(httpFactory)
{
    public override string Id => "render-shotstack-sandbox";
    public override string Name => "Shotstack Sandbox";
    public override ProviderCategory Category => ProviderCategory.FreeCloud;
    public override string Description =>
        "Dựng trên cloud miễn phí nhưng video có watermark Shotstack. Phụ đề hiện theo cụm chữ (không tô màu từng chữ).";
    public override string CostNote => "Miễn phí, có watermark";
    protected override string Stage => "stage";
    protected override string ApiKeySetting => SettingKeys.ShotstackSandboxKey;
}

public sealed class ShotstackProductionRenderProvider(IHttpClientFactory httpFactory) : ShotstackRenderProvider(httpFactory)
{
    public override string Id => "render-shotstack";
    public override string Name => "Shotstack";
    public override ProviderCategory Category => ProviderCategory.PaidCloud;
    public override string Description =>
        "Dựng trên cloud, không cần máy mạnh, không watermark. Phụ đề hiện theo cụm chữ (không tô màu từng chữ).";
    public override string CostNote => "~$0.20–0.40 / phút video";
    protected override string Stage => "v1";
    protected override string ApiKeySetting => SettingKeys.ShotstackProductionKey;

    public override double EstimateCostUsd(VideoRequest request) => Math.Round(request.LengthPreset.Seconds / 60.0 * 0.4, 2);
}
