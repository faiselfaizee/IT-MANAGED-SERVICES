using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

public class AssetsAccessTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    const string Pw = "Long-Enough-1";
    readonly WebApplicationFactory<Program> factory;
    public AssetsAccessTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); });
    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }

    [PgFact]
    public async Task CustomerEmployeesHaveNoAssetsAndCustomerAdminsOnlyRead()
    {
        var admin = await TestSupport.AdminClient(factory);
        await Sql("delete from identity.users where \"Email\" like '%@acc.test'");
        foreach (var (email, role) in new[] { ("cust@acc.test", "customer"), ("cadm@acc.test", "cadmin"), ("eng@acc.test", "engineer"), ("view@acc.test", "viewer") })
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync("/api/platform/users", Json(new { email, name = role, role, password = Pw }))).StatusCode);

        var urls = new[] { "/api/assets", "/api/assets/summary", "/api/assets/companies" };
        var customer = await TestSupport.ClientFor(factory, "cust@acc.test", Pw);
        foreach (var u in urls) Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync(u)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsync("/api/assets", Json(new { name = "x" }))).StatusCode);

        // a customer administrator who is not tied to a company sees nothing
        var cadm = await TestSupport.ClientFor(factory, "cadm@acc.test", Pw);
        foreach (var u in urls) Assert.Equal(HttpStatusCode.Forbidden, (await cadm.GetAsync(u)).StatusCode);

        foreach (var who in new[] { "eng@acc.test", "view@acc.test" })
        {
            var c = await TestSupport.ClientFor(factory, who, Pw);
            foreach (var u in urls) Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(u)).StatusCode);
        }
        var viewer = await TestSupport.ClientFor(factory, "view@acc.test", Pw);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/assets", Json(new { name = "x" }))).StatusCode);
    }

    [PgFact]
    public async Task EngineersEditButAskBeforeDeletingAndAnApproverDecides()
    {
        var admin = await TestSupport.AdminClient(factory);
        await Sql("delete from identity.users where \"Email\" like '%@del.test'");
        await Sql("delete from assets.delete_requests");
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync("/api/platform/users", Json(new { email = "eng@del.test", name = "eng", role = "engineer", password = Pw }))).StatusCode);
        var eng = await TestSupport.ClientFor(factory, "eng@del.test", Pw);

        var made = await eng.PostAsync("/api/assets", Json(new { name = "Reception PC", status = "in_use" }));
        Assert.True(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync());
        var id = JsonDocument.Parse(await made.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt64();
        Assert.True((await eng.PutAsync($"/api/assets/{id}", Json(new { location = "Front desk" }))).IsSuccessStatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await eng.DeleteAsync($"/api/assets/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await eng.PostAsync($"/api/assets/{id}/delete-request", Json(new { reason = "" }))).StatusCode);
        var ask = await eng.PostAsync($"/api/assets/{id}/delete-request", Json(new { reason = "Scrapped" }));
        Assert.Equal(HttpStatusCode.Created, ask.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await eng.PostAsync($"/api/assets/{id}/delete-request", Json(new { reason = "again" }))).StatusCode);

        // the asker sees the request but cannot decide it
        var list = JsonDocument.Parse(await eng.GetStringAsync("/api/assets/delete-requests?status=pending")).RootElement.GetProperty("items");
        Assert.Equal(1, list.GetArrayLength()); Assert.False(list[0].GetProperty("canDecide").GetBoolean());
        var rid = list[0].GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Forbidden, (await eng.PostAsync($"/api/assets/delete-requests/{rid}/approve", Json(new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await eng.GetAsync($"/api/assets/{id}/device")).StatusCode);

        // an administrator rejects once (the asset stays), then a second request is approved and the asset goes
        Assert.True(JsonDocument.Parse(await admin.GetStringAsync("/api/assets/delete-requests?status=pending")).RootElement.GetProperty("items")[0].GetProperty("canDecide").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/assets/delete-requests/{rid}/reject", Json(new { note = "Still in use" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await eng.GetAsync($"/api/assets/{id}/device")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await eng.PostAsync($"/api/assets/{id}/delete-request", Json(new { reason = "Scrapped for real" }))).StatusCode);
        var rid2 = JsonDocument.Parse(await admin.GetStringAsync("/api/assets/delete-requests?status=pending")).RootElement.GetProperty("items")[0].GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/assets/delete-requests/{rid2}/approve", Json(new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await eng.GetAsync($"/api/assets/{id}/device")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/assets/delete-requests/{rid2}/approve", Json(new { }))).StatusCode);
    }

    [PgFact]
    public async Task CredentialsBelongToACustomerAndCanBeEditedWithoutRetypingTheSecret()
    {
        var admin = await TestSupport.AdminClient(factory);
        await Sql("delete from assets.creds");
        var co = await admin.PostAsync("/api/assets/companies", Json(new { name = "Cred Co" }));
        Assert.True(co.IsSuccessStatusCode, await co.Content.ReadAsStringAsync());
        var companyId = JsonDocument.Parse(await co.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();
        var made = await admin.PostAsync("/api/assets/discover/creds", Json(new { name = "Domain admin", kind = "windows", accountType = "domain", username = @"CRED\adm", secret = "pw-1", companyId }));
        Assert.True(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync());
        var id = JsonDocument.Parse(await made.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();
        Assert.True((await admin.PostAsync("/api/assets/discover/creds", Json(new { name = "Local", kind = "sshpw", accountType = "local", username = "root", secret = "pw-2" }))).IsSuccessStatusCode);
        var items = JsonDocument.Parse(await admin.GetStringAsync("/api/assets/discover/creds")).RootElement.GetProperty("items");
        var mine = items.EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == id);
        Assert.Equal(companyId, mine.GetProperty("companyId").GetInt32()); Assert.Equal("domain", mine.GetProperty("accountType").GetString());
        Assert.DoesNotContain("pw-1", items.ToString());
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/assets/discover/creds/{id}", Json(new { name = "Domain admin 2", accountType = "service", companyId }))).StatusCode);
        var after = JsonDocument.Parse(await admin.GetStringAsync("/api/assets/discover/creds")).RootElement.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == id);
        Assert.Equal("Domain admin 2", after.GetProperty("name").GetString()); Assert.Equal(@"CRED\adm", after.GetProperty("username").GetString());
    }
}
