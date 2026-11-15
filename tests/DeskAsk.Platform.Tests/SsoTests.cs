using System.Net;
using System.Text;
using System.Text.Json;
using DeskAsk.Platform.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

public class SsoTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    const string Tid = "11111111-2222-3333-4444-555555555555", Cid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    readonly WebApplicationFactory<Program> factory;
    public SsoTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }
    static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Microsoft, as far as DeskAsk can see: a token endpoint and a user list.</summary>
    sealed class FakeMicrosoft : HttpMessageHandler
    {
        public string Nonce = "", Oid = "oid-1", Email = "sam@acme.test", Name = "Sam Sharma", TenantInToken = Tid; public int GraphOk = 1;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var url = req.RequestUri!.ToString();
            HttpResponseMessage Ok(object o) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };
            if (url.Contains("/oauth2/v2.0/token"))
            {
                var form = req.Content!.ReadAsStringAsync(ct).Result;
                if (form.Contains("client_credentials")) return Task.FromResult(Ok(new { access_token = "graph-token" }));
                var claims = JsonSerializer.Serialize(new { tid = TenantInToken, aud = Cid, nonce = Nonce, oid = Oid, email = Email, name = Name, exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds() });
                return Task.FromResult(Ok(new { id_token = B64("{}") + "." + B64(claims) + ".sig" }));
            }
            if (url.Contains("/v1.0/users"))
                return Task.FromResult(GraphOk == 1 ? Ok(new { value = new object[] {
                    new { id = "oid-1", displayName = "Sam Sharma", mail = "sam@acme.test", userPrincipalName = "sam@acme.test", accountEnabled = true, userType = "Member" },
                    new { id = "oid-2", displayName = "Priya Rao", mail = (string?)null, userPrincipalName = "priya@acme.test", accountEnabled = true, userType = "Member" } } })
                    : new HttpResponseMessage(HttpStatusCode.Forbidden));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    async Task<HttpResponseMessage> SignInWithMicrosoft(FakeMicrosoft ms, string? ret = null)
    {
        var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var start = await browser.GetAsync("/account/sso/microsoft" + (ret != null ? "?returnUrl=" + Uri.EscapeDataString(ret) : ""));
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var loc = new Uri(start.Headers.Location!.ToString()); var q = System.Web.HttpUtility.ParseQueryString(loc.Query);
        Assert.Contains($"/{Tid}/oauth2/v2.0/authorize", loc.AbsolutePath); Assert.Equal(Cid, q["client_id"]); Assert.Equal("S256", q["code_challenge_method"]);
        ms.Nonce = q["nonce"]!;
        return await browser.GetAsync("/account/sso/microsoft/callback?code=abc&state=" + Uri.EscapeDataString(q["state"]!));
    }

    [PgFact]
    public async Task MicrosoftSignInMatchesCreatesAndImports()
    {
        var ms = new FakeMicrosoft(); SsoEndpoints.TestHandler = ms;
        try
        {
            var admin = await TestSupport.AdminClient(factory);
            await Sql("delete from core.settings where key = 'identity.entra'; delete from identity.users where \"Email\" like '%@acme.test' and \"Email\" <> 'admin@acme.test'");

            // off until an administrator sets it up
            Assert.DoesNotContain("Sign in with Microsoft", await factory.CreateClient().GetStringAsync("/account/login"));
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/platform/sso", Json(new { enabled = true, tenantId = Tid }))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/platform/sso", Json(new { tenantId = "not-a-guid" }))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/platform/sso", Json(new { enabled = true, tenantId = Tid, clientId = Cid, secret = "s3cret", domains = "acme.test", autoCreate = false }))).StatusCode);
            var cfg = await J(await admin.GetAsync("/api/platform/sso"));
            Assert.True(cfg.GetProperty("hasSecret").GetBoolean()); Assert.DoesNotContain("s3cret", cfg.ToString());
            Assert.Contains("Sign in with Microsoft", await factory.CreateClient().GetStringAsync("/account/login"));
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/platform/sso/test", Json(new { }))).StatusCode);

            // someone without an account is refused while creation is off
            var no = await SignInWithMicrosoft(ms);
            Assert.Equal(HttpStatusCode.Forbidden, no.StatusCode); Assert.Contains("no DeskAsk account", await no.Content.ReadAsStringAsync());

            // an existing person is matched by email and linked, and lands on the page they asked for
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync("/api/platform/users", Json(new { email = "sam@acme.test", name = "Sam", role = "engineer", password = "Long-Password-1" }))).StatusCode);
            var ok = await SignInWithMicrosoft(ms, "/assets");
            Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode); Assert.Equal("/assets", ok.Headers.Location!.ToString());
            Assert.Contains("dk.session", string.Join(";", ok.Headers.GetValues("Set-Cookie")));

            // a token for another organisation, or a state that was not ours, is refused
            ms.TenantInToken = "99999999-2222-3333-4444-555555555555";
            Assert.Equal(HttpStatusCode.BadRequest, (await SignInWithMicrosoft(ms)).StatusCode);
            ms.TenantInToken = Tid;
            Assert.Equal(HttpStatusCode.BadRequest, (await factory.CreateClient().GetAsync("/account/sso/microsoft/callback?code=x&state=y")).StatusCode);

            // a disabled account cannot come in
            var sam = (await J(await admin.GetAsync("/api/platform/users"))).EnumerateArray().Single(x => x.GetProperty("email").GetString() == "sam@acme.test").GetProperty("id").GetString();
            Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsync("/api/platform/users/" + sam, Json(new { active = false }))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SignInWithMicrosoft(ms)).StatusCode);

            // creation at first sign-in, only for the listed domains, never as administrator
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/platform/sso", Json(new { defaultRole = "admin" }))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/platform/sso", Json(new { autoCreate = true, defaultRole = "viewer" }))).StatusCode);
            ms.Oid = "oid-9"; ms.Email = "new@other.test";
            Assert.Equal(HttpStatusCode.Forbidden, (await SignInWithMicrosoft(ms)).StatusCode);   // domain not listed
            ms.Email = "new@acme.test"; ms.Name = "New Person";
            Assert.Equal(HttpStatusCode.Redirect, (await SignInWithMicrosoft(ms)).StatusCode);
            var created = (await J(await admin.GetAsync("/api/platform/users"))).EnumerateArray().Single(x => x.GetProperty("email").GetString() == "new@acme.test");
            Assert.Equal("viewer", created.GetProperty("role").GetString());

            // the directory list marks people who already have an account; importing creates the rest
            var list = await J(await admin.GetAsync("/api/platform/sso/entra/users"));
            var items = list.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(2, items.Count);
            Assert.True(items.Single(x => x.GetProperty("email").GetString() == "sam@acme.test").GetProperty("exists").GetBoolean());
            Assert.Contains(items, x => x.GetProperty("email").GetString() == "priya@acme.test");   // no mail address: the sign-in name is used
            var imp = await J(await admin.PostAsync("/api/platform/sso/entra/import", Json(new { role = "engineer", users = items.Select(x => new { id = x.GetProperty("id").GetString(), email = x.GetProperty("email").GetString(), name = x.GetProperty("name").GetString() }) })));
            Assert.Equal(1, imp.GetProperty("created").GetInt32());
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/platform/sso/entra/import", Json(new { role = "nope", users = new[] { new { id = "x", email = "a@b.c", name = "A" } } }))).StatusCode);
            ms.GraphOk = 0;
            var denied = await admin.GetAsync("/api/platform/sso/entra/users");
            Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode); Assert.Contains("User.Read.All", await denied.Content.ReadAsStringAsync());
        }
        finally { SsoEndpoints.TestHandler = null; }
    }
}
