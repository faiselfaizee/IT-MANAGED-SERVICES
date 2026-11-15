using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeskAsk.Platform.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace DeskAsk.Platform.Identity;

/// <summary>How people sign in with Microsoft Entra ID (Microsoft 365 work accounts), set by an administrator.</summary>
public sealed record EntraConfig(bool Enabled, string TenantId, string ClientId, string Secret, string Domains, bool AutoCreate, string DefaultRole)
{
    public const string Key = "identity.entra";
    public static readonly EntraConfig None = new(false, "", "", "", "", false, "customer");
    public bool Ready => Enabled && TenantId != "" && ClientId != "" && Secret != "";

    public static async Task<EntraConfig> LoadAsync(ISettings s, IDataProtectionProvider dp, CancellationToken ct)
    {
        var raw = await s.GetAsync(Key, ct);
        if (string.IsNullOrWhiteSpace(raw)) return None;
        try
        {
            var e = JsonDocument.Parse(raw).RootElement;
            string T(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            bool B(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
            var sec = T("secret"); try { sec = sec == "" ? "" : dp.CreateProtector("identity.entra").Unprotect(sec); } catch { sec = ""; }
            return new EntraConfig(B("enabled"), T("tenantId"), T("clientId"), sec, T("domains"), B("autoCreate"), T("defaultRole") is { Length: > 0 } r ? r : "customer");
        }
        catch (JsonException) { return None; }
    }

    public static Task SaveAsync(ISettings s, IDataProtectionProvider dp, EntraConfig c, CancellationToken ct) =>
        s.SetAsync(Key, JsonSerializer.Serialize(new { enabled = c.Enabled, tenantId = c.TenantId, clientId = c.ClientId, secret = c.Secret == "" ? "" : dp.CreateProtector("identity.entra").Protect(c.Secret), domains = c.Domains, autoCreate = c.AutoCreate, defaultRole = c.DefaultRole }), ct);
}

/// <summary>
/// Sign in with Microsoft: the browser goes to Microsoft (authorization code with PKCE), comes back with a code, and the server swaps it for the person's
/// identity over TLS using the app registration's secret. A person is matched to a DeskAsk user by their Microsoft object id or their email; an administrator can
/// let new people from chosen email domains be created at their first sign-in. Microsoft's own sign-in (including its multi-factor step) stands in for the
/// second step. Users can also be imported from the directory.
/// </summary>
public static class SsoEndpoints
{
    /// <summary>Tests replace the network (Microsoft's sign-in and Graph addresses) with a fake.</summary>
    public static HttpMessageHandler? TestHandler { get; set; }
    static HttpClient Client() => TestHandler != null ? new HttpClient(TestHandler, false) : Shared;
    static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(20) };
    const string StateCookie = "dk.sso";

    static string Authority(IConfiguration c) => (c["Identity:EntraAuthority"] ?? "https://login.microsoftonline.com").TrimEnd('/');
    static string Graph(IConfiguration c) => (c["Identity:GraphUrl"] ?? "https://graph.microsoft.com").TrimEnd('/');
    static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    static byte[] UnB64(string s) { s = s.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4)); }
    static string Callback(HttpContext h) => $"{h.Request.Scheme}://{h.Request.Host}/account/sso/microsoft/callback";

    public static async Task<bool> EnabledAsync(HttpContext h) =>
        (await EntraConfig.LoadAsync(h.RequestServices.GetRequiredService<ISettings>(), h.RequestServices.GetRequiredService<IDataProtectionProvider>(), h.RequestAborted)).Ready;

    static IResult Error(string text, int status = 400) =>
        Results.Content(IdentityModule.Html("<form><div class=\"m\"><i></i>DeskAsk</div><h2>Sign in with Microsoft</h2><p class=\"e\" role=\"alert\">" + System.Net.WebUtility.HtmlEncode(text) + "</p><a href=\"/account/login\">Back to sign in</a></form>"), "text/html; charset=utf-8", Encoding.UTF8, status);

    /// <summary>A Microsoft access token for the directory (Graph), for importing users: the app's own permissions, no person involved.</summary>
    static async Task<(string? Token, string? Error)> GraphToken(EntraConfig c, IConfiguration cfg, CancellationToken ct)
    {
        using var r = await Client().PostAsync($"{Authority(cfg)}/{Uri.EscapeDataString(c.TenantId)}/oauth2/v2.0/token", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["client_id"] = c.ClientId, ["client_secret"] = c.Secret, ["grant_type"] = "client_credentials", ["scope"] = Graph(cfg) + "/.default" }), ct);
        var text = await r.Content.ReadAsStringAsync(ct);
        try
        {
            var j = JsonDocument.Parse(text).RootElement;
            if (r.IsSuccessStatusCode && j.TryGetProperty("access_token", out var t)) return (t.GetString(), null);
            return (null, j.TryGetProperty("error_description", out var d) ? d.GetString()!.Split('\n')[0] : "Microsoft refused the sign-in of the app (" + (int)r.StatusCode + ").");
        }
        catch (JsonException) { return (null, "Microsoft answered something unexpected (" + (int)r.StatusCode + ")."); }
    }

    static string Str(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    public static void Map(IEndpointRouteBuilder r)
    {
        // ---- the browser's way in
        r.MapGet("/account/sso/microsoft", async (HttpContext http, ISettings st, IDataProtectionProvider dp, IConfiguration cfg, string? returnUrl) =>
        {
            var c = await EntraConfig.LoadAsync(st, dp, http.RequestAborted);
            if (!c.Ready) return Error("Sign in with Microsoft is not switched on.");
            var state = B64(RandomNumberGenerator.GetBytes(24)); var nonce = B64(RandomNumberGenerator.GetBytes(24)); var verifier = B64(RandomNumberGenerator.GetBytes(48));
            var protectedState = dp.CreateProtector("identity.sso.state").Protect(JsonSerializer.Serialize(new { state, nonce, verifier, ret = IdentityModule.LocalOrRoot(returnUrl), exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds() }));
            http.Response.Cookies.Append(StateCookie, protectedState, new CookieOptions { HttpOnly = true, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromMinutes(10), Path = "/account/sso" });
            var challenge = B64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var url = $"{Authority(cfg)}/{Uri.EscapeDataString(c.TenantId)}/oauth2/v2.0/authorize?client_id={Uri.EscapeDataString(c.ClientId)}&response_type=code&response_mode=query"
                + $"&redirect_uri={Uri.EscapeDataString(Callback(http))}&scope={Uri.EscapeDataString("openid profile email")}&state={state}&nonce={nonce}&code_challenge={challenge}&code_challenge_method=S256&prompt=select_account";
            return Results.Redirect(url);
        }).AllowAnonymous();

        r.MapGet("/account/sso/microsoft/callback", async (HttpContext http, ISettings st, IDataProtectionProvider dp, IConfiguration cfg, IdentityDb db, IPasswordHasher<UserAccount> hasher, ITenantContext tenant, IAuditLog audit) =>
        {
            var q = http.Request.Query; var ct = http.RequestAborted;
            if (q.ContainsKey("error")) return Error("Microsoft did not sign you in: " + (q["error_description"].ToString().Split('\n')[0] is { Length: > 0 } d ? d : q["error"].ToString()));
            var c = await EntraConfig.LoadAsync(st, dp, ct); if (!c.Ready) return Error("Sign in with Microsoft is not switched on.");
            JsonElement saved;
            try { saved = JsonDocument.Parse(dp.CreateProtector("identity.sso.state").Unprotect(http.Request.Cookies[StateCookie] ?? "")).RootElement.Clone(); }
            catch { return Error("The sign-in took too long or was started in another browser. Start again."); }
            http.Response.Cookies.Delete(StateCookie, new CookieOptions { Path = "/account/sso" });
            if (saved.GetProperty("exp").GetInt64() < DateTimeOffset.UtcNow.ToUnixTimeSeconds() || q["state"] != Str(saved, "state") || string.IsNullOrEmpty(q["code"])) return Error("The sign-in could not be checked. Start again.");

            using var resp = await Client().PostAsync($"{Authority(cfg)}/{Uri.EscapeDataString(c.TenantId)}/oauth2/v2.0/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = c.ClientId, ["client_secret"] = c.Secret, ["grant_type"] = "authorization_code", ["code"] = q["code"].ToString(), ["redirect_uri"] = Callback(http),
                ["code_verifier"] = Str(saved, "verifier"), ["scope"] = "openid profile email",
            }), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            JsonElement tok; try { tok = JsonDocument.Parse(body).RootElement.Clone(); } catch (JsonException) { return Error("Microsoft answered something unexpected."); }
            if (!resp.IsSuccessStatusCode || Str(tok, "id_token") == "") return Error("Microsoft refused the sign-in: " + (Str(tok, "error_description").Split('\n')[0] is { Length: > 0 } ed ? ed : "check the app's secret and redirect address."));
            // the identity token came straight from Microsoft over TLS in exchange for our secret, so its claims are read here and checked against what we asked for
            var parts = Str(tok, "id_token").Split('.'); if (parts.Length != 3) return Error("Microsoft's answer was not understood.");
            JsonElement claims; try { claims = JsonDocument.Parse(UnB64(parts[1])).RootElement.Clone(); } catch { return Error("Microsoft's answer was not understood."); }
            var aud = claims.TryGetProperty("aud", out var av) && av.ValueKind == JsonValueKind.String ? av.GetString() : "";
            var exp = claims.TryGetProperty("exp", out var ev) && ev.TryGetInt64(out var ex) ? ex : 0;
            if (!string.Equals(Str(claims, "tid"), c.TenantId, StringComparison.OrdinalIgnoreCase) || aud != c.ClientId || Str(claims, "nonce") != Str(saved, "nonce") || exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                return Error("This Microsoft account does not belong to the organisation set up for DeskAsk.");
            var oid = Str(claims, "oid"); var email = (Str(claims, "email") is { Length: > 0 } em ? em : Str(claims, "preferred_username")).Trim().ToLowerInvariant();
            if (oid == "" || !email.Contains('@')) return Error("Microsoft did not give an email address for this account. Ask your administrator to fill in the email of your Microsoft account.");

            var user = await db.Users.FirstOrDefaultAsync(x => x.ExternalId == oid, ct) ?? await db.Users.FirstOrDefaultAsync(x => x.Email == email, ct);
            if (user == null)
            {
                var domain = email[(email.IndexOf('@') + 1)..];
                var allowed = c.Domains.Split(new[] { ',', ';', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().TrimStart('@').ToLowerInvariant()).ToArray();
                if (!c.AutoCreate || (allowed.Length > 0 && !allowed.Contains(domain)))
                {
                    await audit.WriteAsync("auth.sso_refused", target: email, actor: email);
                    return Error("There is no DeskAsk account for " + email + ". Ask an administrator to add or import you.", 403);
                }
                var role = UsersEndpoints.Roles.Contains(c.DefaultRole) && c.DefaultRole != "admin" ? c.DefaultRole : "customer";
                var name = Str(claims, "name") is { Length: > 0 } nm ? nm : email;
                user = new UserAccount { Email = email, Name = name.Length > 200 ? name[..200] : name, Role = role };
                user.PasswordHash = hasher.HashPassword(user, B64(RandomNumberGenerator.GetBytes(32)));
                db.Users.Add(user);
                await audit.WriteAsync("user.created", email, "role=" + role + " (first Microsoft sign-in)", actor: email);
            }
            if (!user.Active) return Error("This account is disabled.", 403);
            if (user.ExternalId != oid) user.ExternalId = oid;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("auth.login", target: email, actor: email, detail: "microsoft");
            await http.SignInAsync(IdentityModule.CookieName, new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", user.Id.ToString()), new Claim("mfa", "1") }, IdentityModule.CookieName)));
            return Results.Redirect(IdentityModule.LocalOrRoot(Str(saved, "ret")));
        }).AllowAnonymous();

        // ---- the administrator's side
        var g = r.MapGroup("/api/platform/sso").RequireAuthorization(p => p.RequireRole("admin"));
        g.MapGet("", async (HttpContext http, ISettings st, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            var c = await EntraConfig.LoadAsync(st, dp, ct);
            return Results.Ok(new { enabled = c.Enabled, tenantId = c.TenantId, clientId = c.ClientId, hasSecret = c.Secret != "", domains = c.Domains, autoCreate = c.AutoCreate, defaultRole = c.DefaultRole, redirectUri = Callback(http) });
        });
        g.MapPut("", async (JsonElement b, ISettings st, IDataProtectionProvider dp, IAuditLog audit, CancellationToken ct) =>
        {
            var old = await EntraConfig.LoadAsync(st, dp, ct);
            bool Has(string n, out JsonElement v) => b.ValueKind == JsonValueKind.Object && b.TryGetProperty(n, out v);
            string T(string n, string cur) => Has(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : cur;
            var tenantId = T("tenantId", old.TenantId); var clientId = T("clientId", old.ClientId);
            var secret = Has("secret", out var sv) && sv.ValueKind == JsonValueKind.String && sv.GetString() != "" ? sv.GetString()!.Trim() : old.Secret;
            var enabled = Has("enabled", out var en) ? en.ValueKind == JsonValueKind.True : old.Enabled;
            var auto = Has("autoCreate", out var au) ? au.ValueKind == JsonValueKind.True : old.AutoCreate;
            var role = T("defaultRole", old.DefaultRole);
            if (!Guid.TryParse(tenantId, out _) && tenantId != "") return Results.BadRequest(new { error = "The directory (tenant) id is a GUID like 11111111-2222-3333-4444-555555555555." });
            if (clientId != "" && !Guid.TryParse(clientId, out _)) return Results.BadRequest(new { error = "The application (client) id is a GUID." });
            if (!UsersEndpoints.Roles.Contains(role) || role == "admin") return Results.BadRequest(new { error = "New people can be created as engineer, viewer, customer administrator or customer." });
            if (enabled && (tenantId == "" || clientId == "" || secret == "")) return Results.BadRequest(new { error = "Enter the directory id, application id and secret before switching it on." });
            await EntraConfig.SaveAsync(st, dp, new EntraConfig(enabled, tenantId, clientId, secret, T("domains", old.Domains), auto, role), ct);
            await audit.WriteAsync("identity.entra_changed", ct: ct);
            return Results.Ok(new { ok = true });
        });
        // check the registration works: the app can ask the directory for users
        g.MapPost("/test", async (ISettings st, IDataProtectionProvider dp, IConfiguration cfg, CancellationToken ct) =>
        {
            var c = await EntraConfig.LoadAsync(st, dp, ct);
            if (c.TenantId == "" || c.ClientId == "" || c.Secret == "") return Results.BadRequest(new { error = "Save the directory id, application id and secret first." });
            var (token, err) = await GraphToken(c, cfg, ct);
            if (token == null) return Results.BadRequest(new { error = err });
            using var req = new HttpRequestMessage(HttpMethod.Get, Graph(cfg) + "/v1.0/users?$top=1&$select=id"); req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await Client().SendAsync(req, ct);
            return resp.IsSuccessStatusCode ? Results.Ok(new { ok = true }) : Results.BadRequest(new { error = "The app signed in but may not read users (" + (int)resp.StatusCode + "). Give it the Microsoft Graph application permission User.Read.All and grant admin consent." });
        });

        // the people in the directory, marked when they already have an account
        g.MapGet("/entra/users", async (ISettings st, IDataProtectionProvider dp, IConfiguration cfg, IdentityDb db, CancellationToken ct) =>
        {
            var c = await EntraConfig.LoadAsync(st, dp, ct);
            var (token, err) = await GraphToken(c, cfg, ct); if (token == null) return Results.BadRequest(new { error = err });
            var items = new List<object>(); var next = Graph(cfg) + "/v1.0/users?$select=id,displayName,mail,userPrincipalName,accountEnabled,userType&$top=200";
            var have = (await db.Users.AsNoTracking().Select(x => x.Email).ToListAsync(ct)).ToHashSet();
            for (var page = 0; next != null && page < 15; page++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, next); req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var resp = await Client().SendAsync(req, ct);
                var text = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode) return Results.BadRequest(new { error = "The directory could not be read (" + (int)resp.StatusCode + "). Give the app the Graph permission User.Read.All and grant admin consent." });
                var j = JsonDocument.Parse(text).RootElement;
                foreach (var u in j.GetProperty("value").EnumerateArray())
                {
                    var mail = (Str(u, "mail") is { Length: > 0 } m ? m : Str(u, "userPrincipalName")).Trim().ToLowerInvariant();
                    if (!mail.Contains('@')) continue;
                    items.Add(new { id = Str(u, "id"), name = Str(u, "displayName"), email = mail, enabled = !u.TryGetProperty("accountEnabled", out var ae) || ae.ValueKind != JsonValueKind.False, guest = Str(u, "userType") == "Guest", exists = have.Contains(mail) });
                }
                next = j.TryGetProperty("@odata.nextLink", out var nl) && nl.ValueKind == JsonValueKind.String && nl.GetString()!.StartsWith(Graph(cfg), StringComparison.Ordinal) ? nl.GetString() : null;
            }
            return Results.Ok(new { items, more = next != null });
        });

        g.MapPost("/entra/import", async (JsonElement b, IdentityDb db, IPasswordHasher<UserAccount> hasher, IAuditLog audit, CancellationToken ct) =>
        {
            var role = (b.TryGetProperty("role", out var rv) && rv.ValueKind == JsonValueKind.String ? rv.GetString()! : "").Trim().ToLowerInvariant();
            if (!UsersEndpoints.Roles.Contains(role)) return Results.BadRequest(new { error = "Choose a role." });
            if (!b.TryGetProperty("users", out var us) || us.ValueKind != JsonValueKind.Array || us.GetArrayLength() is 0 or > 500) return Results.BadRequest(new { error = "Choose 1 to 500 people." });
            int created = 0, linked = 0, skipped = 0;
            foreach (var u in us.EnumerateArray())
            {
                var email = Str(u, "email").Trim().ToLowerInvariant(); var oid = Str(u, "id"); var name = Str(u, "name").Trim();
                if (!email.Contains('@') || email.Length > 320) { skipped++; continue; }
                var ex = await db.Users.FirstOrDefaultAsync(x => x.Email == email, ct);
                if (ex != null) { if (ex.ExternalId != oid && oid != "") { ex.ExternalId = oid; linked++; } else skipped++; continue; }
                var acc = new UserAccount { Email = email, Name = name == "" ? email : (name.Length > 200 ? name[..200] : name), Role = role, ExternalId = oid == "" ? null : oid };
                acc.PasswordHash = hasher.HashPassword(acc, B64(RandomNumberGenerator.GetBytes(32)));
                db.Users.Add(acc); created++;
            }
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("user.imported", "entra", $"created {created}, linked {linked}, skipped {skipped}, role {role}", ct: ct);
            return Results.Ok(new { created, linked, skipped });
        });
    }
}
