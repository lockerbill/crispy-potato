using System.Net;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Studio.Components;
using Studio.Data;
using Studio.Domain;
using Studio.Providers;
using Studio.Services;

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrEmpty(builder.Configuration["urls"])) builder.WebHost.UseUrls("http://127.0.0.1:5080");
builder.Services.AddSingleton<StudioPaths>();
builder.Services.AddDataProtection().SetApplicationName("YouTubeVideoStudio");
builder.Services.AddOptions<KeyManagementOptions>().Configure<StudioPaths>((options, paths) => options.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(Path.Combine(paths.Root, "keys")), NullLoggerFactory.Instance));
builder.Services.AddDbContextFactory<StudioDb>((services, options) => options.UseSqlite($"Data Source={Path.Combine(services.GetRequiredService<StudioPaths>().Root, "studio.db")};Default Timeout=30"));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o => { o.LoginPath = "/login"; o.Cookie.Name = "studio.session"; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Lax; o.ExpireTimeSpan = TimeSpan.FromHours(12); o.SlidingExpiration = true; });
builder.Services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme).Configure<IConfiguration>((options, cfg) => options.Cookie.SecurePolicy = cfg["Studio:PublicUrl"]?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest);
builder.Services.AddAuthorization(); builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAntiforgery(); builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddRateLimiter(o => { o.RejectionStatusCode = 429; o.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 8, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })); });
builder.Services.AddHttpClient("providers", c => c.Timeout = TimeSpan.FromMinutes(10)).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<SettingsService>(); builder.Services.AddSingleton<AdminAuth>(); builder.Services.AddSingleton<AssetStore>(); builder.Services.AddSingleton<ProcessRunner>(); builder.Services.AddSingleton<MediaEngine>(); builder.Services.AddSingleton<ProviderHttp>();
builder.Services.AddSingleton<IStageProvider, TextProvider>(); builder.Services.AddSingleton<IStageProvider, VoiceProvider>(); builder.Services.AddSingleton<IStageProvider, ImageProvider>(); builder.Services.AddSingleton<IStageProvider, CaptionProvider>(); builder.Services.AddSingleton<IStageProvider, RenderProvider>();
builder.Services.AddSingleton<ProviderRegistry>(); builder.Services.AddSingleton<ProjectService>(); builder.Services.AddSingleton<JobSignals>(); builder.Services.AddSingleton<YouTubeService>();
builder.Services.AddScoped<UiState>();
builder.Services.AddHostedService<PipelineWorker>();
var app = builder.Build();
await using (var db = await app.Services.GetRequiredService<IDbContextFactory<StudioDb>>().CreateDbContextAsync())
{
    await db.Database.EnsureCreatedAsync(); await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    await db.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS SchemaVersion (Version INTEGER NOT NULL);");
    await db.Database.ExecuteSqlRawAsync("INSERT INTO SchemaVersion (Version) SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM SchemaVersion);");
    if (await db.Database.SqlQueryRaw<int>("SELECT Version AS Value FROM SchemaVersion").SingleAsync() != 1) throw new InvalidOperationException("Database schema version not supported. Restore a compatible backup or apply a reviewed migration.");
}
await app.Services.GetRequiredService<AdminAuth>().Initialize(args.Contains("--reset-admin"));
if (args.Contains("--reset-admin")) return;
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/error");
app.Use(async (ctx, next) => { ctx.Response.Headers["X-Content-Type-Options"] = "nosniff"; ctx.Response.Headers["Referrer-Policy"] = "same-origin"; ctx.Response.Headers["X-Frame-Options"] = "DENY"; await next(); });
app.UseStaticFiles(); app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter(); app.UseAntiforgery();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/error", () => Results.Problem("Studio gặp lỗi. Kiểm tra cấu hình hoặc tác vụ; thông tin nhạy cảm không được đưa vào phản hồi."));
app.MapGet("/login", async (HttpContext context, IAntiforgery anti, SettingsService settings) =>
{
    if (context.User.Identity?.IsAuthenticated == true) return Results.Redirect("/");
    var tokens = anti.GetAndStoreTokens(context); var configured = await settings.Get<AdminCredential>("admin") is not null;
    var error = context.Request.Query.ContainsKey("failed") ? "<p class='error'>Mật khẩu chưa đúng / Incorrect password.</p>" : "";
    var form = configured ? $"<form method='post' action='/login'><input type='hidden' name='{tokens.FormFieldName}' value='{WebUtility.HtmlEncode(tokens.RequestToken)}'><label>Mật khẩu / Password<input name='password' type='password' autocomplete='current-password' required autofocus minlength='12'></label>{error}<button class='primary' type='submit'>Mở Studio →</button></form>" : "<p>Chưa thiết lập tài khoản quản trị.</p><p>Cấu hình <code>STUDIO_ADMIN_PASSWORD</code> hoặc <code>STUDIO_ADMIN_PASSWORD_FILE</code> (ít nhất 12 ký tự), rồi khởi động lại. Xem hướng dẫn cài đặt trong README.</p>";
    return Results.Content("<!doctype html><html lang='vi'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>Frame Studio · Sign in</title><link rel='stylesheet' href='/app.css'></head><body class='login-page'><main class='login-card'><div class='brand-mark'>F</div><p class='eyebrow'>YOUR NEXT STORY STARTS HERE</p><h1>Frame Studio</h1><p class='muted'>Một ý tưởng. Từng khung hình. Câu chuyện của anh.</p>" + form + "</main></body></html>", "text/html; charset=utf-8");
});
app.MapPost("/login", async (HttpContext context, IAntiforgery anti, AdminAuth auth) =>
{
    try { await anti.ValidateRequestAsync(context); } catch (AntiforgeryValidationException) { return Results.BadRequest(); }
    var form = await context.Request.ReadFormAsync();
    if (!await auth.Verify(form["password"].ToString())) return Results.Redirect("/login?failed=1");
    await context.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "admin"), new Claim(ClaimTypes.Name, "Studio owner")], CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.Redirect("/");
}).RequireRateLimiting("login");
app.MapPost("/logout", async (HttpContext context, IAntiforgery anti) => { await anti.ValidateRequestAsync(context); await context.SignOutAsync(); return Results.Redirect("/login"); }).RequireAuthorization();
app.MapGet("/media/{id}", async (string id, bool? download, AssetStore store) =>
{
    try { var a = await store.Get(id); return Results.File(a.Path, a.ContentType, download == true ? a.FileName : null, enableRangeProcessing: true); }
    catch (InvalidOperationException) { return Results.NotFound(); }
}).RequireAuthorization();
app.MapGet("/projects/{id:guid}/export/{kind}", async (Guid id, string kind, ProjectService projects) =>
{
    var p = await projects.Get(id);
    var (content, type, name) = kind switch
    {
        "srt" => (CaptionFiles.Srt(p.Captions), "application/x-subrip", "captions.srt"),
        "vtt" => (CaptionFiles.Vtt(p.Captions), "text/vtt", "captions.vtt"),
        "script" => (string.Join("\n\n", p.Scenes.Select((s, i) => $"{i + 1}. {s.Narration}")), "text/plain", "script.txt"),
        "metadata" => (Json.Write(new { p.Title, p.Description, p.Tags, p.Language, p.Format, credits = p.Scenes.Select(s => s.Attribution).Where(s => s is not null).Distinct() }), "application/json", "metadata.json"),
        _ => ("", "text/plain", "")
    };
    return name.Length == 0 ? Results.NotFound() : Results.File(Encoding.UTF8.GetBytes(content), type, name);
}).RequireAuthorization();
app.MapGet("/youtube/connect", async (HttpContext context, YouTubeService yt) =>
{
    try { var (url, state) = await yt.Begin(); context.Response.Cookies.Append("studio.oauth", state, new CookieOptions { HttpOnly = true, Secure = yt.RedirectUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || context.Request.IsHttps, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromMinutes(10), Path = "/youtube/callback" }); return Results.Redirect(url); }
    catch (InvalidOperationException) { return Results.Redirect("/settings?youtube=config-required"); }
}).RequireAuthorization();
app.MapGet("/youtube/callback", async (HttpContext context, YouTubeService yt) =>
{
    var state = context.Request.Query["state"].ToString(); var cookie = context.Request.Cookies["studio.oauth"];
    context.Response.Cookies.Delete("studio.oauth", new CookieOptions { Path = "/youtube/callback" });
    if (string.IsNullOrEmpty(cookie) || state.Length != 64 || cookie != state || !context.Request.Query.ContainsKey("code")) return Results.Redirect("/settings?youtube=oauth-failed");
    try { await yt.Complete(state, context.Request.Query["code"].ToString(), context.RequestAborted); return Results.Redirect("/settings?youtube=connected"); }
    catch (Exception e) when (e is InvalidOperationException or ProviderException) { return Results.Redirect("/settings?youtube=oauth-failed"); }
}).RequireAuthorization();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
await app.RunAsync();
public partial class Program { }
