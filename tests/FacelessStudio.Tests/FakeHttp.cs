using System.Net;
using System.Text;
using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Media;
using FacelessStudio.Core.Models;
using FacelessStudio.Core.Settings;
using FacelessStudio.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace FacelessStudio.Tests;

/// <summary>HTTP giả lập: ghi lại request và trả về phản hồi soạn sẵn theo URL.</summary>
public sealed class FakeHttp : HttpMessageHandler
{
    public sealed record Captured(HttpMethod Method, string Url, string Body, Dictionary<string, string> Headers);

    private readonly List<(Func<string, bool> Match, Func<HttpResponseMessage> Respond)> _routes = [];
    public List<Captured> Requests { get; } = [];

    public FakeHttp On(string urlContains, string body, HttpStatusCode status = HttpStatusCode.OK, string contentType = "application/json")
    {
        _routes.Add((u => u.Contains(urlContains), () => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        }));
        return this;
    }

    public FakeHttp OnBytes(string urlContains, byte[] body, string contentType = "application/octet-stream")
    {
        _routes.Add((u => u.Contains(urlContains), () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body) { Headers = { ContentType = new(contentType) } },
        }));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
        Requests.Add(new Captured(request.Method, url, body, headers));
        // Route khai báo sau được ưu tiên để test có thể ghi đè.
        for (var i = _routes.Count - 1; i >= 0; i--)
            if (_routes[i].Match(url)) return _routes[i].Respond();
        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent($"no route for {url}") };
    }

    public ServiceProvider BuildServices()
    {
        var services = new ServiceCollection().AddFacelessProviders();
        services.AddHttpClient("providers").ConfigurePrimaryHttpMessageHandler(() => this);
        services.AddHttpClient("anthropic").ConfigurePrimaryHttpMessageHandler(() => this);
        return services.BuildServiceProvider();
    }

    public static StepContext Context(Dictionary<string, string> settings, VideoRequest? request = null, string? workDir = null)
    {
        workDir ??= Directory.CreateTempSubdirectory("faceless-test").FullName;
        return new StepContext
        {
            Request = request ?? new VideoRequest { Topic = "Bí ẩn đại dương", Language = "vi", Length = "short60" },
            Settings = new AppSettings(settings, workDir),
            WorkDir = workDir,
            Ffmpeg = new FfmpegTool(),
        };
    }

    public static T Provider<T>(ServiceProvider sp, string id) where T : class, IStepProvider =>
        (T)sp.GetServices<IStepProvider>().First(p => p.Id == id);
}
