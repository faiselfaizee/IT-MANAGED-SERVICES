using System.Net;
using System.Text;
using System.Text.Json;
using DeskAsk.Platform.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

public class AdTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    static readonly string G1 = "0a1b2c3d-0000-4000-8000-000000000001", G2 = "0a1b2c3d-0000-4000-8000-000000000002";
    readonly WebApplicationFactory<Program> factory;

    sealed class FakeLdap : ILdapClient
    {
        public string? TestError; public string Password = "Dir-Password-1"; public string? SeenBase;
        public Task<string?> TestAsync(AdConfig c, CancellationToken ct) { SeenBase = c.BaseDn; return Task.FromResult(TestError); }
        public Task<IReadOnlyList<AdUser>> UsersAsync(AdConfig c, CancellationToken ct) => Task.FromResult<IReadOnlyList<AdUser>>(new[] {
            new AdUser(G1, "Ana Dir", "ana@dir.test", "CN=Ana", true), new AdUser(G2, "Bo Dir", "bo@dir.test", "CN=Bo", false) });
        public Task<bool> AuthenticateAsync(AdConfig c, string guid, string password, CancellationToken ct) => Task.FromResult(guid == G1 && password == Password);
    }
    readonly FakeLdap ldap = new();

    public AdTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b =>
    {
        TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true");
        b.ConfigureServices(s => { s.RemoveAll<ILdapClient>(); s.AddSingleton<ILdapClient>(ldap); });
    });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }

    [PgFact]
    public async Task DirectoryUsersAreImportedAndSignInWithTheirDirectoryPassword()
    {
        var admin = await TestSupport.AdminClient(factory);
        await Sql("delete from core.settings where key = 'identity.ad'; delete from identity.users where \"Email\" like '%@dir.test'");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/platform/ad/users")).StatusCode);   // not set up
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/platform/ad", Json(new { enabled = true, server = "dc01" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/platform/ad", Json(new { filter = "memberOf=x" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/platform/ad", Json(new { enabled = true, server = "dc01.dir.test", baseDn = "DC=dir,DC=test", bindUser = "svc@dir.test", bindPassword = "svc-pass", signIn = true }))).StatusCode);
        var cfg = await J(await admin.GetAsync("/api/platform/ad"));
        Assert.True(cfg.GetProperty("hasPassword").GetBoolean()); Assert.DoesNotContain("svc-pass", cfg.ToString());
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/platform/ad/test", Json(new { }))).StatusCode); Assert.Equal("DC=dir,DC=test", ldap.SeenBase);
        ldap.TestError = "The directory refused the user name or password.";
        var bad = await admin.PostAsync("/api/platform/ad/test", Json(new { })); Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode); Assert.Contains("refused", await bad.Content.ReadAsStringAsync());

        var items = (await J(await admin.GetAsync("/api/platform/ad/users"))).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        var imp = await J(await admin.PostAsync("/api/platform/ad/import", Json(new { role = "engineer", users = items.Select(x => new { id = x.GetProperty("id").GetString(), email = x.GetProperty("email").GetString(), name = x.GetProperty("name").GetString() }) })));
        Assert.Equal(2, imp.GetProperty("created").GetInt32());

        // the person signs in with the directory password; a wrong one, an unknown person and a local password they never knew do not work
        await TestSupport.ClientFor(factory, "ana@dir.test", "Dir-Password-1");
        await Assert.ThrowsAnyAsync<Exception>(() => TestSupport.ClientFor(factory, "ana@dir.test", "wrong-password-1"));
        await Assert.ThrowsAnyAsync<Exception>(() => TestSupport.ClientFor(factory, "bo@dir.test", "Dir-Password-1"));

        // switched off, directory passwords stop working at once
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/platform/ad", Json(new { signIn = false }))).StatusCode);
        await Assert.ThrowsAnyAsync<Exception>(() => TestSupport.ClientFor(factory, "ana@dir.test", "Dir-Password-1"));
    }

    [PgFact]
    public async Task ALocalAccountLinkedToTheDirectoryStopsAcceptingItsOldLocalPassword()
    {
        var admin = await TestSupport.AdminClient(factory);
        await Sql("delete from core.settings where key = 'identity.ad'; delete from identity.users where \"Email\" like '%@dir.test'");
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/platform/ad", Json(new { enabled = true, server = "dc01.dir.test", baseDn = "DC=dir,DC=test", bindUser = "svc@dir.test", bindPassword = "svc-pass", signIn = true }))).StatusCode);
        var made = await admin.PostAsync("/api/platform/users", Json(new { email = "ana@dir.test", name = "Ana", role = "engineer", password = "Local-Password-1" }));
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        await TestSupport.ClientFor(factory, "ana@dir.test", "Local-Password-1");                       // a plain local account signs in locally

        var imp = await J(await admin.PostAsync("/api/platform/ad/import", Json(new { role = "engineer", users = new[] { new { id = G1, email = "ana@dir.test", name = "Ana" } } })));
        Assert.Equal(1, imp.GetProperty("linked").GetInt32());

        await Assert.ThrowsAnyAsync<Exception>(() => TestSupport.ClientFor(factory, "ana@dir.test", "Local-Password-1"));   // the old local password is gone
        await TestSupport.ClientFor(factory, "ana@dir.test", "Dir-Password-1");                         // the directory decides now

        var id = (await J(await admin.GetAsync("/api/platform/users"))).EnumerateArray().First(x => x.GetProperty("email").GetString() == "ana@dir.test").GetProperty("id").GetString();
        var reset = await admin.PostAsync($"/api/platform/users/{id}/password", Json(new { password = "Another-Password-1" }));
        Assert.Equal(HttpStatusCode.BadRequest, reset.StatusCode); Assert.Contains("Active Directory", await reset.Content.ReadAsStringAsync());
        await Assert.ThrowsAnyAsync<Exception>(() => TestSupport.ClientFor(factory, "ana@dir.test", "Another-Password-1"));

        // directory sign-in switched off: nobody gets in with a local password either
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/platform/ad", Json(new { signIn = false }))).StatusCode);
        await Assert.ThrowsAnyAsync<Exception>(() => TestSupport.ClientFor(factory, "ana@dir.test", "Dir-Password-1"));
        await Assert.ThrowsAnyAsync<Exception>(() => TestSupport.ClientFor(factory, "ana@dir.test", "Local-Password-1"));
    }
}
