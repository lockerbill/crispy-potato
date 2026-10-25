using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Studio.Domain;
using Studio.Services;

namespace Studio.Providers;

public sealed class RenderProvider(ProviderHttp http, AssetStore assets, MediaEngine media, ProcessRunner process, IConfiguration config) : IStageProvider
{
    public bool Handles(string id) => id is "ffmpeg" or "shotstack" or "shotstack-sandbox";
    public bool CanResume(string checkpoint) => JsonNode.Parse(checkpoint)?["kind"]?.GetValue<string>() == "shotstack";
    public Task Validate(ProviderConfig config, Stage stage, CancellationToken ct) => Task.CompletedTask;
    public async Task<string> CheckConnection(ProviderConfig c, string? key, CancellationToken ct)
    {
        if (c.Id == "ffmpeg") { await process.Run(config["Studio:Ffmpeg"] ?? "ffmpeg", ["-version"], ct); return "FFmpeg có thể chạy. Render test kiểm tra codec, font và libass."; }
        // An intentionally nonexistent render ID performs a read-only authentication check.
        using var req = ProviderHttp.Request(HttpMethod.Get, Base(c.Id) + "/render/00000000-0000-0000-0000-000000000000"); req.Headers.Add("x-api-key", key);
        using var response = await http.Client.SendAsync(req, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new ProviderException("Shotstack từ chối API key hoặc môi trường.");
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound) throw new ProviderException($"Shotstack HTTP {(int)response.StatusCode}; chưa xác minh được kết nối.");
        return "Endpoint phản hồi; chỉ render thật mới xác minh đầy đủ quyền tài khoản.";
    }
    public async Task<ProviderResult> Execute(ProviderContext c, CancellationToken ct)
    {
        MediaEngine.ValidateTimeline(c.Project);
        string file;
        if (c.Config.Id == "ffmpeg") file = await media.Render(c.Project, c.WorkDirectory, ct);
        else
        {
            string? renderId = null;
            if (c.Checkpoint is not null && CanResume(c.Checkpoint)) renderId = JsonNode.Parse(c.Checkpoint)?["renderId"]?.GetValue<string>();
            if (renderId is null)
            {
                var urls = new Dictionary<string, string>();
                var videoDurations = new Dictionary<string, double>();
                foreach (var id in c.Project.Scenes.SelectMany(s => new[] { s.MediaAssetId!, s.AudioAssetId! }).Distinct()) urls[id] = await Ingest(c, id, ct);
                foreach (var id in c.Project.Scenes.Select(s => s.MediaAssetId!).Distinct())
                {
                    var asset = await assets.Get(id, c.Project.Id);
                    if (asset.ContentType.StartsWith("video/", StringComparison.Ordinal)) videoDurations[id] = await media.Duration(asset.Path, ct);
                }
                var body = Timeline(c.Project, urls, videoDurations.ContainsKey, id => videoDurations[id]);
                await c.SaveCheckpoint(Json.Write(new { kind = "unresolved", message = "Shotstack render submission in progress; check provider before retrying." }));
                using var req = ProviderHttp.Request(HttpMethod.Post, Base(c.Config.Id) + "/render", body); req.Headers.Add("x-api-key", c.Secret);
                renderId = ProviderHttp.Required(await http.Json(req, ct, true), "response", "id");
                await c.SaveCheckpoint(Json.Write(new { kind = "shotstack", renderId }));
            }
            string? url = null;
            for (var i = 0; i < 1440; i++)
            {
                using var req = ProviderHttp.Request(HttpMethod.Get, Base(c.Config.Id) + "/render/" + Uri.EscapeDataString(renderId)); req.Headers.Add("x-api-key", c.Secret);
                var response = (await http.Json(req, ct))["response"];
                var state = response?["status"]?.GetValue<string>();
                if (state == "failed") throw new ProviderException("Shotstack render thất bại. Kiểm tra job ID trong dashboard; có thể vẫn bị tính phí.", true);
                if (state == "done") { url = ProviderHttp.Required(response, "url"); break; }
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            if (url is null) throw new ProviderException("Shotstack vẫn đang chạy; tiếp tục kiểm tra job hiện có.", true);
            file = Path.Combine(c.WorkDirectory, "video.mp4"); await http.Download(url, file, ct, 4_000_000_000);
            var seconds = await media.Duration(file, ct);
            if (seconds > c.Project.MaxSeconds + 0.15) throw new ProviderException("Video cloud vượt giới hạn thời lượng.", true);
        }
        c.Project.VideoAssetId = (await assets.Add(c.Project.Id, file, "video.mp4", "video/mp4", ct)).Id;
        return new(Json.Write(new { assetId = c.Project.VideoAssetId, duration = await media.Duration(file, ct) }), c.Project);
    }
    private static string Base(string id) => "https://api.shotstack.io/edit/" + (id == "shotstack-sandbox" ? "stage" : "v1");
    private async Task<string> Ingest(ProviderContext c, string id, CancellationToken ct)
    {
        var asset = await assets.Get(id, c.Project.Id);
        var root = "https://api.shotstack.io/ingest/" + (c.Config.Id == "shotstack-sandbox" ? "stage" : "v1");
        using var create = ProviderHttp.Request(HttpMethod.Post, root + "/upload", new { }); create.Headers.Add("x-api-key", c.Secret);
        var data = (await http.Json(create, ct))["data"];
        var uploadUrl = ProviderHttp.Required(data, "attributes", "url"); var uploadId = ProviderHttp.Required(data, "id");
        if (!Uri.TryCreate(uploadUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.IsLoopback || uri.UserInfo.Length > 0) throw new ProviderException("Ingest trả upload URL không hợp lệ.");
        using var put = ProviderHttp.Request(HttpMethod.Put, uploadUrl); put.Content = new StreamContent(File.OpenRead(asset.Path)); put.Content.Headers.ContentType = new MediaTypeHeaderValue(asset.ContentType);
        using var uploaded = await http.Send(put, ct);
        for (var attempt = 0; attempt < 300; attempt++)
        {
            using var poll = ProviderHttp.Request(HttpMethod.Get, root + "/sources/" + Uri.EscapeDataString(uploadId)); poll.Headers.Add("x-api-key", c.Secret);
            var source = (await http.Json(poll, ct))["data"]?["attributes"];
            if (source?["status"]?.GetValue<string>() == "failed") throw new ProviderException("Shotstack ingest thất bại.");
            var sourceUrl = source?["source"]?.GetValue<string>();
            if (source?["status"]?.GetValue<string>() == "ready" && sourceUrl is not null) return sourceUrl;
            await Task.Delay(2000, ct);
        }
        throw new ProviderException("Media ingest chưa sẵn sàng. Chưa gửi yêu cầu render.");
    }
    public static object Timeline(Project p, IReadOnlyDictionary<string, string> urls, Func<string, bool> isVideo, Func<string, double>? videoDuration = null)
    {
        var images = new List<object>(); var audio = new List<object>(); var captions = new List<object>(); double start = 0;
        foreach (var s in p.Scenes)
        {
            var length = Math.Max(s.AudioSeconds, s.DurationSeconds);
            var asset = new Dictionary<string, object> { ["type"] = isVideo(s.MediaAssetId!) ? "video" : "image", ["src"] = urls[s.MediaAssetId!] };
            if (isVideo(s.MediaAssetId!)) asset["volume"] = 0;
            var sourceLength = isVideo(s.MediaAssetId!) && videoDuration is not null ? videoDuration(s.MediaAssetId!) : length;
            if (!double.IsFinite(sourceLength) || sourceLength <= 0) throw new InvalidOperationException("Invalid source clip duration.");
            for (double elapsed = 0; elapsed < length - 0.0001; elapsed += sourceLength)
            {
                var partLength = Math.Min(sourceLength, length - elapsed);
                var clip = new Dictionary<string, object> { ["asset"] = asset, ["start"] = start + elapsed, ["length"] = partLength, ["fit"] = "crop" };
                if (s.Transition == "fade")
                {
                    var transition = new Dictionary<string, string>();
                    if (elapsed == 0) transition["in"] = "fade";
                    if (elapsed + partLength >= length - .0001) transition["out"] = "fade";
                    if (transition.Count > 0) clip["transition"] = transition;
                }
                images.Add(clip);
            }
            audio.Add(new { asset = new { type = "audio", src = urls[s.AudioAssetId!] }, start, length = s.AudioSeconds });
            start += length;
        }
        foreach (var group in p.Captions.Chunk(p.Format == VideoFormat.Short ? 4 : 7))
        {
            for (var i = 0; i < group.Length; i++)
            {
                var w = group[i];
                var text = p.CaptionStyle == "word" ? WebUtility.HtmlEncode(w.Text) : string.Join(' ', group.Select((word, index) => index == i ? "<span>" + WebUtility.HtmlEncode(word.Text) + "</span>" : WebUtility.HtmlEncode(word.Text)));
                captions.Add(new { asset = new { type = "html", html = "<p>" + text + "</p>", css = "p { font-family: 'Open Sans'; font-size: 52px; color: white; text-align: center; background: #151515; padding: 12px; } span { color: #5cff80; }", width = p.Width - 140, height = 200, background = "transparent" }, start = w.Start, length = w.End - w.Start, position = "bottom", offset = new { y = p.Format == VideoFormat.Short ? 0.16 : 0.06 } });
            }
        }
        return new { timeline = new { background = "#000000", tracks = new object[] { new { clips = captions }, new { clips = images }, new { clips = audio } } }, output = new { format = "mp4", resolution = "1080", aspectRatio = p.Format == VideoFormat.Short ? "9:16" : "16:9", fps = 30 } };
    }
}
