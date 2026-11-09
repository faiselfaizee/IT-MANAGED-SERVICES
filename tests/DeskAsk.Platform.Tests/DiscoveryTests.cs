using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using DeskAsk.Platform;
using Npgsql;
using Xunit;

public class DiscoveryTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    readonly WebApplicationFactory<Program> factory;
    public DiscoveryTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }

    async Task<HttpClient> Start()
    {
        var api = await TestSupport.AdminClient(factory);
        await api.GetAsync("/api/assets/types");
        await Sql("truncate assets.collectors, assets.creds, assets.shells, assets.rc_log, assets.jobs, assets.agent_q, assets.agents, assets.enroll_tokens, assets.amc, assets.rentals, assets.history, assets.assets, assets.companies, assets.types restart identity cascade");
        return api;
    }
    async Task<long> NewAsset(HttpClient api, string name)
    {
        var r = await api.PostAsync("/api/assets", Json(new { name }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await J(r)).GetProperty("id").GetInt64();
    }

    [PgFact]
    public async Task DiscoverValidatesProbesAndAdopts()
    {
        var api = await Start();
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/assets/discover/probe", Json(new { target = "not a host!" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/assets/discover/scan", Json(new { cidr = "10.0.0.0/16" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/assets/discover/scan", Json(new { cidr = "169.254.1.0/24" }))).StatusCode);

        var p = await api.PostAsync("/api/assets/discover/probe", Json(new { target = "127.0.0.1" }));
        Assert.Equal(HttpStatusCode.OK, p.StatusCode);
        var pj = await J(p);
        Assert.Equal("127.0.0.1", pj.GetProperty("ips")[0].GetString());

        var raw = new { schema = 1, hostname = "SRV-DISC", os_family = "linux", serial_number = "DISC-SERIAL-77", cpu = new { model = "Xeon", cores = 4 } };
        var a = await api.PostAsync("/api/assets/discover/adopt", Json(new { probe = pj, name = "Found server", raw, source = "ssh" }));
        Assert.Equal(HttpStatusCode.OK, a.StatusCode);
        var id = (await J(a)).GetProperty("id").GetInt64();
        var dev = await J(await api.GetAsync($"/api/assets/{id}/device"));
        Assert.Equal("Found server", (await J(await api.GetAsync($"/api/assets/{id}"))).GetProperty("asset").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Object, dev.ValueKind);
    }

    [PgFact]
    public async Task CredentialsAreSealedAndNeverReturned()
    {
        var api = await Start();
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/assets/discover/creds", Json(new { name = "k", kind = "ssh", username = "root", secret = "not a key" }))).StatusCode);
        var ok = await api.PostAsync("/api/assets/discover/creds", Json(new { name = "Domain admin", kind = "windows", username = "CORP\\svc", secret = "S3cret-Pw!" }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var id = (await J(ok)).GetProperty("id").GetInt32();
        var list = await api.GetStringAsync("/api/assets/discover/creds");
        Assert.Contains("Domain admin", list); Assert.DoesNotContain("S3cret", list);
        await using (var db = new NpgsqlConnection(Pg)) { await db.OpenAsync(); await using var c = new NpgsqlCommand("select secret from assets.creds", db); Assert.DoesNotContain("S3cret", (string)(await c.ExecuteScalarAsync())!); }
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/assets/discover/collect", Json(new { target = "10.0.0.5", credId = 9999 }))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await api.DeleteAsync($"/api/assets/discover/creds/{id}")).StatusCode);
    }

    [PgFact]
    public async Task SiteCollectorEnrolsReportsAndItsDevicesAreAdopted()
    {
        var api = await Start();
        var co = (await J(await api.PostAsync("/api/assets/companies", Json(new { name = "Branch Ltd" })))).GetProperty("id").GetInt32();
        var tokRes = await J(await api.PostAsync("/api/assets/collectors/tokens", Json(new { companyId = co, label = "HQ" })));
        var tok = tokRes.GetProperty("token").GetString()!;
        Assert.StartsWith("dkct_", tok);

        var anon = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        // a collector token cannot enrol an agent, and an agent token cannot enrol a collector
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/agent/v1/enroll", Json(new { token = tok, inventory = new { hostname = "X1" } }))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/collector/v1/enroll", Json(new { token = "dkct_nope" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/collector/install.sh?t=" + tok)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/collector/install.sh?t=bad")).StatusCode);

        var en = await J(await anon.PostAsync("/collector/v1/enroll", Json(new { token = tok, name = "HQ collector", hostname = "COL-1", platform = "windows", version = "1.0.0" })));
        var cid = en.GetProperty("collectorId").GetString()!;
        var col = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        col.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", en.GetProperty("key").GetString()); col.DefaultRequestHeaders.Add("X-Collector-Id", cid);

        Assert.Equal(HttpStatusCode.BadRequest, (await api.PatchAsync($"/api/assets/collectors/{cid}", Json(new { scanCidrs = "10.0.0.0/8" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PatchAsync($"/api/assets/collectors/{cid}", Json(new { scanCidrs = "192.168.7.0/24", monitorDefault = "ping" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/assets/collectors/{cid}/scan", Json(new { }))).StatusCode);

        var ci = await J(await col.PostAsync("/collector/v1/checkin", Json(new { version = "1.0.1", found = new object[] { new { ip = "192.168.7.20", hostname = "printer1", open = new[] { 9100, 80 }, mac = "aa-bb-cc-00-11-22" }, new { ip = "192.168.7.30", hostname = "srv30", open = new[] { 22 } }, new { ip = "not-an-ip", open = new[] { 80 } } } })));
        Assert.True(ci.GetProperty("scan").GetProperty("cidrs").GetArrayLength() == 1);
        var found = await J(await api.GetAsync($"/api/assets/collectors/{cid}/found"));
        Assert.Equal(2, found.GetProperty("items").GetArrayLength());
        Assert.Equal("Printers & peripherals", found.GetProperty("items")[0].GetProperty("cat").GetString());

        var ad = await J(await api.PostAsync($"/api/assets/collectors/{cid}/adopt", Json(new { ips = new[] { "192.168.7.20", "192.168.7.99" } })));
        Assert.Equal(1, ad.GetProperty("added").GetInt32()); Assert.Equal(1, ad.GetProperty("skipped").GetInt32());
        var list = await J(await api.GetAsync("/api/assets?search=printer1"));
        var aid = list.GetProperty("items")[0].GetProperty("id").GetInt64();

        // service check on that device, run by the collector
        var chk = await api.PostAsync($"/api/assets/{aid}/checks", Json(new { name = "Web", type = "http", url = "http://192.168.7.20/" }));
        Assert.Equal(HttpStatusCode.OK, chk.StatusCode);
        var chkId = (await J(chk)).GetProperty("id").GetInt32();
        var work = await J(await col.PostAsync("/collector/v1/checkin", Json(new { })));
        Assert.Equal(1, work.GetProperty("assets").GetArrayLength());
        Assert.Equal("192.168.7.20", work.GetProperty("assets")[0].GetProperty("ip").GetString());
        Assert.Equal(1, work.GetProperty("checks").GetProperty(aid.ToString()).GetArrayLength());

        await col.PostAsync("/collector/v1/checkin", Json(new { results = new { health = new[] { new { asset = aid.ToString(), up = true, rtt_ms = 4.0 } }, checks = new[] { new { id = chkId, asset = aid.ToString(), ok = true, ms = 12.0, detail = "200 OK" } } } }));
        var checks = await J(await api.GetAsync($"/api/assets/{aid}/checks"));
        Assert.True(checks.GetProperty("collector").GetBoolean());
        Assert.True(checks.GetProperty("items")[0].GetProperty("lastOk").GetBoolean());
        Assert.Equal("200 OK", checks.GetProperty("items")[0].GetProperty("lastDetail").GetString());
        Assert.Single((await J(await api.GetAsync($"/api/assets/checks/{chkId}/results"))).GetProperty("items").EnumerateArray());

        // deploy an agent through the collector
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/assets/{aid}/agent-install", Json(new { }))).StatusCode);
        var w2 = await J(await col.PostAsync("/collector/v1/checkin", Json(new { })));
        Assert.Equal(1, w2.GetProperty("deploys").GetArrayLength());
        await col.PostAsync("/collector/v1/checkin", Json(new { deploys = new[] { new { asset = aid.ToString(), ok = false, message = "WinRM refused" } } }));
        Assert.Equal("failed", (await J(await api.GetAsync($"/api/assets/{aid}/agent-install"))).GetProperty("status").GetString());

        // a wrong key is refused; deleting the collector frees its devices
        var bad = factory.CreateClient(); bad.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "dkco_" + new string('x', 30)); bad.DefaultRequestHeaders.Add("X-Collector-Id", cid);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bad.PostAsync("/collector/v1/checkin", Json(new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await api.DeleteAsync($"/api/assets/collectors/{cid}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/assets/{aid}/monitor", Json(new { mode = "" }))).StatusCode);
    }

    [PgFact]
    public async Task ServiceChecksValidateAndTicketsAreLinked()
    {
        var api = await Start();
        var id = await NewAsset(api, "Web server");
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/assets/{id}/checks", Json(new { name = "RDP", type = "tcp", port = 0 }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/assets/{id}/checks", Json(new { name = "Site", type = "http", url = "ftp://x" }))).StatusCode);
        var ok = await api.PostAsync($"/api/assets/{id}/checks", Json(new { name = "RDP", type = "tcp", port = 3389 }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var cid = (await J(ok)).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/assets/{id}/checks", Json(new { name = "rdp", type = "tcp", port = 3390 }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PutAsync($"/api/assets/{id}/checks/{cid}", Json(new { name = "RDP", type = "tcp", port = 3390 }))).StatusCode);
        Assert.Equal(3390, (await J(await api.GetAsync($"/api/assets/{id}/checks"))).GetProperty("items")[0].GetProperty("port").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await api.DeleteAsync($"/api/assets/checks/{cid}")).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/assets/{id}/tickets", Json(new { title = "" }))).StatusCode);
        var tk = await J(await api.PostAsync($"/api/assets/{id}/tickets", Json(new { title = "Disk is full", priority = "high" })));
        Assert.StartsWith("TKT-", tk.GetProperty("ref").GetString()); Assert.True(tk.GetProperty("stub").GetBoolean());
        var l = await J(await api.GetAsync($"/api/assets/{id}/tickets"));
        Assert.Equal(1, l.GetProperty("openCount").GetInt32()); Assert.False(l.GetProperty("serviceDesk").GetBoolean());
        var lid = tk.GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await api.PatchAsync($"/api/assets/tickets/{lid}", Json(new { status = "resolved" }))).StatusCode);
        Assert.Equal(0, (await J(await api.GetAsync($"/api/assets/{id}/tickets"))).GetProperty("openCount").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PatchAsync($"/api/assets/tickets/{lid}", Json(new { status = "weird" }))).StatusCode);
    }

    [PgFact]
    public async Task QrLabelsHandoverAndLogs()
    {
        var api = await Start();
        var id = await NewAsset(api, "Laptop 7");
        var svg = await api.GetAsync($"/api/assets/qr/{id}/svg");
        Assert.Equal(HttpStatusCode.OK, svg.StatusCode); Assert.Contains("<svg", await svg.Content.ReadAsStringAsync());
        var info = await J(await api.GetAsync($"/api/assets/qr/{id}/info?content=full"));
        Assert.Contains("Laptop 7", info.GetProperty("text").GetString());
        Assert.Contains("/assets/" + id, (await J(await api.GetAsync($"/api/assets/qr/{id}/info?content=link"))).GetProperty("text").GetString());
        var page = await api.GetAsync($"/api/assets/qr/print?ids={id}&layout=sheet");
        Assert.Contains("A4", await page.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await api.GetAsync("/api/assets/qr/print")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync("/api/assets/qr/999999/svg")).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/assets/{id}/handover", Json(new { condition = "good" }))).StatusCode);   // nobody holds it yet
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/assets/{id}/allocate", Json(new { to = "" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/assets/{id}/allocate", Json(new { to = "Priya Nair", accessories = new[] { "charger", "bag" } }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/assets/{id}/allocate", Json(new { to = "Someone else" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/assets/{id}/handover", Json(new { condition = "broken" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/assets/{id}/handover", Json(new { condition = "fair", notes = "scratched lid" }))).StatusCode);
        Assert.Equal("", (await J(await api.GetAsync($"/api/assets/{id}"))).GetProperty("asset").GetProperty("assignedTo").GetString());
        var h = await J(await api.GetAsync($"/api/assets/{id}/handovers"));
        Assert.Equal(2, h.GetProperty("items").GetArrayLength()); Assert.Equal("handover", h.GetProperty("items")[0].GetProperty("kind").GetString());

        var logs = await J(await api.GetAsync($"/api/assets/{id}/logs"));
        Assert.Contains("handover", logs.GetProperty("kinds").EnumerateArray().Select(x => x.GetString()));
        Assert.All((await J(await api.GetAsync($"/api/assets/{id}/logs?kind=created"))).GetProperty("items").EnumerateArray(), x => Assert.Equal("created", x.GetProperty("kind").GetString()));
    }
}
