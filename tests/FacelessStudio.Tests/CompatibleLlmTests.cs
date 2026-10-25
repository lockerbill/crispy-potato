using System.Net;
using System.Text.Json.Nodes;
using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Settings;

namespace FacelessStudio.Tests;

public class CompatibleLlmTests
{
    private const string ScriptJson = """
        {"title":"Bí ẩn đại dương","description":"Mô tả","tags":["biển","đại dương"],"thumbnail_text":"BIỂN SÂU",
         "scenes":[{"narration":"Câu một.","footage_query":"ocean waves","image_prompt":"deep blue ocean"},
                   {"narration":"Câu hai.","footage_query":"coral reef","image_prompt":"coral reef"}]}
        """;

    private static string Escaped(string json) => JsonValue.Create(json)!.ToJsonString();

    private static string Reply(string content, string finish = "stop") =>
        $$$"""{"choices":[{"index":0,"message":{"role":"assistant","content":{{{Escaped(content)}}}},"finish_reason":"{{{finish}}}"}]}""";

    public static TheoryData<string, string, string, string, string, int> Vendors => new()
    {
        // id, key setting, url fragment (full), default model, response_format type, expected max_tokens
        { "openrouter", SettingKeys.OpenRouterApiKey, "https://openrouter.ai/api/v1/chat/completions", "openrouter/auto", "json_object", 16000 },
        { "zai", SettingKeys.ZaiApiKey, "https://api.z.ai/api/paas/v4/chat/completions", "glm-5", "json_object", 16000 },
        { "kimi", SettingKeys.MoonshotApiKey, "https://api.moonshot.ai/v1/chat/completions", "kimi-k2.5", "json_object", 16000 },
        { "minimax", SettingKeys.MiniMaxApiKey, "https://api.minimax.io/v1/chat/completions", "MiniMax-M2.7", "", 16000 },
        { "deepseek", SettingKeys.DeepSeekApiKey, "https://api.deepseek.com/chat/completions", "deepseek-chat", "json_object", 8192 },
    };

    [Theory]
    [MemberData(nameof(Vendors))]
    public async Task Vendor_posts_chat_completions_with_bearer_key(string id, string keySetting, string url, string model,
        string format, int maxTokens)
    {
        var http = new FakeHttp().On(url, Reply(ScriptJson));
        using var sp = http.BuildServices();
        var script = await FakeHttp.Provider<IScriptProvider>(sp, $"script-{id}")
            .WriteAsync(FakeHttp.Context(new() { [keySetting] = "sk-test" }));

        Assert.Equal("BIỂN SÂU", script.ThumbnailText);
        Assert.Equal(2, script.Scenes.Count);
        var req = Assert.Single(http.Requests);
        Assert.Equal(url, req.Url);
        Assert.Equal("Bearer sk-test", req.Headers["authorization"]);
        var body = JsonNode.Parse(req.Body)!;
        Assert.Equal(model, body["model"]!.GetValue<string>());
        Assert.Equal(maxTokens, body["max_tokens"]!.GetValue<int>());
        Assert.Null(body["temperature"]);
        if (format.Length == 0) Assert.Null(body["response_format"]);
        else Assert.Equal(format, body["response_format"]!["type"]!.GetValue<string>());
        Assert.Equal("system", body["messages"]![0]!["role"]!.GetValue<string>());
        var user = body["messages"]![1]!["content"]!.GetValue<string>();
        Assert.Contains("JSON", user);
        Assert.Contains("Vietnamese", user);
    }

    [Fact]
    public async Task OpenRouter_sends_app_headers()
    {
        var http = new FakeHttp().On("openrouter.ai", Reply(ScriptJson));
        using var sp = http.BuildServices();
        await FakeHttp.Provider<IScriptProvider>(sp, "script-openrouter")
            .WriteAsync(FakeHttp.Context(new() { [SettingKeys.OpenRouterApiKey] = "or", [SettingKeys.OpenRouterModel] = "anthropic/claude-sonnet-4.6" }));
        var req = Assert.Single(http.Requests);
        Assert.Equal("https://github.com/lockerbill/crispy-potato", req.Headers["http-referer"]);
        Assert.Equal("Faceless Studio", req.Headers["x-title"]);
        Assert.Equal("anthropic/claude-sonnet-4.6", JsonNode.Parse(req.Body)!["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task Base_url_setting_overrides_default_and_trailing_slash_is_trimmed()
    {
        var http = new FakeHttp().On("api.moonshot.cn", Reply(ScriptJson));
        using var sp = http.BuildServices();
        await FakeHttp.Provider<IScriptProvider>(sp, "script-kimi").WriteAsync(FakeHttp.Context(new()
        {
            [SettingKeys.MoonshotApiKey] = "k",
            [SettingKeys.MoonshotBaseUrl] = "https://api.moonshot.cn/v1/",
        }));
        Assert.Equal("https://api.moonshot.cn/v1/chat/completions", Assert.Single(http.Requests).Url);
    }

    [Fact]
    public async Task Think_blocks_are_stripped_from_content()
    {
        var content = "<think>\nPhải trả về JSON {sai} thôi.\n</think>\n\n" + ScriptJson;
        var http = new FakeHttp().On("api.minimax.io", Reply(content));
        using var sp = http.BuildServices();
        var script = await FakeHttp.Provider<IScriptProvider>(sp, "script-minimax")
            .WriteAsync(FakeHttp.Context(new() { [SettingKeys.MiniMaxApiKey] = "mm" }));
        Assert.Equal("Bí ẩn đại dương", script.Title);
    }

    [Fact]
    public async Task Finish_reason_length_is_reported()
    {
        var http = new FakeHttp().On("api.deepseek.com", Reply("""{"title":"cắt dở""", "length"));
        using var sp = http.BuildServices();
        var ex = await Assert.ThrowsAsync<ProviderException>(() => FakeHttp.Provider<IScriptProvider>(sp, "script-deepseek")
            .WriteAsync(FakeHttp.Context(new() { [SettingKeys.DeepSeekApiKey] = "k" })));
        Assert.Contains("quá dài", ex.Message);
    }

    [Fact]
    public async Task Empty_content_and_api_error_bodies_become_provider_errors()
    {
        var http = new FakeHttp().On("api.z.ai", Reply("<think>chỉ suy nghĩ</think>"));
        using var sp = http.BuildServices();
        var provider = FakeHttp.Provider<IScriptProvider>(sp, "script-zai");
        var ctx = FakeHttp.Context(new() { [SettingKeys.ZaiApiKey] = "k" });
        var empty = await Assert.ThrowsAsync<ProviderException>(() => provider.WriteAsync(ctx));
        Assert.Contains("không trả về nội dung", empty.Message);

        var http2 = new FakeHttp().On("openrouter.ai", """{"error":{"message":"No endpoints found for model x","code":404}}""");
        using var sp2 = http2.BuildServices();
        var err = await Assert.ThrowsAsync<ProviderException>(() => FakeHttp.Provider<IScriptProvider>(sp2, "script-openrouter")
            .WriteAsync(FakeHttp.Context(new() { [SettingKeys.OpenRouterApiKey] = "k" })));
        Assert.Contains("No endpoints found for model x", err.Message);
    }

    [Fact]
    public async Task Custom_endpoint_without_key_sends_no_authorization_and_defaults_to_json_object()
    {
        var http = new FakeHttp().On("localhost:1234", Reply(ScriptJson));
        using var sp = http.BuildServices();
        var script = await FakeHttp.Provider<IScriptProvider>(sp, "script-openai-compat").WriteAsync(FakeHttp.Context(new()
        {
            [SettingKeys.CustomLlmBaseUrl] = "http://localhost:1234/v1",
            [SettingKeys.CustomLlmModel] = "qwen3-8b",
        }));
        Assert.Equal(2, script.Scenes.Count);
        var req = Assert.Single(http.Requests);
        Assert.Equal("http://localhost:1234/v1/chat/completions", req.Url);
        Assert.False(req.Headers.ContainsKey("authorization"));
        var body = JsonNode.Parse(req.Body)!;
        Assert.Equal("qwen3-8b", body["model"]!.GetValue<string>());
        Assert.Equal("json_object", body["response_format"]!["type"]!.GetValue<string>());
        Assert.Null(body["max_tokens"]);
    }

    [Fact]
    public async Task Custom_endpoint_sends_key_when_present()
    {
        var http = new FakeHttp().On("llm.example.com", Reply(ScriptJson));
        using var sp = http.BuildServices();
        await FakeHttp.Provider<IScriptProvider>(sp, "script-openai-compat").WriteAsync(FakeHttp.Context(new()
        {
            [SettingKeys.CustomLlmBaseUrl] = "https://llm.example.com/v1",
            [SettingKeys.CustomLlmModel] = "m",
            [SettingKeys.CustomLlmApiKey] = "litellm-key",
        }));
        Assert.Equal("Bearer litellm-key", Assert.Single(http.Requests).Headers["authorization"]);
    }

    [Fact]
    public async Task Custom_endpoint_json_schema_mode_sends_schema()
    {
        var http = new FakeHttp().On("localhost:8000", Reply(ScriptJson));
        using var sp = http.BuildServices();
        await FakeHttp.Provider<IScriptProvider>(sp, "script-openai-compat").WriteAsync(FakeHttp.Context(new()
        {
            [SettingKeys.CustomLlmBaseUrl] = "http://localhost:8000/v1",
            [SettingKeys.CustomLlmModel] = "m",
            [SettingKeys.CustomLlmJsonMode] = "json_schema",
        }));
        var format = JsonNode.Parse(Assert.Single(http.Requests).Body)!["response_format"]!;
        Assert.Equal("json_schema", format["type"]!.GetValue<string>());
        Assert.Equal("output", format["json_schema"]!["name"]!.GetValue<string>());
        Assert.False(format["json_schema"]!["strict"]!.GetValue<bool>());
        Assert.NotNull(format["json_schema"]!["schema"]!["properties"]!["scenes"]);
    }

    [Fact]
    public async Task Custom_endpoint_none_mode_omits_response_format()
    {
        var http = new FakeHttp().On("localhost:8000", Reply(ScriptJson));
        using var sp = http.BuildServices();
        await FakeHttp.Provider<IScriptProvider>(sp, "script-openai-compat").WriteAsync(FakeHttp.Context(new()
        {
            [SettingKeys.CustomLlmBaseUrl] = "http://localhost:8000/v1",
            [SettingKeys.CustomLlmModel] = "m",
            [SettingKeys.CustomLlmJsonMode] = "none",
        }));
        Assert.Null(JsonNode.Parse(Assert.Single(http.Requests).Body)!["response_format"]);
    }

    [Fact]
    public async Task Custom_endpoint_unknown_json_mode_falls_back_to_json_object()
    {
        var http = new FakeHttp().On("localhost:8000", Reply(ScriptJson));
        using var sp = http.BuildServices();
        await FakeHttp.Provider<IScriptProvider>(sp, "script-openai-compat").WriteAsync(FakeHttp.Context(new()
        {
            [SettingKeys.CustomLlmBaseUrl] = "http://localhost:8000/v1",
            [SettingKeys.CustomLlmModel] = "m",
            [SettingKeys.CustomLlmJsonMode] = "banana",
        }));
        Assert.Equal("json_object",
            JsonNode.Parse(Assert.Single(http.Requests).Body)!["response_format"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void IsConfigured_follows_required_settings()
    {
        using var sp = new FakeHttp().BuildServices();
        var custom = FakeHttp.Provider<IScriptProvider>(sp, "script-openai-compat");
        Assert.False(custom.IsConfigured(AppSettings.Empty()));
        Assert.False(custom.IsConfigured(new AppSettings(new Dictionary<string, string> { [SettingKeys.CustomLlmModel] = "m" }, ".")));
        Assert.True(custom.IsConfigured(new AppSettings(
            new Dictionary<string, string> { [SettingKeys.CustomLlmBaseUrl] = "http://localhost:1234/v1", [SettingKeys.CustomLlmModel] = "m" }, ".")));

        var deepseek = FakeHttp.Provider<IScriptProvider>(sp, "script-deepseek");
        Assert.False(deepseek.IsConfigured(AppSettings.Empty()));
        Assert.True(deepseek.IsConfigured(new AppSettings(new Dictionary<string, string> { [SettingKeys.DeepSeekApiKey] = "k" }, ".")));
    }

    [Fact]
    public async Task Http_401_mentions_api_key()
    {
        var http = new FakeHttp().On("api.deepseek.com", """{"error":{"message":"Authentication Fails"}}""", HttpStatusCode.Unauthorized);
        using var sp = http.BuildServices();
        var ex = await Assert.ThrowsAsync<ProviderException>(() => FakeHttp.Provider<IScriptProvider>(sp, "script-deepseek")
            .WriteAsync(FakeHttp.Context(new() { [SettingKeys.DeepSeekApiKey] = "bad" })));
        Assert.Contains("API key", ex.Message);
    }

    [Fact]
    public async Task Topic_provider_uses_google_trends_and_compatible_llm()
    {
        var rss = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss xmlns:ht="https://trends.google.com/trending/rss" version="2.0"><channel>
              <item><title>bão số 5</title><ht:news_item><ht:news_item_title>Bão đổ bộ miền Trung</ht:news_item_title></ht:news_item></item>
            </channel></rss>
            """;
        var topics = """{"ideas":[{"title":"Vì sao bão hình thành?","angle":"Giải thích khoa học"}]}""";
        var http = new FakeHttp()
            .On("trends.google.com", rss, contentType: "application/rss+xml")
            .On("api.deepseek.com", Reply(topics));
        using var sp = http.BuildServices();
        var ideas = await FakeHttp.Provider<ITopicProvider>(sp, "topic-deepseek")
            .SuggestAsync(new TopicRequest("khoa học"), FakeHttp.Context(new() { [SettingKeys.DeepSeekApiKey] = "k" }));

        Assert.Equal("Vì sao bão hình thành?", Assert.Single(ideas).Title);
        var prompt = JsonNode.Parse(http.Requests[1].Body)!["messages"]![1]!["content"]!.GetValue<string>();
        Assert.Contains("bão số 5 (Bão đổ bộ miền Trung)", prompt);
    }
}
