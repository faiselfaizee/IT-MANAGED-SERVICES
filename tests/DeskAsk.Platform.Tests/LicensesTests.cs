using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

/// <summary>Software licences and subscriptions: vendor, dates, seats, installs counted from the inventory, the sealed key, renewal and reminders.</summary>
public class LicensesTests : IClassFixture<WebApplicationFactory<Program>>
{
    const string Pw = "Lic-Test-Pass-9";
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    readonly WebApplicationFactory<Program> factory;
    public LicensesTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task<object?> Scalar(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); return await c.ExecuteScalarAsync(); }

    [PgFact]
    public async Task SubscriptionsHaveVendorDatesSeatsInstallsASealedKeyAndRenew()
    {
        var admin = await TestSupport.AdminClient(factory);
        await admin.GetAsync("/api/assets/types");
        await Scalar("delete from assets.licenses; delete from identity.users where \"Email\" like '%@lic.test'");
        var tag = Guid.NewGuid().ToString("N")[..6];
        var co = (await J(await admin.PostAsync("/api/assets/companies", Json(new { name = "Lic Acme " + tag })))).GetProperty("id").GetInt32();
        var other = (await J(await admin.PostAsync("/api/assets/companies", Json(new { name = "Lic Other " + tag })))).GetProperty("id").GetInt32();
        var vendor = (await J(await admin.PostAsync("/api/assets/vendors", Json(new { name = "Soft Reseller " + tag })))).GetProperty("id").GetInt32();
        var pc1 = (await J(await admin.PostAsync("/api/assets", Json(new { name = "CAD-1 " + tag, companyId = co })))).GetProperty("id").GetInt64();
        var pc2 = (await J(await admin.PostAsync("/api/assets", Json(new { name = "CAD-2 " + tag, companyId = co })))).GetProperty("id").GetInt64();
        var foreign = (await J(await admin.PostAsync("/api/assets", Json(new { name = "Else " + tag, companyId = other })))).GetProperty("id").GetInt64();
        await Scalar($"update assets.assets set installed_apps = '[{{\"name\":\"Autodesk AutoCAD 2025\",\"version\":\"25.0\"}}]' where id in ({pc1}, {pc2}, {foreign})");

        // checks: a subscription has an end date, dates are dates, the vendor fits the customer
        var end = DateTime.UtcNow.Date.AddDays(20).ToString("yyyy-MM-dd");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/assets/licenses", Json(new { companyId = co, product = "AutoCAD", kind = "subscription" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/assets/licenses", Json(new { companyId = co, product = "AutoCAD", endDate = "20/11/2026" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/assets/licenses", Json(new { companyId = co, product = "", endDate = end }))).StatusCode);
        var made = await admin.PostAsync("/api/assets/licenses", Json(new { companyId = co, product = "AutoCAD", publisher = "Autodesk", kind = "subscription", model = "user", seats = 1,
            billing = "yearly", cost = 1999.5, currency = "inr", vendorId = vendor, startDate = DateTime.UtcNow.Date.AddDays(-345).ToString("yyyy-MM-dd"), endDate = end, match = "AutoCAD", licenseKey = "AAAA-BBBB-CCCC" }));
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var id = (await J(made)).GetProperty("id").GetInt32();
        Assert.DoesNotContain("AAAA-BBBB", (string)(await Scalar($"select key_sealed from assets.licenses where id = {id}"))!);   // sealed

        // the list: ends within 30 days, installed on two of this customer's computers (not the other customer's), one seat: over-used
        var list = await J(await admin.GetAsync($"/api/assets/licenses?company={co}"));
        var l = list.GetProperty("items")[0];
        Assert.Equal("expiring", l.GetProperty("status").GetString()); Assert.Equal(2, l.GetProperty("installed").GetInt64()); Assert.Equal("INR", l.GetProperty("currency").GetString());
        Assert.True(l.GetProperty("hasKey").GetBoolean()); Assert.False(l.TryGetProperty("licenseKey", out _));
        Assert.Equal(2, (await J(await admin.GetAsync($"/api/assets/licenses/{id}/installed"))).GetArrayLength());
        Assert.Equal(1, (await J(await admin.GetAsync("/api/assets/licenses?status=expiring"))).GetProperty("items").GetArrayLength());

        // seats: a person and a computer of the same customer; not a computer of another customer; not twice
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/assets/licenses/{id}/seats", Json(new { user = "Asha Nair" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/assets/licenses/{id}/seats", Json(new { assetId = pc1 }))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/assets/licenses/{id}/seats", Json(new { assetId = pc1 }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/assets/licenses/{id}/seats", Json(new { assetId = foreign }))).StatusCode);
        var detail = await J(await admin.GetAsync($"/api/assets/licenses/{id}"));
        Assert.Equal(2, detail.GetProperty("seats").GetArrayLength()); Assert.Equal(2, detail.GetProperty("license").GetProperty("assigned").GetInt64());

        // the key: shown to someone who may change it, audited; a viewer may not see it
        Assert.Equal("AAAA-BBBB-CCCC", (await J(await admin.GetAsync($"/api/assets/licenses/{id}/key"))).GetProperty("key").GetString());
        Assert.Equal(1L, await Scalar($"select count(*) from core.audit where action = 'assets.license_key_viewed' and target = '{id}'"));
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync("/api/platform/users", Json(new { email = "view@lic.test", name = "viewer", role = "viewer", password = Pw }))).StatusCode);
        var viewer = await TestSupport.ClientFor(factory, "view@lic.test", Pw);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/assets/licenses/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync($"/api/assets/licenses/{id}/key")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsync($"/api/assets/licenses/{id}", Json(new { product = "X", endDate = end }))).StatusCode);
        // editing without the key keeps it
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/assets/licenses/{id}", Json(new { product = "AutoCAD", kind = "subscription", seats = 3, endDate = end, vendorId = vendor, match = "AutoCAD" }))).StatusCode);
        Assert.Equal("AAAA-BBBB-CCCC", (await J(await admin.GetAsync($"/api/assets/licenses/{id}/key"))).GetProperty("key").GetString());

        // it is among the renewals; renewing makes the next period with the seats, and the old one stops reminding
        var up = await J(await admin.GetAsync("/api/assets/renewals/upcoming?days=60"));
        Assert.Contains(up.GetProperty("items").EnumerateArray(), x => x.GetProperty("kind").GetString() == "license" && x.GetProperty("id").GetInt64() == id);
        var ren = await admin.PostAsync($"/api/assets/licenses/{id}/renew", Json(new { cost = 2100 }));
        Assert.Equal(HttpStatusCode.OK, ren.StatusCode);
        var nid = (await J(ren)).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/assets/licenses/{id}/renew", Json(new { }))).StatusCode);
        var nd = await J(await admin.GetAsync($"/api/assets/licenses/{nid}"));
        Assert.Equal(DateTime.UtcNow.Date.AddDays(21).ToString("yyyy-MM-dd"), nd.GetProperty("license").GetProperty("startDate").GetString());
        Assert.Equal(2, nd.GetProperty("seats").GetArrayLength()); Assert.Equal(id, nd.GetProperty("history")[0].GetProperty("id").GetInt32());
        Assert.Equal(nid, (await J(await admin.GetAsync($"/api/assets/licenses/{id}"))).GetProperty("renewedBy").GetInt32());
        Assert.Equal("renewed", (await J(await admin.GetAsync($"/api/assets/licenses/{id}"))).GetProperty("license").GetProperty("status").GetString());
        up = await J(await admin.GetAsync("/api/assets/renewals/upcoming?days=60"));
        Assert.DoesNotContain(up.GetProperty("items").EnumerateArray(), x => x.GetProperty("kind").GetString() == "license" && x.GetProperty("id").GetInt64() == id);

        var csv = await admin.GetStringAsync($"/api/assets/licenses/export.csv?company={co}");
        Assert.Contains("AutoCAD", csv); Assert.DoesNotContain("AAAA-BBBB", csv);
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/assets/licenses/{nid}")).StatusCode);
    }
}
