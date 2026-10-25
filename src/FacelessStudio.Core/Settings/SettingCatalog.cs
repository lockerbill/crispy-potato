namespace FacelessStudio.Core.Settings;

/// <summary>Mô tả một mục cấu hình để trang Settings tự hiển thị.</summary>
public sealed record SettingDefinition(
    string Key,
    string Label,
    string Group,
    bool Secret = false,
    string Default = "",
    string Help = "");

/// <summary>Tất cả khóa cấu hình. Tên khóa cũng là tên biến môi trường tương ứng.</summary>
public static class SettingKeys
{
    public const string GeminiApiKey = "GEMINI_API_KEY";
    public const string GeminiModel = "GEMINI_MODEL";
    public const string GroqApiKey = "GROQ_API_KEY";
    public const string GroqModel = "GROQ_MODEL";
    public const string GroqWhisperModel = "GROQ_WHISPER_MODEL";
    public const string OllamaUrl = "OLLAMA_URL";
    public const string OllamaModel = "OLLAMA_MODEL";
    public const string AnthropicApiKey = "ANTHROPIC_API_KEY";
    public const string ClaudeModel = "CLAUDE_MODEL";
    public const string GoogleTrendsGeo = "GOOGLE_TRENDS_GEO";

    public const string OpenRouterApiKey = "OPENROUTER_API_KEY";
    public const string OpenRouterModel = "OPENROUTER_MODEL";
    public const string ZaiApiKey = "ZAI_API_KEY";
    public const string ZaiBaseUrl = "ZAI_BASE_URL";
    public const string ZaiModel = "ZAI_MODEL";
    public const string MoonshotApiKey = "MOONSHOT_API_KEY";
    public const string MoonshotBaseUrl = "MOONSHOT_BASE_URL";
    public const string MoonshotModel = "MOONSHOT_MODEL";
    public const string MiniMaxApiKey = "MINIMAX_API_KEY";
    public const string MiniMaxBaseUrl = "MINIMAX_BASE_URL";
    public const string MiniMaxModel = "MINIMAX_MODEL";
    public const string DeepSeekApiKey = "DEEPSEEK_API_KEY";
    public const string DeepSeekModel = "DEEPSEEK_MODEL";
    public const string XaiApiKey = "XAI_API_KEY";
    public const string XaiModel = "XAI_MODEL";
    public const string XaiOAuthModel = "XAI_OAUTH_MODEL";
    public const string CodexModel = "CODEX_MODEL";
    public const string CustomLlmBaseUrl = "CUSTOM_LLM_BASE_URL";
    public const string CustomLlmApiKey = "CUSTOM_LLM_API_KEY";
    public const string CustomLlmModel = "CUSTOM_LLM_MODEL";
    public const string CustomLlmJsonMode = "CUSTOM_LLM_JSON_MODE";
    public const string ClaudeCliPath = "CLAUDE_CLI_PATH";
    public const string ClaudeCliModel = "CLAUDE_CLI_MODEL";

    public const string PiperPath = "PIPER_PATH";
    public const string PiperVoiceVi = "PIPER_VOICE_VI";
    public const string PiperVoiceEn = "PIPER_VOICE_EN";
    public const string ElevenLabsApiKey = "ELEVENLABS_API_KEY";
    public const string ElevenLabsModel = "ELEVENLABS_MODEL";
    public const string ElevenLabsVoiceFemale = "ELEVENLABS_VOICE_FEMALE";
    public const string ElevenLabsVoiceMale = "ELEVENLABS_VOICE_MALE";

    public const string PexelsApiKey = "PEXELS_API_KEY";
    public const string PixabayApiKey = "PIXABAY_API_KEY";
    public const string ComfyUiUrl = "COMFYUI_URL";
    public const string ComfyUiCheckpoint = "COMFYUI_CHECKPOINT";
    public const string ComfyUiWorkflow = "COMFYUI_WORKFLOW";
    public const string OpenAiApiKey = "OPENAI_API_KEY";
    public const string OpenAiImageModel = "OPENAI_IMAGE_MODEL";
    public const string OpenAiTranscribeModel = "OPENAI_TRANSCRIBE_MODEL";
    public const string CloudflareAccountId = "CLOUDFLARE_ACCOUNT_ID";
    public const string CloudflareApiToken = "CLOUDFLARE_API_TOKEN";
    public const string CloudflareImageModel = "CLOUDFLARE_IMAGE_MODEL";

    public const string WhisperModel = "WHISPER_MODEL";
    public const string ShotstackSandboxKey = "SHOTSTACK_SANDBOX_KEY";
    public const string ShotstackProductionKey = "SHOTSTACK_PRODUCTION_KEY";

    public const string FfmpegPath = "FFMPEG_PATH";
    public const string FfprobePath = "FFPROBE_PATH";
    public const string SubtitleFont = "SUBTITLE_FONT";
    public const string ThumbnailFontFile = "THUMBNAIL_FONT_FILE";
    public const string MusicDir = "MUSIC_DIR";
}

public static class SettingCatalog
{
    public static readonly IReadOnlyList<SettingDefinition> All =
    [
        new(SettingKeys.GeminiApiKey, "Gemini API key", "Google Gemini", true,
            Help: "Lấy miễn phí tại https://aistudio.google.com/apikey"),
        new(SettingKeys.GeminiModel, "Gemini model", "Google Gemini", Default: "gemini-2.5-flash"),
        new(SettingKeys.GroqApiKey, "Groq API key", "Groq", true, Help: "Lấy miễn phí tại https://console.groq.com/keys"),
        new(SettingKeys.GroqModel, "Groq model (LLM)", "Groq", Default: "llama-3.3-70b-versatile"),
        new(SettingKeys.GroqWhisperModel, "Groq model (Whisper)", "Groq", Default: "whisper-large-v3-turbo"),
        new(SettingKeys.OllamaUrl, "Ollama URL", "Ollama (self-hosted)", Default: "http://localhost:11434"),
        new(SettingKeys.OllamaModel, "Ollama model", "Ollama (self-hosted)", Default: "qwen2.5:7b",
            Help: "Cài model bằng: ollama pull qwen2.5:7b"),
        new(SettingKeys.AnthropicApiKey, "Anthropic API key", "Claude (Anthropic)", true,
            Help: "Tạo tại https://platform.claude.com/settings/keys"),
        new(SettingKeys.ClaudeModel, "Claude model", "Claude (Anthropic)", Default: "claude-opus-5-5"),
        new(SettingKeys.ClaudeCliPath, "Đường dẫn Claude Code CLI", "Claude Code CLI (gói Pro/Max)", Default: "claude",
            Help: "Cài Claude Code (https://claude.com/claude-code) rồi chạy: claude login. Dùng hạn mức gói Claude của anh."),
        new(SettingKeys.ClaudeCliModel, "Model (tuỳ chọn)", "Claude Code CLI (gói Pro/Max)",
            Help: "Để trống dùng model mặc định của CLI. Ví dụ: opus, sonnet hoặc tên đầy đủ claude-..."),
        new(SettingKeys.CodexModel, "Model", "ChatGPT / Codex (đăng nhập)", Default: "gpt-5.5",
            Help: "Đăng nhập ở mục Tài khoản đăng nhập phía trên. Dùng hạn mức gói ChatGPT Plus/Pro."),
        new(SettingKeys.OpenRouterApiKey, "OpenRouter API key", "OpenRouter", true,
            Help: "Tạo tại https://openrouter.ai/keys"),
        new(SettingKeys.OpenRouterModel, "Model", "OpenRouter", Default: "openrouter/auto",
            Help: "Tên model trên OpenRouter, vd. anthropic/claude-sonnet-4.6, google/gemini-2.5-pro. openrouter/auto tự chọn."),
        new(SettingKeys.ZaiApiKey, "z.ai API key", "z.ai / GLM", true, Help: "Tạo tại https://z.ai/manage-apikey/apikey-list"),
        new(SettingKeys.ZaiBaseUrl, "Base URL", "z.ai / GLM", Default: "https://api.z.ai/api/paas/v4",
            Help: "Gói GLM Coding Plan: https://api.z.ai/api/coding/paas/v4 · Trung Quốc: https://open.bigmodel.cn/api/paas/v4"),
        new(SettingKeys.ZaiModel, "Model", "z.ai / GLM", Default: "glm-5"),
        new(SettingKeys.MoonshotApiKey, "Moonshot API key", "Kimi / Moonshot", true,
            Help: "Tạo tại https://platform.moonshot.ai/console/api-keys"),
        new(SettingKeys.MoonshotBaseUrl, "Base URL", "Kimi / Moonshot", Default: "https://api.moonshot.ai/v1",
            Help: "Tài khoản Trung Quốc: https://api.moonshot.cn/v1"),
        new(SettingKeys.MoonshotModel, "Model", "Kimi / Moonshot", Default: "kimi-k3",
            Help: "kimi-k3, kimi-k3.1-pro hoặc kimi-k2.6 (rẻ hơn)"),
        new(SettingKeys.MiniMaxApiKey, "MiniMax API key", "MiniMax", true,
            Help: "Tạo tại https://platform.minimax.io/user-center/basic-information/interface-key"),
        new(SettingKeys.MiniMaxBaseUrl, "Base URL", "MiniMax", Default: "https://api.minimax.io/v1",
            Help: "Tài khoản Trung Quốc: https://api.minimaxi.com/v1"),
        new(SettingKeys.MiniMaxModel, "Model", "MiniMax", Default: "MiniMax-M3"),
        new(SettingKeys.DeepSeekApiKey, "DeepSeek API key", "DeepSeek", true, Help: "Tạo tại https://platform.deepseek.com/api_keys"),
        new(SettingKeys.DeepSeekModel, "Model", "DeepSeek", Default: "deepseek-flash",
            Help: "deepseek-flash (nhanh, rẻ) hoặc deepseek-v4-pro (mạnh hơn)"),
        new(SettingKeys.XaiApiKey, "xAI API key", "xAI (Grok)", true, Help: "Tạo tại https://console.x.ai"),
        new(SettingKeys.XaiModel, "Model (API key)", "xAI (Grok)", Default: "grok-4.3"),
        new(SettingKeys.XaiOAuthModel, "Model (đăng nhập SuperGrok)", "xAI (Grok)", Default: "grok-4.3",
            Help: "Đăng nhập SuperGrok / X Premium+ ở mục Tài khoản đăng nhập phía trên, không cần API key."),
        new(SettingKeys.CustomLlmBaseUrl, "Base URL (OpenAI-compatible)", "Custom endpoint",
            Help: "Endpoint kiểu OpenAI /v1, vd. http://localhost:1234/v1 (LM Studio), http://localhost:8000/v1 (vLLM), LiteLLM..."),
        new(SettingKeys.CustomLlmApiKey, "API key (tuỳ chọn)", "Custom endpoint", true),
        new(SettingKeys.CustomLlmModel, "Model", "Custom endpoint"),
        new(SettingKeys.CustomLlmJsonMode, "Chế độ JSON", "Custom endpoint", Default: "json_object",
            Help: "json_object, json_schema hoặc none (nếu server không hỗ trợ response_format)"),
        new(SettingKeys.GoogleTrendsGeo, "Google Trends: mã quốc gia", "Chủ đề", Default: "VN",
            Help: "VN, US, ... dùng để lấy xu hướng tìm kiếm"),

        new(SettingKeys.PiperPath, "Đường dẫn piper", "Piper TTS (self-hosted)", Default: "piper",
            Help: "File chạy piper (piper.exe trên Windows)"),
        new(SettingKeys.PiperVoiceVi, "Giọng Piper tiếng Việt (.onnx)", "Piper TTS (self-hosted)",
            Default: "data/piper/vi_VN-vais1000-medium.onnx"),
        new(SettingKeys.PiperVoiceEn, "Giọng Piper tiếng Anh (.onnx)", "Piper TTS (self-hosted)",
            Default: "data/piper/en_US-lessac-medium.onnx"),
        new(SettingKeys.ElevenLabsApiKey, "ElevenLabs API key", "ElevenLabs", true),
        new(SettingKeys.ElevenLabsModel, "ElevenLabs model", "ElevenLabs", Default: "eleven_turbo_v2_5",
            Help: "eleven_turbo_v2_5 / eleven_flash_v2_5 hỗ trợ tiếng Việt; eleven_multilingual_v2 thì không"),
        new(SettingKeys.ElevenLabsVoiceFemale, "Voice ID giọng nữ", "ElevenLabs", Default: "21m00Tcm4TlvDq8ikWAM"),
        new(SettingKeys.ElevenLabsVoiceMale, "Voice ID giọng nam", "ElevenLabs", Default: "pNInz6obpgDQGcFmaJgB"),

        new(SettingKeys.PexelsApiKey, "Pexels API key", "Stock footage", true, Help: "Miễn phí tại https://www.pexels.com/api/"),
        new(SettingKeys.PixabayApiKey, "Pixabay API key", "Stock footage", true, Help: "Miễn phí tại https://pixabay.com/api/docs/"),
        new(SettingKeys.ComfyUiUrl, "ComfyUI URL", "ComfyUI (self-hosted)", Default: "http://127.0.0.1:8188"),
        new(SettingKeys.ComfyUiCheckpoint, "Checkpoint model", "ComfyUI (self-hosted)", Default: "sd_xl_base_1.0.safetensors"),
        new(SettingKeys.ComfyUiWorkflow, "Workflow JSON (API format, tuỳ chọn)", "ComfyUI (self-hosted)",
            Help: "Để trống dùng workflow SDXL mặc định. Workflow riêng dùng {{PROMPT}}, {{WIDTH}}, {{HEIGHT}}, {{SEED}}"),
        new(SettingKeys.OpenAiApiKey, "OpenAI API key", "OpenAI", true),
        new(SettingKeys.OpenAiImageModel, "OpenAI image model", "OpenAI", Default: "gpt-image-1"),
        new(SettingKeys.OpenAiTranscribeModel, "OpenAI transcribe model", "OpenAI", Default: "whisper-1"),
        new(SettingKeys.CloudflareAccountId, "Cloudflare account ID", "Cloudflare Workers AI"),
        new(SettingKeys.CloudflareApiToken, "Cloudflare API token", "Cloudflare Workers AI", true),
        new(SettingKeys.CloudflareImageModel, "Cloudflare image model", "Cloudflare Workers AI",
            Default: "@cf/black-forest-labs/flux-1-schnell"),

        new(SettingKeys.WhisperModel, "Whisper.net model", "Whisper (self-hosted)", Default: "base",
            Help: "tiny, base, small, medium (càng lớn càng chính xác, càng chậm). Tự tải lần đầu."),
        new(SettingKeys.ShotstackSandboxKey, "Shotstack Sandbox key", "Shotstack", true,
            Help: "Miễn phí, video có watermark. https://dashboard.shotstack.io"),
        new(SettingKeys.ShotstackProductionKey, "Shotstack Production key", "Shotstack", true),

        new(SettingKeys.FfmpegPath, "Đường dẫn ffmpeg", "Hệ thống", Default: "ffmpeg"),
        new(SettingKeys.FfprobePath, "Đường dẫn ffprobe", "Hệ thống", Default: "ffprobe"),
        new(SettingKeys.SubtitleFont, "Font phụ đề", "Hệ thống",
            Help: "Tên font cài trên máy, hỗ trợ tiếng Việt. Để trống: Arial (Windows) / DejaVu Sans (Linux)"),
        new(SettingKeys.ThumbnailFontFile, "File font thumbnail (.ttf)", "Hệ thống", Help: "Để trống để tự tìm font đậm"),
        new(SettingKeys.MusicDir, "Thư mục nhạc nền", "Hệ thống", Default: "assets/music",
            Help: "Đặt file mp3 không bản quyền vào đây để trộn nhạc nền"),
    ];

    public static SettingDefinition? Find(string key) => All.FirstOrDefault(d => d.Key == key);
}
