using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Studio.Data;
using Studio.Domain;
using Studio.Providers;
using Studio.Services;

namespace Studio.Tests;

public sealed class ConnectionBudgetTests
{
    private sealed class NoNetworkTextProvider : IStageProvider
    {
        public bool Handles(string id) => ProviderCatalog.Get(id).IsText;
        public Task Validate(ProviderConfig config, Stage stage, CancellationToken ct) => Task.CompletedTask;
        public Task<string> CheckConnection(ProviderConfig config, string? secret, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProviderResult> Execute(ProviderContext context, CancellationToken ct) => throw new NotSupportedException();
    }

    private static TestHost Host() => new()
    {
        ConfigureTestServices = services =>
        {
            services.RemoveAll<IStageProvider>();
            services.AddSingleton<IStageProvider, NoNetworkTextProvider>();
        }
    };

    private static Project ProjectFor(string id, decimal budget = 1)
    {
        var p = new Project { Budget = budget };
        ProviderCatalog.ApplyPreset(p, "local");
        p.Steps[Stage.Topic].ProviderId = id;
        p.Steps[Stage.Topic].Model = "test-model";
        return p;
    }

    [Theory]
    [InlineData("openrouter")] [InlineData("anthropic")] [InlineData("zai")]
    [InlineData("moonshot")] [InlineData("minimax")] [InlineData("deepseek")]
    [InlineData("xai")] [InlineData("custom-text")] [InlineData("openai-text")]
    public void AllTokenProvidersRequireBothPricesAndReserveInputAndOutput(string id)
    {
        var cfg = new ProviderConfig { Id = id, Model = "test-model", UsdPerUnit = 1, PriceAsOf = DateTimeOffset.UtcNow, PriceVersion = "test" };
        var p = ProjectFor(id);
        Assert.Throws<InvalidOperationException>(() => BudgetService.Estimate(p, Stage.Topic, cfg));
        cfg.OutputUsdPerUnit = 10;
        Assert.True(BudgetService.Estimate(p, Stage.Topic, cfg) > BudgetService.OutputTokenLimit(p, Stage.Topic) * 10m / 1_000_000m);
    }

    [Theory]
    [InlineData("chatgpt-subscription")] [InlineData("supergrok")] [InlineData("claude-code")]
    public void SubscriptionsRequirePositivePerCallReservation(string id)
    {
        var cfg = new ProviderConfig { Id = id };
        Assert.Throws<InvalidOperationException>(() => BudgetService.Estimate(ProjectFor(id), Stage.Topic, cfg));
        cfg.SubscriptionReserveUsd = 0;
        Assert.Throws<InvalidOperationException>(() => BudgetService.Estimate(ProjectFor(id), Stage.Topic, cfg));
        cfg.SubscriptionReserveUsd = .1234561m;
        Assert.Equal(.123457m, BudgetService.Estimate(ProjectFor(id), Stage.Topic, cfg));
    }

    [Fact]
    public async Task OAuthMustBeConnectedAndReservationIsIncludedWithoutTokenSnapshots()
    {
        using var host = Host();
        var settings = host.Get<SettingsService>();
        var projects = host.Get<ProjectService>();
        var cfg = new ProviderConfig { Id = "chatgpt-subscription", Model = "test-model", SubscriptionReserveUsd = .75m };
        await settings.SaveProvider(cfg);
        var p = await projects.Create(ProjectFor(cfg.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => projects.Queue(p.Id, Stage.Topic));
        Assert.Empty(await projects.Charges(p.Id));
        const string secret = "test-oauth-bundle-not-a-real-token";
        await settings.Save("oauth:" + cfg.Id, new { account = "test" }, secret);
        Assert.True((await settings.GetProvider(cfg.Id)).Configured);
        await projects.Queue(p.Id, Stage.Topic);
        Assert.Equal(.75m, Assert.Single(await projects.Charges(p.Id)).AmountUsd);
        var job = Assert.Single(await projects.Jobs(p.Id));
        Assert.DoesNotContain(secret, job.ConfigJson + job.RequestJson);
        await using var db = await host.Get<IDbContextFactory<StudioDb>>().CreateDbContextAsync();
        var row = await db.Settings.FindAsync("oauth:" + cfg.Id);
        Assert.DoesNotContain(secret, row!.Json + row.ProtectedSecret);
        var low = await projects.Create(ProjectFor(cfg.Id, .5m));
        await Assert.ThrowsAsync<InvalidOperationException>(() => projects.Queue(low.Id, Stage.Topic));
        Assert.Empty(await projects.Jobs(low.Id));
    }

    [Fact]
    public async Task CustomLocalEndpointCanHaveExplicitZeroPricingWithoutAnApiKey()
    {
        using var host = Host();
        var settings = host.Get<SettingsService>();
        var cfg = new ProviderConfig { Id = "custom-text", Model = "test-model", Endpoint = "http://localhost:1234/v1", UsdPerUnit = 0, OutputUsdPerUnit = 0, PriceAsOf = DateTimeOffset.UtcNow, PriceVersion = "local-zero-confirmed" };
        await settings.SaveProvider(cfg);
        var projects = host.Get<ProjectService>();
        var p = await projects.Create(ProjectFor(cfg.Id, 0));
        await projects.Queue(p.Id, Stage.Topic);
        Assert.Equal(0, Assert.Single(await projects.Charges(p.Id)).AmountUsd);
        p = await projects.Create(ProjectFor(cfg.Id, 0));
        p.Steps[Stage.Topic].Model = "different-priced-model";
        await projects.Edit(p, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => projects.Queue(p.Id, Stage.Topic));
    }

    [Fact]
    public async Task SavingCredentialOrEndpointChangesInvalidatesOldConnectionChecks()
    {
        using var host = Host(); var settings = host.Get<SettingsService>();
        var cfg = new ProviderConfig { Id = "custom-text", Model = "test-model", Endpoint = "http://localhost:1234/v1" };
        await settings.SaveProvider(cfg, "old-test-key");
        cfg.LastCheck = "Previously verified"; cfg.CheckedAt = DateTimeOffset.UtcNow;
        await settings.SaveProvider(cfg);
        Assert.NotNull((await settings.GetProvider(cfg.Id)).LastCheck);
        await settings.SaveProvider(cfg, "new-test-key");
        Assert.Null((await settings.GetProvider(cfg.Id)).LastCheck);
        Assert.Null((await settings.GetProvider(cfg.Id)).CheckedAt);
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.SaveProvider(new ProviderConfig { Id = cfg.Id, Endpoint = "https://user:password@example.test/v1" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.SaveProvider(new ProviderConfig { Id = cfg.Id, Endpoint = "https://example.test/v1?api_key=secret" }));
    }

    [Fact]
    public void LegacyConfigurationAndProjectJsonRemainReadable()
    {
        var cfg = Json.Read<ProviderConfig>("{\"id\":\"openai-text\",\"model\":\"existing-model\",\"usdPerUnit\":1}");
        Assert.Null(cfg.SubscriptionReserveUsd);
        Assert.Equal("existing-model", cfg.Model);
        var p = ProjectFor("openai-text");
        Assert.Equal(p.Steps[Stage.Topic].ProviderId, Json.Read<Project>(Json.Write(p)).Steps[Stage.Topic].ProviderId);
        Assert.All(ProviderCatalog.All.Where(d => d.Protocol != TextProtocol.None), d =>
            Assert.Equal(new[] { Stage.Topic, Stage.Script, Stage.Title, Stage.Description, Stage.Tags }, d.Stages));
    }
}
