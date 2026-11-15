namespace Erp.Api.Infrastructure;

/// <summary>
/// Hardening headers on every API response. The API only returns JSON, so the CSP is "deny everything":
/// nothing on an API response should ever load scripts or be framed (clickjacking).
/// The SPA document gets its own, looser CSP from the static host (render.yaml / nginx.spa.conf).
/// </summary>
public static class SecurityHeaders
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, bool isDevelopment) =>
        app.Use((ctx, next) =>
        {
            ctx.Response.OnStarting(() =>
            {
                var h = ctx.Response.Headers;
                h.XContentTypeOptions = "nosniff";
                h.XFrameOptions = "DENY";
                h["Referrer-Policy"] = "no-referrer";
                h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
                h["Cross-Origin-Opener-Policy"] = "same-origin";
                h["Cross-Origin-Resource-Policy"] = "same-origin";
                // Swagger UI (Development only) needs scripts/styles; everything else is JSON.
                if (!(isDevelopment && ctx.Request.Path.StartsWithSegments("/swagger")))
                    h.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
                return Task.CompletedTask;
            });
            return next(ctx);
        });
}
