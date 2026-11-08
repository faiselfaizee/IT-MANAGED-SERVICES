using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]   // every test host prepares the same database

public class CoreTests : IClassFixture<WebApplicationFactory<Program>>
{
    const string Redirect = "http://localhost/cb";
    readonly WebApplicationFactory<Program> factory;
    public CoreTests(WebApplicationFactory<Program> f) =>
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

    /// <summary>Signs in as the administrator (code flow with PKCE) and returns a client that sends the access token.</summary>
    async Task<HttpClient> AdminClient()
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

    static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    [PgFact]
    public async Task AuditAndSettingsNeedAnAdministrator()
    {
        var anon = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/platform/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/platform/settings")).StatusCode);
    }

    [PgFact]
    public async Task SignInIsAuditedAndTheChainVerifies()
    {
        var api = await AdminClient();
        var rows = JsonDocument.Parse(await api.GetStringAsync("/api/platform/audit?action=auth.")).RootElement;
        Assert.Contains(rows.EnumerateArray(), r => r.GetProperty("action").GetString() == "auth.login" && r.GetProperty("actor").GetString() == "admin@acme.test");
        var v = JsonDocument.Parse(await api.GetStringAsync("/api/platform/audit/verify")).RootElement;
        Assert.True(v.GetProperty("ok").GetBoolean());
        Assert.True(v.GetProperty("count").GetInt64() >= 1);
    }

    [PgFact]
    public async Task ChangingAnAuditRowIsDetected()
    {
        var api = await AdminClient();
        await using (var db = new NpgsqlConnection(Environment.GetEnvironmentVariable("TEST_PG_CONNECTION")))
        {
            await db.OpenAsync();
            await using var cmd = new NpgsqlCommand("update core.audit set detail = 'tampered' where id = (select min(id) from core.audit)", db);
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
        }
        var v = JsonDocument.Parse(await api.GetStringAsync("/api/platform/audit/verify")).RootElement;
        Assert.False(v.GetProperty("ok").GetBoolean());
        await using (var db = new NpgsqlConnection(Environment.GetEnvironmentVariable("TEST_PG_CONNECTION")))
        {   // leave the chain whole for the other tests: drop the rows and start again
            await db.OpenAsync();
            await using var cmd = new NpgsqlCommand("truncate core.audit restart identity", db);
            await cmd.ExecuteNonQueryAsync();
        }
        Assert.True(JsonDocument.Parse(await api.GetStringAsync("/api/platform/audit/verify")).RootElement.GetProperty("ok").GetBoolean());
    }

    [PgFact]
    public async Task SettingsRoundTripAndAreAudited()
    {
        var api = await AdminClient();
        Assert.Equal(HttpStatusCode.OK, (await api.PutAsync("/api/platform/settings/company.name", Body("{\"value\":\"Acme Ltd\"}"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PutAsync("/api/platform/settings/mail.limits", Body("{\"value\":{\"perHour\":30}}"))).StatusCode);
        Assert.Equal("\"Acme Ltd\"", await api.GetStringAsync("/api/platform/settings/company.name"));
        var all = JsonDocument.Parse(await api.GetStringAsync("/api/platform/settings?prefix=mail.")).RootElement;
        Assert.Equal(30, all.GetProperty("mail.limits").GetProperty("perHour").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PutAsync("/api/platform/settings/Bad Key", Body("{\"value\":1}"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PutAsync("/api/platform/settings/x", Body("{\"nope\":1}"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.DeleteAsync("/api/platform/settings/company.name")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, await Status(api, "/api/platform/settings/company.name"));
        var rows = JsonDocument.Parse(await api.GetStringAsync("/api/platform/audit?action=settings.")).RootElement;
        Assert.Contains(rows.EnumerateArray(), r => r.GetProperty("action").GetString() == "settings.set" && r.GetProperty("target").GetString() == "company.name");
        Assert.Contains(rows.EnumerateArray(), r => r.GetProperty("action").GetString() == "settings.delete");
    }

    static async Task<HttpStatusCode> Status(HttpClient c, string path) => (await c.GetAsync(path)).StatusCode;
}
