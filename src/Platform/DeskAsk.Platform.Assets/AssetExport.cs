using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace DeskAsk.Platform.Assets;

/// <summary>
/// Everything we know about an asset in one place: identity, system, ownership, agent and remote access, storage, network, applications,
/// tickets, service checks and activity. The same document feeds the full CSV export (many assets, one flat file) and the PDF of one asset.
/// </summary>
public static class AssetExport
{
    /// <summary>One block of the document: either label/value facts or a table.</summary>
    public sealed record Section(string Name, List<(string Label, string Value)> Facts, string[]? Head = null, List<string[]>? Rows = null);
    public sealed record Full(long Id, string Tag, string Name, string Company, string Category, List<Section> Sections, Dictionary<string, int> Totals);

    // ------------------------------------------------------------------ small formatting helpers
    static readonly Dictionary<string, string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ip"] = "IP", ["ips"] = "IPs", ["cpu"] = "CPU", ["cpus"] = "CPUs", ["ram"] = "RAM", ["dns"] = "DNS", ["os"] = "OS", ["mac"] = "MAC", ["id"] = "ID", ["bios"] = "BIOS",
        ["amc"] = "AMC", ["url"] = "URL", ["gb"] = "GB", ["ssl"] = "SSL", ["tcp"] = "TCP", ["http"] = "HTTP", ["fs"] = "FS", ["dhcp"] = "DHCP", ["gpu"] = "GPU", ["psu"] = "PSU", ["ms"] = "ms",
    };

    /// <summary>"cpuMake" or "cpu_make" becomes "CPU make".</summary>
    public static string Nice(string key)
    {
        var k = key;
        foreach (var suf in new[] { "Bytes", "_bytes" }) if (k.EndsWith(suf, StringComparison.Ordinal) && k.Length > suf.Length) { k = k[..^suf.Length]; break; }
        var parts = new List<string>(); var cur = new StringBuilder();
        for (var i = 0; i < k.Length; i++)
        {
            var c = k[i];
            if (c is '_' or '-' or ' ') { if (cur.Length > 0) { parts.Add(cur.ToString()); cur.Clear(); } continue; }
            if (char.IsUpper(c) && cur.Length > 0 && char.IsLower(k[i - 1])) { parts.Add(cur.ToString()); cur.Clear(); }
            cur.Append(c);
        }
        if (cur.Length > 0) parts.Add(cur.ToString());
        var words = parts.Select((w, i) => Words.TryGetValue(w, out var up) ? up : (i == 0 ? char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant() : w.ToLowerInvariant()));
        return string.Join(" ", words);
    }

    public static string Size(long b)
    {
        if (b <= 0) return "";
        double v = b; string[] u = { "bytes", "KB", "MB", "GB", "TB", "PB" }; var i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? b.ToString(CultureInfo.InvariantCulture) + " bytes" : v.ToString(v >= 100 ? "0" : "0.##", CultureInfo.InvariantCulture) + " " + u[i];
    }

    /// <summary>Any JSON value as a short readable string.</summary>
    public static string Text(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String: return e.GetString() ?? "";
            case JsonValueKind.Number: return e.GetRawText();
            case JsonValueKind.True: return "Yes";
            case JsonValueKind.False: return "No";
            case JsonValueKind.Array: return string.Join("; ", e.EnumerateArray().Select(Text).Where(s => s != ""));
            case JsonValueKind.Object: return string.Join(", ", e.EnumerateObject().Select(p => Nice(p.Name) + ": " + Text(p.Value)).Where(s => !s.EndsWith(": ")));
            default: return "";
        }
    }

    static string Cell(string key, JsonElement v) =>
        (key.EndsWith("Bytes", StringComparison.Ordinal) || key.EndsWith("_bytes", StringComparison.Ordinal)) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var b) ? Size(b) : Text(v);

    /// <summary>An object becomes label/value facts; nested objects are folded in with a dotted label.</summary>
    static List<(string, string)> FactsOf(JsonElement e, string prefix = "")
    {
        var o = new List<(string, string)>();
        if (e.ValueKind != JsonValueKind.Object) return o;
        foreach (var p in e.EnumerateObject())
        {
            var label = (prefix == "" ? "" : prefix + " - ") + Nice(p.Name);
            if (p.Value.ValueKind == JsonValueKind.Object) o.AddRange(FactsOf(p.Value, label));
            else { var v = Cell(p.Name, p.Value); if (v != "") o.Add((label, v)); }
        }
        return o;
    }

    /// <summary>An array of objects becomes a table: the columns are the keys seen in the rows.</summary>
    static Section TableOf(string name, JsonElement e, int maxCols = 9)
    {
        if (e.ValueKind == JsonValueKind.Object) return new Section(name, FactsOf(e));
        var rows = new List<JsonElement>(); if (e.ValueKind == JsonValueKind.Array) rows.AddRange(e.EnumerateArray());
        if (rows.Count == 0 || rows.All(r => r.ValueKind != JsonValueKind.Object))
            return new Section(name, new(), new[] { "Value" }, rows.Select(r => new[] { Text(r) }).ToList());
        var keys = new List<string>();
        foreach (var r in rows.Take(200)) if (r.ValueKind == JsonValueKind.Object) foreach (var p in r.EnumerateObject()) if (!keys.Contains(p.Name)) keys.Add(p.Name);
        keys = keys.Take(maxCols).ToList();
        var head = keys.Select(Nice).ToArray();
        var data = rows.Select(r => keys.Select(k => r.ValueKind == JsonValueKind.Object && r.TryGetProperty(k, out var v) ? Cell(k, v) : "").ToArray()).ToList();
        return new Section(name, new(), head, data);
    }

    static Section FactsSection(string name, params (string, string)[] items) => new(name, items.Where(i => i.Item2 != "").ToList());
    static string Yes(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? (v.GetBoolean() ? "Yes" : "No") : "";
    static string Str(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) ? Cell(k, v) : "";
    static string Iso(object? o) => o switch { DateTime d => d.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture), DateTimeOffset d => d.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture), null => "", _ => o.ToString() ?? "" };

    /// <summary>The extra hardware tables a controller may send, in the order they are shown.</summary>
    public static readonly (string Key, string Title)[] HardwareTables =
    {
        ("ports", "Ports"), ("modules", "Hardware modules"), ("processors", "Processors"), ("memory_modules", "Memory modules"), ("storage_controllers", "Storage controllers"), ("drives", "Physical drives"),
        ("power_supplies", "Power supplies"), ("sensors", "Hardware sensors"), ("gpus", "Graphics cards"), ("hosts", "Hosts"), ("datastores", "Datastores"), ("vms", "Virtual machines"),
    };

    // ------------------------------------------------------------------ the document
    public static async Task<Full?> Build(NpgsqlConnection db, long id, int offlineSec, CancellationToken ct, int appLimit = 5000)
    {
        var dev = await AssetsDevice.DeviceDoc(db, id, offlineSec, ct);
        if (dev == null) return null;
        var d = dev.Value;
        var sections = new List<Section>(); var totals = new Dictionary<string, int>();

        // the base record: type, cost, custom fields, who created it
        string type = "", cost = "", source = "", created = "", updated = ""; JsonElement custom = default;
        await using (var c = new NpgsqlCommand("select t.name, a.purchase_cost, a.custom, a.source, a.created_at, a.created_by, a.updated_at, a.updated_by from assets.assets a left join assets.types t on t.id = a.type_id where a.id = @i", db))
        {
            AgentsApi.P(c, "i", id); await using var r = await c.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                type = r.IsDBNull(0) ? "" : r.GetString(0); cost = r.IsDBNull(1) ? "" : r.GetDecimal(1).ToString("0.00", CultureInfo.InvariantCulture);
                try { custom = JsonDocument.Parse(r.GetString(2)).RootElement.Clone(); } catch (JsonException) { }
                source = r.GetString(3); created = Iso(r.GetDateTime(4)) + (r.GetString(5) == "" ? "" : " by " + r.GetString(5)); updated = Iso(r.GetDateTime(6)) + (r.GetString(7) == "" ? "" : " by " + r.GetString(7));
            }
        }

        var idn = d.GetProperty("identity");
        var facts = new List<(string, string)> { ("Asset tag", Str(d, "tag")), ("Name", Str(d, "name")), ("Status", Str(d, "status")), ("Type", type), ("Category", Str(d, "category")), ("Company", Str(d, "company")) };
        facts.AddRange(FactsOf(idn));
        sections.Add(new Section("Identity", facts.Where(f => f.Item2 != "").ToList()));

        sections.Add(new Section("System", FactsOf(d.GetProperty("system"))));

        var own = FactsOf(d.GetProperty("ownership")); own.Insert(Math.Min(4, own.Count), ("Purchase cost", cost));
        own.AddRange(new[] { ("Record source", source), ("Created", created), ("Last changed", updated) });
        if (custom.ValueKind == JsonValueKind.Object) foreach (var f in FactsOf(custom, "Custom field")) own.Add(f);
        sections.Add(new Section("Ownership and contracts", own.Where(f => f.Item2 != "").ToList()));

        var ag = d.GetProperty("agent"); var rm = d.GetProperty("remote");
        sections.Add(FactsSection("Agent and remote access",
            ("Agent", Str(ag, "id") == "" ? "No agent installed" : Str(ag, "id")), ("Agent online", Str(ag, "id") == "" ? "" : Yes(ag, "online")), ("Last seen", Str(ag, "lastSeen")),
            ("Data source", Str(ag, "source")), ("Inventory taken", Str(ag, "inventoryAt")),
            ("Remote access allowed for this asset", Yes(rm, "assetAllows")), ("Remote access allowed for the company", Yes(rm, "companyAllows"))));

        sections.Add(TableOf("Storage", d.GetProperty("storage"))); totals["Storage"] = sections[^1].Rows!.Count;
        sections.Add(TableOf("Network adapters", d.GetProperty("network"))); totals["Network adapters"] = sections[^1].Rows!.Count;
        sections.Add(TableOf("Connected IP addresses", d.GetProperty("connectedIps"))); totals["Connected IP addresses"] = sections[^1].Rows!.Count;

        // hardware read by a controller: VMware hosts today, servers' management controllers and network gear later
        if (d.TryGetProperty("hardware", out var hw) && hw.ValueKind == JsonValueKind.Object)
        {
            foreach (var (key, title) in HardwareTables)
                if (hw.TryGetProperty(key, out var tbl) && tbl.ValueKind == JsonValueKind.Array && tbl.GetArrayLength() > 0)
                { sections.Add(TableOf(title, tbl)); totals[title] = sections[^1].Rows!.Count; }
        }

        // installed applications
        {
            var rows = new List<string[]>(); long n = 0;
            await using var c = new NpgsqlCommand(@"select e->>'name', coalesce(e->>'version',''), coalesce(e->>'publisher',''), coalesce(e->>'installed_on','')
                from assets.assets a cross join lateral jsonb_array_elements(a.installed_apps) e where a.id = @i order by lower(e->>'name')", db);
            AgentsApi.P(c, "i", id); await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { n++; if (rows.Count < appLimit) rows.Add(new[] { r.IsDBNull(0) ? "" : r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3) }); }
            sections.Add(new Section("Installed applications", new(), new[] { "Name", "Version", "Publisher", "Installed on" }, rows)); totals["Installed applications"] = (int)n;
        }

        // tickets
        {
            var rows = new List<string[]>();
            await using var c = new NpgsqlCommand("select ref, title, priority, status, source, note, created_at, created_by from assets.ticket_links where asset_id = @i order by id desc", db);
            AgentsApi.P(c, "i", id); await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add(new[] { r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), Iso(r.GetDateTime(6)), r.GetString(7) });
            sections.Add(new Section("Tickets", new(), new[] { "Reference", "Title", "Priority", "Status", "Source", "Note", "Created", "Created by" }, rows)); totals["Tickets"] = rows.Count;
        }

        // service checks
        {
            var rows = new List<string[]>();
            await using var c = new NpgsqlCommand("select name, type, host, port, url, enabled, last_at, last_ok, last_ms, last_detail from assets.service_checks where asset_id = @i order by lower(name)", db);
            AgentsApi.P(c, "i", id); await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add(new[] { r.GetString(0), r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2), r.IsDBNull(3) || r.GetInt32(3) == 0 ? "" : r.GetInt32(3).ToString(CultureInfo.InvariantCulture), r.IsDBNull(4) ? "" : r.GetString(4), r.GetBoolean(5) ? "On" : "Off",
                    r.IsDBNull(6) ? "" : Iso(r.GetDateTime(6)), r.IsDBNull(7) ? "" : (r.GetBoolean(7) ? "Up" : "Down"), r.IsDBNull(6) ? "" : Math.Round(Convert.ToDouble(r.GetValue(8))).ToString(CultureInfo.InvariantCulture) + " ms", r.IsDBNull(9) ? "" : r.GetString(9) });
            sections.Add(new Section("Service checks", new(), new[] { "Name", "Type", "Host", "Port", "URL", "State", "Last run", "Result", "Time", "Detail" }, rows)); totals["Service checks"] = rows.Count;
        }

        // activity: changes, commands, live sessions
        {
            var rows = new List<string[]>();
            await using var c = new NpgsqlCommand(@"select at, kind, who, text from (
                select at, action as kind, actor as who, changes as text from assets.history where asset_id = @i
                union all select coalesce(finished_at, created_at), 'command', by_name, title || ' [' || status || ']' from assets.jobs where asset_id = @i
                union all select created_at, 'shell', by_name, 'Live ' || shell || ' session' from assets.shells where asset_id = @i) x order by at desc limit 500", db);
            AgentsApi.P(c, "i", id); await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add(new[] { Iso(r.GetDateTime(0)), r.GetString(1), r.GetString(2), r.GetString(3) });
            sections.Add(new Section("Activity log", new(), new[] { "When", "Kind", "By", "Details" }, rows)); totals["Activity log"] = rows.Count;
        }

        return new Full(id, Str(d, "tag"), Str(d, "name"), Str(d, "company"), Str(d, "category"), sections, totals);
    }

    // ------------------------------------------------------------------ CSV: one flat file, filterable in Excel
    public const string CsvHead = "asset_tag,asset_name,company,section,row,field,value\r\n";

    public static void AppendCsv(StringBuilder sb, Full f)
    {
        void Line(string section, string row, string field, string value) =>
            sb.Append(string.Join(",", new[] { f.Tag, f.Name, f.Company, section, row, field, value }.Select(Csv.Cell))).Append("\r\n");
        foreach (var s in f.Sections)
        {
            foreach (var (label, value) in s.Facts) Line(s.Name, "", label, value);
            if (s.Head == null || s.Rows == null) continue;
            for (var i = 0; i < s.Rows.Count; i++)
                for (var k = 0; k < s.Head.Length; k++)
                    if (s.Rows[i][k] != "") Line(s.Name, (i + 1).ToString(CultureInfo.InvariantCulture), s.Head[k], s.Rows[i][k]);
        }
    }

    // ------------------------------------------------------------------ PDF: one asset as a document
    public static byte[] Pdf(Full f, string organisation = "")
    {
        var p = new PdfDoc();
        p.Heading(f.Name == "" ? f.Tag : f.Name, 20);
        p.Paragraph(string.Join("  -  ", new[] { f.Tag, f.Company, f.Category }.Where(s => s != "")), 11);
        p.Paragraph("Asset details report, created " + DateTime.UtcNow.ToString("d MMMM yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture) + (organisation == "" ? "" : " by " + organisation), 8.5);
        p.Gap(4);
        const int PdfRows = 300;
        foreach (var s in f.Sections)
        {
            p.Heading(s.Name, 12, true);
            if (s.Head == null)
            {
                if (s.Facts.Count == 0) p.Paragraph("Nothing recorded.", 9);
                else p.Table(new[] { "Field", "Value" }, new[] { 2.2, 5.0 }, s.Facts.Select(x => new[] { x.Label, x.Value }), 8.5);
                continue;
            }
            var rows = s.Rows!; var total = f.Totals.TryGetValue(s.Name, out var t) ? t : rows.Count;
            var shares = s.Head.Select(h => h is "Details" or "Note" or "Title" or "Name" or "Detail" ? 2.6 : h is "Value" ? 5.0 : 1.3).ToArray();
            p.Table(s.Head, shares, rows.Take(PdfRows), s.Head.Length > 6 ? 7 : 8);
            if (total > Math.Min(PdfRows, rows.Count) || rows.Count > PdfRows)
                p.Paragraph($"Showing the first {Math.Min(PdfRows, rows.Count)} of {total}. The CSV export lists all of them.", 8);
        }
        return p.ToBytes();
    }

    static string FileSafe(string s) { var o = new string(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray()); return o == "" ? "asset" : o; }

    // ------------------------------------------------------------------ routes
    public static void Map(IEndpointRouteBuilder r, string key)
    {
        RouteHandlerBuilder Scoped(RouteHandlerBuilder b) => b.RequireAuthorization().RequireModule(key).RequireAccess(key, Acts.Read);
        static int Offline(IConfiguration cfg) => int.TryParse(cfg["Assets:AgentOfflineSeconds"], out var s) && s > 0 ? s : 900;

        // one asset, as a PDF document
        Scoped(r.MapGet("/api/assets/{id:long}/export.pdf", async (HttpContext http, long id, ITenantContext t, IAuditLog audit, IConfiguration cfg, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            if (!await AssetsAccess.InScope(db, id, await AssetsAccess.ScopeAsync(http, Acts.Read), ct)) return Results.NotFound();
            var f = await Build(db, id, Offline(cfg), ct, 300);
            if (f == null) return Results.NotFound();
            await audit.WriteAsync("assets.exported_pdf", id.ToString(), f.Tag, ct: ct);
            return Results.File(Pdf(f), "application/pdf", FileSafe(f.Tag) + ".pdf");
        }));

        // one asset, every detail, as CSV
        Scoped(r.MapGet("/api/assets/{id:long}/export.csv", async (HttpContext http, long id, ITenantContext t, IAuditLog audit, IConfiguration cfg, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            if (!await AssetsAccess.InScope(db, id, await AssetsAccess.ScopeAsync(http, Acts.Read), ct)) return Results.NotFound();
            var f = await Build(db, id, Offline(cfg), ct);
            if (f == null) return Results.NotFound();
            var sb = new StringBuilder(CsvHead); AppendCsv(sb, f);
            await audit.WriteAsync("assets.exported_full", id.ToString(), f.Tag, ct: ct);
            return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8", FileSafe(f.Tag) + "-details.csv");
        }));

        // many assets, every detail, one flat CSV (optionally one company)
        Scoped(r.MapGet("/api/assets/export-full.csv", async (HttpContext http, ITenantContext t, IAuditLog audit, IConfiguration cfg, CancellationToken ct) =>
        {
            var scope = await AssetsAccess.ScopeAsync(http, Acts.Read);
            await using var db = await AgentsApi.Open(t, ct);
            var lf = AssetsModule.ListFilter(http.Request.Query, cfg); if (lf.Error != null) return AgentsApi.Bad("bad_input", lf.Error);
            var company = http.Request.Query["company"].ToString();
            var ids = new List<long>();
            await using (var c = new NpgsqlCommand($"select a.id from assets.assets a left join assets.agents g on g.id = a.agent_id and g.deleted_at is null where (not @sc or a.company_id = any(@scope)) and {lf.Sql} order by a.id limit 2000", db).Scoped(scope))
            {
                lf.Bind(c); await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) ids.Add(rd.GetInt64(0));
            }
            var sb = new StringBuilder(CsvHead); var off = Offline(cfg);
            foreach (var id in ids) { var f = await Build(db, id, off, ct); if (f != null) AppendCsv(sb, f); }
            await audit.WriteAsync("assets.exported_full", "all", ids.Count + " assets" + (company == "" ? "" : ", company " + company) + (http.Request.QueryString.HasValue ? ", filtered" : ""), ct: ct);
            return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8", "assets-full-details.csv");
        }));
    }
}
