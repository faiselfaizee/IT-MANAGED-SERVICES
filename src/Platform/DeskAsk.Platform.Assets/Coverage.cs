using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace DeskAsk.Platform.Assets;

/// <summary>
/// What covers an asset: the vendor it was bought from, the AMC contract and the rental contract it is under (each with its own vendor),
/// and whether the asset is watched by monitoring. Contracts list their assets, so putting an asset under a contract adds it to that list.
/// </summary>
public static class CoverageApi
{
    static async Task<NpgsqlConnection> Open(ITenantContext t, CancellationToken ct) { var db = new NpgsqlConnection(t.Require().ConnectionString); await db.OpenAsync(ct); return db; }
    static void P(NpgsqlCommand c, string n, object? v) => c.Parameters.AddWithValue(n, v ?? DBNull.Value);
    static IResult Bad(string detail) => Results.BadRequest(new { error = "bad_input", detail });

    sealed record Pick(int Id, string Title, int? VendorId, string Vendor, string ContractNo, string End, bool Current);

    static async Task<List<Pick>> Contracts(NpgsqlConnection db, string table, int? company, long assetId, CancellationToken ct)
    {
        // a contract that was renewed is replaced by its successor, so only the newest of a chain is offered
        var renewed = table == "amc" ? " and not exists (select 1 from assets.amc n where n.previous_id = m.id)" : "";
        await using var c = new NpgsqlCommand($@"select m.id, m.title, m.vendor_id, coalesce(v.name, ''), m.contract_no, m.end_date, m.asset_ids @> to_jsonb(@a::bigint)
            from assets.{table} m left join assets.vendors v on v.id = m.vendor_id
            where (m.company_id is not distinct from @c or m.company_id is null){renewed} and (m.end_date is null or m.end_date >= current_date - 30 or m.asset_ids @> to_jsonb(@a::bigint))
            order by m.end_date desc nulls first, m.id desc limit 100", db);
        P(c, "c", company); P(c, "a", assetId);
        var res = new List<Pick>(); await using var rd = await c.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            res.Add(new Pick(rd.GetInt32(0), rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetInt32(2), rd.GetString(3), rd.GetString(4), rd.IsDBNull(5) ? "" : rd.GetFieldValue<DateOnly>(5).ToString("yyyy-MM-dd"), rd.GetBoolean(6)));
        return res;
    }

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        r.MapGet("/api/assets/{id:long}/coverage", async (long id, HttpContext http, ITenantContext t, ISettings st, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            if (!await AssetsAccess.InScope(db, id, await AssetsAccess.ScopeAsync(http, Acts.Read), ct)) return Results.NotFound();
            int? company; bool monitored; string host, macs; int? vendorId; string vendor;
            await using (var c = new NpgsqlCommand(@"select a.company_id, a.monitored, a.hostname, (select coalesce(string_agg(x, ', '), '') from jsonb_array_elements_text(a.mac_addresses) x), a.vendor_id, coalesce(v.name, '')
                from assets.assets a left join assets.vendors v on v.id = a.vendor_id where a.id = @i", db))
            {
                P(c, "i", id); await using var rd = await c.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) return Results.NotFound();
                company = rd.IsDBNull(0) ? null : rd.GetInt32(0); monitored = rd.GetBoolean(1); host = rd.GetString(2); macs = rd.GetString(3); vendorId = rd.IsDBNull(4) ? null : rd.GetInt32(4); vendor = rd.GetString(5);
            }
            var amc = await Contracts(db, "amc", company, id, ct); var rent = await Contracts(db, "rentals", company, id, ct);
            var role = http.User.FindFirstValue("role") ?? "";
            var may = role is "admin" or "engineer";
            var mayAmc = may || (await AssetsDevice.AmcManage(http, st, ct)).Allowed;
            return Results.Ok(new
            {
                monitored, hostname = host, macAddress = macs, purchaseVendor = vendorId == null ? null : new { id = vendorId, name = vendor },
                amc = amc.FirstOrDefault(x => x.Current), rental = rent.FirstOrDefault(x => x.Current), amcChoices = amc, rentalChoices = rent,
                canEditAmc = mayAmc, canEditRental = may,
            });
        }).RequireAuthorization(p => p.RequireRole(AssetsAccess.Readers)).RequireModule(key).RequireAccess(key, Acts.Read);

        // put an asset under an AMC and/or a rental contract, or take it out (null)
        r.MapPut("/api/assets/{id:long}/coverage", async (long id, JsonElement b, HttpContext http, ClaimsPrincipal u, ITenantContext t, ISettings st, IAuditLog audit, CancellationToken ct) =>
        {
            if (b.ValueKind != JsonValueKind.Object) return Bad("the body must be a JSON object");
            var role = http.User.FindFirstValue("role") ?? "";
            var staff = role is "admin" or "engineer";
            await using var db = await Open(t, ct);
            if (!await AssetsAccess.InScope(db, id, await AssetsAccess.ScopeAsync(http, Acts.Modify), ct)) return Results.NotFound();
            int? company;
            await using (var c = new NpgsqlCommand("select company_id from assets.assets where id = @i", db)) { P(c, "i", id); var o = await c.ExecuteScalarAsync(ct); if (o == null) return Results.NotFound(); company = o is int ci ? ci : null; }
            var actor = u.FindFirstValue("email") ?? u.FindFirstValue("sub") ?? "unknown";
            var changes = new Dictionary<string, object>();
            await using var tx = await db.BeginTransactionAsync(ct);
            foreach (var (json, table) in new[] { ("amcId", "amc"), ("rentalId", "rentals") })
            {
                if (!b.TryGetProperty(json, out var el)) continue;
                if (table == "amc" ? !(staff || (await AssetsDevice.AmcManage(http, st, ct)).Allowed) : !staff)
                    return Results.Json(new { error = "forbidden", detail = table == "amc" ? "You may not change AMC contracts." : "Only staff can change rental contracts." }, statusCode: 403);
                int? target = null;
                if (el.ValueKind != JsonValueKind.Null)
                {
                    if (!el.TryGetInt32(out var tid)) return Bad(json + " must be a number or null");
                    await using var q = new NpgsqlCommand($"select company_id from assets.{table} where id = @i", db, tx); P(q, "i", tid);
                    var o = await q.ExecuteScalarAsync(ct);
                    if (o == null) return Bad("that contract does not exist");
                    if (o is int cc && company != null && cc != company) return Bad("that contract belongs to another customer");
                    target = tid;
                }
                string was;
                await using (var w = new NpgsqlCommand($"select coalesce(string_agg(title, ', '), '') from assets.{table} where asset_ids @> to_jsonb(@a::bigint) and (end_date is null or end_date >= current_date)", db, tx)) { P(w, "a", id); was = (string)(await w.ExecuteScalarAsync(ct))!; }
                // out of every current contract of that kind, then into the chosen one
                await using (var rm = new NpgsqlCommand($@"update assets.{table} set asset_ids = (select coalesce(jsonb_agg(e), '[]'::jsonb) from jsonb_array_elements(asset_ids) e where e <> to_jsonb(@a::bigint)), updated_at = now()
                    where asset_ids @> to_jsonb(@a::bigint) and (end_date is null or end_date >= current_date)", db, tx)) { P(rm, "a", id); await rm.ExecuteNonQueryAsync(ct); }
                string now = "";
                if (target != null)
                {
                    await using var ad = new NpgsqlCommand($"update assets.{table} set asset_ids = asset_ids || to_jsonb(@a::bigint), updated_at = now() where id = @t and not asset_ids @> to_jsonb(@a::bigint) returning title", db, tx);
                    P(ad, "a", id); P(ad, "t", target); now = (await ad.ExecuteScalarAsync(ct)) as string ?? "";
                    if (now == "") { await using var tt = new NpgsqlCommand($"select title from assets.{table} where id = @t", db, tx); P(tt, "t", target); now = (string?)await tt.ExecuteScalarAsync(ct) ?? ""; }
                }
                if (was != now) changes[table == "amc" ? "amc" : "rental"] = new { from = was, to = now };
            }
            if (changes.Count > 0) await AssetsDevice.History(db, id, actor, "coverage", JsonSerializer.Serialize(changes), ct, tx);
            await tx.CommitAsync(ct);
            if (changes.Count > 0) await audit.WriteAsync("assets.coverage_changed", id.ToString(), string.Join(",", changes.Keys), ct: ct);
            return Results.Ok(new { ok = true, changed = changes.Count });
        }).RequireAuthorization().RequireModule(key).RequireAccess(key, Acts.Modify);

        // choose which assets monitoring watches
        r.MapPost("/api/assets/bulk-monitor", async (JsonElement b, HttpContext http, ClaimsPrincipal u, ITenantContext t, IEntitlements ent, IAuditLog audit, CancellationToken ct) =>
        {
            if (b.ValueKind != JsonValueKind.Object || !b.TryGetProperty("ids", out var ia) || ia.ValueKind != JsonValueKind.Array || !b.TryGetProperty("monitored", out var mv) || mv.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return Bad("ids and monitored are needed");
            var ids = ia.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt64()).Distinct().Take(5000).ToArray();
            if (ids.Length == 0) return Bad("choose at least one asset");
            var scope = await AssetsAccess.ScopeAsync(http, Acts.Modify);
            await using var db = await Open(t, ct);
            if (mv.GetBoolean() && await MonitorLimit.RefuseAsync(db, ent, ids, ct) is { } refused) return refused;
            await using var c = new NpgsqlCommand("update assets.assets set monitored = @m, updated_at = now() where id = any(@ids) and (not @sc or company_id = any(@scope)) and monitored <> @m", db).Scoped(scope);
            P(c, "m", mv.GetBoolean()); P(c, "ids", ids);
            var n = await c.ExecuteNonQueryAsync(ct);
            await audit.WriteAsync("assets.monitoring_set", "", $"{(mv.GetBoolean() ? "on" : "off")} for {n}", ct: ct);
            return Results.Ok(new { ok = true, changed = n });
        }).RequireAuthorization().RequireModule(key).RequireAccess(key, Acts.Modify);
    }
}

/// <summary>The licence limit on monitored devices (monitoring:max_devices).</summary>
public static class MonitorLimit
{
    public static async Task<long> CountAsync(NpgsqlConnection db, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("select count(*) from assets.assets where status not in ('retired','lost') and monitored", db);
        return (long)(await c.ExecuteScalarAsync(ct))!;
    }

    /// <summary>A 402 answer when switching monitoring on for these assets would go over the limit; null when it fits.</summary>
    public static async Task<IResult?> RefuseAsync(NpgsqlConnection db, IEntitlements ent, long[] ids, CancellationToken ct)
    {
        var lim = (await ent.CurrentAsync(ct)).Limit(Modules.Monitoring, LicenseLimits.MaxDevices);
        if (lim == null) return null;
        await using var c = new NpgsqlCommand("select count(*) from assets.assets where id = any(@i) and not monitored and status not in ('retired','lost')", db);
        c.Parameters.AddWithValue("i", ids);
        var adding = (long)(await c.ExecuteScalarAsync(ct))!;
        return adding > 0 && await CountAsync(db, ct) + adding > lim ? LimitResult.Reached(Modules.Monitoring, LicenseLimits.MaxDevices, lim.Value, "monitored devices") : null;
    }
}

/// <summary>Assets in service and monitored devices, for the licence page.</summary>
public sealed class AssetsLicenseUsage : ILicenseUsage
{
    readonly ITenantContext tenant;
    public AssetsLicenseUsage(ITenantContext t) { tenant = t; }
    public async Task<IReadOnlyDictionary<string, long>> UsageAsync(CancellationToken ct = default)
    {
        await using var db = new NpgsqlConnection(tenant.Require().ConnectionString); await db.OpenAsync(ct);
        await using var c = new NpgsqlCommand("select count(*) filter (where status not in ('retired','lost')), count(*) filter (where status not in ('retired','lost') and monitored) from assets.assets", db);
        await using var rd = await c.ExecuteReaderAsync(ct); await rd.ReadAsync(ct);
        return new Dictionary<string, long> { [Modules.Assets + ":" + LicenseLimits.MaxAssets] = rd.GetInt64(0), [Modules.Monitoring + ":" + LicenseLimits.MaxDevices] = rd.GetInt64(1) };
    }
}
