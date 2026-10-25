using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Studio.Data;
using Studio.Domain;
using Studio.Providers;

namespace Studio.Services;

public class YouTubeConfig
{
    public string ClientId { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public string ChannelName { get; set; } = "";
}
public record YouTubeTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);
public record UploadOptions(string Privacy, bool MadeForKids, string ChannelId);
public record OAuthPending(DateTimeOffset ExpiresAt);

public sealed class YouTubeService(SettingsService settings, ProviderHttp http, IConfiguration configuration, IDbContextFactory<StudioDb> factory, ProjectService projects, AssetStore assets)
{
    private readonly SemaphoreSlim tokenLock = new(1, 1);
    public string RedirectUri => (configuration["Studio:PublicUrl"] ?? "http://localhost:5080").TrimEnd('/') + "/youtube/callback";
    public async Task<(string Url, string State)> Begin()
    {
        var cfg = await settings.Get<YouTubeConfig>("youtube:config") ?? throw new InvalidOperationException("Cấu hình Google OAuth trước.");
        if (string.IsNullOrWhiteSpace(cfg.ClientId) || await settings.Secret("youtube:config") is null) throw new InvalidOperationException("Thiếu Google OAuth client ID/secret.");
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); var verifier = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await settings.Save("youtube:state:" + state, new OAuthPending(DateTimeOffset.UtcNow.AddMinutes(10)), verifier);
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var url = "https://accounts.google.com/o/oauth2/v2/auth?" + Query(new Dictionary<string, string> { ["client_id"] = cfg.ClientId, ["redirect_uri"] = RedirectUri, ["response_type"] = "code", ["scope"] = "https://www.googleapis.com/auth/youtube.upload https://www.googleapis.com/auth/youtube.readonly", ["access_type"] = "offline", ["prompt"] = "consent", ["state"] = state, ["code_challenge"] = challenge, ["code_challenge_method"] = "S256" });
        return (url, state);
    }
    public async Task Complete(string state, string code, CancellationToken ct)
    {
        var rowKey = "youtube:state:" + state;
        var pending = await settings.Get<OAuthPending>(rowKey); var verifier = await settings.Secret(rowKey);
        if (pending is null || pending.ExpiresAt < DateTimeOffset.UtcNow || verifier is null) throw new InvalidOperationException("OAuth state đã hết hạn hoặc đã sử dụng.");
        await settings.Save(rowKey, new OAuthPending(DateTimeOffset.MinValue), clearSecret: true);
        var cfg = await settings.Get<YouTubeConfig>("youtube:config") ?? throw new InvalidOperationException("Thiếu OAuth config.");
        var tokens = await Exchange(new() { ["client_id"] = cfg.ClientId, ["client_secret"] = (await settings.Secret("youtube:config"))!, ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = RedirectUri, ["grant_type"] = "authorization_code" }, ct);
        if (tokens.RefreshToken.Length == 0) throw new InvalidOperationException("Google không cấp refresh token. Kết nối lại với consent.");
        await settings.Save("youtube:tokens", new { connected = true }, Json.Write(tokens));
    }
    public async Task<List<(string Id, string Title)>> Channels(CancellationToken ct)
    {
        using var req = ProviderHttp.Request(HttpMethod.Get, "https://www.googleapis.com/youtube/v3/channels?part=snippet&mine=true", bearer: await AccessToken(ct));
        var data = await http.Json(req, ct);
        return (data["items"]?.AsArray() ?? []).Select(x => (ProviderHttp.Required(x, "id"), ProviderHttp.Required(x, "snippet", "title"))).ToList();
    }
    public async Task SelectChannel(string channelId, CancellationToken ct)
    {
        var channels = await Channels(ct); var selected = channels.SingleOrDefault(x => x.Id == channelId);
        if (selected.Id is null) throw new InvalidOperationException("Kênh không thuộc phiên OAuth hiện tại.");
        var cfg = await settings.Get<YouTubeConfig>("youtube:config") ?? throw new InvalidOperationException();
        cfg.ChannelId = selected.Id; cfg.ChannelName = selected.Title; await settings.Save("youtube:config", cfg);
    }
    public async Task<Guid> Queue(Guid projectId, string privacy, bool madeForKids, bool explicitlyApproved, CancellationToken ct)
    {
        if (!explicitlyApproved) throw new InvalidOperationException("Cần duyệt video trước khi đăng.");
        if (privacy is not ("private" or "unlisted" or "public")) throw new InvalidOperationException("Privacy không hợp lệ.");
        var cfg = await settings.Get<YouTubeConfig>("youtube:config") ?? throw new InvalidOperationException("Chưa kết nối YouTube.");
        if (!(await Channels(ct)).Any(x => x.Id == cfg.ChannelId)) throw new InvalidOperationException("Chọn kênh hợp lệ trong Settings.");
        await projects.Gate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct); await ProjectService.EnsureIdle(db, projectId);
            var row = await db.Projects.FindAsync([projectId], ct) ?? throw new InvalidOperationException("Dự án không tồn tại."); var p = Json.Read<Project>(row.Json);
            if (p.YoutubeVideoId is not null) throw new InvalidOperationException("Dự án đã đăng YouTube; không tự upload lại.");
            if (await db.Jobs.AnyAsync(j => j.ProjectId == projectId && j.Stage == "YouTube" && j.Status == "NeedsAttention", ct)) throw new InvalidOperationException("Có upload chưa rõ kết quả; tiếp tục hoặc đối soát upload đó trước.");
            if (new[] { Stage.Render, Stage.Thumbnail, Stage.Title, Stage.Description, Stage.Tags }.Any(s => p.Steps[s].Status != StepStatus.Approved)) throw new InvalidOperationException("Duyệt video, thumbnail và metadata trước khi đăng.");
            await assets.Get(p.VideoAssetId ?? throw new InvalidOperationException("Thiếu video."), p.Id);
            if (p.Title.Length is < 1 or > 100 || p.Description.Length > 5000) throw new InvalidOperationException("Metadata vượt giới hạn YouTube.");
            var job = new JobRow { ProjectId = p.Id, Stage = "YouTube", ProviderId = "youtube", RequestJson = Json.Write(p), ConfigJson = Json.Write(new UploadOptions(privacy, madeForKids, cfg.ChannelId)) };
            db.Jobs.Add(job); await db.SaveChangesAsync(ct); return job.Id;
        }
        finally { projects.Gate.Release(); }
    }
    public async Task<Project> Upload(JobRow job, Func<string, Task> checkpoint, CancellationToken ct)
    {
        var p = Json.Read<Project>(job.RequestJson); var options = Json.Read<UploadOptions>(job.ConfigJson);
        if (!(await Channels(ct)).Any(x => x.Id == options.ChannelId)) throw new InvalidOperationException("Phiên OAuth không còn đúng kênh đã duyệt.");
        var asset = await assets.Get(p.VideoAssetId!, p.Id);
        var existing = job.Checkpoint is null ? null : JsonNode.Parse(job.Checkpoint);
        if (existing?["videoId"]?.GetValue<string>() is { } known) { p.YoutubeVideoId = known; return p; }
        var session = await settings.Secret("youtube:upload:" + job.Id);
        if (session is null)
        {
            if (job.Checkpoint is not null) throw new InvalidOperationException("Thiếu upload session đã lưu; đối soát trên YouTube trước khi upload lại.");
            using var req = ProviderHttp.Request(HttpMethod.Post, "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status", new
            {
                snippet = new { title = p.Title, description = p.Description, tags = p.Tags, categoryId = "22" },
                status = new { privacyStatus = options.Privacy, selfDeclaredMadeForKids = options.MadeForKids }
            }, await AccessToken(ct));
            req.Headers.Add("X-Upload-Content-Type", "video/mp4"); req.Headers.Add("X-Upload-Content-Length", asset.Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var response = await http.Send(req, ct, true);
            session = response.Headers.Location?.AbsoluteUri ?? throw new ProviderException("YouTube không trả resumable session.", true);
            var uri = new Uri(session);
            if (uri.Scheme != "https" || !(uri.Host == "www.googleapis.com" || uri.Host.EndsWith(".googleapis.com", StringComparison.Ordinal))) throw new ProviderException("Upload session host không hợp lệ.", true);
            await settings.Save("youtube:upload:" + job.Id, new { projectId = p.Id }, session);
            await checkpoint(Json.Write(new { kind = "youtube", uploadedBytes = 0 }));
        }
        long offset = 0;
        using (var status = ProviderHttp.Request(HttpMethod.Put, session, bearer: await AccessToken(ct)))
        {
            status.Content = new ByteArrayContent([]); status.Content.Headers.TryAddWithoutValidation("Content-Range", $"bytes */{asset.Bytes}");
            using var response = await http.Client.SendAsync(status, ct);
            if (response.IsSuccessStatusCode)
            {
                var data = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)); p.YoutubeVideoId = ProviderHttp.Required(data, "id");
            }
            else if ((int)response.StatusCode == 308) offset = UploadedOffset(response);
            else throw new ProviderException($"Không đối soát được upload session (HTTP {(int)response.StatusCode}). Kiểm tra YouTube trước khi tạo upload mới.", true);
        }
        if (p.YoutubeVideoId is null)
        {
            await using var stream = File.OpenRead(asset.Path); stream.Position = offset;
            var buffer = new byte[8 * 1024 * 1024];
            while (offset < asset.Bytes)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, asset.Bytes - offset)), ct);
                if (count == 0) throw new IOException("Video file ended unexpectedly.");
                using var req = ProviderHttp.Request(HttpMethod.Put, session, bearer: await AccessToken(ct));
                req.Content = new ByteArrayContent(buffer, 0, count); req.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
                req.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + count - 1, asset.Bytes);
                using var response = await http.Client.SendAsync(req, ct);
                if (response.IsSuccessStatusCode) { p.YoutubeVideoId = ProviderHttp.Required(JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)), "id"); break; }
                if ((int)response.StatusCode != 308) throw new ProviderException($"YouTube upload HTTP {(int)response.StatusCode}. Tiếp tục session hiện có sau khi kiểm tra.", true);
                var next = UploadedOffset(response);
                if (next <= offset || next > offset + count) throw new ProviderException("YouTube báo offset không hợp lệ.", true);
                offset = next; stream.Position = offset;
                await checkpoint(Json.Write(new { kind = "youtube", uploadedBytes = offset }));
            }
        }
        if (p.YoutubeVideoId is null) throw new ProviderException("Chưa nhận video ID. Tiếp tục kiểm tra session; không upload lại.", true);
        await checkpoint(Json.Write(new { kind = "youtube", videoId = p.YoutubeVideoId }));
        // Video success is persisted before the separately retryable thumbnail operation.
        return p;
    }
    public async Task SetThumbnail(Project p, CancellationToken ct)
    {
        if (p.YoutubeVideoId is null || p.ThumbnailAssetId is null) throw new InvalidOperationException("Thiếu video ID hoặc thumbnail.");
        var thumbnail = await assets.Get(p.ThumbnailAssetId, p.Id);
        using var req = ProviderHttp.Request(HttpMethod.Post, "https://www.googleapis.com/upload/youtube/v3/thumbnails/set?videoId=" + Uri.EscapeDataString(p.YoutubeVideoId) + "&uploadType=media", bearer: await AccessToken(ct));
        req.Content = new StreamContent(File.OpenRead(thumbnail.Path)); req.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        using var response = await http.Send(req, ct);
    }
    public async Task<string> AccessToken(CancellationToken ct)
    {
        await tokenLock.WaitAsync(ct);
        try
        {
            var raw = await settings.Secret("youtube:tokens") ?? throw new InvalidOperationException("Chưa kết nối YouTube bằng OAuth.");
            var token = Json.Read<YouTubeTokens>(raw);
            if (token.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return token.AccessToken;
            var cfg = await settings.Get<YouTubeConfig>("youtube:config") ?? throw new InvalidOperationException("Thiếu OAuth config.");
            var fresh = await Exchange(new() { ["client_id"] = cfg.ClientId, ["client_secret"] = (await settings.Secret("youtube:config"))!, ["grant_type"] = "refresh_token", ["refresh_token"] = token.RefreshToken }, ct);
            fresh = fresh with { RefreshToken = token.RefreshToken }; await settings.Save("youtube:tokens", new { connected = true }, Json.Write(fresh)); return fresh.AccessToken;
        }
        finally { tokenLock.Release(); }
    }
    private async Task<YouTubeTokens> Exchange(Dictionary<string, string> fields, CancellationToken ct)
    {
        using var req = ProviderHttp.Request(HttpMethod.Post, "https://oauth2.googleapis.com/token"); req.Content = new FormUrlEncodedContent(fields);
        var data = await http.Json(req, ct);
        return new(ProviderHttp.Required(data, "access_token"), data["refresh_token"]?.GetValue<string>() ?? "", DateTimeOffset.UtcNow.AddSeconds(data["expires_in"]?.GetValue<int>() ?? 3600));
    }
    private static long UploadedOffset(HttpResponseMessage response)
    {
        var range = response.Headers.TryGetValues("Range", out var values) ? values.FirstOrDefault() : null;
        if (range is null) return 0;
        return long.TryParse(range.Split('-').Last(), out var last) ? last + 1 : throw new ProviderException("Upload Range không hợp lệ.", true);
    }
    private static string Query(Dictionary<string, string> values) => string.Join('&', values.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value)));
}
