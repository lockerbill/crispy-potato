using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Studio.Data;
using Studio.Domain;
using Studio.Providers;
using Studio.Services;

namespace Studio.Tests;

public sealed class YouTubeTests
{
    [Fact]
    public async Task InterruptedUploadQueriesCommittedOffsetAndNeverCreatesSecondVideoSession()
    {
        var sessions = 0; var chunks = 0; long committed = 0;
        using var client = new HttpClient(new RecordingHandler(async req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("/channels?", StringComparison.Ordinal)) return Response("{\"items\":[{\"id\":\"channel-1\",\"snippet\":{\"title\":\"Test channel\"}}]}");
            if (req.Method == HttpMethod.Post) { sessions++; var response = Response(""); response.Headers.Location = new Uri("https://www.googleapis.com/upload/session-1"); return response; }
            var range = req.Content!.Headers.ContentRange!;
            if (!range.HasRange)
            {
                var status = new HttpResponseMessage((HttpStatusCode)308);
                if (committed > 0) status.Headers.TryAddWithoutValidation("Range", $"bytes=0-{committed - 1}");
                return status;
            }
            chunks++; Assert.Equal(committed, range.From); var bytes = await req.Content.ReadAsByteArrayAsync(); committed += bytes.Length;
            if (chunks == 1) throw new HttpRequestException("Simulated loss of response after server accepted first chunk.");
            return Response("{\"id\":\"uploaded-video-1\"}");
        }));
        using var host = new TestHost { ConfigureTestServices = s => { s.RemoveAll<ProviderHttp>(); s.AddSingleton(new ProviderHttp(new ClientFactory(client))); } };
        var settings = host.Get<SettingsService>();
        await settings.Save("youtube:config", new YouTubeConfig { ClientId = "test-client", ChannelId = "channel-1", ChannelName = "Test" }, "test-client-secret");
        await settings.Save("youtube:tokens", new { connected = true }, Json.Write(new YouTubeTokens("test-access", "test-refresh", DateTimeOffset.UtcNow.AddHours(1))));
        var p = new Project(); ProviderCatalog.ApplyPreset(p, "local"); foreach (var step in p.Steps.Values) step.Status = StepStatus.Approved; p.Title = "A test video";
        var store = host.Get<AssetStore>(); var file = Path.Combine(store.WorkDir(Guid.NewGuid()), "upload.mp4"); await File.WriteAllBytesAsync(file, new byte[9 * 1024 * 1024]);
        p.VideoAssetId = (await store.Add(p.Id, file, "video.mp4", "video/mp4", CancellationToken.None)).Id;
        await host.Get<ProjectService>().Create(p); var yt = host.Get<YouTubeService>(); var jobId = await yt.Queue(p.Id, "private", false, true, CancellationToken.None);
        await using var db = await host.Get<IDbContextFactory<StudioDb>>().CreateDbContextAsync(); var job = (await db.Jobs.FindAsync(jobId))!;
        Task Checkpoint(string checkpoint) { job.Checkpoint = checkpoint; return Task.CompletedTask; }
        await Assert.ThrowsAsync<HttpRequestException>(() => yt.Upload(job, Checkpoint, CancellationToken.None));
        Assert.NotNull(job.Checkpoint); Assert.Equal(8 * 1024 * 1024, committed);
        var result = await yt.Upload(job, Checkpoint, CancellationToken.None);
        Assert.Equal("uploaded-video-1", result.YoutubeVideoId); Assert.Equal(1, sessions); Assert.Equal(2, chunks); Assert.Equal(9 * 1024 * 1024, committed);
        var stored = await db.Settings.FindAsync("youtube:upload:" + job.Id); Assert.DoesNotContain("session-1", stored!.ProtectedSecret!);
    }
    [Fact]
    public async Task ExpiredOAuthStateCannotExchangeAToken()
    {
        using var host = new TestHost(); var settings = host.Get<SettingsService>();
        await settings.Save("youtube:state:expired", new OAuthPending(DateTimeOffset.UtcNow.AddMinutes(-1)), "verifier");
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Get<YouTubeService>().Complete("expired", "code", CancellationToken.None));
    }
    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
