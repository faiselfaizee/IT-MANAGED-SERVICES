using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

public class CustomFieldsTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    const string Pw = "Long-Enough-1";
    readonly WebApplicationFactory<Program> factory;
    public CustomFieldsTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }
    static Task<HttpResponseMessage> Patch(HttpClient c, string url, object body) => c.SendAsync(new HttpRequestMessage(HttpMethod.Patch, url) { Content = Json(body) });

    [PgFact]
    public async Task FieldsAreCheckedKeptAndHiddenFromCustomersWhenForStaffOnly()
    {
        var admin = await TestSupport.AdminClient(factory);
        await Sql("truncate servicedesk.custom_fields, servicedesk.tickets, servicedesk.events restart identity cascade");
        await Sql("delete from identity.users where \"Email\" like '%@cf.test'");
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync("/api/platform/users", Json(new { email = "eng@cf.test", name = "eng", role = "engineer", password = Pw }))).StatusCode);
        var eng = await TestSupport.ClientFor(factory, "eng@cf.test", Pw);

        Assert.Equal(HttpStatusCode.Forbidden, (await eng.PostAsync("/api/servicedesk/fields", Json(new { label = "Site", kind = "text" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/servicedesk/fields", Json(new { label = "Colour", kind = "choice", options = "" }))).StatusCode);
        var site = await J(await admin.PostAsync("/api/servicedesk/fields", Json(new { label = "Site code", kind = "choice", options = "BLR\nDEL, MUM", required = true })));
        Assert.Equal("site_code", site.GetProperty("key").GetString());
        await admin.PostAsync("/api/servicedesk/fields", Json(new { label = "Cost", kind = "number", staffOnly = true }));
        await admin.PostAsync("/api/servicedesk/fields", Json(new { label = "Go live", kind = "date" }));

        // required field missing, wrong choice, wrong type
        Assert.Equal(HttpStatusCode.BadRequest, (await eng.PostAsync("/api/servicedesk/tickets", Json(new { subject = "No site" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await eng.PostAsync("/api/servicedesk/tickets", Json(new { subject = "Bad", custom = new { site_code = "PAR" } }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await eng.PostAsync("/api/servicedesk/tickets", Json(new { subject = "Bad", custom = new { site_code = "BLR", cost = "lots" } }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await eng.PostAsync("/api/servicedesk/tickets", Json(new { subject = "Bad", custom = new { site_code = "BLR", go_live = "30/11/2026" } }))).StatusCode);

        var made = await eng.PostAsync("/api/servicedesk/tickets", Json(new { subject = "Printer", custom = new { site_code = "del", cost = 120.5, go_live = "2026-11-30" } }));
        Assert.True(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync());
        var id = (await J(made)).GetProperty("id").GetInt64();
        var d = (await J(await eng.GetAsync($"/api/servicedesk/tickets/{id}"))).GetProperty("ticket").GetProperty("custom");
        Assert.Equal("DEL", d.GetProperty("site_code").GetString());
        Assert.Equal(120.5m, d.GetProperty("cost").GetDecimal());

        // change one value, keep the rest; a required field cannot be emptied
        Assert.Equal(HttpStatusCode.OK, (await Patch(eng, $"/api/servicedesk/tickets/{id}", new { custom = new { site_code = "MUM" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(eng, $"/api/servicedesk/tickets/{id}", new { custom = new { site_code = "" } })).StatusCode);
        d = (await J(await eng.GetAsync($"/api/servicedesk/tickets/{id}"))).GetProperty("ticket").GetProperty("custom");
        Assert.Equal("MUM", d.GetProperty("site_code").GetString());
        Assert.Equal("2026-11-30", d.GetProperty("go_live").GetString());

        // the list for customers leaves out staff-only fields
        await Sql("update servicedesk.custom_fields set staff_only = true where key = 'cost'");
        var shown = (await J(await eng.GetAsync("/api/servicedesk/fields"))).GetProperty("items").EnumerateArray().Select(x => x.GetProperty("key").GetString()).ToList();
        Assert.Equal(3, shown.Count);
    }
}
