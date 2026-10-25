using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Playwright;
using Studio.Domain;
using Studio.Providers;
using Studio.Services;

namespace Studio.Tests;

public sealed class ProviderSettingsBrowserTests
{
    [Fact]
    [Trait("Category", "Browser")]
    public async Task OwnerCanConfigureTextProvidersAndCancelDeviceLoginWithoutGeneratingOrLosingCredentials()
    {
        var textProvider = new SettingsTextProvider();
        var subscriptions = new SettingsSubscriptions();
        using var host = new TestHost
        {
            ConfigureTestServices = services =>
            {
                services.RemoveAll<ISubscriptionAuth>();
                services.AddSingleton<ISubscriptionAuth>(subscriptions);
                services.AddSingleton<ProviderConnectionService>();
                services.RemoveAll<ProviderRegistry>();
                services.AddSingleton(new ProviderRegistry([textProvider]));
            }
        };
        host.UseKestrel(0);
        using var client = host.CreateClient(new() { AllowAutoRedirect = false });
        var origin = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Headless = true, ExecutablePath = Environment.GetEnvironmentVariable("CHROMIUM_PATH"), Args = ["--disable-dev-shm-usage"]
        });
        var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1440, Height = 1050 } });
        var errors = new List<string>(); page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync(origin + "/login");
        await page.GetByLabel("Mật khẩu / Password").FillAsync(TestHost.Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Mở Studio →" }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Câu chuyện tiếp theo của anh." }).WaitForAsync();
        await page.GotoAsync(origin + "/settings?provider=custom-text");
        var panel = page.Locator(".provider-settings");
        await panel.GetByRole(AriaRole.Heading, new() { Name = "Custom Endpoint" }).WaitForAsync();
        await panel.GetByLabel("Base URL của API").FillAsync("http://localhost:1234/prefix/v1");
        await panel.GetByLabel("API key (không bắt buộc)").FillAsync("browser-test-secret");
        await panel.GetByLabel("Model", new() { Exact = true }).FillAsync("manual/model");
        await panel.GetByLabel("Input USD / triệu token").FillAsync("0");
        await panel.GetByLabel("Output USD / triệu token").FillAsync("0");
        await panel.GetByLabel("Phiên bản/nguồn bảng giá").FillAsync("Local test endpoint");
        await panel.GetByLabel("Ngày đã xác minh").FillAsync("2026-10-25");
        await panel.GetByRole(AriaRole.Button, new() { Name = "Lưu cấu hình", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Đã lưu cấu hình. Chưa gọi API tạo nội dung." }).WaitForAsync();
        Assert.Equal(0, textProvider.Generations);
        Assert.Equal(0, textProvider.Checks);
        Assert.Equal("", await panel.GetByLabel("API key (không bắt buộc)").InputValueAsync());
        Assert.DoesNotContain("browser-test-secret", await page.ContentAsync());
        var saved = await host.Get<SettingsService>().GetProvider("custom-text");
        Assert.Equal("manual/model", saved.Model);
        Assert.Equal("http://localhost:1234/prefix/v1", saved.Endpoint);
        Assert.Equal("browser-test-secret", await host.Get<SettingsService>().Secret("provider:custom-text"));

        await panel.GetByRole(AriaRole.Button, new() { Name = "Đọc danh sách model" }).ClickAsync();
        await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Đã đọc danh sách model" }).WaitForAsync();
        Assert.Equal("manual/model", await panel.GetByLabel("Model", new() { Exact = true }).InputValueAsync());
        Assert.Equal("fixture/model", await panel.Locator("datalist option").GetAttributeAsync("value"));
        Assert.Equal(0, textProvider.Generations);
        await panel.GetByRole(AriaRole.Button, new() { Name = "Lưu & kiểm tra xác thực" }).ClickAsync();
        await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Authentication checked; generation has not run." }).WaitForAsync();
        Assert.Equal(1, textProvider.Checks);
        Assert.Equal(0, textProvider.Generations);
        await panel.GetByRole(AriaRole.Button, new() { Name = "Lưu & thử tạo nội dung" }).ClickAsync();
        await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Inference thành công" }).WaitForAsync();
        Assert.Equal(1, textProvider.Generations);

        await Select(page, "ChatGPT / Codex Subscription");
        Assert.Equal(0, await panel.Locator("input[type=password]").CountAsync());
        Assert.Equal(1, await panel.Locator("form[action='/providers/chatgpt-subscription/connect'] input[type=hidden]").CountAsync());
        await panel.GetByLabel("Model", new() { Exact = true }).FillAsync("account-model");
        await panel.GetByLabel("USD dự phòng mỗi lần gọi").FillAsync("0.25");
        await panel.GetByRole(AriaRole.Button, new() { Name = "Lưu cấu hình", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Đã lưu cấu hình. Chưa gọi API tạo nội dung." }).WaitForAsync();
        Assert.Equal(.25m, (await host.Get<SettingsService>().GetProvider("chatgpt-subscription")).SubscriptionReserveUsd);
        var screenshots = Environment.GetEnvironmentVariable("STUDIO_SCREENSHOTS");
        if (screenshots is not null)
        {
            Directory.CreateDirectory(screenshots);
            await page.ScreenshotAsync(new() { Path = Path.Combine(screenshots, "subscription-settings.png"), FullPage = true });
        }

        await Select(page, "Claude Subscription · Claude Code");
        Assert.Equal(0, await panel.Locator("input[type=password]").CountAsync());
        Assert.Equal(0, await panel.Locator("form[action*='connect']").CountAsync());
        await panel.GetByLabel("Đường dẫn Claude Code CLI").FillAsync("/opt/engines/bin/claude");
        Assert.Contains("claude auth login", await panel.InnerTextAsync());

        await Select(page, "SuperGrok OAuth · Experimental");
        await panel.GetByRole(AriaRole.Button, new() { Name = "Đăng nhập SuperGrok" }).ClickAsync();
        await panel.Locator(".device-code").WaitForAsync();
        Assert.Equal("TEST-CODE", await panel.Locator(".device-code").InnerTextAsync());
        Assert.Equal("https://auth.x.ai/verify", await panel.GetByRole(AriaRole.Link, new() { Name = "Mở trang xác nhận" }).GetAttributeAsync("href"));
        await panel.GetByRole(AriaRole.Button, new() { Name = "Hủy đăng nhập" }).ClickAsync();
        await panel.GetByRole(AriaRole.Button, new() { Name = "Đăng nhập SuperGrok" }).WaitForAsync();
        Assert.Equal(1, subscriptions.Cancellations);
        Assert.Equal(0, subscriptions.Disconnections);

        await page.GetByRole(AriaRole.Button, new() { Name = "VI / EN" }).ClickAsync();
        await panel.GetByRole(AriaRole.Button, new() { Name = "Sign in to SuperGrok" }).WaitForAsync();
        Assert.Contains("Experimental: signing in does not guarantee", await panel.InnerTextAsync());
        await page.SetViewportSizeAsync(390, 844);
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"));
        if (screenshots is not null) await page.ScreenshotAsync(new() { Path = Path.Combine(screenshots, "provider-settings-mobile.png"), FullPage = true });

        // A saved provider default should land only on the text stage being edited.
        // New text providers must never appear as voice or media options.
        var project = await host.Get<ProjectService>().Create(new Project { Name = "Provider selection fixture" });
        await page.SetViewportSizeAsync(1440, 1050);
        await page.GotoAsync(origin + "/studio/" + project.Id);
        var stagePanel = page.Locator(".stage-workspace");
        var providerSelect = stagePanel.GetByLabel("Provider", new() { Exact = true });
        await providerSelect.SelectOptionAsync("custom-text");
        await Microsoft.Playwright.Assertions.Expect(stagePanel.GetByLabel("Model", new() { Exact = true })).ToHaveValueAsync("manual/model");
        await stagePanel.GetByLabel("Model", new() { Exact = true }).FillAsync("topic/model");
        await stagePanel.GetByRole(AriaRole.Button, new() { Name = "Lưu cấu hình", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Đã lưu cấu hình." }).WaitForAsync();
        await page.Locator(".pipeline-step").Filter(new() { HasText = "Kịch bản" }).ClickAsync();
        await stagePanel.GetByRole(AriaRole.Heading, new() { Name = "Kịch bản", Exact = true }).WaitForAsync();
        await providerSelect.SelectOptionAsync("chatgpt-subscription");
        await Microsoft.Playwright.Assertions.Expect(stagePanel.GetByLabel("Model", new() { Exact = true })).ToHaveValueAsync("account-model");
        await stagePanel.GetByLabel("Model", new() { Exact = true }).FillAsync("script/model");
        await stagePanel.GetByRole(AriaRole.Button, new() { Name = "Lưu cấu hình", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Đã lưu cấu hình." }).WaitForAsync();
        await Microsoft.Playwright.Assertions.Expect(stagePanel.GetByRole(AriaRole.Button, new() { Name = "Lưu cấu hình", Exact = true })).ToBeEnabledAsync();
        var configuredProject = await host.Get<ProjectService>().Get(project.Id);
        Assert.Equal("custom-text", configuredProject.Steps[Stage.Topic].ProviderId);
        Assert.Equal("topic/model", configuredProject.Steps[Stage.Topic].Model);
        Assert.Equal("chatgpt-subscription", configuredProject.Steps[Stage.Script].ProviderId);
        Assert.Equal("script/model", configuredProject.Steps[Stage.Script].Model);
        Assert.Equal(project.Steps[Stage.Voice].ProviderId, configuredProject.Steps[Stage.Voice].ProviderId);
        Assert.Equal(project.Steps[Stage.Media].ProviderId, configuredProject.Steps[Stage.Media].ProviderId);
        string[] newTextProviders = ["openrouter", "chatgpt-subscription", "anthropic", "claude-code", "zai", "moonshot", "minimax", "deepseek", "xai", "supergrok", "custom-text"];
        foreach (var stageName in new[] { "Giọng đọc", "Hình ảnh & footage" })
        {
            await page.Locator(".pipeline-step").Filter(new() { HasText = stageName }).ClickAsync();
            await stagePanel.GetByRole(AriaRole.Heading, new() { Name = stageName, Exact = true }).WaitForAsync();
            var options = await providerSelect.Locator("option").EvaluateAllAsync<string[]>("items => items.map(item => item.value)");
            Assert.Empty(options.Intersect(newTextProviders));
        }
        Assert.Empty(errors);
    }

    private static async Task Select(IPage page, string name)
    {
        await page.Locator(".settings-nav button").Filter(new() { HasText = name }).ClickAsync();
        await page.Locator(".provider-settings").GetByRole(AriaRole.Heading, new() { Name = name, Exact = true }).WaitForAsync();
    }

    private sealed class SettingsTextProvider : IStageProvider, IProviderModelSource
    {
        public int Generations { get; private set; }
        public int Checks { get; private set; }
        public bool Handles(string id) => id == "custom-text";
        public Task Validate(ProviderConfig config, Stage stage, CancellationToken ct) => Task.CompletedTask;
        public Task<string> CheckConnection(ProviderConfig config, string? secret, CancellationToken ct)
        {
            Checks++; return Task.FromResult("Authentication checked; generation has not run.");
        }
        public Task<IReadOnlyList<ProviderModel>> GetModels(ProviderConfig config, string? secret, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ProviderModel>>([new("fixture/model", "Fixture model")]);
        public Task<ProviderResult> Execute(ProviderContext context, CancellationToken ct)
        {
            Generations++; const string output = "{\"topic\":\"Ocean fixture\"}";
            TextProvider.Apply(context.Project, context.Stage, output);
            return Task.FromResult(new ProviderResult(output, context.Project));
        }
    }

    private sealed class SettingsSubscriptions : ISubscriptionAuth
    {
        public int Cancellations { get; private set; }
        public int Disconnections { get; private set; }
        public string ChatGptRedirectUri => "http://127.0.0.1:5080/auth/callback";
        public Task<ProviderConnectionStatus> Status(string providerId, CancellationToken ct) => Task.FromResult(new ProviderConnectionStatus(false, "Not connected."));
        public Task<OAuthStartResult> Begin(string providerId, string binding, CancellationToken ct)
            => Task.FromResult(new OAuthStartResult("test-flow", null, "https://auth.x.ai/verify", "TEST-CODE", DateTimeOffset.UtcNow.AddMinutes(2), 1));
        public Task<ProviderConnectionStatus> Poll(string providerId, string flowId, string binding, CancellationToken ct)
            => Task.FromResult(new ProviderConnectionStatus(false, "Waiting for authorization."));
        public Task CancelPending(string providerId, string flowId, string binding, CancellationToken ct) { Cancellations++; return Task.CompletedTask; }
        public Task Disconnect(string providerId, CancellationToken ct) { Disconnections++; return Task.CompletedTask; }
        public Task CompleteChatGpt(string state, string code, string issuedClientId, string binding, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> AccessToken(string providerId, bool forceRefresh, CancellationToken ct) => throw new NotSupportedException();
    }
}
