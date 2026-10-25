using System.Text.Json.Nodes;
using Studio.Domain;
using Studio.Services;

namespace Studio.Tests;

public sealed class RenderTests
{
    [Theory]
    [InlineData("vi", VideoFormat.Short)] [InlineData("en", VideoFormat.Short)]
    [InlineData("vi", VideoFormat.Long)] [InlineData("en", VideoFormat.Long)]
    [Trait("Category", "Media")]
    public async Task RealFfmpegProducesDecodable1080pVideoWithAudioAndWordCaptions(string language, VideoFormat format)
    {
        using var host = new TestHost(); var process = host.Get<ProcessRunner>(); var assets = host.Get<AssetStore>(); var media = host.Get<MediaEngine>();
        var p = new Project { Language = language, Format = format, TargetSeconds = format == VideoFormat.Short ? 15 : 60 };
        var dir = assets.WorkDir(Guid.NewGuid());
        var image = Path.Combine(dir, "background.png"); var audio = Path.Combine(dir, "narration.wav");
        await process.Run("ffmpeg", ["-y", "-v", "error", "-f", "lavfi", "-i", "color=c=0x284433:s=640x360", "-frames:v", "1", "-threads", "1", image], CancellationToken.None);
        await process.Run("ffmpeg", ["-y", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-ar", "16000", "-ac", "1", audio], CancellationToken.None);
        var imageAsset = await assets.Add(p.Id, image, "background.png", "image/png", CancellationToken.None);
        var audioAsset = await assets.Add(p.Id, audio, "narration.wav", "audio/wav", CancellationToken.None);
        p.Scenes = [new Scene { Narration = language == "vi" ? "Xin chào thế giới" : "Hello beautiful world", MediaAssetId = imageAsset.Id, AudioAssetId = audioAsset.Id, AudioSeconds = 2, DurationSeconds = 2, Transition = "fade" }];
        p.Captions = language == "vi" ? [new("Xin", .1, .5), new("chào", .5, 1), new("thế", 1, 1.4), new("giới", 1.4, 1.9)] : [new("Hello", .1, .6), new("beautiful", .6, 1.2), new("world", 1.2, 1.9)];
        var result = await media.Render(p, dir, CancellationToken.None);
        var probe = JsonNode.Parse(await process.Run("ffprobe", ["-v", "error", "-show_streams", "-show_format", "-of", "json", result], CancellationToken.None))!;
        var video = probe["streams"]!.AsArray().Single(s => s!["codec_type"]!.GetValue<string>() == "video")!;
        Assert.Equal(p.Width, video["width"]!.GetValue<int>()); Assert.Equal(p.Height, video["height"]!.GetValue<int>()); Assert.Equal("h264", video["codec_name"]!.GetValue<string>());
        Assert.Contains(probe["streams"]!.AsArray(), s => s!["codec_name"]!.GetValue<string>() == "aac");
        Assert.InRange(await media.Duration(result, CancellationToken.None), 2, 2.15);
        await process.Run("ffmpeg", ["-v", "error", "-i", result, "-f", "null", "-"], CancellationToken.None);
        var thumb = await media.Thumbnail(image, language == "vi" ? "Khám phá thế giới" : "Explore the world", dir, CancellationToken.None);
        Assert.True(new FileInfo(thumb).Length > 1000);
        var frame = Path.Combine(dir, "caption-frame.png");
        await process.Run("ffmpeg", ["-y", "-v", "error", "-ss", "0.7", "-i", result, "-frames:v", "1", "-threads", "1", frame], CancellationToken.None);
        Assert.True(new FileInfo(frame).Length > 5000);
    }
}
