using SkiaSharp;

namespace FacelessStudio.Providers.Thumbnail;

/// <summary>Ghép ảnh nền + chữ lớn viền đen (hỗ trợ tiếng Việt) thành thumbnail JPG.</summary>
public static class ThumbnailComposer
{
    private static readonly string[] FontCandidates =
    [
        "C:/Windows/Fonts/arialbd.ttf",
        "C:/Windows/Fonts/segoeuib.ttf",
        "/System/Library/Fonts/Supplemental/Arial Bold.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
        "/usr/share/fonts/TTF/DejaVuSans-Bold.ttf",
        "/usr/share/fonts/truetype/noto/NotoSans-Bold.ttf",
    ];

    public static SKTypeface LoadTypeface(string? fontFile)
    {
        foreach (var path in new[] { fontFile }.Concat(FontCandidates))
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                var tf = SKTypeface.FromFile(path);
                if (tf != null) return tf;
            }
        }
        // Tìm font hệ thống có chữ "ệ" để không bị lỗi dấu tiếng Việt.
        return SKFontManager.Default.MatchCharacter("Arial", SKFontStyle.Bold, null, 'ệ')
               ?? SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold);
    }

    public static void Compose(string? backgroundPath, string text, string outputPath, int width, int height,
        string? fontFile = null)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(0x14, 0x1e, 0x30));

        if (backgroundPath != null && File.Exists(backgroundPath))
        {
            using var bg = SKBitmap.Decode(backgroundPath);
            if (bg != null) DrawCover(canvas, bg, width, height);
        }

        // Làm tối nền để chữ nổi bật.
        using (var shade = new SKPaint { Color = new SKColor(0, 0, 0, 110) })
            canvas.DrawRect(0, 0, width, height, shade);

        using var typeface = LoadTypeface(fontFile);
        DrawHeadline(canvas, text.Trim().ToUpperInvariant(), typeface, width, height);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 92);
        using var fs = File.Create(outputPath);
        data.SaveTo(fs);
    }

    private static void DrawCover(SKCanvas canvas, SKBitmap bg, int width, int height)
    {
        var scale = Math.Max((float)width / bg.Width, (float)height / bg.Height);
        var w = bg.Width * scale;
        var h = bg.Height * scale;
        var dest = new SKRect((width - w) / 2, (height - h) / 2, (width + w) / 2, (height + h) / 2);
        using var img = SKImage.FromBitmap(bg);
        canvas.DrawImage(img, dest, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
    }

    public static List<string> Wrap(string text, Func<string, float> measure, float maxWidth)
    {
        var lines = new List<string>();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (lines.Count > 0 && measure(lines[^1] + " " + word) <= maxWidth) lines[^1] += " " + word;
            else lines.Add(word);
        }
        return lines;
    }

    private static void DrawHeadline(SKCanvas canvas, string text, SKTypeface typeface, int width, int height)
    {
        if (text.Length == 0) return;
        var maxWidth = width * 0.88f;
        float size = height * 0.22f;
        SKFont font;
        List<string> lines;
        // Giảm cỡ chữ tới khi vừa tối đa 3 dòng.
        while (true)
        {
            font = new SKFont(typeface, size);
            var f = font;
            lines = Wrap(text, s => f.MeasureText(s), maxWidth);
            if ((lines.Count <= 3 && lines.All(l => f.MeasureText(l) <= maxWidth)) || size < 30) break;
            font.Dispose();
            size -= 6;
        }
        using (font)
        {
            var lineHeight = size * 1.15f;
            var y = (height - lineHeight * lines.Count) / 2 + size * 0.9f;
            using var stroke = new SKPaint
            {
                Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(6, size / 9),
                StrokeJoin = SKStrokeJoin.Round, IsAntialias = true,
            };
            using var fill = new SKPaint { Color = new SKColor(0xFF, 0xE6, 0x00), IsAntialias = true };
            foreach (var line in lines)
            {
                canvas.DrawText(line, width / 2f, y, SKTextAlign.Center, font, stroke);
                canvas.DrawText(line, width / 2f, y, SKTextAlign.Center, font, fill);
                y += lineHeight;
            }
        }
    }
}
