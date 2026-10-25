using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Studio.Services;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace Studio.Tests;

public sealed class TestHost : WebApplicationFactory<Program>
{
    public Action<IServiceCollection>? ConfigureTestServices { get; init; }
    public bool RunWorker { get; init; }
    public string DataPath { get; } = Path.Combine(Path.GetTempPath(), "frame-tests-" + Guid.NewGuid().ToString("N"));
    public const string Password = "Only-for-automated-tests-2026!";
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Studio:DataPath"] = DataPath, ["Studio:DisableWorker"] = (!RunWorker).ToString(), ["STUDIO_ADMIN_PASSWORD"] = Password,
            ["AllowedHosts"] = "*", ["Studio:PublicUrl"] = "http://localhost:5080"
        }));
        builder.ConfigureServices(services => ConfigureTestServices?.Invoke(services));
    }
    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();
    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (Directory.Exists(DataPath)) Directory.Delete(DataPath, true); }
}
