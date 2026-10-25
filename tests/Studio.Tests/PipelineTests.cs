using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Studio.Data;
using Studio.Domain;
using Studio.Providers;
using Studio.Services;

namespace Studio.Tests;

public sealed class PipelineTests
{
    private static Project NewProject() { var p = new Project(); ProviderCatalog.ApplyPreset(p, "local"); return p; }
    [Theory]
    [InlineData(VideoFormat.Short, 14, false)] [InlineData(VideoFormat.Short, 180, true)] [InlineData(VideoFormat.Short, 181, false)]
    [InlineData(VideoFormat.Long, 60, true)] [InlineData(VideoFormat.Long, 1800, true)] [InlineData(VideoFormat.Long, 1801, false)]
    public void ValidatesDurationBoundaries(VideoFormat format, int seconds, bool accepted)
    {
        var p = new Project { Format = format, TargetSeconds = seconds };
        if (accepted) Pipeline.Validate(p); else Assert.Throws<InvalidOperationException>(() => Pipeline.Validate(p));
    }
    [Fact]
    public async Task RejectsStaleEditsAndPreservesRevisions()
    {
        using var host = new TestHost(); var service = host.Get<ProjectService>(); var p = await service.Create(NewProject()); var stale = Json.Clone(p);
        p.Topic = "Đại dương"; await service.Edit(p, Stage.Topic);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.Edit(stale, Stage.Topic));
        Assert.Single(await service.History(p.Id));
        Assert.Equal(StepStatus.AwaitingReview, (await service.Get(p.Id)).Steps[Stage.Topic].Status);
        await service.Approve(p.Id, Stage.Topic); Assert.Equal(StepStatus.Approved, (await service.Get(p.Id)).Steps[Stage.Topic].Status);
    }
    [Fact]
    public async Task MediaChangesRequireRerenderWithoutLosingPreviousAssets()
    {
        using var host = new TestHost(); var service = host.Get<ProjectService>(); var p = NewProject();
        foreach (var s in p.Steps.Values) s.Status = StepStatus.Approved;
        p.VideoAssetId = "old-video"; p.ThumbnailAssetId = "old-thumbnail"; await service.Create(p);
        await service.Edit(p, Stage.Media);
        var changed = await service.Get(p.Id);
        Assert.Equal(StepStatus.Stale, changed.Steps[Stage.Render].Status); Assert.Null(changed.VideoAssetId);
        Assert.Equal(StepStatus.Approved, changed.Steps[Stage.Voice].Status); Assert.Equal("old-thumbnail", changed.ThumbnailAssetId);
        var previous = Assert.Single(await service.History(p.Id)); Assert.Contains("old-video", previous.Json);
    }
    [Fact]
    public async Task ReservesBudgetAtomicallyAndBlocksDuplicateQueues()
    {
        using var host = new TestHost(); var service = host.Get<ProjectService>(); var settings = host.Get<SettingsService>();
        await settings.SaveProvider(new ProviderConfig { Id = "openai-text", Model = "gpt-4.1-mini", UsdPerUnit = 1, OutputUsdPerUnit = 4, PriceAsOf = DateTimeOffset.UtcNow, PriceVersion = "test-price" }, "test-key-not-a-real-credential");
        var p = NewProject(); p.Steps[Stage.Topic].ProviderId = "openai-text"; p.Steps[Stage.Topic].Model = "gpt-4.1-mini"; await service.Create(p);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ => { try { await service.Queue(p.Id, Stage.Topic); return true; } catch (InvalidOperationException) { return false; } }));
        Assert.Single(attempts, x => x); Assert.Single(await service.Jobs(p.Id)); Assert.True(ProjectService.Committed(await service.Charges(p.Id)) > 0);
        await service.Cancel((await service.Jobs(p.Id))[0].Id); Assert.Equal(0, ProjectService.Committed(await service.Charges(p.Id)));
    }
    [Fact]
    public async Task MissingPriceAndInsufficientBudgetDoNotQueueRequests()
    {
        using var host = new TestHost(); var service = host.Get<ProjectService>(); var settings = host.Get<SettingsService>();
        var cfg = new ProviderConfig { Id = "openai-text", Model = "gpt-4.1-mini" }; await settings.SaveProvider(cfg, "test-only");
        var p = NewProject(); p.Currency = "VND"; p.Budget = 1; p.VndPerUsd = 25000; p.Steps[Stage.Topic].ProviderId = cfg.Id; p.Steps[Stage.Topic].Model = cfg.Model; await service.Create(p);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.Queue(p.Id, Stage.Topic)); Assert.Empty(await service.Jobs(p.Id));
        cfg.PriceAsOf = DateTimeOffset.UtcNow; cfg.PriceVersion = "test"; cfg.UsdPerUnit = 1; cfg.OutputUsdPerUnit = 10; await settings.SaveProvider(cfg);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.Queue(p.Id, Stage.Topic)); Assert.Empty(await service.Charges(p.Id));
        Assert.Equal(0.00004m, p.BudgetUsd);
    }
    [Fact]
    public async Task RunningJobsRecoverAsUncertainWithoutAutomaticResubmission()
    {
        using var host = new TestHost(); var service = host.Get<ProjectService>(); var p = await service.Create(NewProject());
        await host.Get<SettingsService>().SaveProvider(new ProviderConfig { Id = "ollama", Endpoint = "http://localhost:11434", Model = "qwen3:4b" });
        var id = await service.Queue(p.Id, Stage.Topic);
        await using (var db = await host.Get<IDbContextFactory<StudioDb>>().CreateDbContextAsync()) { (await db.Jobs.FindAsync(id))!.Status = "Running"; await db.SaveChangesAsync(); }
        var worker = ActivatorUtilities.CreateInstance<PipelineWorker>(host.Services); await worker.Recover();
        var job = Assert.Single(await service.Jobs(p.Id)); Assert.Equal("NeedsAttention", job.Status);
        Assert.Equal("Uncertain", Assert.Single(await service.Charges(p.Id)).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.Queue(p.Id, Stage.Topic));
        await service.Reconcile(id, 0); await service.Queue(p.Id, Stage.Topic); Assert.Equal(2, (await service.Jobs(p.Id)).Count);
    }
    [Fact]
    public async Task SecretsAreProtectedAndNeverReturnedInProviderConfiguration()
    {
        using var host = new TestHost(); var settings = host.Get<SettingsService>(); const string secret = "test-only-key-do-not-expose";
        Assert.Equal(host.DataPath, host.Get<StudioPaths>().Root);
        await settings.SaveProvider(new ProviderConfig { Id = "gemini", Model = "test" }, secret);
        var config = await settings.GetProvider("gemini"); Assert.True(config.Configured); Assert.DoesNotContain(secret, Json.Write(config));
        await using var db = await host.Get<IDbContextFactory<StudioDb>>().CreateDbContextAsync();
        var stored = await db.Settings.FindAsync("provider:gemini"); Assert.DoesNotContain(secret, stored!.ProtectedSecret!); Assert.Equal(secret, await settings.Secret("provider:gemini"));
        await settings.SaveProvider(config, ""); Assert.Equal(secret, await settings.Secret("provider:gemini"));
    }
    [Fact]
    public void CaptionValidationRejectsOverlapAndDoesNotApproximateWordTiming()
    {
        Assert.Throws<InvalidOperationException>(() => CaptionFiles.Validate([new("xin", 0, 1), new("chào", .3, 2)], 3));
        var p = new Project { Captions = [new("Xin", 0, .4), new("chào", .4, 1)] };
        var ass = CaptionFiles.Ass(p); Assert.Contains("chào", ass); Assert.Contains("0:00:00.40", ass); Assert.Contains("\\c&H80FF5C&", ass);
        Assert.Contains("00:00:00,400", CaptionFiles.Srt(p.Captions)); Assert.StartsWith("WEBVTT", CaptionFiles.Vtt(p.Captions));
    }
    [Fact]
    public async Task RestoringEmptyInitialVersionPreservesPublishedVideoAndCharges()
    {
        using var host = new TestHost(); var service = host.Get<ProjectService>(); var p = await service.Create(NewProject());
        p.Topic = "Published story"; p.YoutubeVideoId = "published-id"; await service.Edit(p, Stage.Topic);
        var original = Assert.Single(await service.History(p.Id));
        await service.Restore(p.Id, original.Id);
        var restored = await service.Get(p.Id); Assert.Equal("", restored.Topic); Assert.Equal("published-id", restored.YoutubeVideoId); Assert.Equal(StepStatus.Pending, restored.Steps[Stage.Topic].Status);
    }
    [Fact]
    public async Task GenericSaveCannotLeaveApprovedOutputsAfterChangingTopic()
    {
        using var host = new TestHost(); var service = host.Get<ProjectService>(); var p = NewProject();
        p.Topic = "Original"; foreach (var s in p.Steps.Values) s.Status = StepStatus.Approved; await service.Create(p);
        p.Topic = "Changed"; await service.Edit(p, null);
        var current = await service.Get(p.Id); Assert.Equal(StepStatus.AwaitingReview, current.Steps[Stage.Topic].Status); Assert.Equal(StepStatus.Stale, current.Steps[Stage.Render].Status);
    }
    [Fact]
    public async Task UploadRequiresExplicitApprovalBeforeCheckingCredentials()
    {
        using var host = new TestHost(); var yt = host.Get<YouTubeService>();
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => yt.Queue(Guid.NewGuid(), "public", false, false, CancellationToken.None));
        Assert.Contains("duyệt", e.Message);
    }
}
