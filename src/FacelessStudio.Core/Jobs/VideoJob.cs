using System.Text.Json.Serialization;
using FacelessStudio.Core.Models;

namespace FacelessStudio.Core.Jobs;

public enum JobStatus
{
    Queued,
    Running,
    Done,
    Failed,
    Cancelled,
}

public enum StepStatus
{
    Pending,
    Running,
    Done,
    Skipped,
    Failed,
}

public sealed class StepState
{
    public PipelineStep Step { get; set; }
    public string ProviderId { get; set; } = "";
    public string ProviderName { get; set; } = "";
    public StepStatus Status { get; set; } = StepStatus.Pending;
    public string Message { get; set; } = "";
}

public sealed class JobResult
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public string VideoFile { get; set; } = "video.mp4";
    public string ThumbnailFile { get; set; } = "thumbnail.jpg";
    public string SubtitlesFile { get; set; } = "subtitles.srt";
    public double DurationSeconds { get; set; }
}

/// <summary>Một lần tạo video. Được ghi ra job.json trong thư mục kết quả.</summary>
public sealed class VideoJob
{
    private readonly Lock _lock = new();

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? FinishedAt { get; set; }
    public VideoRequest Request { get; set; } = new();
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public int Progress { get; set; }
    public string Message { get; set; } = "Đang chờ tới lượt...";
    public List<string> Logs { get; set; } = [];
    public List<StepState> Steps { get; set; } = [];
    public string? Error { get; set; }

    /// <summary>Tên thư mục kết quả (nằm trong thư mục output).</summary>
    public string? Folder { get; set; }

    public double EstimatedCostUsd { get; set; }
    public JobResult? Result { get; set; }

    [JsonIgnore] public CancellationTokenSource Cancellation { get; } = new();

    /// <summary>Báo cho giao diện biết job vừa thay đổi.</summary>
    public event Action? Changed;

    public StepState StepOf(PipelineStep step) => Steps.First(s => s.Step == step);

    public void Update(Action<VideoJob> change)
    {
        lock (_lock) change(this);
        Changed?.Invoke();
    }

    public void Log(string message) =>
        Update(j => j.Logs.Add($"[{DateTime.Now:HH:mm:ss}] {message}"));

    public List<string> SnapshotLogs()
    {
        lock (_lock) return [.. Logs];
    }
}
