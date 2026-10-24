using System.Xml.Linq;
using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Settings;

namespace FacelessStudio.Providers.Llm;

/// <summary>Đọc RSS "Daily Search Trends" công khai của Google Trends (miễn phí, không cần key).</summary>
public sealed class GoogleTrendsClient(IHttpClientFactory httpFactory)
{
    public async Task<IReadOnlyList<string>> GetTrendingAsync(StepContext context, int max = 15)
    {
        var geo = context.Settings.Get(SettingKeys.GoogleTrendsGeo);
        try
        {
            var xml = await httpFactory.CreateClient("providers")
                .GetStringAsync($"https://trends.google.com/trending/rss?geo={Uri.EscapeDataString(geo)}", context.CancellationToken);
            return Parse(xml).Take(max).ToList();
        }
        catch (Exception e) when (e is HttpRequestException or System.Xml.XmlException or TaskCanceledException
                                      && !context.CancellationToken.IsCancellationRequested)
        {
            context.Log($"Không lấy được Google Trends ({e.Message}), gợi ý chủ đề không dựa trên xu hướng.");
            return [];
        }
    }

    public static IEnumerable<string> Parse(string xml)
    {
        XNamespace ht = "https://trends.google.com/trending/rss";
        return XDocument.Parse(xml).Descendants("item").Select(item =>
        {
            var title = item.Element("title")?.Value.Trim() ?? "";
            var news = item.Element(ht + "news_item")?.Element(ht + "news_item_title")?.Value.Trim();
            return string.IsNullOrEmpty(news) ? title : $"{title} ({news})";
        }).Where(t => t.Length > 0);
    }
}
