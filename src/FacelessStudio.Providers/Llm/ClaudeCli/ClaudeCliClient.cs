using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Media;
using FacelessStudio.Core.Settings;

namespace FacelessStudio.Providers.Llm.ClaudeCli;

/// <summary>
/// Chạy Claude Code CLI chính chủ (đã `claude login` bằng gói Pro/Max) ở chế độ không giao diện.
/// Không đọc file đăng nhập của Claude, mọi xác thực do CLI tự lo.
/// </summary>
public sealed class ClaudeCliClient(
    Func<string, IReadOnlyList<string>, string?, CancellationToken, string?, Task<(int Code, string Out, string Err)>>? exec = null)
    : ILlmClient
{
    private readonly Func<string, IReadOnlyList<string>, string?, CancellationToken, string?, Task<(int Code, string Out, string Err)>> _exec =
        exec ?? ((exe, args, dir, ct, stdin) => FfmpegTool.ExecAsync(exe, args, dir, ct, stdin));

    public string Service => "Claude Code CLI";
    public IReadOnlyList<string> RequiredSettings => [SettingKeys.ClaudeCliPath];

    public async Task<string> GenerateJsonAsync(LlmRequest request, StepContext context)
    {
        var s = context.Settings;
        var args = new List<string> { "-p", "--output-format", "json" };
        string prompt;
        if (request.WebSearch)
        {
            // Web search không đi cùng --json-schema được nên mô tả schema trong prompt.
            args.AddRange(["--tools", "WebSearch"]);
            prompt = LlmPrompts.WithSchemaInstruction(request.User, request.Schema);
        }
        else
        {
            args.AddRange(["--json-schema", request.Schema.ToJsonString(), "--tools", ""]);
            prompt = request.User;
        }
        if (!string.IsNullOrWhiteSpace(request.System)) args.AddRange(["--system-prompt", request.System]);
        args.Add("--no-session-persistence");
        if (s.Has(SettingKeys.ClaudeCliModel)) args.AddRange(["--model", s.Get(SettingKeys.ClaudeCliModel)]);

        (int Code, string Out, string Err) result;
        try
        {
            result = await _exec(s.ResolvePath(SettingKeys.ClaudeCliPath), args, context.WorkDir, context.CancellationToken, prompt);
        }
        catch (Win32Exception e)
        {
            throw new ProviderException(
                "Không chạy được Claude Code CLI. Cài Claude Code rồi chạy `claude login` (hoặc khai báo đường dẫn trong Cài đặt).", e);
        }

        JsonObject? json = null;
        try
        {
            json = JsonNode.Parse(result.Out) as JsonObject;
        }
        catch (JsonException)
        {
            // Lỗi sớm có thể in chữ thường thay vì JSON.
        }

        var text = json?["result"] is JsonValue r && r.TryGetValue<string>(out var rs) ? rs : null;
        if (json?["is_error"]?.GetValue<bool>() == true || result.Code != 0)
            throw new ProviderException(ErrorMessage(text, result));

        if (json?["structured_output"] is JsonObject or JsonArray)
            return json["structured_output"]!.ToJsonString();
        if (!string.IsNullOrWhiteSpace(text)) return text;
        throw new ProviderException("Claude Code CLI không trả về nội dung.");
    }

    private static string ErrorMessage(string? resultText, (int Code, string Out, string Err) result)
    {
        var detail = !string.IsNullOrWhiteSpace(resultText) ? resultText
            : !string.IsNullOrWhiteSpace(result.Err) ? result.Err
            : result.Out;
        detail = detail.Trim();
        if (detail.Length > 400) detail = detail[..400];
        if (NeedsLogin(resultText ?? (string.IsNullOrWhiteSpace(result.Err) ? result.Out : result.Err)))
            return "Claude Code CLI chưa đăng nhập. Chạy `claude login`.";
        return $"Claude Code CLI lỗi (mã {result.Code}): {detail}";
    }

    private static bool NeedsLogin(string text) =>
        text.Contains("login", StringComparison.OrdinalIgnoreCase)
        || text.Contains("authenticate", StringComparison.OrdinalIgnoreCase);
}
