using FacelessStudio.Core.Abstractions;
using FacelessStudio.Core.Jobs;
using FacelessStudio.Core.Pipeline;
using FacelessStudio.Core.Providers;
using FacelessStudio.Core.Settings;
using FacelessStudio.Providers;
using FacelessStudio.Web;
using FacelessStudio.Web.Components;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

// Thư mục làm việc chứa data/ (cài đặt, model, cache) và output/ (video đã tạo).
var home = AppPaths.ResolveHome(builder.Configuration["FACELESS_HOME"], builder.Environment.ContentRootPath);
var outputDir = Path.Combine(home, "output");

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddFacelessProviders();
builder.Services.AddSingleton(new SettingsStore(home));
builder.Services.AddSingleton(sp => new ProviderRegistry(sp.GetServices<IStepProvider>()));
builder.Services.AddSingleton(sp => new JobManager(sp.GetRequiredService<ProviderRegistry>(), outputDir));
builder.Services.AddSingleton<VideoPipeline>();
builder.Services.AddSingleton<StudioService>();
builder.Services.AddSingleton<SystemChecks>();
builder.Services.AddHostedService<JobRunner>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// Phục vụ video, thumbnail, phụ đề đã tạo tại /files/<thư mục job>/...
var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".srt"] = "text/plain; charset=utf-8";
contentTypes.Mappings[".ass"] = "text/plain; charset=utf-8";
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(outputDir),
    RequestPath = "/files",
    ContentTypeProvider = contentTypes,
});

app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Logger.LogInformation("Faceless Studio: thư mục làm việc {Home}", home);
app.Run();
