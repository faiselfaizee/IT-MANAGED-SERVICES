using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using DeskAsk.Platform;
using DeskAsk.Platform.Monitoring;
using Npgsql;
using Xunit;

/// <summary>The rule logic on its own (no database).</summary>
public class MonitoringRuleTests
{
    static DeviceState Dev(long id = 1, string kind = "server", bool agent = true, DateTime? seen = null, bool? ping = null, DateTime? down = null, double? cpu = 20, double? mem = 30,
        DiskUse[]? disks = null, CheckState[]? checks = null, PartFault[]? faults = null, int? company = 1) =>
        new(id, "AST-" + id, "SRV-" + id, company, "Acme", kind, "10.0.0." + id, "Windows Server 2022", agent, "3.0.0", seen ?? Now, ping, down, 1, Now, Now, cpu, mem, 16L << 30, 3600,
            disks ?? Array.Empty<DiskUse>(), checks ?? Array.Empty<CheckState>(), faults ?? Array.Empty<PartFault>());
    static readonly DateTime Now = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
    static Rule R(int id, string metric, double th = 0, int mins = 0, string sev = "warning", string[]? kinds = null, int? company = null, bool on = true) =>
        new(id, metric + id, on, metric, th, mins, sev, kinds ?? Array.Empty<string>(), company, true, false, "high", false);
    static readonly Func<int, IReadOnlyDictionary<long, Sustained>> NoSustained = _ => new Dictionary<long, Sustained>();
    static List<Condition> Run(IReadOnlyList<DeviceState> d, IReadOnlyList<Rule> r, Func<int, IReadOnlyDictionary<long, Sustained>>? s = null, ISet<long>? muted = null) =>
        Evaluator.Conditions(d, r, s ?? NoSustained, muted ?? new HashSet<long>(), Now);

    [Fact]
    public void DownMeansNoPingForTheTimeOrAnAgentSilentWhileNothingElseSaysItIsUp()
    {
        var rule = R(1, "offline", mins: 5, sev: "critical");
        Assert.Single(Run(new[] { Dev(ping: false, down: Now.AddMinutes(-6)) }, new[] { rule }));
        Assert.Empty(Run(new[] { Dev(ping: false, down: Now.AddMinutes(-3)) }, new[] { rule }));                 // not long enough
        Assert.Single(Run(new[] { Dev(seen: Now.AddMinutes(-20)) }, new[] { rule }));                              // agent silent, no controller
        Assert.Empty(Run(new[] { Dev(seen: Now.AddMinutes(-20), ping: true) }, new[] { rule }));                   // the controller still reaches it
        var c = Run(new[] { Dev(seen: Now.AddMinutes(-90)) }, new[] { rule }).Single();
        Assert.Equal("The agent has not reported for 1 hour 30 minutes.", c.Text);
        Assert.Equal("1:offline:", c.Key);
        Assert.Empty(Run(new[] { Dev(agent: false, ping: null) }, new[] { rule }));                                // not watched at all
        Assert.Empty(Run(new[] { Dev(ping: false, down: Now.AddHours(-1)) }, new[] { rule }, muted: new HashSet<long> { 1 }));
    }

    [Fact]
    public void ProcessorAboveMeansTheLowestSampleInTheWindowWasAbove()
    {
        var rule = R(1, "cpu", 90, 15);
        Sustained S(double min, int n, int firstMinAgo) => new(min, 10, n, Now.AddMinutes(-firstMinAgo));
        Assert.Single(Run(new[] { Dev(cpu: 97) }, new[] { rule }, _ => new Dictionary<long, Sustained> { [1] = S(93, 15, 14) }));
        Assert.Empty(Run(new[] { Dev(cpu: 97) }, new[] { rule }, _ => new Dictionary<long, Sustained> { [1] = S(40, 15, 14) }));  // dipped
        Assert.Empty(Run(new[] { Dev(cpu: 97) }, new[] { rule }, _ => new Dictionary<long, Sustained> { [1] = S(95, 2, 3) }));    // only 3 minutes of data
        Assert.Single(Run(new[] { Dev(mem: 95) }, new[] { R(2, "memory", 90, 0) }));                                              // no duration: the latest value
        var stale = Dev(cpu: 99) with { MetricsAt = Now.AddMinutes(-30) };
        Assert.Empty(Run(new[] { stale }, new[] { R(3, "cpu", 90, 0) }));                                                          // old data proves nothing
    }

    [Fact]
    public void TheMostSevereRuleWinsPerDiskAndKindsAndCustomersLimitARule()
    {
        var disks = new[] { new DiskUse("C:", 3, 3L << 30, 100L << 30), new DiskUse("D:", 8, 8L << 30, 100L << 30), new DiskUse("E:", 50, 50L << 30, 100L << 30) };
        var c = Run(new[] { Dev(disks: disks) }, new[] { R(1, "disk", 10), R(2, "disk", 5, sev: "critical") });
        Assert.Equal(2, c.Count);
        Assert.Equal("critical", c.Single(x => x.Subject == "C:").Severity);
        Assert.Equal("warning", c.Single(x => x.Subject == "D:").Severity);
        Assert.Equal("C: has 3% free (3 GB of 100 GB).", c.Single(x => x.Subject == "C:").Text);
        Assert.Empty(Run(new[] { Dev(kind: "computer", disks: disks) }, new[] { R(1, "disk", 10, kinds: new[] { "server" }) }));
        Assert.Empty(Run(new[] { Dev(company: 2, disks: disks) }, new[] { R(1, "disk", 10, company: 1) }));
        Assert.Empty(Run(new[] { Dev(disks: disks) }, new[] { R(1, "disk", 10, on: false) }));
    }

    [Fact]
    public void ChecksCertificatesAndHardwareFaults()
    {
        var checks = new[] { new CheckState(7, "Web", "http", false, 3, null, "timeout", Now), new CheckState(8, "Portal cert", "ssl", true, 0, 5, "", Now), new CheckState(9, "Old cert", "ssl", false, 1, -2, "expired", Now) };
        var c = Run(new[] { Dev(checks: checks) }, new[] { R(1, "check", 2, sev: "critical"), R(2, "cert", 14) });
        Assert.Equal("Service check \"Web\" failed 3 times in a row: timeout", c.Single(x => x.Subject == "check:7").Text);
        Assert.DoesNotContain(c, x => x.Subject == "check:9");                                     // failed once only
        Assert.Equal("warning", c.Single(x => x.Subject == "cert:8").Severity);
        Assert.Equal("critical", c.Single(x => x.Subject == "cert:9").Severity);                    // already expired
        var hw = Run(new[] { Dev(faults: new[] { new PartFault("power_supplies", "PSU 2", "Critical"), new PartFault("fans", "Fan 3", "Warning") }) }, new[] { R(3, "hardware", sev: "critical") });
        Assert.Equal("critical", hw.Single(x => x.Subject == "hw:power_supplies:PSU 2").Severity);
        Assert.Equal("Fan \"Fan 3\" reports warning.", hw.Single(x => x.Subject == "hw:fans:Fan 3").Text);
        Assert.Equal("warning", hw.Single(x => x.Subject == "hw:fans:Fan 3").Severity);
    }
}

/// <summary>From an agent's performance sample to an alert, mail and ticket, and back to clear; mutes, retirement, access.</summary>
public class MonitoringTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    const string Pw = "Correct-Horse-9";
    readonly WebApplicationFactory<Program> factory;
    public MonitoringTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }
    static async Task<long> Scalar(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); return Convert.ToInt64(await c.ExecuteScalarAsync()); }

    [PgFact]
    public async Task ASampleOpensAlertsThatMailOpenATicketAndClearByThemselves()
    {
        var api = await TestSupport.AdminClient(factory);
        await api.GetAsync("/api/assets/types"); await api.GetAsync("/api/monitoring/rules");
        await Sql("truncate assets.metrics, assets.metrics_hour, assets.agent_upd_log, assets.shells, assets.rc_log, assets.jobs, assets.agent_q, assets.agents, assets.enroll_tokens, assets.history, assets.assets, assets.companies restart identity cascade");
        // workers of earlier test hosts keep running: hold off their minute-by-minute evaluation so only this test evaluates
        await Sql(@"insert into jobs.recurring (name, type, interval_seconds, next_run) values ('monitoring.evaluate', 'monitoring.evaluate', 60, now() + interval '1 day')
            on conflict (name) do update set next_run = now() + interval '1 day'; delete from jobs.queue where type = 'monitoring.evaluate' and status = 'queued'");
        await Sql("truncate monitoring.alerts, monitoring.mutes restart identity; truncate mail.outbox restart identity; delete from identity.users where \"Email\" like '%@mon.test'");

        // the starting rules are there
        var rules = (await J(await api.GetAsync("/api/monitoring/rules"))).GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(rules, x => x.GetProperty("name").GetString() == "Server or network device down" && x.GetProperty("builtin").GetBoolean());
        Assert.Contains(rules, x => x.GetProperty("name").GetString() == "Disk space below 5%" && x.GetProperty("description").GetString() == "Less than 5% free on a disk or datastore of any device");
        await Sql("delete from monitoring.rules");
        async Task<int> Rule(object o) { var r = await api.PostAsync("/api/monitoring/rules", Json(o)); Assert.Equal(HttpStatusCode.OK, r.StatusCode); return (await J(r)).GetProperty("id").GetInt32(); }
        await Rule(new { name = "Memory high", metric = "memory", threshold = 90, minutes = 0, severity = "warning", kinds = new[] { "server" } });
        await Rule(new { name = "Disk low", metric = "disk", threshold = 10, severity = "warning" });
        await Rule(new { name = "Disk very low", metric = "disk", threshold = 5, severity = "critical", ticket = true, ticketPriority = "high" });
        await Rule(new { name = "Server down", metric = "offline", minutes = 5, severity = "critical", kinds = new[] { "server" } });
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/monitoring/rules", Json(new { name = "x", metric = "temperature", severity = "warning" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/monitoring/rules", Json(new { name = "x", metric = "cpu", threshold = 150, minutes = 5, severity = "warning" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/monitoring/rules", Json(new { name = "x", metric = "offline", minutes = 1, severity = "warning" }))).StatusCode);

        using var smtp = new FakeSmtp();
        Assert.Equal(HttpStatusCode.OK, (await api.PutAsync("/api/platform/mail/config", Json(new { provider = "smtp", from = "desk@example.test", fromName = "DeskAsk", host = "127.0.0.1", port = smtp.Port, security = "none", user = "", password = "" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PutAsync("/api/monitoring/settings", Json(new { recipients = "ops@mon.test, not-an-address", mailResolved = true }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PutAsync("/api/monitoring/settings", Json(new { recipients = "ops@mon.test", mailResolved = true }))).StatusCode);

        // a server with an agent sends a sample: memory 95%, C: 3% free
        var acme = (await J(await api.PostAsync("/api/assets/companies", Json(new { name = "Acme Ltd" })))).GetProperty("id").GetInt32();
        var tok = (await J(await api.PostAsync("/api/assets/enrolments", Json(new { companyId = acme })))).GetProperty("token").GetString()!;
        var agent = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var en = await J(await agent.PostAsync("/agent/v1/enroll", Json(new { token = tok, platform = "windows", version = "3.0.0", inventory = new { schema = 1, hostname = "SRV-DB1", os_family = "windows", os_name = "Windows Server 2022 Standard", serial_number = "SN-MON-1" } })));
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", en.GetProperty("key").GetString()); agent.DefaultRequestHeaders.Add("X-Agent-Id", en.GetProperty("agentId").GetString());
        var asset = long.Parse(en.GetProperty("assetId").GetString()!);
        async Task Sample(double mem, double cFree)
        {
            var r = await agent.PostAsync("/agent/v1/metrics", Json(new { cpu = 42.5, mem_used = (long)(mem * 160_000_000), mem_total = 16_000_000_000L, uptime = 7200, disks = new[] { new { mount = "C:", total = 100_000_000_000L, free = (long)(cFree * 1_000_000_000) }, new { mount = "D:", total = 500_000_000_000L, free = 400_000_000_000L } } }));
            Assert.Equal(HttpStatusCode.OK, r.StatusCode); Assert.Equal(60, (await J(r)).GetProperty("every").GetInt32());
        }
        await Sample(95, 3);
        var anon = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/agent/v1/metrics", Json(new { cpu = 1 }))).StatusCode);

        var ev = await J(await api.PostAsync("/api/monitoring/evaluate", Json(new { })));
        Assert.Equal(2, ev.GetProperty("opened").GetInt32()); // memory, and C: as critical only (the 5% rule wins over 10%)
        var open = (await J(await api.GetAsync("/api/monitoring/alerts"))).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, open.Count);
        var disk = open.Single(x => x.GetProperty("metric").GetString() == "disk");
        Assert.Equal("critical", disk.GetProperty("severity").GetString()); Assert.Equal("C:", disk.GetProperty("subject").GetString());
        Assert.StartsWith("TKT-", disk.GetProperty("ticket").GetString());
        Assert.Equal(1, await Scalar("select count(*) from servicedesk.tickets where source = 'monitoring' and asset_id = " + asset + " and priority = 'high'"));
        Assert.Equal("", open.Single(x => x.GetProperty("metric").GetString() == "memory").GetProperty("ticket").GetString());
        Assert.Equal(2, await Scalar("select count(*) from mail.outbox where reference like 'mon-alert:%' and to_addr = 'ops@mon.test'"));
        Assert.Equal(1, await Scalar("select count(*) from mail.outbox where subject = '[Critical] Acme Ltd: SRV-DB1 - Disk very low'"));
        Assert.Equal(0, (await J(await api.PostAsync("/api/monitoring/evaluate", Json(new { })))).GetProperty("opened").GetInt32()); // still the same two, no new mail
        Assert.Equal(2, await Scalar("select count(*) from mail.outbox where reference like 'mon-alert:%'"));

        // the dashboard and the list agree
        var dash = await J(await api.GetAsync("/api/monitoring/dashboard"));
        var tile = dash.GetProperty("customers").EnumerateArray().Single(x => x.GetProperty("company").GetString() == "Acme Ltd");
        Assert.Equal(1, tile.GetProperty("critical").GetInt32()); Assert.Equal(1, tile.GetProperty("alertsCritical").GetInt32()); Assert.Equal(1, tile.GetProperty("alertsWarning").GetInt32());
        Assert.Equal(1, tile.GetProperty("diskCritical").GetInt32()); Assert.Equal(1, tile.GetProperty("byMetric").GetProperty("memory").GetInt32());
        Assert.Equal(1, (await J(await api.GetAsync($"/api/monitoring/devices?company={acme}&status=critical"))).GetProperty("total").GetInt32());
        Assert.Equal(1, (await J(await api.GetAsync("/api/monitoring/devices?disk=critical"))).GetProperty("total").GetInt32());
        Assert.Equal(0, (await J(await api.GetAsync("/api/monitoring/devices?status=ok"))).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await api.GetAsync("/api/monitoring/devices?status=sleepy")).StatusCode);
        var dev = (await J(await api.GetAsync($"/api/monitoring/devices/{asset}"))).GetProperty("device");
        Assert.Equal("server", dev.GetProperty("kind").GetString()); Assert.Equal(42.5, dev.GetProperty("cpu").GetDouble()); Assert.Equal(95, dev.GetProperty("mem").GetDouble(), 1);
        Assert.Equal(3, dev.GetProperty("worstDisk").GetDouble(), 1); Assert.Equal(2, dev.GetProperty("disks").GetArrayLength());
        var series = await J(await api.GetAsync($"/api/monitoring/devices/{asset}/series?range=6h"));
        Assert.Single(series.GetProperty("points").EnumerateArray()); Assert.False(series.GetProperty("hourly").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await api.GetAsync($"/api/monitoring/devices/{asset}/series?range=1y")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync("/api/monitoring/devices/999999")).StatusCode);

        // acknowledge once
        var memId = open.Single(x => x.GetProperty("metric").GetString() == "memory").GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/monitoring/alerts/{memId}/ack", Json(new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/monitoring/alerts/{memId}/ack", Json(new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/monitoring/alerts/{memId}/ticket", Json(new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/monitoring/alerts/{memId}/ticket", Json(new { }))).StatusCode); // has one now

        // healthy again: closes after two clear runs, mails "cleared", notes the tickets
        await Sample(40, 50);
        Assert.Equal(0, (await J(await api.PostAsync("/api/monitoring/evaluate", Json(new { })))).GetProperty("closed").GetInt32());
        Assert.Equal(2, (await J(await api.PostAsync("/api/monitoring/evaluate", Json(new { })))).GetProperty("closed").GetInt32());
        Assert.Equal(0, (await J(await api.GetAsync("/api/monitoring/alerts"))).GetProperty("total").GetInt64());
        var closed = (await J(await api.GetAsync("/api/monitoring/alerts?state=closed"))).GetProperty("items").EnumerateArray().ToList();
        Assert.All(closed, x => Assert.Equal("Cleared", x.GetProperty("closeReason").GetString()));
        Assert.Equal(2, await Scalar("select count(*) from mail.outbox where subject like '[Cleared]%'"));
        Assert.Equal(2, await Scalar("select count(*) from servicedesk.comments where author_name = 'Monitoring' and body like 'The alert has cleared after%'"));
        Assert.Equal("ok", (await J(await api.GetAsync($"/api/monitoring/devices/{asset}"))).GetProperty("device").GetProperty("status").GetString());

        // the agent goes quiet: down after 5 minutes; muting closes the alert; unmuting brings it back; retiring the server closes it
        await Sql($"update assets.agents set last_seen = now() - interval '10 minutes'");
        Assert.Equal(1, (await J(await api.PostAsync("/api/monitoring/evaluate", Json(new { })))).GetProperty("opened").GetInt32());
        Assert.Equal(1, (await J(await api.GetAsync("/api/monitoring/devices?status=down"))).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/monitoring/devices/{asset}/mute", Json(new { hours = 0 }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/monitoring/devices/{asset}/mute", Json(new { hours = 2, reason = "Patching" }))).StatusCode);
        Assert.Equal(1, (await J(await api.PostAsync("/api/monitoring/evaluate", Json(new { })))).GetProperty("closed").GetInt32());
        var muted = (await J(await api.GetAsync($"/api/monitoring/devices/{asset}"))).GetProperty("device");
        Assert.Equal("muted", muted.GetProperty("status").GetString()); Assert.Equal("Patching", muted.GetProperty("muted").GetProperty("reason").GetString());
        Assert.Equal("Device muted", (await J(await api.GetAsync("/api/monitoring/alerts?state=closed&metric=offline"))).GetProperty("items")[0].GetProperty("closeReason").GetString());
        Assert.Equal(HttpStatusCode.OK, (await api.DeleteAsync($"/api/monitoring/devices/{asset}/mute")).StatusCode);
        Assert.Equal(1, (await J(await api.PostAsync("/api/monitoring/evaluate", Json(new { })))).GetProperty("opened").GetInt32());
        await Sql($"update assets.assets set status = 'retired' where id = {asset}");
        Assert.Equal(1, (await J(await api.PostAsync("/api/monitoring/evaluate", Json(new { })))).GetProperty("closed").GetInt32());
        Assert.Equal("Device retired or removed", (await J(await api.GetAsync("/api/monitoring/alerts?state=closed&metric=offline"))).GetProperty("items")[0].GetProperty("closeReason").GetString());
        await Sql($"update assets.assets set status = 'in_use' where id = {asset}");

        // access: a viewer reads but cannot acknowledge or change rules; a customer administrator of another customer sees nothing
        Assert.Equal(HttpStatusCode.Created, (await api.PostAsync("/api/platform/users", Json(new { email = "view@mon.test", name = "viewer", role = "viewer", password = Pw }))).StatusCode);
        var viewer = await TestSupport.ClientFor(factory, "view@mon.test", Pw);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/monitoring/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"/api/monitoring/devices/{asset}/mute", Json(new { hours = 1 }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/monitoring/rules", Json(new { name = "x", metric = "disk", threshold = 10, severity = "warning" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/monitoring/evaluate", Json(new { }))).StatusCode);
        var other = (await J(await api.PostAsync("/api/assets/companies", Json(new { name = "Globex" })))).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Created, (await api.PostAsync("/api/platform/users", Json(new { email = "cadm@mon.test", name = "cadmin", role = "cadmin", password = Pw }))).StatusCode);
        var users = (await J(await api.GetAsync("/api/platform/users"))).EnumerateArray().Single(x => x.GetProperty("email").GetString() == "cadm@mon.test");
        Assert.Equal(HttpStatusCode.OK, (await api.PutAsync($"/api/platform/access/user-companies/{users.GetProperty("id").GetString()}", Json(new { companyId = other }))).StatusCode);
        var cadm = await TestSupport.ClientFor(factory, "cadm@mon.test", Pw);
        var cd = await J(await cadm.GetAsync("/api/monitoring/dashboard"));
        Assert.True(cd.GetProperty("restricted").GetBoolean()); Assert.Equal(0, cd.GetProperty("customers").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await cadm.GetAsync($"/api/monitoring/devices/{asset}")).StatusCode);
        Assert.Equal(0, (await J(await cadm.GetAsync("/api/monitoring/alerts?state=all"))).GetProperty("total").GetInt64());
    }
}
