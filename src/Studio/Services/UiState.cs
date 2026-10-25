using Studio.Domain;

namespace Studio.Services;

public sealed class UiState
{
    public bool English { get; set; }
    public string T(string vi, string en) => English ? en : vi;
    public string StageName(Stage s) => s switch
    {
        Stage.Topic => T("Chủ đề", "Topic"), Stage.Script => T("Kịch bản", "Script"), Stage.Title => T("Tiêu đề", "Title"),
        Stage.Description => T("Mô tả", "Description"), Stage.Tags => "Tags", Stage.Voice => T("Giọng đọc", "Voiceover"),
        Stage.Media => T("Hình ảnh & footage", "Images & footage"), Stage.Captions => T("Phụ đề", "Captions"), Stage.Thumbnail => "Thumbnail", Stage.Render => T("Dựng video", "Render"), _ => s.ToString()
    };
    public string Status(StepStatus s) => s switch
    {
        StepStatus.Pending => T("Chưa chạy", "Pending"), StepStatus.Queued => T("Trong hàng đợi", "Queued"), StepStatus.Running => T("Đang chạy", "Running"),
        StepStatus.AwaitingReview => T("Chờ duyệt", "Review"), StepStatus.Approved => T("Đã duyệt", "Approved"), StepStatus.Stale => T("Cần cập nhật", "Outdated"),
        StepStatus.NeedsAttention => T("Cần kiểm tra", "Needs attention"), StepStatus.Failed => T("Thất bại", "Failed"), StepStatus.Cancelled => T("Đã hủy", "Cancelled"), _ => s.ToString()
    };
    public string Tier(ProviderTier t) => t switch { ProviderTier.FreeCloud => T("Cloud miễn phí", "Free cloud"), ProviderTier.SelfHosted => "Self-hosted", ProviderTier.Paid => T("Trả phí", "Paid cloud"), ProviderTier.Subscription => T("Gói thuê bao", "Subscription"), _ => "Sandbox" };
    public string ProviderNotice(ProviderDefinition p) => p.Id switch
    {
        "openrouter" => T("API key OpenRouter; model và giá phụ thuộc nhà cung cấp được định tuyến.", "OpenRouter API key; models and pricing depend on the routed provider."),
        "chatgpt-subscription" => T("Đăng nhập với ChatGPT. Model phụ thuộc gói thuê bao; extra usage do tài khoản của anh kiểm soát.", "Sign in with ChatGPT. Models depend on your subscription; extra usage is controlled by your account."),
        "anthropic" => T("Anthropic API key có hóa đơn riêng, không dùng quota của gói Claude.", "Anthropic API billing is separate from Claude subscription quotas."),
        "claude-code" => T("Dùng Claude Code nguyên bản đã đăng nhập gói thuê bao. Extra usage có thể phát sinh.", "Use unmodified Claude Code signed in with your subscription. Extra usage may apply."),
        "zai" => T("Standard API key quốc tế của z.ai. Coding Plan là sản phẩm riêng, không tự chuyển endpoint.", "Global z.ai Standard API key. Coding Plan is a separate product; endpoints never switch automatically."),
        "moonshot" => T("API key quốc tế Moonshot; tài khoản API tách biệt với gói Kimi.", "Global Moonshot API key; API billing is separate from Kimi memberships."),
        "minimax" => T("MiniMax API key. Nhập model có sẵn cho tài khoản của anh.", "MiniMax API key. Enter a model available to your account."),
        "deepseek" => T("API key DeepSeek; nhập bảng giá đúng model trước khi chạy.", "DeepSeek API key; confirm the selected model's prices before running."),
        "xai" => T("API key inference của xAI, tính phí API riêng với SuperGrok.", "xAI inference API key, billed separately from SuperGrok."),
        "supergrok" => T("OAuth thử nghiệm tương thích Grok CLI qua triển khai cộng đồng. Một số tài khoản đăng nhập được nhưng inference trả 403.", "Experimental community-compatible Grok CLI OAuth. Some accounts can sign in but receive inference 403."),
        "custom-text" => T("Base URL tương thích OpenAI Chat Completions; API key không bắt buộc. Endpoint local có thể dùng giá 0 đã xác nhận.", "OpenAI Chat Completions-compatible base URL; optional API key. A local endpoint can use confirmed zero prices."),
        "gemini" => T(p.Notice, "Free-tier quotas and account conditions apply. Never automatically switches to billing."),
        "ollama" => T(p.Notice, "Local software; you manage compute and model licensing."),
        "openai-text" => T(p.Notice, "Enter verified input and output token prices before running."),
        "azure-speech" => T(p.Notice, "Use an F0 resource only. Azure S0 incurs charges; verify the SKU in Azure Portal."),
        "piper" => T(p.Notice, "Requires Piper CLI and Vietnamese/English voice models. Check each voice's license."),
        "elevenlabs" => T(p.Notice, "Select a voice ID and model supporting Vietnamese; enter your plan's prices."),
        "pexels" => T(p.Notice, "Stock photos and footage. Preserve author credits and check the Pexels License."),
        "cloudflare" => T(p.Notice, "Free allocation is limited. Verify account billing; paid plans are not guaranteed free."),
        "library" => T(p.Notice, "Use uploaded images and clips that you have permission to use."),
        "comfyui" => T(p.Notice, "Connect ComfyUI with the selected checkpoint. A GPU is recommended; models are not downloaded automatically."),
        "openai-image" => T(p.Notice, "The per-image price must cover the chosen size and quality; v1 uses low quality."),
        "groq" => T(p.Notice, "The free plan has audio and request limits. No automatic plan upgrade."),
        "whisper" => T(p.Notice, "Requires whisper-cli and a multilingual model. Do not use an .en model for Vietnamese."),
        "openai-whisper" => T(p.Notice, "Transcription with word timestamps; enter the price per audio minute."),
        "ffmpeg" => T(p.Notice, "1080p H.264/AAC rendering with libass subtitles and no watermark."),
        "shotstack-sandbox" => T(p.Notice, "Test environment: watermarks, limits and retention depend on the service."),
        "shotstack" => T(p.Notice, "Cloud rendering through ingest uploads; no public access to your machine is required."),
        _ => p.Notice
    };
}
