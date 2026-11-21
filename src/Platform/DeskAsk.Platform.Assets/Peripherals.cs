using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using NpgsqlTypes;

namespace DeskAsk.Platform.Assets;

/// <summary>
/// Monitors, keyboards, mice, docking stations, headsets, phones, tablets and other things that are handed to a person and never reached remotely.
/// Each is an asset of its own with its own serial number, and it can be attached to the main device (the desktop or laptop it came with), because
/// support finds a keyboard through the serial number of its desktop. Also: what a person holds, handing several things over or back at once,
/// adding several of one kind at once, and how many of each kind are in stock.
/// </summary>
public static class PeripheralsApi
{
    static async Task<NpgsqlConnection> Open(ITenantContext t, CancellationToken ct) { var db = new NpgsqlConnection(t.Require().ConnectionString); await db.OpenAsync(ct); return db; }
    static void P(NpgsqlCommand c, string n, object? v) => c.Parameters.AddWithValue(n, v ?? DBNull.Value);
    static void PT(NpgsqlCommand c, string n, object? v, NpgsqlDbType type) => c.Parameters.Add(new NpgsqlParameter(n, type) { Value = v ?? DBNull.Value });
    static IResult Bad(string detail) => Results.BadRequest(new { error = "bad_input", detail });
    static string Date(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetFieldValue<DateOnly>(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    static string Actor(ClaimsPrincipal u) => u.FindFirstValue("email") ?? u.FindFirstValue("sub") ?? "unknown";
    static readonly string[] Conditions = { "good", "fair", "damaged", "lost" };

    // ------------------------------------------------------------------ the starter types
    static object F(string key, string label, string kind = "text", params string[] options) =>
        kind == "choice" ? new { key, label, kind, options } : new { key, label, kind, options = (string[]?)null };

    static IEnumerable<(string Name, object[] Fields)> Starter()
    {
        var link = F("connection", "Connection", "choice", "Wired", "Wireless", "Bluetooth");
        yield return ("Monitor", new[] { F("size_inch", "Screen size (inches)", "number"), F("resolution", "Resolution"), F("connectors", "Connectors") });
        yield return ("Keyboard", new[] { F("layout", "Layout"), link });
        yield return ("Mouse", new[] { link });
        yield return ("Docking station", new[] { F("ports", "Ports"), F("power_w", "Power (W)", "number") });
        yield return ("Headset", new[] { link });
        yield return ("Webcam", new[] { F("resolution", "Resolution") });
        var phone = new[] { F("imei", "IMEI"), F("imei2", "IMEI 2"), F("phone_number", "Phone number"), F("os", "Operating system", "choice", "Android", "iOS", "Other"),
            F("storage_gb", "Storage (GB)", "number"), F("sim_number", "SIM number"), F("carrier", "Carrier"), F("managed", "Managed by MDM", "bool") };
        yield return ("Mobile phone", phone);
        yield return ("Tablet", phone);
        yield return ("SIM card", new[] { F("sim_number", "SIM number"), F("phone_number", "Phone number"), F("carrier", "Carrier"), F("plan", "Plan") });
        yield return ("Other device", new[] { F("details", "Details") });
    }

    /// <summary>Migration step: adds the starter types, flags them (and any type of the same name) as things that are not reached remotely, and leaves them unmonitored.</summary>
    public static async Task SeedTypesAsync(NpgsqlConnection db, NpgsqlTransaction tx, CancellationToken ct)
    {
        var names = new List<string>();
        foreach (var (name, fields) in Starter())
        {
            names.Add(name.ToLowerInvariant());
            await using var c = new NpgsqlCommand("insert into assets.types (name, fields, peripheral) values (@n, @f, true) on conflict do nothing", db, tx);
            P(c, "n", name); P(c, "f", JsonSerializer.Serialize(fields)); await c.ExecuteNonQueryAsync(ct);
        }
        await using (var u = new NpgsqlCommand("update assets.types set peripheral = true where lower(name) = any(@n)", db, tx)) { P(u, "n", names.ToArray()); await u.ExecuteNonQueryAsync(ct); }
        await using (var m = new NpgsqlCommand("update assets.assets set monitored = false where agent_id = '' and type_id in (select id from assets.types where peripheral)", db, tx)) await m.ExecuteNonQueryAsync(ct);
    }

    // ------------------------------------------------------------------ attaching
    /// <summary>Why an asset cannot be attached to this main device (null when it can) and the company of the main device.</summary>
    internal static async Task<(string? Error, int? Company)> CheckParent(NpgsqlConnection db, long? self, long parentId, int? company, IReadOnlyList<int>? scope, CancellationToken ct)
    {
        if (self == parentId) return ("An asset cannot be attached to itself.", null);
        int? pco; long? ppar;
        await using (var c = new NpgsqlCommand("select company_id, parent_id from assets.assets where id = @p and (not @sc or company_id = any(@scope))", db))
        {
            P(c, "p", parentId); AssetsAccess.Bind(c, scope);
            await using var rd = await c.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return ("The main device does not exist.", null);
            pco = rd.IsDBNull(0) ? null : rd.GetInt32(0); ppar = rd.IsDBNull(1) ? null : rd.GetInt64(1);
        }
        if (ppar != null) return ("Attach it to the main device, not to another attached item.", null);
        if (company != null && pco != company) return ("The main device belongs to another customer.", null);
        if (self != null)
        {
            await using var k = new NpgsqlCommand("select exists (select 1 from assets.assets where parent_id = @i)", db); P(k, "i", self);
            if ((bool)(await k.ExecuteScalarAsync(ct))!) return ("This asset has items attached to it, so it cannot be attached to another one.", null);
        }
        return (null, pco);
    }

    /// <summary>What a new accessory takes over from the main device when it has no value of its own: purchase and warranty, vendor, site, room and holder.</summary>
    internal static async Task InheritAsync(NpgsqlConnection db, long id, long parentId, string actor, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand(@"update assets.assets c set
              purchase_date = coalesce(c.purchase_date, p.purchase_date), warranty_end = coalesce(c.warranty_end, p.warranty_end), warranty_months = coalesce(c.warranty_months, p.warranty_months),
              vendor_id = coalesce(c.vendor_id, p.vendor_id), site_id = coalesce(c.site_id, p.site_id),
              po_number = case when c.po_number = '' then p.po_number else c.po_number end, invoice_number = case when c.invoice_number = '' then p.invoice_number else c.invoice_number end,
              location = case when c.location = '' then p.location else c.location end, assigned_to = case when c.assigned_to = '' then p.assigned_to else c.assigned_to end,
              status = case when c.status = 'stock' and c.assigned_to = '' and p.assigned_to <> '' then 'in_use' else c.status end, updated_at = now(), updated_by = @by
            from assets.assets p where c.id = @i and p.id = @p", db);
        P(c, "i", id); P(c, "p", parentId); P(c, "by", actor); await c.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Attaches an asset to a main device (or detaches it with null) and writes it to the asset's history.</summary>
    internal static async Task AttachAsync(NpgsqlConnection db, long id, long? parentId, bool inherit, string actor, CancellationToken ct)
    {
        await using (var c = new NpgsqlCommand("update assets.assets set parent_id = @p, updated_at = now(), updated_by = @by where id = @i", db))
        { PT(c, "p", parentId, NpgsqlDbType.Bigint); P(c, "by", actor); P(c, "i", id); await c.ExecuteNonQueryAsync(ct); }
        string label = "";
        if (parentId != null)
        {
            await using var q = new NpgsqlCommand("select tag || ' ' || name from assets.assets where id = @p", db); P(q, "p", parentId); label = (await q.ExecuteScalarAsync(ct)) as string ?? "";
            if (inherit) await InheritAsync(db, id, parentId.Value, actor, ct);
        }
        await AssetsDevice.History(db, id, actor, parentId == null ? "detached" : "attached", JsonSerializer.Serialize(new { to = label }), ct);
    }

    /// <summary>A thing that is not reached remotely is not watched by monitoring.</summary>
    internal static async Task QuietAsync(NpgsqlConnection db, long id, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("update assets.assets a set monitored = false where a.id = @i and a.agent_id = '' and exists (select 1 from assets.types t where t.id = a.type_id and t.peripheral)", db);
        P(c, "i", id); await c.ExecuteNonQueryAsync(ct);
    }

    static async Task<long> Active(NpgsqlConnection db, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("select count(*) filter (where status not in ('retired','lost')) from assets.assets", db);
        return (long)(await c.ExecuteScalarAsync(ct))!;
    }

    static string? Text(JsonElement b, string name, int max)
    {
        if (b.ValueKind != JsonValueKind.Object || !b.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString()!.Trim();
        return s.Length > max ? s[..max] : s;
    }
    static int? Int(JsonElement b, string name) => b.ValueKind == JsonValueKind.Object && b.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    static long? Long(JsonElement b, string name) => b.ValueKind == JsonValueKind.Object && b.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : null;

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        var readers = (RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole(AssetsAccess.Readers)).RequireModule(key).RequireAccess(key, Acts.Read);
        var admin = (RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole("admin")).RequireModule(key);

        // ---- the main device an asset is attached to, and what is attached to it
        readers(r.MapGet("/api/assets/{id:long}/attached", async (long id, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            var scope = await AssetsAccess.ScopeAsync(http, Acts.Read);
            await using var db = await Open(t, ct);
            if (!await AssetsAccess.InScope(db, id, scope, ct)) return Results.NotFound();
            object? parent = null; bool peripheral = false;
            await using (var c = new NpgsqlCommand(@"select p.id, p.tag, p.name, p.serial, coalesce(pt.name, ''), p.assigned_to, coalesce(t.peripheral, false)
                from assets.assets a left join assets.types t on t.id = a.type_id left join assets.assets p on p.id = a.parent_id left join assets.types pt on pt.id = p.type_id where a.id = @i", db))
            {
                P(c, "i", id); await using var rd = await c.ExecuteReaderAsync(ct);
                if (await rd.ReadAsync(ct)) { peripheral = rd.GetBoolean(6); if (!rd.IsDBNull(0)) parent = new { id = rd.GetInt64(0), tag = rd.GetString(1), name = rd.GetString(2), serial = rd.GetString(3), type = rd.GetString(4), assignedTo = rd.GetString(5) }; }
            }
            var kids = new List<object>();
            await using (var c = new NpgsqlCommand(@"select a.id, a.tag, a.name, coalesce(t.name, ''), a.serial, a.status, a.warranty_end, a.assigned_to, a.manufacturer, a.model
                from assets.assets a left join assets.types t on t.id = a.type_id where a.parent_id = @i and (not @sc or a.company_id = any(@scope)) order by coalesce(t.name, ''), a.id", db))
            {
                P(c, "i", id); AssetsAccess.Bind(c, scope); await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) kids.Add(new { id = rd.GetInt64(0), tag = rd.GetString(1), name = rd.GetString(2), type = rd.GetString(3), serial = rd.GetString(4), status = rd.GetString(5), warrantyEnd = Date(rd, 6), assignedTo = rd.GetString(7), manufacturer = rd.GetString(8), model = rd.GetString(9) });
            }
            return Results.Ok(new { parent, items = kids, peripheral });
        }));

        r.MapPut("/api/assets/{id:long}/attach", async (long id, JsonElement b, HttpContext http, ClaimsPrincipal u, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            if (b.ValueKind != JsonValueKind.Object || !b.TryGetProperty("parentId", out var pv)) return Bad("parentId is needed (a number, or null to detach).");
            long? parentId = null;
            if (pv.ValueKind != JsonValueKind.Null) { if (pv.ValueKind != JsonValueKind.Number || !pv.TryGetInt64(out var pl)) return Bad("parentId must be a number or null"); parentId = pl; }
            var inherit = !(b.TryGetProperty("inherit", out var iv) && iv.ValueKind == JsonValueKind.False);
            await using var db = await Open(t, ct);
            var scope = await AssetsAccess.ScopeAsync(http, Acts.Modify);
            if (!await AssetsAccess.InScope(db, id, scope, ct)) return Results.NotFound();
            int? company;
            await using (var c = new NpgsqlCommand("select company_id from assets.assets where id = @i", db)) { P(c, "i", id); var o = await c.ExecuteScalarAsync(ct); if (o == null) return Results.NotFound(); company = o is int ci ? ci : null; }
            if (parentId != null)
            {
                var (err, _) = await CheckParent(db, id, parentId.Value, company, scope, ct);
                if (err != null) return Bad(err);
            }
            await AttachAsync(db, id, parentId, inherit, Actor(u), ct);
            await audit.WriteAsync(parentId == null ? "assets.detached" : "assets.attached", id.ToString(), parentId?.ToString() ?? "", ct: ct);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization().RequireModule(key).RequireAccess(key, Acts.Modify);

        // ---- several of one kind at once (ten identical mice, a box of monitors with their serial numbers)
        r.MapPost("/api/assets/bulk-add", async (JsonElement b, HttpContext http, ClaimsPrincipal u, ITenantContext t, IEntitlements ent, IAuditLog audit, CancellationToken ct) =>
        {
            var scope = await AssetsAccess.ScopeAsync(http, Acts.Add);
            if (scope != null && scope.Count != 1) return Results.Json(new { error = "forbidden", detail = "You may add assets for one company only; ask an administrator." }, statusCode: 403);
            if (b.ValueKind != JsonValueKind.Object) return Bad("the body must be a JSON object");
            var typeId = Int(b, "typeId"); if (typeId == null) return Bad("Choose the kind of asset.");
            var items = new List<(string Serial, string Name)>();
            if (b.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var it in arr.EnumerateArray()) items.Add((Text(it, "serial", 200) ?? "", Text(it, "name", 200) ?? ""));
            else if (Int(b, "quantity") is int qn) for (var i = 0; i < qn && i < 500; i++) items.Add(("", ""));
            if (items.Count == 0) return Bad("Give the serial numbers, or how many to add.");
            if (items.Count > 200) return Bad("At most 200 at a time.");
            await using var db = await Open(t, ct);
            string typeName; bool peripheral;
            await using (var c = new NpgsqlCommand("select name, peripheral from assets.types where id = @i", db)) { P(c, "i", typeId); await using var rd = await c.ExecuteReaderAsync(ct); if (!await rd.ReadAsync(ct)) return Bad("That kind of asset does not exist."); typeName = rd.GetString(0); peripheral = rd.GetBoolean(1); }
            int? company = scope != null ? scope[0] : Int(b, "companyId");
            long? parentId = Long(b, "parentId");
            if (parentId != null)
            {
                var (perr, pco) = await CheckParent(db, null, parentId.Value, company, await AssetsAccess.ScopeAsync(http, Acts.Read), ct);
                if (perr != null) return Bad(perr);
                company ??= pco;
            }
            if (company != null)
            {
                await using var q = new NpgsqlCommand("select 1 from assets.companies where id = @i", db); P(q, "i", company);
                if (await q.ExecuteScalarAsync(ct) == null) return Bad("companyId does not exist");
            }
            var siteId = Int(b, "siteId"); var vendorId = Int(b, "vendorId");
            if (siteId != null || vendorId != null) { var refErr = await LocationsApi.CheckRefs(db, company, siteId, vendorId, await AssetsAccess.ScopeAsync(http, Acts.Read), ct); if (refErr != null) return Bad(refErr); }
            DateOnly? pd = null, we = null;
            var pds = Text(b, "purchaseDate", 10); if (!string.IsNullOrEmpty(pds)) { if (!DateOnly.TryParseExact(pds, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d1)) return Bad("purchaseDate must be a date like 2026-11-09"); pd = d1; }
            var wes = Text(b, "warrantyEnd", 10); if (!string.IsNullOrEmpty(wes)) { if (!DateOnly.TryParseExact(wes, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2)) return Bad("warrantyEnd must be a date like 2026-11-09"); we = d2; }
            var months = Int(b, "warrantyMonths"); if (months is < 0 or > 240) return Bad("warrantyMonths must be 0 to 240");
            if (we == null && pd != null && months != null) we = pd.Value.AddMonths(months.Value).AddDays(-1);
            var holder = Text(b, "assignedTo", 200) ?? ""; var baseName = Text(b, "name", 200); if (string.IsNullOrEmpty(baseName)) baseName = typeName;
            var make = Text(b, "manufacturer", 200) ?? ""; var model = Text(b, "model", 200) ?? ""; var po = Text(b, "poNumber", 60) ?? ""; var inv = Text(b, "invoiceNumber", 60) ?? ""; var room = Text(b, "location", 200) ?? "";
            var status = holder != "" ? "in_use" : "stock";

            var active = await Active(db, ct); var created = new List<object>(); var skipped = new List<object>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var n = 0;
            foreach (var (rawSerial, itemName) in items)
            {
                n++;
                var serial = DuplicateCheck.IsPlaceholder(rawSerial) ? "" : rawSerial;
                if (serial != "" && !seen.Add(serial)) { skipped.Add(new { serial, name = itemName, reason = "The serial number is in the list twice." }); continue; }
                if (!await ent.WithinLimitAsync(key, "max_assets", active + created.Count, ct)) { skipped.Add(new { serial, name = itemName, reason = "The licensed number of assets is reached." }); continue; }
                var name = itemName != "" ? itemName : serial != "" ? baseName + " " + serial : items.Count > 1 ? baseName + " " + n : baseName;
                await using var tx = await db.BeginTransactionAsync(ct);
                try
                {
                    await using var ins = new NpgsqlCommand(@"insert into assets.assets (name, type_id, status, manufacturer, model, serial, assigned_to, purchase_date, warranty_end, warranty_months, po_number, invoice_number,
                          site_id, vendor_id, company_id, parent_id, monitored, location, source, created_by, updated_by)
                        values (@n, @t, @st, @mf, @md, @sn, @as, @pd, @we, @wm, @po, @inv, @si, @ve, @co, @pa, @mo, @lo, 'manual', @by, @by) returning id, tag", db, tx);
                    P(ins, "n", name); P(ins, "t", typeId); P(ins, "st", status); P(ins, "mf", make); P(ins, "md", model); P(ins, "sn", serial); P(ins, "as", holder); P(ins, "po", po); P(ins, "inv", inv); P(ins, "lo", room); P(ins, "by", Actor(u));
                    PT(ins, "pd", pd, NpgsqlDbType.Date); PT(ins, "we", we, NpgsqlDbType.Date); PT(ins, "wm", months, NpgsqlDbType.Integer); PT(ins, "si", siteId, NpgsqlDbType.Integer);
                    PT(ins, "ve", vendorId, NpgsqlDbType.Integer); PT(ins, "co", company, NpgsqlDbType.Integer); PT(ins, "pa", parentId, NpgsqlDbType.Bigint); PT(ins, "mo", !peripheral, NpgsqlDbType.Boolean);
                    long id; string tag;
                    await using (var rd = await ins.ExecuteReaderAsync(ct)) { await rd.ReadAsync(ct); (id, tag) = (rd.GetInt64(0), rd.GetString(1)); }
                    await using (var h = new NpgsqlCommand("insert into assets.history (asset_id, actor, action, changes) values (@a, @b, 'created', '{}')", db, tx)) { P(h, "a", id); P(h, "b", Actor(u)); await h.ExecuteNonQueryAsync(ct); }
                    await tx.CommitAsync(ct);
                    if (parentId != null) await InheritAsync(db, id, parentId.Value, Actor(u), ct);
                    created.Add(new { id, tag, name, serial });
                }
                catch (PostgresException e) when (e.SqlState == "23505")
                {
                    await tx.RollbackAsync(ct);
                    skipped.Add(new { serial, name, reason = "The serial number is already used by another asset." });
                }
            }
            if (created.Count > 0) await audit.WriteAsync("assets.bulk_added", typeName, created.Count + " added", ct: ct);
            return Results.Ok(new { created, skipped });
        }).RequireAuthorization().RequireModule(key).RequireAccess(key, Acts.Add);

        // ---- the people read from a customer's domain controller: the People page and the drop-down when handing something out
        readers(r.MapGet("/api/assets/directory-people", async (string? search, int? companyId, bool? all, int? limit, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            var scope = await AssetsAccess.ScopeAsync(http, Acts.Read);
            await using var db = await Open(t, ct);
            await using var c = new NpgsqlCommand(@"select p.company_id, coalesce(co.name, ''), p.account, p.name, p.email, p.department, p.title, p.enabled
                from assets.directory_people p left join assets.companies co on co.id = p.company_id
                where (not @sc or p.company_id = any(@scope)) and (@co = 0 or p.company_id = @co) and (@all or p.enabled)
                  and (@q = '' or p.name ilike @l or p.email ilike @l or p.account ilike @l)
                order by lower(p.name), p.company_id limit @lim", db);
            AssetsAccess.Bind(c, scope); var q = (search ?? "").Trim(); P(c, "q", q); P(c, "l", "%" + q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
            P(c, "co", companyId ?? 0); P(c, "all", all == true); P(c, "lim", Math.Clamp(limit ?? 200, 1, 1000));
            var rows = new List<object>(); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) rows.Add(new { companyId = rd.GetInt32(0), company = rd.GetString(1), account = rd.GetString(2), name = rd.GetString(3), email = rd.GetString(4), department = rd.GetString(5), title = rd.GetString(6), enabled = rd.GetBoolean(7) });
            return Results.Ok(new { items = rows });
        }));

        // ---- people and what they hold
        readers(r.MapGet("/api/assets/holders", async (string? search, int? companyId, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            var scope = await AssetsAccess.ScopeAsync(http, Acts.Read);
            await using var db = await Open(t, ct);
            await using var c = new NpgsqlCommand(@"select a.assigned_to, a.company_id, coalesce(max(co.name), ''), count(*), count(*) filter (where coalesce(t.peripheral, false))
                from assets.assets a left join assets.companies co on co.id = a.company_id left join assets.types t on t.id = a.type_id
                where a.assigned_to <> '' and a.status not in ('retired','lost') and (not @sc or a.company_id = any(@scope)) and (@q = '' or a.assigned_to ilike @l) and (@co = 0 or a.company_id = @co)
                group by a.assigned_to, a.company_id order by lower(a.assigned_to), 2 limit 300", db);
            AssetsAccess.Bind(c, scope); var q = (search ?? "").Trim(); P(c, "q", q); P(c, "l", "%" + q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"); P(c, "co", companyId ?? 0);
            var rows = new List<object>(); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) rows.Add(new { name = rd.GetString(0), companyId = rd.IsDBNull(1) ? (int?)null : rd.GetInt32(1), company = rd.GetString(2), assets = rd.GetInt64(3), accessories = rd.GetInt64(4) });
            return Results.Ok(new { items = rows });
        }));

        readers(r.MapGet("/api/assets/holder", async (string? name, int? companyId, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(name)) return Bad("Say whose assets to show.");
            var scope = await AssetsAccess.ScopeAsync(http, Acts.Read);
            await using var db = await Open(t, ct);
            await using var c = new NpgsqlCommand(@"select a.id, a.tag, a.name, coalesce(t.name, ''), a.serial, a.status, a.warranty_end, a.manufacturer, a.model, a.parent_id, coalesce(p.tag, ''), a.custom, coalesce(t.peripheral, false), a.purchase_date
                from assets.assets a left join assets.types t on t.id = a.type_id left join assets.assets p on p.id = a.parent_id
                where lower(a.assigned_to) = lower(@n) and a.status not in ('retired','lost') and (not @sc or a.company_id = any(@scope)) and (@co = 0 or a.company_id = @co)
                order by (a.parent_id is not null), coalesce(t.peripheral, false), a.id", db);
            AssetsAccess.Bind(c, scope); P(c, "n", name.Trim()); P(c, "co", companyId ?? 0);
            var rows = new List<object>(); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                rows.Add(new { id = rd.GetInt64(0), tag = rd.GetString(1), name = rd.GetString(2), type = rd.GetString(3), serial = rd.GetString(4), status = rd.GetString(5), warrantyEnd = Date(rd, 6), manufacturer = rd.GetString(7), model = rd.GetString(8),
                    parentId = rd.IsDBNull(9) ? (long?)null : rd.GetInt64(9), parentTag = rd.GetString(10), custom = JsonDocument.Parse(rd.GetString(11)).RootElement.Clone(), peripheral = rd.GetBoolean(12), purchaseDate = Date(rd, 13) });
            return Results.Ok(new { name = name.Trim(), items = rows });
        }));

        // hand several things to a person, or take several back, in one go; the items attached to a chosen one can come along
        admin(r.MapPost("/api/assets/holder/handover", async (JsonElement b, ClaimsPrincipal u, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            if (b.ValueKind != JsonValueKind.Object || !b.TryGetProperty("ids", out var arr) || arr.ValueKind != JsonValueKind.Array) return Bad("Choose the assets.");
            var ids = arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt64()).Distinct().Take(200).ToList();
            if (ids.Count == 0) return Bad("Choose the assets.");
            var mode = Text(b, "mode", 10); if (mode is not ("assign" or "return")) return Bad("mode must be assign or return");
            var to = Text(b, "to", 120) ?? ""; if (mode == "assign" && to == "") return Bad("Enter who receives the assets.");
            var cond = (Text(b, "condition", 10) ?? "good").ToLowerInvariant(); if (!Conditions.Contains(cond)) return Bad("Choose the condition: good, fair, damaged or lost.");
            var notes = Text(b, "notes", 1000) ?? ""; var withAttached = !(b.TryGetProperty("includeAttached", out var ia) && ia.ValueKind == JsonValueKind.False);
            await using var db = await Open(t, ct);
            if (withAttached)
            {
                await using var k = new NpgsqlCommand("select id from assets.assets where parent_id = any(@p)", db); P(k, "p", ids.ToArray());
                await using var rd = await k.ExecuteReaderAsync(ct); while (await rd.ReadAsync(ct)) if (!ids.Contains(rd.GetInt64(0))) ids.Add(rd.GetInt64(0));
            }
            var done = 0; var skipped = new List<object>(); var actor = Actor(u);
            foreach (var id in ids)
            {
                await using var tx = await db.BeginTransactionAsync(ct);
                string holder, tag;
                await using (var q = new NpgsqlCommand("select assigned_to, tag from assets.assets where id = @i for update", db, tx))
                { P(q, "i", id); await using var rd = await q.ExecuteReaderAsync(ct); if (!await rd.ReadAsync(ct)) { skipped.Add(new { id, tag = "", reason = "It does not exist." }); continue; } holder = rd.GetString(0); tag = rd.GetString(1); }
                if (mode == "assign" && holder != "") { skipped.Add(new { id, tag, reason = "It is with " + holder + " already." }); continue; }
                if (mode == "return" && holder == "") { skipped.Add(new { id, tag, reason = "It is not with anyone." }); continue; }
                await using (var h = new NpgsqlCommand("insert into assets.handovers (asset_id, kind, holder, by_name, cond, accessories, notes) values (@a, @k, @h, @b, @c, '[]'::jsonb, @n)", db, tx))
                { P(h, "a", id); P(h, "k", mode == "assign" ? "assign" : "handover"); P(h, "h", mode == "assign" ? to : holder); P(h, "b", actor); P(h, "c", cond); P(h, "n", notes); await h.ExecuteNonQueryAsync(ct); }
                if (mode == "assign")
                {
                    await using var up = new NpgsqlCommand("update assets.assets set assigned_to = @h, unalloc_at = null, status = case when status = 'stock' then 'in_use' else status end, updated_at = now(), updated_by = @b where id = @i", db, tx);
                    P(up, "h", to); P(up, "b", actor); P(up, "i", id); await up.ExecuteNonQueryAsync(ct);
                }
                else
                {
                    await using var up = new NpgsqlCommand("update assets.assets set assigned_to = '', prev_holder = @h, unalloc_at = now(), status = case when status = 'in_use' then 'stock' when @c = 'lost' then 'lost' else status end, updated_at = now(), updated_by = @b where id = @i", db, tx);
                    P(up, "h", holder); P(up, "c", cond); P(up, "b", actor); P(up, "i", id); await up.ExecuteNonQueryAsync(ct);
                }
                await AssetsDevice.History(db, id, actor, "handover", JsonSerializer.Serialize(mode == "assign" ? (object)new { to, condition = cond, notes } : new { from = holder, condition = cond, notes }), ct, tx);
                await tx.CommitAsync(ct);
                await audit.WriteAsync(mode == "assign" ? "assets.allocated" : "assets.handover", id.ToString(), mode == "assign" ? to : holder + " (" + cond + ")", ct: ct);
                done++;
            }
            return Results.Ok(new { done, skipped });
        }));

        // ---- how many of each kind are in stock, in use, in repair
        readers(r.MapGet("/api/assets/stock", async (int? companyId, int? siteId, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            var scope = await AssetsAccess.ScopeAsync(http, Acts.Read);
            await using var db = await Open(t, ct);
            await using var c = new NpgsqlCommand(@"select coalesce(t.name, '(no type)'), coalesce(t.peripheral, false), t.id,
                    count(*) filter (where a.status = 'stock'), count(*) filter (where a.status = 'in_use'), count(*) filter (where a.status = 'repair'),
                    count(*) filter (where a.status = 'retired'), count(*) filter (where a.status = 'lost'), count(*)
                from assets.assets a left join assets.types t on t.id = a.type_id
                where (not @sc or a.company_id = any(@scope)) and (@co = 0 or a.company_id = @co) and (@si = 0 or a.site_id = @si)
                group by t.id, t.name, t.peripheral order by 2 desc, 1", db);
            AssetsAccess.Bind(c, scope); P(c, "co", companyId ?? 0); P(c, "si", siteId ?? 0);
            var rows = new List<object>(); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) rows.Add(new { type = rd.GetString(0), peripheral = rd.GetBoolean(1), typeId = rd.IsDBNull(2) ? (int?)null : rd.GetInt32(2), stock = rd.GetInt64(3), inUse = rd.GetInt64(4), repair = rd.GetInt64(5), retired = rd.GetInt64(6), lost = rd.GetInt64(7), total = rd.GetInt64(8) });
            return Results.Ok(new { items = rows });
        }));
    }
}
