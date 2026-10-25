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
