using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DeskAsk.Platform.Commerce;

/// <summary>
/// The website's text pages (About, Terms, Privacy, Cancellation and refunds, Delivery). Each starts as the draft built into the release
/// (Pages/*.md) and is edited in Vendor console › Website pages; an edited page is kept in commerce.settings ("page.&lt;slug&gt;").
/// </summary>
public static class Pages
{
    public static readonly (string Slug, string Title)[] All =
    {
        ("about", "About us"), ("terms", "Terms of Service"), ("privacy", "Privacy Policy"), ("refund", "Cancellation and Refund Policy"), ("delivery", "Delivery Policy"),
    };
    public sealed record Page(string Slug, string Title, string Markdown, bool Edited, DateTime? UpdatedAt);

    static string BuiltIn(string slug)
    {
        using var st = typeof(Pages).Assembly.GetManifestResourceStream("DeskAsk.Platform.Commerce.Pages." + slug + ".md");
        if (st == null) return "";
        using var r = new StreamReader(st);
        return r.ReadToEnd();
    }

    sealed record Stored(string Markdown, DateTime UpdatedAt);

    public static async Task<Page?> LoadAsync(CommerceOptions o, string slug, CancellationToken ct)
    {
        var (s, title) = All.FirstOrDefault(p => p.Slug == slug);
        if (s == null) return null;
        var raw = await CommerceDb.GetSettingAsync(o, "page." + slug, ct);
        Stored? st = null;
        if (raw != null) try { st = JsonSerializer.Deserialize<Stored>(raw); } catch (JsonException) { }
        return st is { Markdown.Length: > 0 } ? new Page(slug, title, st.Markdown, true, st.UpdatedAt) : new Page(slug, title, BuiltIn(slug), false, null);
    }

    static RouteHandlerBuilder Vendor(RouteHandlerBuilder b) => b.AddEndpointFilter(async (ctx, next) =>
    {
        var sp = ctx.HttpContext.RequestServices;
        return sp.GetRequiredService<CommerceOptions>().For(sp.GetRequiredService<ITenantContext>()) ? await next(ctx) : Results.NotFound(new { error = "not_found" });
    });

    public static void Map(IEndpointRouteBuilder r)
    {
        Vendor(r.MapGet("/api/store/pages/{slug}", async (string slug, CommerceOptions o, CancellationToken ct) =>
            await LoadAsync(o, slug, ct) is { } p ? Results.Ok(new { p.Slug, p.Title, p.Markdown, p.UpdatedAt }) : Results.NotFound(new { error = "not_found" })).AllowAnonymous());

        Vendor(r.MapGet("/api/commerce/pages", async (CommerceOptions o, CancellationToken ct) =>
        {
            var list = new List<Page>();
            foreach (var (slug, _) in All) list.Add((await LoadAsync(o, slug, ct))!);
            return Results.Ok(list);
        }).RequireAuthorization(p => p.RequireRole("admin")));

        Vendor(r.MapPut("/api/commerce/pages/{slug}", async (string slug, JsonElement b, CommerceOptions o, IAuditLog audit, CancellationToken ct) =>
        {
            if (All.All(p => p.Slug != slug)) return Results.NotFound(new { error = "not_found" });
            var md = b.ValueKind == JsonValueKind.Object && b.TryGetProperty("markdown", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()!.Trim() : null;
            if (md == null || md.Length > 100_000) return Results.BadRequest(new { error = "markdown", detail = "The page text is missing or longer than 100,000 characters." });
            // empty text goes back to the draft built into the release
            await CommerceDb.SetSettingAsync(o, "page." + slug, md == "" ? "{}" : JsonSerializer.Serialize(new Stored(md, DateTime.UtcNow)), ct);
            await audit.WriteAsync("commerce.page", slug, md == "" ? "reset to the built-in draft" : "edited", ct: ct);
            return Results.Ok(await LoadAsync(o, slug, ct));
        }).RequireAuthorization(p => p.RequireRole("admin")));
    }
}
