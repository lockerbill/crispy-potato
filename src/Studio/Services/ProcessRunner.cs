using System.Diagnostics;

namespace Studio.Services;

public sealed class ProcessRunner
{
    public async Task<string> Run(string executable, IEnumerable<string> args, CancellationToken ct, string? stdin = null, string? cwd = null)
    {
        var info = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin is not null, UseShellExecute = false, CreateNoWindow = true };
        if (cwd is not null) info.WorkingDirectory = cwd;
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        try { process.Start(); } catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException($"Không chạy được engine {Path.GetFileName(executable)}. Kiểm tra cài đặt và executable path."); }
        using var registration = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        if (stdin is not null) { await process.StandardInput.WriteAsync(stdin.AsMemory(), ct); process.StandardInput.Close(); }
        await process.WaitForExitAsync(ct);
        var text = await output; var stderr = await error;
        if (process.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(executable)} thất bại ({process.ExitCode}): {stderr[^Math.Min(stderr.Length, 1800)..]}");
        return text;
    }
}
