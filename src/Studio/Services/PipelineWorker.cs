using Microsoft.EntityFrameworkCore;
using Studio.Data;
using Studio.Domain;
using Studio.Providers;

namespace Studio.Services;

public sealed class PipelineWorker(IDbContextFactory<StudioDb> factory, ProjectService projects, ProviderRegistry registry, SettingsService settings, AssetStore assets, JobSignals signals, YouTubeService youtube, ILogger<PipelineWorker> logger, IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (configuration.GetValue<bool>("Studio:DisableWorker")) return;
        await Recover();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await Claim(stoppingToken);
                if (job is null) { await Task.Delay(700, stoppingToken); continue; }
                await Run(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { logger.LogError("Worker infrastructure failure: {Type}. Retrying queue scan.", e.GetType().Name); await Task.Delay(2000, stoppingToken); }
        }
    }
    public async Task Recover()
    {
        await using var db = await factory.CreateDbContextAsync();
        var jobs = await db.Jobs.Where(x => x.Status == "Running").ToListAsync();
        foreach (var job in jobs)
        {
            job.Status = nameof(JobStatus.NeedsAttention); job.Error = "Ứng dụng đã khởi động lại khi tác vụ đang chạy. Tiếp tục job có checkpoint hoặc đối soát trước khi chạy lại.";
            var charge = await db.Charges.SingleOrDefaultAsync(c => c.JobId == job.Id); if (charge is not null) charge.Status = nameof(ChargeStatus.Uncertain);
            var row = await db.Projects.FindAsync(job.ProjectId);
            if (row is not null && Enum.TryParse<Stage>(job.Stage, out var stage)) { var p = Json.Read<Project>(row.Json); p.Steps[stage].Status = StepStatus.NeedsAttention; p.Steps[stage].Error = job.Error; ProjectService.SaveRevision(db, row, p, "Recover interrupted job"); }
        }
        await db.SaveChangesAsync();
    }
    private async Task<JobRow?> Claim(CancellationToken ct)
    {
        await projects.Gate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var job = await db.Jobs.OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(x => x.Status == "Queued" && !x.CancelRequested, ct);
            if (job is null) return null;
            job.Status = nameof(JobStatus.Running);
            var row = await db.Projects.FindAsync([job.ProjectId], ct);
            if (row is not null && Enum.TryParse<Stage>(job.Stage, out var stage)) { var p = Json.Read<Project>(row.Json); p.Steps[stage].Status = StepStatus.Running; ProjectService.SaveRevision(db, row, p, "Run " + stage); }
            await db.SaveChangesAsync(ct); return job;
        }
        finally { projects.Gate.Release(); }
    }
    private async Task Run(JobRow job, CancellationToken stoppingToken)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); signals.Register(job.Id, cancel);
        var ct = cancel.Token; Project? finished = null; var completed = false; var startedExecution = false;
        ProviderContext? providerContext = null;
        async Task Checkpoint(string value)
        {
            await using var db = await factory.CreateDbContextAsync(CancellationToken.None);
            var current = await db.Jobs.FindAsync(job.Id); current!.Checkpoint = value; job.Checkpoint = value; await db.SaveChangesAsync(CancellationToken.None);
        }
        try
        {
            await using (var state = await factory.CreateDbContextAsync(CancellationToken.None))
                if ((await state.Jobs.FindAsync(job.Id))?.CancelRequested == true) cancel.Cancel();
            ct.ThrowIfCancellationRequested();
            string output;
            if (job.Stage == "YouTube") { startedExecution = true; finished = await youtube.Upload(job, Checkpoint, ct); output = Json.Write(new { videoId = finished.YoutubeVideoId }); }
            else
            {
                var stage = Enum.Parse<Stage>(job.Stage); var config = Json.Read<ProviderConfig>(job.ConfigJson);
                var context = new ProviderContext { JobId = job.Id, Project = Json.Read<Project>(job.RequestJson), Stage = stage, Config = config, WorkDirectory = assets.WorkDir(job.Id), Secret = await settings.Secret("provider:" + job.ProviderId), Checkpoint = job.Checkpoint, SaveCheckpoint = Checkpoint };
                providerContext = context;
                ct.ThrowIfCancellationRequested(); startedExecution = true;
                var result = await registry.Get(job.ProviderId).Execute(context, ct); finished = result.Project; output = result.Output;
            }
            await projects.Gate.WaitAsync(CancellationToken.None);
            try
            {
                await using var db = await factory.CreateDbContextAsync(); var currentJob = await db.Jobs.FindAsync(job.Id); var row = await db.Projects.FindAsync(job.ProjectId);
                if (Enum.TryParse<Stage>(job.Stage, out var stage))
                {
                    Pipeline.Invalidate(finished, stage); var step = finished.Steps[stage]; step.Output = output; step.Version++;
                    step.Status = finished.Mode == RunMode.Automatic ? StepStatus.Approved : StepStatus.AwaitingReview; step.Error = null;
                }
                currentJob!.Status = nameof(JobStatus.Completed); currentJob.FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var charge = await db.Charges.SingleOrDefaultAsync(c => c.JobId == job.Id);
                if (charge is not null) charge.Status = charge.AmountUsd == 0 ? nameof(ChargeStatus.Confirmed) : nameof(ChargeStatus.Estimated);
                ProjectService.SaveRevision(db, row!, finished, "Complete " + job.Stage); await db.SaveChangesAsync();
                completed = true;
            }
            finally { projects.Gate.Release(); }
            if (job.Stage == "YouTube")
            {
                try { await youtube.SetThumbnail(finished, ct); }
                catch (Exception) { await using var db = await factory.CreateDbContextAsync(); var row = await db.Jobs.FindAsync(job.Id); row!.Error = "Video đã đăng; thumbnail chưa cập nhật. Dùng nút gửi lại thumbnail; không upload lại video."; await db.SaveChangesAsync(); }
            }
        }
        catch (Exception e)
        {
            if (e is OperationCanceledException && providerContext is not null && !stoppingToken.IsCancellationRequested)
            {
                await using var state = await factory.CreateDbContextAsync(CancellationToken.None);
                if ((await state.Jobs.FindAsync(job.Id))?.CancelRequested == true)
                {
                    providerContext.Checkpoint = job.Checkpoint;
                    using var cancelTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await registry.Get(job.ProviderId).Cancel(providerContext, cancelTimeout.Token); }
                    catch (Exception) { /* Keep the remote checkpoint and uncertain charge for reconciliation. */ }
                }
            }
            var uncertain = e is OperationCanceledException || e is ProviderException { MayHaveCharged: true } || job.Checkpoint is not null;
            // A partial multi-request stage may have consumed quota even if the last request did not.
            if (job.ProviderId is not ("ffmpeg" or "library" or "ollama" or "piper" or "whisper")) uncertain = true;
            if (!startedExecution) uncertain = false;
            await projects.Gate.WaitAsync(CancellationToken.None);
            try
            {
                await using var db = await factory.CreateDbContextAsync(); var row = await db.Jobs.FindAsync(job.Id);
                row!.Status = uncertain ? nameof(JobStatus.NeedsAttention) : e is OperationCanceledException ? nameof(JobStatus.Cancelled) : nameof(JobStatus.Failed);
                row.Error = e is OperationCanceledException ? "Tác vụ đã dừng. Provider cloud có thể vẫn chạy/tính phí; tiếp tục hoặc đối soát job hiện có." : e is InvalidOperationException or ProviderException ? e.Message : "Tác vụ thất bại khi đọc/xử lý kết quả. Kiểm tra cấu hình và trạng thái provider trước khi chạy lại.";
                var charge = await db.Charges.SingleOrDefaultAsync(c => c.JobId == job.Id); if (charge is not null) charge.Status = uncertain ? nameof(ChargeStatus.Uncertain) : nameof(ChargeStatus.Released);
                var projectRow = await db.Projects.FindAsync(job.ProjectId); var p = Json.Read<Project>(projectRow!.Json);
                if (Enum.TryParse<Stage>(job.Stage, out var stage)) { p.Steps[stage].Status = uncertain ? StepStatus.NeedsAttention : StepStatus.Failed; p.Steps[stage].Error = row.Error; }
                ProjectService.SaveRevision(db, projectRow, p, "Job stopped"); await db.SaveChangesAsync();
            }
            finally { projects.Gate.Release(); }
        }
        finally { signals.Remove(job.Id); }
        if (completed && finished?.Mode == RunMode.Automatic && job.Stage != "YouTube" && !stoppingToken.IsCancellationRequested)
        {
            try { await projects.RunNext(job.ProjectId); }
            catch (InvalidOperationException e)
            {
                // Surface budget/configuration blockers without charging or marking an unstarted job as executed.
                await projects.Gate.WaitAsync(CancellationToken.None);
                try { await using var db = await factory.CreateDbContextAsync(); var row = await db.Projects.FindAsync(job.ProjectId); var p = Json.Read<Project>(row!.Json); var next = Pipeline.Order.FirstOrDefault(s => !Pipeline.Usable(p.Steps[s]), Stage.Render); if (!Pipeline.Usable(p.Steps[next])) { p.Steps[next].Error = e.Message; ProjectService.SaveRevision(db, row, p, "Pipeline paused"); await db.SaveChangesAsync(); } }
                finally { projects.Gate.Release(); }
            }
        }
    }
}
