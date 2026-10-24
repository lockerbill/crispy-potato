using System.Globalization;
using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Media;
using FacelessStudio.Core.Models;
using static FacelessStudio.Core.Media.FfmpegTool;

namespace FacelessStudio.Providers.Render;

/// <summary>Dựng video trên máy bằng FFmpeg: footage/ảnh + giọng đọc + phụ đề karaoke + nhạc nền.</summary>
public sealed class FfmpegRenderProvider : ProviderBase, IRenderProvider
{
    private static readonly (string C0, string C1)[] Palettes =
    [
        ("0x0f2027", "0x2c5364"), ("0x1a2a6c", "0xb21f1f"), ("0x232526", "0x414345"),
        ("0x141e30", "0x243b55"), ("0x42275a", "0x734b6d"), ("0x093028", "0x237a57"),
    ];

    public override string Id => "render-ffmpeg";
    public override string Name => "FFmpeg (trên máy)";
    public override PipelineStep Step => PipelineStep.Render;
    public override ProviderCategory Category => ProviderCategory.FreeSelfHosted;
    public override string Description =>
        "Dựng ngay trên máy anh. Chất lượng tốt nhất, phụ đề karaoke tô màu từng chữ, không watermark.";

    public async Task RenderAsync(RenderPlan plan, StepContext context)
    {
        var ff = context.Ffmpeg;
        var ct = context.CancellationToken;
        var segments = new List<string>();
        foreach (var scene in plan.Scenes)
        {
            ct.ThrowIfCancellationRequested();
            segments.AddRange(await RenderSceneAsync(plan, scene, ff, ct));
        }
        var videoOnly = Path.Combine(plan.WorkDir, "video_only.mp4");
        await ff.ConcatAsync(segments, videoOnly, ct);
        context.Log($"Đã dựng {segments.Count} đoạn hình, đang ghép phụ đề và âm thanh...");

        // Chạy trong WorkDir và dùng tên file tương đối để filter subtitles không phải escape đường dẫn Windows.
        File.Copy(plan.AssPath, Path.Combine(plan.WorkDir, "subs.ass"), overwrite: true);
        var args = new List<string> { "-i", "video_only.mp4", "-i", Path.GetFullPath(plan.NarrationPath) };
        string audioFilter;
        if (plan.MusicPath != null)
        {
            args.AddRange(["-stream_loop", "-1", "-i", Path.GetFullPath(plan.MusicPath)]);
            audioFilter = "[2:a]volume=0.12[m];[1:a][m]amix=inputs=2:duration=first:normalize=0[a]";
            context.Log($"Nhạc nền: {Path.GetFileName(plan.MusicPath)}");
        }
        else
        {
            audioFilter = "[1:a]anull[a]";
        }
        args.AddRange([
            "-filter_complex", $"[0:v]subtitles=subs.ass[v];{audioFilter}",
            "-map", "[v]", "-map", "[a]",
            "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "192k", "-shortest", "-movflags", "+faststart",
            Path.GetFullPath(plan.OutputPath),
        ]);
        await ff.RunAsync(args, plan.WorkDir, ct);
    }

    private static List<string> EncodeArgs(int fps) =>
        ["-an", "-c:v", "libx264", "-preset", "veryfast", "-crf", "21", "-pix_fmt", "yuv420p", "-r", fps.ToString(CultureInfo.InvariantCulture)];

    private static string CoverFilter(int w, int h, int fps) =>
        $"scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h},fps={fps},setsar=1,format=yuv420p";

    private static async Task<List<string>> RenderSceneAsync(RenderPlan plan, RenderScene scene, FfmpegTool ff, CancellationToken ct)
    {
        var (w, h, fps) = (plan.Width, plan.Height, plan.Fps);
        var result = new List<string>();
        var part = scene.Duration / scene.Media.Count;
        for (var j = 0; j < scene.Media.Count; j++)
        {
            var media = scene.Media[j];
            var seg = Path.Combine(plan.WorkDir, $"seg_{scene.Index:000}_{j}.mp4");
            var args = new List<string>();
            switch (media.Kind)
            {
                case MediaKind.Video when media.LocalPath != null:
                {
                    var clipLength = await ff.ProbeDurationAsync(media.LocalPath, ct);
                    // Clip dài thì bắt đầu ở vị trí ngẫu nhiên cho đỡ lặp; clip ngắn thì lặp lại.
                    var start = clipLength > part + 1 ? Random.Shared.NextDouble() * (clipLength - part) : 0;
                    args.AddRange(["-stream_loop", "-1", "-ss", F(start), "-i", media.LocalPath,
                        "-t", F(part), "-vf", CoverFilter(w, h, fps)]);
                    break;
                }
                case MediaKind.Image when media.LocalPath != null:
                {
                    // Hiệu ứng Ken Burns: phóng to chậm, đổi hướng giữa các ảnh.
                    var frames = Math.Max(1, (int)Math.Ceiling(part * fps));
                    var zoom = (scene.Index + j) % 2 == 0
                        ? $"min(1+0.12*on/{frames},1.12)"
                        : $"max(1.12-0.12*on/{frames},1)";
                    args.AddRange(["-i", media.LocalPath, "-vf",
                        $"scale={w * 2}:{h * 2}:force_original_aspect_ratio=increase,crop={w * 2}:{h * 2}," +
                        $"zoompan=z='{zoom}':x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':d={frames}:s={w}x{h}:fps={fps}," +
                        "setsar=1,format=yuv420p",
                        "-frames:v", frames.ToString(CultureInfo.InvariantCulture)]);
                    break;
                }
                default:
                {
                    var (c0, c1) = Palettes[scene.Index % Palettes.Length];
                    args.AddRange(["-f", "lavfi", "-i",
                        $"gradients=s={w}x{h}:c0={c0}:c1={c1}:speed=0.015:r={fps}:d={F(part)}",
                        "-vf", "format=yuv420p"]);
                    break;
                }
            }
            args.AddRange(EncodeArgs(fps));
            args.Add(seg);
            await ff.RunAsync(args, ct: ct);
            result.Add(seg);
        }
        return result;
    }
}
