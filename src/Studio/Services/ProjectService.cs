using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Studio.Data;
using Studio.Domain;
using Studio.Providers;

namespace Studio.Services;

public sealed class JobSignals
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> tokens = new();
    public void Register(Guid id, CancellationTokenSource source) => tokens[id] = source;
    public void Remove(Guid id) => tokens.TryRemove(id, out _);
    public void Cancel(Guid id) { if (tokens.TryGetValue(id, out var source)) { try { source.Cancel(); } catch (ObjectDisposedException) { } } }
}

public sealed class ProjectService(IDbContextFactory<StudioDb> factory, SettingsService settings, ProviderRegistry registry, JobSignals signals)
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public async Task<List<Project>> List()
    {
        await using var db = await factory.CreateDbContextAsync();
        return (await db.Projects.AsNoTracking().ToListAsync()).Select(x => Json.Read<Project>(x.Json)).OrderByDescending(p => p.UpdatedAt).ToList();
    }
    public async Task<Project> Get(Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        return Json.Read<Project>((await db.Projects.FindAsync(id))?.Json ?? throw new InvalidOperationException("Dự án không tồn tại."));
    }
    public async Task<Project> Create(Project project)
    {
        Pipeline.Validate(project);
        if (project.Steps.Count == 0) ProviderCatalog.ApplyPreset(project, "free");
        project.Revision = 1;
        await using var db = await factory.CreateDbContextAsync(); db.Projects.Add(new ProjectRow { Id = project.Id, Json = Json.Write(project) }); await db.SaveChangesAsync();
        return project;
    }
    public async Task<List<ChargeRow>> Charges(Guid id) { await using var db = await factory.CreateDbContextAsync(); return await db.Charges.AsNoTracking().Where(x => x.ProjectId == id).ToListAsync(); }
    public async Task<List<JobRow>> Jobs(Guid id) { await using var db = await factory.CreateDbContextAsync(); return await db.Jobs.AsNoTracking().Where(x => x.ProjectId == id).OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(); }
    public async Task<List<RevisionRow>> History(Guid id) { await using var db = await factory.CreateDbContextAsync(); return await db.Revisions.AsNoTracking().Where(x => x.ProjectId == id).OrderByDescending(x => x.Version).Take(100).ToListAsync(); }
    public static decimal Committed(IEnumerable<ChargeRow> charges) => charges.Where(x => x.Status != nameof(ChargeStatus.Released)).Sum(x => x.AmountUsd);
    public async Task<Project> Edit(Project edited, Stage? changed, string reason = "Edit")
    {
        await Gate.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            await EnsureIdle(db, edited.Id);
            var row = await db.Projects.FindAsync(edited.Id) ?? throw new InvalidOperationException("Dự án không tồn tại.");
            var current = Json.Read<Project>(row.Json);
            if (current.Revision != edited.Revision) throw new InvalidOperationException("Dự án đã thay đổi. Tải lại trước khi lưu.");
            Pipeline.Validate(edited);
            foreach (var stage in Pipeline.Order)
            {
                if (!edited.Steps.TryGetValue(stage, out var step) || !ProviderCatalog.Get(step.ProviderId).Stages.Contains(stage)) throw new InvalidOperationException("Provider không hỗ trợ công đoạn.");
                var prior = current.Steps[stage];
                if (prior.ProviderId != step.ProviderId || prior.Model != step.Model || prior.Voice != step.Voice)
                {
                    Pipeline.Invalidate(edited, stage); step.Status = prior.Status == StepStatus.Pending ? StepStatus.Pending : StepStatus.Stale;
                }
            }
            if (current.Brief != edited.Brief || current.Language != edited.Language || current.Audience != edited.Audience || current.Style != edited.Style || current.Format != edited.Format || current.TargetSeconds != edited.TargetSeconds)
            {
                Pipeline.Invalidate(edited, Stage.Topic); edited.Steps[Stage.Topic].Status = StepStatus.Stale;
            }
            void ChangedText(Stage stage, string output)
            {
                TextProvider.Apply(edited, stage, output);
                Pipeline.Invalidate(edited, stage);
                edited.Steps[stage].Status = StepStatus.AwaitingReview;
                edited.Steps[stage].Output = output;
            }
            if (current.Topic != edited.Topic) ChangedText(Stage.Topic, Json.Write(new { topic = edited.Topic }));
            if (current.Title != edited.Title) ChangedText(Stage.Title, Json.Write(new { title = edited.Title }));
            if (current.Description != edited.Description) ChangedText(Stage.Description, Json.Write(new { description = edited.Description }));
            if (!current.Tags.SequenceEqual(edited.Tags)) ChangedText(Stage.Tags, Json.Write(new { tags = edited.Tags }));
            var scriptChanged = Json.Write(current.Scenes.Select(s => new { s.Id, s.Narration, s.VisualPrompt, s.DurationSeconds, s.Transition })) != Json.Write(edited.Scenes.Select(s => new { s.Id, s.Narration, s.VisualPrompt, s.DurationSeconds, s.Transition }));
            if (scriptChanged) { Pipeline.Invalidate(edited, Stage.Script); edited.Steps[Stage.Script].Status = StepStatus.AwaitingReview; }
            if (!current.Scenes.Select(s => s.MediaAssetId).SequenceEqual(edited.Scenes.Select(s => s.MediaAssetId))) { Pipeline.Invalidate(edited, Stage.Media); edited.Steps[Stage.Media].Status = StepStatus.AwaitingReview; }
            if (!current.Captions.SequenceEqual(edited.Captions)) { if (edited.Captions.Count > 0) CaptionFiles.Validate(edited.Captions, edited.ActualSeconds); Pipeline.Invalidate(edited, Stage.Captions); edited.Steps[Stage.Captions].Status = edited.Captions.Count == 0 ? StepStatus.Stale : StepStatus.AwaitingReview; }
            if (current.CaptionStyle != edited.CaptionStyle) { edited.Steps[Stage.Render].Status = StepStatus.Stale; edited.VideoAssetId = null; }
            if (edited.Scenes.Any(s => !double.IsFinite(s.DurationSeconds) || s.DurationSeconds <= 0 || s.DurationSeconds > 1800 || s.Narration.Length > 5000 || s.Transition is not ("cut" or "fade"))) throw new InvalidOperationException("Cảnh hoặc transition không hợp lệ.");
            if (changed.HasValue)
            {
                Pipeline.Invalidate(edited, changed.Value);
                edited.Steps[changed.Value].Status = StepStatus.AwaitingReview;
                edited.Steps[changed.Value].Error = null;
            }
            SaveRevision(db, row, edited, reason); await db.SaveChangesAsync(); return edited;
        }
        finally { Gate.Release(); }
    }
    public async Task Restore(Guid projectId, Guid revisionId)
    {
        await Gate.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync(); await EnsureIdle(db, projectId);
            var revision = await db.Revisions.SingleAsync(x => x.Id == revisionId && x.ProjectId == projectId);
            var row = await db.Projects.FindAsync(projectId) ?? throw new InvalidOperationException("Dự án không tồn tại.");
            var current = Json.Read<Project>(row.Json); var restored = Json.Read<Project>(revision.Json);
            // Restore a previously valid snapshot (including empty pending stages), not a new manual result.
            // An external publication and its cost ledger cannot be undone by a content restore.
            restored.YoutubeVideoId = current.YoutubeVideoId;
            foreach (var step in restored.Steps.Values)
                if (step.Status is StepStatus.Queued or StepStatus.Running) step.Status = StepStatus.Stale;
            Pipeline.Validate(restored);
            SaveRevision(db, row, restored, "Restore version " + revision.Version); await db.SaveChangesAsync();
        }
        finally { Gate.Release(); }
    }
    public async Task Approve(Guid id, Stage stage)
    {
        var p = await Get(id);
        if (p.Steps[stage].Status != StepStatus.AwaitingReview) throw new InvalidOperationException("Bước này chưa có kết quả để duyệt.");
        if (Pipeline.Dependencies(stage).Any(s => !Pipeline.Usable(p.Steps[s]))) throw new InvalidOperationException("Duyệt các bước đầu vào trước.");
        if (stage == Stage.Script && (p.Scenes.Count is < 1 or > 400 || p.Scenes.Any(s => string.IsNullOrWhiteSpace(s.Narration)))) throw new InvalidOperationException("Kịch bản cần 1–400 cảnh có lời đọc.");
        if (stage == Stage.Captions) CaptionFiles.Validate(p.Captions, p.ActualSeconds);
        p.Steps[stage].Status = StepStatus.Approved;
        await Edit(p, null, "Approve " + stage);
    }
    public async Task<Guid?> RunNext(Guid id)
    {
        var p = await Get(id);
        var next = Pipeline.Order.FirstOrDefault(s => !Pipeline.Usable(p.Steps[s]), Stage.Render);
        if (Pipeline.Order.All(s => Pipeline.Usable(p.Steps[s]))) return null;
        if (p.Steps[next].Status == StepStatus.AwaitingReview) throw new InvalidOperationException("Duyệt kết quả hiện tại trước khi tiếp tục.");
        return await Queue(id, next);
    }
    public async Task<Guid> Queue(Guid id, Stage stage)
    {
        await Gate.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            await EnsureIdle(db, id);
            var row = await db.Projects.FindAsync(id) ?? throw new InvalidOperationException("Dự án không tồn tại.");
            var p = Json.Read<Project>(row.Json); var step = p.Steps[stage];
            if (Pipeline.Dependencies(stage).Any(s => !Pipeline.Usable(p.Steps[s]))) throw new InvalidOperationException("Chạy và duyệt các bước đầu vào trước.");
            var unresolved = await db.Jobs.Where(x => x.ProjectId == id && x.Stage == stage.ToString() && x.Status == nameof(JobStatus.NeedsAttention)).ToListAsync();
            if (unresolved.Count > 0) throw new InvalidOperationException("Có lần chạy chưa rõ kết quả. Tiếp tục job đã gửi hoặc xác nhận đối soát trong lịch sử trước khi tạo job mới.");
            var config = await settings.GetProvider(step.ProviderId); var provider = ProviderCatalog.Get(step.ProviderId);
            if (!provider.Stages.Contains(stage)) throw new InvalidOperationException("Provider không hỗ trợ bước này.");
            if ((provider.NeedsKey || provider.Auth is ProviderAuth.OAuth or ProviderAuth.ClaudeCode) && !config.Configured)
                throw new InvalidOperationException("Chưa kết nối provider. Cấu hình trong Settings / Provider is not connected. Configure it in Settings.");
            if (provider.Tier == ProviderTier.FreeCloud && provider.Id != "pexels" && !config.FreePlanConfirmed) throw new InvalidOperationException("Xác nhận tài khoản/resource đang dùng free tier trong Settings trước khi chạy; key của paid plan có thể phát sinh phí.");
            await registry.Get(provider.Id).Validate(config, stage, CancellationToken.None);
            if (provider.Tier == ProviderTier.Paid && step.Model.Length > 0 && step.Model != config.Model) throw new InvalidOperationException("Model khác bảng giá Settings. Cập nhật model và giá tương ứng trước khi chạy.");
            if (stage == Stage.Render) MediaEngine.ValidateTimeline(p);
            var estimate = BudgetService.Estimate(p, stage, config);
            var charges = await db.Charges.Where(x => x.ProjectId == id).ToListAsync();
            if (Committed(charges) + estimate > p.BudgetUsd) throw new InvalidOperationException($"Vượt ngân sách: đã dùng/giữ ${Committed(charges):0.######}, bước tiếp theo tối đa dự kiến ${estimate:0.######}. Tăng ngân sách hoặc đổi provider.");
            var job = new JobRow { ProjectId = id, Stage = stage.ToString(), ProviderId = provider.Id, ConfigJson = Json.Write(config) };
            step.Status = StepStatus.Queued; step.Error = null;
            SaveRevision(db, row, p, "Queue " + stage);
            job.RequestJson = Json.Write(p);
            db.Jobs.Add(job);
            db.Charges.Add(new ChargeRow { ProjectId = id, JobId = job.Id, ProviderId = provider.Id, Stage = stage.ToString(), AmountUsd = estimate, PriceVersion = config.PriceVersion, Note = provider.Pricing == ProviderPricing.SubscriptionReserve ? "Subscription reservation, not a provider spending cap; reconcile extra usage with your account." : provider.Tier is ProviderTier.Paid ? "Conservative estimate; provider invoice is authoritative." : "No API charge assumed for the selected free/local tier; check account plan." });
            await db.SaveChangesAsync(); return job.Id;
        }
        finally { Gate.Release(); }
    }
    public async Task Cancel(Guid id)
    {
        await Gate.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync(); var job = await db.Jobs.FindAsync(id) ?? throw new InvalidOperationException("Job không tồn tại.");
            job.CancelRequested = true;
            if (job.Status == nameof(JobStatus.Queued))
            {
                job.Status = nameof(JobStatus.Cancelled);
                var charge = await db.Charges.SingleOrDefaultAsync(x => x.JobId == id); if (charge is not null) charge.Status = nameof(ChargeStatus.Released);
                var row = await db.Projects.FindAsync(job.ProjectId); var p = Json.Read<Project>(row!.Json);
                if (Enum.TryParse<Stage>(job.Stage, out var stage)) p.Steps[stage].Status = StepStatus.Cancelled;
                SaveRevision(db, row, p, "Cancel queued job");
            }
            await db.SaveChangesAsync(); signals.Cancel(id);
        }
        finally { Gate.Release(); }
    }
    public async Task Resume(Guid id)
    {
        await Gate.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync(); var job = await db.Jobs.FindAsync(id) ?? throw new InvalidOperationException("Job không tồn tại.");
            if (job.Status != nameof(JobStatus.NeedsAttention) || job.Checkpoint is null || (job.Stage != "YouTube" && !registry.Get(job.ProviderId).CanResume(job.Checkpoint))) throw new InvalidOperationException("Job không có checkpoint có thể tiếp tục an toàn.");
            await EnsureIdle(db, job.ProjectId);
            var row = await db.Projects.FindAsync(job.ProjectId); var p = Json.Read<Project>(row!.Json);
            if (InputSignature(p) != InputSignature(Json.Read<Project>(job.RequestJson))) throw new InvalidOperationException("Dự án đã đổi sau khi gửi job. Đối soát job cũ trước; không tự gắn kết quả vào phiên bản mới.");
            job.Status = nameof(JobStatus.Queued); job.CancelRequested = false; job.Error = null;
            if (Enum.TryParse<Stage>(job.Stage, out var stage)) p.Steps[stage].Status = StepStatus.Queued;
            SaveRevision(db, row, p, "Resume existing remote job"); await db.SaveChangesAsync();
        }
        finally { Gate.Release(); }
    }
    public async Task Reconcile(Guid id, decimal actualUsd)
    {
        if (actualUsd < 0) throw new InvalidOperationException("Chi phí không hợp lệ.");
        await Gate.WaitAsync();
        try
        {
            await using var db = await factory.CreateDbContextAsync(); var job = await db.Jobs.FindAsync(id) ?? throw new InvalidOperationException("Job không tồn tại.");
            if (job.Status is "Running" or "Queued") throw new InvalidOperationException("Đợi tác vụ dừng trước khi đối soát.");
            var charge = await db.Charges.SingleOrDefaultAsync(x => x.JobId == id);
            if (charge is not null) { charge.AmountUsd = actualUsd; charge.Status = nameof(ChargeStatus.Confirmed); charge.Note = "Manually reconciled against provider account by the administrator."; }
            if (job.Status == nameof(JobStatus.NeedsAttention)) { job.Status = nameof(JobStatus.Failed); job.Error = "Đã đối soát thủ công. Có thể tạo lần chạy mới."; }
            await db.SaveChangesAsync();
        }
        finally { Gate.Release(); }
    }
    public static async Task EnsureIdle(StudioDb db, Guid id)
    {
        if (await db.Jobs.AnyAsync(j => j.ProjectId == id && (j.Status == "Running" || j.Status == "Queued"))) throw new InvalidOperationException("Dự án đang có tác vụ. Chờ hoặc hủy trước khi sửa/chạy lại.");
    }
    public static void SaveRevision(StudioDb db, ProjectRow row, Project next, string reason)
    {
        var old = Json.Read<Project>(row.Json);
        db.Revisions.Add(new RevisionRow { ProjectId = row.Id, Version = old.Revision, Json = row.Json, Reason = reason });
        next.Revision = old.Revision + 1; next.UpdatedAt = DateTimeOffset.UtcNow; row.Json = Json.Write(next);
    }
    private static string InputSignature(Project project)
    {
        var p = Json.Clone(project); p.Revision = 0; p.UpdatedAt = p.CreatedAt;
        foreach (var s in p.Steps.Values) { s.Status = StepStatus.Pending; s.Error = null; }
        return Json.Write(p);
    }
}
