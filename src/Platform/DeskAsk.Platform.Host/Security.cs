using System.Threading.RateLimiting;

namespace DeskAsk.Platform;

/// <summary>Browser protections on every response, and a limit on how fast one address may try the places that can be reached without signing in.</summary>
static class HostSecurity
{
    // Only DeskAsk's own files are run; nothing is framed; styles may be inline because the pages set colours with style attributes.
    const string Csp = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; object-src 'none'";

    /// <summary>Which kind of limit a request falls under, or null for none.</summary>
    internal static string? Kind(string path, string method)
    {
        if (method != "POST" && method != "GET") return null;
        if (path.StartsWith("/account/", StringComparison.OrdinalIgnoreCase) && method == "POST") return "signin";
        if (path.Equals("/connect/token", StringComparison.OrdinalIgnoreCase)) return "signin";
        if (path.StartsWith("/api/public/", StringComparison.OrdinalIgnoreCase)) return "public";
        if (path.Equals("/agent/v1/enroll", StringComparison.OrdinalIgnoreCase) || path.Equals("/collector/v1/enroll", StringComparison.OrdinalIgnoreCase)) return "enroll";
        return null;
    }

    public static void Add(WebApplicationBuilder b)
    {
        var off = string.Equals(b.Configuration["Security:RateLimits"], "off", StringComparison.OrdinalIgnoreCase);
        var perMinute = Math.Clamp(b.Configuration.GetValue("Security:AuthPerMinute", 30), 1, 100000);
        b.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = async (ctx, ct) => { ctx.HttpContext.Response.Headers.RetryAfter = "60"; await ctx.HttpContext.Response.WriteAsJsonAsync(new { error = "too_many_requests", detail = "Too many requests from this address. Try again in a minute." }, ct); };
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            {
                var kind = off ? null : Kind(ctx.Request.Path.Value ?? "", ctx.Request.Method);
                if (kind == null) return RateLimitPartition.GetNoLimiter("none");
                var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "local";
                // many computers installing at once share an office address, so enrolment gets a wider allowance
                var limit = kind == "enroll" ? perMinute * 4 : perMinute;
                return RateLimitPartition.GetFixedWindowLimiter(kind + "|" + ip, _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
            });
        });
    }

    public static void Use(WebApplication app)
    {
        app.Use((ctx, next) =>
        {
            var h = ctx.Response.Headers;
            ctx.Response.OnStarting(() =>
            {
                h["X-Content-Type-Options"] = "nosniff";
                h["X-Frame-Options"] = "DENY";
                h["Referrer-Policy"] = "no-referrer";
                h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
                h["Cross-Origin-Opener-Policy"] = "same-origin";
                if (!h.ContainsKey("Content-Security-Policy")) h["Content-Security-Policy"] = Csp;
                if (ctx.Request.IsHttps) h["Strict-Transport-Security"] = "max-age=31536000";
                var p = ctx.Request.Path;
                if ((p.StartsWithSegments("/api") || p.StartsWithSegments("/connect") || p.StartsWithSegments("/account")) && !h.ContainsKey("Cache-Control")) h["Cache-Control"] = "no-store";
                return Task.CompletedTask;
            });
            return next(ctx);
        });
        app.UseRateLimiter();
    }
}
