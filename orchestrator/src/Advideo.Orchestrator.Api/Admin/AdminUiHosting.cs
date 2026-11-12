using System.Security.Claims;
using Microsoft.Extensions.FileProviders;

namespace Advideo.Orchestrator.Api.Admin;

/// <summary>
/// Admin UI support: <c>GET /auth/me</c> (who is the API key) and static hosting of the Angular SPA
/// under <c>/admin</c>. The SPA is built into <c>wwwroot/admin</c> by the Docker image; locally it runs
/// on <c>ng serve</c> with a proxy, so the folder may be missing (then nothing is mapped).
/// The SPA talks to the API on the same origin with the X-Api-Key header, so no CORS is needed.
/// </summary>
public static class AdminUiHosting
{
    public const string BasePath = "/admin";

    public static void MapAuthMe(this IEndpointRouteBuilder app) =>
        app.MapGet("/auth/me", (ClaimsPrincipal user) => Results.Ok(new AuthMeResponse(
                user.Identity?.Name ?? "",
                user.FindAll(ClaimTypes.Role).Select(c => c.Value).Distinct().ToList())))
            .WithTags("Auth")
            .RequireAuthorization(Security.ApiKeyDefaults.ReportingPolicy);

    /// <summary>Serves wwwroot/admin (index.html + hashed assets) with an SPA fallback. Returns false when not built.</summary>
    public static bool UseAdminUi(this WebApplication app)
    {
        var root = Path.Combine(app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot"), "admin");
        if (!File.Exists(Path.Combine(root, "index.html"))) return false;

        var files = new PhysicalFileProvider(root);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files, RequestPath = BasePath });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            RequestPath = BasePath,
            OnPrepareResponse = ctx =>
            {
                ApplySecurityHeaders(ctx.Context.Response.Headers);
                // index.html must never be cached (it points to the hashed bundles); bundles are immutable
                ctx.Context.Response.Headers.CacheControl = ctx.File.Name == "index.html"
                    ? "no-cache, no-store, must-revalidate"
                    : "public, max-age=31536000, immutable";
            }
        });

        // SPA fallback as middleware (not an endpoint: a matched endpoint would stop the static file
        // middleware from serving the hashed bundles). Client routes like /admin/campaigns/x -> index.html.
        var index = Path.Combine(root, "index.html");
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path;
            if (!HttpMethods.IsGet(ctx.Request.Method) || !path.StartsWithSegments(BasePath, out var rest))
            {
                await next();
                return;
            }
            if (!rest.HasValue) { ctx.Response.Redirect(BasePath + "/"); return; }
            if (Path.HasExtension(rest.Value)) { await next(); return; } // missing asset -> normal 404
            ApplySecurityHeaders(ctx.Response.Headers);
            ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.SendFileAsync(index);
        });
        return true;
    }

    private static void ApplySecurityHeaders(IHeaderDictionary h)
    {
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        // Angular needs inline styles; scripts only from self. API calls go to the same origin.
        h["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: https:; " +
            "font-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    }
}

public sealed record AuthMeResponse(string Name, IReadOnlyList<string> Roles);
