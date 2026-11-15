using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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
                (cardinality(@macs) > 0 and mac_addresses ?| @macs), (@s <> '' and lower(serial) = lower(@s))
            from assets.assets
            where status not in ('retired', 'lost') and id <> @x and company_id is not distinct from @c
              and ((@h <> '' and (split_part(lower(hostname), '.', 1) = @h or split_part(lower(dns_name), '.', 1) = @h)) or (@ip <> '' and ip_address = @ip) or (@n <> '' and lower(name) = lower(@n))
                   or (cardinality(@macs) > 0 and mac_addresses ?| @macs) or (@s <> '' and lower(serial) = lower(@s)))
            order by id limit 20", db);
        c.Parameters.AddWithValue("c", (object?)company ?? DBNull.Value); c.Parameters.AddWithValue("x", excludeId);
        c.Parameters.AddWithValue("h", host); c.Parameters.AddWithValue("ip", (ip ?? "").Trim()); c.Parameters.AddWithValue("n", (name ?? "").Trim());
        c.Parameters.AddWithValue("macs", (macs ?? Array.Empty<string>()).ToArray()); c.Parameters.AddWithValue("s", sn);
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
