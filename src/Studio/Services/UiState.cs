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
    public string Tier(ProviderTier t) => t switch { ProviderTier.FreeCloud => T("Cloud miễn phí", "Free cloud"), ProviderTier.SelfHosted => "Self-hosted", ProviderTier.Paid => T("Trả phí", "Paid cloud"), _ => "Sandbox" };
}
