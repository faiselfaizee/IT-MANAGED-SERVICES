using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace DeskAsk.Platform.Assets;

/// <summary>
/// Performance data from agents (processor, memory, disks, uptime once a minute) and the live device state the Monitoring module reads.
/// The latest sample is kept on the asset for fast dashboards; minute points are kept two days and hourly figures 90 days (see the janitor).
/// </summary>
public sealed class DeviceHealth : IDeviceHealth
{
    readonly ITenantContext tenant; readonly IConfiguration cfg;
    public DeviceHealth(ITenantContext t, IConfiguration c) { tenant = t; cfg = c; }
    async Task<NpgsqlConnection> Open(CancellationToken ct) { var db = new NpgsqlConnection(tenant.Require().ConnectionString); await db.OpenAsync(ct); return db; }

    /// <summary>How often agents send a sample (seconds); 60 unless Monitoring:MetricsSeconds says otherwise.</summary>
    public static int Every(IConfiguration cfg) => int.TryParse(cfg["Monitoring:MetricsSeconds"], out var s) && s is >= 15 and <= 3600 ? s : 60;

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        r.MapPost("/agent/v1/metrics", async (JsonElement b, HttpContext http, ITenantContext t, IConfiguration cfg, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            var ag = await AgentsApi.AuthAgent(http, db, ct); if (ag == null) return Results.Json(new { error = "agent_auth" }, statusCode: 401);
            if (ag.Value.AssetId == 0) return Results.Ok(new { every = Every(cfg) });
            double? Num(string n) => b.ValueKind == JsonValueKind.Object && b.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
            var cpu = Num("cpu") is double c0 and >= 0 and <= 100 ? Math.Round(c0, 1) : (double?)null;
            var memTotal = (long)Math.Max(0, Num("mem_total") ?? 0); var memUsed = (long)Math.Max(0, Num("mem_used") ?? 0);
            double? mem = memTotal > 0 ? Math.Round(Math.Min(100, memUsed * 100.0 / memTotal), 1) : null;
            var disks = new List<object>();
            if (b.ValueKind == JsonValueKind.Object && b.TryGetProperty("disks", out var ds) && ds.ValueKind == JsonValueKind.Array)
                foreach (var d in ds.EnumerateArray().Take(64))
                {
                    if (d.ValueKind != JsonValueKind.Object) continue;
                    var mount = AgentsApi.Clip(AgentsApi.Str(d, "mount"), 120); if (mount == "") continue;
                    var total = d.TryGetProperty("total", out var tv) && tv.ValueKind == JsonValueKind.Number ? tv.GetDouble() : 0;
                    var free = d.TryGetProperty("free", out var fv) && fv.ValueKind == JsonValueKind.Number ? fv.GetDouble() : 0;
                    if (total <= 0) continue;
                    disks.Add(new { mount, total = (long)total, free = (long)Math.Min(free, total), pct = Math.Round(Math.Clamp(free, 0, total) * 100 / total, 1) });
                }
            var dj = JsonSerializer.Serialize(disks);
            var up = (long)Math.Max(0, Num("uptime") ?? 0);
            await using (var c = new NpgsqlCommand(@"insert into assets.metrics (asset_id, at, cpu, mem, disks) values (@a, date_trunc('second', now()), @c, @m, @d::jsonb) on conflict do nothing;
                update assets.assets set m_at = now(), m_cpu = @c, m_mem = @m, m_mem_total = @mt, m_disks = @d::jsonb, m_uptime = @u where id = @a;
                update assets.agents set last_seen = now() where id = @g", db))
            {
                AgentsApi.P(c, "a", ag.Value.AssetId); AgentsApi.P(c, "c", cpu == null ? null : (float)cpu.Value); AgentsApi.P(c, "m", mem == null ? null : (float)mem.Value);
                AgentsApi.P(c, "mt", memTotal); AgentsApi.P(c, "d", dj); AgentsApi.P(c, "u", up); AgentsApi.P(c, "g", ag.Value.Id);
                await c.ExecuteNonQueryAsync(ct);
            }
            return Results.Ok(new { every = Every(cfg) });
        }).RequireModule(key);
    }

    public async Task<IReadOnlyList<DeviceState>> SnapshotAsync(IReadOnlyList<int>? companies, long? assetId = null, CancellationToken ct = default)
    {
        await using var db = await Open(ct);
        var where = "a.status not in ('retired','lost') and (not @sc or a.company_id = any(@scope)) and (@id = 0 or a.id = @id)";
        void Bind(NpgsqlCommand c) { AssetsAccess.Bind(c, companies); AgentsApi.P(c, "id", assetId ?? 0); }

        var checks = new Dictionary<long, List<CheckState>>();
        await using (var c = new NpgsqlCommand($@"select s.asset_id, s.id, s.name, s.type, s.last_ok, s.fails, s.last_days, s.last_detail, s.last_at
            from assets.service_checks s join assets.assets a on a.id = s.asset_id where s.enabled and {where} order by s.id", db))
        {
            Bind(c); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                if (!checks.TryGetValue(rd.GetInt64(0), out var l)) checks[rd.GetInt64(0)] = l = new();
                l.Add(new CheckState(rd.GetInt32(1), rd.GetString(2), rd.GetString(3), rd.IsDBNull(4) ? null : rd.GetBoolean(4), rd.GetInt32(5),
                    rd.IsDBNull(6) ? null : rd.GetFloat(6), rd.GetString(7), rd.IsDBNull(8) ? null : rd.GetDateTime(8)));
            }
        }
        // parts reported not OK (the same test as the Assets dashboard's "hardware not OK"), with the table they come from
        var faults = new Dictionary<long, List<PartFault>>();
        await using (var c = new NpgsqlCommand($@"select a.id, kv.key, coalesce(e->>'name', e->>'label', e->>'description', ''), coalesce(nullif(e->>'state', ''), e->>'health', '')
            from assets.assets a, jsonb_each(coalesce(a.hw_extra, '{{}}') || coalesce(a.bmc_extra, '{{}}')) kv,
                 jsonb_array_elements(case when jsonb_typeof(kv.value) = 'array' then kv.value else '[]'::jsonb end) e
            where {where} and kv.key <> 'vms' and jsonb_typeof(e) = 'object' and (e->>'state' in ('Critical','Warning') or e->>'health' = 'red')", db))
        {
            Bind(c); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                if (!faults.TryGetValue(rd.GetInt64(0), out var l)) faults[rd.GetInt64(0)] = l = new();
                if (l.Count < 50) l.Add(new PartFault(rd.GetString(1), rd.GetString(2), rd.GetString(3) == "red" ? "Critical" : rd.GetString(3)));
            }
        }
        var outList = new List<DeviceState>();
        await using (var c = new NpgsqlCommand($@"select a.id, a.tag, a.name, a.company_id, coalesce(co.name, ''), {AssetDash.KindSql}, a.ip_address, trim(a.os_name || ' ' || a.os_version),
                g.id is not null, coalesce(g.version, ''), g.last_seen, case when a.collector_id <> '' and a.monitor <> '' then a.check_up end, a.check_down_at, a.check_rtt, a.checked_at,
                a.m_at, a.m_cpu, a.m_mem, a.m_mem_total, a.m_uptime, a.m_disks::text, coalesce(a.hw_extra->'datastores', '[]')::text
            from assets.assets a left join assets.types t on t.id = a.type_id left join assets.companies co on co.id = a.company_id
            left join assets.agents g on g.id = a.agent_id and g.deleted_at is null
            where {where} order by a.id", db))
        {
            Bind(c); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var id = rd.GetInt64(0);
                var mAt = rd.IsDBNull(15) ? (DateTime?)null : rd.GetDateTime(15);
                var disks = new List<DiskUse>();
                if (mAt != null && mAt > DateTime.UtcNow.AddDays(-1))
                    foreach (var d in JsonDocument.Parse(rd.GetString(20)).RootElement.EnumerateArray())
                        disks.Add(new DiskUse(AgentsApi.Str(d, "mount") ?? "", d.GetProperty("pct").GetDouble(), d.GetProperty("free").GetInt64(), d.GetProperty("total").GetInt64()));
                foreach (var d in JsonDocument.Parse(rd.GetString(21)).RootElement.EnumerateArray())
                {
                    if (d.ValueKind != JsonValueKind.Object) continue;
                    var cap = d.TryGetProperty("capacity_bytes", out var cv) && cv.ValueKind == JsonValueKind.Number ? cv.GetDouble() : 0;
                    var free = d.TryGetProperty("free_bytes", out var fv) && fv.ValueKind == JsonValueKind.Number ? fv.GetDouble() : 0;
                    if (cap > 0) disks.Add(new DiskUse("Datastore " + (AgentsApi.Str(d, "name") ?? ""), Math.Round(Math.Clamp(free, 0, cap) * 100 / cap, 1), (long)free, (long)cap));
                }
                outList.Add(new DeviceState(id, rd.GetString(1), rd.GetString(2), rd.IsDBNull(3) ? null : rd.GetInt32(3), rd.IsDBNull(3) ? "No customer" : rd.GetString(4), rd.GetString(5), rd.GetString(6), rd.GetString(7),
                    rd.GetBoolean(8), rd.GetString(9), rd.IsDBNull(10) ? null : rd.GetDateTime(10), rd.IsDBNull(11) ? null : rd.GetBoolean(11), rd.IsDBNull(12) ? null : rd.GetDateTime(12), rd.GetInt32(13), rd.IsDBNull(14) ? null : rd.GetDateTime(14),
                    mAt, rd.IsDBNull(16) ? null : rd.GetFloat(16), rd.IsDBNull(17) ? null : rd.GetFloat(17), rd.GetInt64(18), rd.GetInt64(19), disks,
                    checks.TryGetValue(id, out var ck) ? ck : Array.Empty<CheckState>(), faults.TryGetValue(id, out var fl) ? fl : Array.Empty<PartFault>()));
            }
        }
        return outList;
    }

    public async Task<IReadOnlyDictionary<long, Sustained>> SustainedAsync(int minutes, CancellationToken ct = default)
    {
        await using var db = await Open(ct);
        await using var c = new NpgsqlCommand("select asset_id, min(cpu), min(mem), count(*), min(at) from assets.metrics where at > now() - make_interval(mins => @m) group by asset_id", db);
        AgentsApi.P(c, "m", Math.Clamp(minutes, 1, 24 * 60));
        var o = new Dictionary<long, Sustained>(); await using var rd = await c.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) o[rd.GetInt64(0)] = new Sustained(rd.IsDBNull(1) ? null : rd.GetFloat(1), rd.IsDBNull(2) ? null : rd.GetFloat(2), (int)rd.GetInt64(3), rd.GetDateTime(4));
        return o;
    }

    public async Task<IReadOnlyList<MetricPoint>> SeriesAsync(long assetId, DateTime from, DateTime to, bool hourly, CancellationToken ct = default)
    {
        await using var db = await Open(ct);
        await using var c = new NpgsqlCommand(hourly
            ? "select hour, cpu_avg, cpu_max, mem_avg, mem_max, disk_free_min from assets.metrics_hour where asset_id = @a and hour >= @f and hour <= @t order by hour limit 3000"
            : "select at, cpu, cpu, mem, mem, (select min((d->>'pct')::real) from jsonb_array_elements(disks) d) from assets.metrics where asset_id = @a and at >= @f and at <= @t order by at limit 3000", db);
        AgentsApi.P(c, "a", assetId); AgentsApi.P(c, "f", from.ToUniversalTime()); AgentsApi.P(c, "t", to.ToUniversalTime());
        var o = new List<MetricPoint>(); await using var rd = await c.ExecuteReaderAsync(ct);
        double? F(int i) => rd.IsDBNull(i) ? null : Math.Round(rd.GetFloat(i), 1);
        while (await rd.ReadAsync(ct)) o.Add(new MetricPoint(rd.GetDateTime(0), F(1), F(2), F(3), F(4), F(5)));
        return o;
    }
}
