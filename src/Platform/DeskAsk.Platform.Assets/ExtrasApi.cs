using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace DeskAsk.Platform.Assets;

/// <summary>
/// The rest of the asset page: service checks, ticket links (the Service Desk module is not built yet, so a ticket here is a link record), QR labels,
/// handover and allocation, and the combined log of an asset.
/// </summary>
public static class ExtrasApi
{
    static IResult Bad(string error, string? detail = null) => AgentsApi.Bad(error, detail);
    static readonly string[] CheckTypes = { "tcp", "http", "ssl", "dns" };
    static readonly string[] Conditions = { "good", "fair", "damaged", "lost" };
    static readonly string[] Priorities = { "low", "normal", "high", "urgent" };
    static readonly string[] TicketStates = { "open", "in_progress", "resolved", "closed" };
    public static readonly string[] QrModes = { "details", "full", "link" };

    static string Esc(string? s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    static string Gb(long b) => b > 0 ? Math.Round(b / 1073741824.0, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture) + " GB" : "";
    static string Inv(double d) => d.ToString("0.00", CultureInfo.InvariantCulture);

    sealed record QrAsset(long Id, string Tag, string Name, string Company, string Make, string Model, string Serial, string Ip, string Warranty, bool Amc, string Category, string Location, string Host, string Mac, string Os, string Purchased, string Cpu, long Ram, long Storage, string Notes);

    static async Task<QrAsset?> LoadQr(NpgsqlConnection db, long id, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand(@"select a.id, a.tag, a.name, coalesce(co.name,''), a.manufacturer, a.model, a.serial, a.ip_address, coalesce(to_char(a.warranty_end,'YYYY-MM-DD'),''),
            exists (select 1 from assets.amc m where m.asset_ids @> to_jsonb(a.id) and (m.end_date is null or m.end_date >= current_date)),
            a.category, a.location, a.hostname, coalesce(a.mac_addresses->>0,''), trim(a.os_name || ' ' || a.os_version), coalesce(to_char(a.purchase_date,'YYYY-MM-DD'),''),
            a.cpu_model, a.ram_bytes, a.total_storage_bytes, a.notes from assets.assets a left join assets.companies co on co.id = a.company_id where a.id = @i", db);
        AgentsApi.P(c, "i", id); await using var r = await c.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new QrAsset(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8), r.GetBoolean(9), r.GetString(10), r.GetString(11), r.GetString(12), r.GetString(13), r.GetString(14), r.GetString(15), r.GetString(16), r.GetInt64(17), r.GetInt64(18), r.GetString(19));
    }
    static string T(string? v, int n) { var s = Regex.Replace(v ?? "", @"\s+", " ").Trim(); return s.Length > n ? s[..(n - 1)] + "…" : s; }
    static string J(params string?[] p) => string.Join(" ", p.Where(x => !string.IsNullOrEmpty(x)));

    static string QrPayload(QrAsset a, string mode, string link)
    {
        if (mode == "link") return link;
        bool full = mode == "full"; int n = full ? 120 : 36, nn = full ? 120 : 28;
        var rows = new (string K, string V)[]
        {
            ("ID", a.Tag), ("Name", T(a.Name, nn)), ("Customer", T(a.Company, full ? 120 : 24)), ("Make/Model", T(J(a.Make, a.Model), n)), ("Serial", T(a.Serial, 40)),
            ("IP", a.Ip), ("Warranty", a.Warranty), ("AMC", a.Amc ? "Yes" : ""), ("Type", T(a.Category, n)), ("Location", T(a.Location, n)),
            ("Host", T(a.Host, 40)), ("MAC", a.Mac), ("OS", T(a.Os, n)), ("Purchased", a.Purchased),
            ("CPU", full ? T(a.Cpu, 60) : ""), ("RAM", full ? Gb(a.Ram) : ""), ("Storage", full ? Gb(a.Storage) : ""), ("Notes", full ? T(a.Notes, 160) : "")
        };
        var text = "DeskAsk asset"; int budget = full ? 620 : 230;
        foreach (var (k, v) in rows)
        {
            if (v == "") continue;
            var line = "\n" + k + ": " + v;
            if (Encoding.UTF8.GetByteCount(text + line) > budget) { if (k is "ID" or "Name") text += line; continue; }
            text += line;
        }
        return text;
    }
    static QrCode MakeQr(string text, string layout, string mode) => Qr.Encode(text, layout == "label" || mode == "link" ? 'M' : 'L');
    static string ModeOf(string? m) => m != null && QrModes.Contains(m) ? m : "details";

    static string PrintPage(List<(QrAsset A, QrCode Qr)> items, string layout, int size, bool showText)
    {
        int worst = items.Max(i => i.Qr.Size), quiet = 2; double cellQr = layout == "label" ? size : 17.5;
        double modMm = cellQr / (worst + quiet * 2); bool ok = modMm >= 0.3, mid = modMm >= 0.22;
        string Cell((QrAsset A, QrCode Qr) i) => "<div class=\"cell\"><div class=\"qr\">" + Qr.Svg(i.Qr, quiet) + "</div>" + (showText ? "<div class=\"id\">" + Esc(i.A.Tag) + "</div>" : "") + "</div>";
        var body = new StringBuilder();
        if (layout == "sheet") for (int p = 0; p < items.Count; p += 100) body.Append("<section class=\"page\">").Append(string.Concat(items.Skip(p).Take(100).Select(Cell))).Append("</section>");
        else foreach (var i in items) body.Append("<section class=\"lbl\">").Append(Cell(i)).Append("</section>");
        int lw = size + 4, lh = size + (showText ? 9 : 4);
        var css = layout == "sheet"
            ? "@page{size:A4 portrait;margin:8mm}.page{width:194mm;height:281mm;display:grid;grid-template-columns:repeat(10,1fr);grid-template-rows:repeat(10,1fr);break-after:page;page-break-after:always}.cell{display:flex;flex-direction:column;align-items:center;justify-content:center;overflow:hidden}.qr{width:17.5mm;height:17.5mm}.id{font:6pt Arial,sans-serif;margin-top:.5mm}"
            : "@page{size:" + lw + "mm " + lh + "mm;margin:0}.lbl{width:" + lw + "mm;height:" + lh + "mm;display:flex;align-items:center;justify-content:center;break-after:page;page-break-after:always}.cell{display:flex;flex-direction:column;align-items:center}.qr{width:" + size + "mm;height:" + size + "mm}.id{font:7pt Arial,sans-serif;margin-top:1mm}";
        var msg = ok ? "Scan size is comfortable (" + Inv(modMm) + " mm per dot)." : (mid ? "Dots are small (" + Inv(modMm) + " mm). It scans with a good phone camera and a 600 dpi printer; use the compact content or a larger label for harder conditions." : "Dots are too small (" + Inv(modMm) + " mm) to scan reliably. Use the compact content, the link, or a larger label.");
        int pages = (int)Math.Ceiling(items.Count / 100.0);
        return "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>QR codes - assets</title><style>"
            + "body{margin:0;font-family:Arial,sans-serif;background:#eef3f3}.bar{position:sticky;top:0;background:#fff;border-bottom:1px solid #cfdcdc;padding:10px 16px;display:flex;gap:14px;align-items:center}.sheet{padding:12px}"
            + (layout == "sheet" ? ".page{background:#fff;box-shadow:0 1px 6px #0003;box-sizing:border-box}" : ".lbl{background:#fff;box-shadow:0 1px 6px #0003;margin:6px}")
            + "@media print{body{background:#fff}.bar{display:none}.sheet{padding:0}.page,.lbl{box-shadow:none;margin:0}}" + css + "</style></head><body>"
            + "<div class=\"bar\"><b>" + items.Count + " QR code" + (items.Count == 1 ? "" : "s") + "</b><span>" + (layout == "sheet" ? "A4, 10 x 10 per page, " + pages + " page" + (pages == 1 ? "" : "s") : size + " mm labels") + "</span><span>" + Esc(msg) + "</span><button onclick=\"window.print()\">Print</button></div>"
            + "<div class=\"sheet\">" + body + "</div></body></html>";
    }

    static string? Txt(JsonElement b, string n, int max) { var s = AgentsApi.Str(b, n); return s == null ? null : AgentsApi.Clip(s.Trim(), max); }
    static List<string> Acc(JsonElement b) => b.ValueKind == JsonValueKind.Object && b.TryGetProperty("accessories", out var a) && a.ValueKind == JsonValueKind.Array
        ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => AgentsApi.Clip(x.GetString()!.Trim(), 80)).Where(x => x != "").Take(30).ToList() : new List<string>();

    static object CheckRow(NpgsqlDataReader r) => new
    {
        id = r.GetInt32(0), name = r.GetString(1), type = r.GetString(2), host = r.GetString(3), port = r.GetInt32(4), url = r.GetString(5), expect = r.GetString(6), status = r.GetString(7),
        insecure = r.GetBoolean(8), dns = r.GetString(9), timeoutS = r.GetInt32(10), enabled = r.GetBoolean(11),
        lastAt = r.IsDBNull(12) ? (DateTime?)null : r.GetDateTime(12), lastOk = r.IsDBNull(13) ? (bool?)null : r.GetBoolean(13), lastMs = (double)r.GetFloat(14), lastDays = r.IsDBNull(15) ? (double?)null : r.GetFloat(15), lastDetail = r.GetString(16)
    };
    const string CheckCols = "id, name, type, host, port, url, expect, status, insecure, dns, timeout_s, enabled, last_at, last_ok, last_ms, last_days, last_detail";

    static async Task<IResult> SaveCheck(long assetId, int? checkId, JsonElement b, ITenantContext t, IAuditLog audit, CancellationToken ct)
    {
        var type = AgentsApi.Str(b, "type") ?? ""; if (!CheckTypes.Contains(type)) return Bad("bad_input", "Choose the kind of check: tcp, http, ssl or dns.");
        var name = AgentsApi.Clip(AgentsApi.Str(b, "name"), 60).Trim(); if (name == "") return Bad("bad_input", "Give the check a name, for example \"Web site\" or \"RDP\".");
        int port = b.TryGetProperty("port", out var pe) && pe.ValueKind == JsonValueKind.Number && pe.TryGetInt32(out var pv) ? pv : 0;
        var url = AgentsApi.Clip(AgentsApi.Str(b, "url"), 500).Trim(); var host = AgentsApi.Clip(AgentsApi.Str(b, "host"), 255).Trim();
        int timeout = b.TryGetProperty("timeoutS", out var te) && te.ValueKind == JsonValueKind.Number && te.TryGetInt32(out var tv) ? Math.Clamp(tv, 2, 60) : 10;
        var status = Regex.IsMatch((AgentsApi.Str(b, "status") ?? "").Trim(), @"^\d{3}$") ? AgentsApi.Str(b, "status")!.Trim() : "";
        if (type is "tcp" or "ssl") { if (port < 1 || port > 65535) return Bad("bad_input", "Enter the port number (1 to 65535)."); url = ""; }
        if (type == "http") { if (!Regex.IsMatch(url, @"^https?://[^\s]+$", RegexOptions.IgnoreCase)) return Bad("bad_input", "Enter the web address, starting with http:// or https://."); port = 0; host = ""; }
        if (type == "dns") { if (host == "") return Bad("bad_input", "Enter the name to look up, for example www.example.com."); port = 0; url = ""; }
        bool insecure = b.TryGetProperty("insecure", out var ie) && ie.ValueKind == JsonValueKind.True;
        bool enabled = !(b.TryGetProperty("enabled", out var ee) && ee.ValueKind == JsonValueKind.False);
        await using var db = await AgentsApi.Open(t, ct);
        await using (var q = new NpgsqlCommand("select 1 from assets.assets where id = @i", db)) { AgentsApi.P(q, "i", assetId); if (await q.ExecuteScalarAsync(ct) == null) return Results.NotFound(); }
        try
        {
            if (checkId == null)
            {
                await using var cnt = new NpgsqlCommand("select count(*) from assets.service_checks where asset_id = @i", db); AgentsApi.P(cnt, "i", assetId);
                if ((long)(await cnt.ExecuteScalarAsync(ct))! >= 40) return Bad("too_many", "A device can have at most 40 checks.");
            }
            await using var c = new NpgsqlCommand(checkId == null
                ? "insert into assets.service_checks (asset_id, name, type, host, port, url, expect, status, insecure, dns, timeout_s, enabled) values (@a,@n,@t,@h,@p,@u,@x,@s,@in,@d,@to,@en) returning " + CheckCols
                : "update assets.service_checks set name=@n, type=@t, host=@h, port=@p, url=@u, expect=@x, status=@s, insecure=@in, dns=@d, timeout_s=@to, enabled=@en where id=@ci and asset_id=@a returning " + CheckCols, db);
            AgentsApi.P(c, "a", assetId); AgentsApi.P(c, "n", name); AgentsApi.P(c, "t", type); AgentsApi.P(c, "h", host); AgentsApi.P(c, "p", port); AgentsApi.P(c, "u", url);
            AgentsApi.P(c, "x", AgentsApi.Clip(AgentsApi.Str(b, "expect"), 300)); AgentsApi.P(c, "s", status); AgentsApi.P(c, "in", insecure); AgentsApi.P(c, "d", AgentsApi.Clip(AgentsApi.Str(b, "dns"), 255).Trim()); AgentsApi.P(c, "to", timeout); AgentsApi.P(c, "en", enabled); AgentsApi.P(c, "ci", checkId);
            await using var rd = await c.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return Results.NotFound();
            var row = CheckRow(rd); rd.Close();
            await audit.WriteAsync("assets.check_saved", assetId.ToString(), name + " (" + type + ")", ct: ct);
            return Results.Ok(row);
        }
        catch (PostgresException e) when (e.SqlState == "23505") { return Bad("duplicate", "This device already has a check with that name."); }
    }

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        RouteHandlerBuilder Admin(RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole("admin")).RequireModule(key);
        RouteHandlerBuilder Anyone(RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole(AssetsAccess.Readers)).RequireModule(key);

        // ================================================================ service checks
        Anyone(r.MapGet("/api/assets/{id:long}/checks", async (long id, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct); bool collector;
            await using (var q = new NpgsqlCommand("select collector_id <> '' and monitor <> '' from assets.assets where id = @i", db)) { AgentsApi.P(q, "i", id); var v = await q.ExecuteScalarAsync(ct); if (v == null) return Results.NotFound(); collector = (bool)v; }
            var o = new List<object>();
            await using var c = new NpgsqlCommand("select " + CheckCols + " from assets.service_checks where asset_id = @i order by lower(name)", db); AgentsApi.P(c, "i", id);
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) o.Add(CheckRow(rd));
            return Results.Ok(new { items = o, collector });
        }));
        Admin(r.MapPost("/api/assets/{id:long}/checks", (long id, JsonElement body, ITenantContext t, IAuditLog audit, CancellationToken ct) => SaveCheck(id, null, body, t, audit, ct)));
        Admin(r.MapPut("/api/assets/{id:long}/checks/{cid:int}", (long id, int cid, JsonElement body, ITenantContext t, IAuditLog audit, CancellationToken ct) => SaveCheck(id, cid, body, t, audit, ct)));
        Admin(r.MapDelete("/api/assets/checks/{cid:int}", async (int cid, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            await using var c = new NpgsqlCommand("delete from assets.service_checks where id = @i returning asset_id, name", db); AgentsApi.P(c, "i", cid);
            await using var rd = await c.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return Results.NotFound();
            var detail = rd.GetInt64(0) + ": " + rd.GetString(1); rd.Close();
            await audit.WriteAsync("assets.check_deleted", cid.ToString(), detail, ct: ct);
            return Results.NoContent();
        }));
        // the history Monitoring will read once it exists
        Anyone(r.MapGet("/api/assets/checks/{cid:int}/results", async (int cid, int? take, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct); var o = new List<object>();
            await using var c = new NpgsqlCommand("select at, ok, ms, days from assets.check_results where check_id = @i order by id desc limit @t", db); AgentsApi.P(c, "i", cid); AgentsApi.P(c, "t", Math.Clamp(take ?? 100, 1, 1000));
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) o.Add(new { at = rd.GetDateTime(0), ok = rd.GetBoolean(1), ms = (double)rd.GetFloat(2), days = rd.IsDBNull(3) ? (double?)null : rd.GetFloat(3) });
            return Results.Ok(new { items = o });
        }));

        // ================================================================ ticket links (stub until the Service Desk module exists)
        Anyone(r.MapGet("/api/assets/{id:long}/tickets", async (long id, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            await using (var q = new NpgsqlCommand("select 1 from assets.assets where id = @i", db)) { AgentsApi.P(q, "i", id); if (await q.ExecuteScalarAsync(ct) == null) return Results.NotFound(); }
            var o = new List<object>(); int open = 0;
            await using var c = new NpgsqlCommand("select id, ref, title, priority, status, source, note, created_at, created_by from assets.ticket_links where asset_id = @i order by id desc", db); AgentsApi.P(c, "i", id);
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) { var st = rd.GetString(4); if (st is "open" or "in_progress") open++; o.Add(new { id = rd.GetInt64(0), @ref = rd.GetString(1), title = rd.GetString(2), priority = rd.GetString(3), status = st, source = rd.GetString(5), note = rd.GetString(6), createdAt = rd.GetDateTime(7), createdBy = rd.GetString(8) }); }
            return Results.Ok(new { items = o, openCount = open, serviceDesk = false });
        }));
        Admin(r.MapPost("/api/assets/{id:long}/tickets", async (long id, JsonElement body, ClaimsPrincipal u, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var title = Txt(body, "title", 200) ?? ""; if (title == "") return Bad("bad_input", "Give the ticket a title.");
            var pri = AgentsApi.Str(body, "priority") ?? "normal"; if (!Priorities.Contains(pri)) return Bad("bad_input", "priority must be one of " + string.Join(", ", Priorities));
            await using var db = await AgentsApi.Open(t, ct);
            await using var tx = await db.BeginTransactionAsync(ct);
            string tag;
            await using (var q = new NpgsqlCommand("select tag from assets.assets where id = @i", db, tx)) { AgentsApi.P(q, "i", id); var v = await q.ExecuteScalarAsync(ct); if (v == null) return Results.NotFound(); tag = (string)v; }
            long lid;
            await using (var c = new NpgsqlCommand("insert into assets.ticket_links (asset_id, title, priority, note, created_by) values (@a,@t,@p,@n,@b) returning id", db, tx))
            { AgentsApi.P(c, "a", id); AgentsApi.P(c, "t", title); AgentsApi.P(c, "p", pri); AgentsApi.P(c, "n", Txt(body, "note", 2000) ?? ""); AgentsApi.P(c, "b", AgentsApi.Actor(u)); lid = (long)(await c.ExecuteScalarAsync(ct))!; }
            var reference = "TKT-" + lid.ToString("D5");
            await using (var c = new NpgsqlCommand("update assets.ticket_links set ref = @r where id = @i", db, tx)) { AgentsApi.P(c, "r", reference); AgentsApi.P(c, "i", lid); await c.ExecuteNonQueryAsync(ct); }
            await AssetsDevice.History(db, id, AgentsApi.Actor(u), "ticket", JsonSerializer.Serialize(new { @ref = reference, title }), ct, tx);
            await tx.CommitAsync(ct);
            await audit.WriteAsync("assets.ticket_raised", reference, tag + ": " + title, ct: ct);
            return Results.Ok(new { id = lid, @ref = reference, stub = true });
        }));
        Admin(r.MapPatch("/api/assets/tickets/{lid:long}", async (long lid, JsonElement body, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var st = AgentsApi.Str(body, "status"); if (st != null && !TicketStates.Contains(st)) return Bad("bad_input", "status must be one of " + string.Join(", ", TicketStates));
            await using var db = await AgentsApi.Open(t, ct);
            await using var c = new NpgsqlCommand("update assets.ticket_links set status = coalesce(@s, status), note = coalesce(@n, note) where id = @i", db);
            AgentsApi.P(c, "s", st); AgentsApi.P(c, "n", Txt(body, "note", 2000)); AgentsApi.P(c, "i", lid);
            if (await c.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            await audit.WriteAsync("assets.ticket_changed", lid.ToString(), st ?? "", ct: ct);
            return Results.Ok(new { ok = true });
        }));
        Admin(r.MapDelete("/api/assets/tickets/{lid:long}", async (long lid, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            await using var c = new NpgsqlCommand("delete from assets.ticket_links where id = @i", db); AgentsApi.P(c, "i", lid);
            if (await c.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            await audit.WriteAsync("assets.ticket_removed", lid.ToString(), "", ct: ct);
            return Results.NoContent();
        }));

        // ================================================================ QR labels
        Anyone(r.MapGet("/api/assets/qr/{id:long}/svg", async (long id, string? content, HttpContext http, ITenantContext t, IConfiguration cfg, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct); var a = await LoadQr(db, id, ct); if (a == null) return Results.NotFound();
            var mode = ModeOf(content); var text = QrPayload(a, mode, AgentsApi.PublicUrl(http, cfg) + "/assets/" + a.Id);
            return Results.Text(Qr.Svg(MakeQr(text, "sheet", mode), 2), "image/svg+xml");
        }));
        Anyone(r.MapGet("/api/assets/qr/{id:long}/info", async (long id, string? content, HttpContext http, ITenantContext t, IConfiguration cfg, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct); var a = await LoadQr(db, id, ct); if (a == null) return Results.NotFound();
            var mode = ModeOf(content); var text = QrPayload(a, mode, AgentsApi.PublicUrl(http, cfg) + "/assets/" + a.Id);
            return Results.Ok(new { id = a.Id, tag = a.Tag, mode, text, bytes = Encoding.UTF8.GetByteCount(text), modes = QrModes });
        }));
        Anyone(r.MapGet("/api/assets/qr/print", async (string? ids, string? content, string? layout, int? size, string? text, HttpContext http, ITenantContext t, IConfiguration cfg, CancellationToken ct) =>
        {
            var list = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => long.TryParse(x, out var l) ? l : 0).Where(x => x > 0).Distinct().Take(2000).ToList();
            if (list.Count == 0) return Bad("bad_input", "Choose the assets to print.");
            var mode = ModeOf(content); var lay = layout == "label" ? "label" : "sheet"; var sz = size is 25 or 35 or 50 ? size.Value : 35; var url = AgentsApi.PublicUrl(http, cfg);
            await using var db = await AgentsApi.Open(t, ct); var items = new List<(QrAsset, QrCode)>();
            foreach (var id in list) { var a = await LoadQr(db, id, ct); if (a == null) continue; items.Add((a, MakeQr(QrPayload(a, mode, url + "/assets/" + a.Id), lay, mode))); }
            if (items.Count == 0) return Bad("bad_input", "There are no assets to print.");
            return Results.Text(PrintPage(items, lay, sz, text != "0"), "text/html; charset=utf-8");
        }));

        // ================================================================ handover and allocation
        Anyone(r.MapGet("/api/assets/{id:long}/handovers", async (long id, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct); var o = new List<object>();
            await using var c = new NpgsqlCommand("select id, kind, holder, at, by_name, cond, accessories::text, notes from assets.handovers where asset_id = @i order by id desc", db); AgentsApi.P(c, "i", id);
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) o.Add(new { id = rd.GetInt64(0), kind = rd.GetString(1), holder = rd.GetString(2), at = rd.GetDateTime(3), by = rd.GetString(4), condition = rd.GetString(5), accessories = JsonDocument.Parse(rd.GetString(6)).RootElement.Clone(), notes = rd.GetString(7) });
            return Results.Ok(new { items = o });
        }));
        Admin(r.MapPost("/api/assets/{id:long}/handover", async (long id, JsonElement body, ClaimsPrincipal u, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var cond = (AgentsApi.Str(body, "condition") ?? "").Trim().ToLowerInvariant(); if (!Conditions.Contains(cond)) return Bad("bad_input", "Choose the condition of the asset: good, fair, damaged or lost.");
            var acc = Acc(body); var notes = Txt(body, "notes", 1000) ?? "";
            await using var db = await AgentsApi.Open(t, ct); await using var tx = await db.BeginTransactionAsync(ct);
            string holder;
            await using (var q = new NpgsqlCommand("select assigned_to from assets.assets where id = @i for update", db, tx)) { AgentsApi.P(q, "i", id); var v = await q.ExecuteScalarAsync(ct); if (v == null) return Results.NotFound(); holder = (string)v; }
            if (holder == "") return Bad("already_unallocated", "This asset is already unallocated.");
            await using (var c = new NpgsqlCommand("insert into assets.handovers (asset_id, kind, holder, by_name, cond, accessories, notes) values (@a,'handover',@h,@b,@c,@x,@n)", db, tx))
            { AgentsApi.P(c, "a", id); AgentsApi.P(c, "h", holder); AgentsApi.P(c, "b", AgentsApi.Actor(u)); AgentsApi.P(c, "c", cond); c.Parameters.Add(new NpgsqlParameter("x", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(acc) }); AgentsApi.P(c, "n", notes); await c.ExecuteNonQueryAsync(ct); }
            await using (var c = new NpgsqlCommand("update assets.assets set assigned_to = '', prev_holder = @h, unalloc_at = now(), status = case when status = 'in_use' then 'stock' when @c = 'lost' then 'lost' else status end, updated_at = now(), updated_by = @b where id = @i", db, tx))
            { AgentsApi.P(c, "h", holder); AgentsApi.P(c, "c", cond); AgentsApi.P(c, "b", AgentsApi.Actor(u)); AgentsApi.P(c, "i", id); await c.ExecuteNonQueryAsync(ct); }
            await AssetsDevice.History(db, id, AgentsApi.Actor(u), "handover", JsonSerializer.Serialize(new { from = holder, condition = cond, notes }), ct, tx);
            await tx.CommitAsync(ct);
            await audit.WriteAsync("assets.handover", id.ToString(), holder + " (" + cond + ")", ct: ct);
            return Results.Ok(new { id, from = holder, condition = cond });
        }));
        Admin(r.MapPost("/api/assets/{id:long}/allocate", async (long id, JsonElement body, ClaimsPrincipal u, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var to = Txt(body, "to", 120) ?? ""; if (to == "") return Bad("bad_input", "Enter who receives the asset.");
            var cond = (AgentsApi.Str(body, "condition") ?? "good").Trim().ToLowerInvariant(); if (!Conditions.Contains(cond)) return Bad("bad_input", "Choose the condition of the asset.");
            var acc = Acc(body); var notes = Txt(body, "notes", 1000) ?? "";
            await using var db = await AgentsApi.Open(t, ct); await using var tx = await db.BeginTransactionAsync(ct);
            string holder;
            await using (var q = new NpgsqlCommand("select assigned_to from assets.assets where id = @i for update", db, tx)) { AgentsApi.P(q, "i", id); var v = await q.ExecuteScalarAsync(ct); if (v == null) return Results.NotFound(); holder = (string)v; }
            if (holder != "") return Bad("allocated", "This asset is allocated to " + holder + ". Hand it over first.");
            await using (var c = new NpgsqlCommand("insert into assets.handovers (asset_id, kind, holder, by_name, cond, accessories, notes) values (@a,'assign',@h,@b,@c,@x,@n)", db, tx))
            { AgentsApi.P(c, "a", id); AgentsApi.P(c, "h", to); AgentsApi.P(c, "b", AgentsApi.Actor(u)); AgentsApi.P(c, "c", cond); c.Parameters.Add(new NpgsqlParameter("x", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(acc) }); AgentsApi.P(c, "n", notes); await c.ExecuteNonQueryAsync(ct); }
            await using (var c = new NpgsqlCommand("update assets.assets set assigned_to = @h, unalloc_at = null, status = case when status = 'stock' then 'in_use' else status end, updated_at = now(), updated_by = @b where id = @i", db, tx))
            { AgentsApi.P(c, "h", to); AgentsApi.P(c, "b", AgentsApi.Actor(u)); AgentsApi.P(c, "i", id); await c.ExecuteNonQueryAsync(ct); }
            await AssetsDevice.History(db, id, AgentsApi.Actor(u), "handover", JsonSerializer.Serialize(new { to, condition = cond, notes }), ct, tx);
            await tx.CommitAsync(ct);
            await audit.WriteAsync("assets.allocated", id.ToString(), to, ct: ct);
            return Results.Ok(new { id, to });
        }));

        // ================================================================ the combined log
        Anyone(r.MapGet("/api/assets/{id:long}/logs", async (long id, string? kind, int? take, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            await using (var q = new NpgsqlCommand("select 1 from assets.assets where id = @i", db)) { AgentsApi.P(q, "i", id); if (await q.ExecuteScalarAsync(ct) == null) return Results.NotFound(); }
            var o = new List<object>(); var kinds = new SortedSet<string>();
            await using var c = new NpgsqlCommand(@"select at, kind, who, text from (
                select at, action as kind, actor as who, changes as text from assets.history where asset_id = @i
                union all select coalesce(finished_at, created_at), 'command', by_name, title || ' [' || status || ']' from assets.jobs where asset_id = @i
                union all select created_at, 'shell', by_name, 'Live ' || shell || ' session' from assets.shells where asset_id = @i) x
                where (@k = '' or kind = @k) order by at desc limit @t", db);
            AgentsApi.P(c, "i", id); AgentsApi.P(c, "k", kind ?? ""); AgentsApi.P(c, "t", Math.Clamp(take ?? 200, 1, 1000));
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) { kinds.Add(rd.GetString(1)); o.Add(new { at = rd.GetDateTime(0), kind = rd.GetString(1), by = rd.GetString(2), text = rd.GetString(3) }); }
            return Results.Ok(new { items = o, kinds });
        }));
    }
}
