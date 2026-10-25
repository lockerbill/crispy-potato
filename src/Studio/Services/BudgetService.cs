using System.Text;
using Studio.Domain;
using Studio.Providers;

namespace Studio.Services;

public static class BudgetService
{
    public static int OutputTokenLimit(Project p, Stage s) => s == Stage.Script ? Math.Clamp(p.TargetSeconds * 18, 2048, 32768) : 2048;
    public static decimal Estimate(Project p, Stage stage, ProviderConfig cfg)
    {
        var provider = ProviderCatalog.Get(cfg.Id);
        if (provider.Pricing == ProviderPricing.SubscriptionReserve)
        {
            if (cfg.SubscriptionReserveUsd is not > 0)
                throw new InvalidOperationException("Nhập khoản dự phòng subscription USD mỗi lần gọi lớn hơn 0 / Enter a positive subscription reservation per request.");
            return Round(cfg.SubscriptionReserveUsd.Value);
        }
        if (provider.Tier != ProviderTier.Paid) return 0;
        if (cfg.UsdPerUnit is null || cfg.PriceAsOf is null || string.IsNullOrWhiteSpace(cfg.PriceVersion))
            throw new InvalidOperationException("Chưa có bảng giá đã xác nhận. Vào Settings nhập giá, ngày và phiên bản giá.");
        decimal units;
        if (provider.Pricing == ProviderPricing.Tokens)
        {
            if (cfg.OutputUsdPerUnit is null) throw new InvalidOperationException("Thiếu giá output token.");
            // UTF-8 bytes conservatively bound input tokens, including formatting overhead.
            var input = Encoding.UTF8.GetByteCount(TextPrompts.Build(p, stage)) + 1024;
            return Round(input / 1_000_000m * cfg.UsdPerUnit.Value + OutputTokenLimit(p, stage) / 1_000_000m * cfg.OutputUsdPerUnit.Value);
        }
        units = cfg.Id switch
        {
            "elevenlabs" => p.Scenes.Sum(s => s.Narration.Length) / 1000m,
            "openai-image" => stage == Stage.Thumbnail ? 1 : p.Scenes.Count,
            "openai-whisper" => p.Scenes.Sum(s => (decimal)Math.Ceiling(s.AudioSeconds / 60)),
            "shotstack" => (decimal)Math.Ceiling(p.ActualSeconds / 60),
            _ => throw new InvalidOperationException("Chưa có cách dự toán cho provider này.")
        };
        return Round(units * cfg.UsdPerUnit.Value);
    }
    public static decimal Round(decimal amount) => Math.Ceiling(amount * 1_000_000m) / 1_000_000m;
}
