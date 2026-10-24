using FacelessStudio.Core.Media;
using FacelessStudio.Core.Settings;

namespace FacelessStudio.Web;

/// <summary>Các nút "Kiểm tra" trên trang Cài đặt cho phần mềm chạy trên máy.</summary>
public sealed class SystemChecks(IHttpClientFactory httpFactory)
{
    public sealed record CheckResult(bool Ok, string Message);

    public async Task<CheckResult> FfmpegAsync(AppSettings s)
    {
        var ff = FfmpegTool.FromSettings(s);
        if (!await ff.IsAvailableAsync())
            return new(false, $"Không chạy được \"{ff.FfmpegPath}\". Cài FFmpeg (winget install Gyan.FFmpeg) hoặc khai báo đường dẫn.");
        try
        {
            var (_, output, _) = await FfmpegTool.ExecAsync(ff.FfprobePath, ["-version"], null, CancellationToken.None);
            return new(true, "FFmpeg OK: " + output.Split('\n')[0]);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new(false, $"Có ffmpeg nhưng không chạy được ffprobe \"{ff.FfprobePath}\".");
        }
    }

    public Task<CheckResult> OllamaAsync(AppSettings s) =>
        HttpCheckAsync(s.Get(SettingKeys.OllamaUrl).TrimEnd('/') + "/api/tags", "Ollama",
            body => body.Contains($"\"{s.Get(SettingKeys.OllamaModel)}\"")
                ? "Ollama OK, đã có model " + s.Get(SettingKeys.OllamaModel)
                : $"Ollama chạy nhưng chưa có model. Chạy: ollama pull {s.Get(SettingKeys.OllamaModel)}");

    public Task<CheckResult> ComfyUiAsync(AppSettings s) =>
        HttpCheckAsync(s.Get(SettingKeys.ComfyUiUrl).TrimEnd('/') + "/system_stats", "ComfyUI", _ => "ComfyUI OK");

    public async Task<CheckResult> PiperAsync(AppSettings s)
    {
        var missing = new[] { SettingKeys.PiperVoiceVi, SettingKeys.PiperVoiceEn }
            .Select(s.ResolvePath).Where(p => !File.Exists(p) || !File.Exists(p + ".json")).ToList();
        try
        {
            var (code, _, err) = await FfmpegTool.ExecAsync(s.ResolvePath(SettingKeys.PiperPath), ["--help"], null, CancellationToken.None);
            if (code != 0 && !err.Contains("usage", StringComparison.OrdinalIgnoreCase))
                return new(false, "Piper lỗi: " + err.Trim());
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new(false, $"Không chạy được piper tại \"{s.ResolvePath(SettingKeys.PiperPath)}\".");
        }
        return missing.Count == 0
            ? new(true, "Piper OK, có đủ giọng tiếng Việt và tiếng Anh.")
            : new(missing.Count < 2, "Piper chạy được. Thiếu file giọng: " + string.Join(", ", missing));
    }

    private async Task<CheckResult> HttpCheckAsync(string url, string name, Func<string, string> onOk)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var body = await httpFactory.CreateClient("providers").GetStringAsync(url, cts.Token);
            var message = onOk(body);
            return new(!message.Contains("chưa"), message);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return new(false, $"Không kết nối được {name} tại {url}: {e.Message}");
        }
    }
}
