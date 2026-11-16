using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeskAsk.Platform.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

/// <summary>Sign in with Google (Workspace domains only, account creation, refusing personal accounts) and importing from the Workspace directory.</summary>
[Collection("sso")]
public class GoogleSsoTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    const string Cid = "1234-abc.apps.googleusercontent.com";
    readonly WebApplicationFactory<Program> factory;
    public GoogleSsoTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }
    static async Task<string> Text(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); return (await c.ExecuteScalarAsync())?.ToString() ?? ""; }
    static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    static byte[] UnB64(string s) { s = s.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4)); }

    /// <summary>Google as DeskAsk sees it: the token address (sign-in codes and service account assertions) and the directory.</summary>
    sealed class FakeGoogle : HttpMessageHandler
    {
        public string Nonce = "", Sub = "1001", Email = "new@gws.test", Hd = "gws.test"; public RSA? ServiceKey; public string AssertionSub = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var url = req.RequestUri!.ToString();
            HttpResponseMessage Ok(object o) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };
            if (url.StartsWith("https://oauth2.googleapis.com/token"))
            {
                var form = System.Web.HttpUtility.ParseQueryString(await req.Content!.ReadAsStringAsync(ct));
                if (form["grant_type"] == "urn:ietf:params:oauth:grant-type:jwt-bearer")
                {   // the assertion must be signed with the service account's key and ask for the read-only directory scope
                    var p = form["assertion"]!.Split('.');
                    if (ServiceKey == null || !ServiceKey.VerifyData(Encoding.ASCII.GetBytes(p[0] + "." + p[1]), UnB64(p[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                        return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"invalid_grant\",\"error_description\":\"Invalid JWT Signature.\"}") };
                    var c = JsonDocument.Parse(UnB64(p[1])).RootElement;
                    AssertionSub = c.GetProperty("sub").GetString()!;
                    Assert.Equal("https://www.googleapis.com/auth/admin.directory.user.readonly", c.GetProperty("scope").GetString());
                    return Ok(new { access_token = "dir-token" });
                }
                if (form["code_verifier"] is not { Length: >= 43 }) return new HttpResponseMessage(HttpStatusCode.BadRequest);
                var claims = JsonSerializer.Serialize(new { iss = "https://accounts.google.com", aud = Cid, sub = Sub, email = Email, email_verified = true, hd = Hd, name = "New Person", nonce = Nonce, exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds() });
                return Ok(new { id_token = B64(Encoding.UTF8.GetBytes("{}")) + "." + B64(Encoding.UTF8.GetBytes(claims)) + ".sig", access_token = "x" });
            }
            if (url.StartsWith("https://admin.googleapis.com/admin/directory/v1/users"))
            {
                if (req.Headers.Authorization?.Parameter != "dir-token") return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                return Ok(new { users = new object[] {
                    new { id = "1001", primaryEmail = "new@gws.test", name = new { fullName = "New Person" }, suspended = false },
                    new { id = "1002", primaryEmail = "rita@gws.test", name = new { fullName = "Rita Iyer" }, suspended = false },
                    new { id = "1003", primaryEmail = "old@gws.test", name = new { fullName = "Old Leaver" }, suspended = true } } });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    async Task<HttpResponseMessage> SignIn(FakeGoogle g)
    {
        var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var start = await browser.GetAsync("/account/sso/google?returnUrl=%2F%23%2Fservicedesk");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var loc = new Uri(start.Headers.Location!.ToString()); var q = System.Web.HttpUtility.ParseQueryString(loc.Query);
        Assert.Equal("https://accounts.google.com/o/oauth2/v2/auth", loc.GetLeftPart(UriPartial.Path));
        Assert.Equal((Cid, "S256", "gws.test"), (q["client_id"], q["code_challenge_method"], q["hd"]));
        g.Nonce = q["nonce"]!;
        return await browser.GetAsync("/account/sso/google/callback?code=abc&state=" + Uri.EscapeDataString(q["state"]!));
    }

    [PgFact]
    public async Task GoogleSignInKeepsToTheWorkspaceAndImportsTheDirectory()
    {
        var g = new FakeGoogle(); SsoEndpoints.TestHandler = g;
        try
        {
            var admin = await TestSupport.AdminClient(factory);
            await Sql("delete from core.settings where key = 'identity.google'; delete from identity.users where \"Email\" like '%@gws.test'");
            Assert.DoesNotContain("Sign in with Google", await factory.CreateClient().GetStringAsync("/account/login"));
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/platform/sso/google", Json(new { clientId = "not-google" }))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/platform/sso/google", Json(new { enabled = true, clientId = Cid }))).StatusCode);   // no secret
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/platform/sso/google", Json(new { clientId = Cid, secret = "gs3cret", autoCreate = true }))).StatusCode);   // creating needs domains
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/platform/sso/google", Json(new { enabled = true, clientId = Cid, secret = "gs3cret", domains = "gws.test", autoCreate = true, defaultRole = "engineer" }))).StatusCode);
            var cfg = await J(await admin.GetAsync("/api/platform/sso/google"));
            Assert.True(cfg.GetProperty("hasSecret").GetBoolean()); Assert.DoesNotContain("gs3cret", cfg.ToString());
            Assert.DoesNotContain("gs3cret", await Text("select value from core.settings where key = 'identity.google'"));
            Assert.Contains("Sign in with Google", await factory.CreateClient().GetStringAsync("/account/login"));

            // a Workspace person without an account is created as an engineer and lands where they wanted
            var ok = await SignIn(g);
            Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode); Assert.Equal("/#/servicedesk", ok.Headers.Location!.ToString());
            Assert.Equal("engineer|google:1001", await Text("select \"Role\" || '|' || external_id from identity.users where \"Email\" = 'new@gws.test'"));
            // a personal Gmail account (no Workspace domain) is refused even with a known address
            g.Hd = ""; g.Sub = "777";
            var personal = await SignIn(g);
            Assert.Equal(HttpStatusCode.Forbidden, personal.StatusCode); Assert.Contains("Only accounts of gws.test", await personal.Content.ReadAsStringAsync());
            g.Hd = "gws.test"; g.Sub = "1001";

            // importing: the service account's key is checked, the assertion is signed with it and acts as the administrator
            using var rsa = RSA.Create(2048); g.ServiceKey = rsa;
            var keyFile = JsonSerializer.Serialize(new { type = "service_account", client_email = "deskask@proj.iam.gserviceaccount.com", private_key = rsa.ExportPkcs8PrivateKeyPem() });
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/platform/sso/google", Json(new { serviceAccount = "{\"type\":\"user\"}" }))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/platform/sso/google", Json(new { serviceAccount = keyFile, adminEmail = "admin@gws.test" }))).StatusCode);
            Assert.Equal("deskask@proj.iam.gserviceaccount.com", (await J(await admin.GetAsync("/api/platform/sso/google"))).GetProperty("serviceAccount").GetString());
            Assert.DoesNotContain("PRIVATE KEY", await Text("select value from core.settings where key = 'identity.google'"));
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/platform/sso/google/test", Json(new { }))).StatusCode);
            Assert.Equal("admin@gws.test", g.AssertionSub);
            var users = (await J(await admin.GetAsync("/api/platform/sso/google/users"))).GetProperty("items");
            Assert.Equal(3, users.GetArrayLength());
            Assert.True(users[0].GetProperty("exists").GetBoolean());          // new@gws.test signed in above
            Assert.False(users[2].GetProperty("enabled").GetBoolean());        // suspended
            var imp = await J(await admin.PostAsync("/api/platform/sso/google/import", Json(new { role = "viewer", users = new[] { new { id = "1002", email = "rita@gws.test", name = "Rita Iyer" } } })));
            Assert.Equal(1, imp.GetProperty("created").GetInt32());
            Assert.Equal("viewer|google:1002", await Text("select \"Role\" || '|' || external_id from identity.users where \"Email\" = 'rita@gws.test'"));

            // a wrong key is reported in words
            g.ServiceKey = RSA.Create(2048);
            var bad = await admin.PostAsync("/api/platform/sso/google/test", Json(new { }));
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode); Assert.Contains("domain-wide delegation", await bad.Content.ReadAsStringAsync());
        }
        finally { SsoEndpoints.TestHandler = null; }
    }
}
