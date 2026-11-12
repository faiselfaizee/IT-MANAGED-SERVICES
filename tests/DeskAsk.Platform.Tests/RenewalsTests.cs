using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

public class RenewalsTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    readonly WebApplicationFactory<Program> factory;
    public RenewalsTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); b.UseSetting("Platform:Roles", "all"); b.UseSetting("Jobs:PollSeconds", "1"); });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }
    static async Task<T> Scalar<T>(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); return (T)(await c.ExecuteScalarAsync())!; }
    static async Task Until(Func<bool> ok) { for (var i = 0; i < 40 && !ok(); i++) await Task.Delay(500); }

    [PgFact]
    public async Task ARenewalIsMailedOnceToYouAndToTheCustomer()
    {
        var admin = await TestSupport.AdminClient(factory); using var smtp = new FakeSmtp();
        await Sql("truncate mail.outbox restart identity; delete from assets.renewal_notices; delete from assets.amc; delete from core.settings where key = 'assets.renewals'");
        await Sql("delete from assets.companies where lower(name) = 'renewal client'");
        await Sql("insert into assets.companies (name, contact_email) values ('Renewal client', 'it@client.test')");
        await Sql("insert into assets.amc (company_id, title, end_date) select id, 'Server AMC', current_date + 5 from assets.companies where lower(name) = 'renewal client'");
        await Sql("insert into assets.amc (company_id, title, end_date) select id, 'Far away AMC', current_date + 200 from assets.companies where lower(name) = 'renewal client'");
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/platform/mail/config", Json(new { provider = "smtp", from = "desk@example.test", fromName = "DeskAsk", host = "127.0.0.1", port = smtp.Port, security = "none", user = "", password = "" }))).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/assets/renewals/settings", Json(new { enabled = true, days = new int[0], to = "boss@renew.test", customers = true }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/assets/renewals/settings", Json(new { enabled = true, days = new[] { 30 }, to = "", customers = false }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/assets/renewals/settings", Json(new { enabled = true, days = new[] { 7, 30 }, to = "boss@renew.test", customers = true }))).StatusCode);
        var saved = JsonDocument.Parse(await (await admin.GetAsync("/api/assets/renewals/settings")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(30, saved.GetProperty("days")[0].GetInt32());   // kept longest first

        var up = JsonDocument.Parse(await (await admin.GetAsync("/api/assets/renewals/upcoming?days=30")).Content.ReadAsStringAsync()).RootElement.GetProperty("items");
        Assert.Contains(up.EnumerateArray(), x => x.GetProperty("title").GetString() == "Server AMC");
        Assert.DoesNotContain(up.EnumerateArray(), x => x.GetProperty("title").GetString() == "Far away AMC");

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/assets/renewals/run", null)).StatusCode);
        await Until(() => smtp.Snapshot().Count(m => m.Contains("Server AMC")) >= 2);
        var mails = smtp.Snapshot().Where(m => m.Contains("Server AMC")).ToArray();
        Assert.Equal(2, mails.Length);
        Assert.Contains(mails, m => m.Contains("boss@renew.test")); Assert.Contains(mails, m => m.Contains("it@client.test"));
        Assert.DoesNotContain(smtp.Snapshot(), m => m.Contains("Far away AMC"));
        Assert.Equal(1, await Scalar<long>("select count(*) from assets.renewal_notices where kind = 'amc' and days = 7"));

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/assets/renewals/run", null)).StatusCode);
        await Task.Delay(4000);
        Assert.Equal(2, smtp.Snapshot().Count(m => m.Contains("Server AMC")));   // not twice
    }
}
