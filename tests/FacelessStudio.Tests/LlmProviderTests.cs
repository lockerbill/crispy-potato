using System.Net;
using System.Text.Json.Nodes;
using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Settings;
using FacelessStudio.Providers.Llm;

namespace FacelessStudio.Tests;

public class LlmProviderTests
{
    private const string ScriptJson = """
        {"title":"Bí ẩn đại dương","description":"Mô tả","tags":["biển","đại dương"],"thumbnail_text":"BIỂN SÂU",
         "scenes":[{"narration":"Câu một.","footage_query":"ocean waves","image_prompt":"deep blue ocean"},
                   {"narration":"Câu hai.","footage_query":"coral reef","image_prompt":"coral reef"}]}
        """;

    private static string Escaped(string json) => JsonValue.Create(json)!.ToJsonString();

    [Fact]
    public async Task Gemini_sends_json_schema_and_parses_script()
    {
        var http = new FakeHttp().On("generativelanguage.googleapis.com",
            $$$"""{"candidates":[{"content":{"parts":[{"text":{{{Escaped(ScriptJson)}}}}]},"finishReason":"STOP"}]}""");
        using var sp = http.BuildServices();
        var provider = FakeHttp.Provider<IScriptProvider>(sp, "script-gemini");
        var script = await provider.WriteAsync(FakeHttp.Context(new() { [SettingKeys.GeminiApiKey] = "g-key" }));

        Assert.Equal(2, script.Scenes.Count);
        Assert.Equal("ocean waves", script.Scenes[0].FootageQuery);
        var req = Assert.Single(http.Requests);
        Assert.Contains("models/gemini-2.5-flash:generateContent", req.Url);
        Assert.Equal("g-key", req.Headers["x-goog-api-key"]);
        var body = JsonNode.Parse(req.Body)!;
        Assert.Equal("application/json", body["generationConfig"]!["responseMimeType"]!.GetValue<string>());
        Assert.NotNull(body["generationConfig"]!["responseJsonSchema"]!["properties"]!["scenes"]);
        Assert.Contains("Vietnamese", body["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Groq_uses_json_mode_and_bearer_key()
    {
        var http = new FakeHttp().On("api.groq.com",
            $$$"""{"choices":[{"message":{"content":{{{Escaped(ScriptJson)}}}}}]}""");
        using var sp = http.BuildServices();
        var script = await FakeHttp.Provider<IScriptProvider>(sp, "script-groq")
            .WriteAsync(FakeHttp.Context(new() { [SettingKeys.GroqApiKey] = "gq" }));
        Assert.Equal("BIỂN SÂU", script.ThumbnailText);
        var req = Assert.Single(http.Requests);
        Assert.Equal("Bearer gq", req.Headers["authorization"]);
        Assert.Equal("json_object", JsonNode.Parse(req.Body)!["response_format"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Ollama_posts_schema_as_format()
    {
        var http = new FakeHttp().On("localhost:11434/api/chat",
            $$$"""{"message":{"role":"assistant","content":{{{Escaped(ScriptJson)}}}}}""");
        using var sp = http.BuildServices();
        var script = await FakeHttp.Provider<IScriptProvider>(sp, "script-ollama").WriteAsync(FakeHttp.Context(new()));
        Assert.Equal(2, script.Scenes.Count);
        var body = JsonNode.Parse(Assert.Single(http.Requests).Body)!;
        Assert.Equal("qwen2.5:7b", body["model"]!.GetValue<string>());
        Assert.Equal("object", body["format"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Claude_sends_structured_output_effort_and_default_fallbacks()
    {
        var response = $$$"""
            {"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5",
             "content":[{"type":"text","text":{{{Escaped(ScriptJson)}}}}],
             "stop_reason":"end_turn","stop_sequence":null,
             "usage":{"input_tokens":10,"output_tokens":20}}
            """;
        var http = new FakeHttp().On("/v1/messages", response);
        using var sp = http.BuildServices();
        var script = await FakeHttp.Provider<IScriptProvider>(sp, "script-claude")
            .WriteAsync(FakeHttp.Context(new() { [SettingKeys.AnthropicApiKey] = "sk-ant-test" }));
        Assert.Equal("Bí ẩn đại dương", script.Title);

        var req = Assert.Single(http.Requests);
        // SDK C# gửi API key qua header Authorization: Bearer.
        Assert.Equal("Bearer sk-ant-test", req.Headers["authorization"]);
        Assert.Contains("server-side-fallback-2026-07-01", req.Headers["anthropic-beta"]);
        var body = JsonNode.Parse(req.Body)!;
        Assert.Equal("claude-opus-5-5", body["model"]!.GetValue<string>());
        Assert.Equal("default", body["fallbacks"]!.GetValue<string>());
        Assert.Equal("medium", body["output_config"]!["effort"]!.GetValue<string>());
        Assert.Equal("json_schema", body["output_config"]!["format"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Claude_refusal_becomes_friendly_error()
    {
        var http = new FakeHttp().On("/v1/messages", """
            {"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],
             "stop_reason":"refusal","stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":0}}
            """);
        using var sp = http.BuildServices();
        var ex = await Assert.ThrowsAsync<ProviderException>(() => FakeHttp.Provider<IScriptProvider>(sp, "script-claude")
            .WriteAsync(FakeHttp.Context(new() { [SettingKeys.AnthropicApiKey] = "k" })));
        Assert.Contains("từ chối", ex.Message);
    }

    [Fact]
    public async Task Topic_provider_includes_google_trends()
    {
        var rss = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss xmlns:ht="https://trends.google.com/trending/rss" version="2.0"><channel>
              <item><title>bão số 5</title><ht:news_item><ht:news_item_title>Bão đổ bộ miền Trung</ht:news_item_title></ht:news_item></item>
              <item><title>giá vàng</title></item>
            </channel></rss>
            """;
        var topics = """{"ideas":[{"title":"Vì sao bão hình thành?","angle":"Giải thích khoa học"}]}""";
        var http = new FakeHttp()
            .On("trends.google.com", rss, contentType: "application/rss+xml")
            .On("generativelanguage.googleapis.com",
                $$$"""{"candidates":[{"content":{"parts":[{"text":{{{Escaped(topics)}}}}]}}]}""");
        using var sp = http.BuildServices();
        var ideas = await FakeHttp.Provider<ITopicProvider>(sp, "topic-gemini")
            .SuggestAsync(new TopicRequest("khoa học"), FakeHttp.Context(new() { [SettingKeys.GeminiApiKey] = "k" }));

        Assert.Equal("Vì sao bão hình thành?", Assert.Single(ideas).Title);
        Assert.Contains("geo=VN", http.Requests[0].Url);
        var prompt = JsonNode.Parse(http.Requests[1].Body)!["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("bão số 5 (Bão đổ bộ miền Trung)", prompt);
    }

    [Fact]
    public async Task Http_429_becomes_quota_message()
    {
        var http = new FakeHttp().On("api.groq.com", """{"error":"rate"}""", HttpStatusCode.TooManyRequests);
        using var sp = http.BuildServices();
        var ex = await Assert.ThrowsAsync<ProviderException>(() => FakeHttp.Provider<IScriptProvider>(sp, "script-groq")
            .WriteAsync(FakeHttp.Context(new() { [SettingKeys.GroqApiKey] = "k" })));
        Assert.Contains("quota", ex.Message);
    }

    [Fact]
    public void ParseScript_tolerates_markdown_fences()
    {
        var s = LlmPrompts.ParseScript("```json\n" + ScriptJson + "\n```");
        Assert.Equal(2, s.Scenes.Count);
    }
}
