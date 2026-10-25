using Studio.Domain;
using Studio.Providers;

namespace Studio.Services;

/// <summary>UI-facing operations. Saving configuration never generates content.</summary>
public sealed class ProviderConnectionService(SettingsService settings, ProviderRegistry registry, ISubscriptionAuth subscriptions, StudioPaths paths)
{
    public async Task<ProviderConnectionStatus> Status(string id, CancellationToken ct)
    {
        var def = ProviderCatalog.Get(id);
        if (def.Auth == ProviderAuth.OAuth) return await subscriptions.Status(id, ct);
        var cfg = await settings.GetProvider(id);
        if (def.Auth == ProviderAuth.ClaudeCode)
        {
            if (!cfg.Configured) return new(false, "Chưa cấu hình Claude Code / Claude Code is not configured.");
            try { return new(true, await registry.Get(id).CheckConnection(cfg, null, ct)); }
            catch (Exception e) when (e is ProviderException or InvalidOperationException)
            { return new(false, e.Message); }
        }
        return new(cfg.Configured, cfg.Configured
            ? "Đã lưu cấu hình; kiểm tra quyền inference bằng nút thử nội dung / Configuration saved; use sample generation to verify inference access."
            : "Chưa có API key / API key is missing.");
    }

    public async Task<string> Check(ProviderConfig cfg, CancellationToken ct)
    {
        var result = await registry.Get(cfg.Id).CheckConnection(cfg, await settings.Secret("provider:" + cfg.Id), ct);
        return result;
    }

    public async Task<IReadOnlyList<ProviderModel>> Models(ProviderConfig cfg, CancellationToken ct)
    {
        if (registry.Get(cfg.Id) is IProviderModelSource source)
            return await source.GetModels(cfg, await settings.Secret("provider:" + cfg.Id), ct);
        return string.IsNullOrWhiteSpace(cfg.Model) ? [] : [new(cfg.Model, cfg.Model)];
    }

    public async Task<string> TestGeneration(ProviderConfig cfg, CancellationToken ct)
    {
        if (!ProviderCatalog.Get(cfg.Id).IsText)
            throw new InvalidOperationException("Chỉ thử nội dung văn bản / Sample generation is available for text providers.");
        if (string.IsNullOrWhiteSpace(cfg.Model))
            throw new InvalidOperationException("Chọn model trước / Select a model first.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var token = timeout.Token;
        var adapter = registry.Get(cfg.Id);
        await adapter.Validate(cfg, Stage.Topic, token);
        var project = new Project { Name = "Connection test", Brief = "Suggest a short video topic about the ocean.", Language = "en" };
        ProviderCatalog.ApplyPreset(project, "free");
        project.Steps[Stage.Topic].ProviderId = cfg.Id;
        project.Steps[Stage.Topic].Model = cfg.Model;
        var id = Guid.NewGuid();
        var work = Path.Combine(paths.Root, "connection-tests", id.ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            await adapter.Execute(new ProviderContext
            {
                JobId = id, Project = project, Stage = Stage.Topic, Config = cfg,
                Secret = await settings.Secret("provider:" + cfg.Id), WorkDirectory = work,
                SaveCheckpoint = _ => Task.CompletedTask
            }, token);
            return "Inference thành công, JSON hợp lệ. Lần thử có thể dùng quota/phát sinh phí ngoài ngân sách dự án / Inference succeeded with valid JSON. This test may consume quota or incur charges outside the project budget.";
        }
        finally { if (Directory.Exists(work)) Directory.Delete(work, true); }
    }

    public async Task Disconnect(string id, CancellationToken ct)
    {
        var cfg = await settings.GetProvider(id);
        var def = ProviderCatalog.Get(id);
        if (def.Auth == ProviderAuth.OAuth) await subscriptions.Disconnect(id, ct);
        // Claude retains ownership of its login; forgetting the executable only disconnects Studio.
        if (def.Auth == ProviderAuth.ClaudeCode) cfg.Executable = "";
        cfg.LastCheck = null;
        cfg.CheckedAt = null;
        await settings.SaveProvider(cfg, clearSecret: true);
    }
}
