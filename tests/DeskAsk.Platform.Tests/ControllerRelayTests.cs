using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

/// <summary>The path a directory job takes to a customer's controller and back, with the controller played by the test.</summary>
public class ControllerRelayTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    readonly WebApplicationFactory<Program> factory;
    public ControllerRelayTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task<string> Scalar(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); return (await c.ExecuteScalarAsync())?.ToString() ?? ""; }

    async Task<string> WaitForQueued()
    {
        for (var i = 0; i < 100; i++)
        {
            var id = await Scalar("select id from assets.relay_jobs where status = 'queued' limit 1");
            if (id != "") return id;
            await Task.Delay(100);
        }
        return "";
    }

    [PgFact]
    public async Task AJobWaitsSealedIsHandedOverOnceWipedAndAnsweredByTheController()
    {
        var api = await TestSupport.AdminClient(factory);
        await api.GetAsync("/api/assets/types");
        await Scalar("truncate assets.collectors, adsuite.domains restart identity cascade");
        var co = (await J(await api.PostAsync("/api/assets/companies", Json(new { name = "Relay Ltd " + Guid.NewGuid().ToString("N")[..6] })))).GetProperty("id").GetInt32();
        var tok = (await J(await api.PostAsync("/api/assets/collectors/tokens", Json(new { companyId = co, label = "HQ" })))).GetProperty("token").GetString()!;
        var anon = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var en = await J(await anon.PostAsync("/collector/v1/enroll", Json(new { token = tok, name = "HQ controller", hostname = "COL-1", platform = "windows", version = "3.8.0" })));
        var cid = en.GetProperty("collectorId").GetString()!;
        var col = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        col.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", en.GetProperty("key").GetString()); col.DefaultRequestHeaders.Add("X-Collector-Id", cid);

        // only an enrolled controller may ask for work
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/collector/v1/relay/wait?wait=1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/collector/v1/relay/result", Json(new { id = "x", ok = true }))).StatusCode);

        // the platform lists the controller and refuses one that belongs to another customer
        var list = await J(await api.GetAsync("/api/adsuite/controllers"));
        Assert.Equal(cid, list[0].GetProperty("id").GetString()); Assert.True(list[0].GetProperty("online").GetBoolean());
        var dom = new { companyId = co, name = "relay.test", baseDn = "DC=relay,DC=test", servers = "dc01", tls = true, bindDn = "svc@relay.test", password = "Secret-Pw-1", controllerId = cid };
        var made = await api.PostAsync("/api/adsuite/domains", Json(dom)); Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var id = (await J(made)).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/adsuite/domains", Json(new { companyId = co + 1000, name = "x", baseDn = "DC=x,DC=test", servers = "dc01", tls = true, bindDn = "svc@x.test", password = "p", controllerId = cid }))).StatusCode);

        // a job: queued and sealed while it waits
        var call = api.PostAsync($"/api/adsuite/domains/{id}/test", Json(new { }));
        var jid = await WaitForQueued(); Assert.NotEqual("", jid);
        var at = await Scalar($"select payload from assets.relay_jobs where id = '{jid}'");
        Assert.NotEqual("", at); Assert.DoesNotContain("Secret-Pw-1", at); Assert.DoesNotContain("relay.test", at);

        // handed over once, with the password inside, and wiped from the platform at once
        var w = await J(await col.GetAsync("/collector/v1/relay/wait?wait=5"));
        var job = w.GetProperty("jobs")[0];
        Assert.Equal(jid, job.GetProperty("id").GetString()); Assert.Equal("directory", job.GetProperty("kind").GetString());
        Assert.Equal("Secret-Pw-1", job.GetProperty("payload").GetProperty("conn").GetProperty("password").GetString());
        Assert.Equal("svc@relay.test", job.GetProperty("payload").GetProperty("conn").GetProperty("bindDn").GetString());
        Assert.Equal("rootdse", job.GetProperty("payload").GetProperty("ops")[0].GetProperty("op").GetString());
        Assert.Equal("", await Scalar($"select payload from assets.relay_jobs where id = '{jid}'"));
        Assert.Equal(0, (await J(await col.GetAsync("/collector/v1/relay/wait?wait=1"))).GetProperty("jobs").GetArrayLength());

        // the controller answers; the person who asked gets the answer, and the job is gone
        var ok = await col.PostAsync("/collector/v1/relay/result", Json(new
        {
            id = jid, ok = true,
            result = new { results = new object[] {
                new { server = "dc01:636", entries = new[] { new { dn = "", attrs = new Dictionary<string, string[]> { ["dnsHostName"] = new[] { "dc01.relay.test" }, ["defaultNamingContext"] = new[] { "DC=relay,DC=test" } } } } },
                new { server = "dc01:636", entries = new[] { new { dn = "DC=relay,DC=test", attrs = new Dictionary<string, string[]>() } } } } }
        }));
        Assert.True((await J(ok)).GetProperty("ok").GetBoolean());
        Assert.False((await J(await col.PostAsync("/collector/v1/relay/result", Json(new { id = jid, ok = true, result = new { results = Array.Empty<object>() } })))).GetProperty("ok").GetBoolean());   // an answer counts once
        var t = await J(await call);
        Assert.True(t.GetProperty("ok").GetBoolean()); Assert.Equal("dc01.relay.test", t.GetProperty("host").GetString()); Assert.Equal("dc01:636", t.GetProperty("server").GetString());
        Assert.Equal("0", await Scalar("select count(*) from assets.relay_jobs"));

        // a refusal from the directory travels back with its code
        var again = api.PostAsync($"/api/adsuite/domains/{id}/test", Json(new { }));
        var j2 = await WaitForQueued();
        await col.GetAsync("/collector/v1/relay/wait?wait=5");
        await col.PostAsync("/collector/v1/relay/result", Json(new { id = j2, ok = false, error = new { code = 49, message = "the domain controller dc01 refused the service account: bad password", step = 0 } }));
        var f = await J(await again);
        Assert.False(f.GetProperty("ok").GetBoolean()); Assert.Contains("refused", f.GetProperty("error").GetString()); Assert.Contains("password", f.GetProperty("hint").GetString());
    }
}
