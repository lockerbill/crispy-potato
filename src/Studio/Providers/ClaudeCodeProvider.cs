using System.Text.Json.Nodes;
using Studio.Domain;

namespace Studio.Providers;

public sealed class ClaudeCodeProvider(ClaudeProcessRunner runner) : IStageProvider
{
    public bool Handles(string id) => id == "claude-code";
    public async Task Validate(ProviderConfig config, Stage stage, CancellationToken ct)
    {
        if (stage is not (Stage.Topic or Stage.Script or Stage.Title or Stage.Description or Stage.Tags)) throw new ProviderException("Claude Code bridge chỉ hỗ trợ văn bản.");
        await Verify(config, ct);
    }
    private async Task Verify(ProviderConfig config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.Executable)) throw new ProviderException("Cần cấu hình executable Claude Code / Configure the Claude Code executable.");
        var version = await runner.Run(config.Executable, ["--version"], null, ct);
        if (version.ExitCode != 0 || !System.Text.RegularExpressions.Regex.IsMatch(version.Output, @"\b\d+\.\d+\.\d+\b"))
            throw new ProviderException("Không xác minh được phiên bản Claude Code / Cannot verify Claude Code version.");
        var help = await runner.Run(config.Executable, ["--help"], null, ct);
        if (help.ExitCode != 0 || new[] { "--safe-mode", "--tools", "--disallowedTools", "--no-session-persistence", "--output-format", "--input-format", "--max-turns" }.Any(x => !help.Output.Contains(x, StringComparison.Ordinal)))
            throw new ProviderException("Claude Code thiếu cờ cần thiết; cập nhật binary chính thức / Update the official Claude Code binary for the required flags.");
        var auth = await runner.Run(config.Executable, ["auth", "status"], null, ct);
        try
        {
            var info = JsonNode.Parse(auth.Output);
            if (auth.ExitCode != 0 || info?["authMethod"]?.GetValue<string>() != "claude.ai")
                throw new ProviderException("Chạy claude auth login với cùng user/config directory của server, chọn subscription / Run claude auth login as the server user with the same config directory and select subscription.");
        }
        catch (System.Text.Json.JsonException) { throw new ProviderException("Không đọc được trạng thái Claude Code. Cập nhật CLI và chạy claude auth login."); }
        catch (InvalidOperationException) { throw new ProviderException("Không đọc được trạng thái Claude Code. Cập nhật CLI và chạy claude auth login."); }
    }
    public async Task<string> CheckConnection(ProviderConfig config, string? secret, CancellationToken ct)
    { await Verify(config, ct); return "Claude Code đã đăng nhập subscription / Subscription connected. Extra usage do tài khoản Claude quản lý / Extra usage is managed by your Claude account."; }
    public async Task<ProviderResult> Execute(ProviderContext context, CancellationToken ct)
    {
        await Validate(context.Config, context.Stage, ct);
        if (string.IsNullOrWhiteSpace(context.Model)) throw new ProviderException("Cần chọn model Claude Code.");
        var result = await runner.Run(context.Config.Executable,
            ["--safe-mode", "-p", "--input-format", "text", "--output-format", "json", "--tools", "", "--disallowedTools", "*", "--no-session-persistence", "--max-turns", "1", "--model", context.Model],
            TextPrompts.Build(context.Project, context.Stage), ct);
        try
        {
            var data = JsonNode.Parse(result.Output);
            if (result.ExitCode != 0 || data?["is_error"]?.GetValue<bool>() == true || data?["type"]?.GetValue<string>() != "result" || data?["subtype"]?.GetValue<string>() != "success")
                throw new ProviderException("Claude Code chưa hoàn tất yêu cầu. Kiểm tra đăng nhập, quota và usage trước khi chạy lại.", true);
            var raw = ProviderHttp.Required(data, "result");
            var project = Json.Clone(context.Project);
            TextProvider.Apply(project, context.Stage, raw); return new(raw, project);
        }
        catch (System.Text.Json.JsonException) { throw new ProviderException("Claude Code trả JSON không hợp lệ.", true); }
        catch (InvalidOperationException) { throw new ProviderException("Claude Code trả JSON không hợp lệ.", true); }
    }
}
