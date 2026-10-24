using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Models;
using FacelessStudio.Core.Settings;
using FacelessStudio.Core.Subtitles;

namespace FacelessStudio.Tests;

public class TimingAndThumbnailTests
{
    private static readonly IReadOnlyList<SceneAudio> Scenes =
    [
        new SceneAudio { Index = 0, WavPath = "", Start = 0, Duration = 2, SpeechDuration = 1.6 },
        new SceneAudio { Index = 1, WavPath = "", Start = 2, Duration = 2, SpeechDuration = 1.6 },
    ];

    private static readonly IReadOnlyList<Scene> Script =
    [
        new("Xin chào", "", ""),
        new("tạm biệt nhé", "", ""),
    ];

    [Fact]
    public void AssembleFromRecognized_splits_words_into_scenes()
    {
        var recognized = new List<WordTiming>
        {
            new("xin", 0.1, 0.4), new("chao", 0.5, 0.9),
            new("tam", 2.2, 2.5), new("biet", 2.6, 2.9), new("nhe", 3.0, 3.3),
        };
        var words = WordTimingMath.AssembleFromRecognized(Scenes, Script, recognized);
        Assert.Equal(["Xin", "chào", "tạm", "biệt", "nhé"], words.Select(w => w.Word));
        Assert.Equal(2.2, words[2].Start, 3);
        Assert.Equal(3.3, words[4].End, 3);
    }

    [Fact]
    public async Task Groq_whisper_uploads_audio_and_maps_words()
    {
        if (!await new Core.Media.FfmpegTool().IsAvailableAsync()) return;
        var http = new FakeHttp().On("api.groq.com/openai/v1/audio/transcriptions", """
            {"text":"xin chao","words":[{"word":"xin","start":0.1,"end":0.4},{"word":"chao","start":0.5,"end":0.9},
              {"word":"tam","start":2.2,"end":2.5},{"word":"biet","start":2.6,"end":2.9},{"word":"nhe","start":3.0,"end":3.3}]}
            """);
        using var sp = http.BuildServices();
        var ctx = FakeHttp.Context(new() { [SettingKeys.GroqApiKey] = "gq" });
        var wav = Path.Combine(ctx.WorkDir, "n.wav");
        await ctx.Ffmpeg.RunAsync(["-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo", "-t", "4", wav]);

        var words = await FakeHttp.Provider<ITimingProvider>(sp, "timing-groq")
            .AlignAsync(new TimingRequest(Scenes, Script, wav), ctx);

        Assert.Equal(5, words.Count);
        var req = Assert.Single(http.Requests);
        Assert.Equal("Bearer gq", req.Headers["authorization"]);
        Assert.Contains("whisper-large-v3-turbo", req.Body);
        Assert.Contains("timestamp_granularities[]", req.Body);
    }

    [Fact]
    public async Task Cloudflare_thumbnail_falls_back_to_frame_on_error()
    {
        var http = new FakeHttp().On("api.cloudflare.com", """{"success":false}""", System.Net.HttpStatusCode.Forbidden);
        using var sp = http.BuildServices();
        var ctx = FakeHttp.Context(new()
        {
            [SettingKeys.CloudflareAccountId] = "acc",
            [SettingKeys.CloudflareApiToken] = "tok",
        });
        var output = Path.Combine(ctx.WorkDir, "thumb.jpg");
        var messages = new List<string>();
        var context = new StepContext
        {
            Request = ctx.Request, Settings = ctx.Settings, WorkDir = ctx.WorkDir, Ffmpeg = ctx.Ffmpeg, Log = messages.Add,
        };
        await FakeHttp.Provider<IThumbnailProvider>(sp, "thumb-cloudflare").CreateAsync(
            new ThumbnailRequest(new VideoScript { Title = "Bí ẩn đại dương" }, null, output), context);

        Assert.True(new FileInfo(output).Length > 1000);
        Assert.Contains("/accounts/acc/ai/run/@cf/black-forest-labs/flux-1-schnell", http.Requests[0].Url);
        Assert.Contains(messages, m => m.Contains("dùng khung hình"));
    }
}
