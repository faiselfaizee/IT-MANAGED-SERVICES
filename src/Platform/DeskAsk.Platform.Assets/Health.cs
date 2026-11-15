using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
            // services and programs Monitoring asked about, and new Windows event log errors
            if (b.ValueKind == JsonValueKind.Object && (b.TryGetProperty("services", out _) || b.TryGetProperty("processes", out _)))
            {
                var items = new List<object>();
                foreach (var sv in Items(b, "services", 50))
                    items.Add(new { name = AgentsApi.Clip(AgentsApi.Str(sv, "name"), 120), kind = "service", running = Bool(sv, "running"), state = AgentsApi.Clip(AgentsApi.Str(sv, "state"), 20), count = Bool(sv, "running") ? 1 : 0 });
                foreach (var pr in Items(b, "processes", 50))
                    items.Add(new { name = AgentsApi.Clip(AgentsApi.Str(pr, "name"), 120), kind = "process", running = Bool(pr, "running"), state = Bool(pr, "running") ? "running" : "not running", count = pr.TryGetProperty("count", out var cn) && cn.ValueKind == JsonValueKind.Number ? cn.GetInt32() : 0 });
                await using var c = new NpgsqlCommand("update assets.assets set m_services = @s::jsonb, m_services_at = now() where id = @a", db);
                AgentsApi.P(c, "s", JsonSerializer.Serialize(items)); AgentsApi.P(c, "a", ag.Value.AssetId); await c.ExecuteNonQueryAsync(ct);
            }
            var evs = Items(b, "events", 100).Select(e => (ag.Value.AssetId, Kind: "eventlog", Sev: Math.Clamp(e.TryGetProperty("severity", out var sv) && sv.ValueKind == JsonValueKind.Number ? sv.GetInt32() : 3, 0, 7),
                Source: AgentsApi.Clip(AgentsApi.Str(e, "source"), 120), Id: e.TryGetProperty("id", out var iv) && iv.ValueKind == JsonValueKind.Number ? iv.GetInt32() : 0,
                Text: AgentsApi.Clip(((AgentsApi.Str(e, "log") ?? "") is { Length: > 0 } lg ? lg + ": " : "") + (AgentsApi.Str(e, "text") ?? ""), 1000))).ToList();
            await AddEvents(db, evs, ct);
            var watch = http.RequestServices.GetService<IAgentWatch>();
            AgentWatch? w = null;
            if (watch != null)
            {
                string kind; int? company;
                await using (var c = new NpgsqlCommand($"select {AssetDash.KindSql}, a.company_id from assets.assets a left join assets.types t on t.id = a.type_id where a.id = @a", db))
                {
                    AgentsApi.P(c, "a", ag.Value.AssetId); await using var rd = await c.ExecuteReaderAsync(ct); await rd.ReadAsync(ct);
                    kind = rd.GetString(0); company = rd.IsDBNull(1) ? null : rd.GetInt32(1);
                }
                w = await watch.ForAsync(ag.Value.AssetId, kind, company, ct);
            }
            return Results.Ok(new { every = Every(cfg), watch = new { services = w?.Services ?? Array.Empty<string>(), processes = w?.Processes ?? Array.Empty<string>() }, events = w?.Events ?? false });
        }).RequireModule(key);
    }

    static IEnumerable<JsonElement> Items(JsonElement b, string name, int max) =>
        b.ValueKind == JsonValueKind.Object && b.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Take(max) : Enumerable.Empty<JsonElement>();
    static bool Bool(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
    /// <summary>A time stored in JSON, in ISO form or as Postgres writes it.</summary>
    static DateTime? When(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;
    static double D(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    /// <summary>Stores syslog messages, traps and event log entries.</summary>
    internal static async Task AddEvents(NpgsqlConnection db, IReadOnlyList<(long Asset, string Kind, int Sev, string Source, int Id, string Text)> evs, CancellationToken ct)
    {
        if (evs.Count == 0) return;
        await using var c = new NpgsqlCommand("insert into assets.events (asset_id, kind, severity, source, event_id, text) select * from unnest(@a, @k, @s, @src, @i, @t)", db);
        AgentsApi.P(c, "a", evs.Select(e => e.Asset).ToArray()); AgentsApi.P(c, "k", evs.Select(e => e.Kind).ToArray()); AgentsApi.P(c, "s", evs.Select(e => e.Sev).ToArray());
        AgentsApi.P(c, "src", evs.Select(e => e.Source).ToArray()); AgentsApi.P(c, "i", evs.Select(e => e.Id).ToArray()); AgentsApi.P(c, "t", evs.Select(e => e.Text).ToArray());
        await c.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// A network device's ports from a traffic reading: the latest state goes on the asset (with when a port went down), the rates into
    /// the 5-minute history.
    /// </summary>
    internal static async Task AddTraffic(NpgsqlConnection db, long assetId, JsonElement ports, CancellationToken ct)
    {
        var prevDown = new Dictionary<string, DateTime>();
        await using (var c = new NpgsqlCommand("select m_ports::text from assets.assets where id = @a", db))
        {
            AgentsApi.P(c, "a", assetId);
            if (await c.ExecuteScalarAsync(ct) is string pj)
                foreach (var p in JsonDocument.Parse(pj).RootElement.EnumerateArray())
                    if (When(p, "downAt") is DateTime dt) prevDown[AgentsApi.Str(p, "index") ?? ""] = dt;
        }
        var now = DateTime.UtcNow; var latest = new List<object>();
        var idx = new List<string>(); var ip = new List<float>(); var op = new List<float>(); var ib = new List<double>(); var ob = new List<double>(); var er = new List<float>(); var di = new List<float>();
        foreach (var p in ports.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Take(2048))
        {
            var index = AgentsApi.Clip(AgentsApi.Str(p, "index"), 40); if (index == "") continue;
            var up = AgentsApi.Str(p, "oper") == "up"; var adminUp = AgentsApi.Str(p, "admin") != "down"; var rates = Bool(p, "rates");
            DateTime? downAt = !up && adminUp ? (prevDown.TryGetValue(index, out var pd) ? pd : now) : null;
            double Pct(string n) => Math.Round(Math.Clamp(D(p, n), 0, 100), 1);
            latest.Add(new { index, name = AgentsApi.Clip(AgentsApi.Str(p, "name"), 80), alias = AgentsApi.Clip(AgentsApi.Str(p, "alias"), 120), kind = AgentsApi.Clip(AgentsApi.Str(p, "kind"), 30),
                up, adminUp, mbps = (long)Math.Max(0, D(p, "speed_mbps")), rates, inPct = Pct("in_pct"), outPct = Pct("out_pct"), inBps = Math.Max(0, D(p, "in_bps")), outBps = Math.Max(0, D(p, "out_bps")),
                errors = Math.Max(0, D(p, "errors")), discards = Math.Max(0, D(p, "discards")), downAt });
            if (rates && up) { idx.Add(index); ip.Add((float)Pct("in_pct")); op.Add((float)Pct("out_pct")); ib.Add(Math.Max(0, D(p, "in_bps"))); ob.Add(Math.Max(0, D(p, "out_bps"))); er.Add((float)Math.Max(0, D(p, "errors"))); di.Add((float)Math.Max(0, D(p, "discards"))); }
        }
        await using (var c = new NpgsqlCommand("update assets.assets set m_ports = @p::jsonb, m_ports_at = now() where id = @a", db))
        { AgentsApi.P(c, "p", JsonSerializer.Serialize(latest)); AgentsApi.P(c, "a", assetId); await c.ExecuteNonQueryAsync(ct); }
        if (idx.Count == 0) return;
        await using (var c = new NpgsqlCommand(@"insert into assets.ifstats (asset_id, if_index, at, in_pct, out_pct, in_bps, out_bps, errors, discards)
            select @a, i, date_trunc('second', now()), ip, op, ib, ob, er, di from unnest(@i, @ip, @op, @ib, @ob, @er, @di) as x(i, ip, op, ib, ob, er, di) on conflict do nothing", db))
        {
            AgentsApi.P(c, "a", assetId); AgentsApi.P(c, "i", idx.ToArray()); AgentsApi.P(c, "ip", ip.ToArray()); AgentsApi.P(c, "op", op.ToArray()); AgentsApi.P(c, "ib", ib.ToArray());
            AgentsApi.P(c, "ob", ob.ToArray()); AgentsApi.P(c, "er", er.ToArray()); AgentsApi.P(c, "di", di.ToArray()); await c.ExecuteNonQueryAsync(ct);
        }
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
        var watchSet = new Dictionary<(long, string), bool>();
        await using (var c = new NpgsqlCommand($"select w.asset_id, w.if_index, w.watched from assets.port_watch w join assets.assets a on a.id = w.asset_id where {where}", db))
        {
            Bind(c); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) watchSet[(rd.GetInt64(0), rd.GetString(1))] = rd.GetBoolean(2);
        }
        var outList = new List<DeviceState>();
        await using (var c = new NpgsqlCommand($@"select a.id, a.tag, a.name, a.company_id, coalesce(co.name, ''), {AssetDash.KindSql}, a.ip_address, trim(a.os_name || ' ' || a.os_version),
                g.id is not null, coalesce(g.version, ''), g.last_seen, case when a.collector_id <> '' and a.monitor <> '' then a.check_up end, a.check_down_at, a.check_rtt, a.checked_at,
                a.m_at, a.m_cpu, a.m_mem, a.m_mem_total, a.m_uptime, a.m_disks::text, coalesce(a.hw_extra->'datastores', '[]')::text,
                a.m_ports::text, a.m_ports_at, a.m_services::text, a.m_services_at
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
                var ports = new List<PortState>();
                foreach (var p in JsonDocument.Parse(rd.GetString(22)).RootElement.EnumerateArray())
                {
                    var index = AgentsApi.Str(p, "index") ?? ""; var alias = AgentsApi.Str(p, "alias") ?? "";
                    DateTime? downAt = When(p, "downAt");
                    ports.Add(new PortState(index, AgentsApi.Str(p, "name") ?? "", alias, AgentsApi.Str(p, "kind") ?? "", Bool(p, "up"), Bool(p, "adminUp"), (long)D(p, "mbps"), Bool(p, "rates"),
                        D(p, "inPct"), D(p, "outPct"), D(p, "inBps"), D(p, "outBps"), D(p, "errors"), D(p, "discards"), watchSet.TryGetValue((id, index), out var wv) ? wv : alias.Trim() != "", downAt));
                }
                var watched = new List<WatchedItem>();
                foreach (var w in JsonDocument.Parse(rd.GetString(24)).RootElement.EnumerateArray())
                    watched.Add(new WatchedItem(AgentsApi.Str(w, "name") ?? "", AgentsApi.Str(w, "kind") ?? "", Bool(w, "running"), AgentsApi.Str(w, "state") ?? "", (int)D(w, "count")));
                outList.Add(new DeviceState(id, rd.GetString(1), rd.GetString(2), rd.IsDBNull(3) ? null : rd.GetInt32(3), rd.IsDBNull(3) ? "No customer" : rd.GetString(4), rd.GetString(5), rd.GetString(6), rd.GetString(7),
                    rd.GetBoolean(8), rd.GetString(9), rd.IsDBNull(10) ? null : rd.GetDateTime(10), rd.IsDBNull(11) ? null : rd.GetBoolean(11), rd.IsDBNull(12) ? null : rd.GetDateTime(12), rd.GetInt32(13), rd.IsDBNull(14) ? null : rd.GetDateTime(14),
                    mAt, rd.IsDBNull(16) ? null : rd.GetFloat(16), rd.IsDBNull(17) ? null : rd.GetFloat(17), rd.GetInt64(18), rd.GetInt64(19), disks,
                    checks.TryGetValue(id, out var ck) ? ck : Array.Empty<CheckState>(), faults.TryGetValue(id, out var fl) ? fl : Array.Empty<PartFault>(),
                    ports, rd.IsDBNull(23) ? null : rd.GetDateTime(23), watched, rd.IsDBNull(25) ? null : rd.GetDateTime(25)));
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

    public async Task<IReadOnlyDictionary<(long, string), PortSustained>> SustainedPortsAsync(int minutes, CancellationToken ct = default)
    {
        await using var db = await Open(ct);
        await using var c = new NpgsqlCommand("select asset_id, if_index, min(greatest(in_pct, out_pct)), count(*), min(at) from assets.ifstats where at > now() - make_interval(mins => @m) group by 1, 2", db);
        AgentsApi.P(c, "m", Math.Clamp(minutes, 1, 1440));
        var o = new Dictionary<(long, string), PortSustained>(); await using var rd = await c.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) o[(rd.GetInt64(0), rd.GetString(1))] = new PortSustained(rd.GetFloat(2), (int)rd.GetInt64(3), rd.GetDateTime(4));
        return o;
    }

    public async Task<IReadOnlyList<PortPoint>> PortSeriesAsync(long assetId, string port, DateTime from, DateTime to, bool hourly, CancellationToken ct = default)
    {
        await using var db = await Open(ct);
        await using var c = new NpgsqlCommand(hourly
            ? "select hour, in_avg, out_avg, in_max, out_max, in_bps, out_bps, errors from assets.ifstats_hour where asset_id = @a and if_index = @i and hour >= @f and hour <= @t order by hour limit 3000"
            : "select at, in_pct, out_pct, in_pct, out_pct, in_bps, out_bps, errors from assets.ifstats where asset_id = @a and if_index = @i and at >= @f and at <= @t order by at limit 3000", db);
        AgentsApi.P(c, "a", assetId); AgentsApi.P(c, "i", port); AgentsApi.P(c, "f", from.ToUniversalTime()); AgentsApi.P(c, "t", to.ToUniversalTime());
        var o = new List<PortPoint>(); await using var rd = await c.ExecuteReaderAsync(ct);
        double F(int i) => rd.IsDBNull(i) ? 0 : Math.Round(Convert.ToDouble(rd.GetValue(i)), 1);
        while (await rd.ReadAsync(ct)) o.Add(new PortPoint(rd.GetDateTime(0), F(1), F(2), F(3), F(4), F(5), F(6), F(7)));
        return o;
    }

    public async Task SetPortWatchAsync(long assetId, string port, bool? watched, CancellationToken ct = default)
    {
        await using var db = await Open(ct);
        await using var c = new NpgsqlCommand(watched == null ? "delete from assets.port_watch where asset_id = @a and if_index = @i"
            : "insert into assets.port_watch (asset_id, if_index, watched) values (@a, @i, @w) on conflict (asset_id, if_index) do update set watched = excluded.watched", db);
        AgentsApi.P(c, "a", assetId); AgentsApi.P(c, "i", port); if (watched != null) AgentsApi.P(c, "w", watched.Value);
        await c.ExecuteNonQueryAsync(ct);
    }

    static DeviceEvent Ev(NpgsqlDataReader rd) => new(rd.GetInt64(0), rd.GetInt64(1), rd.GetDateTime(2), rd.GetString(3), rd.GetInt32(4), rd.GetString(5), rd.GetInt32(6), rd.GetString(7));

    public async Task<IReadOnlyList<DeviceEvent>> RecentEventsAsync(int minutes, int maxSeverity, int max, CancellationToken ct = default)
    {
        await using var db = await Open(ct);
        await using var c = new NpgsqlCommand("select id, asset_id, at, kind, severity, source, event_id, text from assets.events where at > now() - make_interval(mins => @m) and severity <= @s order by id desc limit @n", db);
        AgentsApi.P(c, "m", Math.Clamp(minutes, 1, 1440)); AgentsApi.P(c, "s", maxSeverity); AgentsApi.P(c, "n", Math.Clamp(max, 1, 50000));
        var o = new List<DeviceEvent>(); await using var rd = await c.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) o.Add(Ev(rd));
        return o;
    }

    public async Task<(long Total, IReadOnlyList<DeviceEvent> Items)> ListEventsAsync(IReadOnlyList<int>? companies, long? assetId, string kind, int maxSeverity, string search, int skip, int take, CancellationToken ct = default)
    {
        await using var db = await Open(ct);
        await using var c = new NpgsqlCommand(@"select e.id, e.asset_id, e.at, e.kind, e.severity, e.source, e.event_id, e.text, count(*) over() from assets.events e join assets.assets a on a.id = e.asset_id
            where (not @sc or a.company_id = any(@scope)) and (@id = 0 or e.asset_id = @id) and (@k = '' or e.kind = @k) and e.severity <= @s
              and (@q = '' or e.text ilike @l or e.source ilike @l or a.name ilike @l)
            order by e.id desc offset @sk limit @tk", db);
        AssetsAccess.Bind(c, companies); AgentsApi.P(c, "id", assetId ?? 0); AgentsApi.P(c, "k", kind); AgentsApi.P(c, "s", maxSeverity);
        AgentsApi.P(c, "q", search); AgentsApi.P(c, "l", "%" + search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"); AgentsApi.P(c, "sk", Math.Max(0, skip)); AgentsApi.P(c, "tk", Math.Clamp(take, 1, 500));
        var o = new List<DeviceEvent>(); long total = 0; await using var rd = await c.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) { o.Add(Ev(rd)); total = rd.GetInt64(8); }
        return (total, o);
    }

    public async Task<IReadOnlyDictionary<long, DeviceAverages>> AveragesAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        await using var db = await Open(ct);
        await using var c = new NpgsqlCommand(@"select asset_id, sum(cpu_avg * n) / nullif(sum(case when cpu_avg is null then 0 else n end), 0), max(cpu_max),
                sum(mem_avg * n) / nullif(sum(case when mem_avg is null then 0 else n end), 0), max(mem_max), min(disk_free_min)
            from assets.metrics_hour where hour >= @f and hour < @t group by asset_id", db);
        AgentsApi.P(c, "f", from.ToUniversalTime()); AgentsApi.P(c, "t", to.ToUniversalTime());
        var o = new Dictionary<long, DeviceAverages>(); await using var rd = await c.ExecuteReaderAsync(ct);
        double? F(int i) => rd.IsDBNull(i) ? null : Math.Round(Convert.ToDouble(rd.GetValue(i)), 1);
        while (await rd.ReadAsync(ct)) o[rd.GetInt64(0)] = new DeviceAverages(F(1), F(2), F(3), F(4), F(5));
        return o;
    }
}

/// <summary>Restarting a service through the device's agent, for Monitoring rules that ask for it. Runs as an agent job (shown under the asset's commands).</summary>
public sealed class DeviceActions : IDeviceActions
{
    readonly ITenantContext tenant; readonly AgentHub hub; readonly IAuditLog audit; readonly IConfiguration cfg;
    public DeviceActions(ITenantContext t, AgentHub h, IAuditLog a, IConfiguration c) { tenant = t; hub = h; audit = a; cfg = c; }

    public async Task<string?> SetPasswordAsync(long assetId, string account, string target, string password, string by, CancellationToken ct = default)
    {
        if (!SecretHandoff.ValidAccount(account) || target is not ("local" or "domain" or "linux") || string.IsNullOrEmpty(password) || password.Length > 256) return null;
        await using var db = new NpgsqlConnection(tenant.Require().ConnectionString); await db.OpenAsync(ct);
        string agent, platform, name;
        await using (var c = new NpgsqlCommand("select g.id, g.platform, a.name from assets.assets a join assets.agents g on g.id = a.agent_id and g.deleted_at is null and g.uninstall_at is null where a.id = @a", db))
        {
            AgentsApi.P(c, "a", assetId); await using var rd = await c.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return null;
            agent = rd.GetString(0); platform = rd.GetString(1); name = rd.GetString(2);
        }
        if ((platform == "windows") != (target != "linux")) return null;
        var id = "pw" + AgentsApi.Rand(24);
        await using (var c = new NpgsqlCommand("insert into assets.secret_handoff (id, asset_id, agent_id, account, target, secret_sealed, by_name) values (@i, @a, @g, @u, @t, @s, @b)", db))
        {
            AgentsApi.P(c, "i", id); AgentsApi.P(c, "a", assetId); AgentsApi.P(c, "g", agent); AgentsApi.P(c, "u", account); AgentsApi.P(c, "t", target);
            AgentsApi.P(c, "s", SecretBox.Seal(cfg, tenant.Require().Id, password)); AgentsApi.P(c, "b", by); await c.ExecuteNonQueryAsync(ct);
        }
        await AgentsApi.Enqueue(db, hub, tenant, agent, "setpw", new { id }, ct);
        await audit.WriteAsync("assets.password_change_sent", assetId.ToString(), $"new password for {target} account {account} on {name}, asked by {by}", ct: ct);
        return id;
    }

    public async Task<PasswordChange?> PasswordChangeAsync(string handle, CancellationToken ct = default)
    {
        await using var db = new NpgsqlConnection(tenant.Require().ConnectionString); await db.OpenAsync(ct);
        await using var c = new NpgsqlCommand("select state, error, created_at, fetched_at, done_at from assets.secret_handoff where id = @i", db); AgentsApi.P(c, "i", handle);
        await using var rd = await c.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return new(rd.GetString(0), rd.GetString(1), rd.GetDateTime(2), rd.IsDBNull(3) ? null : rd.GetDateTime(3), rd.IsDBNull(4) ? null : rd.GetDateTime(4));
    }

    public async Task<bool> CancelPasswordChangeAsync(string handle, CancellationToken ct = default)
    {
        await using var db = new NpgsqlConnection(tenant.Require().ConnectionString); await db.OpenAsync(ct);
        await using var c = new NpgsqlCommand("update assets.secret_handoff set state = 'cancelled', secret_sealed = null, done_at = now() where id = @i and state = 'queued'", db); AgentsApi.P(c, "i", handle);
        return await c.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<string?> RestartServiceAsync(long assetId, string service, string by, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(service) || service.Length > 120 || service.IndexOfAny(new[] { '\'', '"', '`', '$', ';', '&', '|', '\n', '\r' }) >= 0) return null;
        await using var db = new NpgsqlConnection(tenant.Require().ConnectionString); await db.OpenAsync(ct);
        string agent, platform, name;
        await using (var c = new NpgsqlCommand("select g.id, g.platform, a.name from assets.assets a join assets.agents g on g.id = a.agent_id and g.deleted_at is null and g.uninstall_at is null where a.id = @a", db))
        {
            AgentsApi.P(c, "a", assetId); await using var rd = await c.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return null;
            agent = rd.GetString(0); platform = rd.GetString(1); name = rd.GetString(2);
        }
        var (shell, body) = platform == "windows"
            ? ("powershell", $"Restart-Service -Name '{service}' -Force -ErrorAction Stop; (Get-Service -Name '{service}').Status")
            : ("sh", $"systemctl restart '{service}' && systemctl is-active '{service}'");
        var jid = "j" + AgentsApi.Rand(12);
        await using (var ins = new NpgsqlCommand("insert into assets.jobs (id, asset_id, agent_id, shell, title, body, status, by_name, timeout_s) values (@i,@a,@g,@s,@t,@b,'queued',@u,120)", db))
        {
            AgentsApi.P(ins, "i", jid); AgentsApi.P(ins, "a", assetId); AgentsApi.P(ins, "g", agent); AgentsApi.P(ins, "s", shell); AgentsApi.P(ins, "t", "Restart service " + service + " (Monitoring)");
            AgentsApi.P(ins, "b", body); AgentsApi.P(ins, "u", by); await ins.ExecuteNonQueryAsync(ct);
        }
        await AgentsApi.Enqueue(db, hub, tenant, agent, "job", new { id = jid, shell, body, timeout = 120 }, ct);
        await audit.WriteAsync("assets.command_run", assetId.ToString(), $"\"Restart service {service}\" ({shell}) on {name}, asked by {by}", ct: ct);
        return jid;
    }
}
