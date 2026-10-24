namespace FacelessStudio.Core.Models;

public sealed record RenderScene(int Index, double Start, double Duration, IReadOnlyList<MediaAsset> Media);

/// <summary>Mọi thứ renderer cần để dựng video cuối cùng.</summary>
public sealed class RenderPlan
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public int Fps { get; init; } = 30;
    public required string WorkDir { get; init; }
    public required string OutputPath { get; init; }

    /// <summary>Toàn bộ giọng đọc (WAV 44.1 kHz stereo) đã ghép các cảnh.</summary>
    public required string NarrationPath { get; init; }

    public required IReadOnlyList<RenderScene> Scenes { get; init; }
    public required IReadOnlyList<WordTiming> Words { get; init; }

    /// <summary>Phụ đề karaoke (ASS) cho FFmpeg.</summary>
    public required string AssPath { get; init; }

    /// <summary>Phụ đề SRT theo cụm chữ (dùng cho YouTube CC và renderer cloud).</summary>
    public required string SrtPath { get; init; }

    public string? MusicPath { get; init; }

    public bool Vertical => Height > Width;
    public double Duration => Scenes.Count == 0 ? 0 : Scenes[^1].Start + Scenes[^1].Duration;
}
