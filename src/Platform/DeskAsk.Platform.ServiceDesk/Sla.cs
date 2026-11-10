using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DeskAsk.Platform.ServiceDesk;

/// <summary>Service-level targets per priority (hours), the warning point, and whether the clock stops while a ticket waits for the customer.</summary>
public sealed class SlaConfig
{
    public static readonly string[] Prios = { "critical", "high", "medium", "low" };
    public Dictionary<string, double> Respond = new() { ["critical"] = 1, ["high"] = 2, ["medium"] = 4, ["low"] = 8 };
    public Dictionary<string, double> Resolve = new() { ["critical"] = 4, ["high"] = 8, ["medium"] = 24, ["low"] = 72 };
    public int WarnPct = 75;
    public bool Pause = true, Escalate = true;

    public const string Key = "servicedesk.sla";
    public static readonly string[] DefaultCategories = { "Hardware", "Software", "Network", "Email", "Access and accounts", "Other" };
    public const string CategoriesKey = "servicedesk.categories";

    public object ToJson() => new { respond = Respond, resolve = Resolve, warnPct = WarnPct, pause = Pause, escalate = Escalate };

    /// <summary>Reads and checks the settings JSON; null with an error text when something is out of range.</summary>
    public static SlaConfig? Parse(JsonElement b, out string? error)
    {
        error = null; var c = new SlaConfig();
        if (b.ValueKind != JsonValueKind.Object) { error = "The service levels must be an object."; return null; }
        double Hours(string group, string p)
        {
            if (!b.TryGetProperty(group, out var g) || g.ValueKind != JsonValueKind.Object || !g.TryGetProperty(p, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var h)) return double.NaN;
            return h;
        }
        foreach (var p in Prios)
        {
            var rs = Hours("resolve", p); var rp = Hours("respond", p);
            if (double.IsNaN(rs) || rs < 0.25 || rs > 8760) { error = "Resolution time for " + p + " must be between 0.25 and 8760 hours."; return null; }
            if (double.IsNaN(rp) || rp < 0.25 || rp > 8760) { error = "First response time for " + p + " must be between 0.25 and 8760 hours."; return null; }
            if (rp > rs) { error = "For " + p + ", the first response cannot take longer than the resolution."; return null; }
            c.Resolve[p] = rs; c.Respond[p] = rp;
        }
        if (b.TryGetProperty("warnPct", out var w))
        {
            if (w.ValueKind != JsonValueKind.Number || !w.TryGetInt32(out var pct) || pct is < 10 or > 99) { error = "The warning point must be between 10 and 99 percent."; return null; }
            c.WarnPct = pct;
        }
        if (b.TryGetProperty("pause", out var pa) && pa.ValueKind is JsonValueKind.True or JsonValueKind.False) c.Pause = pa.GetBoolean();
        if (b.TryGetProperty("escalate", out var es) && es.ValueKind is JsonValueKind.True or JsonValueKind.False) c.Escalate = es.GetBoolean();
        return c;
    }

    public static async Task<SlaConfig> LoadAsync(ISettings s, CancellationToken ct)
    {
        var raw = await s.GetAsync(Key, ct);
        if (raw == null) return new SlaConfig();
        try { return Parse(JsonDocument.Parse(raw).RootElement, out _) ?? new SlaConfig(); } catch (JsonException) { return new SlaConfig(); }
    }

    public static async Task<string[]> CategoriesAsync(ISettings s, CancellationToken ct)
    {
        var raw = await s.GetAsync(CategoriesKey, ct);
        if (raw == null) return DefaultCategories;
        try { var l = JsonSerializer.Deserialize<string[]>(raw); return l is { Length: > 0 } ? l : DefaultCategories; } catch (JsonException) { return DefaultCategories; }
    }

    /// <summary>Binds the target hours to a command so due dates can be worked out by the database.</summary>
    public void Bind(NpgsqlCommand c)
    {
        foreach (var p in Prios) { c.Parameters.AddWithValue("rp_" + p, Respond[p]); c.Parameters.AddWithValue("rs_" + p, Resolve[p]); }
    }
    const string Case = "case priority when 'critical' then @{0}_critical when 'high' then @{0}_high when 'medium' then @{0}_medium else @{0}_low end";
    /// <summary>SQL fragments for a ticket's due dates: creation time + target + the time spent waiting for the customer.</summary>
    public static string RespondDue => "created_at + make_interval(secs => " + string.Format(Case, "rp") + " * 3600 + paused_s)";
    public static string ResolveDue => "created_at + make_interval(secs => " + string.Format(Case, "rs") + " * 3600 + paused_s)";

    /// <summary>ok, warning, breached, paused or met (for finished tickets, met or breached).</summary>
    public string State(string status, DateTime created, DateTime? due, DateTime? paused, DateTime? resolved, long pausedS, DateTime now)
    {
        if (due == null) return "ok";
        if (status is "resolved" or "closed") return (resolved ?? now) <= due ? "met" : "breached";
        if (paused != null) return "paused";
        if (now > due) return "breached";
        var target = (due.Value - created).TotalSeconds - pausedS;
        return (now - created).TotalSeconds - pausedS >= target * WarnPct / 100.0 ? "warning" : "ok";
    }
}

/// <summary>Runs every minute: records when a first reply is overdue, when a ticket reaches its warning point and when it misses its target. Each is recorded once per ticket.</summary>
public sealed class SlaJob : IJobHandler
{
    public const string JobType = "servicedesk.sla";
    public string Type => JobType;

    public async Task RunAsync(JobContext job, CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(job.Tenant.ConnectionString); await db.OpenAsync(ct);
        var cfg = new SlaConfig();
        if (job.Services != null)
        {
            var s = job.Services.GetService<ISettings>();
            if (s != null) cfg = await SlaConfig.LoadAsync(s, ct);
        }
        else
        {
            await using var q = new NpgsqlCommand("select value from core.settings where key = @k", db);
            q.Parameters.AddWithValue("k", SlaConfig.Key);
            try { if (await q.ExecuteScalarAsync(ct) is string raw) cfg = SlaConfig.Parse(JsonDocument.Parse(raw).RootElement, out _) ?? cfg; } catch (PostgresException) { }
        }
        if (!cfg.Escalate) return;
        async Task Run(string sql, params (string, object)[] ps)
        {
            await using var c = new NpgsqlCommand(sql, db);
            foreach (var (n, v) in ps) c.Parameters.AddWithValue(n, v);
            await c.ExecuteNonQueryAsync(ct);
        }
        const string Open = "status not in ('resolved','closed') and paused_at is null";
        // 1) the resolution target was missed (also settles the warning, so nobody gets both at once)
        await Run($@"with t as (update servicedesk.tickets set sla_warned = 2 where {Open} and sla_warned < 2 and resolve_due is not null and resolve_due <= now() returning id),
            c as (insert into servicedesk.comments (ticket_id, author_name, kind, body) select id, 'SLA', 'system', 'SLA: the resolution target was missed.' from t)
            insert into servicedesk.events (ticket_id, kind) select id, 'sla.breached' from t");
        // 2) the warning point
        await Run($@"with t as (update servicedesk.tickets set sla_warned = 1 where {Open} and sla_warned < 1 and resolve_due is not null
              and now() >= created_at + make_interval(secs => paused_s) + (resolve_due - created_at - make_interval(secs => paused_s)) * (@w / 100.0) returning id),
            c as (insert into servicedesk.comments (ticket_id, author_name, kind, body) select id, 'SLA', 'system', 'SLA: ' || @w || ' percent of the resolution target is used.' from t)
            insert into servicedesk.events (ticket_id, kind) select id, 'sla.warning' from t", ("w", cfg.WarnPct));
        // 3) nobody has answered in time
        await Run($@"with t as (update servicedesk.tickets set resp_flag = true where {Open} and not resp_flag and first_response_at is null and respond_due is not null and respond_due <= now() returning id),
            c as (insert into servicedesk.comments (ticket_id, author_name, kind, body) select id, 'SLA', 'system', 'SLA: no first reply within the target time.' from t)
            insert into servicedesk.events (ticket_id, kind) select id, 'sla.no_response' from t");
    }
}
