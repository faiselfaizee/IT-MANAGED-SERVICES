using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace DeskAsk.Platform.Assets;

/// <summary>
/// The Assets dashboard: one tile per customer, then a customer's detailed rings, then the filtered asset list.
/// The rings and the list filters use the same conditions (<see cref="Where"/>), so a ring part and the list it opens always agree.
/// Everything counts assets in service (not retired or lost). Needs @off (seconds after which an agent is offline) and @lv (latest agent version).
/// </summary>
public static class AssetDash
{
    const string Seen = "g.last_seen > now() - make_interval(secs => @off)";
    const string NonComp = "(coalesce(a.antivirus_status, '') not in ('', 'Protected') or coalesce(a.last_patch_date < current_date - 30, false))";
    const string Comp = "(a.antivirus_status = 'Protected' and a.last_patch_date >= current_date - 30)";

    /// <summary>What kind of device it is, from its type, how it is read and what it runs.</summary>
    public const string KindSql = @"(case
        when a.monitor = 'network' or a.hw_extra->>'platform' = 'network' or coalesce(t.name, '') ~* '(firewall|switch|router|access point|wireless|wlan)' then 'network'
        when coalesce(t.name, '') ~* 'print' then 'printer'
        when a.os_family in ('vmware','nutanix','proxmox','xen') or a.monitor in ('bmc','vmware','nutanix','proxmox','xen') or a.os_name ~* 'server' or coalesce(t.name, '') ~* 'server' then 'server'
        when a.os_family in ('windows','linux','macos','darwin') or coalesce(t.name, '') ~* '(laptop|desktop|computer|notebook|workstation|\mpc\M)' then 'computer'
        else 'other' end)";

    /// <summary>
    /// A power supply, drive, memory module, fan or other part reported as not OK by a controller, or a hypervisor host in red.
    /// Strict mode: in lax mode the array test unwraps the arrays first and never matches. VMs are left out (a VM warning is not a hardware fault).
    /// </summary>
    public const string HwBadSql = @"jsonb_path_exists(coalesce(a.hw_extra, '{}') || coalesce(a.bmc_extra, '{}'), 'strict $.keyvalue() ? (@.key != ""vms"" && @.value.type() == ""array"").value[*] ? (@.state == ""Critical"" || @.state == ""Warning"" || @.health == ""red"")')";

    /// <summary>The list filter for one ring part, or null when the name is not known.</summary>
    public static string? Where(string ring, string part) => (ring, part) switch
    {
        ("agent", "online") => $"g.id is not null and {Seen} and (@lv = '' or g.version = @lv)",
        ("agent", "update") => $"g.id is not null and {Seen} and @lv <> '' and g.version <> @lv",
        ("agent", "offline") => $"g.id is not null and (g.last_seen is null or not {Seen})",
        ("agent", "none") => "g.id is null",
        ("compliance", "compliant") => $"g.id is not null and {Comp}",
        ("compliance", "noncompliant") => $"g.id is not null and {NonComp}",
        ("compliance", "nodata") => $"g.id is not null and not {Comp} and not {NonComp}",
        ("warranty", "covered") => "a.warranty_end >= current_date + 30",
        ("warranty", "soon") => "a.warranty_end >= current_date and a.warranty_end < current_date + 30",
        ("warranty", "expired") => "a.warranty_end < current_date",
        ("warranty", "unknown") => "a.warranty_end is null",
        ("kind", "network" or "printer" or "server" or "computer" or "other") => $"{KindSql} = '{part}'",
        ("health", "bad") => HwBadSql,
        _ => null,
    };

    static readonly (string Key, string Ring, string Part)[] Counts =
    {
        ("agentOnline", "agent", "online"), ("agentUpdate", "agent", "update"), ("agentOffline", "agent", "offline"), ("agentNone", "agent", "none"),
        ("compliant", "compliance", "compliant"), ("nonCompliant", "compliance", "noncompliant"), ("noData", "compliance", "nodata"),
        ("warrantyCovered", "warranty", "covered"), ("warrantySoon", "warranty", "soon"), ("warrantyExpired", "warranty", "expired"), ("warrantyUnknown", "warranty", "unknown"),
        ("computers", "kind", "computer"), ("servers", "kind", "server"), ("network", "kind", "network"), ("printers", "kind", "printer"), ("other", "kind", "other"),
        ("hardwareNotOk", "health", "bad"),
    };

    /// <summary>The counts per customer (company null = assets with no customer), or for one customer, in a single query.</summary>
    public static async Task<List<Dictionary<string, object?>>> Metrics(NpgsqlConnection db, IReadOnlyList<int>? scope, string? company, int offlineSec, string latest, CancellationToken ct)
    {
        var cols = string.Join(",\n", Counts.Select(c => $"count(*) filter (where a.status not in ('retired','lost') and ({Where(c.Ring, c.Part)}))"));
        var companyWhere = company switch { null => "true", "none" => "a.company_id is null", _ => "a.company_id = @co" };
        await using var c = new NpgsqlCommand($@"select a.company_id, coalesce(max(co.name), ''), count(*), count(*) filter (where a.status not in ('retired','lost')),
            {cols},
            coalesce(sum((a.hw_extra->'port_summary'->>'total')::int) filter (where a.status not in ('retired','lost')), 0),
            coalesce(sum((a.hw_extra->'port_summary'->>'connected')::int) filter (where a.status not in ('retired','lost')), 0),
            coalesce(sum((a.hw_extra->'port_summary'->>'free')::int) filter (where a.status not in ('retired','lost')), 0),
            coalesce(sum((a.hw_extra->'port_summary'->>'disabled')::int) filter (where a.status not in ('retired','lost')), 0)
            from assets.assets a left join assets.types t on t.id = a.type_id left join assets.companies co on co.id = a.company_id
            left join assets.agents g on g.id = a.agent_id and g.deleted_at is null
            where (not @sc or a.company_id = any(@scope)) and {companyWhere}
            group by a.company_id order by 2", db);
        AssetsAccess.Bind(c, scope); AgentsApi.P(c, "off", (double)offlineSec); AgentsApi.P(c, "lv", latest);
        if (company is not (null or "none")) AgentsApi.P(c, "co", int.Parse(company));
        var rows = new List<Dictionary<string, object?>>();
        await using var rd = await c.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var m = new Dictionary<string, object?> { ["companyId"] = rd.IsDBNull(0) ? null : rd.GetInt32(0), ["company"] = rd.IsDBNull(0) ? "No customer" : rd.GetString(1), ["total"] = rd.GetInt64(2), ["inService"] = rd.GetInt64(3) };
            for (var i = 0; i < Counts.Length; i++) m[Counts[i].Key] = rd.GetInt64(4 + i);
            var p = 4 + Counts.Length;
            m["portsTotal"] = rd.GetInt64(p); m["portsUsed"] = rd.GetInt64(p + 1); m["portsFree"] = rd.GetInt64(p + 2); m["portsOff"] = rd.GetInt64(p + 3);
            rows.Add(m);
        }
        return rows;
    }

    /// <summary>All customers together: the sums of the per-customer rows.</summary>
    public static Dictionary<string, object?> Sum(IEnumerable<Dictionary<string, object?>> rows)
    {
        var all = new Dictionary<string, object?> { ["companyId"] = null, ["company"] = "All customers" };
        foreach (var r in rows) foreach (var (k, v) in r) if (v is long n) all[k] = (all.TryGetValue(k, out var o) && o is long x ? x : 0) + n;
        foreach (var k in Counts.Select(c => c.Key).Concat(new[] { "total", "inService", "portsTotal", "portsUsed", "portsFree", "portsOff" })) all.TryAdd(k, 0L);
        return all;
    }

    /// <summary>Computers reporting through an agent, with their last patch and antivirus: the patch and compliance report.</summary>
    static async Task<List<string[]>> Patches(NpgsqlConnection db, IReadOnlyList<int>? scope, string company, string show, CancellationToken ct)
    {
        var w = new List<string> { "a.status not in ('retired','lost')", "a.agent_id <> ''" };
        if (company == "none") w.Add("a.company_id is null"); else if (company != "") w.Add("a.company_id = @co");
        if (show == "behind") w.Add("(a.last_patch_date is null or a.last_patch_date < current_date - 30)");
        else if (show == "noncompliant") w.Add(NonComp);
        await using var c = new NpgsqlCommand($@"select a.id, a.tag, a.name, a.hostname, coalesce(co.name, ''), a.os_name, a.os_version,
            coalesce(to_char(a.last_patch_date, 'YYYY-MM-DD'), ''), coalesce((current_date - a.last_patch_date)::text, ''), coalesce(a.antivirus_status, ''),
            case when {Comp} then 'Compliant' when {NonComp} then 'Not compliant' else 'No data' end
            from assets.assets a left join assets.companies co on co.id = a.company_id
            where (not @sc or a.company_id = any(@scope)) and {string.Join(" and ", w)}
            order by a.last_patch_date nulls first, lower(a.name) limit 5000", db);
        AssetsAccess.Bind(c, scope);
        if (company is not ("" or "none")) AgentsApi.P(c, "co", int.Parse(company));
        var rows = new List<string[]>(); await using var rd = await c.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) rows.Add(Enumerable.Range(0, 11).Select(i => rd.IsDBNull(i) ? "" : Convert.ToString(rd.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? "").ToArray());
        return rows;
    }

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        static string? Check(HttpContext h, out string company, out string show)
        {
            company = h.Request.Query["company"].ToString(); show = h.Request.Query["show"].ToString();
            if (company != "" && company != "none" && !int.TryParse(company, out _)) return "company must be a customer id or none";
            if (show is not ("" or "all" or "behind" or "noncompliant")) return "show must be all, behind or noncompliant";
            return null;
        }
        r.MapGet("/api/assets/reports/patches", async (HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            if (Check(http, out var co, out var show) is string bad) return AgentsApi.Bad("bad_input", bad);
            await using var db = await AgentsApi.Open(t, ct);
            var rows = await Patches(db, await AssetsAccess.ScopeAsync(http, Acts.Read), co, show, ct);
            return Results.Ok(new
            {
                total = rows.Count, behind = rows.Count(x => x[8] == "" || int.Parse(x[8]) > 30), compliant = rows.Count(x => x[10] == "Compliant"), nonCompliant = rows.Count(x => x[10] == "Not compliant"),
                items = rows.Take(1000).Select(x => new { id = long.Parse(x[0]), tag = x[1], name = x[2], hostname = x[3], company = x[4], os = (x[5] + " " + x[6]).Trim(), lastPatch = x[7], days = x[8] == "" ? (int?)null : int.Parse(x[8]), antivirus = x[9], compliance = x[10] }),
            });
        }).RequireAuthorization().RequireModule(key).RequireAccess(key, Acts.Read);
        r.MapGet("/api/assets/reports/patches.csv", async (HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            if (Check(http, out var co, out var show) is string bad) return AgentsApi.Bad("bad_input", bad);
            await using var db = await AgentsApi.Open(t, ct);
            var rows = await Patches(db, await AssetsAccess.ScopeAsync(http, Acts.Read), co, show, ct);
            var sb = new System.Text.StringBuilder("Tag,Asset name,Host name,Customer,Operating system,Last patch,Days since,Antivirus,Compliance\r\n");
            foreach (var x in rows) sb.Append(string.Join(",", new[] { x[1], x[2], x[3], x[4], (x[5] + " " + x[6]).Trim(), x[7], x[8], x[9], x[10] }.Select(Csv.Cell))).Append("\r\n");
            await audit.WriteAsync("assets.report_exported", "patches", rows.Count + " computers", ct: ct);
            return Results.File(System.Text.Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8", "patch-report.csv");
        }).RequireAuthorization().RequireModule(key).RequireAccess(key, Acts.Read);

        // tiles: every customer this person may see, plus all together
        r.MapGet("/api/assets/dashboard", async (HttpContext http, ITenantContext t, IConfiguration cfg, CancellationToken ct) =>
        {
            var scope = await AssetsAccess.ScopeAsync(http, Acts.Read);
            await using var db = await AgentsApi.Open(t, ct);
            var company = http.Request.Query["company"].ToString();
            if (company != "" && company != "none" && !int.TryParse(company, out _)) return AgentsApi.Bad("bad_input", "company must be a customer id or none");
            var rows = await Metrics(db, scope, company == "" ? null : company, AgentsApi.Offline(cfg), AgentsApi.AgentVersion(cfg), ct);
            if (company != "") return rows.Count == 0 ? Results.NotFound() : Results.Ok(new { customer = rows[0] });
            return Results.Ok(new { all = Sum(rows), customers = rows, restricted = scope != null });
        }).RequireAuthorization().RequireModule(key).RequireAccess(key, Acts.Read);
    }
}
