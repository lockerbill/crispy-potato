using System.Diagnostics;
using System.Text;

namespace Studio.Providers;

public record ClaudeProcessResult(int ExitCode, string Output);
public sealed class ClaudeProcessRunner(IConfiguration configuration)
{
    public async Task<ClaudeProcessResult> Run(string executable, IReadOnlyList<string> arguments, string? input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var directory = Path.Combine(Path.GetTempPath(), "studio-claude-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(input is null ? 30 : 600));
        var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // The subscription bridge must never inherit an API credential or a different inference route.
        foreach (var key in start.Environment.Keys.Where(x => x.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase) || x.StartsWith("CLAUDE_CODE_USE_", StringComparison.OrdinalIgnoreCase) || x is "CLAUDE_CODE_OAUTH_TOKEN" or "CLAUDE_CODE_API_KEY_HELPER" or "CLAUDE_CODE_SIMPLE").ToArray()) start.Environment.Remove(key);
        if (configuration["Studio:ClaudeConfigDirectory"] is { Length: > 0 } configDirectory) start.Environment["CLAUDE_CONFIG_DIR"] = Path.GetFullPath(configDirectory);
        using var process = new Process { StartInfo = start };
        try
        {
            ct.ThrowIfCancellationRequested();
            try { if (!process.Start()) throw new InvalidOperationException(); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { throw new ProviderException("Không chạy được Claude Code. Kiểm tra đường dẫn executable và cài bản chính thức."); }
            using var cancel = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
            var output = Read(process.StandardOutput, 1024 * 1024, timeout.Token);
            var error = Read(process.StandardError, 64 * 1024, timeout.Token);
            _ = output.ContinueWith(_ => timeout.Cancel(), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            _ = error.ContinueWith(_ => timeout.Cancel(), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token));
            return new(process.ExitCode, await output); // Never return raw stderr or authentication diagnostics.
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ProviderException("Claude Code hết thời gian chờ.", input is not null); }
        catch (IOException) { throw new ProviderException("Không đọc được kết quả Claude Code.", input is not null); }
        finally
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    private static async Task<string> Read(StreamReader reader, int limit, CancellationToken ct)
    {
        var result = new StringBuilder(); var buffer = new char[4096];
        while (true) { var count = await reader.ReadAsync(buffer.AsMemory(), ct); if (count == 0) break; if (result.Length + count > limit) throw new ProviderException("Claude Code trả dữ liệu vượt giới hạn.", true); result.Append(buffer, 0, count); }
        return result.ToString();
    }
}
