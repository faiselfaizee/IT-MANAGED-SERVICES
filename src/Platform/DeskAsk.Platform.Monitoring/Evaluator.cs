using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DeskAsk.Platform.Monitoring;

/// <summary>One thing that is wrong now: a rule's condition holding for a device (and a disk, check or part of it).</summary>
public sealed record Condition(string Key, Rule Rule, DeviceState Device, string Metric, string Subject, string Severity, string Text, double? Value);

/// <summary>What one evaluation did.</summary>
public sealed record EvalResult(int Opened, int Closed, int Open, int Raised);

public sealed class EvaluateJob : IJobHandler
{
    public const string JobType = "monitoring.evaluate";
    public string Type => JobType;
    public async Task RunAsync(JobContext job, CancellationToken ct)
    {
        var lic = await job.Services.GetRequiredService<IEntitlements>().CurrentAsync(ct);
        if (!lic.Has(Modules.Monitoring) || !lic.Has(Modules.Assets)) return;
        await Evaluator.RunAsync(job.Services, ct);
    }
}

public static class Evaluator
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static int Rank(string severity) => severity == "critical" ? 2 : 1;

    /// <summary>A device is watched when it has an agent, a site controller pings it, or it has service checks.</summary>
    public static bool Watched(DeviceState d) => d.HasAgent || d.PingUp != null || d.Checks.Count > 0 || d.PortsAt != null;
    static string PortLabel(PortState p) => p.Name + (p.Alias != "" ? $" ({p.Alias})" : "");

    static readonly Dictionary<string, string> PartNames = new()
    {
        ["power_supplies"] = "Power supply", ["drives"] = "Drive", ["fans"] = "Fan", ["memory_modules"] = "Memory module", ["processors"] = "Processor",
        ["storage_controllers"] = "Storage controller", ["sensors"] = "Sensor", ["hosts"] = "Host", ["datastores"] = "Datastore", ["modules"] = "Module", ["gpus"] = "GPU", ["ports"] = "Port",
    };

    /// <summary>
    /// Every condition that holds now. Where several rules hit the same thing (for example disk below 10% and below 5%), only the most severe is kept.
    /// sustained(minutes) gives the lowest processor and memory use per device over that many minutes.
    /// </summary>
    public static List<Condition> Conditions(IReadOnlyList<DeviceState> devices, IReadOnlyList<Rule> rules, Func<int, IReadOnlyDictionary<long, Sustained>> sustained, ISet<long> muted, DateTime now,
        Func<int, IReadOnlyDictionary<(long, string), PortSustained>>? ports = null, IReadOnlyList<DeviceEvent>? events = null)
    {
        var all = new List<Condition>();
        var evByAsset = (events ?? Array.Empty<DeviceEvent>()).ToLookup(e => e.AssetId);
        foreach (var d in devices)
        {
            if (muted.Contains(d.Id) || !Watched(d)) continue;
            foreach (var r in rules)
            {
                if (!r.Enabled || (r.CompanyId != null && r.CompanyId != d.CompanyId) || (r.Kinds.Length > 0 && !r.Kinds.Contains(d.Kind))) continue;
                void Add(string subject, string severity, string text, double? value) => all.Add(new Condition($"{d.Id}:{r.Metric}:{subject}", r, d, r.Metric, subject, severity, text, value));
                var th = r.Threshold.ToString("0.#", Inv);
                switch (r.Metric)
                {
                    case "offline":
                    {
                        var limit = TimeSpan.FromMinutes(r.Minutes);
                        if (d.PingUp == false && d.DownSince is DateTime ds && now - ds >= limit)
                            Add("", r.Severity, $"Not answering the site controller for {MonitoringModule.Dur(now - ds)}.", Math.Round((now - ds).TotalMinutes));
                        else if (d.HasAgent && d.PingUp != true && (d.AgentSeen == null || now - d.AgentSeen.Value >= limit))
                            Add("", r.Severity, d.AgentSeen is DateTime seen ? $"The agent has not reported for {MonitoringModule.Dur(now - seen)}." : "The agent has never reported.",
                                d.AgentSeen is DateTime s2 ? Math.Round((now - s2).TotalMinutes) : null);
                        break;
                    }
                    case "cpu" or "memory":
                    {
                        var now1 = r.Metric == "cpu" ? d.Cpu : d.Mem;
                        var word = r.Metric == "cpu" ? "Processor" : "Memory";
                        if (d.MetricsAt == null || now - d.MetricsAt.Value > TimeSpan.FromMinutes(5) || now1 == null) break;
                        bool over;
                        if (r.Minutes <= 1) over = now1 > r.Threshold;
                        else
                        {
                            var s = sustained(r.Minutes).GetValueOrDefault(d.Id);
                            var low = r.Metric == "cpu" ? s?.MinCpu : s?.MinMem;
                            over = s != null && low != null && s.Samples >= 2 && s.First <= now.AddMinutes(-r.Minutes * 0.7) && low > r.Threshold;
                        }
                        if (over) Add("", r.Severity, r.Minutes <= 1 ? $"{word} use is {now1:0.#}% (above {th}%)." : $"{word} use has been above {th}% for {r.Minutes} minutes; now {now1:0.#}%.", now1);
                        break;
                    }
                    case "disk":
                        foreach (var k in d.Disks)
                            if (k.FreePct < r.Threshold)
                                Add(k.Name, r.Severity, $"{k.Name} has {k.FreePct.ToString("0.#", Inv)}% free ({MonitoringModule.Bytes(k.FreeBytes)} of {MonitoringModule.Bytes(k.TotalBytes)}).", k.FreePct);
                        break;
                    case "check":
                        foreach (var c in d.Checks)
                            if (c.Ok == false && c.Fails >= Math.Max(1, (int)r.Threshold))
                                Add("check:" + c.Id, r.Severity, $"Service check \"{c.Name}\" failed {c.Fails} times in a row{(c.Detail != "" ? ": " + c.Detail : ".")}", c.Fails);
                        break;
                    case "cert":
                        foreach (var c in d.Checks)
                            if (c.Type == "ssl" && c.CertDays is double days && days <= r.Threshold)
                                Add("cert:" + c.Id, days <= 0 ? "critical" : r.Severity,
                                    days <= 0 ? $"The certificate checked by \"{c.Name}\" has expired." : $"The certificate checked by \"{c.Name}\" expires in {Math.Floor(days).ToString(Inv)} days.", Math.Floor(days));
                        break;
                    case "traffic":
                        if (d.PortsAt == null || now - d.PortsAt.Value > TimeSpan.FromMinutes(20)) break;
                        foreach (var p in d.PortList)
                        {
                            if (!p.Up || !p.Rates || p.Mbps <= 0) continue;
                            var top = Math.Max(p.InPct, p.OutPct); bool busy;
                            if (r.Minutes <= 5) busy = top > r.Threshold;
                            else
                            {
                                var s = ports?.Invoke(r.Minutes).GetValueOrDefault((d.Id, p.Index));
                                busy = s != null && s.Samples >= 2 && s.First <= now.AddMinutes(-r.Minutes * 0.7) && s.MinPct > r.Threshold;
                            }
                            if (busy) Add("traffic:" + p.Index, r.Severity, $"Port {PortLabel(p)} is at {top.ToString("0.#", Inv)}% of {(p.Mbps >= 1000 ? p.Mbps / 1000 + " Gbps" : p.Mbps + " Mbps")} (in {p.InPct.ToString("0.#", Inv)}%, out {p.OutPct.ToString("0.#", Inv)}%){(r.Minutes > 5 ? $", above {th}% for {r.Minutes} minutes" : "")}.", top);
                        }
                        break;
                    case "port":
                        foreach (var p in d.PortList)
                            if (p.Watched && p.AdminUp && !p.Up && p.DownSince is DateTime pd && now - pd >= TimeSpan.FromMinutes(Math.Max(1, r.Minutes)))
                                Add("port:" + p.Index, r.Severity, $"Port {PortLabel(p)} has been down for {MonitoringModule.Dur(now - pd)}.", Math.Round((now - pd).TotalMinutes));
                        break;
                    case "service" or "process":
                    {
                        if (d.WatchedAt == null || now - d.WatchedAt.Value > TimeSpan.FromMinutes(10)) break;
                        var kind = r.Metric;
                        foreach (var n in r.Names)
                        {
                            var w = d.WatchedList.FirstOrDefault(x => x.Kind == kind && string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase));
                            if (w == null || w.Running || w.State == "unknown") continue;
                            Add($"{kind}:{n.ToLowerInvariant()}", r.Severity, kind == "service"
                                ? (w.State == "missing" ? $"Service {n} is not installed on this device." : $"Service {n} is {w.State}.")
                                : $"Program {n} is not running.", null);
                        }
                        break;
                    }
                    case "log":
                    {
                        var window = now.AddMinutes(-Math.Max(1, r.Minutes));
                        var hits = evByAsset[d.Id].Where(e => e.At >= window && e.Severity <= r.MinSeverity && (r.EventKind == "" || e.Kind == r.EventKind)
                            && (r.Target == "" || e.Text.Contains(r.Target, StringComparison.OrdinalIgnoreCase) || e.Source.Contains(r.Target, StringComparison.OrdinalIgnoreCase))).ToList();
                        if (hits.Count >= Math.Max(1, (int)r.Threshold))
                        {
                            var last = hits.OrderByDescending(e => e.Id).First();
                            var what = last.Kind switch { "trap" => "SNMP trap", "eventlog" => "Windows event", _ => "syslog message" };
                            Add("log:" + r.Id, r.Severity, (hits.Count == 1 ? $"A {what}" : $"{hits.Count} messages in {r.Minutes} minutes; the latest, a {what},") +
                                $" from {(last.Source != "" ? last.Source : "the device")}{(last.EventId > 0 ? $" (event {last.EventId})" : "")}: {(last.Text.Length > 300 ? last.Text[..300] + "…" : last.Text)}", hits.Count);
                        }
                        break;
                    }
                    case "hardware":
                        foreach (var f in d.Faults)
                            Add("hw:" + f.Part + ":" + f.Name, f.State == "Critical" ? r.Severity : "warning",
                                $"{PartNames.GetValueOrDefault(f.Part, f.Part)} {(f.Name != "" ? "\"" + f.Name + "\" " : "")}reports {f.State.ToLowerInvariant()}.", null);
                        break;
                }
            }
        }
        return all.GroupBy(c => c.Key).Select(g => g.OrderByDescending(c => Rank(c.Severity)).ThenBy(c => c.Rule.Id).First()).ToList();
    }

    sealed record OpenAlert(long Id, string Key, int? RuleId, string RuleName, string Severity, long AssetId, int? CompanyId, long? TicketId, int ClearCount, DateTime OpenedAt, string Text);

    /// <summary>Evaluates every rule now: opens, updates and closes alerts, sends mail and opens tickets. Two runs never overlap: the background run skips, a run someone asked for waits.</summary>
    public static async Task<EvalResult> RunAsync(IServiceProvider sp, CancellationToken ct, bool wait = false)
    {
        var tenant = sp.GetRequiredService<ITenantContext>();
        await using var db = await MonitoringModule.Open(tenant, ct);
        if (wait) { await using var l = new NpgsqlCommand("select pg_advisory_lock(727301)", db); await l.ExecuteNonQueryAsync(ct); }
        else { await using var l = new NpgsqlCommand("select pg_try_advisory_lock(727301)", db); if (await l.ExecuteScalarAsync(ct) is not true) return new EvalResult(0, 0, 0, 0); }
        try { return await Evaluate(sp, tenant, db, ct); }
        finally { await using var u = new NpgsqlCommand("select pg_advisory_unlock(727301)", db); await u.ExecuteScalarAsync(CancellationToken.None); }
    }

    static async Task<EvalResult> Evaluate(IServiceProvider sp, ITenantContext tenant, NpgsqlConnection db, CancellationToken ct)
    {
        var health = sp.GetRequiredService<IDeviceHealth>();
        var now = DateTime.UtcNow;
        var rules = await RulesApi.LoadAsync(db, true, ct);
        var muted = await MutedAsync(db, ct);
        var devices = await health.SnapshotAsync(null, null, ct);
        var cache = new Dictionary<int, IReadOnlyDictionary<long, Sustained>>();
        foreach (var m in rules.Where(r => r.Metric is "cpu" or "memory" && r.Minutes > 1).Select(r => r.Minutes).Distinct()) cache[m] = await health.SustainedAsync(m, ct);
        var pcache = new Dictionary<int, IReadOnlyDictionary<(long, string), PortSustained>>();
        foreach (var m in rules.Where(r => r.Metric == "traffic" && r.Minutes > 5).Select(r => r.Minutes).Distinct()) pcache[m] = await health.SustainedPortsAsync(m, ct);
        var logRules = rules.Where(r => r.Metric == "log").ToList();
        var events = logRules.Count == 0 ? Array.Empty<DeviceEvent>() : await health.RecentEventsAsync(logRules.Max(r => Math.Max(1, r.Minutes)), logRules.Max(r => r.MinSeverity), 20000, ct);
        var conds = Conditions(devices, rules, m => cache.GetValueOrDefault(m) ?? new Dictionary<long, Sustained>(), muted, now,
            m => pcache.GetValueOrDefault(m) ?? new Dictionary<(long, string), PortSustained>(), events);

        var open = new Dictionary<string, OpenAlert>();
        await using (var c = new NpgsqlCommand("select id, akey, rule_id, rule_name, severity, asset_id, company_id, ticket_id, clear_count, opened_at, text from monitoring.alerts where closed_at is null", db))
        {
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                open[rd.GetString(1)] = new OpenAlert(rd.GetInt64(0), rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetInt32(2), rd.GetString(3), rd.GetString(4), rd.GetInt64(5),
                    rd.IsDBNull(6) ? null : rd.GetInt32(6), rd.IsDBNull(7) ? null : rd.GetInt64(7), rd.GetInt32(8), rd.GetDateTime(9), rd.GetString(10));
        }

        var settingsStore = sp.GetRequiredService<ISettings>();
        var settings = await MonitorSettings.LoadAsync(settingsStore, ct);
        var protector = sp.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>().CreateProtector(Teams.Purpose);
        var baseUrl = await Teams.BaseAsync(settingsStore, tenant.Require(), ct);
        var actions = sp.GetService<IDeviceActions>();
        async Task Post(Condition c, long alertId, string kind, DateTime opened, TimeSpan? lasted)
        {
            if (!c.Rule.Teams) return;
            var sealedUrl = c.Device.CompanyId is int ci && settings.CompanyTeams.TryGetValue(ci.ToString(), out var own) && own != "" ? own : settings.Teams;
            if (sealedUrl == "") return;
            var title = kind == "cleared" ? $"Cleared: {c.Device.Name}" : $"{(c.Severity == "critical" ? "Critical" : "Warning")}: {c.Device.Name}";
            var tone = kind == "cleared" ? "Good" : c.Severity == "critical" ? "Attention" : "Warning";
            var text = kind == "cleared" ? $"The alert has cleared after {MonitoringModule.Dur(lasted ?? TimeSpan.Zero)}. {c.Text}" : c.Text;
            await Teams.QueueAsync(db, alertId, sealedUrl, Teams.Card(title, tone, text, new[] { ("Customer", c.Device.Company), ("Rule", c.Rule.Name), ("Device", c.Device.Tag), ("Since", opened.ToString("yyyy-MM-dd HH:mm 'UTC'", Inv)) },
                baseUrl + "/#/monitoring/device/" + c.Device.Id, "Open the device"), ct);
        }
        var mail = sp.GetService<IMailQueue>();
        var lic = await sp.GetRequiredService<IEntitlements>().CurrentAsync(ct);
        var desk = lic.Has(Modules.ServiceDesk) ? sp.GetService<ITicketDesk>() : null;
        int opened = 0, closed = 0, raised = 0;

        foreach (var c in conds)
        {
            if (open.Remove(c.Key, out var a))
            {
                var up = Rank(c.Severity) > Rank(a.Severity);
                await using (var u = new NpgsqlCommand(@"update monitoring.alerts set last_at = now(), clear_count = 0, text = @tx, value = @v, severity = @s, rule_id = @r, rule_name = @rn,
                    asset_name = @an, company_name = @cn where id = @id", db))
                {
                    MonitoringModule.P(u, "tx", c.Text); MonitoringModule.P(u, "v", c.Value == null ? null : (float)c.Value.Value); MonitoringModule.P(u, "s", c.Severity);
                    MonitoringModule.P(u, "r", c.Rule.Id); MonitoringModule.P(u, "rn", c.Rule.Name); MonitoringModule.P(u, "an", c.Device.Name); MonitoringModule.P(u, "cn", c.Device.Company); MonitoringModule.P(u, "id", a.Id);
                    await u.ExecuteNonQueryAsync(ct);
                }
                if (up)
                {
                    raised++;
                    if (c.Rule.Mail) await Mail(mail, settings, "monitoring_alert", c, a.Id, a.OpenedAt, null, ct);
                    await Post(c, a.Id, "raised", a.OpenedAt, null);
                    if (c.Rule.Ticket && desk != null && a.TicketId == null) await Ticket(db, desk, c, a.Id, ct);
                    else if (desk != null && a.TicketId is long tk) await desk.NoteAsync(tk, "Monitoring raised the alert to critical: " + c.Text, ct);
                }
                continue;
            }
            long id;
            await using (var ins = new NpgsqlCommand(@"insert into monitoring.alerts (akey, rule_id, rule_name, metric, severity, asset_id, asset_name, asset_tag, company_id, company_name, subject, text, value)
                values (@k, @r, @rn, @m, @s, @a, @an, @at, @co, @cn, @su, @tx, @v) on conflict do nothing returning id", db))
            {
                MonitoringModule.P(ins, "k", c.Key); MonitoringModule.P(ins, "r", c.Rule.Id); MonitoringModule.P(ins, "rn", c.Rule.Name); MonitoringModule.P(ins, "m", c.Metric);
                MonitoringModule.P(ins, "s", c.Severity); MonitoringModule.P(ins, "a", c.Device.Id); MonitoringModule.P(ins, "an", c.Device.Name); MonitoringModule.P(ins, "at", c.Device.Tag);
                MonitoringModule.P(ins, "co", c.Device.CompanyId); MonitoringModule.P(ins, "cn", c.Device.Company); MonitoringModule.P(ins, "su", c.Subject); MonitoringModule.P(ins, "tx", c.Text);
                MonitoringModule.P(ins, "v", c.Value == null ? null : (float)c.Value.Value);
                if (await ins.ExecuteScalarAsync(ct) is not long nid) continue;
                id = nid;
            }
            opened++;
            if (c.Rule.Mail && await Mail(mail, settings, "monitoring_alert", c, id, now, null, ct))
            { await using var m = new NpgsqlCommand("update monitoring.alerts set mailed = true where id = @i", db); MonitoringModule.P(m, "i", id); await m.ExecuteNonQueryAsync(ct); }
            if (c.Rule.Ticket && desk != null) await Ticket(db, desk, c, id, ct);
            await Post(c, id, "opened", now, null);
            if (c.Rule.Restart && c.Metric == "service" && actions != null)
            {
                var svc = c.Rule.Names.FirstOrDefault(n => string.Equals("service:" + n.ToLowerInvariant(), c.Subject, StringComparison.Ordinal));
                var job = svc == null ? null : await actions.RestartServiceAsync(c.Device.Id, svc, "Monitoring", ct);
                await using var an = new NpgsqlCommand("update monitoring.alerts set action_note = @n where id = @i", db);
                MonitoringModule.P(an, "n", job == null ? "Restart not possible: the device has no working agent." : $"Restart asked for at {now:HH:mm} UTC (job {job})."); MonitoringModule.P(an, "i", id);
                await an.ExecuteNonQueryAsync(ct);
            }
        }

        // what no longer holds: closed at once when the device is muted, gone or its rule is off; otherwise after two clear runs in a row
        var byId = devices.ToDictionary(d => d.Id);
        var ruleIds = rules.Select(r => r.Id).ToHashSet();
        foreach (var a in open.Values)
        {
            string? reason = muted.Contains(a.AssetId) ? "Device muted" : !byId.ContainsKey(a.AssetId) ? "Device retired or removed" : a.RuleId is int ri && !ruleIds.Contains(ri) || a.RuleId == null ? "Rule switched off or removed" : null;
            var clears = reason == null ? a.ClearCount + 1 : 2;
            if (clears < 2)
            {
                await using var u = new NpgsqlCommand("update monitoring.alerts set clear_count = @n where id = @i", db); MonitoringModule.P(u, "n", clears); MonitoringModule.P(u, "i", a.Id); await u.ExecuteNonQueryAsync(ct);
                continue;
            }
            await using (var u = new NpgsqlCommand("update monitoring.alerts set closed_at = now(), clear_count = @n, close_reason = @r where id = @i and closed_at is null", db))
            { MonitoringModule.P(u, "n", clears); MonitoringModule.P(u, "r", reason ?? "Cleared"); MonitoringModule.P(u, "i", a.Id); if (await u.ExecuteNonQueryAsync(ct) == 0) continue; }
            closed++;
            var rule = rules.FirstOrDefault(r => r.Id == a.RuleId);
            if (reason == null && settings.MailResolved && rule != null && byId.TryGetValue(a.AssetId, out var dev))
            {
                var cc = new Condition(a.Key, rule, dev, rule.Metric, "", a.Severity, a.Text, null);
                if (rule.Mail) await Mail(mail, settings, "monitoring_resolved", cc, a.Id, a.OpenedAt, now - a.OpenedAt, ct);
                await Post(cc, a.Id, "cleared", a.OpenedAt, now - a.OpenedAt);
            }
            if (desk != null && a.TicketId is long tk)
                await desk.NoteAsync(tk, (reason ?? "The alert has cleared") + $" after {MonitoringModule.Dur(now - a.OpenedAt)}: {a.RuleName}.", ct);
        }
        await Escalate(db, settings, mail, protector, baseUrl, now, ct);
        await Teams.SendDueAsync(db, protector, ct);
        return new EvalResult(opened, closed, conds.Count, raised);
    }

    /// <summary>Critical alerts nobody acknowledged in time go to the escalation list and channel, once per alert.</summary>
    static async Task Escalate(NpgsqlConnection db, MonitorSettings s, IMailQueue? mail, Microsoft.AspNetCore.DataProtection.IDataProtector p, string baseUrl, DateTime now, CancellationToken ct)
    {
        if (s.EscalateMinutes <= 0 || (MonitorSettings.Split(s.EscalateRecipients).Count == 0 && s.EscalateTeams == "")) return;
        var due = new List<(long Id, long Asset, string Name, string Tag, string Company, string Rule, string Text, DateTime Opened)>();
        await using (var c = new NpgsqlCommand(@"update monitoring.alerts set escalated_at = now() where closed_at is null and severity = 'critical' and ack_at is null and escalated_at is null
            and opened_at <= now() - make_interval(mins => @m) returning id, asset_id, asset_name, asset_tag, company_name, rule_name, text, opened_at", db))
        {
            MonitoringModule.P(c, "m", s.EscalateMinutes); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) due.Add((rd.GetInt64(0), rd.GetInt64(1), rd.GetString(2), rd.GetString(3), rd.GetString(4), rd.GetString(5), rd.GetString(6), rd.GetDateTime(7)));
        }
        foreach (var a in due)
        {
            var values = new Dictionary<string, string>
            {
                ["severity"] = "Critical", ["device"] = a.Name, ["tag"] = a.Tag, ["company"] = a.Company, ["rule"] = a.Rule, ["message"] = a.Text,
                ["opened"] = a.Opened.ToString("yyyy-MM-dd HH:mm 'UTC'", Inv), ["duration"] = MonitoringModule.Dur(now - a.Opened), ["ticket"] = "", ["path"] = "#/monitoring/device/" + a.Asset,
            };
            if (mail != null) foreach (var to in MonitorSettings.Split(s.EscalateRecipients)) try { await mail.EnqueueTemplateAsync("monitoring_escalated", to, values, $"mon-alert:{a.Id}", ct); } catch (Exception) { }
            await Teams.QueueAsync(db, a.Id, s.EscalateTeams, Teams.Card($"Not acknowledged for {MonitoringModule.Dur(now - a.Opened)}: {a.Name}", "Attention", a.Text,
                new[] { ("Customer", a.Company), ("Rule", a.Rule), ("Device", a.Tag), ("Since", a.Opened.ToString("yyyy-MM-dd HH:mm 'UTC'", Inv)) }, baseUrl + "/#/monitoring/device/" + a.Asset, "Open the device"), ct);
        }
    }

    internal static async Task<HashSet<long>> MutedAsync(NpgsqlConnection db, CancellationToken ct)
    {
        var o = new HashSet<long>();
        await using (var d = new NpgsqlCommand("delete from monitoring.mutes where until is not null and until <= now()", db)) await d.ExecuteNonQueryAsync(ct);
        await using var c = new NpgsqlCommand("select asset_id from monitoring.mutes", db);
        await using var rd = await c.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) o.Add(rd.GetInt64(0));
        return o;
    }

    static async Task<bool> Mail(IMailQueue? mail, MonitorSettings s, string template, Condition c, long alertId, DateTime opened, TimeSpan? lasted, CancellationToken ct)
    {
        if (mail == null) return false;
        var sent = false;
        var values = new Dictionary<string, string>
        {
            ["severity"] = c.Severity == "critical" ? "Critical" : "Warning", ["device"] = c.Device.Name, ["tag"] = c.Device.Tag, ["company"] = c.Device.Company,
            ["rule"] = c.Rule.Name, ["message"] = c.Text, ["opened"] = opened.ToString("yyyy-MM-dd HH:mm 'UTC'", Inv),
            ["duration"] = lasted is TimeSpan t ? MonitoringModule.Dur(t) : "", ["ticket"] = "", ["path"] = "#/monitoring/device/" + c.Device.Id,
        };
        foreach (var to in s.For(c.Device.CompanyId))
            try { sent |= await mail.EnqueueTemplateAsync(template, to, values, $"mon-alert:{alertId}", ct); } catch (Exception) { /* mail is best effort */ }
        return sent;
    }

    internal static async Task<string?> Ticket(NpgsqlConnection db, ITicketDesk desk, Condition c, long alertId, CancellationToken ct)
    {
        try
        {
            var (tid, number) = await desk.OpenAsync(new TicketRequest($"[{(c.Severity == "critical" ? "Critical" : "Warning")}] {c.Device.Name}: {c.Rule.Name}",
                $"{c.Text}\n\nDevice: {c.Device.Name} ({c.Device.Tag})\nCustomer: {c.Device.Company}\nRule: {c.Rule.Name}\n\nOpened by Monitoring. The ticket gets a note when the alert clears.",
                c.Rule.TicketPriority, c.Device.CompanyId, c.Device.Id, c.Device.Name, "monitoring", "Monitoring"), ct);
            await using var u = new NpgsqlCommand("update monitoring.alerts set ticket_id = @t, ticket_number = @n where id = @i", db);
            MonitoringModule.P(u, "t", tid); MonitoringModule.P(u, "n", number); MonitoringModule.P(u, "i", alertId); await u.ExecuteNonQueryAsync(ct);
            return number;
        }
        catch (Exception) { return null; }
    }
}
