using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace DeskAsk.Platform.Monitoring;

/// <summary>
/// An alert rule. Metric decides what Threshold and Minutes mean: offline (down for Minutes), cpu and memory (above Threshold % for Minutes),
/// disk (free space below Threshold %), check (failed Threshold times in a row), cert (expires within Threshold days), hardware (a part reports a fault).
/// traffic (a port above Threshold % for Minutes), port (a watched port down for Minutes), service and process (one named in Target not running;
/// Restart asks the agent to start a service again), log (Threshold or more messages of EventKind at MinSeverity or worse, containing Target,
/// within Minutes). Teams posts the alert to the Teams channel.
/// Kinds empty = every kind of device; CompanyId null = every customer.
/// </summary>
public sealed record Rule(int Id, string Name, bool Enabled, string Metric, double Threshold, int Minutes, string Severity, string[] Kinds, int? CompanyId,
    bool Mail, bool Ticket, string TicketPriority, bool Builtin, string Target = "", string EventKind = "", int MinSeverity = 3, bool Teams = true, bool Restart = false)
{
    /// <summary>The names in Target (services or programs), one per comma or line.</summary>
    public string[] Names => Target.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}

public static class RulesApi
{
    public static readonly string[] Metrics = { "offline", "cpu", "memory", "disk", "check", "cert", "hardware", "traffic", "port", "service", "process", "log" };
    public static readonly string[] EventKinds = { "", "syslog", "trap", "eventlog" };
    static readonly string[] SevWords = { "emergency", "alert", "critical", "error", "warning", "notice", "information", "debug" };
    public static readonly string[] Kinds = { "computer", "server", "network", "printer", "other" };
    static readonly string[] Prios = { "critical", "high", "medium", "low" };

    internal static async Task<List<Rule>> LoadAsync(NpgsqlConnection db, bool enabledOnly, CancellationToken ct)
    {
        var o = new List<Rule>();
        await using var c = new NpgsqlCommand("select id, name, enabled, metric, threshold, minutes, severity, kinds, company_id, mail, ticket, ticket_priority, builtin, target, event_kind, min_severity, teams, restart from monitoring.rules" + (enabledOnly ? " where enabled" : "") + " order by id", db);
        await using var rd = await c.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            o.Add(new Rule(rd.GetInt32(0), rd.GetString(1), rd.GetBoolean(2), rd.GetString(3), rd.GetFloat(4), rd.GetInt32(5), rd.GetString(6), rd.GetFieldValue<string[]>(7),
                rd.IsDBNull(8) ? null : rd.GetInt32(8), rd.GetBoolean(9), rd.GetBoolean(10), rd.GetString(11), rd.GetBoolean(12), rd.GetString(13), rd.GetString(14), rd.GetInt32(15), rd.GetBoolean(16), rd.GetBoolean(17)));
        return o;
    }

    static object View(Rule r, IReadOnlyDictionary<int, string> companies) => new
    {
        r.Id, r.Name, r.Enabled, r.Metric, r.Threshold, r.Minutes, r.Severity, r.Kinds, r.CompanyId,
        company = r.CompanyId is int c ? companies.GetValueOrDefault(c, "Customer " + c) : "", r.Mail, r.Ticket, r.TicketPriority, r.Builtin, description = Describe(r),
        r.Target, r.EventKind, r.MinSeverity, r.Teams, r.Restart,
    };

    /// <summary>The rule in words, as the list shows it.</summary>
    public static string Describe(Rule r)
    {
        var who = r.Kinds.Length == 0 ? "any device" : string.Join(", ", r.Kinds.Select(k => k switch { "computer" => "computers", "server" => "servers", "network" => "network devices", "printer" => "printers", _ => "other devices" }));
        var t = r.Threshold.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        return r.Metric switch
        {
            "offline" => $"{who} down or not reporting for {MonitoringModule.Dur(TimeSpan.FromMinutes(r.Minutes))}",
            "cpu" => $"Processor above {t}% for {MonitoringModule.Dur(TimeSpan.FromMinutes(Math.Max(1, r.Minutes)))} on {who}",
            "memory" => $"Memory above {t}% for {MonitoringModule.Dur(TimeSpan.FromMinutes(Math.Max(1, r.Minutes)))} on {who}",
            "disk" => $"Less than {t}% free on a disk or datastore of {who}",
            "check" => $"A service check on {who} fails {Math.Max(1, (int)r.Threshold)} times in a row",
            "cert" => $"A certificate checked on {who} expires within {t} days",
            "traffic" => $"A port of {who} above {t}% of its speed" + (r.Minutes > 1 ? $" for {MonitoringModule.Dur(TimeSpan.FromMinutes(r.Minutes))}" : ""),
            "port" => $"A watched port of {who} down for {MonitoringModule.Dur(TimeSpan.FromMinutes(Math.Max(1, r.Minutes)))}",
            "service" => $"Service {string.Join(", ", r.Names)} not running on {who}" + (r.Restart ? " (the agent restarts it)" : ""),
            "process" => $"Program {string.Join(", ", r.Names)} not running on {who}",
            "log" => $"{(Math.Max(1, (int)r.Threshold) == 1 ? "A" : Math.Max(1, (int)r.Threshold).ToString())} {(r.EventKind switch { "syslog" => "syslog message", "trap" => "SNMP trap", "eventlog" => "Windows event", _ => "message" })}{(Math.Max(1, (int)r.Threshold) == 1 ? "" : "s")} at {SevWords[Math.Clamp(r.MinSeverity, 0, 7)]} or worse from {who}" + (r.Target != "" ? $" containing \"{r.Target}\"" : "") + $" within {MonitoringModule.Dur(TimeSpan.FromMinutes(Math.Max(1, r.Minutes)))}",
            _ => $"A hardware part of {who} reports a fault",
        };
    }

    static (Rule? Rule, string? Error) Parse(JsonElement b, int id, bool builtin)
    {
        string S(string n, string d = "") => b.ValueKind == JsonValueKind.Object && b.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : d;
        double N(string n, double d = 0) => b.ValueKind == JsonValueKind.Object && b.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : d;
        bool B(string n, bool d) => b.ValueKind == JsonValueKind.Object && b.TryGetProperty(n, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : d;
        var name = S("name"); if (name == "" || name.Length > 120) return (null, "Give the rule a name (up to 120 characters).");
        var metric = S("metric"); if (!Metrics.Contains(metric)) return (null, "metric must be one of " + string.Join(", ", Metrics));
        var sev = S("severity", "warning"); if (sev is not ("warning" or "critical")) return (null, "severity must be warning or critical");
        var th = N("threshold"); var mins = (int)N("minutes");
        switch (metric)
        {
            case "offline": if (mins < 2 || mins > 10080) return (null, "Down for how long: 2 minutes to 7 days."); th = 0; break;
            case "cpu" or "memory": if (th < 1 || th > 100) return (null, "The percentage must be 1 to 100."); if (mins < 0 || mins > 1440) return (null, "For how long: 0 to 1440 minutes."); break;
            case "disk": if (th <= 0 || th >= 100) return (null, "Free space below: more than 0 and less than 100 %."); mins = 0; break;
            case "check": if (th < 1 || th > 100) return (null, "Failures in a row: 1 to 100."); th = Math.Floor(th); mins = 0; break;
            case "cert": if (th < 1 || th > 365) return (null, "Days before expiry: 1 to 365."); mins = 0; break;
            case "traffic": if (th < 1 || th > 100) return (null, "The percentage must be 1 to 100."); if (mins < 0 || mins > 1440) return (null, "For how long: 0 to 1440 minutes."); break;
            case "port": if (mins < 1 || mins > 10080) return (null, "Down for how long: 1 minute to 7 days."); th = 0; break;
            case "service" or "process": th = 0; mins = 0; break;
            case "log": if (th < 1 || th > 10000) return (null, "How many messages: 1 to 10000."); th = Math.Floor(th); if (mins < 1 || mins > 1440) return (null, "Within how many minutes: 1 to 1440."); break;
            default: th = 0; mins = 0; break;
        }
        var kinds = b.TryGetProperty("kinds", out var kv) && kv.ValueKind == JsonValueKind.Array ? kv.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Distinct().ToArray() : Array.Empty<string>();
        if (kinds.Any(k => !Kinds.Contains(k))) return (null, "kinds may hold " + string.Join(", ", Kinds));
        int? company = b.TryGetProperty("companyId", out var cv) && cv.ValueKind == JsonValueKind.Number ? cv.GetInt32() : null;
        var prio = S("ticketPriority", "high"); if (!Prios.Contains(prio)) return (null, "ticketPriority must be critical, high, medium or low");
        var target = S("target"); if (target.Length > 500) return (null, "Keep the names or text under 500 characters.");
        var evKind = S("eventKind"); if (!EventKinds.Contains(evKind)) return (null, "eventKind must be syslog, trap, eventlog or empty (any)");
        var minSev = (int)N("minSeverity", 3); if (minSev < 0 || minSev > 7) return (null, "minSeverity must be 0 (emergency) to 7 (debug)");
        if (metric is "service" or "process")
        {
            var names = target.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (names.Length == 0) return (null, metric == "service" ? "Name the services to watch (for example Spooler, MSSQLSERVER)." : "Name the programs to watch (for example sqlservr.exe, nginx).");
            if (names.Length > 20) return (null, "Watch at most 20 names in one rule.");
            if (names.Any(n => n.Length > 120 || n.IndexOfAny(new[] { '\'', '"', '`', '$', '&', '|', '<', '>' }) >= 0)) return (null, "A name has characters that are not allowed (quotes, $, &, |, < or >).");
        }
        else if (metric != "log") target = "";
        var restart = metric == "service" && B("restart", false);
        return (new Rule(id, name, B("enabled", true), metric, th, mins, sev, kinds, company, B("mail", true), B("ticket", false), prio, builtin, target, metric == "log" ? evKind : "", metric == "log" ? minSev : 3, B("teams", true), restart), null);
    }

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        RouteHandlerBuilder Admin(RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole("admin")).RequireModule(key);
        static async Task<Dictionary<int, string>> Companies(ICompanyDirectory d, CancellationToken ct) => (await d.ListAsync(ct)).ToDictionary(c => c.Id, c => c.Name);

        r.MapGet("/api/monitoring/rules", async (ITenantContext t, ICompanyDirectory dir, CancellationToken ct) =>
        {
            await using var db = await MonitoringModule.Open(t, ct);
            var co = await Companies(dir, ct);
            return Results.Ok(new { items = (await LoadAsync(db, false, ct)).Select(x => View(x, co)) });
        }).RequireAuthorization().RequireModule(key).RequireAccess(key, Acts.Read);

        Admin(r.MapPost("/api/monitoring/rules", async (JsonElement b, ITenantContext t, ICompanyDirectory dir, IAuditLog audit, CancellationToken ct) =>
        {
            var (rule, err) = Parse(b, 0, false); if (rule == null) return MonitoringModule.Bad("bad_input", err);
            if (rule.CompanyId is int ci && await dir.GetAsync(ci, ct) == null) return MonitoringModule.Bad("bad_input", "That customer does not exist.");
            await using var db = await MonitoringModule.Open(t, ct);
            await using var c = new NpgsqlCommand(@"insert into monitoring.rules (name, enabled, metric, threshold, minutes, severity, kinds, company_id, mail, ticket, ticket_priority, target, event_kind, min_severity, teams, restart)
                values (@n,@e,@m,@th,@mi,@s,@k,@co,@ma,@ti,@tp,@tg,@ek,@ms,@tm,@rs) returning id", db);
            Bind(c, rule);
            var id = (int)(await c.ExecuteScalarAsync(ct))!;
            await audit.WriteAsync("monitoring.rule_created", id.ToString(), Describe(rule), ct: ct);
            AgentWatchList.Forget(t.Require().Id);
            return Results.Ok(new { id });
        }));

        Admin(r.MapPut("/api/monitoring/rules/{id:int}", async (int id, JsonElement b, ITenantContext t, ICompanyDirectory dir, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await MonitoringModule.Open(t, ct);
            var old = (await LoadAsync(db, false, ct)).FirstOrDefault(x => x.Id == id); if (old == null) return Results.NotFound();
            var (rule, err) = Parse(b, id, old.Builtin); if (rule == null) return MonitoringModule.Bad("bad_input", err);
            if (rule.CompanyId is int ci && await dir.GetAsync(ci, ct) == null) return MonitoringModule.Bad("bad_input", "That customer does not exist.");
            await using var c = new NpgsqlCommand(@"update monitoring.rules set name = @n, enabled = @e, metric = @m, threshold = @th, minutes = @mi, severity = @s, kinds = @k,
                company_id = @co, mail = @ma, ticket = @ti, ticket_priority = @tp, target = @tg, event_kind = @ek, min_severity = @ms, teams = @tm, restart = @rs, updated_at = now() where id = @id", db);
            Bind(c, rule); MonitoringModule.P(c, "id", id); await c.ExecuteNonQueryAsync(ct);
            await audit.WriteAsync("monitoring.rule_changed", id.ToString(), Describe(rule) + (rule.Enabled ? "" : " (off)"), ct: ct);
            AgentWatchList.Forget(t.Require().Id);
            return Results.Ok(new { ok = true });
        }));

        Admin(r.MapDelete("/api/monitoring/rules/{id:int}", async (int id, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await MonitoringModule.Open(t, ct);
            await using var c = new NpgsqlCommand("delete from monitoring.rules where id = @id returning name", db); MonitoringModule.P(c, "id", id);
            if (await c.ExecuteScalarAsync(ct) is not string name) return Results.NotFound();
            await audit.WriteAsync("monitoring.rule_deleted", id.ToString(), name, ct: ct);
            AgentWatchList.Forget(t.Require().Id);
            return Results.Ok(new { ok = true });
        }));
    }

    static void Bind(NpgsqlCommand c, Rule r)
    {
        MonitoringModule.P(c, "n", r.Name); MonitoringModule.P(c, "e", r.Enabled); MonitoringModule.P(c, "m", r.Metric); MonitoringModule.P(c, "th", (float)r.Threshold);
        MonitoringModule.P(c, "mi", r.Minutes); MonitoringModule.P(c, "s", r.Severity); MonitoringModule.P(c, "k", r.Kinds); MonitoringModule.P(c, "co", r.CompanyId);
        MonitoringModule.P(c, "ma", r.Mail); MonitoringModule.P(c, "ti", r.Ticket); MonitoringModule.P(c, "tp", r.TicketPriority);
        MonitoringModule.P(c, "tg", r.Target); MonitoringModule.P(c, "ek", r.EventKind); MonitoringModule.P(c, "ms", r.MinSeverity); MonitoringModule.P(c, "tm", r.Teams); MonitoringModule.P(c, "rs", r.Restart);
    }
}
