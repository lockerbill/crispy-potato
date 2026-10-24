using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Jobs;
using FacelessStudio.Core.Media;
using FacelessStudio.Core.Models;
using FacelessStudio.Core.Providers;
using FacelessStudio.Core.Settings;
using FacelessStudio.Core.Subtitles;

namespace FacelessStudio.Core.Pipeline;

/// <summary>Chạy 7 bước: chủ đề → kịch bản → giọng đọc → footage → timing phụ đề → dựng → thumbnail.</summary>
public sealed partial class VideoPipeline(ProviderRegistry registry, SettingsStore settingsStore, JobManager jobs)
{
    /// <summary>Khoảng lặng thêm vào cuối mỗi cảnh cho giọng đọc tự nhiên.</summary>
    public const double ScenePause = 0.35;

    public async Task RunAsync(VideoJob job)
    {
        var ct = job.Cancellation.Token;
        var settings = settingsStore.Current;
        var ffmpeg = FfmpegTool.FromSettings(settings);
        var request = job.Request;
        job.Update(j => j.Status = JobStatus.Running);

        try
        {
            if (!await ffmpeg.IsAvailableAsync())
                throw new ProviderException("Không tìm thấy FFmpeg. Cài FFmpeg hoặc khai báo đường dẫn trong Cài đặt.");

            // 1. Chủ đề
            var tempCtx = NewContext(job, settings, ffmpeg, Path.GetTempPath());
            await RunStepAsync(job, PipelineStep.Topic, 2, async () =>
            {
                if (!string.IsNullOrWhiteSpace(request.Topic)) return "Dùng chủ đề anh đã nhập.";
                var provider = registry.Get<ITopicProvider>(PipelineStep.Topic, ProviderId(request, PipelineStep.Topic));
                var ideas = await provider.SuggestAsync(new TopicRequest(request.Notes, 5), tempCtx);
                if (ideas.Count == 0) throw new ProviderException("Không tìm được chủ đề nào. Hãy nhập chủ đề.");
                request.Topic = ideas[0].Title;
                return $"Chọn chủ đề: {ideas[0].Title}";
            });

            var folderName = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Slugify(request.Topic)}";
            var folder = Path.Combine(jobs.OutputDir, folderName);
            var work = Path.Combine(folder, "work");
            Directory.CreateDirectory(work);
            job.Update(j => j.Folder = folderName);
            jobs.Save(job);
            var ctx = NewContext(job, settings, ffmpeg, work);

            // 2. Kịch bản
            VideoScript script = null!;
            await RunStepAsync(job, PipelineStep.Script, 5, async () =>
            {
                var provider = registry.Get<IScriptProvider>(PipelineStep.Script, ProviderId(request, PipelineStep.Script));
                script = KeepCustomNarration(await provider.WriteAsync(ctx), request);
                if (script.Scenes.Count == 0) throw new ProviderException("Kịch bản không có cảnh nào.");
                await WriteJsonAsync(Path.Combine(folder, "script.json"), script, ct);
                return $"\"{script.Title}\" ({script.Scenes.Count} cảnh)";
            });

            // 3. Giọng đọc
            var audio = new List<SceneAudio>();
            var narration = Path.Combine(work, "narration.wav");
            await RunStepAsync(job, PipelineStep.Voice, 15, async () =>
            {
                var tts = registry.Get<ITtsProvider>(PipelineStep.Voice, ProviderId(request, PipelineStep.Voice));
                double start = 0;
                for (var i = 0; i < script.Scenes.Count; i++)
                {
                    ReportProgress(job, 15 + 25 * i / script.Scenes.Count, $"Giọng đọc cảnh {i + 1}/{script.Scenes.Count}...");
                    var res = await tts.SynthesizeAsync(script.Scenes[i].Narration, Path.Combine(work, $"voice_{i:000}"), ctx);
                    var wav = Path.Combine(work, $"scene_{i:000}.wav");
                    await ffmpeg.ToWavAsync(res.AudioPath, wav, ScenePause, ct: ct);
                    var duration = await ffmpeg.ProbeDurationAsync(wav, ct);
                    audio.Add(new SceneAudio
                    {
                        Index = i,
                        WavPath = wav,
                        Start = start,
                        Duration = duration,
                        SpeechDuration = Math.Max(0.1, duration - ScenePause),
                        TtsWords = res.Words,
                    });
                    start += duration;
                }
                await ffmpeg.ConcatAsync(audio.Select(a => a.WavPath).ToList(), narration, ct);
                return $"Tổng {audio.Sum(a => a.Duration):0.0} giây";
            });

            // 4. Hình ảnh / footage
            var length = request.LengthPreset;
            var renderScenes = new List<RenderScene>();
            await RunStepAsync(job, PipelineStep.Media, 40, async () =>
            {
                var media = registry.Get<IMediaProvider>(PipelineStep.Media, ProviderId(request, PipelineStep.Media));
                var fallbacks = 0;
                for (var i = 0; i < script.Scenes.Count; i++)
                {
                    ReportProgress(job, 40 + 25 * i / script.Scenes.Count, $"Hình ảnh cảnh {i + 1}/{script.Scenes.Count}...");
                    var a = audio[i];
                    var count = Math.Clamp((int)Math.Ceiling(a.Duration / (length.Vertical ? 4.5 : 7.0)), 1, 3);
                    IReadOnlyList<MediaAsset> assets;
                    try
                    {
                        assets = await media.GetAsync(
                            new MediaRequest(script.Scenes[i], i, count, a.Duration, length.Width, length.Height), ctx);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        ctx.Log($"Cảnh {i + 1}: {media.Name} lỗi ({e.Message}), dùng nền màu.");
                        assets = [];
                    }
                    if (assets.Count == 0)
                    {
                        fallbacks++;
                        assets = [new MediaAsset(MediaKind.Gradient, null, null)];
                    }
                    renderScenes.Add(new RenderScene(i, a.Start, a.Duration, assets));
                }
                return fallbacks == 0 ? $"{renderScenes.Sum(s => s.Media.Count)} media" : $"{fallbacks} cảnh dùng nền màu";
            });

            // 5. Timing phụ đề
            IReadOnlyList<WordTiming> words = [];
            var assPath = Path.Combine(folder, "subtitles.ass");
            var srtPath = Path.Combine(folder, "subtitles.srt");
            await RunStepAsync(job, PipelineStep.Timing, 66, async () =>
            {
                var timing = registry.Get<ITimingProvider>(PipelineStep.Timing, ProviderId(request, PipelineStep.Timing));
                words = await timing.AlignAsync(new TimingRequest(audio, script.Scenes, narration), ctx);
                var groups = SubtitleWriter.Group(words, SubtitleWriter.MaxWordsFor(length.Vertical));
                await File.WriteAllTextAsync(assPath,
                    SubtitleWriter.ToAssKaraoke(groups, length.Width, length.Height, SubtitleFont(settings)), ct);
                await File.WriteAllTextAsync(srtPath, SubtitleWriter.ToSrt(groups), ct);
                return $"{words.Count} chữ, {groups.Count} cụm phụ đề";
            });

            // 6. Dựng video
            var videoPath = Path.Combine(folder, "video.mp4");
            await RunStepAsync(job, PipelineStep.Render, 72, async () =>
            {
                var renderer = registry.Get<IRenderProvider>(PipelineStep.Render, ProviderId(request, PipelineStep.Render));
                await renderer.RenderAsync(new RenderPlan
                {
                    Width = length.Width,
                    Height = length.Height,
                    WorkDir = work,
                    OutputPath = videoPath,
                    NarrationPath = narration,
                    Scenes = renderScenes,
                    Words = words,
                    AssPath = assPath,
                    SrtPath = srtPath,
                    MusicPath = PickMusic(settings),
                }, ctx);
                if (!File.Exists(videoPath)) throw new ProviderException("Renderer không tạo ra file video.");
                return $"{await ffmpeg.ProbeDurationAsync(videoPath, ct):0.0} giây";
            });

            // 7. Thumbnail
            var thumbPath = Path.Combine(folder, "thumbnail.jpg");
            await RunStepAsync(job, PipelineStep.Thumbnail, 94, async () =>
            {
                var thumb = registry.Get<IThumbnailProvider>(PipelineStep.Thumbnail, ProviderId(request, PipelineStep.Thumbnail));
                var frame = await PrepareFrameAsync(renderScenes, work, ffmpeg, ct);
                await thumb.CreateAsync(new ThumbnailRequest(script, frame, thumbPath), ctx);
                return "Xong";
            });

            var result = new JobResult
            {
                Title = script.Title,
                Description = script.Description,
                Tags = script.Tags,
                DurationSeconds = Math.Round(audio.Sum(a => a.Duration), 1),
            };
            await WriteJsonAsync(Path.Combine(folder, "metadata.json"), result, ct);
            if (Environment.GetEnvironmentVariable("FACELESS_KEEP_WORK") != "1")
                TryDelete(work);

            job.Update(j =>
            {
                j.Result = result;
                j.Status = JobStatus.Done;
                j.Progress = 100;
                j.Message = "Hoàn tất!";
                j.FinishedAt = DateTime.Now;
            });
            job.Log("Hoàn tất!");
        }
        catch (OperationCanceledException)
        {
            job.Update(j =>
            {
                j.Status = JobStatus.Cancelled;
                j.Message = "Đã huỷ.";
                j.FinishedAt = DateTime.Now;
            });
        }
        catch (Exception e)
        {
            var message = e is ProviderException or FfmpegException ? e.Message : $"{e.GetType().Name}: {e.Message}";
            job.Update(j =>
            {
                j.Status = JobStatus.Failed;
                j.Error = message;
                j.Message = "Lỗi: " + message;
                j.FinishedAt = DateTime.Now;
            });
            job.Log("LỖI: " + message);
        }
        finally
        {
            jobs.Save(job);
        }
    }

    private StepContext NewContext(VideoJob job, AppSettings settings, FfmpegTool ffmpeg, string workDir) => new()
    {
        Request = job.Request,
        Settings = settings,
        WorkDir = workDir,
        Ffmpeg = ffmpeg,
        Log = job.Log,
        CancellationToken = job.Cancellation.Token,
    };

    private static string? ProviderId(VideoRequest r, PipelineStep step) => r.Providers.GetValueOrDefault(step);

    private async Task RunStepAsync(VideoJob job, PipelineStep step, int progress, Func<Task<string>> body)
    {
        job.Cancellation.Token.ThrowIfCancellationRequested();
        var state = job.StepOf(step);
        job.Update(j =>
        {
            state.Status = StepStatus.Running;
            j.Progress = progress;
            j.Message = $"{StepInfo.Label(step)}: {state.ProviderName}...";
        });
        job.Log($"▶ {StepInfo.Label(step)} ({state.ProviderName})");
        try
        {
            var message = await body();
            job.Update(_ =>
            {
                state.Status = step == PipelineStep.Topic && message.StartsWith("Dùng chủ đề") ? StepStatus.Skipped : StepStatus.Done;
                state.Message = message;
            });
            job.Log($"✓ {StepInfo.Label(step)}: {message}");
        }
        catch (Exception e)
        {
            job.Update(_ =>
            {
                state.Status = StepStatus.Failed;
                state.Message = e.Message;
            });
            throw;
        }
        jobs.Save(job);
    }

    private static void ReportProgress(VideoJob job, int progress, string message) =>
        job.Update(j =>
        {
            j.Progress = progress;
            j.Message = message;
        });

    /// <summary>Kịch bản tự nhập: luôn giữ nguyên lời người dùng viết, chỉ lấy metadata và từ khoá từ AI.</summary>
    public static VideoScript KeepCustomNarration(VideoScript script, VideoRequest request)
    {
        var paragraphs = request.CustomParagraphs;
        if (paragraphs.Count == 0) return script;
        script.Scenes = paragraphs.Select((p, i) =>
        {
            var ai = i < script.Scenes.Count ? script.Scenes[i] : null;
            return new Scene(p, ai?.FootageQuery is { Length: > 0 } q ? q : request.Topic,
                ai?.ImagePrompt is { Length: > 0 } ip ? ip : request.Topic);
        }).ToList();
        if (string.IsNullOrWhiteSpace(script.Title)) script.Title = request.Topic;
        if (string.IsNullOrWhiteSpace(script.ThumbnailText)) script.ThumbnailText = script.Title;
        return script;
    }

    private static async Task<string?> PrepareFrameAsync(IReadOnlyList<RenderScene> scenes, string work,
        FfmpegTool ffmpeg, CancellationToken ct)
    {
        var first = scenes.SelectMany(s => s.Media).FirstOrDefault(m => m.LocalPath != null && m.Kind != MediaKind.Gradient);
        var frame = Path.Combine(work, "frame.jpg");
        try
        {
            switch (first)
            {
                case { Kind: MediaKind.Image }:
                    return first.LocalPath;
                case { Kind: MediaKind.Video }:
                    await ffmpeg.ExtractFrameAsync(first.LocalPath!, 1.0, frame, ct);
                    return frame;
                default:
                    await ffmpeg.RunAsync(["-f", "lavfi", "-i", "gradients=s=1280x720:c0=0x141e30:c1=0x243b55:d=1",
                        "-frames:v", "1", frame], ct: ct);
                    return frame;
            }
        }
        catch (FfmpegException)
        {
            return null;
        }
    }

    private static string SubtitleFont(AppSettings s) =>
        s.Has(SettingKeys.SubtitleFont) ? s.Get(SettingKeys.SubtitleFont)
        : OperatingSystem.IsLinux() ? "DejaVu Sans" : "Arial";

    private static string? PickMusic(AppSettings s)
    {
        var dir = s.ResolvePath(SettingKeys.MusicDir);
        if (!Path.IsPathRooted(dir)) dir = Path.Combine(s.BaseDir, dir);
        if (!Directory.Exists(dir)) return null;
        var tracks = Directory.EnumerateFiles(dir)
            .Where(f => new[] { ".mp3", ".m4a", ".wav", ".ogg" }.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToList();
        return tracks.Count == 0 ? null : tracks[Random.Shared.Next(tracks.Count)];
    }

    private static Task WriteJsonAsync<T>(string path, T value, CancellationToken ct) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JobManager.JsonOptions), ct);

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [GeneratedRegex("[^a-zA-Z0-9]+")]
    private static partial Regex NonAlnum();

    public static string Slugify(string text, int maxLength = 40)
    {
        var normalized = text.Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD);
        var ascii = new string(normalized
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .ToArray());
        var slug = NonAlnum().Replace(ascii, "-").Trim('-').ToLowerInvariant();
        if (slug.Length > maxLength) slug = slug[..maxLength].Trim('-');
        return slug.Length == 0 ? "video" : slug;
    }
}
