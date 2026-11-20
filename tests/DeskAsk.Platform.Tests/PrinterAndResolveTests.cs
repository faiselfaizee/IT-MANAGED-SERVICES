using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using DeskAsk.Platform.Assets;
using DeskAsk.Platform.Monitoring;
using Npgsql;
using Xunit;

/// <summary>What a printer reports, and what Monitoring makes of it (no database).</summary>
public class PrinterRuleTests
{
    static readonly DateTime Now = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
    static PrinterSupply Toner(string name, double pct, string state = "ok", string color = "black") => new(name, "toner", color, pct, state, false);
    static PrinterState P(PrinterSupply[]? supplies = null, string[]? flags = null, PrinterAlert[]? alerts = null, string message = "") =>
        new("idle", flags ?? Array.Empty<string>(), supplies ?? Array.Empty<PrinterSupply>(), Array.Empty<PrinterTray>(), alerts ?? Array.Empty<PrinterAlert>(), message, 1000);
    static DeviceState Dev(PrinterState? p, DateTime? at = null, string kind = "printer") =>
        new(1, "AST-1", "Accounts printer", 1, "Acme", kind, "10.0.0.9", "", false, "", null, true, null, 1, Now, null, null, null, 0, 0, Array.Empty<DiskUse>(), Array.Empty<CheckState>(), Array.Empty<PartFault>(),
            null, null, null, null, null, null, null, p, at ?? Now.AddMinutes(-5));
    static Rule R(int id, string metric, double th = 0, string sev = "warning", string[]? kinds = null) =>
        new(id, metric + id, true, metric, th, 0, sev, kinds ?? Array.Empty<string>(), null, true, false, "low", false);
    static List<Condition> Run(DeviceState d, params Rule[] rules) =>
        Evaluator.Conditions(new[] { d }, rules, NoSustained, new HashSet<long>(), Now);
    static readonly Func<int, IReadOnlyDictionary<long, Sustained>> NoSustained = _ => new Dictionary<long, Sustained>();

    [Fact]
    public void ASupplyAtOrBelowTheThresholdRaisesAnAlertAndAnEmptyOneIsCritical()
    {
        var p = P(new[] { Toner("Black Cartridge", 80), Toner("Cyan Cartridge", 8, "low", "cyan"), Toner("Yellow Cartridge", 0, "empty", "yellow"), Toner("Magenta (no level)", -1, "ok", "magenta") });
        var c = Run(Dev(p), R(1, "supply", 10));
        Assert.Equal(2, c.Count);
        Assert.Equal("Cyan Cartridge is at 8% (at or below 10%).", c.Single(x => x.Subject == "supply:Cyan Cartridge").Text);
        Assert.Equal("warning", c.Single(x => x.Subject == "supply:Cyan Cartridge").Severity);
        Assert.Equal("critical", c.Single(x => x.Subject == "supply:Yellow Cartridge").Severity);
        Assert.Equal("Yellow Cartridge is empty.", c.Single(x => x.Subject == "supply:Yellow Cartridge").Text);
    }

    [Fact]
    public void TheMostSevereSupplyRuleWinsAndAFullWasteBoxIsReported()
    {
        var p = P(new[] { Toner("Black Cartridge", 2), new PrinterSupply("Waste Toner Box", "waste", "", 95, "full", true), new PrinterSupply("Waste Toner Box 2", "waste", "", 40, "ok", true) });
        var c = Run(Dev(p), R(1, "supply", 10), R(2, "supply", 3, "critical"));
        Assert.Equal("critical", c.Single(x => x.Subject == "supply:Black Cartridge").Severity);
        Assert.Equal("Waste Toner Box is 95% full; empty or replace it.", c.Single(x => x.Subject == "supply:Waste Toner Box").Text);
        Assert.DoesNotContain(c, x => x.Subject == "supply:Waste Toner Box 2");
    }

    [Fact]
    public void AStaleOrMissingReadingProvesNothingAndKindsLimitTheRule()
    {
        var low = P(new[] { Toner("Black", 1) });
        Assert.Empty(Run(Dev(low, Now.AddHours(-2)), R(1, "supply", 10)));
        Assert.Empty(Run(Dev(null), R(1, "supply", 10)));
        Assert.Empty(Run(Dev(low, kind: "computer"), R(1, "supply", 10, kinds: new[] { "printer" })));
        Assert.True(Evaluator.Watched(Dev(low)));                      // a printer read by a controller counts as watched
    }

    [Fact]
    public void FaultsSayWhatIsWrongAndAddWhatThePrinterDisplays()
    {
        var c = Run(Dev(P(flags: new[] { "jammed", "low_toner", "door_open", "low_paper" }, message: "Paper jam in tray 2")), R(1, "printer"));
        Assert.Equal(2, c.Count);                                      // low toner and low paper belong to the supply rule, not here
        Assert.Equal("Paper jam. The printer says: Paper jam in tray 2", c.Single(x => x.Subject == "printer:jammed").Text);
        Assert.Equal("A door or cover is open. The printer says: Paper jam in tray 2", c.Single(x => x.Subject == "printer:door_open").Text);
    }

    [Fact]
    public void ACriticalAlertOfThePrinterIsPassedOnOnlyWhenNoFlagCoversIt()
    {
        var alerts = new[] { new PrinterAlert("critical", "Fuser error 59.F0"), new PrinterAlert("warning", "Cyan low") };
        var c = Run(Dev(P(alerts: alerts)), R(1, "printer"));
        Assert.Equal("The printer reports: Fuser error 59.F0", c.Single().Text);
        Assert.Empty(Run(Dev(P(flags: new[] { "low_toner" }, alerts: new[] { new PrinterAlert("warning", "Cyan low") })), R(1, "printer")));
        var covered = Run(Dev(P(flags: new[] { "jammed" }, alerts: alerts)), R(1, "printer"));
        Assert.Equal("printer:jammed", covered.Single().Subject);
    }

    [Fact]
    public void OutOfTonerIsOnlyAFaultWhenThePrinterGivesNoSupplyLevels()
    {
        Assert.Single(Run(Dev(P(flags: new[] { "no_toner" })), R(1, "printer")));
        Assert.Empty(Run(Dev(P(new[] { Toner("Black", 0, "empty") }, flags: new[] { "no_toner" })), R(1, "printer")));   // the supply rule says it better
    }

    [Fact]
    public void TheNewRulesAreDescribedAndValidated()
    {
        var supply = new Rule(0, "x", true, "supply", 10, 0, "warning", new[] { "printer" }, null, true, false, "low", false);
        Assert.Equal("A toner, ink or other supply of printers at or below 10% (or a waste container nearly full)", RulesApi.Describe(supply));
        Assert.StartsWith("A paper jam, open door", RulesApi.Describe(new Rule(0, "x", true, "printer", 0, 0, "warning", Array.Empty<string>(), null, true, false, "low", false)));
        Assert.Contains("'supply'", string.Join("\n", MonitoringSchema.Migrations.Select(m => m.Sql)));
        Assert.Contains("'Printer toner or ink nearly empty (3% or less)'", string.Join("\n", MonitoringSchema.Migrations.Select(m => m.Sql)));
    }
}

public class PrinterReadingTests
{
    const string Reading = @"{""status"":""printing"",""flags"":[""jammed"",""low_toner""],""message"":""Paper jam"",""name"":""Accounts"",""model"":""HP M454"",""serial"":""PH1"",""pages"":48213,
        ""supplies"":[{""name"":""Black"",""kind"":""toner"",""color"":""black"",""pct"":80,""state"":""ok"",""waste"":false},{""name"":""Waste"",""kind"":""waste"",""color"":"""",""pct"":95,""state"":""full"",""waste"":true}],
        ""trays"":[{""name"":""Tray 1"",""media"":""A4"",""pct"":-1,""state"":""ok""}],""alerts"":[{""severity"":""critical"",""text"":""Fuser error""}],""junk"":""ignored""}";

    [Fact]
    public void AStoredReadingKeepsOnlyKnownFieldsAndComesBackAsState()
    {
        var clean = PrinterApi.Clean(JsonDocument.Parse(Reading).RootElement);
        var json = JsonSerializer.Serialize(clean);
        Assert.DoesNotContain("junk", json);
        var s = PrinterApi.Parse(json)!;
        Assert.Equal("printing", s.Status); Assert.Equal(new[] { "jammed", "low_toner" }, s.Flags); Assert.Equal(48213, s.Pages); Assert.Equal("Paper jam", s.Message);
        Assert.Equal(2, s.Supplies.Count); Assert.True(s.Supplies[1].Waste); Assert.Equal("full", s.Supplies[1].State); Assert.Equal(-1, s.Trays[0].Pct);
        Assert.Equal("critical", s.Alerts.Single().Severity);
    }

    [Fact]
    public void UnknownWordsFromTheControllerAreMadeSafe()
    {
        var clean = PrinterApi.Clean(JsonDocument.Parse(@"{""status"":""exploding"",""pages"":-5,""supplies"":[{""name"":""X"",""pct"":500,""state"":""weird""}]}").RootElement);
        var s = PrinterApi.Parse(JsonSerializer.Serialize(clean))!;
        Assert.Equal("unknown", s.Status); Assert.Equal(-1, s.Pages); Assert.Equal(100, s.Supplies[0].Pct); Assert.Equal("unknown", s.Supplies[0].State);
        Assert.Null(PrinterApi.Clean(JsonDocument.Parse("[]").RootElement));
        Assert.Null(PrinterApi.Parse("not json")); Assert.Null(PrinterApi.Parse(null));
    }

    static DateOnly D(int m, int d) => new(2026, m, d);

    [Fact]
    public void PagesPerPeriodComeFromTheDailyCounterReadings()
    {
        var rows = new List<(DateOnly, long)> { (D(10, 28), 1000), (D(10, 31), 1300), (D(11, 3), 1500), (D(11, 10), 1900), (D(11, 20), 2400) };
        // November so far: from the last October reading (1300) to the latest (2400)
        Assert.Equal(new PageCount(1100, false), PrinterApi.Between(rows, D(11, 1), D(11, 20)));
        // October: before the first reading there is nothing, so it is counted from that reading and marked partial
        Assert.Equal(new PageCount(300, true), PrinterApi.Between(rows, D(10, 1), D(10, 31)));
        Assert.Equal(new PageCount(500, false), PrinterApi.Between(rows, D(11, 14), D(11, 20)));      // 7 days: from the 10th (1900) to the 20th
        Assert.Null(PrinterApi.Between(rows, D(9, 1), D(9, 30)));                                       // before any reading
        Assert.Null(PrinterApi.Between(new List<(DateOnly, long)>(), D(11, 1), D(11, 20)));
    }

    [Fact]
    public void AReplacedPrinterWhoseCounterWentDownGivesNoFigure()
    {
        var rows = new List<(DateOnly, long)> { (D(11, 1), 90000), (D(11, 10), 12) };
        Assert.Null(PrinterApi.Between(rows, D(11, 2), D(11, 20)));
        Assert.Empty(PrinterApi.Daily(rows));                                                            // a drop is not a negative day
        var ok = PrinterApi.Daily(new List<(DateOnly, long)> { (D(11, 1), 100), (D(11, 2), 160), (D(11, 4), 160) });
        Assert.Equal(new[] { (D(11, 2), 60L), (D(11, 4), 0L) }, ok.ToArray());
    }
}

/// <summary>Tickets that resolve themselves when the alert clears: only when nobody worked on them, and reopened when the same alert returns.</summary>
public class MonitoringResolveTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    readonly WebApplicationFactory<Program> factory;
    public MonitoringResolveTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }
    static async Task<string> Text(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); return Convert.ToString(await c.ExecuteScalarAsync()) ?? ""; }

    [PgFact]
    public async Task AnUntouchedTicketResolvesWhenTheAlertClearsAndReopensWhenItReturns()
    {
        var api = await TestSupport.AdminClient(factory);
        await api.GetAsync("/api/assets/types"); await api.GetAsync("/api/monitoring/rules");
        await Sql("truncate assets.metrics, assets.metrics_hour, assets.agent_upd_log, assets.shells, assets.rc_log, assets.jobs, assets.agent_q, assets.agents, assets.enroll_tokens, assets.history, assets.assets, assets.companies restart identity cascade");
        await Sql(@"insert into jobs.recurring (name, type, interval_seconds, next_run) values ('monitoring.evaluate', 'monitoring.evaluate', 60, now() + interval '1 day')
            on conflict (name) do update set next_run = now() + interval '1 day'; delete from jobs.queue where type = 'monitoring.evaluate' and status = 'queued'");
        await Sql("truncate monitoring.alerts, monitoring.mutes restart identity; truncate mail.outbox, mail.outbox_files restart identity; truncate servicedesk.tickets restart identity cascade; delete from monitoring.rules");

        // a built-in rule that opens tickets resolves them out of the box
        var seeds = string.Join("\n", MonitoringSchema.Migrations.Select(m => m.Sql));
        Assert.Contains("update monitoring.rules set resolve = true where builtin and ticket", seeds);

        var made = await api.PostAsync("/api/monitoring/rules", Json(new { name = "Disk very low", metric = "disk", threshold = 5, severity = "critical", ticket = true, resolve = true, ticketPriority = "high" }));
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var rules = (await J(await api.GetAsync("/api/monitoring/rules"))).GetProperty("items").EnumerateArray().ToList();
        Assert.True(rules.Single().GetProperty("resolve").GetBoolean());
        // resolve means nothing without a ticket
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync("/api/monitoring/rules", Json(new { name = "Disk low", metric = "disk", threshold = 10, severity = "warning", ticket = false, resolve = true }))).StatusCode);
        var both = (await J(await api.GetAsync("/api/monitoring/rules"))).GetProperty("items").EnumerateArray().ToList();
        Assert.False(both.Single(x => x.GetProperty("name").GetString() == "Disk low").GetProperty("resolve").GetBoolean());
        await Sql("delete from monitoring.rules where name = 'Disk low'");

        var acme = (await J(await api.PostAsync("/api/assets/companies", Json(new { name = "Acme Ltd" })))).GetProperty("id").GetInt32();
        var tok = (await J(await api.PostAsync("/api/assets/enrolments", Json(new { companyId = acme })))).GetProperty("token").GetString()!;
        var agent = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var en = await J(await agent.PostAsync("/agent/v1/enroll", Json(new { token = tok, platform = "windows", version = "3.0.0", inventory = new { schema = 1, hostname = "SRV-RES1", os_family = "windows", os_name = "Windows Server 2022 Standard", serial_number = "SN-RES-1" } })));
        agent.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", en.GetProperty("key").GetString()); agent.DefaultRequestHeaders.Add("X-Agent-Id", en.GetProperty("agentId").GetString());
        async Task Sample(double cFree) => Assert.Equal(HttpStatusCode.OK, (await agent.PostAsync("/agent/v1/metrics", Json(new { cpu = 20, mem_used = 4_000_000_000L, mem_total = 16_000_000_000L, uptime = 7200, disks = new[] { new { mount = "C:", total = 100_000_000_000L, free = (long)(cFree * 1_000_000_000) } } }))).StatusCode);
        async Task<int> Eval(string field) => (await J(await api.PostAsync("/api/monitoring/evaluate", Json(new { })))).GetProperty(field).GetInt32();
        async Task Clear() { await Sample(50); Assert.Equal(0, await Eval("closed")); Assert.Equal(1, await Eval("closed")); }

        // 1. the disk fills up: an alert and a ticket that says it resolves itself
        await Sample(3);
        Assert.Equal(1, await Eval("opened"));
        var number = (await J(await api.GetAsync("/api/monitoring/alerts"))).GetProperty("items")[0].GetProperty("ticket").GetString()!;
        Assert.StartsWith("INC-", number);
        Assert.Contains("resolved by itself", await Text($"select description from servicedesk.tickets where number = '{number}'"));
        Assert.Equal("open", await Text($"select status from servicedesk.tickets where number = '{number}'"));

        // 2. nobody touched it and the disk is fine again: the ticket is resolved, with a note that says why
        await Clear();
        Assert.Equal("resolved", await Text($"select status from servicedesk.tickets where number = '{number}'"));
        Assert.Contains("Monitoring resolved it", await Text($"select string_agg(body, ' | ') from servicedesk.comments c join servicedesk.tickets t on t.id = c.ticket_id where t.number = '{number}'"));
        Assert.NotEqual("", await Text($"select resolved_at::text from servicedesk.tickets where number = '{number}'"));

        // 3. the same alert returns within 24 hours: the same ticket is reopened, not a second one
        await Sample(3);
        Assert.Equal(1, await Eval("opened"));
        Assert.Equal(number, (await J(await api.GetAsync("/api/monitoring/alerts"))).GetProperty("items")[0].GetProperty("ticket").GetString());
        Assert.Equal("open", await Text($"select status from servicedesk.tickets where number = '{number}'"));
        Assert.Equal("", await Text($"select resolved_at::text from servicedesk.tickets where number = '{number}'"));
        Assert.Equal("1", await Text("select count(*) from servicedesk.tickets where source = 'monitoring'"));
        Assert.Contains("resolved → open", await Text($"select string_agg(body, ' | ') from servicedesk.comments c join servicedesk.tickets t on t.id = c.ticket_id where t.number = '{number}'"));

        // 4. a technician replies, so the ticket is theirs: clearing only adds a note
        var id = await Text($"select id from servicedesk.tickets where number = '{number}'");
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/servicedesk/tickets/{id}/comments", Json(new { body = "Looking at it now." }))).StatusCode);
        await Clear();
        Assert.Equal("open", await Text($"select status from servicedesk.tickets where number = '{number}'"));
        Assert.Contains("The alert has cleared after", await Text($"select string_agg(body, ' | ') from servicedesk.comments c join servicedesk.tickets t on t.id = c.ticket_id where t.number = '{number}' and c.kind = 'system'"));

        // 5. a person resolved it, the alert returns: a person's decision is not undone, so a new ticket is opened
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/servicedesk/tickets/{id}/status", Json(new { status = "closed", note = "Freed space." }))).StatusCode);
        await Sample(3);
        Assert.Equal(1, await Eval("opened"));
        var again = (await J(await api.GetAsync("/api/monitoring/alerts"))).GetProperty("items")[0].GetProperty("ticket").GetString()!;
        Assert.NotEqual(number, again);
        await Clear();
        Assert.Equal("resolved", await Text($"select status from servicedesk.tickets where number = '{again}'"));
        Assert.Equal("closed", await Text($"select status from servicedesk.tickets where number = '{number}'"));

        // 6. the alert returns once more (the ticket resolved in step 5 is reopened); a muted device never resolves a ticket by itself, the alert closes with a note only
        await Sample(3);
        Assert.Equal(1, await Eval("opened"));
        var third = (await J(await api.GetAsync("/api/monitoring/alerts"))).GetProperty("items")[0].GetProperty("ticket").GetString()!;
        var asset = await Text("select id from assets.assets limit 1");
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/monitoring/devices/{asset}/mute", Json(new { hours = 1, reason = "Patching" }))).StatusCode);
        Assert.Equal(1, await Eval("closed"));
        Assert.Equal("open", await Text($"select status from servicedesk.tickets where number = '{third}'"));
        Assert.Contains("Device muted", await Text($"select string_agg(body, ' | ') from servicedesk.comments c join servicedesk.tickets t on t.id = c.ticket_id where t.number = '{third}'"));
    }
}
