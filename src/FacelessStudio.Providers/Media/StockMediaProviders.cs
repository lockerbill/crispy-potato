using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Models;
using FacelessStudio.Core.Settings;
using FacelessStudio.Providers.Http;

namespace FacelessStudio.Providers.Media;

public sealed record StockCandidate(string Id, string Url, int Width, int Height, string Credit);

/// <summary>Khung chung cho kho video stock: tìm theo từ khoá, bỏ clip đã dùng, tải về cache.</summary>
public abstract class StockMediaProvider(IHttpClientFactory httpFactory) : ProviderBase, IMediaProvider
{
    public override PipelineStep Step => PipelineStep.Media;
    public override ProviderCategory Category => ProviderCategory.FreeCloud;

    protected HttpClient Http => httpFactory.CreateClient("providers");

    protected abstract Task<IReadOnlyList<StockCandidate>> SearchAsync(string query, bool vertical, StepContext context);

    public async Task<IReadOnlyList<MediaAsset>> GetAsync(MediaRequest request, StepContext context)
    {
        var used = context.GetItem<HashSet<string>>("used-stock");
        var result = new List<MediaAsset>();
        // Thử từ khoá của cảnh trước, thiếu thì tìm theo chủ đề.
        foreach (var query in new[] { request.Scene.FootageQuery, context.Request.Topic }.Where(q => !string.IsNullOrWhiteSpace(q)).Distinct())
        {
            if (result.Count >= request.Count) break;
            foreach (var c in await SearchAsync(query, request.Vertical, context))
            {
                if (result.Count >= request.Count) break;
                if (!used.Add($"{Id}:{c.Id}")) continue;
                var path = await DownloadToCacheAsync(c.Url, ".mp4", context);
                result.Add(new MediaAsset(MediaKind.Video, path, c.Url, c.Credit));
            }
        }
        return result;
    }

    protected async Task<string> DownloadToCacheAsync(string url, string extension, StepContext context)
    {
        var dir = Path.Combine(context.Settings.BaseDir, "data", "cache", Id);
        Directory.CreateDirectory(dir);
        var name = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..16] + extension;
        var path = Path.Combine(dir, name);
        if (!File.Exists(path)) await HttpHelpers.DownloadAsync(Http, Name, url, path, context.CancellationToken);
        return path;
    }

    /// <summary>Chọn file nhỏ nhất mà cạnh ngắn vẫn &gt;= 1080 (đủ nét, tải nhanh); không có thì lấy file lớn nhất.</summary>
    public static T? PickFile<T>(IEnumerable<T> files, Func<T, int> width, Func<T, int> height) where T : class
    {
        var list = files.Where(f => width(f) > 0 && height(f) > 0).ToList();
        if (list.Count == 0) return null;
        var sharp = list.Where(f => Math.Min(width(f), height(f)) >= 1080).ToList();
        return sharp.Count > 0
            ? sharp.MinBy(f => width(f) * height(f))
            : list.MaxBy(f => width(f) * height(f));
    }
}

public sealed class PexelsMediaProvider(IHttpClientFactory httpFactory) : StockMediaProvider(httpFactory)
{
    public override string Id => "media-pexels";
    public override string Name => "Pexels";
    public override string Description => "Kho video stock miễn phí, chất lượng cao, dùng thương mại được. Cần API key miễn phí.";
    public override IReadOnlyList<string> RequiredSettings => [SettingKeys.PexelsApiKey];

    protected override async Task<IReadOnlyList<StockCandidate>> SearchAsync(string query, bool vertical, StepContext context)
    {
        var url = $"https://api.pexels.com/videos/search?query={Uri.EscapeDataString(query)}" +
                  $"&orientation={(vertical ? "portrait" : "landscape")}&per_page=15&size=medium";
        var json = await HttpHelpers.GetJsonAsync(Http, Name, url, context.CancellationToken,
            ("Authorization", context.Settings.Get(SettingKeys.PexelsApiKey)));
        var result = new List<StockCandidate>();
        foreach (var v in json["videos"]?.AsArray() ?? [])
        {
            var files = (v?["video_files"]?.AsArray() ?? []).OfType<JsonObject>()
                .Where(f => f["file_type"]?.GetValue<string>() == "video/mp4");
            var file = PickFile(files, f => f["width"]?.GetValue<int>() ?? 0, f => f["height"]?.GetValue<int>() ?? 0);
            if (file == null) continue;
            result.Add(new StockCandidate(v!["id"]!.ToString(), file["link"]!.GetValue<string>(),
                file["width"]!.GetValue<int>(), file["height"]!.GetValue<int>(),
                $"Pexels / {v["user"]?["name"]?.GetValue<string>()}"));
        }
        return result;
    }
}

public sealed class PixabayMediaProvider(IHttpClientFactory httpFactory) : StockMediaProvider(httpFactory)
{
    public override string Id => "media-pixabay";
    public override string Name => "Pixabay";
    public override string Description => "Kho video stock miễn phí, nhiều cảnh thiên nhiên và hoạt hình. Cần API key miễn phí.";
    public override IReadOnlyList<string> RequiredSettings => [SettingKeys.PixabayApiKey];

    protected override async Task<IReadOnlyList<StockCandidate>> SearchAsync(string query, bool vertical, StepContext context)
    {
        var url = $"https://pixabay.com/api/videos/?key={Uri.EscapeDataString(context.Settings.Get(SettingKeys.PixabayApiKey))}" +
                  $"&q={Uri.EscapeDataString(query)}&per_page=20&safesearch=true";
        var json = await HttpHelpers.GetJsonAsync(Http, Name, url, context.CancellationToken);
        var result = new List<StockCandidate>();
        foreach (var hit in json["hits"]?.AsArray() ?? [])
        {
            var variants = (hit?["videos"]?.AsObject().Select(kv => kv.Value).OfType<JsonObject>() ?? [])
                .Where(v => !string.IsNullOrEmpty(v["url"]?.GetValue<string>()));
            var file = PickFile(variants, f => f["width"]?.GetValue<int>() ?? 0, f => f["height"]?.GetValue<int>() ?? 0);
            if (file == null) continue;
            int w = file["width"]!.GetValue<int>(), h = file["height"]!.GetValue<int>();
            result.Add(new StockCandidate(hit!["id"]!.ToString(), file["url"]!.GetValue<string>(), w, h,
                $"Pixabay / {hit["user"]?.GetValue<string>()}"));
        }
        // Pixabay không lọc được theo chiều dọc/ngang nên xếp clip đúng hướng lên trước.
        return result.OrderByDescending(c => vertical ? c.Height > c.Width : c.Width >= c.Height).ToList();
    }
}
