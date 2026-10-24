using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using FacelessStudio.Core.Models;
using FacelessStudio.Core.Providers;

namespace FacelessStudio.Core.Jobs;

/// <summary>Quản lý hàng đợi job và lịch sử video đã tạo (đọc từ thư mục output).</summary>
public sealed class JobManager
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ConcurrentDictionary<string, VideoJob> _jobs = new();
    private readonly Channel<VideoJob> _queue = Channel.CreateUnbounded<VideoJob>();
    private readonly ProviderRegistry _registry;

    public JobManager(ProviderRegistry registry, string outputDir)
    {
        _registry = registry;
        OutputDir = Path.GetFullPath(outputDir);
        Directory.CreateDirectory(OutputDir);
        LoadHistory();
    }

    public string OutputDir { get; }

    public ChannelReader<VideoJob> Queue => _queue.Reader;

    /// <summary>Có job mới hoặc job đổi trạng thái.</summary>
    public event Action? JobsChanged;

    public VideoJob Enqueue(VideoRequest request)
    {
        var job = new VideoJob
        {
            Request = request,
            Steps = StepInfo.All.Select(step =>
            {
                var p = request.Providers.TryGetValue(step, out var id) ? _registry.Find(id) : null;
                return new StepState { Step = step, ProviderId = p?.Id ?? "", ProviderName = p?.Name ?? "" };
            }).ToList(),
            EstimatedCostUsd = EstimateCost(request),
        };
        _jobs[job.Id] = job;
        job.Changed += () => JobsChanged?.Invoke();
        _queue.Writer.TryWrite(job);
        JobsChanged?.Invoke();
        return job;
    }

    public double EstimateCost(VideoRequest request) =>
        request.Providers.Values.Select(_registry.Find).Sum(p => p?.EstimateCostUsd(request) ?? 0);

    public VideoJob? Get(string id) => _jobs.GetValueOrDefault(id);

    public IReadOnlyList<VideoJob> All => _jobs.Values.OrderByDescending(j => j.CreatedAt).ToList();

    public void Cancel(string id)
    {
        if (_jobs.TryGetValue(id, out var job) && job.Status is JobStatus.Queued or JobStatus.Running)
            job.Cancellation.Cancel();
    }

    public void Save(VideoJob job)
    {
        if (job.Folder == null) return;
        var dir = Path.Combine(OutputDir, job.Folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "job.json"), JsonSerializer.Serialize(job, JsonOptions));
    }

    public string? FolderPath(VideoJob job) => job.Folder == null ? null : Path.Combine(OutputDir, job.Folder);

    private void LoadHistory()
    {
        foreach (var file in Directory.EnumerateFiles(OutputDir, "job.json", SearchOption.AllDirectories))
        {
            try
            {
                var job = JsonSerializer.Deserialize<VideoJob>(File.ReadAllText(file), JsonOptions);
                if (job == null) continue;
                // Job đang chạy dở khi app tắt thì coi như lỗi.
                if (job.Status is JobStatus.Queued or JobStatus.Running)
                {
                    job.Status = JobStatus.Failed;
                    job.Error = "Ứng dụng đã tắt khi job đang chạy.";
                }
                _jobs.TryAdd(job.Id, job);
            }
            catch (JsonException)
            {
                // Bỏ qua file hỏng.
            }
        }
    }
}
