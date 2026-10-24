using System.Diagnostics;
using System.Globalization;
using FacelessStudio.Core.Settings;

namespace FacelessStudio.Core.Media;

public sealed class FfmpegException(string message) : Exception(message);

/// <summary>Gọi ffmpeg/ffprobe bằng tiến trình con. Dùng chung cho renderer, Whisper, thumbnail.</summary>
public sealed class FfmpegTool(string ffmpegPath = "ffmpeg", string ffprobePath = "ffprobe")
{
    public static FfmpegTool FromSettings(AppSettings s) =>
        new(s.ResolvePath(SettingKeys.FfmpegPath), s.ResolvePath(SettingKeys.FfprobePath));

    public string FfmpegPath { get; } = ffmpegPath;
    public string FfprobePath { get; } = ffprobePath;

    public static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            var (code, _, _) = await ExecAsync(FfmpegPath, ["-version"], null, CancellationToken.None);
            return code == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task RunAsync(IEnumerable<string> args, string? workingDir = null, CancellationToken ct = default)
    {
        var all = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        all.AddRange(args);
        var (code, _, err) = await ExecAsync(FfmpegPath, all, workingDir, ct);
        if (code != 0)
        {
            var tail = err.Length > 1200 ? err[^1200..] : err;
            throw new FfmpegException($"FFmpeg lỗi (mã {code}): {tail.Trim()}");
        }
    }

    public async Task<double> ProbeDurationAsync(string path, CancellationToken ct = default)
    {
        var (code, output, err) = await ExecAsync(
            FfprobePath,
            ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path],
            null, ct);
        if (code != 0 || !double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            throw new FfmpegException($"Không đọc được thời lượng của {Path.GetFileName(path)}: {err.Trim()}");
        return d;
    }

    /// <summary>Chuyển âm thanh bất kỳ sang WAV, có thể thêm khoảng lặng cuối.</summary>
    public Task ToWavAsync(string input, string output, double padSeconds = 0, int sampleRate = 44100,
        int channels = 2, CancellationToken ct = default)
    {
        var args = new List<string> { "-i", input };
        if (padSeconds > 0) args.AddRange(["-af", $"apad=pad_dur={F(padSeconds)}"]);
        args.AddRange(["-ar", sampleRate.ToString(CultureInfo.InvariantCulture), "-ac", channels.ToString(CultureInfo.InvariantCulture), output]);
        return RunAsync(args, ct: ct);
    }

    /// <summary>Lấy một khung hình (dùng làm nền thumbnail).</summary>
    public Task ExtractFrameAsync(string video, double at, string output, CancellationToken ct = default) =>
        RunAsync(["-ss", F(at), "-i", video, "-frames:v", "1", "-q:v", "2", output], ct: ct);

    /// <summary>Ghép nhiều file cùng định dạng bằng concat demuxer (không encode lại).</summary>
    public async Task ConcatAsync(IReadOnlyList<string> files, string output, CancellationToken ct = default)
    {
        var list = Path.ChangeExtension(output, ".txt");
        await File.WriteAllLinesAsync(list,
            files.Select(f => $"file '{Path.GetFullPath(f).Replace('\\', '/').Replace("'", "'\\''")}'"), ct);
        await RunAsync(["-f", "concat", "-safe", "0", "-i", list, "-c", "copy", output], ct: ct);
    }

    /// <summary>Chạy một tiến trình bất kỳ, có thể gửi dữ liệu vào stdin (dùng cho piper).</summary>
    public static async Task<(int Code, string Out, string Err)> ExecAsync(
        string exe, IEnumerable<string> args, string? workingDir, CancellationToken ct, string? stdin = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            StandardInputEncoding = stdin != null ? new System.Text.UTF8Encoding(false) : null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (workingDir != null) psi.WorkingDirectory = workingDir;

        using var proc = Process.Start(psi) ?? throw new FfmpegException($"Không chạy được {exe}");
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        if (stdin != null)
        {
            await proc.StandardInput.WriteAsync(stdin);
            proc.StandardInput.Close();
        }
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        return (proc.ExitCode, await stdout, await stderr);
    }
}
