using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace DeskAsk.Platform.Assets;

/// <summary>
/// Assets › Reports: every asset report in one place (inventory, hardware, disks, network, software, antivirus, contracts, licences, agents).
/// Each report is one query; the page shows the first 2,000 rows and the CSV holds them all (up to 50,000). Customer scope applies to every row.
/// Filters: company (id or "none"), q (words to find), status (in use, stock...). The patch report keeps its own page (/api/assets/reports/patches).
/// </summary>
public static class AssetReports
{
    public sealed record Report(string Key, string Group, string Title, string About, string[] Columns, string Sql, string Search, string Order);

    // "a" is assets.assets, "co" its company. {W} is replaced by the filters. Rows come back as text.
    const string From = "from assets.assets a left join assets.companies co on co.id = a.company_id left join assets.types ty on ty.id = a.type_id";
    static readonly string Gb = "round(({0})::numeric / 1073741824, 1)::text";
    static string GB(string col) => string.Format(CultureInfo.InvariantCulture, Gb, col);

    public static readonly Report[] All =
    {
        new("inventory", "Inventory", "Asset inventory", "Every asset with its customer, place, user and operating system.",
            new[] { "Tag", "Name", "Kind", "Status", "Customer", "Location", "Assigned to", "Manufacturer", "Model", "Serial", "Host name", "IP address", "Operating system", "Last seen" },
            $@"select a.id, a.tag, a.name, coalesce(ty.name, a.category), a.status, coalesce(co.name, ''), a.location, a.assigned_to, a.manufacturer, a.model, a.serial, a.hostname, a.ip_address,
                trim(a.os_name || ' ' || a.os_version), coalesce(to_char(a.last_seen at time zone 'UTC', 'YYYY-MM-DD HH24:MI'), '') {From} where {{W}}",
            "a.tag || ' ' || a.name || ' ' || a.hostname || ' ' || a.serial || ' ' || a.assigned_to || ' ' || a.ip_address || ' ' || a.model", "lower(a.name)"),
        new("hardware", "Inventory", "Hardware and firmware", "Make, model, processors, memory, storage and BIOS of computers and servers.",
            new[] { "Tag", "Name", "Customer", "Manufacturer", "Model", "Serial", "Processor", "Sockets", "Cores", "Logical processors", "Memory (GB)", "Storage (GB)", "BIOS" },
            $@"select a.id, a.tag, a.name, coalesce(co.name, ''), a.manufacturer, a.model, a.serial, trim(a.cpu_make || ' ' || a.cpu_model), a.total_cpu::text, a.cpu_cores::text, a.logical_processors::text,
                {GB("a.ram_bytes")}, {GB("a.total_storage_bytes")}, a.bios_version {From} where {{W}} and (a.ram_bytes > 0 or a.cpu_model <> '')",
            "a.tag || ' ' || a.name || ' ' || a.manufacturer || ' ' || a.model || ' ' || a.cpu_model || ' ' || a.bios_version", "lower(a.name)"),
        new("disks", "Inventory", "Disks and partitions", "Every partition with its size and free space; the fullest first.",
            new[] { "Tag", "Name", "Customer", "Disk", "Disk type", "Partition", "Mount", "File system", "Size (GB)", "Free (GB)", "Free %" },
            $@"select a.id, a.tag, a.name, coalesce(co.name, ''), d->>'name', coalesce(d->>'type', ''), p->>'name', coalesce(p->>'mount', ''), coalesce(p->>'fs', ''),
                {GB("coalesce((p->>'size_bytes')::bigint, 0)")}, {GB("coalesce((p->>'free_bytes')::bigint, 0)")},
                case when coalesce((p->>'size_bytes')::bigint, 0) > 0 then round(100.0 * coalesce((p->>'free_bytes')::bigint, 0) / (p->>'size_bytes')::bigint)::text else '' end
                {From} cross join lateral jsonb_array_elements(case when jsonb_typeof(a.disks) = 'array' then a.disks else '[]' end) d
                cross join lateral jsonb_array_elements(case when jsonb_typeof(d->'partitions') = 'array' then d->'partitions' else '[]' end) p where {{W}}",
            "a.tag || ' ' || a.name || ' ' || coalesce(p->>'mount', '') || ' ' || coalesce(d->>'name', '')",
            "case when coalesce((p->>'size_bytes')::bigint, 0) > 0 then coalesce((p->>'free_bytes')::bigint, 0)::float / (p->>'size_bytes')::bigint else 1 end, lower(a.name)"),
        new("network", "Inventory", "Network adapters", "Every network adapter with its MAC address, speed, state and IP addresses.",
            new[] { "Tag", "Name", "Customer", "Adapter", "Description", "MAC address", "Connected", "Speed (Mbps)", "IP addresses" },
            $@"select a.id, a.tag, a.name, coalesce(co.name, ''), n->>'name', coalesce(n->>'description', ''), coalesce(n->>'mac', ''),
                case when (n->>'connected')::boolean then 'Yes' else 'No' end, coalesce(n->>'speed_mbps', ''),
                coalesce((select string_agg(x, ', ') from jsonb_array_elements_text(case when jsonb_typeof(n->'ips') = 'array' then n->'ips' else '[]' end) x), '')
                {From} cross join lateral jsonb_array_elements(case when jsonb_typeof(a.nics) = 'array' then a.nics else '[]' end) n where {{W}}",
            "a.tag || ' ' || a.name || ' ' || coalesce(n->>'mac', '') || ' ' || coalesce(n->>'description', '') || ' ' || coalesce(n->'ips'::text, '')", "lower(a.name), n->>'name'"),
        new("software", "Software", "Installed software", "Every installed program on every computer. Search for a program to see where it is installed.",
            new[] { "Program", "Version", "Publisher", "Installed on", "Tag", "Name", "Customer", "Operating system" },
            $@"select a.id, s->>'name', coalesce(s->>'version', ''), coalesce(s->>'publisher', ''), coalesce(s->>'installed_on', ''), a.tag, a.name, coalesce(co.name, ''), trim(a.os_name || ' ' || a.os_version)
                {From} cross join lateral jsonb_array_elements(case when jsonb_typeof(a.installed_apps) = 'array' then a.installed_apps else '[]' end) s where {{W}} and coalesce(s->>'name', '') <> ''",
            "coalesce(s->>'name', '') || ' ' || coalesce(s->>'publisher', '') || ' ' || coalesce(s->>'version', '') || ' ' || a.name || ' ' || a.tag", "lower(s->>'name'), lower(a.name)"),
        new("antivirus", "Security", "Antivirus", "The antivirus each computer reports and whether it is on and up to date; unprotected first.",
            new[] { "Tag", "Name", "Customer", "Status", "Products", "Operating system", "Last seen" },
            $@"select a.id, a.tag, a.name, coalesce(co.name, ''), coalesce(nullif(a.antivirus_status, ''), 'No data'),
                coalesce((select string_agg((v->>'name') || case when (v->>'enabled')::boolean then '' else ' (off)' end, ', ') from jsonb_array_elements(case when jsonb_typeof(a.antivirus) = 'array' then a.antivirus else '[]' end) v), ''),
                trim(a.os_name || ' ' || a.os_version), coalesce(to_char(a.last_seen at time zone 'UTC', 'YYYY-MM-DD HH24:MI'), '') {From} where {{W}} and (a.agent_id <> '' or a.collector_id <> '' or a.antivirus <> '[]')",
            "a.tag || ' ' || a.name || ' ' || a.antivirus::text", "case when a.antivirus_status = 'Protected' then 1 else 0 end, lower(a.name)"),
        new("contracts", "Contracts", "Purchase, warranty and AMC", "Purchase date and cost, warranty end and the AMC each asset is in; warranties ending soonest first.",
            new[] { "Tag", "Name", "Customer", "Manufacturer", "Model", "Serial", "Purchased", "Cost", "Warranty ends", "Warranty", "AMC", "AMC ends" },
            $@"select a.id, a.tag, a.name, coalesce(co.name, ''), a.manufacturer, a.model, a.serial, coalesce(to_char(a.purchase_date, 'YYYY-MM-DD'), ''), coalesce(a.purchase_cost::text, ''),
                coalesce(to_char(a.warranty_end, 'YYYY-MM-DD'), ''),
                case when a.warranty_end is null then 'Not recorded' when a.warranty_end < current_date then 'Expired' when a.warranty_end < current_date + 30 then 'Ends within 30 days' else 'Covered' end,
                coalesce(m.title, ''), coalesce(to_char(m.end_date, 'YYYY-MM-DD'), '')
                {From} left join lateral (select x.title, x.end_date from assets.amc x where x.asset_ids @> to_jsonb(a.id) order by x.end_date desc nulls last limit 1) m on true where {{W}}",
            "a.tag || ' ' || a.name || ' ' || a.serial || ' ' || a.model || ' ' || coalesce(m.title, '')", "a.warranty_end nulls last, lower(a.name)"),
        new("licences", "Contracts", "Licences and subscriptions", "Every licence with its seats, cost and end date; ending soonest first.",
            new[] { "Product", "Publisher", "Edition", "Customer", "Kind", "Counted by", "Seats", "Cost", "Billing", "Starts", "Ends", "Auto renew" },
            @"select l.id, l.product, l.publisher, l.edition, coalesce(co.name, ''), l.kind, l.model, l.seats::text, trim(coalesce(l.cost::text, '') || ' ' || l.currency), l.billing,
                coalesce(to_char(l.start_date, 'YYYY-MM-DD'), ''), coalesce(to_char(l.end_date, 'YYYY-MM-DD'), ''), case when l.auto_renew then 'Yes' else 'No' end
                from assets.licenses l left join assets.companies co on co.id = l.company_id where {L}",
            "l.product || ' ' || l.publisher || ' ' || l.edition || ' ' || l.contract_no", "l.end_date nulls last, lower(l.product)"),
        new("agents", "Agents", "Agent health", "Every computer with an agent: version, last contact and whether it still reports; silent ones first.",
            new[] { "Tag", "Name", "Customer", "Host name", "Platform", "Agent version", "Last contact", "State", "IP address" },
            $@"select a.id, a.tag, a.name, coalesce(co.name, ''), g.hostname, g.platform, g.version, coalesce(to_char(g.last_seen at time zone 'UTC', 'YYYY-MM-DD HH24:MI'), ''),
                case when g.last_seen is null then 'Never reported' when g.last_seen < now() - interval '15 minutes' then 'Not reporting' else 'Live' end, g.ip
                {From} join assets.agents g on g.asset_id = a.id where {{W}}",
            "a.tag || ' ' || a.name || ' ' || g.hostname || ' ' || g.version || ' ' || g.ip", "g.last_seen nulls first, lower(a.name)"),
    };

    static readonly string[] Statuses = { "stock", "in_use", "repair", "retired", "lost" };

    sealed record Filter(string Company, string Q, string Status);

    static string? Read(HttpContext h, out Filter f)
    {
        var q = h.Request.Query;
        f = new Filter(q["company"].ToString(), q["q"].ToString().Trim(), q["status"].ToString());
        if (f.Company is not ("" or "none") && !int.TryParse(f.Company, out _)) return "company must be a customer id or none";
        if (f.Status != "" && f.Status != "active" && !Statuses.Contains(f.Status)) return "status must be active, stock, in_use, repair, retired or lost";
        if (f.Q.Length > 100) return "the search is longer than 100 characters";
        return null;
    }

    /// <summary>Runs one report. Returns the total number of rows and the first <paramref name="max"/>; the first column (the record id) is kept for links.</summary>
    static async Task<(int Total, List<string[]> Rows)> Run(NpgsqlConnection db, Report r, Filter f, IReadOnlyList<int>? scope, int max, CancellationToken ct)
    {
        string sql;
        if (r.Key == "licences")
        {
            var w = new List<string> { "(not @sc or l.company_id = any(@scope))" };
            if (f.Company == "none") w.Add("false"); else if (f.Company != "") w.Add("l.company_id = @co");
            if (f.Q != "") w.Add("(" + r.Search + ") ilike @q");
            sql = r.Sql.Replace("{L}", string.Join(" and ", w));
        }
        else
        {
            var w = new List<string> { "(not @sc or a.company_id = any(@scope))" };
            if (f.Status == "" || f.Status == "active") w.Add("a.status not in ('retired', 'lost')"); else w.Add("a.status = @st");
            if (f.Company == "none") w.Add("a.company_id is null"); else if (f.Company != "") w.Add("a.company_id = @co");
            if (f.Q != "") w.Add("(" + r.Search + ") ilike @q");
            sql = r.Sql.Replace("{W}", string.Join(" and ", w));
        }
        void Bind(NpgsqlCommand c)
        {
            AssetsAccess.Bind(c, scope);
            if (f.Company is not ("" or "none")) c.Parameters.AddWithValue("co", int.Parse(f.Company));
            if (f.Status is not ("" or "active")) c.Parameters.AddWithValue("st", f.Status);
            if (f.Q != "") c.Parameters.AddWithValue("q", "%" + f.Q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        }
        int total;
        await using (var cnt = new NpgsqlCommand($"select count(*) from ({sql}) x", db)) { Bind(cnt); total = Convert.ToInt32(await cnt.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture); }
        await using var c = new NpgsqlCommand($"{sql} order by {r.Order} limit @max", db);
        Bind(c);
        c.Parameters.AddWithValue("max", max);
        var rows = new List<string[]>();
        await using var rd = await c.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            rows.Add(Enumerable.Range(0, rd.FieldCount).Select(i => rd.IsDBNull(i) ? "" : Convert.ToString(rd.GetValue(i), CultureInfo.InvariantCulture) ?? "").ToArray());
        return (total, rows);
    }

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        r.MapGet("/api/assets/report-catalog", () => Results.Ok(All.Select(x => new { x.Key, x.Group, x.Title, x.About })))
            .RequireAuthorization().RequireModule(key).RequireAccess(key, Acts.Read);

        r.MapGet("/api/assets/report/{name}", async (string name, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            var rep = All.FirstOrDefault(x => x.Key == name); if (rep == null) return Results.NotFound(new { error = "not_found" });
            if (Read(http, out var f) is string bad) return AgentsApi.Bad("bad_input", bad);
            await using var db = await AgentsApi.Open(t, ct);
            var (total, rows) = await Run(db, rep, f, await AssetsAccess.ScopeAsync(http, Acts.Read), 2000, ct);
            return Results.Ok(new { rep.Key, rep.Title, rep.About, columns = rep.Columns, total, shown = rows.Count, idKind = rep.Key == "licences" ? "licence" : "asset", rows });
        }).RequireAuthorization().RequireModule(key).RequireAccess(key, Acts.Read);

        r.MapGet("/api/assets/report/{name}.csv", async (string name, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var rep = All.FirstOrDefault(x => x.Key == name); if (rep == null) return Results.NotFound(new { error = "not_found" });
            if (Read(http, out var f) is string bad) return AgentsApi.Bad("bad_input", bad);
            await using var db = await AgentsApi.Open(t, ct);
            var (_, rows) = await Run(db, rep, f, await AssetsAccess.ScopeAsync(http, Acts.Read), 50_000, ct);
            var sb = new StringBuilder(string.Join(",", rep.Columns.Select(Csv.Cell))).Append("\r\n");
            foreach (var x in rows) sb.Append(string.Join(",", x.Skip(1).Select(Csv.Cell))).Append("\r\n");
            await audit.WriteAsync("assets.report_exported", rep.Key, rows.Count + " rows", ct: ct);
            return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv; charset=utf-8", rep.Key + "-report.csv");
        }).RequireAuthorization().RequireModule(key).RequireAccess(key, Acts.Read);
    }
}
