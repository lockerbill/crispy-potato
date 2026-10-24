using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FacelessStudio.Core.Models;

namespace FacelessStudio.Core.Subtitles;

public sealed record CaptionGroup(IReadOnlyList<WordTiming> Words)
{
    public double Start => Words[0].Start;
    public double End => Words[^1].End;
    public string Text => string.Join(' ', Words.Select(w => w.Word));
}

public static partial class SubtitleWriter
{
    [GeneratedRegex(@"[.,!?;:…]$")]
    private static partial Regex ClauseEnd();

    /// <summary>Gom chữ thành cụm ngắn để hiển thị, ưu tiên ngắt sau dấu câu hoặc khi có khoảng lặng.</summary>
    public static List<CaptionGroup> Group(IReadOnlyList<WordTiming> words, int maxWords, double maxGap = 0.6)
    {
        var groups = new List<CaptionGroup>();
        var current = new List<WordTiming>();
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            if (current.Count > 0 && w.Start - current[^1].End > maxGap)
            {
                groups.Add(new CaptionGroup(current));
                current = [];
            }
            current.Add(w);
            var endsClause = ClauseEnd().IsMatch(w.Word);
            if (current.Count >= maxWords || (endsClause && current.Count >= Math.Max(2, maxWords / 2)))
            {
                groups.Add(new CaptionGroup(current));
                current = [];
            }
        }
        if (current.Count > 0) groups.Add(new CaptionGroup(current));
        return groups;
    }

    public static int MaxWordsFor(bool vertical) => vertical ? 3 : 7;

    public static string ToSrt(IReadOnlyList<CaptionGroup> groups)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            var end = i + 1 < groups.Count ? Math.Min(g.End + 0.3, groups[i + 1].Start) : g.End + 0.3;
            sb.Append(i + 1).Append('\n')
              .Append(SrtTime(g.Start)).Append(" --> ").Append(SrtTime(end)).Append('\n')
              .Append(g.Text).Append("\n\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Phụ đề karaoke: hiện cả cụm, chữ đang được đọc đổi màu vàng và phóng to.
    /// Mỗi chữ là một event riêng nên chữ sáng lên đúng lúc giọng đọc tới.
    /// </summary>
    public static string ToAssKaraoke(IReadOnlyList<CaptionGroup> groups, int width, int height, string font)
    {
        var vertical = height > width;
        var size = vertical ? 92 : 64;
        var marginV = vertical ? 560 : 90;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"""
            [Script Info]
            ScriptType: v4.00+
            PlayResX: {width}
            PlayResY: {height}
            WrapStyle: 0
            ScaledBorderAndShadow: yes

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Default,{font},{size},&H00FFFFFF,&H000000FF,&H00000000,&H96000000,-1,0,0,0,100,100,0,0,1,6,3,2,70,70,{marginV},1

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text

            """);

        for (var gi = 0; gi < groups.Count; gi++)
        {
            var g = groups[gi];
            var nextStart = gi + 1 < groups.Count ? groups[gi + 1].Start : double.MaxValue;
            var words = g.Words.Select(w => Escape(vertical ? w.Word.ToUpperInvariant() : w.Word)).ToArray();
            for (var k = 0; k < g.Words.Count; k++)
            {
                var start = g.Words[k].Start;
                var end = k + 1 < g.Words.Count
                    ? g.Words[k + 1].Start
                    : Math.Min(g.Words[k].End + 0.35, nextStart);
                if (end <= start) end = start + 0.05;
                var text = new StringBuilder();
                for (var j = 0; j < words.Length; j++)
                {
                    if (j > 0) text.Append(' ');
                    if (j == k) text.Append(@"{\c&H0000E6FF&\fscx112\fscy112}").Append(words[j]).Append(@"{\r}");
                    else text.Append(words[j]);
                }
                sb.Append(CultureInfo.InvariantCulture,
                    $"Dialogue: 0,{AssTime(start)},{AssTime(end)},Default,,0,0,0,,{text}\n");
            }
        }
        return sb.ToString();
    }

    private static string Escape(string s) => s.Replace("\\", "").Replace("{", "(").Replace("}", ")");

    public static string SrtTime(double t)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Round(Math.Max(0, t) * 1000));
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00},{ts.Milliseconds:000}";
    }

    public static string AssTime(double t)
    {
        var cs = (long)Math.Round(Math.Max(0, t) * 100);
        var h = cs / 360000;
        var m = cs / 6000 % 60;
        var s = cs / 100 % 60;
        return $"{h}:{m:00}:{s:00}.{cs % 100:00}";
    }
}
