using System.Net;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DeskAsk.Platform.Identity;

/// <summary>
/// A one-time link for a person to choose their password (first administrator of a customer made by the store). The link carries the user id,
/// the customer id and a stamp of the current password hash, sealed with data protection for 72 hours; setting a password changes the stamp,
/// so the link works once.
/// </summary>
public sealed class FirstAdmin : IFirstAdmin
{
    const string Purpose = "identity.set-password";
    readonly IdentityDb db; readonly IPasswordHasher<UserAccount> hasher; readonly IDataProtectionProvider dp; readonly ITenantContext tenant;
    public FirstAdmin(IdentityDb db, IPasswordHasher<UserAccount> hasher, IDataProtectionProvider dp, ITenantContext tenant) { this.db = db; this.hasher = hasher; this.dp = dp; this.tenant = tenant; }

    public async Task<string> CreateAsync(string email, string name, CancellationToken ct = default)
    {
        email = email.Trim().ToLowerInvariant();
        var u = await db.Users.FirstOrDefaultAsync(x => x.Email == email, ct);
        if (u == null)
        {
            u = new UserAccount { Email = email, Name = name.Trim() == "" ? "Administrator" : name.Trim(), Role = "admin" };
            u.PasswordHash = hasher.HashPassword(u, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));   // nobody knows it
            db.Users.Add(u);
            await db.SaveChangesAsync(ct);
        }
        return "/account/set-password?t=" + Uri.EscapeDataString(Link(dp, tenant.Require().Id, u));
    }

    static string Stamp(UserAccount u) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(u.PasswordHash)))[..16];
    internal static string Link(IDataProtectionProvider dp, string tenantId, UserAccount u) =>
        dp.CreateProtector(Purpose).ToTimeLimitedDataProtector().Protect(tenantId + "|" + u.Id + "|" + Stamp(u), TimeSpan.FromHours(72));

    /// <summary>The user the link is for, or null when it is wrong, expired, used or for another customer.</summary>
    internal static async Task<UserAccount?> ReadAsync(IDataProtectionProvider dp, IdentityDb db, string tenantId, string? t, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(t) || t.Length > 2000) return null;
        string plain;
        try { plain = dp.CreateProtector(Purpose).ToTimeLimitedDataProtector().Unprotect(t); } catch (System.Security.Cryptography.CryptographicException) { return null; }
        var p = plain.Split('|');
        if (p.Length != 3 || p[0] != tenantId || !Guid.TryParse(p[1], out var id)) return null;
        var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id && x.Active, ct);
        return u != null && Stamp(u) == p[2] ? u : null;
    }

    public static void Map(IEndpointRouteBuilder r)
    {
        r.MapGet("/account/set-password", async (HttpContext http, string? t, IAntiforgery af, IdentityDb db, IDataProtectionProvider dp, ITenantContext tenant) =>
            await Page(http, af, t, await ReadAsync(dp, db, tenant.Require().Id, t, http.RequestAborted) == null ? "This link is not valid any more. It works once and for 72 hours. Ask for a new one." : ""))
            .AllowAnonymous();

        r.MapPost("/account/set-password", async (HttpContext http, IAntiforgery af, IdentityDb db, IDataProtectionProvider dp, ITenantContext tenant,
            IPasswordHasher<UserAccount> hasher, IAuditLog audit) =>
        {
            try { await af.ValidateRequestAsync(http); } catch (AntiforgeryValidationException) { return Results.BadRequest(new { error = "invalid_form" }); }
            var f = await http.Request.ReadFormAsync();
            var t = f["t"].ToString();
            var u = await ReadAsync(dp, db, tenant.Require().Id, t, http.RequestAborted);
            if (u == null) return await Page(http, af, t, "This link is not valid any more. It works once and for 72 hours. Ask for a new one.", 400);
            var pw = f["password"].ToString();
            if (pw.Length < 10) return await Page(http, af, t, "The password needs at least 10 characters.", 400);
            if (pw != f["confirm"].ToString()) return await Page(http, af, t, "The two passwords are not the same.", 400);
            u.PasswordHash = hasher.HashPassword(u, pw);
            await db.SaveChangesAsync(http.RequestAborted);
            await audit.WriteAsync("auth.password_set", target: u.Email, actor: u.Email);
            return Results.Redirect("/account/login?returnUrl=%2F");
        }).AllowAnonymous();
    }

    static async Task<IResult> Page(HttpContext http, IAntiforgery af, string? t, string error, int status = 200)
    {
        var tokens = af.GetAndStoreTokens(http);
        var brand = await BrandingApi.LoadAsync(http.RequestServices.GetRequiredService<ISettings>(), http.RequestAborted);
        var body = "<form method=\"post\" action=\"/account/set-password\">" + BrandingApi.LoginParts(brand).Mark + "<h2>Choose your password</h2>"
            + (error == "" ? "" : "<p class=\"e\" role=\"alert\">" + WebUtility.HtmlEncode(error) + "</p>")
            + "<input type=\"hidden\" name=\"" + tokens.FormFieldName + "\" value=\"" + WebUtility.HtmlEncode(tokens.RequestToken) + "\">"
            + "<input type=\"hidden\" name=\"t\" value=\"" + WebUtility.HtmlEncode(t ?? "") + "\">"
            + "<input name=\"password\" type=\"password\" placeholder=\"New password (10 characters or more)\" autocomplete=\"new-password\" minlength=\"10\" required>"
            + "<input name=\"confirm\" type=\"password\" placeholder=\"The same password again\" autocomplete=\"new-password\" minlength=\"10\" required>"
            + "<button type=\"submit\">Save and sign in</button>" + BrandingApi.Footer(brand) + "</form>";
        return Results.Content(IdentityModule.Html(body, brand), "text/html; charset=utf-8", Encoding.UTF8, status);
    }
}
