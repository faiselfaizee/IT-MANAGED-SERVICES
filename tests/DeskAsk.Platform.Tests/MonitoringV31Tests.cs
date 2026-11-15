using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using DeskAsk.Platform;
using DeskAsk.Platform.Monitoring;
using Npgsql;
using Xunit;

/// <summary>A Teams webhook stand-in: keeps every JSON body posted to it.</summary>
sealed class FakeWebhook : IDisposable
{
    readonly HttpListener l = new();
    public readonly List<string> Bodies = new();
    public string Url { get; }
    public FakeWebhook()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        Url = $"http://127.0.0.1:{port}/hook/";
        l.Prefixes.Add(Url); l.Start();
        _ = Task.Run(async () =>
        {
            while (l.IsListening)
            {
                HttpListenerContext c; try { c = await l.GetContextAsync(); } catch { return; }
                using (var r = new StreamReader(c.Request.InputStream)) lock (Bodies) Bodies.Add(r.ReadToEnd());
                c.Response.StatusCode = 202; c.Response.Close();
            }
        });
    }
    public List<string> Snapshot() { lock (Bodies) return Bodies.ToList(); }
    public void Dispose() { try { l.Stop(); l.Close(); } catch { } }
}

/// <summary>The new rule kinds on their own (no database).</summary>
public class MonitoringV31RuleTests
{
    static readonly DateTime Now = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
    static DeviceState Dev(PortState[]? ports = null, WatchedItem[]? watched = null, string kind = "network") =>
        new(1, "AST-1", "CORE-SW1", 1, "Acme", kind, "10.0.0.2", "", false, "", null, true, null, 1, Now, null, null, null, 0, 0, Array.Empty<DiskUse>(), Array.Empty<CheckState>(), Array.Empty<PartFault>(),
            ports, ports == null ? null : Now.AddMinutes(-2), watched, watched == null ? null : Now.AddMinutes(-1));
    static PortState P(string i, bool up = true, double inPct = 10, double outPct = 5, bool watched = false, DateTime? down = null, string alias = "") =>
        new(i, "Gi1/0/" + i, alias, "Ethernet", up, true, 1000, true, inPct, outPct, inPct * 1e7, outPct * 1e7, 0, 0, watched, down);
    static Rule R(string metric, double th = 0, int mins = 0, string target = "", string kind = "", int sev = 3) =>
        new(1, metric, true, metric, th, mins, "critical", Array.Empty<string>(), null, true, false, "high", false, target, kind, sev);
    static List<Condition> Run(DeviceState d, Rule r, IReadOnlyList<DeviceEvent>? ev = null, Func<int, IReadOnlyDictionary<(long, string), PortSustained>>? ports = null) =>
        Evaluator.Conditions(new[] { d }, new[] { r }, _ => new Dictionary<long, Sustained>(), new HashSet<long>(), Now, ports, ev);

    [Fact]
    public void BusyPortsAndWatchedPortsThatWentDown()
    {
        var c = Run(Dev(new[] { P("1", inPct: 92, outPct: 40, alias: "Uplink"), P("2", inPct: 30) }), R("traffic", 80));
        Assert.Equal("Port Gi1/0/1 (Uplink) is at 92% of 1 Gbps (in 92%, out 40%).", c.Single().Text);
        var sustained = new Dictionary<(long, string), PortSustained> { [(1, "1")] = new(85, 4, Now.AddMinutes(-14)) };
        Assert.Single(Run(Dev(new[] { P("1", inPct: 92) }), R("traffic", 80, 15), ports: _ => sustained));
        Assert.Empty(Run(Dev(new[] { P("1", inPct: 92) }), R("traffic", 80, 15), ports: _ => new Dictionary<(long, string), PortSustained> { [(1, "1")] = new(40, 4, Now.AddMinutes(-14)) }));
        var down = Dev(new[] { P("1", up: false, watched: true, down: Now.AddMinutes(-7), alias: "Uplink"), P("2", up: false, watched: false, down: Now.AddHours(-3)) });
        Assert.Equal("Port Gi1/0/1 (Uplink) has been down for 7 minutes.", Run(down, R("port", mins: 5)).Single().Text);
        Assert.Empty(Run(down, R("port", mins: 10)));
    }

    [Fact]
    public void StoppedServicesMissingProgramsAndCountedMessages()
    {
        var d = Dev(watched: new[] { new WatchedItem("Spooler", "service", false, "stopped", 0), new WatchedItem("W32Time", "service", true, "running", 1),
            new WatchedItem("Backup", "service", false, "missing", 0), new WatchedItem("sqlservr.exe", "process", false, "not running", 0) }, kind: "server");
        var s = Run(d, R("service", target: "Spooler, W32Time, Backup, NotReportedYet"));
        Assert.Equal(new[] { "Service Spooler is stopped.", "Service Backup is not installed on this device." }, s.Select(x => x.Text));
        Assert.Equal("Program sqlservr.exe is not running.", Run(d, R("process", target: "sqlservr.exe")).Single().Text);
        var stale = d with { WatchedAt = Now.AddMinutes(-30) };
        Assert.Empty(Run(stale, R("service", target: "Spooler"))); // old news proves nothing
        var ev = new[]
        {
            new DeviceEvent(1, 1, Now.AddMinutes(-3), "syslog", 2, "kernel", 0, "fan 2 failed"), new DeviceEvent(2, 1, Now.AddMinutes(-2), "syslog", 2, "kernel", 0, "fan 2 failed"),
            new DeviceEvent(3, 1, Now.AddMinutes(-1), "trap", 3, "linkDown", 0, "linkDown: Gi1/0/7"), new DeviceEvent(4, 1, Now.AddMinutes(-40), "syslog", 2, "kernel", 0, "old"),
        };
        var one = Run(Dev(), R("log", 1, 15, sev: 2), ev).Single();
        Assert.Equal("2 messages in 15 minutes; the latest, a syslog message, from kernel: fan 2 failed", one.Text);
        Assert.Empty(Run(Dev(), R("log", 3, 15, sev: 2), ev));                                // only two at critical
        Assert.Single(Run(Dev(), R("log", 1, 15, target: "gi1/0/7", kind: "trap", sev: 3), ev)); // text match, any case
        Assert.Empty(Run(Dev(), R("log", 1, 15, kind: "eventlog", sev: 7), ev));
    }
}

/// <summary>Traffic and messages from a site controller, services and event logs from an agent, Teams, escalation and the monthly report.</summary>
public class MonitoringV31Tests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    readonly WebApplicationFactory<Program> factory;
    public MonitoringV31Tests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b =>
    {
        TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); b.UseSetting("Platform:Roles", "all"); b.UseSetting("Jobs:PollSeconds", "1");
    });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }
    static async Task<long> Scalar(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); return Convert.ToInt64(await c.ExecuteScalarAsync()); }
    static async Task Until(Func<Task<bool>> ok, int seconds = 30) { for (var i = 0; i < seconds * 4; i++) { if (await ok()) return; await Task.Delay(250); } Assert.Fail("timed out"); }

    [PgFact]
    public async Task PortsMessagesServicesTeamsEscalationAndTheMonthlyReport()
    {
        var api = await TestSupport.AdminClient(factory);
        await api.GetAsync("/api/assets/types"); await api.GetAsync("/api/monitoring/rules");
        await Sql(@"insert into jobs.recurring (name, type, interval_seconds, next_run) values ('monitoring.evaluate', 'monitoring.evaluate', 60, now() + interval '1 day')
            on conflict (name) do update set next_run = now() + interval '1 day'; delete from jobs.queue where type in ('monitoring.evaluate','monitoring.report') and status = 'queued';
            insert into jobs.recurring (name, type, interval_seconds, next_run) values ('monitoring.report', 'monitoring.report', 3600, now() + interval '1 day')
            on conflict (name) do update set next_run = now() + interval '1 day'");
        await Sql("truncate assets.events, assets.ifstats, assets.port_watch, assets.metrics, assets.collectors, assets.creds, assets.agent_upd_log, assets.shells, assets.rc_log, assets.jobs, assets.agent_q, assets.agents, assets.enroll_tokens, assets.history, assets.assets, assets.companies restart identity cascade");
        await Sql("truncate monitoring.alerts, monitoring.mutes, monitoring.outbox, monitoring.report_sent restart identity; truncate mail.outbox restart identity cascade; delete from monitoring.rules");
        Assert.Equal(HttpStatusCode.OK, (await api.PutAsync("/api/monitoring/settings", Json(new { recipients = "" }))).StatusCode);
        using var smtp = new FakeSmtp();
        Assert.Equal(HttpStatusCode.OK, (await api.PutAsync("/api/platform/mail/config", Json(new { provider = "smtp", from = "desk@example.test", fromName = "DeskAsk", host = "127.0.0.1", port = smtp.Port, security = "none", user = "", password = "" }))).StatusCode);

        async Task<int> Rule(object o) { var r = await api.PostAsync("/api/monitoring/rules", Json(o)); Assert.Equal(HttpStatusCode.OK, r.StatusCode); return (await J(r)).GetProperty("id").GetInt32(); }
        await Rule(new { name = "Uplink down", metric = "port", minutes = 5, severity = "critical", kinds = new[] { "network" } });
        await Rule(new { name = "Device says critical", metric = "log", threshold = 1, minutes = 15, minSeverity = 2, severity = "critical" });
        await Rule(new { name = "Print spooler", metric = "service", target = "Spooler", restart = true, severity = "warning" });
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/monitoring/rules", Json(new { name = "x", metric = "service", target = "", severity = "warning" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/monitoring/rules", Json(new { name = "x", metric = "service", target = "a'b", severity = "warning" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/monitoring/rules", Json(new { name = "x", metric = "log", threshold = 1, minutes = 5, eventKind = "email", severity = "warning" }))).StatusCode);

        using var hook = new FakeWebhook();
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PutAsync("/api/monitoring/settings", Json(new { teams = "http://example.com/hook" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PutAsync("/api/monitoring/settings", Json(new { recipients = "", teams = hook.Url, escalateMinutes = 1, escalateRecipients = "boss@mon31.test", escalateTeams = hook.Url }))).StatusCode);
        var st = await J(await api.GetAsync("/api/monitoring/settings"));
        Assert.Equal("127.0.0.1/…", st.GetProperty("teams").GetString()); Assert.DoesNotContain("/hook", st.ToString());
        Assert.Equal(HttpStatusCode.OK, (await api.PutAsync("/api/monitoring/settings", Json(new { recipients = "", teams = "127.0.0.1/…", escalateMinutes = 1, escalateRecipients = "boss@mon31.test", escalateTeams = "127.0.0.1/…" }))).StatusCode); // the hint keeps the saved address
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync("/api/monitoring/settings/teams-test", Json(new { }))).StatusCode);
        Assert.Contains("Test from DeskAsk Monitoring", hook.Snapshot().Single());

        // a switch watched by a site controller sends its ports, a syslog message and a trap
        var acme = (await J(await api.PostAsync("/api/assets/companies", Json(new { name = "Acme Ltd" })))).GetProperty("id").GetInt32();
        var tok = (await J(await api.PostAsync("/api/assets/collectors/tokens", Json(new { companyId = acme, label = "HQ" })))).GetProperty("token").GetString()!;
        var anon = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var en = await J(await anon.PostAsync("/collector/v1/enroll", Json(new { token = tok, name = "HQ", hostname = "COL-1", platform = "windows", version = "3.1.0" })));
        var cid = en.GetProperty("collectorId").GetString()!;
        var col = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        col.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", en.GetProperty("key").GetString()); col.DefaultRequestHeaders.Add("X-Collector-Id", cid);
        await col.PostAsync("/collector/v1/checkin", Json(new { found = new object[] { new { ip = "10.9.0.2", hostname = "core-sw1", open = new[] { 22, 161 }, mac = "" } } }));
        await api.PostAsync($"/api/assets/collectors/{cid}/adopt", Json(new { ips = new[] { "10.9.0.2" } }));
        var sw = (await J(await api.GetAsync("/api/assets"))).GetProperty("items").EnumerateArray().Single().GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/assets/{sw}/monitor", Json(new { mode = "network", collectorId = cid }))).StatusCode);
        await Sql($"update assets.assets set type_id = null, hw_extra = '{{\"platform\":\"network\"}}' where id = {sw}");
        object Port(string i, string oper, double inPct, string alias) => new { index = i, name = "Gi1/0/" + i, alias, kind = "Ethernet", oper, admin = "up", speed_mbps = 1000, rates = true, in_pct = inPct, out_pct = 2.5, in_bps = inPct * 1e7, out_bps = 2.5e7, errors = 0.4, discards = 0 };
        var r1 = await col.PostAsync("/collector/v1/checkin", Json(new
        {
            results = new
            {
                health = new[] { new { asset = sw.ToString(), up = true, rtt_ms = 1 } },
                traffic = new[] { new { asset = sw.ToString(), ports = new[] { Port("1", "down", 0, "Uplink to FW"), Port("2", "up", 64.2, ""), Port("3", "down", 0, "") } } },
                events = new object[]
                {
                    new { ip = "10.9.0.2", kind = "syslog", severity = 2, source = "kernel", text = "fan 2 failed", at = DateTime.UtcNow.ToString("o") },
                    new { ip = "10.9.0.2", kind = "trap", severity = 3, source = "linkDown", text = "linkDown: Gi1/0/1", at = DateTime.UtcNow.ToString("o") },
                    new { ip = "10.9.9.9", kind = "syslog", severity = 5, source = "x", text = "from an unknown device", at = DateTime.UtcNow.ToString("o") },
                },
            },
        }));
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Equal(2, await Scalar("select count(*) from assets.events"));
        Assert.Equal(1, await Scalar("select count(*) from assets.ifstats where if_index = '2'"));
        var dev = (await J(await api.GetAsync($"/api/monitoring/devices/{sw}"))).GetProperty("device");
        var ports = dev.GetProperty("ports").EnumerateArray().ToList();
        Assert.Equal(3, ports.Count);
        Assert.True(ports[0].GetProperty("watched").GetBoolean());   // has a description
        Assert.False(ports[2].GetProperty("watched").GetBoolean());
        Assert.Equal(64.2, ports[1].GetProperty("inPct").GetDouble());
        Assert.Equal(JsonValueKind.String, ports[0].GetProperty("downSince").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await api.PutAsync($"/api/monitoring/devices/{sw}/ports/3/watch", Json(new { watched = true }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.PutAsync($"/api/monitoring/devices/{sw}/ports/99/watch", Json(new { watched = true }))).StatusCode);
        var evs = await J(await api.GetAsync($"/api/monitoring/events?asset={sw}&severity=3"));
        Assert.Equal(2, evs.GetProperty("total").GetInt64());
        Assert.Equal(1, (await J(await api.GetAsync("/api/monitoring/events?kind=trap"))).GetProperty("total").GetInt64());
        Assert.Single((await J(await api.GetAsync($"/api/monitoring/devices/{sw}/ports/2/series?range=6h"))).GetProperty("points").EnumerateArray());

        // the port went down just now: the log rule fires at once, the port rule after 5 minutes
        var ev1 = await J(await api.PostAsync("/api/monitoring/evaluate", Json(new { })));
        Assert.Equal(1, ev1.GetProperty("opened").GetInt32());
        await Sql($"update assets.assets set m_ports = (select jsonb_agg(case when p->>'index' in ('1','3') then jsonb_set(p, '{{downAt}}', to_jsonb((now() - interval '8 minutes')::text)) else p end) from jsonb_array_elements(m_ports) p) where id = {sw}");
        Assert.Equal(2, (await J(await api.PostAsync("/api/monitoring/evaluate", Json(new { })))).GetProperty("opened").GetInt32()); // ports 1 and 3 (now watched)
        var open = (await J(await api.GetAsync("/api/monitoring/alerts"))).GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(open, a => a.GetProperty("text").GetString() == "Port Gi1/0/1 (Uplink to FW) has been down for 8 minutes.");
        Assert.Contains(open, a => a.GetProperty("metric").GetString() == "log" && a.GetProperty("text").GetString()!.Contains("fan 2 failed"));
        Assert.Equal(1, (await J(await api.GetAsync("/api/monitoring/devices?port=down"))).GetProperty("total").GetInt32());
        var tile = (await J(await api.GetAsync("/api/monitoring/dashboard"))).GetProperty("customers")[0];
        Assert.Equal(2, tile.GetProperty("portsDown").GetInt32()); Assert.Equal(1, tile.GetProperty("byMetric").GetProperty("log").GetInt32());
        await Until(async () => hook.Snapshot().Count(b => b.Contains("Critical: core-sw1") || b.Contains("Critical: CORE-SW1") || b.Contains("Critical:")) >= 3);
        var card = hook.Snapshot().First(b => b.Contains("has been down for"));
        Assert.Contains("application/vnd.microsoft.card.adaptive", card); Assert.Contains("#/monitoring/device/" + sw, card); Assert.Contains("Acme Ltd", card);

        // an agent on a Windows server: the watch list comes back with the answer; a stopped spooler opens an alert and a restart job
        var etok = (await J(await api.PostAsync("/api/assets/enrolments", Json(new { companyId = acme })))).GetProperty("token").GetString()!;
        var agent = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var aen = await J(await agent.PostAsync("/agent/v1/enroll", Json(new { token = etok, platform = "windows", version = "3.1.0", inventory = new { schema = 1, hostname = "SRV-PRINT", os_family = "windows", os_name = "Windows Server 2022", serial_number = "SN-31" } })));
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", aen.GetProperty("key").GetString()); agent.DefaultRequestHeaders.Add("X-Agent-Id", aen.GetProperty("agentId").GetString());
        var srv = long.Parse(aen.GetProperty("assetId").GetString()!);
        var ans = await J(await agent.PostAsync("/agent/v1/metrics", Json(new { cpu = 5, mem_used = 1, mem_total = 10, uptime = 60, disks = Array.Empty<object>() })));
        Assert.Equal("Spooler", ans.GetProperty("watch").GetProperty("services")[0].GetString()); Assert.True(ans.GetProperty("events").GetBoolean());
        await agent.PostAsync("/agent/v1/metrics", Json(new
        {
            cpu = 5, mem_used = 1, mem_total = 10, uptime = 60, disks = Array.Empty<object>(),
            services = new[] { new { name = "Spooler", state = "stopped", running = false } },
            events = new[] { new { log = "System", source = "Service Control Manager", id = 7031, severity = 3, text = "The Print Spooler service terminated unexpectedly.", at = DateTime.UtcNow.ToString("o"), record = 99 } },
        }));
        Assert.Equal(1, await Scalar($"select count(*) from assets.events where asset_id = {srv} and kind = 'eventlog' and event_id = 7031"));
        Assert.Equal(1, (await J(await api.PostAsync("/api/monitoring/evaluate", Json(new { })))).GetProperty("opened").GetInt32());
        var spool = (await J(await api.GetAsync($"/api/monitoring/devices/{srv}"))).GetProperty("alerts").EnumerateArray().Single();
        Assert.Equal("Service Spooler is stopped.", spool.GetProperty("text").GetString());
        Assert.StartsWith("Restart asked for at", spool.GetProperty("actionNote").GetString());
        Assert.Equal(1, await Scalar($"select count(*) from assets.jobs where asset_id = {srv} and title = 'Restart service Spooler (Monitoring)' and body like 'Restart-Service -Name ''Spooler''%'"));
        Assert.Equal("stopped", (await J(await api.GetAsync($"/api/monitoring/devices/{srv}"))).GetProperty("device").GetProperty("watched")[0].GetProperty("state").GetString());

        // nobody acknowledged the critical port alert: escalation mails and posts once
        await Sql("update monitoring.alerts set opened_at = now() - interval '5 minutes' where metric = 'port'");
        await api.PostAsync("/api/monitoring/evaluate", Json(new { }));
        await api.PostAsync("/api/monitoring/evaluate", Json(new { }));
        Assert.Equal(2, await Scalar("select count(*) from mail.outbox where to_addr = 'boss@mon31.test' and subject like '[Not acknowledged]%'"));
        await Until(async () => hook.Snapshot().Count(b => b.Contains("Not acknowledged for")) == 2);

        // the monthly report: a closed down alert of an hour this month makes the switch 1 hour short of the month
        await Sql($@"insert into monitoring.alerts (akey, rule_name, metric, severity, asset_id, asset_name, asset_tag, company_id, company_name, text, opened_at, closed_at)
            values ('x', 'Server down', 'offline', 'critical', {sw}, 'core-sw1', 'AST', {acme}, 'Acme Ltd', 'down', date_trunc('month', now()) + interval '1 hour', date_trunc('month', now()) + interval '2 hours')");
        var month = DateTime.UtcNow.ToString("yyyy-MM");
        var rep = await J(await api.GetAsync($"/api/monitoring/reports/uptime?company={acme}&month={month}"));
        Assert.Equal("Acme Ltd", rep.GetProperty("title").GetString()); Assert.Equal(2, rep.GetProperty("devices").GetInt32()); Assert.Equal(1, rep.GetProperty("outages").GetInt32());
        var line = rep.GetProperty("lines").EnumerateArray().First();
        Assert.Equal(sw, line.GetProperty("id").GetInt64()); Assert.Equal(60, line.GetProperty("downMinutes").GetDouble()); Assert.True(line.GetProperty("availability").GetDouble() < 100);
        var pdf = await api.GetAsync($"/api/monitoring/reports/uptime.pdf?company={acme}&month={month}");
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync())[..4]));
        Assert.Equal(HttpStatusCode.BadRequest, (await api.GetAsync("/api/monitoring/reports/uptime?month=2026-13")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await api.PutAsync("/api/monitoring/settings", Json(new { recipients = "", reportEnabled = true, reportTo = "noc@mon31.test", reportCustomers = true, companies = new[] { new { companyId = acme, recipients = "it@acme31.test" } } }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync("/api/monitoring/reports/send", Json(new { month }))).StatusCode);
        await Until(async () => await Scalar("select count(*) from mail.outbox where subject like 'Uptime report%' and status = 'sent'") == 2, 60);
        Assert.Equal(2, await Scalar("select count(*) from mail.outbox_files where content_type = 'application/pdf' and name like 'uptime-%.pdf'"));
        var raw = smtp.Messages.First(m => m.Contains("Uptime report"));
        Assert.Contains("Content-Disposition: attachment", raw); Assert.Contains("application/pdf", raw);
    }
}
