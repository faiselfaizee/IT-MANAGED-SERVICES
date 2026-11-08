using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DeskAsk.Platform.Tenancy;
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
        s.AddSingleton<TenantInitializer>();
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
                 .SetUserInfoEndpointUris("/connect/userinfo").SetEndSessionEndpointUris("/connect/logout");
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
        r.MapGet("/api/platform/me", (ClaimsPrincipal u) => Results.Ok(new
        {
            sub = u.FindFirstValue(Claims.Subject), email = u.FindFirstValue(Claims.Email),
            name = u.FindFirstValue(Claims.Name), role = u.FindFirstValue(Claims.Role), tenant = u.FindFirstValue("tenant"),
        })).RequireAuthorization();
    }

    // ---------------------------------------------------------------- OpenID Connect endpoints

    static async Task<IResult> Authorize(HttpContext http, IdentityDb db, ITenantContext tenant)
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
        var p = Principal(user, tenant.Require());
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
        var p = Principal(user, tenant.Require());    // claims are rebuilt from the database: a role change shows at the next refresh
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

    static async Task<IResult> Logout(HttpContext http)
    {
        await http.SignOutAsync(CookieName);
        return Results.SignOut(new AuthenticationProperties { RedirectUri = "/" }, new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme });
    }

    static IResult Fail(string error, string description) =>
        Results.Forbid(new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }), new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme });

    static ClaimsPrincipal Principal(UserAccount u, TenantInfo t)
    {
        var id = new ClaimsIdentity("DeskAsk", Claims.Name, Claims.Role);
        id.SetClaim(Claims.Subject, u.Id.ToString());
        id.SetClaim(Claims.Email, u.Email);
        id.SetClaim(Claims.Name, u.Name);
        id.SetClaim(Claims.Role, u.Role);
        id.SetClaim("tenant", t.Id);
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

    static string Html(string body) => "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>Sign in</title>"
        + "<style>body{font-family:system-ui,sans-serif;background:#f4f5f7;display:flex;justify-content:center;padding-top:10vh}form{background:#fff;padding:28px;border-radius:10px;width:320px;box-shadow:0 2px 12px #0002}"
        + "input,button{width:100%;box-sizing:border-box;padding:10px;margin:6px 0;font-size:16px}button{background:#14213d;color:#fff;border:0;border-radius:6px}.e{color:#b00020}</style></head><body>" + body + "</body></html>";

    static string LocalOrRoot(string? url) =>
        !string.IsNullOrEmpty(url) && url[0] == '/' && !url.StartsWith("//") && !url.StartsWith("/\\") ? url : "/";

    static IResult Page(HttpContext http, IAntiforgery af, string? returnUrl, string error = "", int status = 200)
    {
        var t = af.GetAndStoreTokens(http);
        var body = "<form method=\"post\" action=\"/account/login\"><h2>Sign in</h2>"
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

    static async Task<IResult> LoginPost(HttpContext http, IAntiforgery af, IdentityDb db, IPasswordHasher<UserAccount> hasher, ITenantContext tenant, ILogger<IdentityModule> log)
    {
        try { await af.ValidateRequestAsync(http); } catch (AntiforgeryValidationException) { return Results.BadRequest(new { error = "invalid_form" }); }
        var f = await http.Request.ReadFormAsync();
        var email = f["email"].ToString().Trim().ToLowerInvariant();
        var returnUrl = f["returnUrl"].ToString();
        var key = tenant.Require().Id + "|" + email;
        if (fails.TryGetValue(key, out var fl) && fl.n >= MaxFails && fl.until > DateTime.UtcNow)
            return Page(http, af, returnUrl, "Too many attempts. Try again in a few minutes.", StatusCodes.Status429TooManyRequests);

        var user = await db.Users.FirstOrDefaultAsync(x => x.Email == email && x.Active, http.RequestAborted);
        var res = hasher.VerifyHashedPassword(user ?? Dummy, user?.PasswordHash ?? hasher.HashPassword(Dummy, "x"), f["password"].ToString());   // same work whether or not the account exists
        if (user == null || res == PasswordVerificationResult.Failed)
        {
            fails.AddOrUpdate(key, (1, DateTime.UtcNow.AddMinutes(15)), (_, o) => (o.until > DateTime.UtcNow ? o.n + 1 : 1, DateTime.UtcNow.AddMinutes(15)));
            log.LogWarning("Failed sign-in for {Email} on {Tenant}", email, tenant.Require().Id);
            return Page(http, af, returnUrl, "Email or password is not correct.", StatusCodes.Status401Unauthorized);
        }
        fails.TryRemove(key, out _);
        if (res == PasswordVerificationResult.SuccessRehashNeeded) { user.PasswordHash = hasher.HashPassword(user, f["password"].ToString()); await db.SaveChangesAsync(http.RequestAborted); }
        await http.SignInAsync(CookieName, new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", user.Id.ToString()) }, CookieName)));
        return Results.Redirect(LocalOrRoot(returnUrl));
    }
}

public static class IdentityPipeline
{
    /// <summary>After tenancy: prepares the customer's identity schema, then authentication and authorization.</summary>
    public static IApplicationBuilder UseIdentity(this IApplicationBuilder app)
    {
        app.Use(async (http, next) =>
        {
            if (!http.Request.Path.StartsWithSegments("/health"))
            {
                var t = http.RequestServices.GetRequiredService<ITenantContext>().Current;
                if (t != null && t.ConnectionString != "") await http.RequestServices.GetRequiredService<TenantInitializer>().EnsureAsync(http.RequestServices, t, http.RequestAborted);
            }
            await next();
        });
        return app.UseAuthentication().UseAuthorization();
    }
}
