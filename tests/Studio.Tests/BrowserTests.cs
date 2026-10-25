using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Studio.Domain;
using Studio.Services;

namespace Studio.Tests;

public sealed class BrowserTests
{
    [Fact]
    [Trait("Category", "Browser")]
    public async Task OwnerCanCreateConfigureEditReviewAndDownloadAProjectInTheBrowser()
    {
        using var host = new TestHost(); host.UseKestrel(0);
        using var client = host.CreateClient(new() { AllowAutoRedirect = false });
        client.BaseAddress = new Uri(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
        using var unauthenticated = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = client.BaseAddress };
        Assert.Equal(HttpStatusCode.Redirect, (await unauthenticated.GetAsync("/media/unknown")).StatusCode);
        using var playwright = await Playwright.CreateAsync();
        var executable = Environment.GetEnvironmentVariable("CHROMIUM_PATH");
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, ExecutablePath = executable, Args = ["--disable-dev-shm-usage"] });
        var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1050 }, AcceptDownloads = true });
        var page = await context.NewPageAsync(); var pageErrors = new List<string>(); page.PageError += (_, error) => pageErrors.Add(error);
        var origin = client.BaseAddress!.ToString().TrimEnd('/');
        await page.GotoAsync(origin + "/login");
        await page.GetByLabel("Mật khẩu / Password").FillAsync(TestHost.Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Mở Studio →" }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Câu chuyện tiếp theo của anh." }).WaitForAsync();
        var screenshots = Environment.GetEnvironmentVariable("STUDIO_SCREENSHOTS");
        if (screenshots is not null) { Directory.CreateDirectory(screenshots); await page.ScreenshotAsync(new() { Path = Path.Combine(screenshots, "dashboard.png"), FullPage = true }); }
        await page.GetByRole(AriaRole.Button, new() { Name = "VI / EN" }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Your next story starts here." }).WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "EN / VI" }).ClickAsync();
        await page.Locator(".page-heading").GetByRole(AriaRole.Link, new() { Name = "＋ Tạo video mới", Exact = true }).ClickAsync();
        await page.GetByLabel("Tên dự án", new() { Exact = true }).FillAsync("Hành trình đại dương");
        await page.GetByLabel("Ý tưởng hoặc yêu cầu chọn chủ đề").FillAsync("Khám phá đại dương bằng tiếng Việt");
        await page.GetByLabel("Ngôn ngữ video").SelectOptionAsync("vi");
        await page.GetByLabel("Trần ngân sách / video").FillAsync("0");
        await page.GetByRole(AriaRole.Button, new() { Name = "⌂ Self-hosted" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Tạo dự án →" }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Hành trình đại dương SHORTS" }).WaitForAsync();
        await page.GetByLabel("Chủ đề đã chọn").FillAsync("Bí mật của biển sâu");
        await page.GetByRole(AriaRole.Button, new() { Name = "Lưu nội dung chỉnh sửa" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Duyệt kết quả", Exact = false }).ClickAsync();
        await page.Locator(".pipeline-step").Filter(new() { HasText = "Kịch bản" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Mở trình sửa cảnh" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Thêm cảnh" }).ClickAsync();
        await page.GetByLabel("Lời đọc", new() { Exact = true }).FillAsync("Xin chào, cùng khám phá đại dương.");
        await page.GetByLabel("Mô tả hình ảnh (tiếng Anh)").FillAsync("Deep blue ocean with sun rays");
        await page.GetByRole(AriaRole.Button, new() { Name = "Lưu kịch bản & các cảnh" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Công đoạn", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Duyệt kết quả", Exact = false }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Preview & YouTube", Exact = true }).ClickAsync();
        var download = await page.RunAndWaitForDownloadAsync(() => page.GetByRole(AriaRole.Link, new() { Name = "Kịch bản", Exact = true }).ClickAsync());
        Assert.Equal("script.txt", download.SuggestedFilename);
        var file = await download.PathAsync(); Assert.Contains("đại dương", await File.ReadAllTextAsync(file!));
        Assert.True(await page.GetByRole(AriaRole.Button, new() { Name = "Duyệt & đăng YouTube", Exact = false }).IsDisabledAsync());
        // Seed a real media fixture to exercise the authenticated video endpoint, playback and MP4 download.
        // Generation itself is covered by RenderTests; this is not a cloud-generated demo result.
        var previewProject = Assert.Single(await host.Get<ProjectService>().List());
        var assetStore = host.Get<AssetStore>(); var fixtureDirectory = assetStore.WorkDir(Guid.NewGuid());
        var videoFile = Path.Combine(fixtureDirectory, "preview.mp4");
        await host.Get<ProcessRunner>().Run("ffmpeg", ["-y", "-v", "error", "-f", "lavfi", "-i", "color=c=0x2c503b:s=1080x1920:d=1", "-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-c:v", "libx264", "-preset", "ultrafast", "-threads", "2", "-pix_fmt", "yuv420p", "-c:a", "aac", "-movflags", "+faststart", videoFile], CancellationToken.None);
        previewProject.VideoAssetId = (await assetStore.Add(previewProject.Id, videoFile, "video.mp4", "video/mp4", CancellationToken.None)).Id;
        previewProject.Steps[Stage.Render].Status = StepStatus.Approved;
        await host.Get<ProjectService>().Edit(previewProject, null, "Browser playback fixture");
        await page.GetByRole(AriaRole.Button, new() { Name = "Tải lại", Exact = false }).ClickAsync();
        await page.WaitForFunctionAsync("() => { const v = document.querySelector('.video-preview video'); return v && Number.isFinite(v.duration) && v.duration > 0; }");
        await page.Locator(".video-preview video").EvaluateAsync("v => v.play()");
        await page.WaitForFunctionAsync("document.querySelector('.video-preview video').currentTime > 0");
        var mp4 = await page.RunAndWaitForDownloadAsync(() => page.GetByRole(AriaRole.Link, new() { Name = "↓ MP4", Exact = true }).ClickAsync());
        Assert.Equal("video.mp4", mp4.SuggestedFilename); Assert.True(new FileInfo((await mp4.PathAsync())!).Length > 1000);
        if (screenshots is not null) await page.ScreenshotAsync(new() { Path = Path.Combine(screenshots, "studio.png"), FullPage = true });
        await page.SetViewportSizeAsync(390, 844);
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"));
        if (screenshots is not null) await page.ScreenshotAsync(new() { Path = Path.Combine(screenshots, "studio-mobile.png"), FullPage = true });
        Assert.Empty(pageErrors);
        var p = Assert.Single(await host.Get<ProjectService>().List());
        Assert.Equal("Bí mật của biển sâu", p.Topic); Assert.Single(p.Scenes); Assert.Equal(StepStatus.Approved, p.Steps[Stage.Script].Status);
    }
}
