using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]   // every test host prepares the same database

public class CoreTests : IClassFixture<WebApplicationFactory<Program>>
{
    readonly WebApplicationFactory<Program> factory;
    public CoreTests(WebApplicationFactory<Program> f) =>
        factory = f.WithWebHostBuilder(TestSupport.Configure);

    Task<HttpClient> AdminClient() => TestSupport.AdminClient(factory);

    static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    [PgFact]
    public async Task AuditAndSettingsNeedAnAdministrator()
    {
        var anon = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/platform/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/platform/settings")).StatusCode);
    }

    [PgFact]
    public async Task SignInIsAuditedAndTheChainVerifies()
    {
        var api = await AdminClient();
        var rows = JsonDocument.Parse(await api.GetStringAsync("/api/platform/audit?action=auth.")).RootElement;
        Assert.Contains(rows.EnumerateArray(), r => r.GetProperty("action").GetString() == "auth.login" && r.GetProperty("actor").GetString() == "admin@acme.test");
        var v = JsonDocument.Parse(await api.GetStringAsync("/api/platform/audit/verify")).RootElement;
        Assert.True(v.GetProperty("ok").GetBoolean());
        Assert.True(v.GetProperty("count").GetInt64() >= 1);
    }

    [PgFact]
    public async Task ChangingAnAuditRowIsDetected()
    {
        var api = await AdminClient();
        await using (var db = new NpgsqlConnection(Environment.GetEnvironmentVariable("TEST_PG_CONNECTION")))
        {
            await db.OpenAsync();
            await using var cmd = new NpgsqlCommand("update core.audit set detail = 'tampered' where id = (select min(id) from core.audit)", db);
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
        }
        var v = JsonDocument.Parse(await api.GetStringAsync("/api/platform/audit/verify")).RootElement;
        Assert.False(v.GetProperty("ok").GetBoolean());
        await using (var db = new NpgsqlConnection(Environment.GetEnvironmentVariable("TEST_PG_CONNECTION")))
        {   // leave the chain whole for the other tests: drop the rows and start again
            await db.OpenAsync();
            await using var cmd = new NpgsqlCommand("truncate core.audit restart identity", db);
            await cmd.ExecuteNonQueryAsync();
        }
        Assert.True(JsonDocument.Parse(await api.GetStringAsync("/api/platform/audit/verify")).RootElement.GetProperty("ok").GetBoolean());
    }

    [PgFact]
    public async Task SettingsRoundTripAndAreAudited()
    {
        var api = await AdminClient();
        Assert.Equal(HttpStatusCode.OK, (await api.PutAsync("/api/platform/settings/company.name", Body("{\"value\":\"Acme Ltd\"}"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PutAsync("/api/platform/settings/mail.limits", Body("{\"value\":{\"perHour\":30}}"))).StatusCode);
        Assert.Equal("\"Acme Ltd\"", await api.GetStringAsync("/api/platform/settings/company.name"));
        var all = JsonDocument.Parse(await api.GetStringAsync("/api/platform/settings?prefix=mail.")).RootElement;
        Assert.Equal(30, all.GetProperty("mail.limits").GetProperty("perHour").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PutAsync("/api/platform/settings/Bad Key", Body("{\"value\":1}"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PutAsync("/api/platform/settings/x", Body("{\"nope\":1}"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.DeleteAsync("/api/platform/settings/company.name")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, await Status(api, "/api/platform/settings/company.name"));
        var rows = JsonDocument.Parse(await api.GetStringAsync("/api/platform/audit?action=settings.")).RootElement;
        Assert.Contains(rows.EnumerateArray(), r => r.GetProperty("action").GetString() == "settings.set" && r.GetProperty("target").GetString() == "company.name");
        Assert.Contains(rows.EnumerateArray(), r => r.GetProperty("action").GetString() == "settings.delete");
    }

    static async Task<HttpStatusCode> Status(HttpClient c, string path) => (await c.GetAsync(path)).StatusCode;
}
