using FacelessStudio.Core.Models;
using FacelessStudio.Core.Pipeline;
using FacelessStudio.Core.Subtitles;

namespace FacelessStudio.Tests;

public class SubtitleTests
{
    private static List<WordTiming> Words(params (string W, double S, double E)[] items) =>
        items.Select(i => new WordTiming(i.W, i.S, i.E)).ToList();

    [Fact]
    public void Estimate_spans_whole_duration_in_order()
    {
        var t = WordTimingMath.Estimate(["Xin", "chào", "các", "bạn"], 2, 4);
        Assert.Equal(2, t[0].Start, 3);
        Assert.Equal(6, t[^1].End, 3);
        Assert.True(t.Zip(t.Skip(1)).All(p => p.First.End <= p.Second.Start + 1e-9));
    }

    [Fact]
    public void MapOnto_uses_recognized_times_when_counts_match()
    {
        var rec = Words(("xin", 0.1, 0.3), ("chao", 0.4, 0.8));
        var mapped = WordTimingMath.MapOnto(["Xin", "chào"], rec, 0, 1);
        Assert.Equal("chào", mapped[1].Word);
        Assert.Equal(0.4, mapped[1].Start, 3);
    }

    [Fact]
    public void MapOnto_skips_silence_when_counts_differ()
    {
        // Hai đoạn nói cách nhau 2 giây im lặng.
        var rec = Words(("a", 0, 1), ("b", 3, 4));
        var mapped = WordTimingMath.MapOnto(["aa", "bb", "cc", "dd"], rec, 0, 4);
        Assert.Equal(4, mapped.Count);
        Assert.True(mapped[1].End <= 1.0001);
        Assert.True(mapped[2].Start >= 3 - 1e-6);
        Assert.Equal(4, mapped[^1].End, 3);
    }

    [Fact]
    public void Group_breaks_on_max_words_punctuation_and_gaps()
    {
        var w = Words(("Một", 0, 0.2), ("hai,", 0.2, 0.4), ("ba", 0.4, 0.6), ("bốn", 0.6, 0.8), ("năm", 2.0, 2.2));
        var groups = SubtitleWriter.Group(w, 3);
        Assert.Equal(["Một hai,", "ba bốn", "năm"], groups.Select(g => g.Text));
    }

    [Fact]
    public void Ass_karaoke_highlights_each_word()
    {
        var groups = SubtitleWriter.Group(Words(("xin", 0, 0.5), ("chào", 0.5, 1.0)), 3);
        var ass = SubtitleWriter.ToAssKaraoke(groups, 1080, 1920, "Arial");
        var dialogues = ass.Split('\n').Where(l => l.StartsWith("Dialogue:")).ToList();
        Assert.Equal(2, dialogues.Count);
        Assert.Contains(@"{\c&H0000E6FF&\fscx112\fscy112}XIN{\r} CHÀO", dialogues[0]);
        Assert.Contains(@"XIN {\c&H0000E6FF&\fscx112\fscy112}CHÀO{\r}", dialogues[1]);
        Assert.Contains("PlayResY: 1920", ass);
    }

    [Fact]
    public void Srt_and_ass_time_formats()
    {
        Assert.Equal("01:02:03,456", SubtitleWriter.SrtTime(3723.456));
        Assert.Equal("1:02:03.46", SubtitleWriter.AssTime(3723.456));
    }

    [Theory]
    [InlineData("Bí ẩn Đại dương sâu thẳm!", "bi-an-dai-duong-sau-tham")]
    [InlineData("???", "video")]
    public void Slugify_handles_vietnamese(string input, string expected)
    {
        Assert.Equal(expected, VideoPipeline.Slugify(input));
    }

    [Fact]
    public void Custom_narration_is_kept_verbatim()
    {
        var req = new VideoRequest { Topic = "Biển", CustomScript = "Đoạn một.\n\nĐoạn hai." };
        var ai = new VideoScript { Title = "T", Scenes = [new Scene("viết lại", "ocean", "sea")] };
        var s = VideoPipeline.KeepCustomNarration(ai, req);
        Assert.Equal(["Đoạn một.", "Đoạn hai."], s.Scenes.Select(x => x.Narration));
        Assert.Equal("ocean", s.Scenes[0].FootageQuery);
        Assert.Equal("Biển", s.Scenes[1].FootageQuery);
    }
}
