using System.Security.Cryptography;
using Microsoft.AspNetCore.Antiforgery;
using Studio.Providers;

namespace Studio.Services;

public static class ProviderEndpoints
{
    private const string BindingCookie = "studio.chatgpt.oauth";

    public static void MapProviderEndpoints(this WebApplication app)
    {
        app.MapPost("/providers/chatgpt-subscription/connect", async (HttpContext context, IAntiforgery anti, ISubscriptionAuth auth) =>
        {
            try { await anti.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            var redirect = new Uri(auth.ChatGptRedirectUri);
            if (context.Request.Host.Host != "127.0.0.1" || (context.Request.Host.Port ?? 80) != redirect.Port || context.Request.Scheme != "http")
                return Results.Redirect(redirect.GetLeftPart(UriPartial.Authority) + "/settings?provider=chatgpt-subscription&connection=local-required");
            var binding = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            try
            {
                var start = await auth.Begin("chatgpt-subscription", binding, context.RequestAborted);
                context.Response.Cookies.Append(BindingCookie, binding, new CookieOptions
                {
                    HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = false,
                    Path = "/auth/callback", MaxAge = TimeSpan.FromMinutes(10)
                });
                context.Response.Headers.CacheControl = "no-store";
                return Results.Redirect(start.AuthorizationUrl!);
            }
            catch (Exception e) when (e is InvalidOperationException or ProviderException or HttpRequestException)
            { return Results.Redirect("/settings?provider=chatgpt-subscription&connection=failed"); }
        }).RequireAuthorization();

        app.MapGet("/auth/callback", async (HttpContext context, ISubscriptionAuth auth) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            var binding = context.Request.Cookies[BindingCookie];
            context.Response.Cookies.Delete(BindingCookie, new CookieOptions { Path = "/auth/callback" });
            const string failure = "/settings?provider=chatgpt-subscription&connection=failed";
            if (string.IsNullOrWhiteSpace(binding) || context.Request.Query.ContainsKey("error"))
                return Results.Redirect(failure);
            try
            {
                await auth.CompleteChatGpt(context.Request.Query["state"].ToString(), context.Request.Query["code"].ToString(),
                    context.Request.Query["client_id"].ToString(), binding, context.RequestAborted);
                return Results.Redirect("/settings?provider=chatgpt-subscription&connection=connected");
            }
            catch (Exception e) when (e is InvalidOperationException or ProviderException or HttpRequestException or OperationCanceledException)
            { return Results.Redirect(failure); }
        }).RequireAuthorization();
    }
}
