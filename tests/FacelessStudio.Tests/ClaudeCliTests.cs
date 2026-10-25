using System.ComponentModel;
using System.Text.Json.Nodes;
using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Settings;
using FacelessStudio.Providers.Llm;
using FacelessStudio.Providers.Llm.ClaudeCli;
using Microsoft.Extensions.DependencyInjection;

namespace FacelessStudio.Tests;

public class ClaudeCliTests
{
    private sealed class FakeExec(int code, string output, string error = "")
    {
        public string? Exe;
        public List<string> Args = [];
        public string? WorkDir;
        public string? Stdin;

        public Task<(int Code, string Out, string Err)> Run(string exe, IReadOnlyList<string> args, string? dir,
            CancellationToken ct, string? stdin)
        {
            (Exe, Args, WorkDir, Stdin) = (exe, args.ToList(), dir, stdin);
            return Task.FromResult((code, output, error));
        }
    }

    private static LlmRequest Request(bool webSearch = false) =>
        new("system prompt", "user prompt", LlmPrompts.TopicSchema, WebSearch: webSearch);

    [Fact]
    public async Task Sends_headless_args_with_schema_no_tools_and_prompt_on_stdin()
    {
        var exec = new FakeExec(0, """{"is_error":false,"result":"{}","structured_output":{"ideas":[]}}""");
        var ctx = FakeHttp.Context(new() { [SettingKeys.ClaudeCliModel] = "sonnet" });

        var text = await new ClaudeCliClient(exec.Run).GenerateJsonAsync(Request(), ctx);

        Assert.Equal("""{"ideas":[]}""", text);
        Assert.Equal("claude", exec.Exe);
        Assert.Equal(ctx.WorkDir, exec.WorkDir);
        Assert.Equal("user prompt", exec.Stdin);
        Assert.Contains("-p", exec.Args);
        Assert.Equal("json", exec.Args[exec.Args.IndexOf("--output-format") + 1]);
        var schema = JsonNode.Parse(exec.Args[exec.Args.IndexOf("--json-schema") + 1])!;
        Assert.NotNull(schema["properties"]!["ideas"]);
        Assert.Equal("", exec.Args[exec.Args.IndexOf("--tools") + 1]);
        Assert.Equal("system prompt", exec.Args[exec.Args.IndexOf("--system-prompt") + 1]);
        Assert.Contains("--no-session-persistence", exec.Args);
        Assert.Equal("sonnet", exec.Args[exec.Args.IndexOf("--model") + 1]);
    }

    [Fact]
    public async Task Omits_model_when_not_set()
    {
        var exec = new FakeExec(0, """{"is_error":false,"result":"{}","structured_output":{"a":1}}""");
        await new ClaudeCliClient(exec.Run).GenerateJsonAsync(Request(), FakeHttp.Context(new()));
        Assert.DoesNotContain("--model", exec.Args);
    }

    [Fact]
    public async Task Falls_back_to_result_text_when_no_structured_output()
    {
        var exec = new FakeExec(0, """{"is_error":false,"result":"{\"ideas\":[{\"title\":\"A\",\"angle\":\"B\"}]}"}""");
        var text = await new ClaudeCliClient(exec.Run).GenerateJsonAsync(Request(), FakeHttp.Context(new()));
        Assert.Equal("""{"ideas":[{"title":"A","angle":"B"}]}""", text);
    }

    [Fact]
    public async Task Web_search_enables_tool_and_puts_schema_in_prompt()
    {
        var exec = new FakeExec(0, """{"is_error":false,"result":"{\"ideas\":[]}"}""");
        await new ClaudeCliClient(exec.Run).GenerateJsonAsync(Request(webSearch: true), FakeHttp.Context(new()));
        Assert.Equal("WebSearch", exec.Args[exec.Args.IndexOf("--tools") + 1]);
        Assert.DoesNotContain("--json-schema", exec.Args);
        Assert.Contains("JSON schema", exec.Stdin);
    }

    [Fact]
    public async Task Is_error_becomes_provider_exception_with_result_text()
    {
        var exec = new FakeExec(1, """{"is_error":true,"result":"There's an issue with the selected model (x)."}""");
        var ex = await Assert.ThrowsAsync<ProviderException>(
            () => new ClaudeCliClient(exec.Run).GenerateJsonAsync(Request(), FakeHttp.Context(new())));
        Assert.Contains("selected model", ex.Message);
    }

    [Fact]
    public async Task Nonzero_exit_without_json_reports_stderr_trimmed()
    {
        var exec = new FakeExec(2, "", new string('x', 1000));
        var ex = await Assert.ThrowsAsync<ProviderException>(
            () => new ClaudeCliClient(exec.Run).GenerateJsonAsync(Request(), FakeHttp.Context(new())));
        Assert.True(ex.Message.Length < 500);
    }

    [Fact]
    public async Task Login_problem_gets_friendly_message()
    {
        var exec = new FakeExec(1, """{"is_error":true,"result":"Not logged in · Please run /login"}""");
        var ex = await Assert.ThrowsAsync<ProviderException>(
            () => new ClaudeCliClient(exec.Run).GenerateJsonAsync(Request(), FakeHttp.Context(new())));
        Assert.Contains("claude login", ex.Message);
        Assert.Contains("chưa đăng nhập", ex.Message);
    }

    [Fact]
    public async Task Missing_executable_gives_install_hint()
    {
        var client = new ClaudeCliClient((_, _, _, _, _) => throw new Win32Exception(2));
        var ex = await Assert.ThrowsAsync<ProviderException>(
            () => client.GenerateJsonAsync(Request(), FakeHttp.Context(new())));
        Assert.Contains("Cài Claude Code", ex.Message);
    }

    [Fact]
    public void Vendor_is_registered_without_web_search()
    {
        using var sp = new FakeHttp().BuildServices();
        var script = FakeHttp.Provider<IScriptProvider>(sp, "script-claude-cli");
        var topic = FakeHttp.Provider<ITopicProvider>(sp, "topic-claude-cli");
        Assert.Equal(ProviderCategoryOf(script), ProviderCategoryOf(topic));
        Assert.True(script.IsConfigured(AppSettings.Empty()));
        Assert.Contains("Google Trends", topic.Name);
    }

    [Fact]
    public void Real_client_is_configured_only_when_executable_exists()
    {
        var dir = Directory.CreateTempSubdirectory("faceless-cli").FullName;
        var exe = Path.Combine(dir, "claude-fake");
        File.WriteAllText(exe, "");
        var client = new ClaudeCliClient();

        Assert.False(client.IsConfigured(new AppSettings(new Dictionary<string, string>
            { [SettingKeys.ClaudeCliPath] = Path.Combine(dir, "missing") }, dir)));
        Assert.True(client.IsConfigured(new AppSettings(new Dictionary<string, string>
            { [SettingKeys.ClaudeCliPath] = exe }, dir)));
        Assert.Null(ClaudeCliClient.FindExecutable("definitely-not-a-real-cli-name-42"));
    }

    private static object ProviderCategoryOf(IStepProvider p) => p.Category;
}
