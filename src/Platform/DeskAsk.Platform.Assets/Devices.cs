using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using NpgsqlTypes;

namespace DeskAsk.Platform.Assets;

/// <summary>
/// Companies (customers), AMC and rental contracts, the device side of an asset (hardware, storage, network, applications, remote switch),
/// and the fleet-wide software inventory. Inventory documents come from agents and collectors (later slices) or are posted by an administrator.
/// </summary>
public static class AssetsDevice
{
    static IResult Bad(string error, string? detail = null) => Results.BadRequest(new { error, detail });
    static string Actor(ClaimsPrincipal u) => u.FindFirstValue("email") ?? u.FindFirstValue("sub") ?? "unknown";
    static async Task<NpgsqlConnection> Open(ITenantContext t, CancellationToken ct) { var db = new NpgsqlConnection(t.Require().ConnectionString); await db.OpenAsync(ct); return db; }
    static void P(NpgsqlCommand c, string n, object? v) => c.Parameters.AddWithValue(n, v ?? DBNull.Value);
    static void PJ(NpgsqlCommand c, string n, string json) => c.Parameters.Add(new NpgsqlParameter(n, NpgsqlDbType.Jsonb) { Value = json });
    static string Like(string q) => "%" + q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
    static JsonElement Clone(string json) => JsonDocument.Parse(json).RootElement.Clone();

    static string? Txt(JsonElement b, string n, int max, out bool present, out string? error)
    {
        error = null; present = b.ValueKind == JsonValueKind.Object && b.TryGetProperty(n, out var v);
        if (!present) return null;
        var el = b.GetProperty(n);
        if (el.ValueKind == JsonValueKind.Null) return "";
        if (el.ValueKind != JsonValueKind.String) { error = n + " must be text"; return null; }
        var s = el.GetString()!.Trim();
        if (s.Length > max) { error = n + " is longer than " + max + " characters"; return null; }
        return s;
    }
    static DateOnly? Day(JsonElement b, string n, out bool present, out string? error)
    {
        error = null; present = b.ValueKind == JsonValueKind.Object && b.TryGetProperty(n, out _);
        if (!present) return null;
        var el = b.GetProperty(n);
        if (el.ValueKind == JsonValueKind.Null || (el.ValueKind == JsonValueKind.String && el.GetString() == "")) return null;
        if (el.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(el.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        error = n + " must be a date like 2026-11-09"; return null;
    }
    static string D(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
    static async Task History(NpgsqlConnection db, long id, string actor, string action, string changes, CancellationToken ct)
    {
        await using var h = new NpgsqlCommand("insert into assets.history (asset_id, actor, action, changes) values (@i, @a, @c, @x)", db);
        P(h, "i", id); P(h, "a", actor); P(h, "c", action); P(h, "x", changes);
        await h.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Writes a normalised inventory into an asset. Fields the inventory does not know keep their value; a serial number that another asset already has is not copied.</summary>
    public static async Task ApplyInventory(NpgsqlConnection db, long id, NormInv n, string source, string actor, CancellationToken ct)
    {
        string? serial = n.Serial;
        if (serial != null)
        {
            await using var chk = new NpgsqlCommand("select 1 from assets.assets where lower(serial) = lower(@s) and id <> @i", db);
            P(chk, "s", serial); P(chk, "i", id);
            if (await chk.ExecuteScalarAsync(ct) != null) serial = null;
        }
        var first = System.Text.Json.JsonSerializer.Deserialize<List<string>>(n.Ips) is { Count: > 0 } l ? l[0] : "";
        DateOnly? patch = n.LastPatch != null && DateOnly.TryParseExact(n.LastPatch, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var pd) ? pd : null;
        await using var c = new NpgsqlCommand(@"update assets.assets set
            manufacturer = coalesce(@make, manufacturer), model = coalesce(@model, model), serial = coalesce(@serial, serial),
            hostname = coalesce(@host, ''), dns_name = coalesce(@dns, ''), os_family = coalesce(@fam, os_family), os_name = coalesce(@osn, ''), os_version = coalesce(@osv, ''),
            cpu_make = coalesce(@cm, ''), cpu_model = coalesce(@cmo, ''), cpu_socket = coalesce(@cs, ''),
            total_cpu = coalesce(@tc, 0), cpu_cores = coalesce(@cc, 0), cores_per_cpu = coalesce(@cpp, 0), logical_processors = coalesce(@lp, 0),
            ram_bytes = coalesce(@ram, 0), total_storage_bytes = coalesce(@ts, 0), uptime_s = coalesce(@up, uptime_s),
            disks = @disks, nics = @nics, mac_addresses = @macs, connected_ips = @ips, installed_apps = @apps, antivirus = @av, antivirus_status = @avs,
            domain_type = @dt, domain_workgroup = coalesce(@dw, ''), bios_version = coalesce(@bios, ''), last_patch_date = @patch,
            ip_address = case when ip_address = '' then @ip else ip_address end,
            category = case when category = '' then 'IT hardware' else category end,
            source = @src, inventory_at = now(), updated_at = now(), updated_by = @actor
            where id = @id", db);
        P(c, "make", n.Make); P(c, "model", n.Model); P(c, "serial", serial); P(c, "host", n.Hostname); P(c, "dns", n.DnsName);
        P(c, "fam", n.OsFamily); P(c, "osn", n.OsName); P(c, "osv", n.OsVersion); P(c, "cm", n.CpuMake); P(c, "cmo", n.CpuModel); P(c, "cs", n.CpuSocket);
        P(c, "tc", (int?)n.TotalCpu); P(c, "cc", (int?)n.CpuCores); P(c, "cpp", (int?)n.CoresPerCpu); P(c, "lp", (int?)n.Logical);
        P(c, "ram", n.Ram); P(c, "ts", n.TotalStorage); P(c, "up", n.UptimeS);
        PJ(c, "disks", n.Disks); PJ(c, "nics", n.Nics); PJ(c, "macs", n.Macs); PJ(c, "ips", n.Ips); PJ(c, "apps", n.Apps); PJ(c, "av", n.Antivirus);
        P(c, "avs", n.AntivirusStatus); P(c, "dt", n.DomainType); P(c, "dw", n.DomainWorkgroup); P(c, "bios", n.BiosVersion);
        c.Parameters.Add(new NpgsqlParameter("patch", NpgsqlDbType.Date) { Value = patch.HasValue ? patch.Value : DBNull.Value });
        P(c, "ip", first); P(c, "src", source); P(c, "actor", actor); P(c, "id", id);
        await c.ExecuteNonQueryAsync(ct);
        await History(db, id, actor, "inventory", JsonSerializer.Serialize(new { source, apps = n.Apps.Length > 2 ? 1 : 0 }), ct);
    }

    const string DeviceSql = @"select a.id, a.tag, a.name, a.status, a.category, a.company_id, c.name, a.manufacturer, a.model, a.serial, a.bios_version,
        a.cpu_make, a.cpu_model, a.cpu_socket, a.total_cpu, a.cpu_cores, a.cores_per_cpu, a.logical_processors, a.ram_bytes, a.total_storage_bytes,
        a.hostname, a.dns_name, a.ip_address, a.domain_type, a.domain_workgroup, a.os_family, a.os_name, a.os_version, a.antivirus_status, a.antivirus, a.last_patch_date,
        a.agent_id, a.remote_ok, a.last_seen, a.source, a.inventory_at, a.uptime_s, a.location, a.assigned_to, a.purchase_date, a.warranty_end, a.notes, a.disks, a.nics, a.connected_ips,
        coalesce(c.remote_allowed, true),
        exists (select 1 from assets.amc m where m.asset_ids @> to_jsonb(a.id) and (m.end_date is null or m.end_date >= current_date)),
        (select m.title from assets.amc m where m.asset_ids @> to_jsonb(a.id) and (m.end_date is null or m.end_date >= current_date) order by m.end_date desc nulls first limit 1),
        exists (select 1 from assets.rentals m where m.asset_ids @> to_jsonb(a.id) and (m.end_date is null or m.end_date >= current_date))
        from assets.assets a left join assets.companies c on c.id = a.company_id where a.id = @i";

    static async Task<object?> Device(NpgsqlConnection db, long id, int offlineSec, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand(DeviceSql, db); P(c, "i", id);
        await using var r = await c.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        string S(int i) => r.IsDBNull(i) ? "" : r.GetString(i);
        long L(int i) => r.IsDBNull(i) ? 0 : Convert.ToInt64(r.GetValue(i));
        string Dt(int i) => r.IsDBNull(i) ? "" : D(r.GetFieldValue<DateOnly>(i));
        DateTime? T(int i) => r.IsDBNull(i) ? null : r.GetDateTime(i);
        var seen = T(33); var online = S(31) != "" && seen != null && seen.Value > DateTime.UtcNow.AddSeconds(-offlineSec);
        return new
        {
            id = r.GetInt64(0), tag = S(1), name = S(2), status = S(3), category = S(4), companyId = r.IsDBNull(5) ? (int?)null : r.GetInt32(5), company = S(6),
            identity = new { make = S(7), model = S(8), serial = S(9), bios = S(10), cpuMake = S(11), cpuModel = S(12), cpuSocket = S(13), cpus = L(14), cores = L(15), coresPerCpu = L(16), logical = L(17), ramBytes = L(18), storageBytes = L(19) },
            system = new { hostname = S(20), dnsName = S(21), ip = S(22), domainType = S(23), domain = S(24), osFamily = S(25), os = S(26), osVersion = S(27), antivirus = S(28), antivirusProducts = Clone(r.GetString(29)), lastPatch = Dt(30), uptimeSeconds = L(36) },
            agent = new { id = S(31), online, lastSeen = seen, source = S(34), inventoryAt = T(35) },
            ownership = new { location = S(37), assignedTo = S(38), purchaseDate = Dt(39), warrantyEnd = Dt(40), underAmc = r.GetBoolean(46), amc = S(47), rented = r.GetBoolean(48), notes = S(41) },
            remote = new { assetAllows = r.GetBoolean(32), companyAllows = r.GetBoolean(45) },
            storage = Clone(r.GetString(42)), network = Clone(r.GetString(43)), connectedIps = Clone(r.GetString(44))
        };
    }

    static int OfflineSeconds(Microsoft.Extensions.Configuration.IConfiguration cfg) => int.TryParse(cfg["Assets:AgentOfflineSeconds"], out var s) && s > 0 ? s : 900;

    // ---- contracts (AMC and rentals share the shape)
    sealed record Contract(string Table, bool IsAmc);
    static async Task<List<object>> ListContracts(NpgsqlConnection db, Contract k, CancellationToken ct)
    {
        var extra = k.IsAmc ? ", m.coverage, m.visits, m.sla_pct" : "";
        await using var c = new NpgsqlCommand($"select m.id, m.company_id, c.name, m.title, m.start_date, m.end_date, m.notes, m.asset_ids{extra} from assets.{k.Table} m left join assets.companies c on c.id = m.company_id order by m.end_date desc nulls first, m.id desc", db);
        var rows = new List<object>(); await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var ids = Clone(r.GetString(7));
            var active = r.IsDBNull(5) || r.GetFieldValue<DateOnly>(5) >= DateOnly.FromDateTime(DateTime.UtcNow);
            if (k.IsAmc) rows.Add(new { id = r.GetInt32(0), companyId = r.IsDBNull(1) ? (int?)null : r.GetInt32(1), company = r.IsDBNull(2) ? "" : r.GetString(2), title = r.GetString(3), start = r.IsDBNull(4) ? "" : D(r.GetFieldValue<DateOnly>(4)), end = r.IsDBNull(5) ? "" : D(r.GetFieldValue<DateOnly>(5)), notes = r.GetString(6), assetIds = ids, active, coverage = r.GetString(8), visits = r.GetInt32(9), slaPct = r.GetInt32(10) });
            else rows.Add(new { id = r.GetInt32(0), companyId = r.IsDBNull(1) ? (int?)null : r.GetInt32(1), company = r.IsDBNull(2) ? "" : r.GetString(2), title = r.GetString(3), start = r.IsDBNull(4) ? "" : D(r.GetFieldValue<DateOnly>(4)), end = r.IsDBNull(5) ? "" : D(r.GetFieldValue<DateOnly>(5)), notes = r.GetString(6), assetIds = ids, active });
        }
        return rows;
    }

    static async Task<(IResult? Error, long[] Ids)> AssetIds(NpgsqlConnection db, JsonElement body, CancellationToken ct)
    {
        var ids = new List<long>();
        if (body.TryGetProperty("assetIds", out var a))
        {
            if (a.ValueKind != JsonValueKind.Array || a.GetArrayLength() > 5000) return (Bad("bad_input", "assetIds must be a list of at most 5000 asset ids"), Array.Empty<long>());
            foreach (var x in a.EnumerateArray()) { if (x.ValueKind != JsonValueKind.Number || !x.TryGetInt64(out var v)) return (Bad("bad_input", "assetIds must be numbers"), Array.Empty<long>()); ids.Add(v); }
        }
        ids = ids.Distinct().ToList();
        if (ids.Count > 0)
        {
            await using var c = new NpgsqlCommand("select count(*) from assets.assets where id = any(@ids)", db); P(c, "ids", ids.ToArray());
            if ((long)(await c.ExecuteScalarAsync(ct))! != ids.Count) return (Bad("bad_input", "one of the assets does not exist"), Array.Empty<long>());
        }
        return (null, ids.ToArray());
    }

    static async Task<(IResult? Error, int? Company)> CompanyOf(NpgsqlConnection db, JsonElement body, CancellationToken ct)
    {
        if (!body.TryGetProperty("companyId", out var c) || c.ValueKind == JsonValueKind.Null) return (null, null);
        if (c.ValueKind != JsonValueKind.Number || !c.TryGetInt32(out var id)) return (Bad("bad_input", "companyId must be a number"), null);
        await using var q = new NpgsqlCommand("select 1 from assets.companies where id = @i", db); P(q, "i", id);
        return await q.ExecuteScalarAsync(ct) == null ? (Bad("bad_input", "company not found"), null) : (null, id);
    }

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        var admin = (RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole("admin")).RequireModule(key);
        var anyone = (RouteHandlerBuilder b) => b.RequireAuthorization().RequireModule(key);

        // ---------------------------------------------------------------- companies
        anyone(r.MapGet("/api/assets/companies", async (ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            await using var c = new NpgsqlCommand(@"select c.id, c.name, c.code, c.contact_name, c.contact_email, c.phone, c.address, c.notes, c.status, c.remote_allowed,
                (select count(*) from assets.assets a where a.company_id = c.id) from assets.companies c order by lower(c.name)", db);
            var rows = new List<object>(); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) rows.Add(new { id = rd.GetInt32(0), name = rd.GetString(1), code = rd.GetString(2), contactName = rd.GetString(3), contactEmail = rd.GetString(4), phone = rd.GetString(5), address = rd.GetString(6), notes = rd.GetString(7), status = rd.GetString(8), remoteAllowed = rd.GetBoolean(9), assets = rd.GetInt64(10) });
            return Results.Ok(rows);
        }));

        async Task<IResult> SaveCompany(int? id, JsonElement body, ITenantContext t, IAuditLog audit, CancellationToken ct)
        {
            string?[] vals = new string?[7]; var names = new[] { "name", "code", "contactName", "contactEmail", "phone", "address", "notes" }; var max = new[] { 120, 30, 120, 200, 40, 400, 2000 };
            var present = new bool[7];
            for (int i = 0; i < 7; i++) { vals[i] = Txt(body, names[i], max[i], out present[i], out var err); if (err != null) return Bad("bad_input", err); }
            if (id == null && (vals[0] ?? "") == "") return Bad("bad_input", "name is required");
            if (present[0] && (vals[0] ?? "") == "") return Bad("bad_input", "name cannot be empty");
            var status = body.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
            if (status != null && status is not ("active" or "inactive")) return Bad("bad_input", "status must be active or inactive");
            bool? remote = body.TryGetProperty("remoteAllowed", out var ra) && ra.ValueKind is JsonValueKind.True or JsonValueKind.False ? ra.GetBoolean() : null;
            await using var db = await Open(t, ct);
            try
            {
                if (id == null)
                {
                    await using var c = new NpgsqlCommand("insert into assets.companies (name, code, contact_name, contact_email, phone, address, notes, status, remote_allowed) values (@n,@c,@cn,@ce,@p,@a,@no,@s,@r) returning id", db);
                    P(c, "n", vals[0]); P(c, "c", vals[1] ?? ""); P(c, "cn", vals[2] ?? ""); P(c, "ce", vals[3] ?? ""); P(c, "p", vals[4] ?? ""); P(c, "a", vals[5] ?? ""); P(c, "no", vals[6] ?? ""); P(c, "s", status ?? "active"); P(c, "r", remote ?? false);
                    var nid = (int)(await c.ExecuteScalarAsync(ct))!;
                    await audit.WriteAsync("assets.company_created", nid.ToString(), vals[0]!, ct: ct);
                    return Results.Ok(new { id = nid });
                }
                await using var u = new NpgsqlCommand(@"update assets.companies set name = coalesce(@n, name), code = coalesce(@c, code), contact_name = coalesce(@cn, contact_name), contact_email = coalesce(@ce, contact_email),
                    phone = coalesce(@p, phone), address = coalesce(@a, address), notes = coalesce(@no, notes), status = coalesce(@s, status), remote_allowed = coalesce(@r, remote_allowed) where id = @i", db);
                P(u, "n", present[0] ? vals[0] : null); P(u, "c", vals[1]); P(u, "cn", vals[2]); P(u, "ce", vals[3]); P(u, "p", vals[4]); P(u, "a", vals[5]); P(u, "no", vals[6]); P(u, "s", status); P(u, "r", remote); P(u, "i", id);
                if (await u.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
                await audit.WriteAsync("assets.company_changed", id.ToString(), remote != null ? "remote_allowed=" + remote : "", ct: ct);
                return Results.Ok(new { id });
            }
            catch (PostgresException e) when (e.SqlState == "23505") { return Results.Conflict(new { error = "company_exists" }); }
        }
        admin(r.MapPost("/api/assets/companies", (JsonElement body, ITenantContext t, IAuditLog audit, CancellationToken ct) => SaveCompany(null, body, t, audit, ct)));
        admin(r.MapPut("/api/assets/companies/{id:int}", (int id, JsonElement body, ITenantContext t, IAuditLog audit, CancellationToken ct) => SaveCompany(id, body, t, audit, ct)));
        admin(r.MapDelete("/api/assets/companies/{id:int}", async (int id, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            await using var chk = new NpgsqlCommand("select count(*) from assets.assets where company_id = @i", db); P(chk, "i", id);
            var n = (long)(await chk.ExecuteScalarAsync(ct))!;
            if (n > 0) return Results.Conflict(new { error = "company_in_use", assets = n });
            await using var c = new NpgsqlCommand("delete from assets.companies where id = @i", db); P(c, "i", id);
            if (await c.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            await audit.WriteAsync("assets.company_deleted", id.ToString(), ct: ct);
            return Results.Ok(new { ok = true });
        }));

        // ---------------------------------------------------------------- AMC and rentals
        foreach (var k in new[] { new Contract("amc", true), new Contract("rentals", false) })
        {
            var path = "/api/assets/" + k.Table; var what = k.IsAmc ? "amc" : "rental";
            anyone(r.MapGet(path, async (ITenantContext t, CancellationToken ct) => { await using var db = await Open(t, ct); return Results.Ok(await ListContracts(db, k, ct)); }));

            async Task<IResult> Save(int? id, JsonElement body, ITenantContext t, IAuditLog audit, CancellationToken ct)
            {
                var title = Txt(body, "title", 160, out var hasTitle, out var e1); var notes = Txt(body, "notes", 2000, out var hasNotes, out var e2);
                var coverage = Txt(body, "coverage", 1000, out var hasCov, out var e3);
                var start = Day(body, "start", out var hasStart, out var e4); var end = Day(body, "end", out var hasEnd, out var e5);
                var err = e1 ?? e2 ?? e3 ?? e4 ?? e5; if (err != null) return Bad("bad_input", err);
                if (id == null && (title ?? "") == "") return Bad("bad_input", "title is required");
                if (start != null && end != null && end < start) return Bad("bad_input", "end is before start");
                int visits = 0, sla = 0;
                if (k.IsAmc)
                {
                    if (body.TryGetProperty("visits", out var v) && (!v.TryGetInt32(out visits) || visits < 0 || visits > 10000)) return Bad("bad_input", "visits must be 0 to 10000");
                    if (body.TryGetProperty("slaPct", out var s) && (!s.TryGetInt32(out sla) || sla < 0 || sla > 100)) return Bad("bad_input", "slaPct must be 0 to 100");
                }
                await using var db = await Open(t, ct);
                var (ce, company) = await CompanyOf(db, body, ct); if (ce != null) return ce;
                var hasCompany = body.TryGetProperty("companyId", out _);
                var hasAssets = body.TryGetProperty("assetIds", out _);
                var (ae, ids) = await AssetIds(db, body, ct); if (ae != null) return ae;
                if (id == null)
                {
                    await using var c = new NpgsqlCommand($"insert into assets.{k.Table} (company_id, title, start_date, end_date, notes, asset_ids{(k.IsAmc ? ", coverage, visits, sla_pct" : "")}) values (@co,@t,@s,@e,@n,@a{(k.IsAmc ? ",@cv,@v,@sl" : "")}) returning id", db);
                    P(c, "co", company); P(c, "t", title); c.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Date) { Value = start.HasValue ? start.Value : DBNull.Value }); c.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Date) { Value = end.HasValue ? end.Value : DBNull.Value });
                    P(c, "n", notes ?? ""); PJ(c, "a", JsonSerializer.Serialize(ids));
                    if (k.IsAmc) { P(c, "cv", coverage ?? ""); P(c, "v", visits); P(c, "sl", sla); }
                    var nid = (int)(await c.ExecuteScalarAsync(ct))!;
                    await audit.WriteAsync("assets." + what + "_created", nid.ToString(), title!, ct: ct);
                    return Results.Ok(new { id = nid });
                }
                await using var u = new NpgsqlCommand($@"update assets.{k.Table} set title = coalesce(@t, title), notes = coalesce(@n, notes),
                    company_id = case when @hc then @co else company_id end, start_date = case when @hs then @s else start_date end, end_date = case when @he then @e else end_date end,
                    asset_ids = case when @ha then @a else asset_ids end{(k.IsAmc ? ", coverage = coalesce(@cv, coverage), visits = case when @hv then @v else visits end, sla_pct = case when @hsl then @sl else sla_pct end" : "")}, updated_at = now() where id = @i", db);
                P(u, "t", hasTitle ? title : null); P(u, "n", notes); P(u, "hc", hasCompany); P(u, "co", company); P(u, "hs", hasStart); P(u, "he", hasEnd); P(u, "ha", hasAssets); P(u, "i", id);
                u.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Date) { Value = start.HasValue ? start.Value : DBNull.Value }); u.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Date) { Value = end.HasValue ? end.Value : DBNull.Value });
                PJ(u, "a", JsonSerializer.Serialize(ids));
                if (k.IsAmc) { P(u, "cv", coverage); P(u, "hv", body.TryGetProperty("visits", out _)); P(u, "v", visits); P(u, "hsl", body.TryGetProperty("slaPct", out _)); P(u, "sl", sla); }
                if (await u.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
                await audit.WriteAsync("assets." + what + "_changed", id.ToString(), ct: ct);
                return Results.Ok(new { id });
            }
            admin(r.MapPost(path, (JsonElement body, ITenantContext t, IAuditLog audit, CancellationToken ct) => Save(null, body, t, audit, ct)));
            admin(r.MapPut(path + "/{id:int}", (int id, JsonElement body, ITenantContext t, IAuditLog audit, CancellationToken ct) => Save(id, body, t, audit, ct)));
            admin(r.MapDelete(path + "/{id:int}", async (int id, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
            {
                await using var db = await Open(t, ct);
                await using var c = new NpgsqlCommand($"delete from assets.{k.Table} where id = @i", db); P(c, "i", id);
                if (await c.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
                await audit.WriteAsync("assets." + what + "_deleted", id.ToString(), ct: ct);
                return Results.Ok(new { ok = true });
            }));
        }

        // ---------------------------------------------------------------- the device side of one asset
        anyone(r.MapGet("/api/assets/{id:long}/device", async (long id, ITenantContext t, Microsoft.Extensions.Configuration.IConfiguration cfg, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            var d = await Device(db, id, OfflineSeconds(cfg), ct);
            return d == null ? Results.NotFound() : Results.Ok(d);
        }));

        anyone(r.MapGet("/api/assets/{id:long}/applications", async (long id, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            var q = http.Request.Query["q"].ToString().Trim();
            await using var c = new NpgsqlCommand(@"select e->>'name', coalesce(e->>'version',''), coalesce(e->>'publisher',''), coalesce(e->>'installed_on','')
                from assets.assets a cross join lateral jsonb_array_elements(a.installed_apps) e
                where a.id = @i and (@q = '' or lower(concat_ws(' ', e->>'name', e->>'publisher')) like @l escape '\') order by lower(e->>'name') limit 5000", db);
            P(c, "i", id); P(c, "q", q); P(c, "l", Like(q.ToLowerInvariant()));
            var rows = new List<object>(); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) rows.Add(new { name = rd.GetString(0), version = rd.GetString(1), publisher = rd.GetString(2), installedOn = rd.GetString(3) });
            return Results.Ok(new { count = rows.Count, items = rows });
        }));

        admin(r.MapPatch("/api/assets/{id:long}/ownership", async (long id, JsonElement body, ClaimsPrincipal u, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var category = Txt(body, "category", 60, out var hasCat, out var err); if (err != null) return Bad("bad_input", err);
            await using var db = await Open(t, ct);
            var (ce, company) = await CompanyOf(db, body, ct); if (ce != null) return ce;
            var hasCompany = body.TryGetProperty("companyId", out _);
            await using var c = new NpgsqlCommand("update assets.assets set company_id = case when @hc then @co else company_id end, category = coalesce(@cat, category), updated_at = now(), updated_by = @a where id = @i", db);
            P(c, "hc", hasCompany); P(c, "co", company); P(c, "cat", category); P(c, "a", Actor(u)); P(c, "i", id);
            if (await c.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            await History(db, id, Actor(u), "ownership", JsonSerializer.Serialize(new { companyId = hasCompany ? company : (int?)null, category }), ct);
            await audit.WriteAsync("assets.ownership_changed", id.ToString(), ct: ct);
            return Results.Ok(new { ok = true });
        }));

        admin(r.MapPost("/api/assets/{id:long}/remote", async (long id, JsonElement body, ClaimsPrincipal u, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            if (!body.TryGetProperty("on", out var on) || on.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return Bad("bad_input", "on must be true or false");
            await using var db = await Open(t, ct);
            await using var c = new NpgsqlCommand("update assets.assets set remote_ok = @o, updated_at = now() where id = @i", db); P(c, "o", on.GetBoolean()); P(c, "i", id);
            if (await c.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            await History(db, id, Actor(u), "remote", JsonSerializer.Serialize(new { allow = on.GetBoolean() }), ct);
            await audit.WriteAsync(on.GetBoolean() ? "assets.remote_allowed" : "assets.remote_blocked", id.ToString(), ct: ct);
            return Results.Ok(new { remoteOk = on.GetBoolean() });
        }));

        admin(r.MapPost("/api/assets/{id:long}/inventory", async (long id, JsonElement body, ClaimsPrincipal u, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            if (body.ValueKind != JsonValueKind.Object) return Bad("bad_input", "send the inventory document as a JSON object");
            await using var db = await Open(t, ct);
            await using (var chk = new NpgsqlCommand("select 1 from assets.assets where id = @i", db)) { P(chk, "i", id); if (await chk.ExecuteScalarAsync(ct) == null) return Results.NotFound(); }
            await ApplyInventory(db, id, Inventory.Normalize(body), "import", Actor(u), ct);
            await audit.WriteAsync("assets.inventory_imported", id.ToString(), ct: ct);
            return Results.Ok(new { ok = true });
        }));

        // ---------------------------------------------------------------- software inventory across all computers
        const string AppFrom = " from assets.assets a cross join lateral jsonb_array_elements(a.installed_apps) e ";
        static void Scope(HttpContext http, List<string> w, NpgsqlCommand c)
        {
            var co = http.Request.Query["company"].ToString(); var os = http.Request.Query["os"].ToString();
            if (int.TryParse(co, out var cid)) { w.Add("a.company_id = @co"); P(c, "co", cid); }
            if (os is "windows" or "linux") { w.Add("a.os_family = @os"); P(c, "os", os); }
        }

        anyone(r.MapGet("/api/assets/software", async (HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            var c = new NpgsqlCommand { Connection = db };
            var w = new List<string> { "(e->>'name') is not null", "(e->>'name') <> ''" };
            Scope(http, w, c);
            var q = http.Request.Query["q"].ToString().Trim();
            if (q != "") { w.Add("lower(concat_ws(' ', e->>'name', e->>'publisher')) like @l escape '\\'"); P(c, "l", Like(q.ToLowerInvariant())); }
            var order = http.Request.Query["sort"] == "name" ? "lower(min(e->>'name')) asc" : "count(distinct a.id) desc, lower(min(e->>'name')) asc";
            c.CommandText = "select lower(e->>'name'), min(e->>'name'), max(coalesce(e->>'publisher','')), count(distinct a.id), count(distinct coalesce(e->>'version','')) " + AppFrom + " where " + string.Join(" and ", w) + " group by lower(e->>'name') order by " + order + " limit 1001";
            var rows = new List<object>(); bool more = false;
            await using (var rd = await c.ExecuteReaderAsync(ct))
                while (await rd.ReadAsync(ct)) { if (rows.Count == 1000) { more = true; break; } rows.Add(new { name = rd.GetString(1), publisher = rd.GetString(2), computers = rd.GetInt64(3), versions = rd.GetInt64(4) }); }
            return Results.Ok(new { items = rows, truncated = more });
        }));

        async Task<List<object[]>> Computers(HttpContext http, NpgsqlConnection db, CancellationToken ct)
        {
            var name = http.Request.Query["name"].ToString().Trim();
            var c = new NpgsqlCommand { Connection = db };
            var w = new List<string> { "lower(e->>'name') = lower(@n)" }; P(c, "n", name);
            if (http.Request.Query.ContainsKey("version")) { w.Add("coalesce(e->>'version','') = @v"); P(c, "v", http.Request.Query["version"].ToString()); }
            Scope(http, w, c);
            c.CommandText = "select a.id, a.tag, a.name, a.hostname, coalesce(co.name,''), a.os_name, coalesce(e->>'version',''), coalesce(e->>'publisher',''), coalesce(e->>'installed_on','') " + AppFrom + " left join assets.companies co on co.id = a.company_id where " + string.Join(" and ", w) + " order by lower(a.name) limit 2000";
            var rows = new List<object[]>(); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) rows.Add(new object[] { rd.GetInt64(0), rd.GetString(1), rd.GetString(2), rd.GetString(3), rd.GetString(4), rd.GetString(5), rd.GetString(6), rd.GetString(7), rd.GetString(8) });
            return rows;
        }

        anyone(r.MapGet("/api/assets/software/computers", async (HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            if (http.Request.Query["name"].ToString().Trim() == "") return Bad("bad_input", "choose a program (name)");
            await using var db = await Open(t, ct);
            var rows = await Computers(http, db, ct);
            return Results.Ok(new { items = rows.Select(x => new { id = x[0], tag = x[1], name = x[2], hostname = x[3], company = x[4], os = x[5], version = x[6], publisher = x[7], installedOn = x[8] }) });
        }));

        anyone(r.MapGet("/api/assets/software/export.csv", async (HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            var sb = new StringBuilder();
            if (http.Request.Query["name"].ToString().Trim() != "")
            {
                sb.Append("Tag,Asset name,Host name,Company,Operating system,Program,Version,Publisher,Installed on\r\n");
                foreach (var x in await Computers(http, db, ct))
                    sb.Append(string.Join(",", new[] { (string)x[1], (string)x[2], (string)x[3], (string)x[4], (string)x[5], http.Request.Query["name"].ToString().Trim(), (string)x[6], (string)x[7], (string)x[8] }.Select(Csv.Cell))).Append("\r\n");
            }
            else
            {
                var c = new NpgsqlCommand { Connection = db }; var w = new List<string> { "(e->>'name') is not null", "(e->>'name') <> ''" }; Scope(http, w, c);
                c.CommandText = "select min(e->>'name'), max(coalesce(e->>'publisher','')), count(distinct a.id), count(distinct coalesce(e->>'version','')) " + AppFrom + " where " + string.Join(" and ", w) + " group by lower(e->>'name') order by count(distinct a.id) desc, lower(min(e->>'name')) limit 20000";
                sb.Append("Program,Publisher,Computers,Versions\r\n");
                await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) sb.Append(string.Join(",", new[] { rd.GetString(0), rd.GetString(1), rd.GetInt64(2).ToString(CultureInfo.InvariantCulture), rd.GetInt64(3).ToString(CultureInfo.InvariantCulture) }.Select(Csv.Cell))).Append("\r\n");
            }
            return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8", "software-inventory.csv");
        }));
    }
}
