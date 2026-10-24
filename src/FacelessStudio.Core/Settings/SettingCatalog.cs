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
        new(SettingKeys.GoogleTrendsGeo, "Google Trends: mã quốc gia", "Chủ đề", Default: "VN",
            Help: "VN, US, ... dùng để lấy xu hướng tìm kiếm"),

        new(SettingKeys.PiperPath, "Đường dẫn piper", "Piper TTS (self-hosted)", Default: "piper",
            Help: "File chạy piper (piper.exe trên Windows)"),
        new(SettingKeys.PiperVoiceVi, "Giọng Piper tiếng Việt (.onnx)", "Piper TTS (self-hosted)",
            Default: "data/piper/vi_VN-vais1000-medium.onnx"),
        new(SettingKeys.PiperVoiceEn, "Giọng Piper tiếng Anh (.onnx)", "Piper TTS (self-hosted)",
            Default: "data/piper/en_US-lessac-medium.onnx"),
        new(SettingKeys.ElevenLabsApiKey, "ElevenLabs API key", "ElevenLabs", true),
        new(SettingKeys.ElevenLabsModel, "ElevenLabs model", "ElevenLabs", Default: "eleven_multilingual_v2"),
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
