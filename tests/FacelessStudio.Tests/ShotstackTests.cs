using System.Text.Json.Nodes;
using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Models;
using FacelessStudio.Core.Settings;
using FacelessStudio.Providers.Render;

namespace FacelessStudio.Tests;

public class ShotstackTests
{
    private static RenderPlan Plan(string work, IReadOnlyList<RenderScene> scenes) => new()
    {
        Width = 1080, Height = 1920, WorkDir = work, OutputPath = Path.Combine(work, "out.mp4"),
        NarrationPath = Path.Combine(work, "n.wav"), Scenes = scenes, Words = [],
        AssPath = Path.Combine(work, "s.ass"), SrtPath = Path.Combine(work, "s.srt"),
    };

    [Fact]
    public void BuildEdit_layers_captions_visuals_and_narration()
    {
        var scenes = new List<RenderScene>
        {
            new(0, 0, 4, [new MediaAsset(MediaKind.Video, "/clip.mp4", "https://cdn/clip.mp4")]),
            new(1, 4, 3, [new MediaAsset(MediaKind.Image, "/a.png", null)]),
            new(2, 7, 2, [new MediaAsset(MediaKind.Gradient, null, null)]),
        };
        var edit = ShotstackRenderProvider.BuildEdit(Plan("/w", scenes), "https://n.mp3", "https://s.srt", null,
            m => m.PublicUrl ?? (m.LocalPath == "/a.png" ? "https://up/a.png" : null), _ => 2.5);

        var tracks = edit["timeline"]!["tracks"]!.AsArray();
        Assert.Equal("caption", tracks[0]!["clips"]![0]!["asset"]!["type"]!.GetValue<string>());
        Assert.Equal(9, tracks[0]!["clips"]![0]!["length"]!.GetValue<double>(), 3);
        var visual = tracks[1]!["clips"]!.AsArray();
        // Clip 2.5s phải lặp để phủ 4s; cảnh nền màu không có clip.
        Assert.Equal(3, visual.Count);
        Assert.Equal(1.5, visual[1]!["length"]!.GetValue<double>(), 3);
        Assert.Equal("zoomOut", visual[2]!["effect"]!.GetValue<string>());
        Assert.Equal("https://n.mp3", tracks[2]!["clips"]![0]!["asset"]!["src"]!.GetValue<string>());
        Assert.Equal(1920, edit["output"]!["size"]!["height"]!.GetValue<int>());
    }

    [Fact]
    public async Task Sandbox_render_uploads_polls_and_downloads()
    {
        var ff = new Core.Media.FfmpegTool();
        if (!await ff.IsAvailableAsync()) return;
        ShotstackRenderProvider.PollInterval = TimeSpan.Zero;

        var http = new FakeHttp()
            .On("/ingest/stage/upload", """{"data":{"id":"src1","attributes":{"url":"https://s3.example/put"}}}""")
            .On("s3.example/put", "")
            .On("/ingest/stage/sources/src1", """{"data":{"attributes":{"status":"ready","source":"https://cdn.shotstack/file"}}}""")
            .On("/edit/stage/render", """{"success":true,"response":{"id":"r1"}}""")
            .On("/edit/stage/render/r1", """{"response":{"status":"done","url":"https://cdn.shotstack/out.mp4"}}""")
            .OnBytes("https://cdn.shotstack/out.mp4", [1, 2, 3], "video/mp4");
        using var sp = http.BuildServices();
        var ctx = FakeHttp.Context(new() { [SettingKeys.ShotstackSandboxKey] = "ss" });
        var plan = Plan(ctx.WorkDir, [new RenderScene(0, 0, 2, [new MediaAsset(MediaKind.Gradient, null, null)])]);
        await ff.RunAsync(["-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo", "-t", "2", plan.NarrationPath]);
        await File.WriteAllTextAsync(plan.SrtPath, "1\n00:00:00,000 --> 00:00:01,000\nxin chào\n");

        await FakeHttp.Provider<IRenderProvider>(sp, "render-shotstack-sandbox").RenderAsync(plan, ctx);

        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(plan.OutputPath));
        var render = http.Requests.First(r => r.Url.EndsWith("/edit/stage/render"));
        Assert.Equal("ss", render.Headers["x-api-key"]);
        Assert.Equal("https://cdn.shotstack/file",
            JsonNode.Parse(render.Body)!["timeline"]!["tracks"]![2]!["clips"]![0]!["asset"]!["src"]!.GetValue<string>());
        Assert.Equal(2, http.Requests.Count(r => r.Url.Contains("s3.example/put")));
    }
}
