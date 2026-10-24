using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Jobs;
using FacelessStudio.Core.Models;
using FacelessStudio.Core.Pipeline;
using FacelessStudio.Core.Providers;
using FacelessStudio.Core.Settings;
using FacelessStudio.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace FacelessStudio.Tests;

public class PipelineTests
{
    internal static ServiceProvider BuildServices() =>
        new ServiceCollection().AddFacelessProviders().BuildServiceProvider();

    internal static ProviderRegistry Registry(ServiceProvider sp) => new(sp.GetServices<IStepProvider>());

    [Fact]
    public void Every_step_has_a_provider_and_ids_are_unique()
    {
        using var sp = BuildServices();
        var registry = Registry(sp);
        foreach (var step in StepInfo.All) Assert.NotEmpty(registry.For(step));
    }

    [Fact]
    public void Presets_fall_back_to_usable_providers_without_keys()
    {
        using var sp = BuildServices();
        var registry = Registry(sp);
        var settings = AppSettings.Empty();
        foreach (var preset in BudgetPresets.All)
        {
            var map = BudgetPresets.Resolve(preset, registry, settings, "vi");
            Assert.Equal(StepInfo.All.Length, map.Count);
            Assert.All(map, kv => Assert.True(registry.Find(kv.Value)!.IsConfigured(settings), kv.Value));
        }
    }

    /// <summary>
    /// Chạy trọn quy trình thật: kịch bản tự nhập + Edge TTS + nền màu + FFmpeg + thumbnail.
    /// Cần mạng và FFmpeg nên chỉ chạy khi đặt FACELESS_E2E=1.
    /// </summary>
    [Theory]
    [InlineData("vi", "short30", "timing-tts", "tts-edge")]
    [InlineData("en", "long3", "timing-tts", "tts-edge")]
    [InlineData("vi", "short30", "timing-whisper-net", "tts-edge")]
    [InlineData("vi", "short30", "timing-whisper-net", "tts-piper")]
    public async Task End_to_end_with_free_providers(string language, string length, string timing, string voice)
    {
        if (Environment.GetEnvironmentVariable("FACELESS_E2E") != "1") return;
        // Whisper.net phải tải model (~75 MB với tiny) nên bật riêng.
        if (timing == "timing-whisper-net" && Environment.GetEnvironmentVariable("FACELESS_E2E_WHISPER") != "1") return;
        // Piper cần PIPER_PATH và PIPER_VOICE_VI trỏ tới binary và giọng .onnx.
        if (voice == "tts-piper" && Environment.GetEnvironmentVariable("PIPER_VOICE_VI") == null) return;

        using var sp = BuildServices();
        var registry = Registry(sp);
        var baseDir = Path.Combine(Path.GetTempPath(), "faceless-e2e-" + Guid.NewGuid().ToString("N")[..6]);
        var store = new SettingsStore(baseDir);
        var jobs = new JobManager(registry, Path.Combine(baseDir, "output"));
        var pipeline = new VideoPipeline(registry, store, jobs);

        var script = language == "vi"
            ? "Bạn có biết đại dương chiếm hơn bảy mươi phần trăm bề mặt Trái Đất không?\n\n" +
              "Con người mới khám phá chưa tới một phần tư đáy biển.\n\nHãy theo dõi kênh để biết thêm nhé!"
            : "Did you know the ocean covers more than seventy percent of our planet?\n\n" +
              "We have mapped less than a quarter of the sea floor in detail.\n\nSubscribe for more ocean facts!";
        var request = new VideoRequest
        {
            Topic = language == "vi" ? "Bí ẩn đại dương" : "Ocean mysteries",
            Language = language,
            Length = length,
            CustomScript = script,
            Providers = new()
            {
                [PipelineStep.Topic] = "topic-manual",
                [PipelineStep.Script] = "script-custom",
                [PipelineStep.Voice] = voice,
                [PipelineStep.Media] = "media-gradient",
                [PipelineStep.Timing] = timing,
                [PipelineStep.Render] = "render-ffmpeg",
                [PipelineStep.Thumbnail] = "thumb-local",
            },
        };
        var job = jobs.Enqueue(request);
        Environment.SetEnvironmentVariable("FACELESS_KEEP_WORK", "1");
        await pipeline.RunAsync(job);

        Assert.True(job.Status == JobStatus.Done, job.Error + "\n" + string.Join('\n', job.Logs));
        var folder = jobs.FolderPath(job)!;
        Assert.True(File.Exists(Path.Combine(folder, "video.mp4")));
        Assert.True(File.Exists(Path.Combine(folder, "thumbnail.jpg")));
        Assert.Contains("-->", await File.ReadAllTextAsync(Path.Combine(folder, "subtitles.srt")));
        Console.WriteLine("E2E output: " + folder);
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), $"faceless-e2e-{language}-{timing}-{voice}.txt"), folder);
    }
}
