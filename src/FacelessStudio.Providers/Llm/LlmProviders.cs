using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Models;
using FacelessStudio.Core.Settings;

namespace FacelessStudio.Providers.Llm;

/// <summary>Viết kịch bản bằng một LLM bất kỳ.</summary>
public abstract class LlmScriptProvider(ILlmClient llm) : ProviderBase, IScriptProvider
{
    public override PipelineStep Step => PipelineStep.Script;
    public override IReadOnlyList<string> RequiredSettings => llm.RequiredSettings;
    public override bool IsConfigured(AppSettings settings) => llm.IsConfigured(settings);

    /// <summary>Mức effort cho Claude; LLM khác bỏ qua.</summary>
    protected virtual string Effort => "medium";

    public async Task<VideoScript> WriteAsync(StepContext context)
    {
        if (string.IsNullOrWhiteSpace(context.Request.Topic)) throw new ProviderException("Chưa có chủ đề để viết kịch bản.");
        var text = await llm.GenerateJsonAsync(
            new LlmRequest(LlmPrompts.ScriptSystem, LlmPrompts.ScriptUser(context.Request), LlmPrompts.ScriptSchema, Effort),
            context);
        return LlmPrompts.ParseScript(text);
    }
}

/// <summary>Gợi ý chủ đề bằng LLM, có thể kèm dữ liệu Google Trends.</summary>
public abstract class LlmTopicProvider(ILlmClient llm, GoogleTrendsClient? trends) : ProviderBase, ITopicProvider
{
    public override PipelineStep Step => PipelineStep.Topic;
    public override IReadOnlyList<string> RequiredSettings => llm.RequiredSettings;
    public override bool IsConfigured(AppSettings settings) => llm.IsConfigured(settings);
    protected virtual bool WebSearch => false;

    public async Task<IReadOnlyList<TopicIdea>> SuggestAsync(TopicRequest request, StepContext context)
    {
        IReadOnlyList<string> trending = trends != null ? await trends.GetTrendingAsync(context) : [];
        var user = LlmPrompts.TopicUser(context.Request, request, trending);
        if (WebSearch) user += "\nSearch the web first for what is trending now in this niche, then suggest topics.";
        var text = await llm.GenerateJsonAsync(
            new LlmRequest(LlmPrompts.TopicSystem, user, LlmPrompts.TopicSchema, "low", WebSearch), context);
        return LlmPrompts.ParseTopics(text);
    }
}

public sealed class GeminiScriptProvider(GeminiClient llm) : LlmScriptProvider(llm)
{
    public override string Id => "script-gemini";
    public override string Name => "Google Gemini";
    public override ProviderCategory Category => ProviderCategory.FreeCloud;
    public override string Description => "Gemini qua Google AI Studio, gói miễn phí. Viết tiếng Việt tốt.";
    public override string CostNote => "Miễn phí (giới hạn số lần gọi mỗi ngày)";
}

public sealed class GroqScriptProvider(GroqClient llm) : LlmScriptProvider(llm)
{
    public override string Id => "script-groq";
    public override string Name => "Groq (Llama)";
    public override ProviderCategory Category => ProviderCategory.FreeCloud;
    public override string Description => "Rất nhanh, gói miễn phí. Tiếng Anh tốt, tiếng Việt khá.";
    public override string CostNote => "Miễn phí (giới hạn số lần gọi mỗi phút/ngày)";
}

public sealed class OllamaScriptProvider(OllamaClient llm) : LlmScriptProvider(llm)
{
    public override string Id => "script-ollama";
    public override string Name => "Ollama (trên máy)";
    public override ProviderCategory Category => ProviderCategory.FreeSelfHosted;
    public override string Description => "Chạy model mở (Qwen, Llama...) trên máy anh. Không tốn phí, chất lượng tuỳ model và cấu hình máy.";
}

public sealed class ClaudeScriptProvider(ClaudeClient llm) : LlmScriptProvider(llm)
{
    public override string Id => "script-claude";
    public override string Name => "Claude (Anthropic)";
    public override ProviderCategory Category => ProviderCategory.PaidCloud;
    public override string Description => "Kịch bản tự nhiên, bám sát yêu cầu nhất. Model mặc định claude-opus-5-5.";
    public override string CostNote => "~$0.05–0.30 / video";
    protected override string Effort => "medium";

    public override double EstimateCostUsd(VideoRequest request)
    {
        // Giá Claude Opus 5.5: $4 / 1 triệu token vào, $20 / 1 triệu token ra (ước lượng 2.5 token/chữ + thinking).
        var output = request.TargetWords * 2.5 + 600 + 3000;
        return Math.Round((2500 * 4.0 + output * 20.0) / 1_000_000, 3);
    }
}

public sealed class GeminiTopicProvider(GeminiClient llm, GoogleTrendsClient trends) : LlmTopicProvider(llm, trends)
{
    public override string Id => "topic-gemini";
    public override string Name => "Google Trends + Gemini";
    public override ProviderCategory Category => ProviderCategory.FreeCloud;
    public override string Description => "Lấy xu hướng tìm kiếm từ Google Trends rồi để Gemini chọn chủ đề hợp kênh.";
}

public sealed class GroqTopicProvider(GroqClient llm, GoogleTrendsClient trends) : LlmTopicProvider(llm, trends)
{
    public override string Id => "topic-groq";
    public override string Name => "Google Trends + Groq";
    public override ProviderCategory Category => ProviderCategory.FreeCloud;
    public override string Description => "Lấy xu hướng tìm kiếm từ Google Trends rồi để Groq chọn chủ đề hợp kênh.";
}

public sealed class OllamaTopicProvider(OllamaClient llm, GoogleTrendsClient trends) : LlmTopicProvider(llm, trends)
{
    public override string Id => "topic-ollama";
    public override string Name => "Google Trends + Ollama";
    public override ProviderCategory Category => ProviderCategory.FreeSelfHosted;
    public override string Description => "Xu hướng từ Google Trends, model Ollama trên máy chọn chủ đề.";
}

public sealed class ClaudeTopicProvider(ClaudeClient llm) : LlmTopicProvider(llm, null)
{
    public override string Id => "topic-claude";
    public override string Name => "Claude + Web search";
    public override ProviderCategory Category => ProviderCategory.PaidCloud;
    public override string Description => "Claude tự tìm kiếm web để nắm xu hướng mới nhất rồi đề xuất chủ đề.";
    public override string CostNote => "~$0.05–0.15 / lần gợi ý (gồm phí web search)";
    protected override bool WebSearch => true;
}
