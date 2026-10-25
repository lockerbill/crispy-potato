using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Studio.Domain;

namespace Studio.Services;

public sealed class MediaEngine(ProcessRunner processes, IConfiguration config, AssetStore assets)
{
    private string Ffmpeg => config["Studio:Ffmpeg"] ?? "ffmpeg";
    public async Task<double> Duration(string path, CancellationToken ct)
    {
        var raw = await processes.Run(config["Studio:Ffprobe"] ?? "ffprobe", ["-v", "error", "-show_entries", "format=duration", "-of", "json", path], ct);
        var text = JsonNode.Parse(raw)?["format"]?["duration"]?.GetValue<string>();
        if (!double.TryParse(text, CultureInfo.InvariantCulture, out var seconds) || !double.IsFinite(seconds) || seconds <= 0) throw new InvalidOperationException("Không đọc được thời lượng media.");
        return seconds;
    }
    public async Task ValidateMedia(string path, CancellationToken ct)
    {
        await processes.Run(config["Studio:Ffprobe"] ?? "ffprobe", ["-v", "error", "-show_entries", "stream=codec_type", "-of", "json", path], ct);
    }
    public async Task<string> NormalizeAudio(string source, string output, CancellationToken ct)
    {
        await processes.Run(Ffmpeg, ["-y", "-v", "error", "-i", source, "-vn", "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", output], ct);
        return output;
    }
    public async Task<string> Render(Project p, string dir, CancellationToken ct)
    {
        ValidateTimeline(p);
        var clips = new List<string>();
        for (var i = 0; i < p.Scenes.Count; i++)
        {
            var scene = p.Scenes[i];
            var media = await assets.Get(scene.MediaAssetId!, p.Id); var audio = await assets.Get(scene.AudioAssetId!, p.Id);
            var duration = Math.Max(scene.DurationSeconds, scene.AudioSeconds);
            var clip = Path.Combine(dir, $"scene-{i:D4}.mp4");
            var vf = $"scale={p.Width}:{p.Height}:force_original_aspect_ratio=increase,crop={p.Width}:{p.Height},setsar=1,fps=30";
            if (scene.Transition == "fade") vf += $",fade=t=in:st=0:d=0.2,fade=t=out:st={Number(Math.Max(0.2, duration - 0.2))}:d=0.2";
            var args = new List<string> { "-y", "-v", "error", "-filter_complex_threads", "2" };
            args.AddRange(media.ContentType.StartsWith("video/", StringComparison.Ordinal) ? ["-stream_loop", "-1"] : ["-loop", "1"]);
            args.AddRange(["-i", media.Path, "-i", audio.Path, "-filter_complex", $"[0:v]{vf}[v];[1:a]apad[a]", "-map", "[v]", "-map", "[a]", "-t", Number(duration), "-c:v", "libx264", "-preset", "veryfast", "-crf", "22", "-threads", "2", "-pix_fmt", "yuv420p", "-c:a", "aac", "-ar", "48000", "-ac", "2", clip]);
            await processes.Run(Ffmpeg, args, ct); clips.Add(Path.GetFileName(clip));
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "concat.txt"), string.Join('\n', clips.Select(c => $"file '{c}'")), ct);
        await File.WriteAllTextAsync(Path.Combine(dir, "captions.ass"), CaptionFiles.Ass(p), ct);
        var output = Path.Combine(dir, "video.mp4");
        await processes.Run(Ffmpeg, ["-y", "-v", "error", "-filter_threads", "2", "-f", "concat", "-safe", "1", "-i", "concat.txt", "-vf", "ass=captions.ass", "-c:v", "libx264", "-preset", "veryfast", "-crf", "21", "-threads", "2", "-pix_fmt", "yuv420p", "-c:a", "aac", "-movflags", "+faststart", output], ct, cwd: dir);
        var seconds = await Duration(output, ct);
        if (seconds > p.MaxSeconds + 0.15) throw new InvalidOperationException("Video dựng xong vượt giới hạn thời lượng. Hãy rút gọn cảnh.");
        return output;
    }
    public async Task<string> Thumbnail(string background, string title, string dir, CancellationToken ct)
    {
        var textFile = Path.Combine(dir, "thumbnail-title.txt");
        var words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries); var lines = new List<string>(); var line = "";
        foreach (var word in words) { if (line.Length + word.Length > 28 && line.Length > 0) { lines.Add(line); line = ""; } line += (line.Length == 0 ? "" : " ") + word; }
        if (line.Length > 0) lines.Add(line);
        await File.WriteAllTextAsync(textFile, string.Join('\n', lines), ct);
        var font = config["Studio:FontFile"] ?? "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
        if (!File.Exists(font)) throw new InvalidOperationException("Thiếu font thumbnail. Cấu hình Studio:FontFile với font hỗ trợ tiếng Việt.");
        var output = Path.Combine(dir, "thumbnail.jpg");
        var filter = $"scale=1280:720:force_original_aspect_ratio=increase,crop=1280:720,drawbox=x=0:y=ih*0.45:w=iw:h=ih*0.55:color=black@0.65:t=fill,drawtext=fontfile='{EscapeFilter(font)}':textfile=thumbnail-title.txt:fontsize=56:fontcolor=white:line_spacing=12:x=60:y=h-text_h-60";
        await processes.Run(Ffmpeg, ["-y", "-v", "error", "-filter_threads", "2", "-i", background, "-vf", filter, "-frames:v", "1", "-q:v", "2", output], ct, cwd: dir);
        return output;
    }
    public static void ValidateTimeline(Project p)
    {
        if (p.Scenes.Count == 0 || p.Scenes.Any(s => s.AudioAssetId is null || s.MediaAssetId is null || s.AudioSeconds <= 0)) throw new InvalidOperationException("Mỗi cảnh cần giọng đọc và media.");
        if (!double.IsFinite(p.ActualSeconds) || p.ActualSeconds > p.MaxSeconds) throw new InvalidOperationException("Thời lượng thực vượt giới hạn. Rút gọn lời đọc/cảnh; Studio không cắt mất nội dung.");
        CaptionFiles.Validate(p.Captions, p.ActualSeconds);
    }
    public static string Number(double n) => n.ToString("0.###", CultureInfo.InvariantCulture);
    private static string EscapeFilter(string path) => path.Replace("\\", "/").Replace(":", "\\:").Replace("'", "\\'");
}

public static class CaptionFiles
{
    public static void Validate(IReadOnlyList<WordCue> words, double duration)
    {
        if (words.Count == 0) throw new InvalidOperationException("Chưa có word timestamps.");
        double end = 0;
        foreach (var word in words)
        {
            if (string.IsNullOrWhiteSpace(word.Text) || !double.IsFinite(word.Start) || !double.IsFinite(word.End) || word.Start < 0 || word.End <= word.Start || word.Start + 0.03 < end || word.End > duration + 0.1)
                throw new InvalidOperationException("Mốc phụ đề không hợp lệ hoặc chồng lấn. Kiểm tra từng từ.");
            end = word.End;
        }
    }
    public static string Srt(IReadOnlyList<WordCue> words) => string.Join("\n\n", words.Select((w, i) => $"{i + 1}\n{Stamp(w.Start, ',')} --> {Stamp(w.End, ',')}\n{w.Text}")) + "\n";
    public static string Vtt(IReadOnlyList<WordCue> words) => "WEBVTT\n\n" + string.Join("\n\n", words.Select(w => $"{Stamp(w.Start, '.')} --> {Stamp(w.End, '.')}\n{w.Text}")) + "\n";
    public static string Stamp(double seconds, char separator)
    {
        var t = TimeSpan.FromSeconds(seconds); return $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}{separator}{t.Milliseconds:D3}";
    }
    public static string Ass(Project p)
    {
        var size = p.Format == VideoFormat.Short ? 64 : 58;
        var result = new StringBuilder($"[Script Info]\nScriptType: v4.00+\nPlayResX: {p.Width}\nPlayResY: {p.Height}\nWrapStyle: 0\n\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\nStyle: Default,DejaVu Sans,{size},&H00FFFFFF,&H00FFFFFF,&H00101010,&H70000000,-1,0,0,0,100,100,0,0,1,3,1,2,70,70,{(p.Format == VideoFormat.Short ? 330 : 100)},1\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");
        foreach (var group in p.Captions.Chunk(p.Format == VideoFormat.Short ? 4 : 7))
        {
            for (var i = 0; i < group.Length; i++)
            {
                var word = group[i];
                var text = p.CaptionStyle == "word" ? Safe(word.Text) : string.Join(' ', group.Select((w, j) => j == i ? "{\\c&H80FF5C&}" + Safe(w.Text) + "{\\c&HFFFFFF&}" : Safe(w.Text)));
                result.AppendLine($"Dialogue: 0,{AssTime(word.Start)},{AssTime(word.End)},Default,,0,0,0,,{text}");
            }
        }
        return result.ToString();
    }
    public static string Safe(string text) => text.Replace("\\", "＼").Replace("{", "(").Replace("}", ")").Replace('\n', ' ').Replace('\r', ' ');
    private static string AssTime(double value) { var t = TimeSpan.FromSeconds(value); return $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds / 10:D2}"; }
}
