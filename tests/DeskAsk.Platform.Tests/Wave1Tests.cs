using System.Net;
using System.Text;
using System.Text.Json;
using DeskAsk.Platform.Monitoring;
using DeskAsk.Platform.ServiceDesk;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

/// <summary>6.22.0: alert routes and working-hours settings (no database).</summary>
public class Wave1RuleTests
{
    [Fact]
    public void TheFirstRouteThatFitsDecidesWhoIsMailedAndOtherAlertsGoToTheUsualList()
    {
        var m = new MonitorSettings { Recipients = "noc@x.test", Companies = new() { ["7"] = "it@acme.test" } };
        m.Routes.Add(new AlertRoute { Name = "Printers", Kinds = new[] { "printer" }, To = "local@x.test" });
        m.Routes.Add(new AlertRoute { Name = "Acme critical", CompanyId = 7, Severity = "critical", To = "boss@acme.test" });
        m.Routes.Add(new AlertRoute { Name = "Network", Kinds = new[] { "network", "server" }, To = "net@x.test; sys@x.test" });
        Assert.Equal(new[] { "local@x.test" }, m.For(7, "printer", "critical"));                 // the first fitting route wins, even for a customer with its own list
        Assert.Equal(new[] { "boss@acme.test" }, m.For(7, "computer", "critical"));
        Assert.Equal(new[] { "net@x.test", "sys@x.test" }, m.For(9, "server", "warning"));
        Assert.Equal(new[] { "it@acme.test" }, m.For(7, "computer", "warning"));                 // no route fits: the customer's own list
        Assert.Equal(new[] { "noc@x.test" }, m.For(9, "computer", "warning"));                   // no route, no own list: the default
    }

    [Fact]
    public void WorkingHoursAreCheckedWhenTheServiceLevelsAreSaved()
    {
        JsonElement Body(string hours) => JsonDocument.Parse($@"{{""respond"":{{""critical"":1,""high"":2,""medium"":4,""low"":8}},""resolve"":{{""critical"":4,""high"":8,""medium"":24,""low"":72}},""hours"":{hours}}}").RootElement;
        var ok = SlaConfig.Parse(Body(@"{""enabled"":true,""start"":""08:30"",""end"":""17:00"",""days"":[5,1,2],""zone"":""Europe/London""}"), out var err);
        Assert.NotNull(ok); Assert.Null(err);
        Assert.True(ok!.Business); Assert.Equal(8 * 60 + 30, ok.StartMin); Assert.Equal(17 * 60, ok.EndMin); Assert.Equal(new[] { 1, 2, 5 }, ok.Days); Assert.Equal("Europe/London", ok.Zone);
        Assert.Null(SlaConfig.Parse(Body(@"{""enabled"":true,""start"":""18:00"",""end"":""09:00""}"), out err)); Assert.Contains("end after", err);
        Assert.Null(SlaConfig.Parse(Body(@"{""enabled"":true,""days"":[]}"), out err)); Assert.Contains("one working day", err);
        Assert.Null(SlaConfig.Parse(Body(@"{""enabled"":true,""days"":[8]}"), out err)); Assert.Contains("1 (Monday)", err);
        Assert.Null(SlaConfig.Parse(Body(@"{""enabled"":true,""zone"":""Mars/Olympus""}"), out err)); Assert.Contains("not known", err);
        var off = SlaConfig.Parse(Body(@"{""enabled"":false}"), out err); Assert.NotNull(off); Assert.False(off!.Business);
    }
}

/// <summary>6.22.0: canned replies, the working-hours function, CMDB relationships and software rules.</summary>
public class Wave1Tests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    readonly WebApplicationFactory<Program> factory;
    public Wave1Tests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); });
    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }
    static async Task<object?> Scalar(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); return await c.ExecuteScalarAsync(); }

    [PgFact]
    public async Task CannedRepliesAreKeptByAdministratorsAndTitlesAreUnique()
    {
        var admin = await TestSupport.AdminClient(factory);
        await admin.GetAsync("/api/servicedesk/me");
        await Sql("truncate servicedesk.canned restart identity");
        var made = await admin.PostAsync("/api/servicedesk/canned", Json(new { title = "Restart first", body = "Hello {requester}, please restart and tell us on {number}. {agent}" }));
        Assert.Equal(HttpStatusCode.OK, made.StatusCode); var id = (await J(made)).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/servicedesk/canned", Json(new { title = "restart FIRST", body = "again" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/servicedesk/canned", Json(new { title = "", body = "x" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/servicedesk/canned", Json(new { title = "Hidden", body = "not offered", active = false }))).StatusCode);
        var offered = await J(await admin.GetAsync("/api/servicedesk/canned"));
        Assert.Single(offered.EnumerateArray());                                                  // switched-off replies are not offered in the reply box
        Assert.Equal(2, (await J(await admin.GetAsync("/api/servicedesk/canned?all=1"))).GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/servicedesk/canned/{id}", Json(new { title = "Restart it first", body = "New text" }))).StatusCode);
        Assert.Equal("Restart it first", (await J(await admin.GetAsync("/api/servicedesk/canned"))).EnumerateArray().Single().GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/servicedesk/canned/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/servicedesk/canned/{id}")).StatusCode);
    }

    [PgFact]
    public async Task TargetsCountOnlyWorkingTimeWhenWorkingHoursAreOn()
    {
        var admin = await TestSupport.AdminClient(factory);
        await admin.GetAsync("/api/servicedesk/me");
        const string days = "'{1,2,3,4,5}'::int[]";
        // Friday 17:00 + 3 working hours: one hour that day, two on Monday morning
        Assert.Equal(new DateTime(2026, 11, 30, 11, 0, 0, DateTimeKind.Utc), (DateTime)(await Scalar($"select servicedesk.work_due('2026-11-27 17:00+00', 10800, true, 540, 1080, {days}, 'UTC') at time zone 'UTC'"))!);
        // opened on a Saturday: the clock starts on Monday at 09:00
        Assert.Equal(new DateTime(2026, 11, 30, 10, 0, 0, DateTimeKind.Utc), (DateTime)(await Scalar($"select servicedesk.work_due('2026-11-28 10:00+00', 3600, true, 540, 1080, {days}, 'UTC') at time zone 'UTC'"))!);
        // inside the day, nothing moves; with working hours off it is plain clock time
        Assert.Equal(new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc), (DateTime)(await Scalar($"select servicedesk.work_due('2026-11-26 10:00+00', 7200, true, 540, 1080, {days}, 'UTC') at time zone 'UTC'"))!);
        Assert.Equal(new DateTime(2026, 11, 28, 13, 0, 0, DateTimeKind.Utc), (DateTime)(await Scalar($"select servicedesk.work_due('2026-11-28 10:00+00', 10800, false, 540, 1080, {days}, 'UTC') at time zone 'UTC'"))!);
        // a zone moves the working day: 09:00 to 18:00 in Kolkata is 03:30 to 12:30 UTC
        Assert.Equal(new DateTime(2026, 11, 26, 4, 30, 0, DateTimeKind.Utc), (DateTime)(await Scalar($"select servicedesk.work_due('2026-11-26 02:00+00', 3600, true, 540, 1080, {days}, 'Asia/Kolkata') at time zone 'UTC'"))!);

        // saving the service levels with hours keeps them, and bad hours are refused
        var cur = await J(await admin.GetAsync("/api/servicedesk/settings")); var sla = JsonSerializer.Deserialize<Dictionary<string, object>>(cur.GetProperty("sla").GetRawText())!;
        sla["hours"] = new { enabled = true, start = "09:00", end = "18:00", days = new[] { 1, 2, 3, 4, 5 }, zone = "Asia/Kolkata" };
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/servicedesk/settings", Json(new { sla }))).StatusCode);
        var hours = (await J(await admin.GetAsync("/api/servicedesk/settings"))).GetProperty("sla").GetProperty("hours");
        Assert.True(hours.GetProperty("enabled").GetBoolean()); Assert.Equal("Asia/Kolkata", hours.GetProperty("zone").GetString());
        sla["hours"] = new { enabled = true, start = "18:00", end = "09:00" };
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/servicedesk/settings", Json(new { sla }))).StatusCode);
        sla["hours"] = new { enabled = false };
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/servicedesk/settings", Json(new { sla }))).StatusCode);
    }

    [PgFact]
    public async Task RelationshipsShowWhatIsAffectedWhenSomethingFails()
    {
        var admin = await TestSupport.AdminClient(factory);
        await admin.GetAsync("/api/assets/types");
        async Task<long> Make(string name) => (await J(await admin.PostAsync("/api/assets", Json(new { name })))).GetProperty("id").GetInt64();
        var host = await Make("rel-host"); var vm = await Make("rel-vm"); var app = await Make("rel-app"); var sw = await Make("rel-switch"); var alone = await Make("rel-alone");
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/assets/{vm}/relations", Json(new { toId = host, kind = "runs_on" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/assets/{app}/relations", Json(new { toId = vm, kind = "depends_on" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/assets/{host}/relations", Json(new { toId = sw, kind = "connected_to" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/assets/{vm}/relations", Json(new { toId = host, kind = "runs_on" }))).StatusCode);   // twice
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/assets/{vm}/relations", Json(new { toId = vm, kind = "runs_on" }))).StatusCode);     // itself
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/assets/{vm}/relations", Json(new { toId = alone, kind = "loves" }))).StatusCode);

        // if the host fails, the VM (directly), the application (through the VM) and the switch it is connected to are affected; the unrelated asset is not
        var impact = (await J(await admin.GetAsync($"/api/assets/{host}/impact"))).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(new[] { "rel-app", "rel-switch", "rel-vm" }, impact.Select(x => x.GetProperty("name").GetString()!).OrderBy(x => x).ToArray());
        Assert.Equal(1, impact.Single(x => x.GetProperty("name").GetString() == "rel-vm").GetProperty("depth").GetInt32());
        Assert.Equal(2, impact.Single(x => x.GetProperty("name").GetString() == "rel-app").GetProperty("depth").GetInt32());
        // a failing application affects nothing that runs on it the other way round
        Assert.Empty((await J(await admin.GetAsync($"/api/assets/{app}/impact"))).GetProperty("items").EnumerateArray());

        var rel = await J(await admin.GetAsync($"/api/assets/{vm}/relations"));
        Assert.Single(rel.GetProperty("outgoing").EnumerateArray()); Assert.Single(rel.GetProperty("incoming").EnumerateArray());
        var rid = rel.GetProperty("outgoing")[0].GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/assets/relations/{rid}")).StatusCode);
        var after = (await J(await admin.GetAsync($"/api/assets/{host}/impact"))).GetProperty("items").EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToArray();
        Assert.Equal(new[] { "rel-switch" }, after);                                              // the VM and its application left the picture
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/assets/relations/{rid}")).StatusCode);
    }

    [PgFact]
    public async Task RequiredAndProhibitedSoftwareListTheComputersThatBreakTheRule()
    {
        var admin = await TestSupport.AdminClient(factory);
        await admin.GetAsync("/api/assets/types");
        await Sql("truncate assets.packages restart identity cascade");
        async Task<long> Pc(string name, string apps)
        {
            var id = (await J(await admin.PostAsync("/api/assets", Json(new { name })))).GetProperty("id").GetInt64();
            await Sql($"update assets.assets set os_family = 'windows', installed_apps = '{apps}'::jsonb where id = {id}");
            return id;
        }
        var good = await Pc("rule-good", "[{\"name\":\"Wazuh Agent\",\"version\":\"4.8\"}]");
        var bad = await Pc("rule-bad", "[{\"name\":\"uTorrent Classic\",\"version\":\"3.6\"}]");
        var silent = (await J(await admin.PostAsync("/api/assets", Json(new { name = "rule-silent" })))).GetProperty("id").GetInt64();   // never reported a program list
        await Sql($"update assets.assets set os_family = 'windows' where id = {silent}");

        var wazuh = (await J(await admin.PostAsync("/api/assets/packages", Json(new { name = "Wazuh agent", os = "windows", kind = "winget", source = "Wazuh.Agent" })))).GetProperty("id").GetInt32();
        var torrent = (await J(await admin.PostAsync("/api/assets/packages", Json(new { name = "uTorrent", os = "windows", kind = "winget", source = "BitTorrent.uTorrent" })))).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync($"/api/assets/packages/{wazuh}", Json(new { rule = "sometimes" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/assets/packages/{wazuh}", Json(new { rule = "required", matchName = "wazuh agent" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/assets/packages/{torrent}", Json(new { rule = "prohibited" }))).StatusCode);

        var items = (await J(await admin.GetAsync("/api/assets/packages/violations"))).GetProperty("items").EnumerateArray().ToList();
        string Pair(JsonElement x) => x.GetProperty("rule").GetString() + ":" + x.GetProperty("asset").GetString();
        Assert.Contains("required:rule-bad", items.Select(Pair));                                // has no Wazuh agent
        Assert.Contains("prohibited:rule-bad", items.Select(Pair));                              // has uTorrent
        Assert.DoesNotContain("required:rule-good", items.Select(Pair));
        Assert.DoesNotContain(items, x => x.GetProperty("asset").GetString() == "rule-silent");    // nothing is known about it, so nothing is claimed
        Assert.Equal("3.6", items.Single(x => Pair(x) == "prohibited:rule-bad").GetProperty("version").GetString());
        Assert.NotEqual(good, bad);
    }
}
