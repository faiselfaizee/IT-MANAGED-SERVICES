using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

/// <summary>Runs only when TEST_PG_CONNECTION points at an empty PostgreSQL database (CI provides one).</summary>
public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute() { if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TEST_PG_CONNECTION"))) Skip = "TEST_PG_CONNECTION is not set"; }
}

public class IdentityFlowTests : IClassFixture<WebApplicationFactory<Program>>
{
    const string Redirect = "http://localhost/cb";
    readonly WebApplicationFactory<Program> factory;
    public IdentityFlowTests(WebApplicationFactory<Program> f) =>
        factory = f.WithWebHostBuilder(b =>
        {
            b.UseSetting("Platform:DevTenantConnection", Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "");
            b.UseSetting("Platform:BootstrapAdmin:Email", "admin@acme.test");
            b.UseSetting("Platform:BootstrapAdmin:Password", "Correct-Horse-9");
            b.UseSetting("Identity:AllowInsecureHttp", "true");
            b.UseSetting("Identity:Clients:0:Id", "web");
            b.UseSetting("Identity:Clients:0:RedirectUris:0", Redirect);
        });

    static string B64u(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    static (string verifier, string challenge) Pkce()
    {
        var v = B64u(RandomNumberGenerator.GetBytes(48));
        return (v, B64u(SHA256.HashData(Encoding.ASCII.GetBytes(v))));
    }
    static string AuthUrl(string challenge, bool pkce = true) =>
        "/connect/authorize?response_type=code&client_id=web&redirect_uri=" + Uri.EscapeDataString(Redirect) +
        "&scope=" + Uri.EscapeDataString("openid email profile roles") + "&state=st1&nonce=n1" +
        (pkce ? "&code_challenge=" + challenge + "&code_challenge_method=S256" : "");

    HttpClient NewClient() => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    static async Task<HttpResponseMessage> Login(HttpClient c, string returnUrl, string password)
    {
        var page = await (await c.GetAsync("/account/login?ReturnUrl=" + Uri.EscapeDataString(returnUrl))).Content.ReadAsStringAsync();
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEqual("", token);
        return await c.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["__RequestVerificationToken"] = token, ["returnUrl"] = returnUrl, ["email"] = "admin@acme.test", ["password"] = password }));
    }

    static string PathOf(Uri u) => u.IsAbsoluteUri ? u.PathAndQuery : u.OriginalString;
    static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;
    static JsonElement Jwt(string jwt) => Json(Encoding.UTF8.GetString(Convert.FromBase64String(jwt.Split('.')[1].Replace('-', '+').Replace('_', '/').PadRight((jwt.Split('.')[1].Length + 3) / 4 * 4, '='))));

    [PgFact]
    public async Task DiscoveryPublishesEndpointsAndKeys()
    {
        var c = NewClient();
        var d = Json(await c.GetStringAsync("/.well-known/openid-configuration"));
        Assert.Equal("http://localhost", d.GetProperty("issuer").GetString()!.TrimEnd('/'));
        Assert.Contains("S256", d.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(x => x.GetString()));
        var jwks = Json(await c.GetStringAsync("/.well-known/jwks"));
        Assert.True(jwks.GetProperty("keys").GetArrayLength() > 0);
    }

    [PgFact]
    public async Task FullSignInGivesTokensAndTheCodeWorksOnce()
    {
        var c = NewClient();
        var (verifier, challenge) = Pkce();
        var url = AuthUrl(challenge);

        var first = await c.GetAsync(url);                                   // not signed in: sent to the sign-in page
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.StartsWith("/account/login", PathOf(first.Headers.Location!));

        var bad = await Login(c, url, "wrong-password");
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);

        var ok = await Login(c, url, "Correct-Horse-9");
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);

        var back = await c.GetAsync(url);                                    // signed in: back to the app with a code
        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        Assert.StartsWith(Redirect, back.Headers.Location!.ToString());
        var q = System.Web.HttpUtility.ParseQueryString(new Uri(back.Headers.Location.ToString()).Query);
        Assert.Equal("st1", q["state"]);
        var code = q["code"]; Assert.False(string.IsNullOrEmpty(code));

        var form = () => new FormUrlEncodedContent(new Dictionary<string, string>
        { ["grant_type"] = "authorization_code", ["code"] = code!, ["redirect_uri"] = Redirect, ["client_id"] = "web", ["code_verifier"] = verifier });
        var tr = await c.PostAsync("/connect/token", form());
        var tok = Json(await tr.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, tr.StatusCode);

        var idc = Jwt(tok.GetProperty("id_token").GetString()!);
        Assert.Equal("admin@acme.test", idc.GetProperty("email").GetString());
        Assert.Equal("n1", idc.GetProperty("nonce").GetString());
        Assert.Equal("dev", idc.GetProperty("tenant").GetString());
        Assert.Equal("admin", idc.GetProperty("role").GetString());

        var api = NewClient();                                               // fresh client: no cookie, bearer only
        var ar = new HttpRequestMessage(HttpMethod.Get, "/api/platform/me");
        ar.Headers.Authorization = new("Bearer", tok.GetProperty("access_token").GetString());
        var me = await api.SendAsync(ar);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal("admin@acme.test", Json(await me.Content.ReadAsStringAsync()).GetProperty("email").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/api/platform/me")).StatusCode);

        var again = await c.PostAsync("/connect/token", form());             // same code again
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("invalid_grant", Json(await again.Content.ReadAsStringAsync()).GetProperty("error").GetString());
    }

    [PgFact]
    public async Task RequestWithoutPkceIsRefused()
    {
        var c = NewClient();
        var r = await c.GetAsync(AuthUrl("", pkce: false));
        // OpenIddict answers a bad request with an error page (400) or an error redirect; either way: no code
        Assert.DoesNotContain("code=", r.Headers.Location?.ToString() ?? "");
        Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
    }

    [PgFact]
    public async Task UnregisteredReturnAddressIsNeverRedirectedTo()
    {
        var c = NewClient();
        var (_, ch) = Pkce();
        var r = await c.GetAsync(AuthUrl(ch).Replace(Uri.EscapeDataString(Redirect), Uri.EscapeDataString("https://evil.example/steal")));
        Assert.DoesNotContain("evil.example", r.Headers.Location?.ToString() ?? "");
    }
}
