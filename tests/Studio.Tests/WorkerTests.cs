using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Studio.Domain;
using Studio.Providers;
using Studio.Services;

namespace Studio.Tests;

public sealed class TestPipelineProvider(bool wait = false) : IStageProvider
{
    public bool Handles(string id) => true;
    public Task Validate(ProviderConfig c, Stage s, CancellationToken ct) => Task.CompletedTask;
    public Task<string> CheckConnection(ProviderConfig c, string? secret, CancellationToken ct) => Task.FromResult("Test adapter");
    public async Task<ProviderResult> Execute(ProviderContext c, CancellationToken ct)
    {
        if (wait) await Task.Delay(Timeout.Infinite, ct);
        var p = c.Project;
        switch (c.Stage)
        {
            case Stage.Topic: p.Topic = "Ocean"; break;
            case Stage.Script: p.Scenes = [new Scene { Narration = "Hello world", VisualPrompt = "Ocean", DurationSeconds = 2 }]; break;
            case Stage.Title: p.Title = "Ocean story"; break;
            case Stage.Description: p.Description = "About the ocean"; break;
            case Stage.Tags: p.Tags = ["ocean"]; break;
            case Stage.Voice: p.Scenes[0].AudioAssetId = "test-audio"; p.Scenes[0].AudioSeconds = 2; break;
            case Stage.Media: p.Scenes[0].MediaAssetId = "test-media"; break;
            case Stage.Captions: p.Captions = [new("Hello", 0, .8), new("world", .8, 1.8)]; break;
            case Stage.Thumbnail: p.ThumbnailAssetId = "test-thumbnail"; break;
            case Stage.Render: p.VideoAssetId = "test-video"; break;
        }
        return new("Test result for " + c.Stage, p);
    }
}
public sealed class WorkerTests
{
    private sealed class RejectedTextProvider(bool uncertain) : IStageProvider
    {
        public bool Handles(string id) => id == "openrouter";
        public Task Validate(ProviderConfig c, Stage s, CancellationToken ct) => Task.CompletedTask;
        public Task<string> CheckConnection(ProviderConfig c, string? secret, CancellationToken ct) => Task.FromResult("Test");
        public Task<ProviderResult> Execute(ProviderContext c, CancellationToken ct) => throw new ProviderException("Test provider rejected or interrupted the request", uncertain);
    }

    [Theory]
    [InlineData(false, "Failed", "Released")]
    [InlineData(true, "NeedsAttention", "Uncertain")]
    public async Task TextFailuresPreserveBillingClassificationWithoutRetryOrFallback(bool uncertain, string jobStatus, string chargeStatus)
    {
        using var host = new TestHost { RunWorker = true, ConfigureTestServices = s => { s.RemoveAll<IStageProvider>(); s.AddSingleton<IStageProvider>(new RejectedTextProvider(uncertain)); } };
        var settings = host.Get<SettingsService>();
        await settings.SaveProvider(new ProviderConfig { Id = "openrouter", Model = "test-model", UsdPerUnit = 1, OutputUsdPerUnit = 1, PriceAsOf = DateTimeOffset.UtcNow, PriceVersion = "test-price" }, "test-only");
        var projects = host.Get<ProjectService>();
        var p = new Project { Mode = RunMode.Automatic };
        ProviderCatalog.ApplyPreset(p, "local");
        p.Steps[Stage.Topic].ProviderId = "openrouter";
        p.Steps[Stage.Topic].Model = "test-model";
        await projects.Create(p);
        await projects.RunNext(p.Id);
        await Until(async () => (await projects.Jobs(p.Id)).Any(j => j.Status == jobStatus));
        Assert.Equal(jobStatus, Assert.Single(await projects.Jobs(p.Id)).Status);
        Assert.Equal(chargeStatus, Assert.Single(await projects.Charges(p.Id)).Status);
    }

    private static TestHost Host(bool wait = false) => new() { RunWorker = true, ConfigureTestServices = s => { s.RemoveAll<IStageProvider>(); s.AddSingleton<IStageProvider>(new TestPipelineProvider(wait)); } };
    [Fact]
    public async Task AutomaticModeCompletesAllStagesButNeverUploadsWithoutApproval()
    {
        using var host = Host(); var service = host.Get<ProjectService>(); var p = new Project { Mode = RunMode.Automatic }; ProviderCatalog.ApplyPreset(p, "local"); await service.Create(p);
        {
            await service.RunNext(p.Id);
            await Until(async () => (await service.Get(p.Id)).Steps.Values.All(s => s.Status == StepStatus.Approved));
            var result = await service.Get(p.Id); Assert.Equal("test-video", result.VideoAssetId); Assert.Equal("test-thumbnail", result.ThumbnailAssetId); Assert.Null(result.YoutubeVideoId);
            var jobs = await service.Jobs(p.Id); Assert.Equal(10, jobs.Count); Assert.All(jobs, job => Assert.Equal("Completed", job.Status)); Assert.DoesNotContain(jobs, j => j.Stage == "YouTube");
        }
    }
    [Fact]
    public async Task ReviewModeStopsUntilOwnerApprovesAndContinues()
    {
        using var host = Host(); var service = host.Get<ProjectService>(); var p = new Project { Mode = RunMode.Review }; ProviderCatalog.ApplyPreset(p, "local"); await service.Create(p);
        {
            await service.RunNext(p.Id); await Until(async () => (await service.Get(p.Id)).Steps[Stage.Topic].Status == StepStatus.AwaitingReview);
            Assert.Single(await service.Jobs(p.Id)); await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunNext(p.Id));
            await service.Approve(p.Id, Stage.Topic); await service.RunNext(p.Id);
            await Until(async () => (await service.Get(p.Id)).Steps[Stage.Script].Status == StepStatus.AwaitingReview); Assert.Equal(2, (await service.Jobs(p.Id)).Count);
        }
    }
    [Fact]
    public async Task CancellingAnInFlightRequestStopsAutomaticAdvancement()
    {
        using var host = Host(wait: true); var service = host.Get<ProjectService>(); var p = new Project { Mode = RunMode.Automatic }; ProviderCatalog.ApplyPreset(p, "local"); await service.Create(p);
        {
            var id = await service.RunNext(p.Id); await Until(async () => (await service.Jobs(p.Id)).Any(j => j.Status == "Running")); await service.Cancel(id!.Value);
            await Until(async () => (await service.Jobs(p.Id)).All(j => j.Status == "NeedsAttention")); Assert.Single(await service.Jobs(p.Id));
        }
    }
    private static async Task Until(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!await condition()) await Task.Delay(50, timeout.Token);
    }
}
