using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DeskAsk.Platform.Identity;

public interface ITotpClock { DateTimeOffset Now { get; } }
public sealed class SystemTotpClock : ITotpClock { public DateTimeOffset Now => DateTimeOffset.UtcNow; }

/// <summary>RFC 6238 time-based one-time passwords (HMAC-SHA1, 30 s steps, 6 digits), compatible with every authenticator app.</summary>
public static class Totp
{
    const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Base32(byte[] data)
    {
        var sb = new StringBuilder(); int buffer = 0, bits = 0;
        foreach (var b in data) { buffer = (buffer << 8) | b; bits += 8; while (bits >= 5) { sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; } }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] FromBase32(string s)
    {
        var o = new List<byte>(); int buffer = 0, bits = 0;
        foreach (var ch in s.Trim().TrimEnd('=').ToUpperInvariant().Replace(" ", ""))
        {
            var v = Alphabet.IndexOf(ch); if (v < 0) throw new FormatException("Not base32.");
            buffer = (buffer << 5) | v; bits += 5;
            if (bits >= 8) { o.Add((byte)((buffer >> (bits - 8)) & 255)); bits -= 8; }
        }
        return o.ToArray();
    }

    public static string NewSecret() => Base32(RandomNumberGenerator.GetBytes(20));
    public static long Step(DateTimeOffset t) => t.ToUnixTimeSeconds() / 30;

    public static string Code(byte[] key, long step, int digits = 6)
    {
        var msg = BitConverter.GetBytes(step); if (BitConverter.IsLittleEndian) Array.Reverse(msg);
        var h = HMACSHA1.HashData(key, msg);
        var o = h[^1] & 0xf;
        var bin = ((h[o] & 0x7f) << 24) | (h[o + 1] << 16) | (h[o + 2] << 8) | h[o + 3];
        return (bin % (int)Math.Pow(10, digits)).ToString().PadLeft(digits, '0');
    }

    /// <summary>Accepts the previous, current and next step (clock drift), but never a step at or before lastStep (a code works once).</summary>
    public static bool TryVerify(string secretBase32, string? code, DateTimeOffset now, long lastStep, out long usedStep)
    {
        usedStep = 0;
        var c = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (c.Length != 6) return false;
        var key = FromBase32(secretBase32); var cur = Step(now);
        for (var s = cur - 1; s <= cur + 1; s++)
        {
            if (s <= lastStep) continue;
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(key, s)), Encoding.ASCII.GetBytes(c))) { usedStep = s; return true; }
        }
        return false;
    }

    public static string Uri(string issuer, string account, string secret) =>
        $"otpauth://totp/{System.Uri.EscapeDataString(issuer)}:{System.Uri.EscapeDataString(account)}?secret={secret}&issuer={System.Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits=6&period=30";
}

public enum StepResult { Bad, Totp, Recovery }

public sealed class TwoStepService
{
    readonly IdentityDb db; readonly ITotpClock clock; readonly IConfiguration cfg; readonly ITenantContext tenant; readonly IAuditLog audit; readonly IPasswordHasher<UserAccount> hasher;
    public TwoStepService(IdentityDb db, ITotpClock clock, IConfiguration cfg, ITenantContext tenant, IAuditLog audit, IPasswordHasher<UserAccount> hasher)
    { this.db = db; this.clock = clock; this.cfg = cfg; this.tenant = tenant; this.audit = audit; this.hasher = hasher; }

    // The secret is stored sealed with AES-GCM under a key made from Identity:EncryptionSecret, so a copy of the database alone is not enough.
    byte[] Key => SHA256.HashData(Encoding.UTF8.GetBytes(cfg["Identity:EncryptionSecret"] ?? "development-only-secret"));
    string Seal(string plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12); var data = Encoding.UTF8.GetBytes(plain); var ct = new byte[data.Length]; var tag = new byte[16];
        using var g = new AesGcm(Key, 16); g.Encrypt(nonce, data, ct, tag);
        return Convert.ToBase64String(nonce.Concat(tag).Concat(ct).ToArray());
    }
    string Open(string sealedText)
    {
        var all = Convert.FromBase64String(sealedText); var pt = new byte[all.Length - 28];
        using var g = new AesGcm(Key, 16); g.Decrypt(all[..12], all[28..], all[12..28], pt);
        return Encoding.UTF8.GetString(pt);
    }

    static string Norm(string c) => new string(c.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    static string RHash(string c) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Norm(c)))).ToLowerInvariant();
    static string[] NewRecovery() => Enumerable.Range(0, 10).Select(_ => { var s = Totp.Base32(RandomNumberGenerator.GetBytes(7))[..10].ToLowerInvariant(); return s[..5] + "-" + s[5..]; }).ToArray();

    public async Task<(bool enabled, int recoveryLeft)?> StatusAsync(Guid id, CancellationToken ct)
    {
        var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return u == null ? null : (u.TotpEnabled, JsonSerializer.Deserialize<string[]>(u.RecoveryHashes)?.Length ?? 0);
    }

    public async Task<(string secret, string uri)?> BeginSetupAsync(Guid id, CancellationToken ct)
    {
        var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id && x.Active, ct);
        if (u == null || u.TotpEnabled) return null;
        var secret = Totp.NewSecret();
        u.TotpSecret = Seal(secret); u.TotpLastStep = 0;
        await db.SaveChangesAsync(ct);
        return (secret, Totp.Uri(tenant.Require().Name, u.Email, secret));
    }

    public async Task<string[]?> EnableAsync(Guid id, string code, CancellationToken ct)
    {
        var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id && x.Active, ct);
        if (u == null || u.TotpEnabled || u.TotpSecret == null) return null;
        if (!Totp.TryVerify(Open(u.TotpSecret), code, clock.Now, u.TotpLastStep, out var step)) { await audit.WriteAsync("auth.2fa_failed", u.Email, "while switching on", actor: u.Email, ct: ct); return null; }
        var codes = NewRecovery();
        u.TotpLastStep = step; u.TotpEnabled = true; u.RecoveryHashes = JsonSerializer.Serialize(codes.Select(RHash));
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("auth.2fa_enabled", u.Email, actor: u.Email, ct: ct);
        return codes;
    }

    /// <summary>Checks an authenticator code, or else one recovery code (which is then used up).</summary>
    public async Task<StepResult> VerifyAsync(Guid id, string code, CancellationToken ct)
    {
        var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id && x.Active, ct);
        if (u == null || !u.TotpEnabled || u.TotpSecret == null) return StepResult.Bad;
        if (Totp.TryVerify(Open(u.TotpSecret), code, clock.Now, u.TotpLastStep, out var step))
        {
            u.TotpLastStep = step; await db.SaveChangesAsync(ct); return StepResult.Totp;
        }
        var left = JsonSerializer.Deserialize<List<string>>(u.RecoveryHashes) ?? new();
        var h = RHash(code ?? "");
        if (Norm(code ?? "").Length >= 8 && left.Remove(h))
        {
            u.RecoveryHashes = JsonSerializer.Serialize(left); await db.SaveChangesAsync(ct);
            await audit.WriteAsync("auth.recovery_used", u.Email, left.Count + " left", actor: u.Email, ct: ct);
            return StepResult.Recovery;
        }
        await audit.WriteAsync("auth.2fa_failed", u.Email, actor: u.Email, ct: ct);
        return StepResult.Bad;
    }

    public async Task<bool> DisableAsync(Guid id, string password, string code, CancellationToken ct)
    {
        var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id && x.Active, ct);
        if (u == null || !u.TotpEnabled) return false;
        if (hasher.VerifyHashedPassword(u, u.PasswordHash, password ?? "") == PasswordVerificationResult.Failed) { await audit.WriteAsync("auth.2fa_failed", u.Email, "wrong password while switching off", actor: u.Email, ct: ct); return false; }
        if (await VerifyAsync(id, code, ct) == StepResult.Bad) return false;
        u = await db.Users.FirstAsync(x => x.Id == id, ct);
        u.TotpEnabled = false; u.TotpSecret = null; u.RecoveryHashes = "[]";
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("auth.2fa_disabled", u.Email, actor: u.Email, ct: ct);
        return true;
    }

    /// <summary>An administrator removes the second step of someone who lost the phone and the recovery codes.</summary>
    public async Task<bool> ResetAsync(string email, CancellationToken ct)
    {
        var e = email.Trim().ToLowerInvariant();
        var u = await db.Users.FirstOrDefaultAsync(x => x.Email == e, ct);
        if (u == null) return false;
        u.TotpEnabled = false; u.TotpSecret = null; u.RecoveryHashes = "[]"; u.TotpLastStep = 0;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("auth.2fa_reset", e, ct: ct);
        return true;
    }
}

public static class TwoStepEndpoints
{
    static readonly ConcurrentDictionary<string, (int n, DateTime until)> fails = new();
    static bool Locked(string key) => fails.TryGetValue(key, out var f) && f.n >= 5 && f.until > DateTime.UtcNow;
    static void Fail(string key) => fails.AddOrUpdate(key, (1, DateTime.UtcNow.AddMinutes(15)), (_, o) => (o.until > DateTime.UtcNow ? o.n + 1 : 1, DateTime.UtcNow.AddMinutes(15)));

    static ITimeLimitedDataProtector Protector(HttpContext h) => h.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector("dk.twostep").ToTimeLimitedDataProtector();
    static Guid? Sub(ClaimsPrincipal u) => Guid.TryParse(u.FindFirstValue("sub"), out var g) ? g : null;

    static string Field(IAntiforgery af, HttpContext http) { var t = af.GetAndStoreTokens(http); return "<input type=\"hidden\" name=\"" + t.FormFieldName + "\" value=\"" + WebUtility.HtmlEncode(t.RequestToken) + "\">"; }

    static IResult CodePage(HttpContext http, IAntiforgery af, string error = "", int status = 200) =>
        Results.Content(IdentityModule.Html("<form method=\"post\" action=\"/account/login/code\"><h2>Two-step sign-in</h2>"
            + (error == "" ? "<p>Enter the 6-digit code from your authenticator app, or a recovery code.</p>" : "<p class=\"e\" role=\"alert\">" + WebUtility.HtmlEncode(error) + "</p>")
            + Field(af, http) + "<input name=\"code\" autocomplete=\"one-time-code\" inputmode=\"numeric\" placeholder=\"Code\" required autofocus><button type=\"submit\">Continue</button></form>"),
            "text/html; charset=utf-8", Encoding.UTF8, status);

    /// <summary>The password was right: remember who for five minutes (sealed cookie) and ask for the code.</summary>
    internal static IResult Pending(HttpContext http, IAntiforgery af, Guid userId, string returnUrl)
    {
        var tok = Protector(http).Protect(userId.ToString("N") + "|" + IdentityModule.LocalOrRoot(returnUrl), TimeSpan.FromMinutes(5));
        http.Response.Cookies.Append("dk.pending", tok, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = http.Request.IsHttps, MaxAge = TimeSpan.FromMinutes(5), Path = "/account" });
        return CodePage(http, af);
    }

    static string SecurityPage(HttpContext http, IAntiforgery af, string body) => IdentityModule.Html("<form method=\"post\" action=\"/account/security/enable\">" + body + "</form>");

    public static void Map(IEndpointRouteBuilder r)
    {
        // ---- sign-in: second step
        r.MapPost("/account/login/code", async (HttpContext http, IAntiforgery af, TwoStepService svc, IdentityDb db, ITenantContext tenant, IAuditLog audit, CancellationToken ct) =>
        {
            try { await af.ValidateRequestAsync(http); } catch (AntiforgeryValidationException) { return Results.BadRequest(new { error = "invalid_form" }); }
            string[] pending;
            try { pending = Protector(http).Unprotect(http.Request.Cookies["dk.pending"] ?? "").Split('|', 2); }
            catch (Exception) { return Results.Redirect("/account/login"); }   // missing, changed or older than five minutes: start again
            var uid = Guid.Parse(pending[0]); var returnUrl = pending[1];
            var key = "login|" + tenant.Require().Id + "|" + uid;
            if (Locked(key)) return CodePage(http, af, "Too many attempts. Try again in a few minutes.", 429);
            var code = (await http.Request.ReadFormAsync())["code"].ToString();
            var res = await svc.VerifyAsync(uid, code, ct);
            if (res == StepResult.Bad) { Fail(key); return CodePage(http, af, "That code is not right.", 401); }
            fails.TryRemove(key, out _);
            var u = await db.Users.AsNoTracking().FirstAsync(x => x.Id == uid, ct);
            http.Response.Cookies.Delete("dk.pending", new CookieOptions { Path = "/account" });
            await http.SignInAsync(IdentityModule.CookieName, new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", uid.ToString()), new Claim("mfa", "1") }, IdentityModule.CookieName)));
            await audit.WriteAsync("auth.login", u.Email, res == StepResult.Recovery ? "password + recovery code" : "password + authenticator code", actor: u.Email, ct: ct);
            return Results.Redirect(IdentityModule.LocalOrRoot(returnUrl));
        }).AllowAnonymous();

        // ---- API (bearer): used by the web app
        r.MapGet("/api/account/security", async (ClaimsPrincipal user, TwoStepService svc, CancellationToken ct) =>
        {
            var s = Sub(user) is { } id ? await svc.StatusAsync(id, ct) : null;
            return s == null ? Results.Unauthorized() : Results.Ok(new { totpEnabled = s.Value.enabled, recoveryCodesLeft = s.Value.recoveryLeft });
        }).RequireAuthorization();

        r.MapPost("/api/account/totp/setup", async (ClaimsPrincipal user, TwoStepService svc, CancellationToken ct) =>
        {
            var s = Sub(user) is { } id ? await svc.BeginSetupAsync(id, ct) : null;
            return s == null ? Results.Conflict(new { error = "already_enabled" }) : Results.Ok(new { secret = s.Value.secret, otpauthUri = s.Value.uri });
        }).RequireAuthorization();

        r.MapPost("/api/account/totp/enable", async (ClaimsPrincipal user, HttpRequest req, TwoStepService svc, ITenantContext tenant, CancellationToken ct) =>
        {
            var id = Sub(user); if (id == null) return Results.Unauthorized();
            var key = "enable|" + tenant.Require().Id + "|" + id; if (Locked(key)) return Results.StatusCode(429);
            var code = await ReadField(req, "code", ct);
            var codes = await svc.EnableAsync(id.Value, code, ct);
            if (codes == null) { Fail(key); return Results.BadRequest(new { error = "code_not_right_or_not_started" }); }
            fails.TryRemove(key, out _);
            return Results.Ok(new { recoveryCodes = codes, note = "Keep these codes safe. They are not shown again." });
        }).RequireAuthorization();

        r.MapPost("/api/account/totp/disable", async (ClaimsPrincipal user, HttpRequest req, TwoStepService svc, ITenantContext tenant, CancellationToken ct) =>
        {
            var id = Sub(user); if (id == null) return Results.Unauthorized();
            var key = "disable|" + tenant.Require().Id + "|" + id; if (Locked(key)) return Results.StatusCode(429);
            var body = await ReadBody(req, ct);   // the body can be read once: take both fields from it
            var ok = await svc.DisableAsync(id.Value, Str(body, "password"), Str(body, "code"), ct);
            if (!ok) { Fail(key); return Results.BadRequest(new { error = "password_or_code_not_right" }); }
            fails.TryRemove(key, out _);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization();

        r.MapPost("/api/admin/users/totp-reset", async (HttpRequest req, TwoStepService svc, CancellationToken ct) =>
            await svc.ResetAsync(await ReadField(req, "email", ct), ct) ? Results.Ok(new { ok = true }) : Results.NotFound())
            .RequireAuthorization(p => p.RequireRole("admin"));

        // ---- pages (session cookie): used when a customer must switch on two-step sign-in before the first app opens
        r.MapGet("/account/security", async (HttpContext http, IAntiforgery af, TwoStepService svc, CancellationToken ct) =>
        {
            var c = await http.AuthenticateAsync(IdentityModule.CookieName);
            if (!c.Succeeded || !Guid.TryParse(c.Principal!.FindFirstValue("sub"), out var id)) return Results.Redirect("/account/login?ReturnUrl=" + Uri.EscapeDataString("/account/security"));
            var st = await svc.StatusAsync(id, ct);
            if (st == null) return Results.Redirect("/account/login");
            var back = IdentityModule.LocalOrRoot(http.Request.Query["returnUrl"].ToString());
            if (st.Value.enabled)
                return Results.Content(IdentityModule.Html("<form><h2>Two-step sign-in is on</h2><p>Recovery codes left: " + st.Value.recoveryLeft + "</p><a href=\"" + WebUtility.HtmlEncode(back) + "\">Continue</a></form>"), "text/html; charset=utf-8");
            var setup = await svc.BeginSetupAsync(id, ct);
            if (setup == null) return Results.Redirect("/account/login");
            return Results.Content(SecurityPage(http, af,
                "<h2>Switch on two-step sign-in</h2><p>Add this key to an authenticator app (Microsoft Authenticator, Google Authenticator, Authy ...):</p><p><code>" + setup.Value.secret + "</code></p>"
                + "<p><a href=\"" + WebUtility.HtmlEncode(setup.Value.uri) + "\">Open in the app</a></p><p>Then enter the 6-digit code it shows:</p>"
                + Field(af, http) + "<input type=\"hidden\" name=\"returnUrl\" value=\"" + WebUtility.HtmlEncode(back) + "\"><input name=\"code\" inputmode=\"numeric\" autocomplete=\"one-time-code\" required><button type=\"submit\">Switch on</button>"),
                "text/html; charset=utf-8");
        }).AllowAnonymous();

        r.MapPost("/account/security/enable", async (HttpContext http, IAntiforgery af, TwoStepService svc, IdentityDb db, ITenantContext tenant, CancellationToken ct) =>
        {
            try { await af.ValidateRequestAsync(http); } catch (AntiforgeryValidationException) { return Results.BadRequest(new { error = "invalid_form" }); }
            var c = await http.AuthenticateAsync(IdentityModule.CookieName);
            if (!c.Succeeded || !Guid.TryParse(c.Principal!.FindFirstValue("sub"), out var id)) return Results.Redirect("/account/login");
            var f = await http.Request.ReadFormAsync(); var back = IdentityModule.LocalOrRoot(f["returnUrl"].ToString());
            var key = "enable|" + tenant.Require().Id + "|" + id; if (Locked(key)) return Results.StatusCode(429);
            var codes = await svc.EnableAsync(id, f["code"].ToString(), ct);
            if (codes == null) { Fail(key); return Results.Redirect("/account/security?returnUrl=" + Uri.EscapeDataString(back)); }
            fails.TryRemove(key, out _);
            await http.SignInAsync(IdentityModule.CookieName, new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", id.ToString()), new Claim("mfa", "1") }, IdentityModule.CookieName)));   // they just proved the app works
            return Results.Content(IdentityModule.Html("<form><h2>Two-step sign-in is on</h2><p>Keep these recovery codes safe. Each works once if you lose your phone. They are not shown again.</p><pre>"
                + string.Join("\n", codes) + "</pre><a href=\"" + WebUtility.HtmlEncode(back) + "\">Continue</a></form>"), "text/html; charset=utf-8");
        }).AllowAnonymous();
    }

    static async Task<JsonElement> ReadBody(HttpRequest req, CancellationToken ct)
    {
        try { return await req.ReadFromJsonAsync<JsonElement>(ct); }
        catch (JsonException) { return default; }
    }
    static string Str(JsonElement el, string name) => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
    static async Task<string> ReadField(HttpRequest req, string name, CancellationToken ct) => Str(await ReadBody(req, ct), name);
}
