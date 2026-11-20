using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DeskAsk.Platform.Monitoring;

/// <summary>
/// Monitoring › Reports: alert history, busiest devices (processor and memory), disk space, network port traffic and service state, as tables
/// with CSV export. The monthly uptime report keeps its own page (/api/monitoring/reports/uptime). Filters: company (id or "none"), days (1 to 366), q.
/// </summary>
public static class MonitoringReports
{
    public sealed record Report(string Key, string Group, string Title, string About, string[] Columns, string Sql, string Search, string Order);

    const string Dev = "from assets.assets a left join assets.companies co on co.id = a.company_id";

    public static readonly Report[] All =
    {
        new("alerts", "Alerts", "Alert history", "Every alert opened in the period: device, rule, severity, how long it lasted and the ticket it opened; newest first.",
            new[] { "Opened (UTC)", "Device", "Customer", "Severity", "Rule", "What happened", "Closed (UTC)", "Lasted (minutes)", "Acknowledged by", "Ticket" },
            @"select al.asset_id, to_char(al.opened_at at time zone 'UTC', 'YYYY-MM-DD HH24:MI'), al.asset_name, al.company_name, al.severity, al.rule_name, coalesce(nullif(al.subject, ''), al.text),
                coalesce(to_char(al.closed_at at time zone 'UTC', 'YYYY-MM-DD HH24:MI'), 'Still open'), round(extract(epoch from coalesce(al.closed_at, now()) - al.opened_at) / 60)::text, al.ack_by, al.ticket_number
              from monitoring.alerts al where (not @sc or al.company_id = any(@scope)) and al.opened_at > now() - make_interval(days => @d) {C:al.company_id}",
            "al.asset_name || ' ' || al.company_name || ' ' || al.rule_name || ' ' || al.text || ' ' || al.ticket_number", "al.opened_at desc"),
        new("resources", "Performance", "Busiest devices", "Average and highest processor and memory use in the period, and the lowest free disk space seen; busiest first.",
            new[] { "Device", "Customer", "Processor average %", "Processor highest %", "Memory average %", "Memory highest %", "Lowest free disk %", "Hours measured" },
            $@"select a.id, a.name, coalesce(co.name, ''), round(avg(h.cpu_avg)::numeric, 1)::text, round(max(h.cpu_max)::numeric, 1)::text, round(avg(h.mem_avg)::numeric, 1)::text,
                round(max(h.mem_max)::numeric, 1)::text, coalesce(round(min(h.disk_free_min)::numeric, 1)::text, ''), count(*)::text
              {Dev} join assets.metrics_hour h on h.asset_id = a.id
              where (not @sc or a.company_id = any(@scope)) and h.hour > now() - make_interval(days => @d) {{C:a.company_id}} {{Q}} group by a.id, a.name, co.name",
            "a.name || ' ' || a.tag || ' ' || a.hostname", "avg(h.cpu_avg) desc nulls last, a.name"),
        new("disks", "Performance", "Disk space now", "Every disk of every monitored device with its size and free space; the fullest first.",
            new[] { "Device", "Customer", "Disk", "Size (GB)", "Free (GB)", "Free %", "Measured (UTC)" },
            $@"select a.id, a.name, coalesce(co.name, ''), d->>'mount', round(coalesce((d->>'total')::numeric, 0) / 1073741824, 1)::text, round(coalesce((d->>'free')::numeric, 0) / 1073741824, 1)::text,
                coalesce(d->>'pct', ''), coalesce(to_char(a.m_at at time zone 'UTC', 'YYYY-MM-DD HH24:MI'), '')
              {Dev} cross join lateral jsonb_array_elements(case when jsonb_typeof(a.m_disks) = 'array' then a.m_disks else '[]' end) d
              where (not @sc or a.company_id = any(@scope)) and a.monitored and a.status not in ('retired', 'lost') {{C:a.company_id}}",
            "a.name || ' ' || a.tag || ' ' || coalesce(d->>'mount', '')", "coalesce((d->>'pct')::float, 100), a.name"),
        new("traffic", "Network", "Network port traffic", "Average and highest use of each network port in the period, in and out; the busiest first.",
            new[] { "Device", "Customer", "Port", "In average %", "In highest %", "Out average %", "Out highest %", "Errors", "Hours measured" },
            $@"select a.id, a.name, coalesce(co.name, ''), coalesce((select coalesce(nullif(p->>'alias', ''), p->>'name') from jsonb_array_elements(case when jsonb_typeof(a.m_ports) = 'array' then a.m_ports else '[]' end) p where p->>'index' = h.if_index limit 1), h.if_index),
                round(avg(h.in_avg)::numeric, 1)::text, round(max(h.in_max)::numeric, 1)::text, round(avg(h.out_avg)::numeric, 1)::text, round(max(h.out_max)::numeric, 1)::text,
                round(coalesce(sum(h.errors), 0)::numeric)::text, count(*)::text
              {Dev} join assets.ifstats_hour h on h.asset_id = a.id
              where (not @sc or a.company_id = any(@scope)) and h.hour > now() - make_interval(days => @d) {{C:a.company_id}} {{Q}} group by a.id, a.name, co.name, a.m_ports, h.if_index",
            "a.name || ' ' || a.tag || ' ' || h.if_index", "greatest(max(h.in_max), max(h.out_max)) desc nulls last, a.name"),
        new("services", "Services", "Service status", "Services and programs Monitoring watches on each device, as last reported; stopped ones first.",
            new[] { "Device", "Customer", "Service or program", "Kind", "State", "Reported (UTC)" },
            $@"select a.id, a.name, coalesce(co.name, ''), s->>'name', coalesce(s->>'kind', 'service'), coalesce(s->>'state', ''), coalesce(to_char(a.m_services_at at time zone 'UTC', 'YYYY-MM-DD HH24:MI'), '')
              {Dev} cross join lateral jsonb_array_elements(case when jsonb_typeof(a.m_services) = 'array' then a.m_services else '[]' end) s
              where (not @sc or a.company_id = any(@scope)) and a.monitored and a.status not in ('retired', 'lost') {{C:a.company_id}}",
            "a.name || ' ' || a.tag || ' ' || coalesce(s->>'name', '')", "case when (s->>'running')::boolean then 1 else 0 end, a.name, s->>'name'"),
    };

    sealed record Filter(string Company, int Days, string Q);

    static string? Read(HttpContext h, out Filter f)
    {
        var q = h.Request.Query;
        var days = int.TryParse(q["days"], out var d) ? d : 30;
        f = new Filter(q["company"].ToString(), days, q["q"].ToString().Trim());
        if (f.Company is not ("" or "none") && !int.TryParse(f.Company, out _)) return "company must be a customer id or none";
        if (days is < 1 or > 366) return "days must be between 1 and 366";
        if (f.Q.Length > 100) return "the search is longer than 100 characters";
        return null;
    }

    static async Task<(int Total, List<string[]> Rows)> Run(NpgsqlConnection db, Report r, Filter f, IReadOnlyList<int>? scope, int max, CancellationToken ct)
    {
        var sql = r.Sql;
        // {C:col} becomes the customer filter on that column; {Q} the search where rows are grouped (the search must apply before grouping)
        var ci = sql.IndexOf("{C:", StringComparison.Ordinal);
        if (ci >= 0)
        {
            var end = sql.IndexOf('}', ci); var col = sql[(ci + 3)..end];
            sql = sql[..ci] + (f.Company == "none" ? $"and {col} is null" : f.Company != "" ? $"and {col} = @co" : "") + sql[(end + 1)..];
        }
        var search = f.Q == "" ? "" : "and (" + r.Search + ") ilike @q";
        if (sql.Contains("{Q}")) sql = sql.Replace("{Q}", search);
        else if (search != "") sql += " " + search;
        void Bind(NpgsqlCommand c)
        {
            c.Parameters.AddWithValue("sc", scope != null); c.Parameters.AddWithValue("scope", scope?.ToArray() ?? Array.Empty<int>());
            c.Parameters.AddWithValue("d", f.Days);
            if (f.Company is not ("" or "none")) c.Parameters.AddWithValue("co", int.Parse(f.Company));
            if (f.Q != "") c.Parameters.AddWithValue("q", "%" + f.Q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        }
        int total;
        await using (var cnt = new NpgsqlCommand($"select count(*) from ({sql}) x", db)) { Bind(cnt); total = Convert.ToInt32(await cnt.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture); }
        await using var c = new NpgsqlCommand($"{sql} order by {r.Order} limit @max", db);
        Bind(c); c.Parameters.AddWithValue("max", max);
        var rows = new List<string[]>();
        await using var rd = await c.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) rows.Add(Enumerable.Range(0, rd.FieldCount).Select(i => rd.IsDBNull(i) ? "" : Convert.ToString(rd.GetValue(i), CultureInfo.InvariantCulture) ?? "").ToArray());
        return (total, rows);
    }

    static Task<IReadOnlyList<int>?> Scope(HttpContext h) => h.RequestServices.GetRequiredService<IAccess>().ScopeAsync(h.User, Modules.Monitoring, Acts.Read, h.RequestAborted);

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        r.MapGet("/api/monitoring/report-catalog", () => Results.Ok(All.Select(x => new { x.Key, x.Group, x.Title, x.About })))
            .RequireAuthorization().RequireModule(key).RequireAccess(key, Acts.Read);

        r.MapGet("/api/monitoring/report/{name}", async (string name, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            var rep = All.FirstOrDefault(x => x.Key == name); if (rep == null) return Results.NotFound(new { error = "not_found" });
            if (Read(http, out var f) is string bad) return MonitoringModule.Bad("bad_input", bad);
            await using var db = await MonitoringModule.Open(t, ct);
            var (total, rows) = await Run(db, rep, f, await Scope(http), 2000, ct);
            return Results.Ok(new { rep.Key, rep.Title, rep.About, columns = rep.Columns, total, shown = rows.Count, idKind = "device", rows });
        }).RequireAuthorization().RequireModule(key).RequireAccess(key, Acts.Read);

        r.MapGet("/api/monitoring/report/{name}.csv", async (string name, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var rep = All.FirstOrDefault(x => x.Key == name); if (rep == null) return Results.NotFound(new { error = "not_found" });
            if (Read(http, out var f) is string bad) return MonitoringModule.Bad("bad_input", bad);
            await using var db = await MonitoringModule.Open(t, ct);
            var (_, rows) = await Run(db, rep, f, await Scope(http), 50_000, ct);
            static string Cell(string v) { if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t' or '\r') v = "'" + v; return v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v; }
            var sb = new StringBuilder(string.Join(",", rep.Columns.Select(Cell))).Append("\r\n");
            foreach (var x in rows) sb.Append(string.Join(",", x.Skip(1).Select(Cell))).Append("\r\n");
            await audit.WriteAsync("monitoring.report_exported", rep.Key, rows.Count + " rows", ct: ct);
            return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv; charset=utf-8", rep.Key + "-report.csv");
        }).RequireAuthorization().RequireModule(key).RequireAccess(key, Acts.Read);
    }
}
