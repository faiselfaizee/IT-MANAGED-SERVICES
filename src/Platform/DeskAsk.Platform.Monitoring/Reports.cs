using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DeskAsk.Platform.Monitoring;

/// <summary>
/// The monthly uptime report of a customer (or all customers): how long each watched device was down, the alerts by kind and severity,
/// tickets opened from alerts, the busiest devices and the fullest disks. Down time is the time "down" alerts were open in the month.
/// </summary>
public static class ReportsApi
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public sealed record DeviceLine(long Id, string Name, string Tag, string Company, double DownMinutes, double Availability, int Alerts, double? CpuAvg, double? MemAvg, double? DiskFreeMin);
    public sealed record Report(string Title, string Month, int Devices, double Availability, int Critical, int Warning, int Tickets, Dictionary<string, int> ByKind,
        List<DeviceLine> Lines, int Outages, double DownMinutes);

    static readonly Dictionary<string, string> KindWords = new()
    {
        ["offline"] = "Device down", ["cpu"] = "Processor", ["memory"] = "Memory", ["disk"] = "Disk space", ["check"] = "Service checks", ["cert"] = "Certificates", ["hardware"] = "Hardware",
        ["traffic"] = "Port traffic", ["port"] = "Ports down", ["service"] = "Services", ["process"] = "Programs", ["log"] = "Device messages", ["supply"] = "Printer supplies", ["printer"] = "Printer faults",
    };

    /// <summary>Builds the report for a month. companyId: a customer, 0 for devices without a customer, null for all.</summary>
    public static async Task<Report> Build(IServiceProvider sp, NpgsqlConnection db, int? companyId, DateTime first, string title, IReadOnlyList<int>? scope, CancellationToken ct)
    {
        var end = first.AddMonths(1); var now = DateTime.UtcNow; var stop = end < now ? end : now;
        var monthMinutes = Math.Max(1, (stop - first).TotalMinutes);
        var health = sp.GetRequiredService<IDeviceHealth>();
        var devices = (await health.SnapshotAsync(scope, null, ct)).Where(d => Evaluator.Watched(d) && (companyId == null || (companyId == 0 ? d.CompanyId == null : d.CompanyId == companyId))).ToList();
        var ids = devices.Select(d => d.Id).ToHashSet();
        var down = new Dictionary<long, double>(); var alertsPer = new Dictionary<long, int>(); var byKind = new Dictionary<string, int>();
        int critical = 0, warning = 0, tickets = 0, outages = 0;
        await using (var c = new NpgsqlCommand("select asset_id, metric, severity, opened_at, closed_at, ticket_id is not null from monitoring.alerts where opened_at < @e and coalesce(closed_at, now()) > @f", db))
        {
            MonitoringModule.P(c, "f", first); MonitoringModule.P(c, "e", end);
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var a = rd.GetInt64(0); if (!ids.Contains(a)) continue;
                var metric = rd.GetString(1); var opened = rd.GetDateTime(3); var closed = rd.IsDBNull(4) ? now : rd.GetDateTime(4);
                if (metric == "offline")
                {
                    var from = opened < first ? first : opened; var to = closed > stop ? stop : closed;
                    if (to > from) { down[a] = down.GetValueOrDefault(a) + (to - from).TotalMinutes; outages++; }
                }
                if (opened < first) continue; // counted in the month it opened
                alertsPer[a] = alertsPer.GetValueOrDefault(a) + 1; byKind[metric] = byKind.GetValueOrDefault(metric) + 1;
                if (rd.GetString(2) == "critical") critical++; else warning++;
                if (rd.GetBoolean(5)) tickets++;
            }
        }
        var avg = await health.AveragesAsync(first, end, ct);
        var lines = devices.Select(d =>
        {
            var dm = Math.Min(monthMinutes, down.GetValueOrDefault(d.Id)); var av = avg.GetValueOrDefault(d.Id);
            return new DeviceLine(d.Id, d.Name, d.Tag, d.Company, Math.Round(dm), Math.Round(100 - dm * 100 / monthMinutes, 3), alertsPer.GetValueOrDefault(d.Id), av?.CpuAvg, av?.MemAvg, av?.DiskFreeMin);
        }).OrderBy(l => l.Availability).ThenByDescending(l => l.Alerts).ThenBy(l => l.Name).ToList();
        var overall = lines.Count == 0 ? 100 : Math.Round(lines.Average(l => l.Availability), 3);
        return new Report(title, first.ToString("MMMM yyyy", Inv), lines.Count, overall, critical, warning, tickets, byKind, lines, outages, lines.Sum(l => l.DownMinutes));
    }

    static string Pct(double v) => v >= 99.995 ? "100%" : v.ToString(v >= 99 ? "0.00" : "0.0", Inv) + "%";
    static string Mins(double m) => m < 1 ? "none" : MonitoringModule.Dur(TimeSpan.FromMinutes(m));

    public static byte[] Pdf(Report r, string site, PdfStyle? style = null)
    {
        var d = new PdfDoc(style);
        d.Heading($"Uptime report: {r.Title}", 17, rule: true);
        d.Paragraph($"{r.Month}. Prepared by {site} from DeskAsk Monitoring on {DateTime.UtcNow:d MMMM yyyy}.", 9.5);
        d.Gap(6);
        d.Facts(new List<(string, string)>
        {
            ("Devices watched", r.Devices.ToString(Inv)), ("Average availability", Pct(r.Availability)), ("Outages", r.Outages.ToString(Inv)), ("Total down time", Mins(r.DownMinutes)),
            ("Critical alerts", r.Critical.ToString(Inv)), ("Warning alerts", r.Warning.ToString(Inv)), ("Tickets opened from alerts", r.Tickets.ToString(Inv)),
        });
        d.Gap(8);
        var downs = r.Lines.Where(l => l.DownMinutes >= 1).ToList();
        d.Heading("Devices that were down", 12.5);
        if (downs.Count == 0) d.Paragraph("No watched device was down this month.");
        else d.Table(new[] { "Device", "Customer", "Down for", "Availability", "Alerts" }, new[] { 0.3, 0.27, 0.17, 0.14, 0.12 },
            downs.Take(60).Select(l => new[] { $"{l.Name} ({l.Tag})", l.Company, Mins(l.DownMinutes), Pct(l.Availability), l.Alerts.ToString(Inv) }));
        d.Gap(8);
        d.Heading("Alerts by kind", 12.5);
        if (r.ByKind.Count == 0) d.Paragraph("No alerts this month.");
        else d.Table(new[] { "Kind", "Alerts" }, new[] { 0.7, 0.3 }, r.ByKind.OrderByDescending(k => k.Value).Select(k => new[] { KindWords.GetValueOrDefault(k.Key, k.Key), k.Value.ToString(Inv) }));
        var busy = r.Lines.Where(l => l.CpuAvg != null).OrderByDescending(l => l.CpuAvg).Take(10).ToList();
        if (busy.Count > 0)
        {
            d.Gap(8); d.Heading("Busiest devices (average processor use)", 12.5);
            d.Table(new[] { "Device", "Customer", "Processor", "Memory" }, new[] { 0.36, 0.32, 0.16, 0.16 },
                busy.Select(l => new[] { $"{l.Name} ({l.Tag})", l.Company, l.CpuAvg!.Value.ToString("0.#", Inv) + "%", l.MemAvg == null ? "-" : l.MemAvg.Value.ToString("0.#", Inv) + "%" }));
        }
        var full = r.Lines.Where(l => l.DiskFreeMin != null).OrderBy(l => l.DiskFreeMin).Take(10).ToList();
        if (full.Count > 0)
        {
            d.Gap(8); d.Heading("Least free disk space during the month", 12.5);
            d.Table(new[] { "Device", "Customer", "Lowest free" }, new[] { 0.42, 0.38, 0.2 }, full.Select(l => new[] { $"{l.Name} ({l.Tag})", l.Company, l.DiskFreeMin!.Value.ToString("0.#", Inv) + "%" }));
        }
        d.Gap(8);
        d.Paragraph("Availability is the share of the month a device had no open \"down\" alert. Devices are counted when they are watched (an agent, a site controller or service checks) and in service.", 8.5);
        return d.ToBytes();
    }

    static bool Month(string? m, out DateTime first)
    {
        first = DateTime.SpecifyKind(new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1), DateTimeKind.Utc).AddMonths(-1);
        if (string.IsNullOrEmpty(m)) return true;
        if (!DateTime.TryParseExact(m, "yyyy-MM", Inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var f)) return false;
        first = DateTime.SpecifyKind(f, DateTimeKind.Utc); return true;
    }

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        async Task<(IResult? Error, Report? Report)> Get(HttpContext http, string? company, string? month, CancellationToken ct)
        {
            if (!Month(month, out var first)) return (MonitoringModule.Bad("bad_input", "month must look like 2026-10"), null);
            var scope = await http.RequestServices.GetRequiredService<IAccess>().ScopeAsync(http.User, Modules.Monitoring, Acts.Read, ct);
            int? co = null; var title = "All customers";
            if (company is { Length: > 0 } and not "all")
            {
                if (company == "none") { co = 0; title = "No customer"; }
                else if (int.TryParse(company, out var id)) { co = id; title = (await http.RequestServices.GetRequiredService<ICompanyDirectory>().GetAsync(id, ct))?.Name ?? ""; if (title == "") return (Results.NotFound(), null); }
                else return (MonitoringModule.Bad("bad_input", "company must be a customer id, none or all"), null);
                if (scope != null && (co == 0 || !scope.Contains(co!.Value))) return (Results.NotFound(), null);
            }
            else if (scope != null && scope.Count == 1) { co = scope[0]; title = (await http.RequestServices.GetRequiredService<ICompanyDirectory>().GetAsync(scope[0], ct))?.Name ?? title; }
            await using var db = await MonitoringModule.Open(http.RequestServices.GetRequiredService<ITenantContext>(), ct);
            return (null, await Build(http.RequestServices, db, co, first, title, scope, ct));
        }

        r.MapGet("/api/monitoring/reports/uptime", async (string? company, string? month, HttpContext http, CancellationToken ct) =>
        {
            var (err, rep) = await Get(http, company, month, ct); return err ?? Results.Ok(rep);
        }).RequireAuthorization().RequireModule(key).RequireAccess(key, Acts.Read);

        r.MapGet("/api/monitoring/reports/uptime.pdf", async (string? company, string? month, HttpContext http, ITenantContext t, ISettings pdfSettings, CancellationToken ct) =>
        {
            var (err, rep) = await Get(http, company, month, ct); if (err != null) return err;
            return Results.File(Pdf(rep!, t.Require().Name, await PdfStyle.LoadAsync(pdfSettings, ct)), "application/pdf", $"uptime-{(company ?? "all")}-{month ?? DateTime.UtcNow.AddMonths(-1).ToString("yyyy-MM", Inv)}.pdf");
        }).RequireAuthorization().RequireModule(key).RequireAccess(key, Acts.Read);

        r.MapPost("/api/monitoring/reports/send", async (JsonElement b, IJobQueue jobs, IAuditLog audit, CancellationToken ct) =>
        {
            var month = b.ValueKind == JsonValueKind.Object && b.TryGetProperty("month", out var mv) && mv.ValueKind == JsonValueKind.String ? mv.GetString() : null;
            if (!Month(month, out var first)) return MonitoringModule.Bad("bad_input", "month must look like 2026-10");
            await jobs.EnqueueAsync(ReportJob.JobType, JsonSerializer.Serialize(new { month = first.ToString("yyyy-MM", Inv), force = true }), null, null, ct);
            await audit.WriteAsync("monitoring.report_sent_now", first.ToString("yyyy-MM", Inv), ct: ct);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(p => p.RequireRole("admin")).RequireModule(key);
    }
}

/// <summary>Mails last month's uptime reports as PDFs in the first days of the month, once each (recorded), or now when asked.</summary>
public sealed class ReportJob : IJobHandler
{
    public const string JobType = "monitoring.report";
    public string Type => JobType;

    public async Task RunAsync(JobContext job, CancellationToken ct)
    {
        var sp = job.Services;
        var lic = await sp.GetRequiredService<IEntitlements>().CurrentAsync(ct);
        if (!lic.Has(Modules.Monitoring) || !lic.Has(Modules.Assets)) return;
        var settings = await MonitorSettings.LoadAsync(sp.GetRequiredService<ISettings>(), ct);
        var mail = sp.GetService<IMailQueue>(); if (mail == null) return;
        string? forced = null; var force = false;
        try { var p = JsonDocument.Parse(string.IsNullOrWhiteSpace(job.Payload) ? "{}" : job.Payload).RootElement; if (p.TryGetProperty("month", out var m)) forced = m.GetString(); force = p.TryGetProperty("force", out var f) && f.ValueKind == JsonValueKind.True; }
        catch (JsonException) { }
        if (!settings.ReportEnabled && !force) return;
        if (!force && DateTime.UtcNow.Day > 3) return; // sent in the first days of the month (a server that was off on the 1st catches up)
        var first = DateTime.SpecifyKind(new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1), DateTimeKind.Utc).AddMonths(-1);
        if (forced != null && DateTime.TryParseExact(forced, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var fm)) first = DateTime.SpecifyKind(fm, DateTimeKind.Utc);
        var month = first.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var site = job.Tenant.Name;
        var pdfStyle = await PdfStyle.LoadAsync(sp.GetRequiredService<ISettings>(), ct);
        await using var db = new NpgsqlConnection(job.Tenant.ConnectionString); await db.OpenAsync(ct);

        async Task<bool> Claim(string scope, string to)
        {
            await using var c = new NpgsqlCommand("insert into monitoring.report_sent (month, scope, recipient) values (@m,@s,@r) on conflict (month, scope, recipient) do " +
                (force ? "update set at = now() where monitoring.report_sent.at < now() - interval '5 minutes'" : "nothing"), db);
            MonitoringModule.P(c, "m", month); MonitoringModule.P(c, "s", scope); MonitoringModule.P(c, "r", to.ToLowerInvariant());
            return await c.ExecuteNonQueryAsync(ct) == 1;
        }
        async Task Release(string scope, string to)
        {
            await using var c = new NpgsqlCommand("delete from monitoring.report_sent where month = @m and scope = @s and recipient = @r", db);
            MonitoringModule.P(c, "m", month); MonitoringModule.P(c, "s", scope); MonitoringModule.P(c, "r", to.ToLowerInvariant()); await c.ExecuteNonQueryAsync(ct);
        }
        async Task Send(string scope, IEnumerable<string> to, ReportsApi.Report rep, string file)
        {
            var pdf = new MailFile(file, "application/pdf", ReportsApi.Pdf(rep, site, pdfStyle));
            var values = new Dictionary<string, string>
            {
                ["company"] = rep.Title, ["month"] = rep.Month, ["availability"] = rep.Availability >= 99.995 ? "100%" : rep.Availability.ToString("0.00", CultureInfo.InvariantCulture) + "%",
                ["devices"] = rep.Devices.ToString(CultureInfo.InvariantCulture), ["outages"] = rep.Outages.ToString(CultureInfo.InvariantCulture),
                ["alerts"] = (rep.Critical + rep.Warning).ToString(CultureInfo.InvariantCulture), ["path"] = "#/monitoring/reports",
            };
            foreach (var a in to)
            {
                if (!await Claim(scope, a)) continue;
                bool ok; try { ok = await mail.EnqueueTemplateAsync("monitoring_report", a, values, new[] { pdf }, "mon-report:" + month, ct); } catch (Exception) { ok = false; }
                if (!ok) await Release(scope, a);
            }
        }

        var internalTo = MonitorSettings.Split(settings.ReportTo);
        if (internalTo.Count > 0)
            await Send("all", internalTo, await ReportsApi.Build(sp, db, null, first, "All customers", null, ct), $"uptime-all-{month}.pdf");
        if (settings.ReportCustomers)
        {
            var dir = sp.GetRequiredService<ICompanyDirectory>();
            foreach (var (key, list) in settings.Companies)
            {
                if (!int.TryParse(key, out var co)) continue;
                var name = (await dir.GetAsync(co, ct))?.Name; if (name == null) continue;
                var rep = await ReportsApi.Build(sp, db, co, first, name, null, ct);
                if (rep.Devices == 0) continue;
                await Send("co:" + co, MonitorSettings.Split(list), rep, $"uptime-{co}-{month}.pdf");
            }
        }
    }
}
