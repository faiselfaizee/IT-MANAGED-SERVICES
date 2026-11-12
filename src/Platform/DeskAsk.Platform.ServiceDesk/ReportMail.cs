using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DeskAsk.Platform.ServiceDesk;

/// <summary>Who gets the monthly report by mail.</summary>
public sealed class ReportMailSettings
{
    public const string Key = "servicedesk.reportmail";
    public bool Enabled { get; set; }
    /// <summary>Your own addresses (comma separated). They get the report for all companies.</summary>
    public string To { get; set; } = "";
    /// <summary>Each customer admin gets the report of their own company.</summary>
    public bool CompanyAdmins { get; set; } = true;

    public static async Task<ReportMailSettings> LoadAsync(ISettings s, CancellationToken ct)
    {
        var raw = await s.GetAsync(Key, ct); if (raw == null) return new ReportMailSettings();
        try { return JsonSerializer.Deserialize<ReportMailSettings>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new ReportMailSettings(); } catch (JsonException) { return new ReportMailSettings(); }
    }
}

/// <summary>Mails last month's report once, to the people chosen in Connections. Running again changes nothing: each report is recorded as sent.</summary>
public sealed class ReportMailJob : IJobHandler
{
    public const string JobType = "servicedesk.reportmail";
    public string Type => JobType;

    static string Hours(double? h) => h == null ? "no data" : h < 48 ? h + " hours" : Math.Round(h.Value / 24) + " days";
    static string Pct(double? p) => p == null ? "no data" : p + "%";

    internal static string Render(string title, string month, ReportsApi.ReportData d)
    {
        var sb = new StringBuilder();
        sb.Append(title).Append(" - service report for ").Append(month).Append("\n\n");
        sb.Append("Tickets opened: ").Append(d.Opened).Append('\n').Append("Tickets solved: ").Append(d.Resolved).Append('\n');
        sb.Append("Open at the start of the month: ").Append(d.BacklogStart).Append('\n').Append("Open at the end of the month: ").Append(d.BacklogEnd).Append('\n');
        sb.Append("Average first reply: ").Append(Hours(d.AvgFirst)).Append('\n').Append("Average time to solve: ").Append(Hours(d.AvgSolve)).Append('\n');
        sb.Append("Replied in time: ").Append(Pct(d.RespondPct)).Append('\n').Append("Solved in time: ").Append(Pct(d.ResolvePct)).Append('\n');
        void List(string head, List<ReportsApi.Counted> rows) { if (rows.Count == 0) return; sb.Append('\n').Append(head).Append(":\n"); foreach (var r in rows) sb.Append("  ").Append(r.Name).Append(": ").Append(r.Count).Append('\n'); }
        List("By category", d.ByCategory); List("By priority", d.ByPriority); List("Devices with most tickets", d.ByAsset);
        sb.Append("\nThe full report with every ticket is in the Service Desk under Reports.");
        return sb.ToString();
    }

    public async Task RunAsync(JobContext job, CancellationToken ct)
    {
        var settings = job.Services.GetService<ISettings>(); var mail = job.Services.GetService<IMailQueue>();
        if (settings == null || mail == null) return;
        var cfg = await ReportMailSettings.LoadAsync(settings, ct);
        string? forced = null; var force = false;
        try { var p = JsonDocument.Parse(string.IsNullOrWhiteSpace(job.Payload) ? "{}" : job.Payload).RootElement; if (p.TryGetProperty("month", out var m) && m.ValueKind == JsonValueKind.String) forced = m.GetString(); force = p.TryGetProperty("force", out var f) && f.ValueKind == JsonValueKind.True; } catch (JsonException) { }
        if (!cfg.Enabled && !force) return;
        var first = DateTime.SpecifyKind(new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1), DateTimeKind.Utc).AddMonths(-1);
        if (forced != null && DateTime.TryParseExact(forced, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var fm)) first = fm;
        var end = first.AddMonths(1); var month = first.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        await using var db = new NpgsqlConnection(job.Tenant.ConnectionString); await db.OpenAsync(ct);

        async Task<bool> Sent(string scope, string to)
        {
            await using var c = new NpgsqlCommand("select 1 from servicedesk.report_sent where month = @m and scope = @s and recipient = @r", db);
            c.Parameters.AddWithValue("m", month); c.Parameters.AddWithValue("s", scope); c.Parameters.AddWithValue("r", to.ToLowerInvariant());
            return await c.ExecuteScalarAsync(ct) != null;
        }
        async Task Record(string scope, string to)
        {
            await using var c = new NpgsqlCommand("insert into servicedesk.report_sent (month, scope, recipient) values (@m,@s,@r) on conflict do nothing", db);
            c.Parameters.AddWithValue("m", month); c.Parameters.AddWithValue("s", scope); c.Parameters.AddWithValue("r", to.ToLowerInvariant()); await c.ExecuteNonQueryAsync(ct);
        }

        var internalTo = cfg.To.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Where(a => a.Contains('@')).ToList();
        if (internalTo.Count > 0)
        {
            var todo = new List<string>(); foreach (var a in internalTo) if (force || !await Sent("all", a)) todo.Add(a);
            if (todo.Count > 0)
            {
                var body = Render("All companies", month, await ReportsApi.Build(db, first, end, null, true, ct));
                foreach (var a in todo) if (await mail.EnqueueAsync(a, "Service report " + month, body, "report", ct)) await Record("all", a);
            }
        }
        var users = job.Services.GetService<IUserDirectory>(); var companies = job.Services.GetService<ICompanyDirectory>();
        if (cfg.CompanyAdmins && users != null && companies != null)
        {
            var links = new List<(Guid user, int company)>();
            await using (var c = new NpgsqlCommand("select user_id, company_id from servicedesk.people where company_id is not null", db))
            { await using var rd = await c.ExecuteReaderAsync(ct); while (await rd.ReadAsync(ct)) links.Add((rd.GetGuid(0), rd.GetInt32(1))); }
            foreach (var g in links.GroupBy(l => l.company))
            {
                var admins = new List<UserRef>();
                foreach (var l in g) { var u = await users.GetAsync(l.user, ct); if (u is { Active: true, Role: "cadmin" } && u.Email.Contains('@')) admins.Add(u); }
                var todo = admins.Where(a => true).ToList(); var pending = new List<UserRef>(); foreach (var a in todo) if (force || !await Sent("co:" + g.Key, a.Email)) pending.Add(a);
                if (pending.Count == 0) continue;
                var co = await companies.GetAsync(g.Key, ct); if (co == null) continue;
                var data = await ReportsApi.Build(db, first, end, g.Key, false, ct);
                if (data.Opened == 0 && data.BacklogEnd == 0) continue;
                var body = Render(co.Name, month, data);
                foreach (var a in pending) if (await mail.EnqueueAsync(a.Email, "Service report " + month + " - " + co.Name, body, "report", ct)) await Record("co:" + g.Key, a.Email);
            }
        }
    }
}

internal static class ReportMailApi
{
    public static void Map(IEndpointRouteBuilder r, string key)
    {
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        RouteHandlerBuilder Admin(RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole("admin")).RequireModule(key);
        Admin(r.MapGet("/api/servicedesk/integration/reportmail", async (ISettings s, CancellationToken ct) => Results.Ok(await ReportMailSettings.LoadAsync(s, ct))));
        Admin(r.MapPut("/api/servicedesk/integration/reportmail", async (JsonElement b, ISettings s, IAuditLog audit, CancellationToken ct) =>
        {
            ReportMailSettings? cfg; try { cfg = b.Deserialize<ReportMailSettings>(opts); } catch (JsonException) { cfg = null; }
            if (cfg == null) return ServiceDeskModule.Bad("bad_input", "That is not a valid setting.");
            cfg.To = (cfg.To ?? "").Trim(); if (cfg.To.Length > 500) return ServiceDeskModule.Bad("bad_input", "Too many addresses.");
            if (cfg.Enabled && cfg.To == "" && !cfg.CompanyAdmins) return ServiceDeskModule.Bad("bad_input", "Choose who gets the report: your own address, the customer admins, or both.");
            await s.SetAsync(ReportMailSettings.Key, JsonSerializer.Serialize(cfg, opts), ct);
            await audit.WriteAsync("servicedesk.reportmail_settings", cfg.Enabled ? "on" : "off", cfg.To, ct: ct);
            return Results.Ok(cfg);
        }));
        Admin(r.MapPost("/api/servicedesk/integration/reportmail/send", async (JsonElement b, IJobQueue jobs, IAuditLog audit, CancellationToken ct) =>
        {
            var month = b.ValueKind == JsonValueKind.Object && b.TryGetProperty("month", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            if (month != null && !DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return ServiceDeskModule.Bad("bad_month", "month must look like 2026-10");
            await jobs.EnqueueAsync(ReportMailJob.JobType, JsonSerializer.Serialize(new { month, force = true }), null, null, ct);
            await audit.WriteAsync("servicedesk.reportmail_sent_now", month ?? "last month", "", ct: ct);
            return Results.Ok(new { ok = true });
        }));
    }
}
