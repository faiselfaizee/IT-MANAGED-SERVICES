using System.Net;
using System.Text;
using System.Text.Json;
using DeskAsk.Platform;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

/// <summary>The people of a customer's domain controller are read for Assets: set up per customer, synced, listed and offered when handing out assets.</summary>
public class DirectoryPeopleTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    readonly WebApplicationFactory<Program> factory;
    static readonly FakeController Fake = new();

    public DirectoryPeopleTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b =>
    {
        TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true");
        b.ConfigureServices(s => { s.RemoveAll<IControllerRelay>(); s.AddSingleton<IControllerRelay>(Fake); });
    });
    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }

    [PgFact]
    public async Task ACompanyGetsItsPeopleFromItsDomainController()
    {
        Fake.Reset();
        var admin = await TestSupport.AdminClient(factory);
        await admin.GetAsync("/api/assets/types");
        await Sql("truncate adsuite.domains restart identity cascade; delete from assets.directory_people");
        var co = (await J(await admin.PostAsync("/api/assets/companies", Json(new { name = "Dir Ltd " + Guid.NewGuid().ToString("N")[..6] })))).GetProperty("id").GetInt32();
        var path = "/api/directory/companies/" + co;

        // nothing set up yet
        Assert.Equal(JsonValueKind.Null, (await J(await admin.GetAsync(path))).GetProperty("domain").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync(path + "/test", Json(new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync(path, Json(new { baseDn = "nonsense", servers = "dc01.acme.test", bindDn = "svc@acme.test", password = "x", controllerId = "ctl1" }))).StatusCode);

        var body = new { baseDn = "DC=acme,DC=test", servers = "dc01.acme.test", tls = true, bindDn = "svc-deskask@acme.test", password = "Secret-Pw-1", controllerId = "ctl1" };
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync(path, Json(body))).StatusCode);
        // saving again changes the same domain, and the password may stay empty
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync(path, Json(new { body.baseDn, body.servers, body.tls, body.bindDn, password = "", body.controllerId }))).StatusCode);
        var got = await J(await admin.GetAsync(path));
        Assert.Equal("DC=acme,DC=test", got.GetProperty("domain").GetProperty("baseDn").GetString());
        Assert.False(got.GetProperty("domain").TryGetProperty("password", out _));
        Assert.True((await J(await admin.PostAsync(path + "/test", Json(new { })))).GetProperty("ok").GetBoolean());

        var sync = await J(await admin.PostAsync(path + "/sync", Json(new { })));
        Assert.True(sync.GetProperty("ok").GetBoolean());
        Assert.True(sync.GetProperty("count").GetInt32() >= 2);

        var list = (await J(await admin.GetAsync("/api/assets/directory-people?companyId=" + co))).GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(list, p => p.GetProperty("name").GetString() == "Ana Lee" && p.GetProperty("email").GetString() == "ana@acme.test");
        var found = (await J(await admin.GetAsync("/api/assets/directory-people?companyId=" + co + "&search=ana"))).GetProperty("items").EnumerateArray().ToList();
        Assert.All(found, p => Assert.Contains("ana", (p.GetProperty("name").GetString() + p.GetProperty("email").GetString()).ToLowerInvariant()));
        Assert.Equal(sync.GetProperty("count").GetInt32(), (await J(await admin.GetAsync(path))).GetProperty("domain").GetProperty("people").GetInt32());

        // a controller that is down never empties the list
        Fake.Down = true;
        var failed = await J(await admin.PostAsync(path + "/sync", Json(new { })));
        Assert.False(failed.GetProperty("ok").GetBoolean());
        Fake.Down = false;
        Assert.NotEmpty((await J(await admin.GetAsync("/api/assets/directory-people?companyId=" + co))).GetProperty("items").EnumerateArray());

        // a person who leaves the directory leaves the list at the next sync
        Fake.Dir.Remove("CN=Ana Lee,OU=Sales,OU=Staff,DC=acme,DC=test");
        Assert.True((await J(await admin.PostAsync(path + "/sync", Json(new { })))).GetProperty("ok").GetBoolean());
        Assert.DoesNotContain((await J(await admin.GetAsync("/api/assets/directory-people?companyId=" + co))).GetProperty("items").EnumerateArray(), p => p.GetProperty("name").GetString() == "Ana Lee");

        // only administrators set it up; removing it removes the people
        var pw = "Long-Enough-1";
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync("/api/platform/users", Json(new { email = "eng@dirppl.test", name = "Eng", role = "engineer", password = pw }))).StatusCode);
        var eng = await TestSupport.ClientFor(factory, "eng@dirppl.test", pw);
        Assert.Equal(HttpStatusCode.Forbidden, (await eng.PutAsync(path, Json(body))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await eng.PostAsync(path + "/sync", Json(new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync(path)).StatusCode);
        Assert.Empty((await J(await admin.GetAsync("/api/assets/directory-people?companyId=" + co))).GetProperty("items").EnumerateArray());
    }

    static async Task<(HttpStatusCode Code, string Page)> SignIn(WebApplicationFactory<Program> f, string email, string password)
    {
        var c = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var page = await (await c.GetAsync("/account/login")).Content.ReadAsStringAsync();
        var token = System.Text.RegularExpressions.Regex.Match(page, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        var r = await c.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["returnUrl"] = "/", ["email"] = email, ["password"] = password }));
        return (r.StatusCode, await r.Content.ReadAsStringAsync());
    }

    [PgFact]
    public async Task PeoplePickedFromTheCompanysDomainControllerSignInWithTheirWindowsPassword()
    {
        Fake.Reset();
        var admin = await TestSupport.AdminClient(factory);
        await admin.GetAsync("/api/assets/types");
        await Sql("truncate adsuite.domains restart identity cascade; delete from assets.directory_people; delete from identity.users where \"Email\" in ('ana@acme.test', 'bo@acme.test')");
        var co = (await J(await admin.PostAsync("/api/assets/companies", Json(new { name = "Win Ltd " + Guid.NewGuid().ToString("N")[..6] })))).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/directory/companies/" + co, Json(new { baseDn = "DC=acme,DC=test", servers = "dc01.acme.test", tls = true, bindDn = "svc-deskask@acme.test", password = "Secret-Pw-1", controllerId = "ctl1" }))).StatusCode);
        Fake.Passwords["CN=Ana Lee,OU=Sales,OU=Staff,DC=acme,DC=test"] = "Win-Pass-1";

        var made = await J(await admin.PostAsync($"/api/companies/{co}/users/from-directory", Json(new { role = "customer", people = new object[] { new { account = "ana", name = "Ana Lee", email = "ana@acme.test" }, new { account = "bo", name = "Bo Kim", email = "bo@acme.test" }, new { account = "x", name = "No mail", email = "" } } })));
        Assert.Equal(2, made.GetProperty("created").GetInt32()); Assert.Equal(1, made.GetProperty("skipped").GetInt32());
        var users = (await J(await admin.GetAsync("/api/platform/users"))).EnumerateArray().ToList();
        var ana = users.First(u => u.GetProperty("email").GetString() == "ana@acme.test");
        Assert.Equal("windows", ana.GetProperty("signIn").GetString());
        Assert.Contains((await J(await admin.GetAsync("/api/platform/access/user-companies"))).EnumerateArray(), x => x.GetProperty("userId").GetString() == ana.GetProperty("id").GetString() && x.GetProperty("companyId").GetInt32() == co);

        // the right Windows password signs her in; it is checked through the controller and never stored
        var me = await TestSupport.ClientFor(factory, "ana@acme.test", "Win-Pass-1");
        Assert.Equal(HttpStatusCode.OK, (await me.GetAsync("/api/servicedesk/me")).StatusCode);
        Assert.Contains("verify CN=Ana Lee,OU=Sales,OU=Staff,DC=acme,DC=test", Fake.Log);
        var wrong = await SignIn(factory, "ana@acme.test", "nope-nope-1");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.Code); Assert.Contains("Email or password is not correct.", wrong.Page);
        // a disabled Windows account is told so
        var bo = await SignIn(factory, "bo@acme.test", "anything-1");
        Assert.Contains("Your Windows account is disabled.", bo.Page);
        // a controller that has not been updated cannot check it, and says so
        Fake.OldAgent = true;
        var old = await SignIn(factory, "ana@acme.test", "Win-Pass-1");
        Assert.Equal(HttpStatusCode.Unauthorized, old.Code); Assert.Contains("cannot be checked right now", old.Page);
        Fake.OldAgent = false;
        // only administrators pick people
        Assert.Equal(HttpStatusCode.Forbidden, (await me.PostAsync($"/api/companies/{co}/users/from-directory", Json(new { people = new object[] { new { account = "cy", name = "Cy", email = "cy@acme.test" } } }))).StatusCode);
    }

}
