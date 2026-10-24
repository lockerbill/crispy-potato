using FacelessStudio.Core.Models;

namespace FacelessStudio.Core.Subtitles;

/// <summary>Các phép tính thời gian cho từng chữ trong phụ đề.</summary>
public static class WordTimingMath
{
    private static double Weight(string word) => word.Length + 1;

    /// <summary>Ước lượng thời điểm từng chữ theo độ dài ký tự, khi không có dữ liệu timing thật.</summary>
    public static List<WordTiming> Estimate(IReadOnlyList<string> words, double start, double duration)
    {
        var result = new List<WordTiming>(words.Count);
        if (words.Count == 0 || duration <= 0) return result;
        var total = words.Sum(Weight);
        var t = start;
        foreach (var w in words)
        {
            var d = duration * Weight(w) / total;
            result.Add(new WordTiming(w, t, t + d));
            t += d;
        }
        return result;
    }

    /// <summary>
    /// Gán chữ của kịch bản lên các chữ nhận dạng được (TTS hoặc Whisper).
    /// Chữ nhận dạng có thể khác chính tả hoặc số lượng, nên ta trải chữ kịch bản dọc theo
    /// phần thời gian có tiếng nói (bỏ qua khoảng lặng) theo tỉ lệ độ dài ký tự.
    /// </summary>
    public static List<WordTiming> MapOnto(IReadOnlyList<string> scriptWords, IReadOnlyList<WordTiming> recognized,
        double fallbackStart, double fallbackDuration)
    {
        var spans = recognized.Where(r => r.End > r.Start).OrderBy(r => r.Start).ToList();
        if (scriptWords.Count == 0) return [];
        if (spans.Count == 0) return Estimate(scriptWords, fallbackStart, fallbackDuration);
        if (spans.Count == scriptWords.Count)
            return scriptWords.Select((w, i) => new WordTiming(w, spans[i].Start, spans[i].End)).ToList();

        var speech = spans.Sum(s => s.End - s.Start);
        var total = scriptWords.Sum(Weight);
        var result = new List<WordTiming>(scriptWords.Count);
        double cumulative = 0;
        foreach (var w in scriptWords)
        {
            var from = SpeechToTime(spans, speech * cumulative / total, atStart: true);
            cumulative += Weight(w);
            var to = SpeechToTime(spans, speech * cumulative / total, atStart: false);
            result.Add(new WordTiming(w, from, Math.Max(to, from + 0.01)));
        }
        return result;
    }

    /// <summary>
    /// Đổi vị trí trên trục "chỉ tính lúc có tiếng" sang thời gian thật. Ở đúng ranh giới hai đoạn nói,
    /// điểm bắt đầu của chữ thuộc về đoạn sau, điểm kết thúc thuộc về đoạn trước.
    /// </summary>
    private static double SpeechToTime(List<WordTiming> spans, double position, bool atStart)
    {
        for (var i = 0; i < spans.Count; i++)
        {
            var len = spans[i].End - spans[i].Start;
            var inside = atStart && i + 1 < spans.Count ? position < len - 1e-9 : position <= len + 1e-9;
            if (inside) return spans[i].Start + Math.Max(0, position);
            position -= len;
        }
        return spans[^1].End;
    }

    /// <summary>
    /// Từ danh sách chữ nhận dạng trên toàn bộ giọng đọc (Whisper), chia về từng cảnh rồi gán chữ của kịch bản.
    /// </summary>
    public static List<WordTiming> AssembleFromRecognized(IReadOnlyList<SceneAudio> scenes, IReadOnlyList<Scene> script,
        IReadOnlyList<WordTiming> recognized)
    {
        var result = new List<WordTiming>();
        foreach (var scene in scenes)
        {
            var end = scene.Start + scene.Duration;
            var inScene = recognized
                .Where(w => (w.Start + w.End) / 2 >= scene.Start && (w.Start + w.End) / 2 < end)
                .Select(w => w with
                {
                    Start = Math.Clamp(w.Start - scene.Start, 0, scene.Duration),
                    End = Math.Clamp(w.End - scene.Start, 0, scene.Duration),
                })
                .ToList();
            var mapped = MapOnto(ScriptText.Words(script[scene.Index].Narration), inScene, 0, scene.SpeechDuration);
            result.AddRange(Shift(mapped, scene.Start));
        }
        return result;
    }

    /// <summary>Cộng thêm thời điểm bắt đầu cảnh vào timing tương đối.</summary>
    public static IEnumerable<WordTiming> Shift(IEnumerable<WordTiming> words, double offset) =>
        words.Select(w => w with { Start = w.Start + offset, End = w.End + offset });
}
