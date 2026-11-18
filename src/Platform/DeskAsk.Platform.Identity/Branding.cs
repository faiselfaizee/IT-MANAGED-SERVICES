using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DeskAsk.Platform.Identity;

/// <summary>What a customer's installation looks like: name, logo, header and footer text, colours and theme. Read by anyone (the sign-in page needs it), changed by administrators.</summary>
public sealed record Brand(string Name, string HeaderText, string FooterText, string Accent, string Nav, string Theme, bool HasLogo, long LogoVersion, string Accent2 = "")
{
    public static readonly Brand Default = new("DeskAsk", "", "", "", "", "auto", false, 0);
}

public static class BrandingApi
{
    const string Key = "branding", LogoKey = "branding.logo";
    const int MaxLogo = 256 * 1024;
    static readonly Regex Hex = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);
    static readonly string[] Themes = { "auto", "light", "dark" };
    static readonly Dictionary<string, byte[][]> Magic = new()
    {
        ["image/png"] = new[] { new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A } },
        ["image/jpeg"] = new[] { new byte[] { 0xFF, 0xD8, 0xFF } },
        ["image/gif"] = new[] { Encoding.ASCII.GetBytes("GIF87a"), Encoding.ASCII.GetBytes("GIF89a") },
        ["image/webp"] = new[] { Encoding.ASCII.GetBytes("RIFF") },
    };
    // Things that make an SVG do more than draw: scripts, embedded pages, outside addresses, event handlers.
    static readonly string[] SvgBad = { "<script", "<foreignobject", "<iframe", "<object", "<embed", "<!entity", "<!doctype", "javascript:", "<animate", "<set ", "@import", "href=\"http", "href='http", "url(http", "url(\"http", "url('http", "<audio", "<video" };

    sealed record Stored(string? Name, string? HeaderText, string? FooterText, string? Accent, string? Nav, string? Theme, long LogoVersion, string? Accent2 = null);
    sealed record Logo(string Type, string Data);
    sealed record Update(string? Name, string? HeaderText, string? FooterText, string? Accent, string? Nav, string? Theme, string? Accent2 = null);

    public static async Task<Brand> LoadAsync(ISettings s, CancellationToken ct)
    {
        try
        {
            var raw = await s.GetAsync(Key, ct); if (string.IsNullOrWhiteSpace(raw)) return Brand.Default;
            var o = JsonSerializer.Deserialize<Stored>(raw, J) ?? new Stored(null, null, null, null, null, null, 0);
            var name = Clean(o.Name, 60); if (name == "") name = "DeskAsk";
            var theme = Themes.Contains(o.Theme ?? "") ? o.Theme! : "auto";
            return new Brand(name, Clean(o.HeaderText, 120), Clean(o.FooterText, 300), Colour(o.Accent), Colour(o.Nav), theme, o.LogoVersion > 0, o.LogoVersion, Colour(o.Accent2));
        }
        catch { return Brand.Default; }   // a damaged setting must never stop the sign-in page
    }

    static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);
    static string Clean(string? s, int max) { s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim(); return s.Length > max ? s[..max] : s; }
    static string Colour(string? c) => c != null && Hex.IsMatch(c.Trim()) ? c.Trim().ToLowerInvariant() : "";

    /// <summary>Why this SVG may not be used, or null.</summary>
    internal static string? CheckSvg(byte[] b)
    {
        string t; try { t = new UTF8Encoding(false, true).GetString(b); } catch { return "The SVG is not valid text."; }
        var l = t.ToLowerInvariant();
        if (!l.Contains("<svg")) return "This is not an SVG picture.";
        foreach (var bad in SvgBad) if (l.Contains(bad)) return "The SVG contains " + bad.Trim('<', ' ', '"', '\'', '=') + ", which is not allowed in a logo. Export a plain SVG, or use a PNG.";
        if (Regex.IsMatch(l, @"[\s""'/]on[a-z]+\s*=")) return "The SVG contains event handlers, which are not allowed in a logo.";
        return null;
    }

    public static string? CheckLogo(string type, byte[] b)
    {
        if (b.Length == 0) return "The picture is empty.";
        if (b.Length > MaxLogo) return "The logo is larger than 256 KB. Use a smaller picture.";
        if (type == "image/svg+xml") return CheckSvg(b);
        if (!Magic.TryGetValue(type, out var sigs)) return "Use a PNG, JPEG, WebP, GIF or SVG picture.";
        if (!sigs.Any(s => b.Length >= s.Length && b.AsSpan(0, s.Length).SequenceEqual(s))) return "The file is not really a " + type[6..] + " picture.";
        if (type == "image/webp" && (b.Length < 12 || Encoding.ASCII.GetString(b, 8, 4) != "WEBP")) return "The file is not really a webp picture.";
        return null;
    }

    public static void Map(IEndpointRouteBuilder r)
    {
        r.MapGet("/api/branding", async (ISettings s, CancellationToken ct) => Results.Ok(await LoadAsync(s, ct))).AllowAnonymous();

        r.MapGet("/api/branding/logo", async (ISettings s, HttpContext http, CancellationToken ct) =>
        {
            var raw = await s.GetAsync(LogoKey, ct); if (string.IsNullOrWhiteSpace(raw)) return Results.NotFound();
            Logo? l; try { l = JsonSerializer.Deserialize<Logo>(raw, J); } catch { return Results.NotFound(); }
            if (l == null || l.Data == "") return Results.NotFound();
            http.Response.Headers.CacheControl = "public, max-age=86400";
            // the picture is only ever drawn by an <img>; if someone opens its address directly nothing in it can run
            http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
            return Results.File(Convert.FromBase64String(l.Data), l.Type);
        }).AllowAnonymous();

        var g = r.MapGroup("/api/branding").RequireAuthorization(p => p.RequireRole("admin"));

        g.MapPut("", async (Update b, ISettings s, IAuditLog audit, CancellationToken ct) =>
        {
            if (b.Accent is { Length: > 0 } a && !Hex.IsMatch(a.Trim())) return Results.BadRequest(new { error = "bad_colour", detail = "The accent colour must be like #0d8a7f." });
            if (b.Accent2 is { Length: > 0 } a2 && !Hex.IsMatch(a2.Trim())) return Results.BadRequest(new { error = "bad_colour", detail = "The second colour must be like #c31528." });
            if (b.Nav is { Length: > 0 } n && !Hex.IsMatch(n.Trim())) return Results.BadRequest(new { error = "bad_colour", detail = "The menu colour must be like #0f1c2e." });
            if (b.Theme is { Length: > 0 } t && !Themes.Contains(t)) return Results.BadRequest(new { error = "bad_theme", detail = "The theme must be auto, light or dark." });
            var cur = await LoadAsync(s, ct);
            var next = new Stored(Clean(b.Name, 60), Clean(b.HeaderText, 120), Clean(b.FooterText, 300), Colour(b.Accent), Colour(b.Nav), string.IsNullOrEmpty(b.Theme) ? "auto" : b.Theme, cur.LogoVersion, Colour(b.Accent2));
            await s.SetAsync(Key, JsonSerializer.Serialize(next, J), ct);
            await audit.WriteAsync("branding.updated", detail: $"name={next.Name}; accent={next.Accent}; accent2={next.Accent2}; nav={next.Nav}; theme={next.Theme}", ct: ct);
            return Results.Ok(await LoadAsync(s, ct));
        });

        // the picture is sent as the request body with its type (image/png, image/jpeg, image/webp, image/gif or image/svg+xml)
        g.MapPut("/logo", async (HttpContext http, ISettings s, IAuditLog audit, CancellationToken ct) =>
        {
            var type = (http.Request.ContentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
            using var ms = new MemoryStream(); var buf = new byte[16384]; int n;
            while ((n = await http.Request.Body.ReadAsync(buf, ct)) > 0) { ms.Write(buf, 0, n); if (ms.Length > MaxLogo) return Results.BadRequest(new { error = "too_large", detail = "The logo is larger than 256 KB. Use a smaller picture." }); }
            var bytes = ms.ToArray();
            var why = CheckLogo(type, bytes); if (why != null) return Results.BadRequest(new { error = "bad_logo", detail = why });
            await s.SetAsync(LogoKey, JsonSerializer.Serialize(new Logo(type, Convert.ToBase64String(bytes)), J), ct);
            await SetVersion(s, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ct);
            await audit.WriteAsync("branding.logo_changed", detail: type + ", " + bytes.Length + " bytes", ct: ct);
            return Results.Ok(await LoadAsync(s, ct));
        });

        g.MapDelete("/logo", async (ISettings s, IAuditLog audit, CancellationToken ct) =>
        {
            await s.DeleteAsync(LogoKey, ct); await SetVersion(s, 0, ct);
            await audit.WriteAsync("branding.logo_removed", ct: ct);
            return Results.Ok(await LoadAsync(s, ct));
        });

        g.MapPost("/reset", async (ISettings s, IAuditLog audit, CancellationToken ct) =>
        {
            await s.DeleteAsync(Key, ct); await s.DeleteAsync(LogoKey, ct);
            await audit.WriteAsync("branding.reset", ct: ct);
            return Results.Ok(Brand.Default);
        });
    }

    static async Task SetVersion(ISettings s, long v, CancellationToken ct)
    {
        var cur = await LoadAsync(s, ct);
        await s.SetAsync(Key, JsonSerializer.Serialize(new Stored(cur.Name, cur.HeaderText, cur.FooterText, cur.Accent, cur.Nav, cur.Theme, v, cur.Accent2), J), ct);
    }

    // ---- the server-made pages (sign-in, two-step) ----

    static readonly Regex Hx = new("^#[0-9a-f]{6}$");
    static (int r, int g, int b) Rgb(string h) => (Convert.ToInt32(h.Substring(1, 2), 16), Convert.ToInt32(h.Substring(3, 2), 16), Convert.ToInt32(h.Substring(5, 2), 16));
    /// <summary>Black or white text, whichever reads better on this colour.</summary>
    public static string InkOn(string hex) { var (r, g, b) = Rgb(hex); return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255 > 0.58 ? "#101820" : "#ffffff"; }

    /// <summary>The style lines that bring a brand to the sign-in pages, and the heading for them.</summary>
    internal static (string Css, string Mark) LoginParts(Brand b)
    {
        var css = new StringBuilder();
        if (Hx.IsMatch(b.Accent))
        {
            var (r, g, bl) = Rgb(b.Accent); var ink = InkOn(b.Accent);
            css.Append($"input:focus{{border-color:{b.Accent};box-shadow:0 0 0 3px rgba({r},{g},{bl},.3)}}button{{color:{ink};background:{b.Accent};box-shadow:0 10px 26px -8px rgba({r},{g},{bl},.6)}}.sso:hover{{border-color:{b.Accent}}}");
            css.Append($":root{{--a:rgba({r},{g},{bl},.42);--b:rgba({r},{g},{bl},.22);--c:rgba({r},{g},{bl},.3)}}");
        }
        if (b.Theme == "light") css.Append(":root{--ink:#0f1b2d;--muted:#4c5d75;--base:#e8f0fb;--glass:rgba(255,255,255,.62);--edge:rgba(255,255,255,.9);--line:rgba(30,60,120,.22);--field:rgba(255,255,255,.75);--bad:#b3261e;color-scheme:light}");
        if (b.Theme == "dark") css.Append(":root{--ink:#eaf1fb;--muted:#a8b7cc;--base:#0a1322;--glass:rgba(255,255,255,.08);--edge:rgba(255,255,255,.18);--line:rgba(255,255,255,.22);--field:rgba(6,12,24,.4);--bad:#ff8a8a;color-scheme:dark}");
        // the brand stripe across the top of the sign-in box
        var s1 = Hx.IsMatch(b.Accent) ? b.Accent : "#2fe0cf"; var s2 = Hx.IsMatch(b.Accent2) ? b.Accent2 : "#5b6bff";
        css.Append($"form{{position:relative;overflow:hidden}}form::before{{content:'';position:absolute;left:0;right:0;top:0;height:5px;background:linear-gradient(90deg,{s1} 0 72%,{s2} 72% 100%)}}");
        css.Append(".lg{display:block;max-width:100%;max-height:56px;margin:0 0 14px;object-fit:contain}.ft{margin:18px 0 0;color:var(--muted);font-size:12.5px;text-align:center}");
        var name = WebUtility.HtmlEncode(b.Name);
        var mark = b.HasLogo ? $"<img class=\"lg\" src=\"/api/branding/logo?v={b.LogoVersion}\" alt=\"{name}\">" + $"<div class=\"m\" style=\"margin-bottom:10px\">{name}</div>"
                              : $"<div class=\"m\"><i{(Hx.IsMatch(b.Accent) ? $" style=\"background:{b.Accent}\"" : "")}></i>{name}</div>";
        return (css.ToString(), mark);
    }

    internal static string Footer(Brand b) => b.FooterText == "" ? "" : "<p class=\"ft\">" + WebUtility.HtmlEncode(b.FooterText) + "</p>";
}
