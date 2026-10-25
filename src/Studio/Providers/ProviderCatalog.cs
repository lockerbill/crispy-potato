using Studio.Domain;

namespace Studio.Providers;

public static class ProviderCatalog
{
    private static readonly Stage[] Text = [Stage.Topic, Stage.Script, Stage.Title, Stage.Description, Stage.Tags];
    public static readonly ProviderDefinition[] All =
    [
        new("gemini", "Google Gemini", ProviderTier.FreeCloud, Text, "gemini-2.5-flash", "million tokens", "Free tier có quota và điều kiện tài khoản; không tự chuyển sang billing."),
        new("ollama", "Ollama", ProviderTier.SelfHosted, Text, "qwen3:4b", "local", "Phần mềm local; compute và giấy phép model do anh quản lý.", false),
        new("openai-text", "OpenAI", ProviderTier.Paid, Text, "gpt-4.1-mini", "million tokens", "Nhập giá input/output token đã kiểm tra trước khi chạy.", Pricing: ProviderPricing.Tokens),
        new("openrouter", "OpenRouter", ProviderTier.Paid, Text, "openai/gpt-4.1-mini", "million tokens", "API key · OpenRouter.", Protocol: TextProtocol.ChatCompletions, Pricing: ProviderPricing.Tokens, BaseUrl: "https://openrouter.ai/api/v1", SupportsModelDiscovery: true),
        new("chatgpt-subscription", "ChatGPT / Codex Subscription", ProviderTier.Subscription, Text, "", "request", "Sign in with ChatGPT; models depend on your subscription. Extra usage is controlled by your account.", false, ProviderAuth.OAuth, TextProtocol.Responses, ProviderPricing.SubscriptionReserve, "https://api.openai.com/v1", true),
        new("anthropic", "Anthropic", ProviderTier.Paid, Text, "claude-sonnet-4-6", "million tokens", "Anthropic API billing is separate from Claude subscriptions.", Protocol: TextProtocol.AnthropicMessages, Pricing: ProviderPricing.Tokens, BaseUrl: "https://api.anthropic.com/v1", SupportsModelDiscovery: true),
        new("claude-code", "Claude Subscription · Claude Code", ProviderTier.Subscription, Text, "sonnet", "request", "Use an unmodified Claude Code installation signed in with your subscription. Extra usage may apply.", false, ProviderAuth.ClaudeCode, TextProtocol.ClaudeCode, ProviderPricing.SubscriptionReserve),
        new("zai", "z.ai / GLM", ProviderTier.Paid, Text, "glm-4.7", "million tokens", "Global Standard API key; Coding Plan keys use a different product.", Protocol: TextProtocol.ChatCompletions, Pricing: ProviderPricing.Tokens, BaseUrl: "https://api.z.ai/api/paas/v4"),
        new("moonshot", "Kimi / Moonshot", ProviderTier.Paid, Text, "kimi-k2.5", "million tokens", "Global Moonshot API key; separate from Kimi memberships.", Protocol: TextProtocol.ChatCompletions, Pricing: ProviderPricing.Tokens, BaseUrl: "https://api.moonshot.ai/v1", SupportsModelDiscovery: true),
        new("minimax", "MiniMax", ProviderTier.Paid, Text, "MiniMax-M2.5", "million tokens", "MiniMax API key. Enter a model available to your account.", Protocol: TextProtocol.ChatCompletions, Pricing: ProviderPricing.Tokens, BaseUrl: "https://api.minimax.io/v1"),
        new("deepseek", "DeepSeek", ProviderTier.Paid, Text, "deepseek-chat", "million tokens", "DeepSeek API key.", Protocol: TextProtocol.ChatCompletions, Pricing: ProviderPricing.Tokens, BaseUrl: "https://api.deepseek.com", SupportsModelDiscovery: true),
        new("xai", "xAI / Grok · API key", ProviderTier.Paid, Text, "grok-4", "million tokens", "xAI inference API key; separate API billing.", Protocol: TextProtocol.ChatCompletions, Pricing: ProviderPricing.Tokens, BaseUrl: "https://api.x.ai/v1", SupportsModelDiscovery: true),
        new("supergrok", "SuperGrok OAuth · Experimental", ProviderTier.Subscription, Text, "", "request", "Experimental community-compatible Grok CLI OAuth. Login does not guarantee inference access; some accounts receive 403.", false, ProviderAuth.OAuth, TextProtocol.Responses, ProviderPricing.SubscriptionReserve, "https://api.x.ai/v1", true, true),
        new("custom-text", "Custom Endpoint", ProviderTier.Paid, Text, "", "million tokens", "OpenAI Chat Completions API base URL; optional key. Set confirmed zero prices for a local endpoint.", false, ProviderAuth.OptionalApiKey, TextProtocol.ChatCompletions, ProviderPricing.Tokens, SupportsModelDiscovery: true),
        new("azure-speech", "Azure Speech · F0", ProviderTier.FreeCloud, [Stage.Voice], "", "characters", "Chỉ dùng resource F0. Azure S0 có tính phí; kiểm tra SKU trong Azure Portal."),
        new("piper", "Piper", ProviderTier.SelfHosted, [Stage.Voice], "", "local", "Cần Piper CLI và model giọng vi/en; kiểm tra giấy phép từng voice.", false),
        new("elevenlabs", "ElevenLabs", ProviderTier.Paid, [Stage.Voice], "eleven_v3", "1000 characters", "Chọn voice ID và model hỗ trợ tiếng Việt; nhập mức giá theo gói."),
        new("pexels", "Pexels · stock", ProviderTier.FreeCloud, [Stage.Media], "photos", "request", "Ảnh/footage stock. Lưu credit tác giả và kiểm tra Pexels License."),
        new("cloudflare", "Cloudflare Workers AI", ProviderTier.FreeCloud, [Stage.Media, Stage.Thumbnail], "@cf/black-forest-labs/flux-1-schnell", "image", "Free allocation có giới hạn. Dùng tài khoản free và kiểm tra billing; không bảo đảm miễn phí trên paid plan."),
        new("library", "Thư viện media", ProviderTier.SelfHosted, [Stage.Media], "", "local", "Dùng ảnh/clip anh tải lên và có quyền sử dụng.", false),
        new("comfyui", "ComfyUI", ProviderTier.SelfHosted, [Stage.Media, Stage.Thumbnail], "sd_xl_base_1.0.safetensors", "local", "Kết nối ComfyUI có checkpoint tương ứng. GPU khuyến nghị; không tự tải model.", false),
        new("openai-image", "OpenAI Images", ProviderTier.Paid, [Stage.Media, Stage.Thumbnail], "gpt-image-1", "image", "Giá mỗi ảnh phải bao phủ size/quality đã chọn; chọn quality low trong v1."),
        new("groq", "Groq Whisper", ProviderTier.FreeCloud, [Stage.Captions], "whisper-large-v3", "audio minute", "Free plan có giới hạn audio/request; không tự nâng gói."),
        new("whisper", "whisper.cpp", ProviderTier.SelfHosted, [Stage.Captions], "", "local", "Cần whisper-cli và model đa ngôn ngữ (không dùng bản .en cho tiếng Việt).", false),
        new("openai-whisper", "OpenAI Whisper", ProviderTier.Paid, [Stage.Captions], "whisper-1", "audio minute", "Transcription với word timestamps; giá nhập theo phút audio."),
        new("ffmpeg", "FFmpeg", ProviderTier.SelfHosted, [Stage.Render], "", "local", "Dựng 1080p H.264/AAC, phụ đề libass, không watermark.", false),
        new("shotstack-sandbox", "Shotstack · sandbox", ProviderTier.Sandbox, [Stage.Render], "", "video minute", "Môi trường thử nghiệm: watermark, giới hạn và thời gian lưu tùy dịch vụ."),
        new("shotstack", "Shotstack · production", ProviderTier.Paid, [Stage.Render], "", "video minute", "Render cloud; media được tải lên ingest, không công khai máy của anh.")
    ];
    public static ProviderDefinition Get(string id) => All.SingleOrDefault(p => p.Id == id) ?? throw new InvalidOperationException("Unknown provider.");
    public static IEnumerable<ProviderDefinition> For(Stage stage) => All.Where(p => p.Stages.Contains(stage));
    public static void ApplyPreset(Project p, string preset)
    {
        string Pick(Stage s) => (preset, s) switch
        {
            ("local", Stage.Voice) => "piper", ("local", Stage.Media or Stage.Thumbnail) => "comfyui",
            ("local", Stage.Captions) => "whisper", ("local", Stage.Render) => "ffmpeg", ("local", _) => "ollama",
            ("quality", Stage.Voice) => "elevenlabs", ("quality", Stage.Media or Stage.Thumbnail) => "openai-image",
            ("quality", Stage.Captions) => "openai-whisper", ("quality", Stage.Render) => "shotstack", ("quality", _) => "openai-text",
            (_, Stage.Voice) => "azure-speech", (_, Stage.Media) => "pexels", (_, Stage.Thumbnail) => "cloudflare",
            (_, Stage.Captions) => "groq", (_, Stage.Render) => "shotstack-sandbox", _ => "gemini"
        };
        foreach (var stage in Pipeline.Order)
        {
            var id = Pick(stage);
            p.Steps[stage] = new Step { Stage = stage, ProviderId = id, Model = Get(id).Model };
        }
    }
}
