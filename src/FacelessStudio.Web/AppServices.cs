using FacelessStudio.Core.Jobs;
using FacelessStudio.Core.Pipeline;

namespace FacelessStudio.Web;

public static class AppPaths
{
    /// <summary>
    /// FACELESS_HOME nếu có; không thì thư mục gốc repo (chỗ có FacelessStudio.slnx) để data/ và output/
    /// nằm cạnh mã nguồn khi chạy bằng dotnet run; cuối cùng là thư mục của ứng dụng.
    /// </summary>
    public static string ResolveHome(string? configured, string contentRoot)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        for (var dir = new DirectoryInfo(contentRoot); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "FacelessStudio.slnx"))) return dir.FullName;
        return contentRoot;
    }
}

/// <summary>Lấy job trong hàng đợi và chạy lần lượt (dựng video tốn CPU nên không chạy song song).</summary>
public sealed class JobRunner(JobManager jobs, VideoPipeline pipeline, ILogger<JobRunner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunQueueAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Ứng dụng đang tắt.
        }
    }

    private async Task RunQueueAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in jobs.Queue.ReadAllAsync(stoppingToken))
        {
            if (job.Cancellation.IsCancellationRequested)
            {
                job.Update(j =>
                {
                    j.Status = JobStatus.Cancelled;
                    j.Message = "Đã huỷ.";
                });
                continue;
            }
            logger.LogInformation("Bắt đầu job {Id}: {Topic}", job.Id, job.Request.Topic);
            await pipeline.RunAsync(job);
            logger.LogInformation("Job {Id} kết thúc: {Status} {Error}", job.Id, job.Status, job.Error);
        }
    }
}
