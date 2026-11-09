using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

public static class TestSupport
{
    public const string Redirect = "http://localhost/cb";

    public static void Configure(IWebHostBuilder b)
    {
        b.UseSetting("Platform:DevTenantConnection", Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "");
        b.UseSetting("Platform:BootstrapAdmin:Email", "admin@acme.test");
        b.UseSetting("Platform:BootstrapAdmin:Password", "Correct-Horse-9");
        b.UseSetting("Platform:Roles", "web");   // no background worker unless a test asks for it
        b.UseSetting("Identity:AllowInsecureHttp", "true");
        b.UseSetting("Identity:Clients:0:Id", "web");
        b.UseSetting("Identity:Clients:0:RedirectUris:0", Redirect);
    }

    static string B64u(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Signs in as the administrator (code flow with PKCE) and returns a client that sends the access token.</summary>
    public static async Task<HttpClient> AdminClient(WebApplicationFactory<Program> factory)
    {
        var c = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var verifier = B64u(RandomNumberGenerator.GetBytes(48));
        var challenge = B64u(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var url = "/connect/authorize?response_type=code&client_id=web&redirect_uri=" + Uri.EscapeDataString(Redirect) + "&scope=" + Uri.EscapeDataString("openid email profile roles")
                  + "&state=s&code_challenge=" + challenge + "&code_challenge_method=S256";
        var page = await (await c.GetAsync("/account/login?ReturnUrl=" + Uri.EscapeDataString(url))).Content.ReadAsStringAsync();
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        var login = await c.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["__RequestVerificationToken"] = token, ["returnUrl"] = url, ["email"] = "admin@acme.test", ["password"] = "Correct-Horse-9" }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var back = await c.GetAsync(url);
        var code = System.Web.HttpUtility.ParseQueryString(new Uri(back.Headers.Location!.ToString()).Query)["code"]!;
        var tr = await c.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = Redirect, ["client_id"] = "web", ["code_verifier"] = verifier }));
        var access = JsonDocument.Parse(await tr.Content.ReadAsStringAsync()).RootElement.GetProperty("access_token").GetString();
        var api = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
        return api;
    }
}
