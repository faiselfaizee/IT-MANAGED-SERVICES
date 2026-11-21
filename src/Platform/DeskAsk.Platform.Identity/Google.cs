using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

/// <summary>
/// Sign in with Google (Google Workspace accounts), set by an administrator: an OAuth client (id and secret) for signing in, the Workspace
/// domains people may come from, and for importing users a service account with domain-wide delegation plus the Workspace administrator
/// it acts as (read-only directory scope).
/// </summary>
public sealed record GoogleConfig(bool Enabled, string ClientId, string Secret, string Domains, bool AutoCreate, string DefaultRole, string ServiceAccount, string AdminEmail)
{
    public const string Key = "identity.google";
    const string Purpose = "identity.google";
    public static readonly GoogleConfig None = new(false, "", "", "", false, "customer", "", "");
    public bool Ready => Enabled && ClientId != "" && Secret != "";
    public string[] DomainList => Domains.Split(new[] { ',', ';', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().TrimStart('@').ToLowerInvariant()).Distinct().ToArray();

    public static async Task<GoogleConfig> LoadAsync(ISettings s, IDataProtectionProvider dp, CancellationToken ct)
    {
        var raw = await s.GetAsync(Key, ct);
        if (string.IsNullOrWhiteSpace(raw)) return None;
        try
        {
            var e = JsonDocument.Parse(raw).RootElement;
            string T(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            bool B(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
            string Open(string n) { var x = T(n); try { return x == "" ? "" : dp.CreateProtector(Purpose).Unprotect(x); } catch { return ""; } }
            return new GoogleConfig(B("enabled"), T("clientId"), Open("secret"), T("domains"), B("autoCreate"), T("defaultRole") is { Length: > 0 } r ? r : "customer", Open("serviceAccount"), T("adminEmail"));
        }
        catch (JsonException) { return None; }
    }

    public Task SaveAsync(ISettings s, IDataProtectionProvider dp, CancellationToken ct)
    {
        string Seal(string x) => x == "" ? "" : dp.CreateProtector(Purpose).Protect(x);
        return s.SetAsync(Key, JsonSerializer.Serialize(new { enabled = Enabled, clientId = ClientId, secret = Seal(Secret), domains = Domains, autoCreate = AutoCreate, defaultRole = DefaultRole,
            serviceAccount = Seal(ServiceAccount), adminEmail = AdminEmail }), ct);
    }
}

public static class GoogleSso
{
    const string StateCookie = "dk.gsso";
    static string Accounts(IConfiguration c) => (c["Identity:GoogleAccounts"] ?? "https://accounts.google.com").TrimEnd('/');
    static string TokenUrl(IConfiguration c) => c["Identity:GoogleToken"] ?? "https://oauth2.googleapis.com/token";
    static string Directory(IConfiguration c) => (c["Identity:GoogleDirectory"] ?? "https://admin.googleapis.com").TrimEnd('/');
    static string Callback(HttpContext h) => $"{h.Request.Scheme}://{h.Request.Host}/account/sso/google/callback";
    static IResult Error(string text, int status = 400) => SsoEndpoints.ErrorPage("Google", text, status);
    static readonly string[] Issuers = { "https://accounts.google.com", "accounts.google.com" };

    public static async Task<bool> EnabledAsync(HttpContext h) =>
        (await GoogleConfig.LoadAsync(h.RequestServices.GetRequiredService<ISettings>(), h.RequestServices.GetRequiredService<IDataProtectionProvider>(), h.RequestAborted)).Ready;

    /// <summary>Reads the service account key file Google gives (JSON with client_email and private_key). Null and a message when it is not one.</summary>
    internal static (string? Email, RSA? Key, string? Error) ServiceAccount(string json)
    {
        try
        {
            var e = JsonDocument.Parse(json).RootElement;
            var email = SsoEndpoints.Str(e, "client_email"); var pem = SsoEndpoints.Str(e, "private_key");
            if (SsoEndpoints.Str(e, "type") != "service_account" || email == "" || !pem.Contains("PRIVATE KEY")) return (null, null, "That is not a service account key file (JSON with type service_account).");
            var rsa = RSA.Create(); rsa.ImportFromPem(pem);
            return (email, rsa, null);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or CryptographicException) { return (null, null, "The service account key could not be read: " + ex.Message); }
    }

    /// <summary>An access token for the Workspace directory (read only), acting as the administrator through domain-wide delegation.</summary>
    static async Task<(string? Token, string? Error)> DirectoryToken(GoogleConfig c, IConfiguration cfg, CancellationToken ct)
    {
        if (c.ServiceAccount == "" || c.AdminEmail == "") return (null, "Save the service account key and the Workspace administrator's email first.");
        var (email, key, err) = ServiceAccount(c.ServiceAccount); if (key == null) return (null, err);
        using (key)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var head = SsoEndpoints.B64(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
            var claims = SsoEndpoints.B64(JsonSerializer.SerializeToUtf8Bytes(new { iss = email, sub = c.AdminEmail, scope = "https://www.googleapis.com/auth/admin.directory.user.readonly", aud = TokenUrl(cfg), iat = now, exp = now + 3600 }));
            var sig = SsoEndpoints.B64(key.SignData(Encoding.ASCII.GetBytes(head + "." + claims), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            using var r = await SsoEndpoints.Client().PostAsync(TokenUrl(cfg), new FormUrlEncodedContent(new Dictionary<string, string>
            { ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer", ["assertion"] = head + "." + claims + "." + sig }), ct);
            var text = await r.Content.ReadAsStringAsync(ct);
            try
            {
                var j = JsonDocument.Parse(text).RootElement;
                if (r.IsSuccessStatusCode && SsoEndpoints.Str(j, "access_token") != "") return (SsoEndpoints.Str(j, "access_token"), null);
                var why = SsoEndpoints.Str(j, "error_description") is { Length: > 0 } d ? d : SsoEndpoints.Str(j, "error");
                return (null, $"Google refused the service account ({(int)r.StatusCode}): {why}. Check domain-wide delegation for its client id with the scope admin.directory.user.readonly, and that the administrator's email is right.");
            }
            catch (JsonException) { return (null, "Google answered something unexpected (" + (int)r.StatusCode + ")."); }
        }
    }

    public static void Map(IEndpointRouteBuilder r)
    {
        r.MapGet("/account/sso/google", async (HttpContext http, ISettings st, IDataProtectionProvider dp, IConfiguration cfg, string? returnUrl) =>
        {
            var c = await GoogleConfig.LoadAsync(st, dp, http.RequestAborted);
            if (!c.Ready) return Error("Sign in with Google is not switched on.");
            var state = SsoEndpoints.B64(RandomNumberGenerator.GetBytes(24)); var nonce = SsoEndpoints.B64(RandomNumberGenerator.GetBytes(24)); var verifier = SsoEndpoints.B64(RandomNumberGenerator.GetBytes(48));
            var saved = dp.CreateProtector("identity.gsso.state").Protect(JsonSerializer.Serialize(new { state, nonce, verifier, ret = IdentityModule.LocalOrRoot(returnUrl), exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds() }));
            http.Response.Cookies.Append(StateCookie, saved, new CookieOptions { HttpOnly = true, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromMinutes(10), Path = "/account/sso" });
            var challenge = SsoEndpoints.B64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var domains = c.DomainList;
            var url = $"{Accounts(cfg)}/o/oauth2/v2/auth?client_id={Uri.EscapeDataString(c.ClientId)}&response_type=code&redirect_uri={Uri.EscapeDataString(Callback(http))}"
                + $"&scope={Uri.EscapeDataString("openid email profile")}&state={state}&nonce={nonce}&code_challenge={challenge}&code_challenge_method=S256&prompt=select_account"
                + (domains.Length == 1 ? "&hd=" + Uri.EscapeDataString(domains[0]) : "");
            return Results.Redirect(url);
        }).AllowAnonymous();

        r.MapGet("/account/sso/google/callback", async (HttpContext http, ISettings st, IDataProtectionProvider dp, IConfiguration cfg, IdentityDb db, IPasswordHasher<UserAccount> hasher, IAuditLog audit, IEntitlements ent) =>
        {
            var q = http.Request.Query; var ct = http.RequestAborted;
            if (q.ContainsKey("error")) return Error("Google did not sign you in: " + q["error"]);
            var c = await GoogleConfig.LoadAsync(st, dp, ct); if (!c.Ready) return Error("Sign in with Google is not switched on.");
            JsonElement saved;
            try { saved = JsonDocument.Parse(dp.CreateProtector("identity.gsso.state").Unprotect(http.Request.Cookies[StateCookie] ?? "")).RootElement.Clone(); }
            catch { return Error("The sign-in took too long or was started in another browser. Start again."); }
            http.Response.Cookies.Delete(StateCookie, new CookieOptions { Path = "/account/sso" });
            if (saved.GetProperty("exp").GetInt64() < DateTimeOffset.UtcNow.ToUnixTimeSeconds() || q["state"] != SsoEndpoints.Str(saved, "state") || string.IsNullOrEmpty(q["code"])) return Error("The sign-in could not be checked. Start again.");

            using var resp = await SsoEndpoints.Client().PostAsync(TokenUrl(cfg), new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = c.ClientId, ["client_secret"] = c.Secret, ["grant_type"] = "authorization_code", ["code"] = q["code"].ToString(), ["redirect_uri"] = Callback(http),
                ["code_verifier"] = SsoEndpoints.Str(saved, "verifier"),
            }), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            JsonElement tok; try { tok = JsonDocument.Parse(body).RootElement.Clone(); } catch (JsonException) { return Error("Google answered something unexpected."); }
            if (!resp.IsSuccessStatusCode || SsoEndpoints.Str(tok, "id_token") == "") return Error("Google refused the sign-in: " + (SsoEndpoints.Str(tok, "error_description") is { Length: > 0 } ed ? ed : "check the client secret and the redirect address."));
            // the identity token came straight from Google over TLS in exchange for our secret; its claims are checked against what we asked for
            var parts = SsoEndpoints.Str(tok, "id_token").Split('.'); if (parts.Length != 3) return Error("Google's answer was not understood.");
            JsonElement claims; try { claims = JsonDocument.Parse(SsoEndpoints.UnB64(parts[1])).RootElement.Clone(); } catch { return Error("Google's answer was not understood."); }
            var aud = claims.TryGetProperty("aud", out var av) && av.ValueKind == JsonValueKind.String ? av.GetString() : "";
            var exp = claims.TryGetProperty("exp", out var ev) && ev.ValueKind == JsonValueKind.Number && ev.TryGetInt64(out var ex) ? ex : 0;
            var verified = claims.TryGetProperty("email_verified", out var vv) && (vv.ValueKind == JsonValueKind.True || (vv.ValueKind == JsonValueKind.String && vv.GetString() == "true"));
            if (!Issuers.Contains(SsoEndpoints.Str(claims, "iss")) || aud != c.ClientId || SsoEndpoints.Str(claims, "nonce") != SsoEndpoints.Str(saved, "nonce") || exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                return Error("Google's answer could not be checked. Start again.");
            var sub = SsoEndpoints.Str(claims, "sub"); var email = SsoEndpoints.Str(claims, "email").Trim().ToLowerInvariant(); var hd = SsoEndpoints.Str(claims, "hd").ToLowerInvariant();
            if (sub == "" || !email.Contains('@') || !verified) return Error("Google did not give a confirmed email address for this account.");
            var domains = c.DomainList;
            // with Workspace domains set, only accounts managed by them come in (a personal Gmail with the same address cannot)
            if (domains.Length > 0 && !domains.Contains(hd))
            {
                await audit.WriteAsync("auth.sso_refused", target: email, detail: "google, domain " + (hd == "" ? "none (personal account)" : hd), actor: email);
                return Error("Only accounts of " + string.Join(", ", domains) + " may sign in here.", 403);
            }
            var ext = "google:" + sub;
            UserAccount? fresh = null;
            var user = await db.Users.FirstOrDefaultAsync(x => x.ExternalId == ext, ct) ?? await db.Users.FirstOrDefaultAsync(x => x.Email == email, ct);
            if (user == null)
            {
                if (!c.AutoCreate || domains.Length == 0)
                {
                    await audit.WriteAsync("auth.sso_refused", target: email, detail: "google, no account", actor: email);
                    return Error("There is no DeskAsk account for " + email + ". Ask an administrator to add or import you.", 403);
                }
                var role = UsersEndpoints.Roles.Contains(c.DefaultRole) && c.DefaultRole != "admin" ? c.DefaultRole : "customer";
                if (StaffLimit.IsStaff(role) && await StaffLimit.RefuseAsync(ent, db, 1, ct) != null) return Error("All staff accounts of the licence are in use. Ask an administrator to add you.", 403);
                var name = SsoEndpoints.Str(claims, "name") is { Length: > 0 } nm ? nm : email;
                user = new UserAccount { Email = email, Name = name.Length > 200 ? name[..200] : name, Role = role };
                user.PasswordHash = hasher.HashPassword(user, SsoEndpoints.B64(RandomNumberGenerator.GetBytes(32)));
                db.Users.Add(user); fresh = user;
                await audit.WriteAsync("user.created", email, "role=" + role + " (first Google sign-in)", actor: email);
            }
            if (!user.Active) return Error("This account is disabled.", 403);
            // an account already linked to Microsoft or Active Directory keeps that link; Google signs it in by its email
            if (string.IsNullOrEmpty(user.ExternalId)) user.ExternalId = ext;
            await db.SaveChangesAsync(ct);
            if (fresh != null) await SingleCompanyLink.TryAsync(http.RequestServices, new[] { fresh }, ct);
            await audit.WriteAsync("auth.login", target: email, actor: email, detail: "google");
            await http.SignInAsync(IdentityModule.CookieName, new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", user.Id.ToString()), new Claim("mfa", "1") }, IdentityModule.CookieName)));
            return Results.Redirect(IdentityModule.LocalOrRoot(SsoEndpoints.Str(saved, "ret")));
        }).AllowAnonymous();

        var g = r.MapGroup("/api/platform/sso/google").RequireAuthorization(p => p.RequireRole("admin"));
        g.MapGet("", async (HttpContext http, ISettings st, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            var c = await GoogleConfig.LoadAsync(st, dp, ct);
            string? saEmail = null;
            if (c.ServiceAccount != "") { var sa = ServiceAccount(c.ServiceAccount); saEmail = sa.Email; sa.Key?.Dispose(); }
            return Results.Ok(new { enabled = c.Enabled, clientId = c.ClientId, hasSecret = c.Secret != "", domains = c.Domains, autoCreate = c.AutoCreate, defaultRole = c.DefaultRole,
                serviceAccount = saEmail ?? "", adminEmail = c.AdminEmail, redirectUri = Callback(http) });
        });
        g.MapPut("", async (JsonElement b, ISettings st, IDataProtectionProvider dp, IAuditLog audit, CancellationToken ct) =>
        {
            var old = await GoogleConfig.LoadAsync(st, dp, ct);
            bool Has(string n, out JsonElement v) { v = default; return b.ValueKind == JsonValueKind.Object && b.TryGetProperty(n, out v); }
            string T(string n, string cur) => Has(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : cur;
            string Secret(string n, string cur) => Has(n, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Trim() != "" ? v.GetString()!.Trim() : cur;
            var clientId = T("clientId", old.ClientId); var secret = Secret("secret", old.Secret); var sa = Secret("serviceAccount", old.ServiceAccount);
            var enabled = Has("enabled", out var en) ? en.ValueKind == JsonValueKind.True : old.Enabled;
            var auto = Has("autoCreate", out var au) ? au.ValueKind == JsonValueKind.True : old.AutoCreate;
            var role = T("defaultRole", old.DefaultRole); var domains = T("domains", old.Domains); var admin = T("adminEmail", old.AdminEmail).ToLowerInvariant();
            if (clientId != "" && !clientId.EndsWith(".apps.googleusercontent.com")) return Results.BadRequest(new { error = "The client id ends with .apps.googleusercontent.com." });
            if (!UsersEndpoints.Roles.Contains(role) || role == "admin") return Results.BadRequest(new { error = "New people can be created as engineer, viewer, customer administrator or customer." });
            if (enabled && (clientId == "" || secret == "")) return Results.BadRequest(new { error = "Enter the client id and secret before switching it on." });
            var cfgNew = new GoogleConfig(enabled, clientId, secret, domains, auto, role, sa, admin);
            if (auto && cfgNew.DomainList.Length == 0) return Results.BadRequest(new { error = "To create people at their first sign-in, give the Workspace domains they come from." });
            if (cfgNew.DomainList.Any(d => !d.Contains('.') || d.Contains('@'))) return Results.BadRequest(new { error = "Give domains like acme.com." });
            if (admin != "" && !admin.Contains('@')) return Results.BadRequest(new { error = "The Workspace administrator is an email address." });
            if (sa != old.ServiceAccount) { var (_, key, err) = ServiceAccount(sa); if (key == null) return Results.BadRequest(new { error = err }); key.Dispose(); }
            await cfgNew.SaveAsync(st, dp, ct);
            await audit.WriteAsync("identity.google_changed", ct: ct);
            return Results.Ok(new { ok = true });
        });
        g.MapPost("/test", async (ISettings st, IDataProtectionProvider dp, IConfiguration cfg, CancellationToken ct) =>
        {
            var c = await GoogleConfig.LoadAsync(st, dp, ct);
            var (token, err) = await DirectoryToken(c, cfg, ct); if (token == null) return Results.BadRequest(new { error = err });
            using var req = new HttpRequestMessage(HttpMethod.Get, Directory(cfg) + "/admin/directory/v1/users?customer=my_customer&maxResults=1"); req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await SsoEndpoints.Client().SendAsync(req, ct);
            return resp.IsSuccessStatusCode ? Results.Ok(new { ok = true }) : Results.BadRequest(new { error = $"Signed in, but the directory could not be read ({(int)resp.StatusCode}). Turn on the Admin SDK API in the service account's Google Cloud project." });
        });
        g.MapGet("/users", async (ISettings st, IDataProtectionProvider dp, IConfiguration cfg, IdentityDb db, CancellationToken ct) =>
        {
            var c = await GoogleConfig.LoadAsync(st, dp, ct);
            var (token, err) = await DirectoryToken(c, cfg, ct); if (token == null) return Results.BadRequest(new { error = err });
            var have = (await db.Users.AsNoTracking().Select(x => x.Email).ToListAsync(ct)).ToHashSet();
            var items = new List<object>(); string? page = ""; var pages = 0;
            while (page != null && pages++ < 15)
            {
                var url = Directory(cfg) + "/admin/directory/v1/users?customer=my_customer&maxResults=200&orderBy=email&fields=users(id,primaryEmail,name/fullName,suspended,isAdmin),nextPageToken"
                    + (page != "" ? "&pageToken=" + Uri.EscapeDataString(page) : "");
                using var req = new HttpRequestMessage(HttpMethod.Get, url); req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var resp = await SsoEndpoints.Client().SendAsync(req, ct);
                var text = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode) return Results.BadRequest(new { error = $"The directory could not be read ({(int)resp.StatusCode})." });
                var j = JsonDocument.Parse(text).RootElement;
                if (j.TryGetProperty("users", out var us) && us.ValueKind == JsonValueKind.Array)
                    foreach (var u in us.EnumerateArray())
                    {
                        var mail = SsoEndpoints.Str(u, "primaryEmail").Trim().ToLowerInvariant(); if (!mail.Contains('@')) continue;
                        var name = u.TryGetProperty("name", out var nv) ? SsoEndpoints.Str(nv, "fullName") : "";
                        items.Add(new { id = SsoEndpoints.Str(u, "id"), name, email = mail, enabled = !(u.TryGetProperty("suspended", out var sv) && sv.ValueKind == JsonValueKind.True), exists = have.Contains(mail) });
                    }
                page = SsoEndpoints.Str(j, "nextPageToken") is { Length: > 0 } np ? np : null;
            }
            return Results.Ok(new { items, more = page != null });
        });
        // people chosen from the Workspace directory: created with the role, or linked when they already have an account (by email)
        g.MapPost("/import", async (JsonElement b, IServiceProvider sp, IdentityDb db, IPasswordHasher<UserAccount> hasher, IAuditLog audit, IEntitlements ent, CancellationToken ct) =>
        {
            var role = (b.TryGetProperty("role", out var rv) && rv.ValueKind == JsonValueKind.String ? rv.GetString()! : "").Trim().ToLowerInvariant();
            if (!UsersEndpoints.Roles.Contains(role)) return Results.BadRequest(new { error = "Choose a role." });
            if (!b.TryGetProperty("users", out var us) || us.ValueKind != JsonValueKind.Array || us.GetArrayLength() is 0 or > 500) return Results.BadRequest(new { error = "Choose 1 to 500 people." });
            if (await StaffLimit.RefuseImportAsync(ent, db, role, us.EnumerateArray().Select(x => SsoEndpoints.Str(x, "email")), ct) is { } over) return over;
            int created = 0, linked = 0, skipped = 0; var madeUsers = new List<UserAccount>();
            foreach (var u in us.EnumerateArray())
            {
                var email = SsoEndpoints.Str(u, "email").Trim().ToLowerInvariant(); var gid = SsoEndpoints.Str(u, "id"); var name = SsoEndpoints.Str(u, "name").Trim();
                if (!email.Contains('@') || email.Length > 320) { skipped++; continue; }
                var ext = gid == "" ? null : "google:" + gid;
                var exist = await db.Users.FirstOrDefaultAsync(x => x.Email == email, ct);
                if (exist != null) { if (ext != null && string.IsNullOrEmpty(exist.ExternalId)) { exist.ExternalId = ext; linked++; } else skipped++; continue; }
                var acc = new UserAccount { Email = email, Name = name == "" ? email : (name.Length > 200 ? name[..200] : name), Role = role, ExternalId = ext };
                acc.PasswordHash = hasher.HashPassword(acc, SsoEndpoints.B64(RandomNumberGenerator.GetBytes(32)));
                db.Users.Add(acc); created++; madeUsers.Add(acc);
            }
            await db.SaveChangesAsync(ct);
            await SingleCompanyLink.TryAsync(sp, madeUsers, ct);
            await audit.WriteAsync("user.imported", "google", $"created {created}, linked {linked}, skipped {skipped}, role {role}", ct: ct);
            return Results.Ok(new { created, linked, skipped });
        });
    }
}
