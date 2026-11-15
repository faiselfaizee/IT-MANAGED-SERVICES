using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

public class DuplicateTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    readonly WebApplicationFactory<Program> factory;
    public DuplicateTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }

    [PgFact]
    public async Task TwinsAreBlockedOrWarnedAndCanBeMerged()
    {
        var admin = await TestSupport.AdminClient(factory);
        await admin.GetAsync("/api/assets/types");
        await Sql("delete from assets.amc; delete from assets.rentals; delete from assets.assets; delete from assets.companies where name in ('Dup Acme', 'Dup Globex')");
        var acme = (await J(await admin.PostAsync("/api/assets/companies", Json(new { name = "Dup Acme" })))).GetProperty("id").GetInt32();
        var globex = (await J(await admin.PostAsync("/api/assets/companies", Json(new { name = "Dup Globex" })))).GetProperty("id").GetInt32();

        var first = await admin.PostAsync("/api/assets", Json(new { name = "SQL01", companyId = acme, hostname = "sql01.acme.local", macAddress = "00-1A-2B-3C-4D-5E", ipAddress = "10.1.1.5", serial = "SN-123" }));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode); var a = (await J(first)).GetProperty("id").GetInt64();

        // same host name (any case, with or without the domain), MAC (any notation) or serial is refused
        foreach (var body in new object[] { new { name = "x1", companyId = acme, hostname = "SQL01" }, new { name = "x2", companyId = acme, macAddress = "001a.2b3c.4d5e" }, new { name = "x3", companyId = acme, serial = "sn-123" } })
        {
            var r = await admin.PostAsync("/api/assets", Json(body));
            Assert.Equal(HttpStatusCode.Conflict, r.StatusCode); Assert.Equal("duplicate", (await J(r)).GetProperty("error").GetString());
        }
        // the same values at another customer are a different device
        var gOk = await admin.PostAsync("/api/assets", Json(new { name = "SQL01", companyId = globex, hostname = "sql01", macAddress = "00:1a:2b:3c:4d:5e" }));
        Assert.Equal(HttpStatusCode.OK, gOk.StatusCode); var gAsset = (await J(gOk)).GetProperty("id").GetInt64();
        // a placeholder serial does not count
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/assets", Json(new { name = "p1", companyId = acme, serial = "N/A" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/assets", Json(new { name = "p2", companyId = acme, serial = "N/A" }))).StatusCode);
        // a bad MAC is a plain error
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/assets", Json(new { name = "bad", companyId = acme, macAddress = "nonsense" }))).StatusCode);

        // the same IP is a warning that can be overruled
        var warn = await admin.PostAsync("/api/assets", Json(new { name = "Printer", companyId = acme, ipAddress = "10.1.1.5" }));
        Assert.Equal(HttpStatusCode.Conflict, warn.StatusCode); var w = await J(warn);
        Assert.Equal("possible_duplicate", w.GetProperty("error").GetString()); Assert.Equal(a, w.GetProperty("matches")[0].GetProperty("id").GetInt64());
        var twin = await admin.PostAsync("/api/assets", Json(new { name = "Printer", companyId = acme, ipAddress = "10.1.1.5", confirmDuplicate = true }));
        Assert.Equal(HttpStatusCode.OK, twin.StatusCode); var b = (await J(twin)).GetProperty("id").GetInt64();

        // changing an asset into a twin is checked too; an unrelated edit of an asset that has a twin is not
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsync("/api/assets/" + b, Json(new { hostname = "sql01" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/assets/" + b, Json(new { notes = "still a twin by IP" }))).StatusCode);

        // the duplicates page lists the pair
        var groups = (await J(await admin.GetAsync("/api/assets/duplicates"))).GetProperty("groups");
        var g = groups.EnumerateArray().Single(x => x.GetProperty("company").GetString() == "Dup Acme");
        Assert.Equal(2, g.GetProperty("items").GetArrayLength());

        // monitoring can be chosen per asset
        Assert.True((await J(await admin.GetAsync($"/api/assets/{b}/coverage"))).GetProperty("monitored").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/assets/bulk-monitor", Json(new { ids = new[] { b }, monitored = false }))).StatusCode);
        Assert.False((await J(await admin.GetAsync($"/api/assets/{b}/coverage"))).GetProperty("monitored").GetBoolean());

        // an asset goes under an AMC and a rental, each with its vendor
        var vendor = (await J(await admin.PostAsync("/api/assets/vendors", Json(new { name = "Dup Vendor" })))).GetProperty("id").GetInt32();
        var today = DateTime.UtcNow.Date; string D(int d) => today.AddDays(d).ToString("yyyy-MM-dd");
        var amc = (await J(await admin.PostAsync("/api/assets/amc", Json(new { title = "Acme AMC", companyId = acme, start = D(-10), end = D(300), vendorId = vendor })))).GetProperty("id").GetInt32();
        var rent = (await J(await admin.PostAsync("/api/assets/rentals", Json(new { title = "Acme rental", companyId = acme, start = D(-10), end = D(300), vendorId = vendor, contractNo = "R-1" })))).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/assets/{b}/coverage", Json(new { amcId = amc, rentalId = rent }))).StatusCode);
        var cov = await J(await admin.GetAsync($"/api/assets/{b}/coverage"));
        Assert.Equal(amc, cov.GetProperty("amc").GetProperty("id").GetInt32()); Assert.Equal("Dup Vendor", cov.GetProperty("rental").GetProperty("vendor").GetString());
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/assets/{b}/coverage", Json(new { rentalId = (int?)null }))).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await J(await admin.GetAsync($"/api/assets/{b}/coverage"))).GetProperty("rental").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/assets/{b}/coverage", Json(new { rentalId = rent }))).StatusCode);
        // another customer's contract is refused
        var gAmc = (await J(await admin.PostAsync("/api/assets/amc", Json(new { title = "Globex AMC", companyId = globex, start = D(-10), end = D(300) })))).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync($"/api/assets/{b}/coverage", Json(new { amcId = gAmc }))).StatusCode);

        // merge: the kept asset takes what it lacked, the contracts follow, the other is retired
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/assets/" + a, Json(new { notes = "kept" }))).StatusCode);
        var m = await admin.PostAsync("/api/assets/merge", Json(new { keepId = a, mergeIds = new[] { b } }));
        Assert.Equal(HttpStatusCode.OK, m.StatusCode);
        Assert.Equal("retired", (await J(await admin.GetAsync("/api/assets/" + b))).GetProperty("asset").GetProperty("status").GetString());
        var covA = await J(await admin.GetAsync($"/api/assets/{a}/coverage"));
        Assert.Equal(amc, covA.GetProperty("amc").GetProperty("id").GetInt32()); Assert.Equal(rent, covA.GetProperty("rental").GetProperty("id").GetInt32());
        Assert.Empty((await J(await admin.GetAsync("/api/assets/duplicates"))).GetProperty("groups").EnumerateArray().Where(x => x.GetProperty("company").GetString() == "Dup Acme"));
        // another customer's asset cannot be merged in
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/assets/merge", Json(new { keepId = a, mergeIds = new[] { gAsset } }))).StatusCode);

        // changing the IP address replaces it entirely
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/assets/" + a, Json(new { ipAddress = "10.9.9.9" }))).StatusCode);
        Assert.Equal("10.9.9.9", (await J(await admin.GetAsync("/api/assets/" + a + "/device"))).GetProperty("system").GetProperty("ip").GetString());
    }
}
