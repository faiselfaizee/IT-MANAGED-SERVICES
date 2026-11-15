using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

public class AmcAccessTests : IClassFixture<WebApplicationFactory<Program>>
{
    const string Pw = "Amc-Test-Pass-9";
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    readonly WebApplicationFactory<Program> factory;
    public AmcAccessTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); b.UseSetting("Platform:Roles", "all"); b.UseSetting("Jobs:PollSeconds", "1"); });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }

    [PgFact]
    public async Task IpAddressCanBeChangedAndCustomerAdminsEditAmcOnlyWhenAllowed()
    {
        var admin = await TestSupport.AdminClient(factory);
        await admin.GetAsync("/api/assets/types");
        await Sql("truncate assets.events, assets.ifstats, assets.port_watch, assets.metrics, assets.collectors, assets.creds, assets.agent_upd_log, assets.shells, assets.rc_log, assets.jobs, assets.agent_q, assets.agents, assets.enroll_tokens, assets.history, assets.assets, assets.companies, assets.amc, assets.rentals restart identity cascade");
        await Sql("delete from core.settings where key = 'assets.renewals'; delete from identity.users where \"Email\" like '%@amc.test'");

        var acme = (await J(await admin.PostAsync("/api/assets/companies", Json(new { name = "Acme" })))).GetProperty("id").GetInt32();
        var globex = (await J(await admin.PostAsync("/api/assets/companies", Json(new { name = "Globex" })))).GetProperty("id").GetInt32();

        // ---- the IP address of an asset can be changed after it was added
        var a = (await J(await admin.PostAsync("/api/assets", Json(new { name = "SW-01", companyId = acme, ipAddress = "10.0.0.5" })))).GetProperty("id").GetInt64();
        Assert.Equal("10.0.0.5", (await J(await admin.GetAsync("/api/assets/" + a))).GetProperty("system").GetProperty("ip").GetString());
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/assets/" + a, Json(new { ipAddress = " 10.0.0.77 " }))).StatusCode);
        Assert.Equal("10.0.0.77", (await J(await admin.GetAsync("/api/assets/" + a))).GetProperty("system").GetProperty("ip").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/assets/" + a, Json(new { ipAddress = "ten.zero" }))).StatusCode);
        Assert.Equal("10.0.0.77", (await J(await admin.GetAsync("/api/assets/" + a))).GetProperty("system").GetProperty("ip").GetString());

        // ---- a customer administrator and AMC contracts
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync("/api/platform/users", Json(new { email = "cadm@amc.test", name = "Acme admin", role = "cadmin", password = Pw }))).StatusCode);
        var cid = (await J(await admin.GetAsync("/api/platform/users"))).EnumerateArray().Single(x => x.GetProperty("email").GetString() == "cadm@amc.test").GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/platform/access/user-companies/{cid}", Json(new { companyId = acme }))).StatusCode);
        var cadm = await TestSupport.ClientFor(factory, "cadm@amc.test", Pw);
        var today = DateTime.UtcNow.Date; string D(int d) => today.AddDays(d).ToString("yyyy-MM-dd");

        var gAmc = (await J(await admin.PostAsync("/api/assets/amc", Json(new { title = "Globex support", companyId = globex, start = D(-300), end = D(60) })))).GetProperty("id").GetInt32();

        // off by default: they cannot see or change contracts
        Assert.False((await J(await cadm.GetAsync("/api/assets/amc/access"))).GetProperty("canEdit").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await cadm.GetAsync("/api/assets/amc")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cadm.PostAsync("/api/assets/amc", Json(new { title = "Mine", companyId = acme, start = D(-1), end = D(364) }))).StatusCode);

        // an administrator turns it on
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/assets/renewals/settings", Json(new { enabled = false, days = new[] { 30, 15 }, customerAmc = true }))).StatusCode);
        Assert.True((await J(await cadm.GetAsync("/api/assets/amc/access"))).GetProperty("canEdit").GetBoolean());

        var made = await cadm.PostAsync("/api/assets/amc", Json(new { title = "Acme support", companyId = acme, start = D(-1), end = D(364), assetIds = new[] { a }, contractNo = "AMC-1" }));
        Assert.Equal(HttpStatusCode.OK, made.StatusCode); var mine = (await J(made)).GetProperty("id").GetInt32();
        var list = await J(await cadm.GetAsync("/api/assets/amc"));
        Assert.Single(list.EnumerateArray()); Assert.Equal(mine, list[0].GetProperty("id").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await cadm.PutAsync("/api/assets/amc/" + mine, Json(new { notes = "changed" }))).StatusCode);

        // never another customer's contract, nor another customer's assets
        Assert.Equal(HttpStatusCode.NotFound, (await cadm.PutAsync("/api/assets/amc/" + gAmc, Json(new { notes = "x" }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cadm.DeleteAsync("/api/assets/amc/" + gAmc)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cadm.PostAsync($"/api/assets/amc/{gAmc}/renew", Json(new { start = D(61), end = D(425) }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cadm.PostAsync("/api/assets/amc", Json(new { title = "Theirs", companyId = globex, start = D(-1), end = D(364) }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await cadm.PostAsync("/api/assets/amc", Json(new { title = "No customer", start = D(-1), end = D(364) }))).StatusCode);
        var ga = (await J(await admin.PostAsync("/api/assets", Json(new { name = "Globex box", companyId = globex })))).GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.BadRequest, (await cadm.PutAsync("/api/assets/amc/" + mine, Json(new { assetIds = new[] { ga } }))).StatusCode);

        // renewing their own; rentals stay with administrators
        var renewed = await cadm.PostAsync($"/api/assets/amc/{mine}/renew", Json(new { start = D(365), end = D(729) }));
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cadm.PostAsync("/api/assets/rentals", Json(new { title = "Laptops", companyId = acme }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await cadm.DeleteAsync("/api/assets/amc/" + (await J(renewed)).GetProperty("id").GetInt32())).StatusCode);

        // switched off again, they are locked out at once
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/assets/renewals/settings", Json(new { enabled = false, days = new[] { 30, 15 }, customerAmc = false }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cadm.PutAsync("/api/assets/amc/" + mine, Json(new { notes = "again" }))).StatusCode);
    }
}
