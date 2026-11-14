using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

/// <summary>
/// v2.14: the Assets dashboard (customer tiles, a customer's rings, the list a ring part opens), agent actions on many assets at once,
/// the patch report, and SNMPv3 logins reaching the site controller.
/// </summary>
public class AssetsDashboardTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    readonly WebApplicationFactory<Program> factory;
    public AssetsDashboardTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }

    async Task<HttpClient> Start()
    {
        var api = await TestSupport.AdminClient(factory);
        await api.GetAsync("/api/assets/types");
        await Sql("truncate assets.collectors, assets.creds, assets.agent_upd_log, assets.shells, assets.rc_log, assets.jobs, assets.agent_q, assets.agents, assets.enroll_tokens, assets.amc, assets.rentals, assets.history, assets.assets, assets.companies restart identity cascade");
        await Sql("insert into assets.types (name) values ('Printer') on conflict do nothing");
        return api;
    }

    /// <summary>Enrols an agent for the customer and returns (asset id, a client signed in as that agent).</summary>
    async Task<(long Asset, HttpClient Agent)> Enrol(HttpClient api, int company, string host)
    {
        var tok = (await J(await api.PostAsync("/api/assets/enrolments", Json(new { companyId = company })))).GetProperty("token").GetString()!;
        var agent = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var en = await J(await agent.PostAsync("/agent/v1/enroll", Json(new { token = tok, platform = "windows", version = "2.8.6", inventory = new { schema = 1, hostname = host, os_family = "windows", serial_number = "SN-" + host } })));
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", en.GetProperty("key").GetString()); agent.DefaultRequestHeaders.Add("X-Agent-Id", en.GetProperty("agentId").GetString());
        return (long.Parse(en.GetProperty("assetId").GetString()!), agent);
    }

    static long N(JsonElement m, string k) => m.GetProperty(k).GetInt64();

    [PgFact]
    public async Task TilesCountEachCustomerAndARingPartOpensTheSameAssetsInTheList()
    {
        var api = await Start();
        var acme = (await J(await api.PostAsync("/api/assets/companies", Json(new { name = "Acme Ltd" })))).GetProperty("id").GetInt32();
        var globex = (await J(await api.PostAsync("/api/assets/companies", Json(new { name = "Globex" })))).GetProperty("id").GetInt32();
        var (pc1, _) = await Enrol(api, acme, "PC-1");
        var (pc2, _) = await Enrol(api, acme, "PC-2");
        await Sql($"update assets.assets set antivirus_status = 'Protected', last_patch_date = current_date - 3, warranty_end = current_date + 400 where id = {pc1}");
        await Sql($"update assets.assets set antivirus_status = 'Not detected', last_patch_date = current_date - 90, warranty_end = current_date + 10 where id = {pc2}");
        await Sql($"update assets.agents set last_seen = now() - interval '3 days' where asset_id = {pc2}"); // not reporting
        var printer = (await J(await api.PostAsync("/api/assets", Json(new { name = "PRN-1", companyId = globex })))).GetProperty("id").GetInt64();
        await Sql($"update assets.assets set type_id = (select id from assets.types where name = 'Printer'), warranty_end = current_date - 5, company_id = {globex} where id = {printer}");
        var old = (await J(await api.PostAsync("/api/assets", Json(new { name = "OLD-1" })))).GetProperty("id").GetInt64();
        await Sql($"update assets.assets set status = 'retired' where id = {old}"); // no customer, retired: counted in total, not in the rings
        await Sql($"update assets.assets set hw_extra = '{{\"power_supplies\":[{{\"name\":\"PSU 1\",\"state\":\"Critical\"}}]}}' where id = {pc1}");

        var d = await J(await api.GetAsync("/api/assets/dashboard"));
        Assert.False(d.GetProperty("restricted").GetBoolean());
        var tiles = d.GetProperty("customers").EnumerateArray().ToDictionary(x => x.GetProperty("company").GetString()!);
        Assert.Equal(new[] { "Acme Ltd", "Globex", "No customer" }, tiles.Keys.OrderBy(x => x));
        var a = tiles["Acme Ltd"];
        Assert.Equal(2, N(a, "inService"));
        Assert.Equal(1, N(a, "agentOnline") + N(a, "agentUpdate")); Assert.Equal(1, N(a, "agentOffline")); Assert.Equal(0, N(a, "agentNone"));
        Assert.Equal(1, N(a, "compliant")); Assert.Equal(1, N(a, "nonCompliant"));
        Assert.Equal(1, N(a, "warrantyCovered")); Assert.Equal(1, N(a, "warrantySoon"));
        Assert.Equal(2, N(a, "computers")); Assert.Equal(1, N(a, "hardwareNotOk"));
        var g = tiles["Globex"];
        Assert.Equal(1, N(g, "agentNone")); Assert.Equal(1, N(g, "printers")); Assert.Equal(1, N(g, "warrantyExpired"));
        Assert.Equal(1, N(tiles["No customer"], "total")); Assert.Equal(0, N(tiles["No customer"], "inService"));
        var all = d.GetProperty("all"); Assert.Equal(4, N(all, "total")); Assert.Equal(3, N(all, "inService")); Assert.Equal(1, N(all, "agentOffline"));

        // one customer, and the list each ring part opens holds exactly the counted assets
        Assert.Equal("Acme Ltd", (await J(await api.GetAsync($"/api/assets/dashboard?company={acme}"))).GetProperty("customer").GetProperty("company").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync("/api/assets/dashboard?company=999")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.GetAsync("/api/assets/dashboard?company=abc")).StatusCode);
        async Task<long[]> Ids(string q) => (await J(await api.GetAsync("/api/assets?take=200&" + q))).GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetInt64()).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { pc1, pc2 }, await Ids($"company={acme}"));
        Assert.Equal(new[] { pc2 }, await Ids($"company={acme}&agent=offline"));
        Assert.Equal(new[] { pc2 }, await Ids($"company={acme}&compliance=noncompliant"));
        Assert.Equal(new[] { pc1 }, await Ids($"company={acme}&compliance=compliant"));
        Assert.Equal(new[] { pc2 }, await Ids("warranty=soon"));
        Assert.Equal(new[] { printer }, await Ids("kind=printer"));
        Assert.Equal(new[] { printer }, await Ids("warranty=expired"));
        Assert.Equal(new[] { pc1 }, await Ids("health=bad"));
        Assert.Equal(new[] { old }, await Ids("company=none"));
        Assert.Equal(HttpStatusCode.BadRequest, (await api.GetAsync("/api/assets?compliance=sometimes")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.GetAsync("/api/assets?company=x")).StatusCode);

        // the patch report: computers with an agent, oldest patch first
        var rep = await J(await api.GetAsync($"/api/assets/reports/patches?company={acme}"));
        Assert.Equal(2, rep.GetProperty("total").GetInt32()); Assert.Equal(1, rep.GetProperty("behind").GetInt32());
        Assert.Equal(1, rep.GetProperty("compliant").GetInt32()); Assert.Equal(1, rep.GetProperty("nonCompliant").GetInt32());
        Assert.Equal("PC-2", rep.GetProperty("items")[0].GetProperty("name").GetString());
        Assert.Equal(1, (await J(await api.GetAsync("/api/assets/reports/patches?show=behind"))).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await api.GetAsync("/api/assets/reports/patches?show=weird")).StatusCode);
        var csv = await api.GetStringAsync($"/api/assets/reports/patches.csv?company={acme}");
        Assert.StartsWith("Tag,Asset name,Host name,Customer", csv); Assert.Contains("PC-2", csv); Assert.Contains("Not compliant", csv);
    }

    [PgFact]
    public async Task AgentsOfManyAssetsAreBinnedOrUninstalledTogetherAndTheUninstallOrderReachesTheAgent()
    {
        var api = await Start();
        var acme = (await J(await api.PostAsync("/api/assets/companies", Json(new { name = "Acme Ltd" })))).GetProperty("id").GetInt32();
        var (pc1, ag1) = await Enrol(api, acme, "PC-1");
        var (pc2, ag2) = await Enrol(api, acme, "PC-2");
        var bare = (await J(await api.PostAsync("/api/assets", Json(new { name = "NO-AGENT" })))).GetProperty("id").GetInt64();

        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/assets/agents/bulk", Json(new { action = "explode", assetIds = new[] { pc1 } }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/assets/agents/bulk", Json(new { action = "bin", assetIds = Array.Empty<long>() }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/assets/agents/bulk", Json(new { action = "bin", assetIds = Enumerable.Range(1, 1001).ToArray() }))).StatusCode);

        // update: an asset without an agent is skipped with its reason
        var up = await J(await api.PostAsync("/api/assets/agents/bulk", Json(new { action = "update", assetIds = new[] { bare } })));
        Assert.Equal(0, up.GetProperty("done").GetInt32()); Assert.Equal(1, up.GetProperty("skipped").GetProperty("no_agent").GetInt32());

        // bin: the agent stops reporting, the asset stays, the agent is in the recycle bin
        var bin = await J(await api.PostAsync("/api/assets/agents/bulk", Json(new { action = "bin", assetIds = new[] { pc1, bare } })));
        Assert.Equal(1, bin.GetProperty("done").GetInt32()); Assert.Equal(1, bin.GetProperty("skipped").GetProperty("no_agent").GetInt32());
        Assert.Equal(HttpStatusCode.Unauthorized, (await ag1.GetAsync("/agent/v1/poll?wait=0")).StatusCode);
        Assert.Single((await J(await api.GetAsync("/api/assets/recycle"))).GetProperty("agents").EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync($"/api/assets/{pc1}/device")).StatusCode);

        // uninstall: the agent may still fetch its order once; it can no longer report; then its record goes
        await ag2.GetAsync("/agent/v1/poll?wait=0"); // drain
        Assert.Equal(1, (await J(await api.PostAsync("/api/assets/agents/bulk", Json(new { action = "uninstall", assetIds = new[] { pc2 } })))).GetProperty("done").GetInt32());
        Assert.Equal(1, (await J(await api.GetAsync($"/api/assets?agent=none&search=PC-2"))).GetProperty("total").GetInt64());
        Assert.Equal(HttpStatusCode.Unauthorized, (await ag2.PostAsync("/agent/v1/checkin", Json(new { inventory = new { schema = 1, hostname = "PC-2" } }))).StatusCode);
        var msgs = (await J(await ag2.GetAsync("/agent/v1/poll?wait=0"))).GetProperty("msgs").EnumerateArray().Select(m => m.GetProperty("type").GetString()).ToList();
        Assert.Equal(new[] { "uninstall" }, msgs);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ag2.GetAsync("/agent/v1/poll?wait=0")).StatusCode);
        Assert.DoesNotContain("PC-2", await api.GetStringAsync("/api/assets/agents"));
    }

    [PgFact]
    public async Task AnSnmpV3LoginIsCheckedAndReachesTheControllerWithItsProtocols()
    {
        var api = await Start();
        var acme = (await J(await api.PostAsync("/api/assets/companies", Json(new { name = "Acme Ltd" })))).GetProperty("id").GetInt32();
        async Task<HttpResponseMessage> Add(object o) => await api.PostAsync("/api/assets/discover/creds", Json(o));
        Assert.Equal(HttpStatusCode.BadRequest, (await Add(new { name = "v3", kind = "snmp3", category = "switch", username = "mon", secret = "short", authProto = "SHA", privProto = "AES", privSecret = "privpass1", companyId = acme })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Add(new { name = "v3", kind = "snmp3", category = "switch", username = "mon", secret = "authpass1", authProto = "SHA512", privProto = "AES", privSecret = "privpass1", companyId = acme })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Add(new { name = "v3", kind = "snmp3", category = "switch", username = "mon", secret = "authpass1", authProto = "SHA", privProto = "AES", privSecret = "", companyId = acme })).StatusCode);
        Assert.True((await Add(new { name = "Switch v3", kind = "snmp3", category = "switch", username = "mon", secret = "authpass1", authProto = "SHA256", privProto = "AES", privSecret = "privpass1", companyId = acme })).IsSuccessStatusCode);
        var listed = (await J(await api.GetAsync("/api/assets/discover/creds"))).GetProperty("items").EnumerateArray().Single();
        Assert.Equal("SHA256", listed.GetProperty("authProto").GetString()); Assert.Equal("AES", listed.GetProperty("privProto").GetString());
        var text = await api.GetStringAsync("/api/assets/discover/creds");
        Assert.DoesNotContain("authpass1", text); Assert.DoesNotContain("privpass1", text);

        var tok = (await J(await api.PostAsync("/api/assets/collectors/tokens", Json(new { companyId = acme, label = "HQ" })))).GetProperty("token").GetString()!;
        var anon = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var en = await J(await anon.PostAsync("/collector/v1/enroll", Json(new { token = tok, name = "HQ controller", hostname = "COL-1", platform = "windows", version = "2.14.0" })));
        var cid = en.GetProperty("collectorId").GetString()!;
        var col = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        col.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", en.GetProperty("key").GetString()); col.DefaultRequestHeaders.Add("X-Collector-Id", cid);
        await col.PostAsync("/collector/v1/checkin", Json(new { found = new object[] { new { ip = "192.168.40.2", hostname = "core-sw", open = new[] { 22, 161 }, mac = "" } } }));
        await api.PostAsync($"/api/assets/collectors/{cid}/adopt", Json(new { ips = new[] { "192.168.40.2" } }));
        var aid = (await J(await api.GetAsync("/api/assets"))).GetProperty("items").EnumerateArray().Single().GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/assets/{aid}/monitor", Json(new { mode = "network", collectorId = cid }))).StatusCode);

        var login = (await J(await col.PostAsync("/collector/v1/checkin", Json(new { })))).GetProperty("assets").EnumerateArray().Single().GetProperty("logins").EnumerateArray().Single(l => l.GetProperty("kind").GetString() == "snmp3");
        Assert.Equal("mon", login.GetProperty("user").GetString()); Assert.Equal("authpass1", login.GetProperty("secret").GetString());
        Assert.Equal("SHA256", login.GetProperty("authProto").GetString()); Assert.Equal("AES", login.GetProperty("privProto").GetString()); Assert.Equal("privpass1", login.GetProperty("privSecret").GetString());

        // changing the name keeps both passwords; the privacy protocol can be turned off
        var id = listed.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await api.PutAsync($"/api/assets/discover/creds/{id}", Json(new { name = "Core switch v3", category = "switch", authProto = "SHA", privProto = "", companyId = acme }))).StatusCode);
        var again = (await J(await col.PostAsync("/collector/v1/checkin", Json(new { })))).GetProperty("assets")[0].GetProperty("logins").EnumerateArray().Single(l => l.GetProperty("kind").GetString() == "snmp3");
        Assert.Equal("SHA", again.GetProperty("authProto").GetString()); Assert.Equal("", again.GetProperty("privProto").GetString()); Assert.Equal("authpass1", again.GetProperty("secret").GetString());
    }
}
