using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DeskAsk.Platform.Tenancy;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.DataProtection;
using OpenIddict.Server;
using OpenIddict.Validation;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace DeskAsk.Platform.Identity;

/// <summary>
/// The single sign-in for every module (ADR 0006): OpenID Connect provider (authorization code + PKCE) on OpenIddict,
/// users and clients stored in the customer's own database, issuer = the customer's web address.
/// </summary>
public sealed class IdentityModule : IPlatformModule
{
    public const string CookieName = "dk.session";
    public string Key => "identity";
    public string Schema => "identity";

    public void AddServices(IServiceCollection s, IConfiguration cfg)
    {
        s.AddDbContext<IdentityDb>((sp, o) =>
        {
            o.UseNpgsql(sp.GetRequiredService<ITenantContext>().Require().ConnectionString);
            o.UseOpenIddict();
        });
        s.AddSingleton<IPasswordHasher<UserAccount>, PasswordHasher<UserAccount>>();
        s.AddSingleton<ITenantSchema, IdentitySchema>();
        var dp = s.AddDataProtection().SetApplicationName("DeskAsk.Platform");
        var dpPath = cfg["Platform:DataProtectionPath"];   // keep these keys on persistent storage in production so the two-step cookie survives restarts
        if (!string.IsNullOrWhiteSpace(dpPath)) dp.PersistKeysToFileSystem(new DirectoryInfo(dpPath));
        s.AddHttpContextAccessor();
        s.AddSingleton<TenantKeys>();
        s.AddSingleton<ITotpClock, SystemTotpClock>();
        s.AddScoped<TwoStepService>();
        s.AddSingleton<IJobHandler, IdentityPruneJob>();
        s.AddSingleton(new RecurringJob("identity.prune", IdentityPruneJob.JobType, TimeSpan.FromHours(24)));
        s.AddAntiforgery(o => { o.Cookie.Name = "dk.csrf"; o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; });
        s.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
            .AddCookie(CookieName, o =>
            {
                o.LoginPath = "/account/login";
                o.Cookie.Name = CookieName; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromHours(8); o.SlidingExpiration = true;
            });
        s.AddAuthorization();

        var allowHttp = cfg.GetValue<bool>("Identity:AllowInsecureHttp");
        s.AddOpenIddict()
            .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<IdentityDb>())
            .AddServer(o =>
            {
                o.SetAuthorizationEndpointUris("/connect/authorize").SetTokenEndpointUris("/connect/token")
                 .SetUserInfoEndpointUris("/connect/userinfo").SetEndSessionEndpointUris("/connect/logout")
                 .SetConfigurationEndpointUris("/.well-known/openid-configuration").SetJsonWebKeySetEndpointUris("/.well-known/jwks");
                o.AllowAuthorizationCodeFlow().AllowRefreshTokenFlow().RequireProofKeyForCodeExchange();
                o.RegisterScopes(Scopes.OpenId, Scopes.Email, Scopes.Profile, Scopes.Roles, "deskask");
                o.SetAccessTokenLifetime(TimeSpan.FromMinutes(10)).SetIdentityTokenLifetime(TimeSpan.FromMinutes(10))
                 .SetAuthorizationCodeLifetime(TimeSpan.FromSeconds(60)).SetRefreshTokenLifetime(TimeSpan.FromDays(14));
                AddKeys(o, cfg);
                o.DisableAccessTokenEncryption();   // plain signed JWT, so any module or relying party can check it with the published key
                var host = o.UseAspNetCore().EnableAuthorizationEndpointPassthrough().EnableTokenEndpointPassthrough()
                            .EnableUserInfoEndpointPassthrough().EnableEndSessionEndpointPassthrough();
                if (allowHttp) host.DisableTransportSecurityRequirement();
            })
            .AddValidation(o => { o.UseLocalServer(); o.UseAspNetCore(); });

        if (string.IsNullOrWhiteSpace(cfg["Identity:SigningKeyPem"]))
        {   // each customer gets its own keys (TenantKeys); the options above only supply a placeholder key for start-up
            s.AddSingleton<IOptionsMonitor<OpenIddictServerOptions>>(sp => new TenantOptionsMonitor<OpenIddictServerOptions>(
                sp.GetRequiredService<IOptionsFactory<OpenIddictServerOptions>>(), sp.GetRequiredService<IHttpContextAccessor>(), sp.GetRequiredService<TenantKeys>(),
                (o, k) =>
                {
                    o.SigningCredentials.Clear(); foreach (var c in k.Signing) o.SigningCredentials.Add(c);
                    o.EncryptionCredentials.Clear(); o.EncryptionCredentials.Add(k.Encryption);
                }));
            s.AddSingleton<IOptionsMonitor<OpenIddictValidationOptions>>(sp => new TenantOptionsMonitor<OpenIddictValidationOptions>(
                sp.GetRequiredService<IOptionsFactory<OpenIddictValidationOptions>>(), sp.GetRequiredService<IHttpContextAccessor>(), sp.GetRequiredService<TenantKeys>(), null));
        }
    }

    static void AddKeys(OpenIddictServerBuilder o, IConfiguration cfg)
    {
        var pem = cfg["Identity:SigningKeyPem"];
        if (string.IsNullOrWhiteSpace(pem))
        {   // development only: keys are new at every start, so every sign-in ends when the app restarts
            o.AddEphemeralSigningKey(SecurityAlgorithms.EcdsaSha256).AddEphemeralEncryptionKey();
            return;
        }
        var secret = cfg["Identity:EncryptionSecret"];
        if (string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException("Identity:EncryptionSecret is needed together with Identity:SigningKeyPem.");
        var ec = ECDsa.Create(); ec.ImportFromPem(pem);
        var kid = Convert.ToHexString(SHA256.HashData(ec.ExportSubjectPublicKeyInfo()))[..16].ToLowerInvariant();
        o.AddSigningKey(new ECDsaSecurityKey(ec) { KeyId = kid });
        o.AddEncryptionKey(new SymmetricSecurityKey(SHA256.HashData(Encoding.UTF8.GetBytes(secret))));
    }

    public void MapEndpoints(IEndpointRouteBuilder r)
    {
        r.MapMethods("/connect/authorize", new[] { "GET", "POST" }, Authorize).AllowAnonymous();
        r.MapPost("/connect/token", Token).AllowAnonymous();
        r.MapMethods("/connect/userinfo", new[] { "GET", "POST" }, UserInfo).AllowAnonymous();
        r.MapMethods("/connect/logout", new[] { "GET", "POST" }, Logout).AllowAnonymous();
        r.MapGet("/account/login", LoginPage).AllowAnonymous();
        r.MapPost("/account/login", LoginPost).AllowAnonymous();
        TwoStepEndpoints.Map(r);
        r.MapPost("/api/platform/identity/keys/rotate", async (ITenantContext t, TenantKeys keys, IAuditLog audit, CancellationToken ct) =>
        {
            if (keys.Shared) return Results.Conflict(new { error = "This server uses one fixed key (Identity:SigningKeyPem); rotate it in the configuration." });
            keys.Rotate(t.Require());
            await audit.WriteAsync("identity.keys_rotated", ct: ct);
            return Results.Ok(new { ok = true, kid = keys.Get(t.Require()).Kids[0] });
        }).RequireAuthorization(p => p.RequireRole("admin"));
        r.MapGet("/api/platform/me", (ClaimsPrincipal u) => Results.Ok(new
        {
            sub = u.FindFirstValue(Claims.Subject), email = u.FindFirstValue(Claims.Email),
            name = u.FindFirstValue(Claims.Name), role = u.FindFirstValue(Claims.Role), tenant = u.FindFirstValue("tenant"),
        })).RequireAuthorization();
    }

    // ---------------------------------------------------------------- OpenID Connect endpoints

    static async Task<IResult> Authorize(HttpContext http, IdentityDb db, ITenantContext tenant, ISettings settings)
    {
        var req = http.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("No OpenID Connect request.");
        var cookie = await http.AuthenticateAsync(CookieName);
        var sub = cookie.Succeeded ? cookie.Principal!.FindFirstValue("sub") : null;
        if (cookie.Succeeded && req.HasPromptValue(PromptValues.Login) && !http.Request.Query.ContainsKey("_relogin"))
        {   // prompt=login: end the session once, then come back without it asking again
            await http.SignOutAsync(CookieName);
            return Results.Redirect(http.Request.Path + "?" + AddQuery(http.Request.QueryString, "_relogin", "1"));
        }
        if (sub == null || !Guid.TryParse(sub, out var uid))
        {
            if (req.HasPromptValue(PromptValues.None)) return Fail(Errors.LoginRequired, "Sign-in is needed.");
            return Results.Challenge(new AuthenticationProperties { RedirectUri = http.Request.Path + http.Request.QueryString }, new[] { CookieName });
        }
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == uid && x.Active, http.RequestAborted);
        if (user == null) { await http.SignOutAsync(CookieName); return Fail(Errors.LoginRequired, "This account is not active."); }
        var mfa = cookie.Principal!.HasClaim("mfa", "1");
        if (user.TotpEnabled && !mfa)   // a session made without the second step is not enough for someone who has one
        {
            await http.SignOutAsync(CookieName);
            return Results.Challenge(new AuthenticationProperties { RedirectUri = http.Request.Path + http.Request.QueryString }, new[] { CookieName });
        }
        if (!user.TotpEnabled && await settings.GetAsync("security.require_2fa", http.RequestAborted) == "true")
            return Results.Redirect("/account/security?returnUrl=" + Uri.EscapeDataString(http.Request.Path + http.Request.QueryString));
        var p = Principal(user, tenant.Require(), mfa ? new[] { "pwd", "otp" } : new[] { "pwd" });
        p.SetScopes(req.GetScopes());
        SetDestinations(p);
        return Results.SignIn(p, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    static string AddQuery(QueryString q, string k, string v) => (q.HasValue ? q.Value!.TrimStart('?') + "&" : "") + Uri.EscapeDataString(k) + "=" + Uri.EscapeDataString(v);

    static async Task<IResult> Token(HttpContext http, IdentityDb db, ITenantContext tenant)
    {
        var req = http.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("No OpenID Connect request.");
        if (!req.IsAuthorizationCodeGrantType() && !req.IsRefreshTokenGrantType()) return Fail(Errors.UnsupportedGrantType, "This grant type is not supported.");
        var auth = await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        var old = auth.Principal;
        if (old == null || !Guid.TryParse(old.GetClaim(Claims.Subject), out var uid)) return Fail(Errors.InvalidGrant, "The grant is not valid.");
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == uid && x.Active, http.RequestAborted);
        if (user == null) return Fail(Errors.InvalidGrant, "This account is not active any more.");
        var p = Principal(user, tenant.Require(), old.GetClaims("amr").ToArray());    // claims are rebuilt from the database: a role change shows at the next refresh
        p.SetScopes(old.GetScopes());
        SetDestinations(p);
        return Results.SignIn(p, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    static async Task<IResult> UserInfo(HttpContext http)
    {
        var auth = await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        var p = auth.Principal;
        if (p == null) return Results.Challenge(authenticationSchemes: new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme });
        var d = new Dictionary<string, object?> { [Claims.Subject] = p.GetClaim(Claims.Subject), ["tenant"] = p.GetClaim("tenant") };
        if (p.HasScope(Scopes.Email)) d[Claims.Email] = p.GetClaim(Claims.Email);
        if (p.HasScope(Scopes.Profile)) d[Claims.Name] = p.GetClaim(Claims.Name);
        if (p.HasScope(Scopes.Roles)) d[Claims.Role] = p.GetClaim(Claims.Role);
        return Results.Ok(d);
    }

    static async Task<IResult> Logout(HttpContext http, IAuditLog audit)
    {
        var who = (await http.AuthenticateAsync(CookieName)).Principal?.FindFirstValue("sub");
        await http.SignOutAsync(CookieName);
        if (who != null) await audit.WriteAsync("auth.logout", actor: who);
        return Results.SignOut(new AuthenticationProperties { RedirectUri = "/" }, new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme });
    }

    static IResult Fail(string error, string description) =>
        Results.Forbid(new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }), new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme });

    static ClaimsPrincipal Principal(UserAccount u, TenantInfo t, string[] amr)
    {
        var id = new ClaimsIdentity("DeskAsk", Claims.Name, Claims.Role);
        id.SetClaim(Claims.Subject, u.Id.ToString());
        id.SetClaim(Claims.Email, u.Email);
        id.SetClaim(Claims.Name, u.Name);
        id.SetClaim(Claims.Role, u.Role);
        id.SetClaim("tenant", t.Id);
        id.SetClaims("amr", amr.ToImmutableArray());
        return new ClaimsPrincipal(id);
    }

    static void SetDestinations(ClaimsPrincipal p)
    {
        foreach (var c in p.Claims) c.SetDestinations(Destinations(c, p));
    }

    static IEnumerable<string> Destinations(Claim c, ClaimsPrincipal p)
    {
        yield return OpenIddictConstants.Destinations.AccessToken;
        var wanted = c.Type switch { Claims.Name => Scopes.Profile, Claims.Email => Scopes.Email, Claims.Role => Scopes.Roles, _ => null };
        if (wanted == null || p.HasScope(wanted)) yield return OpenIddictConstants.Destinations.IdentityToken;
    }

    // ---------------------------------------------------------------- sign-in page

    // The sign-in pages share the look of the web app: frosted glass over a soft aurora, dark or light as the system prefers.
    const string PageCss =
        ":root{--ink:#eaf1fb;--muted:#a8b7cc;--base:#0a1322;--glass:rgba(255,255,255,.08);--edge:rgba(255,255,255,.18);--line:rgba(255,255,255,.22);--field:rgba(6,12,24,.4);--bad:#ff8a8a;--a:rgba(47,224,207,.55);--b:rgba(91,107,255,.62);--c:rgba(255,138,194,.36);color-scheme:dark}"
        + "@media(prefers-color-scheme:light){:root{--ink:#0f1b2d;--muted:#4c5d75;--base:#e8f0fb;--glass:rgba(255,255,255,.62);--edge:rgba(255,255,255,.9);--line:rgba(30,60,120,.22);--field:rgba(255,255,255,.75);--bad:#b3261e;--a:rgba(47,224,207,.42);--b:rgba(120,130,255,.38);--c:rgba(255,150,200,.32);color-scheme:light}}"
        + "*{box-sizing:border-box}html{background:var(--base)}body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;padding:24px;color:var(--ink);font:15px/1.5 'Segoe UI Variable Text','Segoe UI',system-ui,sans-serif}"
        + "body::before{content:'';position:fixed;inset:-15%;z-index:-1;background:radial-gradient(40% 40% at 14% 12%,var(--a),transparent 70%),radial-gradient(44% 44% at 88% 10%,var(--b),transparent 70%),radial-gradient(42% 42% at 74% 90%,var(--c),transparent 70%),radial-gradient(38% 38% at 30% 70%,var(--b),transparent 72%)}"
        + "form{width:min(380px,100%);padding:30px 28px;border-radius:24px;background:var(--glass);border:1px solid var(--edge);box-shadow:0 22px 60px -20px rgba(0,0,0,.6),inset 0 1px 0 rgba(255,255,255,.35);-webkit-backdrop-filter:blur(24px) saturate(1.5);backdrop-filter:blur(24px) saturate(1.5)}"
        + "h2{margin:0 0 16px;font:600 24px 'Segoe UI Variable Display','Segoe UI',system-ui,sans-serif;letter-spacing:-.02em}"
        + "input,button{width:100%;padding:11px 14px;margin:6px 0;font:inherit;border-radius:12px}input{color:inherit;background:var(--field);border:1px solid var(--line)}input::placeholder{color:var(--muted)}"
        + "input:focus{outline:none;border-color:#2fe0cf;box-shadow:0 0 0 3px rgba(47,224,207,.3)}"
        + "button{margin-top:14px;border:0;cursor:pointer;font-weight:600;color:#04201d;background:linear-gradient(135deg,#2fe0cf,#7a9bff);box-shadow:0 10px 26px -8px rgba(47,224,207,.7),inset 0 1px 0 rgba(255,255,255,.5)}button:hover{filter:brightness(1.07)}"
        + ".e{color:var(--bad);margin:0 0 8px}.m{display:flex;align-items:center;gap:10px;margin:0 0 18px;color:var(--muted);font-weight:600}.m i{width:30px;height:30px;border-radius:9px;background:linear-gradient(135deg,#2fe0cf,#5b6bff)}";

    internal static string Html(string body) => "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta name=\"color-scheme\" content=\"dark light\"><title>Sign in</title>"
        + "<style>" + PageCss + "</style></head><body>" + body + "</body></html>";

    internal static string LocalOrRoot(string? url) =>
        !string.IsNullOrEmpty(url) && url[0] == '/' && !url.StartsWith("//") && !url.StartsWith("/\\") ? url : "/";

    static IResult Page(HttpContext http, IAntiforgery af, string? returnUrl, string error = "", int status = 200)
    {
        var t = af.GetAndStoreTokens(http);
        var body = "<form method=\"post\" action=\"/account/login\"><div class=\"m\"><i></i>DeskAsk</div><h2>Sign in</h2>"
            + (error == "" ? "" : "<p class=\"e\" role=\"alert\">" + WebUtility.HtmlEncode(error) + "</p>")
            + "<input type=\"hidden\" name=\"" + t.FormFieldName + "\" value=\"" + WebUtility.HtmlEncode(t.RequestToken) + "\">"
            + "<input type=\"hidden\" name=\"returnUrl\" value=\"" + WebUtility.HtmlEncode(LocalOrRoot(returnUrl)) + "\">"
            + "<input name=\"email\" type=\"email\" placeholder=\"Email\" autocomplete=\"username\" required>"
            + "<input name=\"password\" type=\"password\" placeholder=\"Password\" autocomplete=\"current-password\" required>"
            + "<button type=\"submit\">Sign in</button></form>";
        return Results.Content(Html(body), "text/html; charset=utf-8", Encoding.UTF8, status);
    }

    static IResult LoginPage(HttpContext http, IAntiforgery af, string? returnUrl) =>
        Page(http, af, returnUrl ?? http.Request.Query["ReturnUrl"].ToString());

    static readonly ConcurrentDictionary<string, (int n, DateTime until)> fails = new();
    const int MaxFails = 5;
    static readonly UserAccount Dummy = new();

    static async Task<IResult> LoginPost(HttpContext http, IAntiforgery af, IdentityDb db, IPasswordHasher<UserAccount> hasher, ITenantContext tenant, ILogger<IdentityModule> log, IAuditLog audit)
    {
        try { await af.ValidateRequestAsync(http); } catch (AntiforgeryValidationException) { return Results.BadRequest(new { error = "invalid_form" }); }
        var f = await http.Request.ReadFormAsync();
        var email = f["email"].ToString().Trim().ToLowerInvariant();
        var returnUrl = f["returnUrl"].ToString();
        var key = tenant.Require().Id + "|" + email;
        if (fails.TryGetValue(key, out var fl) && fl.n >= MaxFails && fl.until > DateTime.UtcNow)
        {
            await audit.WriteAsync("auth.locked", target: email, actor: email);
            return Page(http, af, returnUrl, "Too many attempts. Try again in a few minutes.", StatusCodes.Status429TooManyRequests);
        }

        var user = await db.Users.FirstOrDefaultAsync(x => x.Email == email && x.Active, http.RequestAborted);
        var res = hasher.VerifyHashedPassword(user ?? Dummy, user?.PasswordHash ?? hasher.HashPassword(Dummy, "x"), f["password"].ToString());   // same work whether or not the account exists
        if (user == null || res == PasswordVerificationResult.Failed)
        {
            fails.AddOrUpdate(key, (1, DateTime.UtcNow.AddMinutes(15)), (_, o) => (o.until > DateTime.UtcNow ? o.n + 1 : 1, DateTime.UtcNow.AddMinutes(15)));
            log.LogWarning("Failed sign-in for {Email} on {Tenant}", email, tenant.Require().Id);
            await audit.WriteAsync("auth.login_failed", target: email, actor: email);
            return Page(http, af, returnUrl, "Email or password is not correct.", StatusCodes.Status401Unauthorized);
        }
        fails.TryRemove(key, out _);
        if (user.TotpEnabled) return TwoStepEndpoints.Pending(http, af, user.Id, returnUrl);   // password was right; the session starts after the second step
        await audit.WriteAsync("auth.login", target: email, actor: email);
        if (res == PasswordVerificationResult.SuccessRehashNeeded) { user.PasswordHash = hasher.HashPassword(user, f["password"].ToString()); await db.SaveChangesAsync(http.RequestAborted); }
        await http.SignInAsync(CookieName, new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", user.Id.ToString()) }, CookieName)));
        return Results.Redirect(LocalOrRoot(returnUrl));
    }
}

/// <summary>Daily: removes expired and used-up tokens and authorizations (two weeks after they ended) so the identity tables stay small.</summary>
public sealed class IdentityPruneJob : IJobHandler
{
    public const string JobType = "identity.prune";
    public string Type => JobType;
    public async Task RunAsync(JobContext job, CancellationToken ct)
    {
        var threshold = DateTimeOffset.UtcNow.AddDays(-14);
        await job.Services.GetRequiredService<IOpenIddictTokenManager>().PruneAsync(threshold, ct);
        await job.Services.GetRequiredService<IOpenIddictAuthorizationManager>().PruneAsync(threshold, ct);
        await using var db = new Npgsql.NpgsqlConnection(job.Tenant.ConnectionString); await db.OpenAsync(ct);
        await using var cmd = new Npgsql.NpgsqlCommand("delete from identity.keys where retired_at < now() - interval '30 days'", db);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

public static class IdentityPipeline
{
    /// <summary>After tenancy and tenant schemas: authentication and authorization.</summary>
    public static IApplicationBuilder UseIdentity(this IApplicationBuilder app) => app.UseAuthentication().UseAuthorization();
}
