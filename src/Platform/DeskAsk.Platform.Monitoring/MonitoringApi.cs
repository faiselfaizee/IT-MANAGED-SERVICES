using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DeskAsk.Platform.Monitoring;

/// <summary>
/// The Monitoring screens: customer tiles, a customer's rings, the device list (filtered by the ring part clicked), a device with its charts,
/// alerts (acknowledge, open a ticket), mutes, alert recipients, and "check now".
/// </summary>
public static class MonitoringApi
{
    internal sealed record AlertRow(long Id, long AssetId, string Severity, string Metric);
    static readonly string[] Statuses = { "ok", "warning", "critical", "muted", "unwatched" };

    static async Task<List<AlertRow>> OpenAlerts(NpgsqlConnection db, CancellationToken ct)
    {
        var o = new List<AlertRow>();
        await using var c = new NpgsqlCommand("select id, asset_id, severity, metric from monitoring.alerts where closed_at is null", db);
        await using var rd = await c.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) o.Add(new AlertRow(rd.GetInt64(0), rd.GetInt64(1), rd.GetString(2), rd.GetString(3)));
        return o;
    }

    static async Task<Dictionary<long, (DateTime? Until, string Reason, string By)>> Mutes(NpgsqlConnection db, CancellationToken ct)
    {
        var o = new Dictionary<long, (DateTime?, string, string)>();
        await using var c = new NpgsqlCommand("select asset_id, until, reason, by_name from monitoring.mutes where until is null or until > now()", db);
        await using var rd = await c.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) o[rd.GetInt64(0)] = (rd.IsDBNull(1) ? null : rd.GetDateTime(1), rd.GetString(2), rd.GetString(3));
        return o;
    }

    /// <summary>muted, critical, warning, ok (watched, nothing wrong) or unwatched (no agent, no site controller, no checks).</summary>
    internal static string Status(DeviceState d, IReadOnlyCollection<AlertRow> alerts, bool muted) =>
        muted ? "muted" : alerts.Any(a => a.Severity == "critical") ? "critical" : alerts.Count > 0 ? "warning" : Evaluator.Watched(d) ? "ok" : "unwatched";

    static double? WorstDisk(DeviceState d) => d.Disks.Count == 0 ? null : d.Disks.Min(x => x.FreePct);
    static string DiskBand(DeviceState d) => WorstDisk(d) switch { null => "nodata", < 10 => "critical", < 20 => "low", _ => "ok" };
    static string CertBand(CheckState c) => c.CertDays switch { null => "", <= 7 => "urgent", <= 30 => "soon", _ => "ok" };

    sealed record Row(DeviceState D, string Status, List<AlertRow> Alerts);

    static async Task<(List<Row> Rows, bool Restricted)> Load(HttpContext http, NpgsqlConnection db, long? assetId, CancellationToken ct)
    {
        var scope = await http.RequestServices.GetRequiredService<IAccess>().ScopeAsync(http.User, Modules.Monitoring, Acts.Read, ct);
        var devices = await http.RequestServices.GetRequiredService<IDeviceHealth>().SnapshotAsync(scope, assetId, ct);
        var alerts = (await OpenAlerts(db, ct)).ToLookup(a => a.AssetId);
        var mutes = await Mutes(db, ct);
        return (devices.Select(d => { var al = alerts[d.Id].ToList(); return new Row(d, Status(d, al, mutes.ContainsKey(d.Id)), al); }).ToList(), scope != null);
    }

    static Dictionary<string, object?> Metrics(string company, int? companyId, IReadOnlyCollection<Row> rows)
    {
        int S(string s) => rows.Count(r => r.Status == s);
        var al = rows.SelectMany(r => r.Alerts).ToList();
        var checks = rows.SelectMany(r => r.D.Checks).ToList();
        var certs = checks.Where(c => c.Type == "ssl" && c.CertDays != null).ToList();
        return new()
        {
            ["company"] = company, ["companyId"] = companyId, ["devices"] = rows.Count, ["watched"] = rows.Count(r => Evaluator.Watched(r.D)),
            ["ok"] = S("ok"), ["warning"] = S("warning"), ["critical"] = S("critical"), ["muted"] = S("muted"), ["unwatched"] = S("unwatched"),
            ["down"] = rows.Count(r => r.Alerts.Any(a => a.Metric == "offline")),
            ["alertsCritical"] = al.Count(a => a.Severity == "critical"), ["alertsWarning"] = al.Count(a => a.Severity != "critical"),
            ["byMetric"] = RulesApi.Metrics.ToDictionary(m => m, m => al.Count(a => a.Metric == m)),
            ["checksOk"] = checks.Count(c => c.Ok == true), ["checksFailing"] = checks.Count(c => c.Ok == false), ["checksNotRun"] = checks.Count(c => c.Ok == null),
            ["certOk"] = certs.Count(c => CertBand(c) == "ok"), ["certSoon"] = certs.Count(c => CertBand(c) == "soon"), ["certUrgent"] = certs.Count(c => CertBand(c) == "urgent"),
            ["diskOk"] = rows.Count(r => DiskBand(r.D) == "ok"), ["diskLow"] = rows.Count(r => DiskBand(r.D) == "low"), ["diskCritical"] = rows.Count(r => DiskBand(r.D) == "critical"),
            ["diskNoData"] = rows.Count(r => DiskBand(r.D) == "nodata"),
            ["withMetrics"] = rows.Count(r => r.D.MetricsAt != null && r.D.MetricsAt > DateTime.UtcNow.AddMinutes(-10)),
        };
    }

    static object DeviceView(Row r, (DateTime? Until, string Reason, string By)? mute) => new
    {
        id = r.D.Id, tag = r.D.Tag, name = r.D.Name, company = r.D.Company, companyId = r.D.CompanyId, kind = r.D.Kind, ip = r.D.Ip, os = r.D.Os, status = r.Status,
        agent = r.D.HasAgent, agentVersion = r.D.AgentVersion, agentSeen = r.D.AgentSeen, pingUp = r.D.PingUp, downSince = r.D.DownSince, rttMs = r.D.RttMs, checkedAt = r.D.CheckedAt,
        metricsAt = r.D.MetricsAt, cpu = r.D.Cpu, mem = r.D.Mem, memTotal = r.D.MemTotal, uptimeS = r.D.UptimeS, worstDisk = WorstDisk(r.D),
        disks = r.D.Disks.Select(k => new { name = k.Name, freePct = k.FreePct, free = k.FreeBytes, total = k.TotalBytes }),
        checks = r.D.Checks.Select(c => new { c.Id, c.Name, c.Type, c.Ok, c.Fails, certDays = c.CertDays, c.Detail, c.At }),
        faults = r.D.Faults.Select(f => new { f.Part, f.Name, f.State }),
        alerts = r.Alerts.Count, alertsCritical = r.Alerts.Count(a => a.Severity == "critical"),
        muted = mute == null ? null : new { until = mute.Value.Until, reason = mute.Value.Reason, by = mute.Value.By },
    };

    static readonly string[] AlertCols = { "id", "rule_id", "rule_name", "metric", "severity", "asset_id", "asset_name", "asset_tag", "company_id", "company_name", "subject", "text", "value",
        "opened_at", "last_at", "closed_at", "close_reason", "ack_by", "ack_at", "ticket_id", "ticket_number" };
    static object AlertView(NpgsqlDataReader rd) => new
    {
        id = rd.GetInt64(0), ruleId = rd.IsDBNull(1) ? (int?)null : rd.GetInt32(1), rule = rd.GetString(2), metric = rd.GetString(3), severity = rd.GetString(4),
        assetId = rd.GetInt64(5), asset = rd.GetString(6), tag = rd.GetString(7), companyId = rd.IsDBNull(8) ? (int?)null : rd.GetInt32(8), company = rd.GetString(9), subject = rd.GetString(10),
        text = rd.GetString(11), value = rd.IsDBNull(12) ? (double?)null : rd.GetFloat(12), openedAt = rd.GetDateTime(13), lastAt = rd.GetDateTime(14),
        closedAt = rd.IsDBNull(15) ? (DateTime?)null : rd.GetDateTime(15), closeReason = rd.GetString(16), ackBy = rd.GetString(17), ackAt = rd.IsDBNull(18) ? (DateTime?)null : rd.GetDateTime(18),
        ticketId = rd.IsDBNull(19) ? (long?)null : rd.GetInt64(19), ticket = rd.GetString(20),
    };

    /// <summary>An alert, if the person may see its customer's data.</summary>
    static async Task<(long Id, long AssetId, int? Company, long? Ticket, DateTime? Closed)?> AlertFor(HttpContext http, NpgsqlConnection db, long id, string act, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("select id, asset_id, company_id, ticket_id, closed_at from monitoring.alerts where id = @i", db); MonitoringModule.P(c, "i", id);
        (long, long, int?, long?, DateTime?) a;
        await using (var rd = await c.ExecuteReaderAsync(ct))
        {
            if (!await rd.ReadAsync(ct)) return null;
            a = (rd.GetInt64(0), rd.GetInt64(1), rd.IsDBNull(2) ? null : rd.GetInt32(2), rd.IsDBNull(3) ? null : rd.GetInt64(3), rd.IsDBNull(4) ? null : rd.GetDateTime(4));
        }
        var scope = await http.RequestServices.GetRequiredService<IAccess>().ScopeAsync(http.User, Modules.Monitoring, act, ct);
        return scope == null || (a.Item3 is int co && scope.Contains(co)) ? a : null;
    }

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        RouteHandlerBuilder Read(RouteHandlerBuilder b) => b.RequireAuthorization().RequireModule(key).RequireAccess(key, Acts.Read);
        RouteHandlerBuilder Modify(RouteHandlerBuilder b) => b.RequireAuthorization().RequireModule(key).RequireAccess(key, Acts.Modify);
        RouteHandlerBuilder Admin(RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole("admin")).RequireModule(key);

        // tiles: every customer this person may see, plus all together; ?company=<id|none> for one customer's rings
        Read(r.MapGet("/api/monitoring/dashboard", async (HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            var company = http.Request.Query["company"].ToString();
            if (company != "" && company != "none" && !int.TryParse(company, out _)) return MonitoringModule.Bad("bad_input", "company must be a customer id or none");
            await using var db = await MonitoringModule.Open(t, ct);
            var (rows, restricted) = await Load(http, db, null, ct);
            var groups = rows.GroupBy(x => x.D.CompanyId).Select(g => Metrics(g.First().D.Company, g.Key, g.ToList())).OrderBy(m => (string)m["company"]!).ToList();
            if (company != "")
            {
                var one = groups.FirstOrDefault(g => company == "none" ? g["companyId"] == null : g["companyId"] is int i && i.ToString() == company);
                return one == null ? Results.NotFound() : Results.Ok(new { customer = one });
            }
            return Results.Ok(new { all = Metrics("All customers", null, rows), customers = groups, restricted });
        }));

        // devices, filtered by what a ring part means
        Read(r.MapGet("/api/monitoring/devices", async (HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            var q = http.Request.Query;
            string Q(string n) => q[n].ToString().Trim();
            var company = Q("company"); var status = Q("status"); var kind = Q("kind"); var disk = Q("disk"); var cert = Q("cert"); var check = Q("check"); var metric = Q("metric"); var search = Q("search").ToLowerInvariant();
            if (company != "" && company != "none" && !int.TryParse(company, out _)) return MonitoringModule.Bad("bad_input", "company must be a customer id or none");
            if (status != "" && status != "down" && !Statuses.Contains(status)) return MonitoringModule.Bad("bad_input", "status must be ok, warning, critical, muted, unwatched or down");
            if (kind != "" && !RulesApi.Kinds.Contains(kind)) return MonitoringModule.Bad("bad_input", "kind");
            if (disk is not ("" or "ok" or "low" or "critical" or "nodata")) return MonitoringModule.Bad("bad_input", "disk must be ok, low, critical or nodata");
            if (cert is not ("" or "ok" or "soon" or "urgent")) return MonitoringModule.Bad("bad_input", "cert must be ok, soon or urgent");
            if (check is not ("" or "ok" or "failing" or "notrun")) return MonitoringModule.Bad("bad_input", "check must be ok, failing or notrun");
            if (metric != "" && !RulesApi.Metrics.Contains(metric)) return MonitoringModule.Bad("bad_input", "metric");
            int.TryParse(q["skip"], out var skip); int.TryParse(q["take"], out var take); take = take <= 0 ? 50 : Math.Min(take, 500); skip = Math.Max(0, skip);
            await using var db = await MonitoringModule.Open(t, ct);
            var (rows, _) = await Load(http, db, null, ct);
            var mutes = await Mutes(db, ct);
            IEnumerable<Row> f = rows;
            if (company == "none") f = f.Where(x => x.D.CompanyId == null); else if (company != "") f = f.Where(x => x.D.CompanyId?.ToString() == company);
            if (status == "down") f = f.Where(x => x.Alerts.Any(a => a.Metric == "offline")); else if (status != "") f = f.Where(x => x.Status == status);
            if (kind != "") f = f.Where(x => x.D.Kind == kind);
            if (disk != "") f = f.Where(x => DiskBand(x.D) == disk);
            if (cert != "") f = f.Where(x => x.D.Checks.Any(c => c.Type == "ssl" && CertBand(c) == cert));
            if (check == "failing") f = f.Where(x => x.D.Checks.Any(c => c.Ok == false)); else if (check == "ok") f = f.Where(x => x.D.Checks.Count > 0 && x.D.Checks.All(c => c.Ok == true)); else if (check == "notrun") f = f.Where(x => x.D.Checks.Any(c => c.Ok == null));
            if (metric != "") f = f.Where(x => x.Alerts.Any(a => a.Metric == metric));
            if (search != "") f = f.Where(x => (x.D.Name + " " + x.D.Tag + " " + x.D.Ip + " " + x.D.Company).ToLowerInvariant().Contains(search));
            var order = new Dictionary<string, int> { ["critical"] = 0, ["warning"] = 1, ["muted"] = 2, ["ok"] = 3, ["unwatched"] = 4 };
            var list = f.OrderBy(x => order[x.Status]).ThenBy(x => x.D.Name, StringComparer.OrdinalIgnoreCase).ToList();
            return Results.Ok(new { total = list.Count, items = list.Skip(skip).Take(take).Select(x => DeviceView(x, mutes.TryGetValue(x.D.Id, out var m) ? m : null)) });
        }));

        Read(r.MapGet("/api/monitoring/devices/{id:long}", async (long id, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await MonitoringModule.Open(t, ct);
            var (rows, _) = await Load(http, db, id, ct);
            if (rows.Count == 0) return Results.NotFound();
            var mutes = await Mutes(db, ct);
            var alerts = new List<object>();
            await using (var c = new NpgsqlCommand($"select {string.Join(", ", AlertCols)} from monitoring.alerts where asset_id = @a order by closed_at is not null, opened_at desc limit 50", db))
            {
                MonitoringModule.P(c, "a", id); await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) alerts.Add(AlertView(rd));
            }
            return Results.Ok(new { device = DeviceView(rows[0], mutes.TryGetValue(id, out var m) ? m : null), alerts });
        }));

        Read(r.MapGet("/api/monitoring/devices/{id:long}/series", async (long id, string? range, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            var span = (range ?? "24h") switch { "6h" => TimeSpan.FromHours(6), "24h" => TimeSpan.FromHours(24), "7d" => TimeSpan.FromDays(7), "30d" => TimeSpan.FromDays(30), "90d" => TimeSpan.FromDays(90), _ => TimeSpan.Zero };
            if (span == TimeSpan.Zero) return MonitoringModule.Bad("bad_input", "range must be 6h, 24h, 7d, 30d or 90d");
            var health = http.RequestServices.GetRequiredService<IDeviceHealth>();
            var scope = await http.RequestServices.GetRequiredService<IAccess>().ScopeAsync(http.User, Modules.Monitoring, Acts.Read, ct);
            if ((await health.SnapshotAsync(scope, id, ct)).Count == 0) return Results.NotFound();
            var now = DateTime.UtcNow; var hourly = span > TimeSpan.FromHours(24);
            var pts = await health.SeriesAsync(id, now - span, now, hourly, ct);
            return Results.Ok(new { range = range ?? "24h", hourly, from = now - span, to = now, points = pts.Select(p => new { at = p.At, cpu = p.Cpu, cpuMax = p.CpuMax, mem = p.Mem, memMax = p.MemMax, diskFree = p.DiskFreeMin }) });
        }));

        Modify(r.MapPost("/api/monitoring/devices/{id:long}/mute", async (long id, JsonElement b, HttpContext http, ClaimsPrincipal u, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var health = http.RequestServices.GetRequiredService<IDeviceHealth>();
            var scope = await http.RequestServices.GetRequiredService<IAccess>().ScopeAsync(u, Modules.Monitoring, Acts.Modify, ct);
            var dev = (await health.SnapshotAsync(scope, id, ct)).FirstOrDefault(); if (dev == null) return Results.NotFound();
            double? hours = b.ValueKind == JsonValueKind.Object && b.TryGetProperty("hours", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetDouble() : null;
            if (hours is <= 0 or > 24 * 90) return MonitoringModule.Bad("bad_input", "Mute for 1 hour to 90 days, or leave hours out to mute until you unmute.");
            var reason = b.ValueKind == JsonValueKind.Object && b.TryGetProperty("reason", out var rv) && rv.ValueKind == JsonValueKind.String ? (rv.GetString() ?? "").Trim() : "";
            if (reason.Length > 200) reason = reason[..200];
            await using var db = await MonitoringModule.Open(t, ct);
            await using (var c = new NpgsqlCommand(@"insert into monitoring.mutes (asset_id, until, reason, by_name) values (@a, @u, @r, @b)
                on conflict (asset_id) do update set until = excluded.until, reason = excluded.reason, by_name = excluded.by_name, at = now()", db))
            {
                MonitoringModule.P(c, "a", id); MonitoringModule.P(c, "u", hours == null ? null : DateTime.UtcNow.AddHours(hours.Value)); MonitoringModule.P(c, "r", reason); MonitoringModule.P(c, "b", MonitoringModule.Actor(u));
                await c.ExecuteNonQueryAsync(ct);
            }
            await audit.WriteAsync("monitoring.muted", dev.Tag, (hours == null ? "until unmuted" : MonitoringModule.Dur(TimeSpan.FromHours(hours.Value))) + (reason != "" ? ": " + reason : ""), ct: ct);
            return Results.Ok(new { ok = true });
        }));

        Modify(r.MapDelete("/api/monitoring/devices/{id:long}/mute", async (long id, HttpContext http, ClaimsPrincipal u, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var scope = await http.RequestServices.GetRequiredService<IAccess>().ScopeAsync(u, Modules.Monitoring, Acts.Modify, ct);
            var dev = (await http.RequestServices.GetRequiredService<IDeviceHealth>().SnapshotAsync(scope, id, ct)).FirstOrDefault(); if (dev == null) return Results.NotFound();
            await using var db = await MonitoringModule.Open(t, ct);
            await using var c = new NpgsqlCommand("delete from monitoring.mutes where asset_id = @a", db); MonitoringModule.P(c, "a", id); await c.ExecuteNonQueryAsync(ct);
            await audit.WriteAsync("monitoring.unmuted", dev.Tag, ct: ct);
            return Results.Ok(new { ok = true });
        }));

        Read(r.MapGet("/api/monitoring/alerts", async (HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            var q = http.Request.Query; string Q(string n) => q[n].ToString().Trim();
            var state = Q("state"); if (state == "") state = "open";
            if (state is not ("open" or "closed" or "all")) return MonitoringModule.Bad("bad_input", "state must be open, closed or all");
            var sev = Q("severity"); if (sev is not ("" or "warning" or "critical")) return MonitoringModule.Bad("bad_input", "severity must be warning or critical");
            var metric = Q("metric"); if (metric != "" && !RulesApi.Metrics.Contains(metric)) return MonitoringModule.Bad("bad_input", "metric");
            var company = Q("company"); if (company != "" && company != "none" && !int.TryParse(company, out _)) return MonitoringModule.Bad("bad_input", "company must be a customer id or none");
            int.TryParse(q["skip"], out var skip); int.TryParse(q["take"], out var take); take = take <= 0 ? 50 : Math.Min(take, 200); skip = Math.Max(0, skip);
            var search = Q("search");
            var scope = await http.RequestServices.GetRequiredService<IAccess>().ScopeAsync(http.User, Modules.Monitoring, Acts.Read, ct);
            await using var db = await MonitoringModule.Open(t, ct);
            await using var c = new NpgsqlCommand($@"select {string.Join(", ", AlertCols)}, count(*) over() from monitoring.alerts
                where (@st = 'all' or (@st = 'open') = (closed_at is null)) and (@sv = '' or severity = @sv) and (@m = '' or metric = @m)
                  and (@co = '' or (@co = 'none' and company_id is null) or company_id::text = @co)
                  and (not @sc or company_id = any(@scope))
                  and (@q = '' or asset_name ilike @l or asset_tag ilike @l or company_name ilike @l or rule_name ilike @l or text ilike @l)
                order by closed_at is not null, case when closed_at is null and severity = 'critical' then 0 else 1 end, coalesce(closed_at, opened_at) desc offset @sk limit @tk", db);
            MonitoringModule.P(c, "st", state); MonitoringModule.P(c, "sv", sev); MonitoringModule.P(c, "m", metric); MonitoringModule.P(c, "co", company);
            MonitoringModule.P(c, "sc", scope != null); MonitoringModule.P(c, "scope", (scope ?? Array.Empty<int>()).ToArray());
            MonitoringModule.P(c, "q", search); MonitoringModule.P(c, "l", "%" + search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
            MonitoringModule.P(c, "sk", skip); MonitoringModule.P(c, "tk", take);
            var items = new List<object>(); long total = 0;
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) { items.Add(AlertView(rd)); total = rd.GetInt64(AlertCols.Length); }
            return Results.Ok(new { total, items });
        }));

        Modify(r.MapPost("/api/monitoring/alerts/{id:long}/ack", async (long id, HttpContext http, ClaimsPrincipal u, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await MonitoringModule.Open(t, ct);
            var a = await AlertFor(http, db, id, Acts.Modify, ct); if (a == null) return Results.NotFound();
            if (a.Value.Closed != null) return MonitoringModule.Bad("closed", "This alert has already cleared.");
            await using var c = new NpgsqlCommand("update monitoring.alerts set ack_by = @b, ack_at = now() where id = @i and ack_at is null", db);
            MonitoringModule.P(c, "b", MonitoringModule.Actor(u)); MonitoringModule.P(c, "i", id);
            if (await c.ExecuteNonQueryAsync(ct) == 0) return MonitoringModule.Bad("already", "Someone has already acknowledged this alert.");
            await audit.WriteAsync("monitoring.alert_acknowledged", id.ToString(), ct: ct);
            return Results.Ok(new { ok = true });
        }));

        Modify(r.MapPost("/api/monitoring/alerts/{id:long}/ticket", async (long id, HttpContext http, ITenantContext t, IEntitlements ent, IAuditLog audit, CancellationToken ct) =>
        {
            if (!(await ent.CurrentAsync(ct)).Has(Modules.ServiceDesk)) return MonitoringModule.Bad("no_servicedesk", "Tickets need the Service Desk module in your licence.");
            var desk = http.RequestServices.GetService<ITicketDesk>(); if (desk == null) return MonitoringModule.Bad("no_servicedesk", "The Service Desk is not installed.");
            await using var db = await MonitoringModule.Open(t, ct);
            var a = await AlertFor(http, db, id, Acts.Modify, ct); if (a == null) return Results.NotFound();
            if (a.Value.Ticket is long existing) return MonitoringModule.Bad("has_ticket", "This alert already has a ticket.");
            var dev = (await http.RequestServices.GetRequiredService<IDeviceHealth>().SnapshotAsync(null, a.Value.AssetId, ct)).FirstOrDefault();
            string rule, metric, severity, text;
            await using (var c = new NpgsqlCommand("select rule_name, metric, severity, text, asset_name, asset_tag, company_name from monitoring.alerts where id = @i", db))
            {
                MonitoringModule.P(c, "i", id); await using var rd = await c.ExecuteReaderAsync(ct); await rd.ReadAsync(ct);
                rule = rd.GetString(0); metric = rd.GetString(1); severity = rd.GetString(2); text = rd.GetString(3);
                dev ??= new DeviceState(a.Value.AssetId, rd.GetString(5), rd.GetString(4), a.Value.Company, rd.GetString(6), "other", "", "", false, "", null, null, null, 0, null, null, null, null, 0, 0,
                    Array.Empty<DiskUse>(), Array.Empty<CheckState>(), Array.Empty<PartFault>());
            }
            var rr = new Rule(0, rule, true, metric, 0, 0, severity, Array.Empty<string>(), null, false, true, severity == "critical" ? "critical" : "high", false);
            var number = await Evaluator.Ticket(db, desk, new Condition("", rr, dev, metric, "", severity, text, null), id, ct);
            if (number == null) return MonitoringModule.Bad("ticket_failed", "The ticket could not be opened.");
            await audit.WriteAsync("monitoring.alert_ticket", id.ToString(), number, ct: ct);
            return Results.Ok(new { ticket = number });
        }));

        Admin(r.MapPost("/api/monitoring/evaluate", async (HttpContext http, CancellationToken ct) =>
        {
            var res = await Evaluator.RunAsync(http.RequestServices, ct, wait: true);
            return Results.Ok(new { opened = res.Opened, closed = res.Closed, open = res.Open, raised = res.Raised });
        }));

        Admin(r.MapGet("/api/monitoring/settings", async (ISettings s, ICompanyDirectory dir, CancellationToken ct) =>
        {
            var m = await MonitorSettings.LoadAsync(s, ct);
            var names = (await dir.ListAsync(ct)).ToDictionary(c => c.Id.ToString(), c => c.Name);
            return Results.Ok(new { recipients = m.Recipients, mailResolved = m.MailResolved, companies = m.Companies.Select(x => new { companyId = int.Parse(x.Key), company = names.GetValueOrDefault(x.Key, "Customer " + x.Key), recipients = x.Value }) });
        }));

        Admin(r.MapPut("/api/monitoring/settings", async (JsonElement b, ISettings s, ICompanyDirectory dir, IAuditLog audit, CancellationToken ct) =>
        {
            static string? CheckList(string list)
            {
                var bad = MonitorSettings.Split(list).FirstOrDefault(x => !System.Text.RegularExpressions.Regex.IsMatch(x, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"));
                return bad == null ? null : $"\"{bad}\" is not an email address.";
            }
            var m = new MonitorSettings
            {
                Recipients = b.TryGetProperty("recipients", out var rv) && rv.ValueKind == JsonValueKind.String ? rv.GetString()!.Trim() : "",
                MailResolved = !b.TryGetProperty("mailResolved", out var mr) || mr.ValueKind != JsonValueKind.False,
            };
            if (CheckList(m.Recipients) is string e1) return MonitoringModule.Bad("bad_input", e1);
            if (b.TryGetProperty("companies", out var cs) && cs.ValueKind == JsonValueKind.Array)
                foreach (var c in cs.EnumerateArray())
                {
                    if (!c.TryGetProperty("companyId", out var ci) || ci.ValueKind != JsonValueKind.Number) return MonitoringModule.Bad("bad_input", "companyId");
                    var list = c.TryGetProperty("recipients", out var lv) && lv.ValueKind == JsonValueKind.String ? lv.GetString()!.Trim() : "";
                    if (list == "") continue;
                    if (CheckList(list) is string e2) return MonitoringModule.Bad("bad_input", e2);
                    if (await dir.GetAsync(ci.GetInt32(), ct) == null) return MonitoringModule.Bad("bad_input", "That customer does not exist.");
                    m.Companies[ci.GetInt32().ToString()] = list;
                }
            await m.SaveAsync(s, ct);
            await audit.WriteAsync("monitoring.settings_changed", "", $"{MonitorSettings.Split(m.Recipients).Count} default recipients, {m.Companies.Count} customers with their own", ct: ct);
            return Results.Ok(new { ok = true });
        }));
    }
}
