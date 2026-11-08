using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeskAsk.Platform.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

public class TotpUnitTests
{
    static readonly byte[] Rfc = Encoding.ASCII.GetBytes("12345678901234567890");   // the secret of the RFC 6238 test table
    [Theory]
    [InlineData(59L, "287082")] [InlineData(1111111109L, "081804")] [InlineData(1234567890L, "005924")] [InlineData(2000000000L, "279037")]
    public void MatchesTheRfc6238Vectors(long unixTime, string expected) => Assert.Equal(expected, Totp.Code(Rfc, unixTime / 30));

    [Fact] public void Base32RoundTrips()
    {
        var b = System.Security.Cryptography.RandomNumberGenerator.GetBytes(20);
        Assert.Equal(b, Totp.FromBase32(Totp.Base32(b)));
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Totp.Base32(Rfc));
    }
    [Fact] public void AcceptsDriftOfOneStepAndNoMore()
    {
        var secret = Totp.Base32(Rfc); var now = DateTimeOffset.FromUnixTimeSeconds(1111111109);
        var step = Totp.Step(now);
        Assert.True(Totp.TryVerify(secret, Totp.Code(Rfc, step), now, 0, out _));
        Assert.True(Totp.TryVerify(secret, Totp.Code(Rfc, step - 1), now, 0, out _));
        Assert.True(Totp.TryVerify(secret, Totp.Code(Rfc, step + 1), now, 0, out _));
        Assert.False(Totp.TryVerify(secret, Totp.Code(Rfc, step - 2), now, 0, out _));
        Assert.False(Totp.TryVerify(secret, Totp.Code(Rfc, step + 2), now, 0, out _));
    }
    [Fact] public void ACodeWorksOnlyOnce()
    {
        var secret = Totp.Base32(Rfc); var now = DateTimeOffset.FromUnixTimeSeconds(1111111109);
        Assert.True(Totp.TryVerify(secret, Totp.Code(Rfc, Totp.Step(now)), now, 0, out var used));
        Assert.False(Totp.TryVerify(secret, Totp.Code(Rfc, Totp.Step(now)), now, used, out _));
        Assert.False(Totp.TryVerify(secret, Totp.Code(Rfc, Totp.Step(now) - 1), now, used, out _));   // an older step is no way back either
    }
    [Theory] [InlineData("")] [InlineData("12345")] [InlineData("abcdef")] [InlineData("1234567")]
    public void WrongShapesAreRefused(string code) => Assert.False(Totp.TryVerify(Totp.Base32(Rfc), code, DateTimeOffset.UtcNow, 0, out _));
}

public class TestClock : ITotpClock
{
    public DateTimeOffset Now { get; private set; } = DateTimeOffset.UtcNow;
    public void Advance() => Now = Now.AddSeconds(31);   // always at least one step
}

public class TwoStepFlowTests : IClassFixture<WebApplicationFactory<Program>>
{
    readonly WebApplicationFactory<Program> factory; readonly TestClock clock = new();
    public TwoStepFlowTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b =>
    {
        TestSupport.Configure(b);
        b.ConfigureTestServices(s => s.AddSingleton<ITotpClock>(clock));
    });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static JsonElement Parse(string s) => JsonDocument.Parse(s).RootElement;
    string CodeFor(string secret) => Totp.Code(Totp.FromBase32(secret), Totp.Step(clock.Now));
    static async Task Cleanup()
    {
        await using var db = new NpgsqlConnection(Environment.GetEnvironmentVariable("TEST_PG_CONNECTION")); await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("update identity.users set totp_enabled = false, totp_secret = null, recovery_hashes = '[]', totp_last_step = 0", db);
        await cmd.ExecuteNonQueryAsync();
    }

    // password step of the browser sign-in; returns the client (holding cookies), the authorize address, the PKCE verifier and the answer
    async Task<(HttpClient c, string url, string verifier, HttpResponseMessage resp)> PasswordStep()
    {
        var c = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var verifier = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var url = "/connect/authorize?response_type=code&client_id=web&redirect_uri=" + Uri.EscapeDataString(TestSupport.Redirect) + "&scope=" + Uri.EscapeDataString("openid email profile roles") + "&state=s&code_challenge=" + challenge + "&code_challenge_method=S256";
        var page = await (await c.GetAsync("/account/login?ReturnUrl=" + Uri.EscapeDataString(url))).Content.ReadAsStringAsync();
        var resp = await c.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["__RequestVerificationToken"] = Antiforgery(page), ["returnUrl"] = url, ["email"] = "admin@acme.test", ["password"] = "Correct-Horse-9" }));
        return (c, url, verifier, resp);
    }
    static string Antiforgery(string html) => Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
    static async Task<HttpResponseMessage> SendCode(HttpClient c, string codePageHtml, string code) =>
        await c.PostAsync("/account/login/code", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = Antiforgery(codePageHtml), ["code"] = code }));

    async Task<(string secret, string[] recovery, HttpClient admin)> SwitchOn()
    {
        var admin = await TestSupport.AdminClient(factory);
        var setup = Parse(await (await admin.PostAsync("/api/account/totp/setup", null)).Content.ReadAsStringAsync());
        var secret = setup.GetProperty("secret").GetString()!;
        Assert.StartsWith("otpauth://totp/", setup.GetProperty("otpauthUri").GetString());
        var en = await admin.PostAsync("/api/account/totp/enable", Json(new { code = CodeFor(secret) }));
        Assert.Equal(HttpStatusCode.OK, en.StatusCode);
        var rec = Parse(await en.Content.ReadAsStringAsync()).GetProperty("recoveryCodes").EnumerateArray().Select(x => x.GetString()!).ToArray();
        return (secret, rec, admin);
    }

    [PgFact]
    public async Task SwitchOn_SignInNeedsTheCode_RecoveryCodeWorksOnce_ThenSwitchOff()
    {
        try
        {
            await Cleanup();
            var (secret, recovery, admin) = await SwitchOn();
            Assert.Equal(10, recovery.Length); Assert.Matches("^[a-z2-7]{5}-[a-z2-7]{5}$", recovery[0]);
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/account/totp/setup", null)).StatusCode);   // already on
            var st = Parse(await admin.GetStringAsync("/api/account/security"));
            Assert.True(st.GetProperty("totpEnabled").GetBoolean()); Assert.Equal(10, st.GetProperty("recoveryCodesLeft").GetInt32());

            // password alone no longer signs in
            var (c, url, verifier, pw) = await PasswordStep();
            Assert.Equal(HttpStatusCode.OK, pw.StatusCode);
            var codePage = await pw.Content.ReadAsStringAsync();
            Assert.Contains("name=\"code\"", codePage);
            var bad = await SendCode(c, codePage, "000000");
            Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
            var codePage2 = await bad.Content.ReadAsStringAsync();

            clock.Advance();
            var good = CodeFor(secret);
            var ok = await SendCode(c, codePage2, good);
            Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
            var back = await c.GetAsync(url);                                    // authorize now gives a code, and the ID token says how they signed in
            var q = System.Web.HttpUtility.ParseQueryString(new Uri(back.Headers.Location!.ToString()).Query);
            var tr = await c.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["grant_type"] = "authorization_code", ["code"] = q["code"]!, ["redirect_uri"] = TestSupport.Redirect, ["client_id"] = "web", ["code_verifier"] = verifier }));
            var idt = Parse(await tr.Content.ReadAsStringAsync()).GetProperty("id_token").GetString()!.Split('.')[1];
            var payload = Parse(Encoding.UTF8.GetString(Convert.FromBase64String(idt.Replace('-', '+').Replace('_', '/').PadRight((idt.Length + 3) / 4 * 4, '='))));
            Assert.Equal(new[] { "pwd", "otp" }, payload.GetProperty("amr").EnumerateArray().Select(x => x.GetString()).ToArray());

            // the same code a second time is refused (replay)
            var (c2, _, _, pw2) = await PasswordStep();
            var replay = await SendCode(c2, await pw2.Content.ReadAsStringAsync(), good);
            Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

            // a recovery code signs in once
            var (c3, _, _, pw3) = await PasswordStep();
            var r1 = await SendCode(c3, await pw3.Content.ReadAsStringAsync(), recovery[0]);
            Assert.Equal(HttpStatusCode.Redirect, r1.StatusCode);
            var (c4, _, _, pw4) = await PasswordStep();
            var r2 = await SendCode(c4, await pw4.Content.ReadAsStringAsync(), recovery[0]);
            Assert.Equal(HttpStatusCode.Unauthorized, r2.StatusCode);
            Assert.Equal(9, Parse(await admin.GetStringAsync("/api/account/security")).GetProperty("recoveryCodesLeft").GetInt32());

            // switching off needs the password and a fresh code
            clock.Advance();
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/account/totp/disable", Json(new { password = "wrong", code = CodeFor(secret) }))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/account/totp/disable", Json(new { password = "Correct-Horse-9", code = CodeFor(secret) }))).StatusCode);
            Assert.False(Parse(await admin.GetStringAsync("/api/account/security")).GetProperty("totpEnabled").GetBoolean());

            var audit = Parse(await admin.GetStringAsync("/api/platform/audit?action=auth.&take=200")).EnumerateArray().Select(r => r.GetProperty("action").GetString()).ToList();
            Assert.Contains("auth.2fa_enabled", audit); Assert.Contains("auth.2fa_failed", audit); Assert.Contains("auth.recovery_used", audit); Assert.Contains("auth.2fa_disabled", audit);
        }
        finally { await Cleanup(); }
    }

    [PgFact]
    public async Task WrongCodeWhileSwitchingOnDoesNotSwitchOn()
    {
        try
        {
            await Cleanup();
            var admin = await TestSupport.AdminClient(factory);
            await admin.PostAsync("/api/account/totp/setup", null);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/account/totp/enable", Json(new { code = "000000" }))).StatusCode);
            Assert.False(Parse(await admin.GetStringAsync("/api/account/security")).GetProperty("totpEnabled").GetBoolean());
        }
        finally { await Cleanup(); }
    }

    [PgFact]
    public async Task AdministratorCanResetSomeonesSecondStep()
    {
        try
        {
            await Cleanup();
            var (_, _, admin) = await SwitchOn();
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/admin/users/totp-reset", Json(new { email = "admin@acme.test" }))).StatusCode);
            Assert.False(Parse(await admin.GetStringAsync("/api/account/security")).GetProperty("totpEnabled").GetBoolean());
            Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/admin/users/totp-reset", Json(new { email = "nobody@acme.test" }))).StatusCode);
        }
        finally { await Cleanup(); }
    }

    [PgFact]
    public async Task WhenRequiredAUserWithoutASecondStepIsSentToSwitchItOn()
    {
        try
        {
            await Cleanup();
            var admin = await TestSupport.AdminClient(factory);
            await admin.PutAsync("/api/platform/settings/security.require_2fa", Json(new { value = true }));
            var (c, url, _, pw) = await PasswordStep();
            Assert.Equal(HttpStatusCode.Redirect, pw.StatusCode);                // password is enough to reach the enrolment page ...
            var go = await c.GetAsync(url);                                      // ... but not to get tokens
            Assert.Equal(HttpStatusCode.Redirect, go.StatusCode);
            Assert.StartsWith("/account/security", new Uri("http://x" + (go.Headers.Location!.IsAbsoluteUri ? go.Headers.Location.PathAndQuery : go.Headers.Location.OriginalString)).PathAndQuery);
            var page = await (await c.GetAsync("/account/security?returnUrl=" + Uri.EscapeDataString(url))).Content.ReadAsStringAsync();
            Assert.Contains("Switch on two-step sign-in", page);
        }
        finally
        {
            var admin = await TestSupport.AdminClient(factory);
            await admin.DeleteAsync("/api/platform/settings/security.require_2fa");
            await Cleanup();
        }
    }

    [PgFact]
    public async Task TheAccountApiNeedsASignedInUser()
    {
        var anon = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/account/security")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/account/totp/setup", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/admin/users/totp-reset", Json(new { email = "x@y.z" }))).StatusCode);
    }
}
