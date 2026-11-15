using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DeskAsk.Platform.Assets;

/// <summary>
/// Keeping one record per device. A serial number, MAC address or host name that another asset of the same customer already has is refused;
/// the same IP address or the same name is a warning that a person may confirm. Assets that look like the same device are listed on the
/// Duplicates page and can be merged: the asset with the agent is kept and the other record is retired, with everything of it moved over.
/// </summary>
public static class DuplicateCheck
{
    public sealed record Match(long Id, string Tag, string Name, string Hostname, string Ip, string Serial, string Status, string[] Why, bool Hard);

    /// <summary>"N/A", "To be filled by O.E.M.", all zeros and the like: not a serial number, so never a reason to call two assets the same.</summary>
    public static bool IsPlaceholder(string s)
    {
        var v = s.Trim().ToLowerInvariant();
        return v is "" or "n/a" or "na" or "none" or "null" or "unknown" or "tbd" or "-" || v.Contains("o.e.m") || v.Contains("default string") || v.Contains("not specified") || v.Contains("to be filled") || v.Contains("system serial") || v.Trim('0', '-', ' ', 'x', 'f') == "";
    }

    static readonly Regex Hex = new("^[0-9a-f]{12}$", RegexOptions.Compiled);

    /// <summary>aa:bb:cc:dd:ee:ff from aa-bb-.., aabb.ccdd.eeff or aabbccddeeff; null when it is not a MAC address.</summary>
    public static string? NormMac(string s)
    {
        var h = new string(s.Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        if (!Hex.IsMatch(h) || s.Count(ch => !Uri.IsHexDigit(ch) && ch is not (':' or '-' or '.' or ' ')) > 0) return null;
        return string.Join(':', Enumerable.Range(0, 6).Select(i => h.Substring(i * 2, 2)));
    }

    /// <summary>One or more MAC addresses separated by commas, semicolons or spaces. Error text when one is wrong.</summary>
    public static List<string>? ParseMacs(string input, out string? error)
    {
        error = null; var res = new List<string>();
        foreach (var part in Regex.Split(input.Trim(), @"[,;\s]+").Where(x => x != ""))
        {
            var m = NormMac(part);
            if (m == null) { error = $"\"{part}\" is not a MAC address; write it like 00:1A:2B:3C:4D:5E"; return null; }
            if (!res.Contains(m)) res.Add(m);
        }
        return res;
    }

    /// <summary>
    /// The 12 hex digits of a MAC address, lower case, however it was written (agents send AA-BB-.., controllers AA:BB:.., people type
    /// anything); null for something that is not one, and for all zeros or all ones, which many virtual adapters report.
    /// </summary>
    public static string? Hex12(string s)
    {
        var h = new string((s ?? "").Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        return Hex.IsMatch(h) && h is not ("000000000000" or "ffffffffffff") ? h : null;
    }

    /// <summary>SQL: the asset has one of the MAC addresses in @macs (12 hex digits each), compared by digits only.</summary>
    internal const string MacMatch = "(cardinality(@macs) > 0 and exists (select 1 from jsonb_array_elements_text(case when jsonb_typeof(mac_addresses) = 'array' then mac_addresses else '[]'::jsonb end) mm where lower(regexp_replace(mm, '[^0-9A-Fa-f]', '', 'g')) = any(@macs)))";

    /// <summary>The host name without its domain: sql01.acme.local and SQL01 are the same machine.</summary>
    public static string FirstLabel(string host) => host.Trim().ToLowerInvariant().Split('.')[0];

    /// <summary>Other assets of the same customer (retired and lost ones do not count) that share a serial number, MAC address, host name, IP address or name.</summary>
    public static async Task<List<Match>> Find(NpgsqlConnection db, int? company, string? name, string? hostname, string? ip, IReadOnlyList<string>? macs, string? serial, long excludeId, CancellationToken ct)
    {
        var host = FirstLabel(hostname ?? ""); var sn = (serial ?? "").Trim(); if (IsPlaceholder(sn)) sn = "";
        var res = new List<Match>();
        await using var c = new NpgsqlCommand(@"select id, tag, name, hostname, ip_address, serial, status, mac_addresses::text,
                (@h <> '' and (split_part(lower(hostname), '.', 1) = @h or split_part(lower(dns_name), '.', 1) = @h)),
                (@ip <> '' and ip_address = @ip), (@n <> '' and lower(name) = lower(@n)),
                " + MacMatch + @", (@s <> '' and lower(serial) = lower(@s))
            from assets.assets
            where status not in ('retired', 'lost') and id <> @x and company_id is not distinct from @c
              and ((@h <> '' and (split_part(lower(hostname), '.', 1) = @h or split_part(lower(dns_name), '.', 1) = @h)) or (@ip <> '' and ip_address = @ip) or (@n <> '' and lower(name) = lower(@n))
                   or " + MacMatch + @" or (@s <> '' and lower(serial) = lower(@s)))
            order by id limit 20", db);
        c.Parameters.AddWithValue("c", (object?)company ?? DBNull.Value); c.Parameters.AddWithValue("x", excludeId);
        c.Parameters.AddWithValue("h", host); c.Parameters.AddWithValue("ip", (ip ?? "").Trim()); c.Parameters.AddWithValue("n", (name ?? "").Trim());
        c.Parameters.AddWithValue("macs", (macs ?? Array.Empty<string>()).Select(Hex12).OfType<string>().Distinct().ToArray()); c.Parameters.AddWithValue("s", sn);
        await using var rd = await c.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var why = new List<string>(); var hard = false;
            if (rd.GetBoolean(12)) { why.Add("serial number"); hard = true; }
            if (rd.GetBoolean(11)) { why.Add("MAC address"); hard = true; }
            if (rd.GetBoolean(8)) { why.Add("host name"); hard = true; }
            if (rd.GetBoolean(9)) why.Add("IP address");
            if (rd.GetBoolean(10)) why.Add("name");
            res.Add(new Match(rd.GetInt64(0), rd.GetString(1), rd.GetString(2), rd.GetString(3), rd.GetString(4), rd.GetString(5), rd.GetString(6), why.ToArray(), hard));
        }
        return res;
    }

    public sealed record Known(long Id, string Name, string Ip, bool HasAgent, string Why);

    /// <summary>
    /// The asset a device found on the network already is: same MAC address first, then the same host name, then the same IP address.
    /// A device that moved to another address (DHCP) is still recognised. Only the collector's customer is searched, unless anyCompany.
    /// Retired and lost assets do not count, so a machine brought back is offered again.
    /// </summary>
    public static async Task<Known?> KnownAsync(NpgsqlConnection db, bool anyCompany, int? company, string? ip, string? mac, string? host, CancellationToken ct, NpgsqlTransaction? tx = null)
    {
        var raw = (host ?? "").Trim();
        var h = raw == "" || System.Net.IPAddress.TryParse(raw, out _) ? "" : FirstLabel(raw);
        var m = Hex12(mac ?? ""); var addr = (ip ?? "").Trim();
        if (h == "" && m == null && addr == "") return null;
        await using var c = new NpgsqlCommand(@"select id, name, ip_address, agent_id <> '',
                case when " + MacMatch + @" then 'MAC address'
                     when @h <> '' and (split_part(lower(hostname), '.', 1) = @h or split_part(lower(dns_name), '.', 1) = @h) then 'host name' else 'IP address' end as why
            from assets.assets
            where status not in ('retired', 'lost') and (@any or company_id is not distinct from @c)
              and (" + MacMatch + @" or (@h <> '' and (split_part(lower(hostname), '.', 1) = @h or split_part(lower(dns_name), '.', 1) = @h)) or (@ip <> '' and ip_address = @ip))
            order by case when " + MacMatch + @" then 0 when @h <> '' and (split_part(lower(hostname), '.', 1) = @h or split_part(lower(dns_name), '.', 1) = @h) then 1 else 2 end, id
            limit 1", db, tx);
        c.Parameters.AddWithValue("any", anyCompany); c.Parameters.AddWithValue("c", (object?)company ?? DBNull.Value);
        c.Parameters.AddWithValue("macs", m == null ? Array.Empty<string>() : new[] { m }); c.Parameters.AddWithValue("h", h); c.Parameters.AddWithValue("ip", addr);
        await using var rd = await c.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? new Known(rd.GetInt64(0), rd.GetString(1), rd.GetString(2), rd.GetBoolean(3), rd.GetString(4)) : null;
    }

    /// <summary>
    /// A device without an agent that was recognised by its MAC address at a new IP address: the asset takes the new address, with a
    /// history line, so monitoring and the remote tools follow it. An agent reports its own address, and a host-name match alone is not
    /// trusted enough to move the address.
    /// </summary>
    public static async Task<bool> FollowAddressAsync(NpgsqlConnection db, Known k, string newIp, string via, CancellationToken ct)
    {
        if (k.HasAgent || k.Why != "MAC address" || string.IsNullOrWhiteSpace(newIp) || k.Ip == newIp) return false;
        await using (var c = new NpgsqlCommand("update assets.assets set ip_address = @ip, updated_at = now() where id = @i and ip_address = @old and agent_id = ''", db))
        {
            c.Parameters.AddWithValue("ip", newIp); c.Parameters.AddWithValue("i", k.Id); c.Parameters.AddWithValue("old", k.Ip);
            if (await c.ExecuteNonQueryAsync(ct) == 0) return false;
        }
        await AssetsDevice.History(db, k.Id, via, "updated", JsonSerializer.Serialize(new { ip_address = new { from = k.Ip, to = newIp } }), ct);
        return true;
    }

    public static string Describe(IEnumerable<Match> m) => string.Join("; ", m.Take(3).Select(x => $"{x.Tag} {x.Name} (same {string.Join(" and ", x.Why)})"));
}

public static class DuplicatesApi
{
    static async Task<NpgsqlConnection> Open(ITenantContext t, CancellationToken ct) { var db = new NpgsqlConnection(t.Require().ConnectionString); await db.OpenAsync(ct); return db; }
    static void P(NpgsqlCommand c, string n, object? v) => c.Parameters.AddWithValue(n, v ?? DBNull.Value);

    sealed record Row(long Id, string Tag, string Name, string Host, string Ip, string Serial, string Status, string Source, int? Company, string CompanyName, bool Agent, DateTime? LastSeen, DateTime Created, List<string> Macs);

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        // the groups of assets that look like one device
        r.MapGet("/api/assets/duplicates", async (HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            var scope = await AssetsAccess.ScopeAsync(http, Acts.Read);
            await using var db = await Open(t, ct);
            var rows = new List<Row>();
            await using (var c = new NpgsqlCommand(@"select a.id, a.tag, a.name, a.hostname, a.ip_address, a.serial, a.status, a.source, a.company_id, coalesce(co.name, ''), a.agent_id <> '', a.last_seen, a.created_at, a.mac_addresses::text
                from assets.assets a left join assets.companies co on co.id = a.company_id where a.status not in ('retired', 'lost') and (not @sc or a.company_id = any(@scope))", db).Scoped(scope))
            {
                await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                    rows.Add(new Row(rd.GetInt64(0), rd.GetString(1), rd.GetString(2), rd.GetString(3), rd.GetString(4), rd.GetString(5), rd.GetString(6), rd.GetString(7),
                        rd.IsDBNull(8) ? null : rd.GetInt32(8), rd.GetString(9), rd.GetBoolean(10), rd.IsDBNull(11) ? null : rd.GetDateTime(11), rd.GetDateTime(12),
                        (JsonSerializer.Deserialize<List<string>>(rd.GetString(13)) ?? new()).ToList()));
            }
            // two assets are linked by a shared key; groups are the connected sets
            var parent = rows.ToDictionary(x => x.Id, x => x.Id);
            long Find(long x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            var keys = new Dictionary<string, (long First, string Why)>(); var reasons = new Dictionary<long, HashSet<string>>();
            void Link(Row a, string k, string why)
            {
                if (!keys.TryGetValue(k, out var f)) { keys[k] = (a.Id, why); return; }
                parent[Find(a.Id)] = Find(f.First);
                foreach (var id in new[] { a.Id, f.First }) { if (!reasons.TryGetValue(id, out var s)) reasons[id] = s = new(); s.Add(why); }
            }
            foreach (var a in rows)
            {
                var co = (a.Company?.ToString(CultureInfo.InvariantCulture) ?? "-") + "|";
                if (a.Serial != "" && !DuplicateCheck.IsPlaceholder(a.Serial)) Link(a, "s|" + a.Serial.ToLowerInvariant(), "same serial number");
                foreach (var m in a.Macs) Link(a, "m|" + co + m, "same MAC address");
                if (a.Host != "") Link(a, "h|" + co + DuplicateCheck.FirstLabel(a.Host), "same host name");
                if (a.Ip != "") Link(a, "i|" + co + a.Ip, "same IP address");
                if (a.Name.Trim() != "") Link(a, "n|" + co + a.Name.Trim().ToLowerInvariant(), "same name");
            }
            var groups = rows.GroupBy(x => Find(x.Id)).Where(g => g.Count() > 1)
                .Select(g => new
                {
                    company = g.First().CompanyName,
                    why = g.SelectMany(x => reasons.TryGetValue(x.Id, out var s) ? s : new HashSet<string>()).Distinct().OrderBy(x => x).ToArray(),
                    // the asset with the agent, then the one seen last, is the suggested one to keep
                    items = g.OrderByDescending(x => x.Agent).ThenByDescending(x => x.LastSeen ?? DateTime.MinValue).ThenBy(x => x.Id)
                        .Select(x => new { id = x.Id, tag = x.Tag, name = x.Name, hostname = x.Host, ip = x.Ip, serial = x.Serial, status = x.Status, source = x.Source, agent = x.Agent, lastSeen = x.LastSeen, createdAt = x.Created }).ToArray(),
                })
                .OrderByDescending(g => g.items.Length).ThenBy(g => g.company).Take(200).ToArray();
            return Results.Ok(new { groups });
        }).RequireAuthorization(p => p.RequireRole(AssetsAccess.Readers)).RequireModule(key).RequireAccess(key, Acts.Read);

        // keep one asset and retire the others of the same device
        r.MapPost("/api/assets/merge", async (JsonElement b, HttpContext http, ClaimsPrincipal u, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            if (b.ValueKind != JsonValueKind.Object || !b.TryGetProperty("keepId", out var kv) || !kv.TryGetInt64(out var keep) || !b.TryGetProperty("mergeIds", out var mv) || mv.ValueKind != JsonValueKind.Array)
                return Results.BadRequest(new { error = "bad_input", detail = "keepId and mergeIds are needed" });
            var merge = mv.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt64()).Where(x => x != keep).Distinct().ToList();
            if (merge.Count == 0 || merge.Count > 20) return Results.BadRequest(new { error = "bad_input", detail = "choose 1 to 20 assets to merge into the one you keep" });
            var scope = await AssetsAccess.ScopeAsync(http, Acts.Modify);
            var actor = u.FindFirstValue("email") ?? u.FindFirstValue("sub") ?? "unknown";
            await using var db = await Open(t, ct);
            foreach (var id in merge.Append(keep)) if (!await AssetsAccess.InScope(db, id, scope, ct)) return Results.NotFound();
            await using var tx = await db.BeginTransactionAsync(ct);
            async Task<(string Tag, int? Company, string Agent, string Serial, string Status)?> Load(long id)
            {
                await using var c = new NpgsqlCommand("select tag, company_id, agent_id, serial, status from assets.assets where id = @i for update", db, tx); P(c, "i", id);
                await using var rd = await c.ExecuteReaderAsync(ct);
                return await rd.ReadAsync(ct) ? (rd.GetString(0), rd.IsDBNull(1) ? null : rd.GetInt32(1), rd.GetString(2), rd.GetString(3), rd.GetString(4)) : null;
            }
            var k = await Load(keep); if (k == null) return Results.NotFound();
            if (k.Value.Status is "retired" or "lost") return Results.BadRequest(new { error = "bad_input", detail = "the asset to keep is retired; keep an active one" });
            var tags = new List<string>(); var hasAgent = k.Value.Agent != "";
            foreach (var mid in merge)
            {
                var m = await Load(mid); if (m == null) return Results.NotFound();
                if (m.Value.Company != k.Value.Company) return Results.BadRequest(new { error = "bad_input", detail = $"{m.Value.Tag} belongs to another customer; only assets of one customer can be merged" });
                if (m.Value.Agent != "" && hasAgent) return Results.BadRequest(new { error = "bad_input", detail = $"{m.Value.Tag} and {k.Value.Tag} both have an agent. Move one agent to the recycle bin first, then merge." });
                if (m.Value.Agent != "") return Results.BadRequest(new { error = "bad_input", detail = $"{m.Value.Tag} has the agent. Keep that one and merge the other into it." });
                // free the serial number first, the keeper may take it
                await using (var fs = new NpgsqlCommand("update assets.assets set serial = '' where id = @m", db, tx)) { P(fs, "m", mid); await fs.ExecuteNonQueryAsync(ct); }
                await using (var fill = new NpgsqlCommand(@"update assets.assets k set
                        type_id = coalesce(k.type_id, m.type_id), manufacturer = case when k.manufacturer = '' then m.manufacturer else k.manufacturer end, model = case when k.model = '' then m.model else k.model end,
                        serial = case when k.serial = '' then @sn else k.serial end, location = case when k.location = '' then m.location else k.location end,
                        assigned_to = case when k.assigned_to = '' then m.assigned_to else k.assigned_to end,
                        purchase_date = coalesce(k.purchase_date, m.purchase_date), purchase_cost = coalesce(k.purchase_cost, m.purchase_cost), warranty_end = coalesce(k.warranty_end, m.warranty_end),
                        site_id = coalesce(k.site_id, m.site_id), vendor_id = coalesce(k.vendor_id, m.vendor_id), po_number = case when k.po_number = '' then m.po_number else k.po_number end,
                        invoice_number = case when k.invoice_number = '' then m.invoice_number else k.invoice_number end, warranty_months = coalesce(k.warranty_months, m.warranty_months),
                        ip_address = case when k.ip_address = '' then m.ip_address else k.ip_address end, hostname = case when k.hostname = '' then m.hostname else k.hostname end,
                        notes = case when m.notes <> '' and position(m.notes in k.notes) = 0 then btrim(k.notes || E'\n' || m.notes) else k.notes end,
                        mac_addresses = (select coalesce(jsonb_agg(distinct x), '[]'::jsonb) from jsonb_array_elements_text(k.mac_addresses || m.mac_addresses) x),
                        monitored = k.monitored or m.monitored, updated_at = now(), updated_by = @by
                    from assets.assets m where k.id = @k and m.id = @m", db, tx))
                { P(fill, "k", keep); P(fill, "m", mid); P(fill, "sn", m.Value.Serial); P(fill, "by", actor); await fill.ExecuteNonQueryAsync(ct); }
                foreach (var table in new[] { "assets.amc", "assets.rentals" })
                    await using (var rp = new NpgsqlCommand($@"update {table} set asset_ids = (select coalesce(jsonb_agg(distinct x), '[]'::jsonb) from (select case when e = to_jsonb(@m::bigint) then to_jsonb(@k::bigint) else e end as x from jsonb_array_elements(asset_ids) e) s)
                        where asset_ids @> to_jsonb(@m::bigint)", db, tx)) { P(rp, "k", keep); P(rp, "m", mid); await rp.ExecuteNonQueryAsync(ct); }
                await using (var sc = new NpgsqlCommand("update assets.service_checks set asset_id = @k where asset_id = @m and lower(name) not in (select lower(name) from assets.service_checks where asset_id = @k)", db, tx)) { P(sc, "k", keep); P(sc, "m", mid); await sc.ExecuteNonQueryAsync(ct); }
                await using (var hs = new NpgsqlCommand("update assets.history set asset_id = @k where asset_id = @m", db, tx)) { P(hs, "k", keep); P(hs, "m", mid); await hs.ExecuteNonQueryAsync(ct); }
                await using (var rt = new NpgsqlCommand(@"update assets.assets set status = 'retired', hostname = '', dns_name = '', ip_address = '', mac_addresses = '[]', monitored = false, serial = '',
                        notes = btrim('Merged into ' || @kt || ' on ' || to_char(now(), 'YYYY-MM-DD') || ' by ' || @by || E'\n' || notes), updated_at = now(), updated_by = @by where id = @m", db, tx))
                { P(rt, "kt", k.Value.Tag); P(rt, "by", actor); P(rt, "m", mid); await rt.ExecuteNonQueryAsync(ct); }
                await AssetsDevice.History(db, keep, actor, "merged", JsonSerializer.Serialize(new { merged = m.Value.Tag }), ct, tx);
                await AssetsDevice.History(db, mid, actor, "merged", JsonSerializer.Serialize(new { into = k.Value.Tag }), ct, tx);
                tags.Add(m.Value.Tag);
            }
            await tx.CommitAsync(ct);
            await audit.WriteAsync("assets.merged", keep.ToString(), $"{string.Join(", ", tags)} into {k.Value.Tag}", ct: ct);
            return Results.Ok(new { ok = true, kept = k.Value.Tag, merged = tags });
        }).RequireAuthorization().RequireModule(key).RequireAccess(key, Acts.Modify);
    }
}

/// <summary>
/// Who may save an asset that only might be a duplicate (same IP address or same name) with "Add anyway". Nobody but an administrator,
/// unless an administrator names people or groups here. A sure duplicate (serial, MAC, host name) is refused for everyone.
/// </summary>
public static class DuplicateOverride
{
    public const string Key = "assets.duplicate_override";
    public sealed class Who { public List<Guid> Users { get; set; } = new(); public List<Guid> Groups { get; set; } = new(); }
    static readonly JsonSerializerOptions Opt = new(JsonSerializerDefaults.Web);

    public static async Task<Who> LoadAsync(ISettings s, CancellationToken ct)
    {
        var raw = await s.GetAsync(Key, ct);
        if (string.IsNullOrWhiteSpace(raw)) return new Who();
        try { return JsonSerializer.Deserialize<Who>(raw, Opt) ?? new Who(); } catch (JsonException) { return new Who(); }
    }

    public static async Task<bool> AllowedAsync(HttpContext h, CancellationToken ct)
    {
        var role = h.User.FindFirstValue("role") ?? "";
        if (role == "admin") return true;
        if (role == "customer" || !Guid.TryParse(h.User.FindFirstValue("sub"), out var me)) return false;
        var who = await LoadAsync(h.RequestServices.GetRequiredService<ISettings>(), ct);
        if (who.Users.Contains(me)) return true;
        if (who.Groups.Count == 0) return false;
        var access = h.RequestServices.GetService<IAccess>(); if (access == null) return false;
        var mine = await access.GroupsOfAsync(me, ct);
        return mine.Any(who.Groups.Contains);
    }

    /// <summary>The answer when someone without the right confirms a possible duplicate.</summary>
    public static IResult Refused(IEnumerable<DuplicateCheck.Match> dup) => Results.Json(new
    {
        error = "duplicate_override_forbidden",
        detail = "Possible duplicate: " + DuplicateCheck.Describe(dup) + ". Only an administrator, or someone an administrator allows, can add it anyway.",
        matches = dup
    }, statusCode: 403);

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        // the web asks whether to offer "Add anyway"
        r.MapGet("/api/assets/duplicates/override", async (HttpContext http, CancellationToken ct) => Results.Ok(new { allowed = await AllowedAsync(http, ct) }))
            .RequireAuthorization().RequireModule(key);

        r.MapGet("/api/assets/settings/duplicate-override", async (ISettings s, IUserDirectory users, IAccess access, CancellationToken ct) =>
        {
            var who = await LoadAsync(s, ct);
            var people = (await users.ListAsync(ct)).Where(u => u.Active && u.Role != "customer" && u.Role != "admin").OrderBy(u => u.Name).Select(u => new { id = u.Id, name = u.Name, email = u.Email, role = u.Role });
            var groups = (await access.GroupsAsync(ct)).OrderBy(g => g.Name).Select(g => new { id = g.Id, name = g.Name });
            return Results.Ok(new { users = who.Users, groups = who.Groups, choices = new { users = people, groups } });
        }).RequireAuthorization(p => p.RequireRole("admin")).RequireModule(key);

        r.MapPut("/api/assets/settings/duplicate-override", async (JsonElement body, ISettings s, IUserDirectory users, IAccess access, IAuditLog audit, CancellationToken ct) =>
        {
            List<Guid> Ids(string n)
            {
                var l = new List<Guid>();
                if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty(n, out var a) && a.ValueKind == JsonValueKind.Array)
                    foreach (var x in a.EnumerateArray()) if (x.ValueKind == JsonValueKind.String && Guid.TryParse(x.GetString(), out var g)) l.Add(g);
                return l.Distinct().ToList();
            }
            var known = (await users.ListAsync(ct)).Where(u => u.Role != "customer").Select(u => u.Id).ToHashSet();
            var knownGroups = (await access.GroupsAsync(ct)).Select(g => g.Id).ToHashSet();
            var who = new Who { Users = Ids("users"), Groups = Ids("groups") };
            if (who.Users.Any(u => !known.Contains(u))) return Results.BadRequest(new { error = "bad_input", detail = "Choose staff members only; customer users never get this right." });
            if (who.Groups.Any(g => !knownGroups.Contains(g))) return Results.BadRequest(new { error = "bad_input", detail = "One of the groups no longer exists." });
            await s.SetAsync(Key, JsonSerializer.Serialize(who, Opt), ct);
            await audit.WriteAsync("assets.duplicate_override_set", "settings", $"{who.Users.Count} people, {who.Groups.Count} groups", ct: ct);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(p => p.RequireRole("admin")).RequireModule(key);
    }
}
