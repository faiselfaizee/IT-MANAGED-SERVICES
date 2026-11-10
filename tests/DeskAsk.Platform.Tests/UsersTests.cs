using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

public class UsersTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    readonly WebApplicationFactory<Program> factory;
    public UsersTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }
    static Task<HttpResponseMessage> Patch(HttpClient c, string url, object body) => c.SendAsync(new HttpRequestMessage(HttpMethod.Patch, url) { Content = Json(body) });

    async Task<HttpClient> Start()
    {
        var api = await TestSupport.AdminClient(factory);
        await Sql("delete from identity.users where \"Email\" <> 'admin@acme.test'");
        return api;
    }

    [PgFact]
    public async Task AnonymousCallersAreRefused()
    {
        var c = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/platform/users")).StatusCode);
    }

    [PgFact]
    public async Task AdminCreatesListsEditsAndDisablesPeople()
    {
        var api = await Start();
        var roles = await J(await api.GetAsync("/api/platform/users/roles"));
        Assert.Contains("engineer", roles.EnumerateArray().Select(x => x.GetString()));

        var made = await api.PostAsync("/api/platform/users", Json(new { email = "  Sam@Acme.test ", name = "Sam", role = "engineer", password = "Long-Enough-1" }));
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var u = await J(made);
        Assert.Equal("sam@acme.test", u.GetProperty("email").GetString());
        Assert.False(u.TryGetProperty("passwordHash", out _));
        var id = u.GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.Conflict, (await api.PostAsync("/api/platform/users", Json(new { email = "sam@acme.test", name = "Sam 2", role = "viewer", password = "Long-Enough-1" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/platform/users", Json(new { email = "x@acme.test", name = "X", role = "viewer", password = "short" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/platform/users", Json(new { email = "x@acme.test", name = "X", role = "boss", password = "Long-Enough-1" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/platform/users", Json(new { email = "not-an-email", name = "X", role = "viewer", password = "Long-Enough-1" }))).StatusCode);

        var edit = await Patch(api, "/api/platform/users/" + id, new { name = "Samuel", role = "viewer" });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var e = await J(edit);
        Assert.Equal("Samuel", e.GetProperty("name").GetString()); Assert.Equal("viewer", e.GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.OK, (await Patch(api, "/api/platform/users/" + id, new { active = false })).StatusCode);

        var list = await J(await api.GetAsync("/api/platform/users"));
        var sam = list.EnumerateArray().Single(x => x.GetProperty("email").GetString() == "sam@acme.test");
        Assert.False(sam.GetProperty("active").GetBoolean());
        var audit = await J(await api.GetAsync("/api/platform/audit?action=user.updated&take=5"));
        Assert.True(audit.GetRawText().Contains("sam@acme.test"));
    }

    [PgFact]
    public async Task TheLastAdministratorAndYourOwnAccountAreProtected()
    {
        var api = await Start();
        var me = (await J(await api.GetAsync("/api/platform/me"))).GetProperty("sub").GetString();
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(api, "/api/platform/users/" + me, new { active = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(api, "/api/platform/users/" + me, new { role = "viewer" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Patch(api, "/api/platform/users/" + Guid.NewGuid(), new { name = "x" })).StatusCode);

        // a second administrator may be demoted, because the first remains
        var id = (await J(await api.PostAsync("/api/platform/users", Json(new { email = "b@acme.test", name = "B", role = "admin", password = "Long-Enough-1" })))).GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.OK, (await Patch(api, "/api/platform/users/" + id, new { role = "engineer" })).StatusCode);
    }

    [PgFact]
    public async Task PasswordAndTwoStepResets()
    {
        var api = await Start();
        var id = (await J(await api.PostAsync("/api/platform/users", Json(new { email = "c@acme.test", name = "C", role = "customer", password = "Long-Enough-1" })))).GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/platform/users/" + id + "/password", Json(new { password = "tiny" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync("/api/platform/users/" + id + "/password", Json(new { password = "Another-Long-2" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync("/api/platform/users/" + id + "/two-step/reset", Json(new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.PostAsync("/api/platform/users/" + Guid.NewGuid() + "/password", Json(new { password = "Another-Long-2" }))).StatusCode);
    }
}
