using FacelessStudio.Providers.Tts;

namespace FacelessStudio.Tests;

public class EdgeTtsTests
{
    // Giá trị tham chiếu sinh bằng edge-tts 7.3.1 (drm.generate_sec_ms_gec_v2).
    [Theory]
    [InlineData(1700000000, "2CD23A2CA0B2595A-F3C846C370E05F9CC83ACC5F995C5F5D87A4D0F14CE8936A0F902E64099E38A2")]
    [InlineData(1761300000.5, "FB5DBA9FF3D535F8-C63AE68E039A9D5A9ADD9FA6648858E3F6A6E20D99B3E279E5440EBEA674C198")]
    [InlineData(1800000123, "99E54519B490BE5C-E162DB0977FBDCB9B86DD8148BF1BCDC3C0303176CE39852A42B415935C20654")]
    public void Token_matches_reference_implementation(double unix, string expected)
    {
        Assert.Equal(expected, EdgeTtsToken.Generate(unix));
    }

    [Fact]
    public void SplitByBytes_keeps_chunks_under_limit()
    {
        var text = string.Join(' ', Enumerable.Repeat("tiếng Việt", 500));
        var chunks = EdgeTtsClient.SplitByBytes(text, 400).ToList();
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(System.Text.Encoding.UTF8.GetByteCount(c) <= 400));
        Assert.Equal(text, string.Join(' ', chunks));
    }

    [Fact]
    public void CleanText_replaces_control_characters()
    {
        Assert.Equal("a b c", EdgeTtsClient.CleanText("a\vb\u0001c"));
    }
}
