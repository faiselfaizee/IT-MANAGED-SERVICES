using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DeskAsk.Platform.Backup;

public sealed class BackupCheckJob : IJobHandler
{
    public const string JobType = "backup.check";
    public string Type => JobType;
    public async Task RunAsync(JobContext job, CancellationToken ct)
    {
        var lic = await job.Services.GetRequiredService<IEntitlements>().CurrentAsync(ct);
        if (!lic.Has(Modules.Backup)) return;
        await BackupChecker.RunAsync(job.Services, ct);
    }
}

/// <summary>
/// Every five minutes (and after new results arrive): reads new syslog lines the site controllers stored, works out each job's status
/// (a job that sent nothing for its interval plus the grace hours is missed), and opens or clears alerts with mail and a ticket.
/// </summary>
public static class BackupChecker
{
    public const string CursorKey = "backup.syslog_cursor";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>ignored, missed, waiting (no run yet), or the newest run's result (success, warning, failed).</summary>
    public static string Status(bool watched, int everyHours, string lastResult, DateTime? lastAt, DateTime created, int graceHours, DateTime now)
    {
        if (!watched) return "ignored";
        if (everyHours > 0 && (lastAt ?? created) < now.AddHours(-(everyHours + Math.Max(0, graceHours)))) return "missed";
        return lastResult == "" ? "waiting" : lastResult;
    }

    /// <summary>The alert a job should have now: missed, failed (after enough failures in a row), warning (when warnings alert), or none.</summary>
    public static string Wanted(string status, int fails, BackupSettings s) => status switch
    {
        "missed" => "missed",
        "failed" when fails >= Math.Max(1, s.FailuresBeforeAlert) => "failed",
        "warning" when s.AlertWarnings => "warning",
        _ => "",
    };

    /// <summary>Two checks never overlap: a check that finds another one running skips (the running one sees the same data).</summary>
    public static async Task RunAsync(IServiceProvider sp, CancellationToken ct)
    {
        var tenant = sp.GetRequiredService<ITenantContext>();
        await using var db = await BackupModule.Open(tenant, ct);
        await using (var l = new NpgsqlCommand("select pg_try_advisory_lock(727311)", db)) if (await l.ExecuteScalarAsync(ct) is not true) return;
        try
        {
            await ReadSyslog(sp, db, ct);
            await Evaluate(sp, db, ct);
        }
        finally { await using var u = new NpgsqlCommand("select pg_advisory_unlock(727311)", db); await u.ExecuteScalarAsync(CancellationToken.None); }
    }

    static async Task ReadSyslog(IServiceProvider sp, NpgsqlConnection db, CancellationToken ct)
    {
        var settings = sp.GetRequiredService<ISettings>(); var health = sp.GetRequiredService<IDeviceHealth>();
        long cursor = long.TryParse(await settings.GetAsync(CursorKey, ct), NumberStyles.Integer, Inv, out var cv) ? cv : 0;
        Dictionary<long, DeviceState>? devices = null;
        for (var round = 0; round < 5; round++)
        {
            var evs = await health.EventsAfterAsync(cursor, "syslog", 2000, ct);
            if (evs.Count == 0) break;
            foreach (var e in evs)
            {
                cursor = Math.Max(cursor, e.Id);
                var p = BackupParse.Syslog(e.Source, e.Text); if (p == null) continue;
                devices ??= (await health.SnapshotAsync(null, null, ct)).ToDictionary(d => d.Id);
                if (!devices.TryGetValue(e.AssetId, out var d)) continue;
                await BackupRecorder.RecordAsync(db, new RunOrigin(d.CompanyId, d.Company, d.Id, d.Name, "syslog"),
                    new BackupReport(p.Tool, p.Job, null, DateTime.SpecifyKind(e.At, DateTimeKind.Utc), p.Result, p.Message, 0, "syslog:" + e.Id), ct);
            }
            await settings.SetAsync(CursorKey, cursor.ToString(Inv), ct);
            if (evs.Count < 2000) break;
        }
    }

    sealed record Row(long Id, int? CompanyId, string Company, string AssetName, string Tool, string Name, int Every, bool Watched, string LastResult,
        DateTime? LastAt, DateTime? LastSuccess, string LastMessage, int Fails, string Alert, long? TicketId, DateTime Created);

    static async Task Evaluate(IServiceProvider sp, NpgsqlConnection db, CancellationToken ct)
    {
        var s = await BackupSettings.LoadAsync(sp.GetRequiredService<ISettings>(), ct);
        var now = DateTime.UtcNow;
        var rows = new List<Row>();
        await using (var c = new NpgsqlCommand(@"select id, company_id, company, asset_name, tool, name, every_hours, watched, last_result, last_at, last_success_at, last_message,
                fails, alert, ticket_id, created_at from backup.jobs", db))
        await using (var rd = await c.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct))
                rows.Add(new Row(rd.GetInt64(0), rd.IsDBNull(1) ? null : rd.GetInt32(1), rd.GetString(2), rd.GetString(3), rd.GetString(4), rd.GetString(5), rd.GetInt32(6), rd.GetBoolean(7),
                    rd.GetString(8), rd.IsDBNull(9) ? null : rd.GetDateTime(9), rd.IsDBNull(10) ? null : rd.GetDateTime(10), rd.GetString(11), rd.GetInt32(12), rd.GetString(13),
                    rd.IsDBNull(14) ? null : rd.GetInt64(14), rd.GetDateTime(15)));
        var mail = sp.GetService<IMailQueue>();
        ITicketDesk? desk = null;
        if (s.Ticket && (await sp.GetRequiredService<IEntitlements>().CurrentAsync(ct)).Has(Modules.ServiceDesk)) desk = sp.GetService<ITicketDesk>();
        foreach (var r in rows)
        {
            var status = Status(r.Watched, r.Every, r.LastResult, r.LastAt, r.Created, s.GraceHours, now);
            var want = Wanted(status, r.Fails, s);
            if (want == r.Alert) continue;
            if (r.Alert == "")
            {   // a new alert: mail and (when switched on) a ticket
                long? tid = null; var number = "";
                var text = Text(want, r, now, s);
                if (desk != null)
                {
                    try
                    {
                        var opened = await desk.OpenAsync(new TicketRequest($"[Backup {Word(want)}] {(r.Company != "" ? r.Company + ": " : "")}{r.Name}",
                            $"{text}\n\nJob: {r.Name}\nSoftware: {BackupModule.ToolNames.GetValueOrDefault(r.Tool, r.Tool)}\nDevice: {(r.AssetName == "" ? "-" : r.AssetName)}\nCustomer: {(r.Company == "" ? "-" : r.Company)}\n\nOpened by Backup. The ticket gets a note when the job is back to normal.",
                            s.TicketPriority is "low" or "medium" or "high" or "critical" ? s.TicketPriority : "high", r.CompanyId, null, r.AssetName, "backup", "Backup"), ct);
                        tid = opened.Id; number = opened.Number;
                    }
                    catch (Exception) { tid = null; number = ""; }
                }
                await using (var u = new NpgsqlCommand("update backup.jobs set alert = @a, alert_at = now(), ticket_id = @t, ticket_number = @n where id = @i", db))
                { BackupModule.P(u, "a", want); BackupModule.P(u, "t", tid); BackupModule.P(u, "n", number); BackupModule.P(u, "i", r.Id); await u.ExecuteNonQueryAsync(ct); }
                await Mail(mail, s, "backup_alert", r, want, text, number, ct);
            }
            else if (want != "")
            {   // still wrong, in another way (failed, then nothing at all): the alert follows, no second mail
                await using var u = new NpgsqlCommand("update backup.jobs set alert = @a where id = @i", db);
                BackupModule.P(u, "a", want); BackupModule.P(u, "i", r.Id); await u.ExecuteNonQueryAsync(ct);
            }
            else
            {   // back to normal (or no longer watched)
                await using (var u = new NpgsqlCommand("update backup.jobs set alert = '', alert_at = null, ticket_id = null, ticket_number = '' where id = @i", db))
                { BackupModule.P(u, "i", r.Id); await u.ExecuteNonQueryAsync(ct); }
                var text = r.Watched ? $"The job is back to normal: the latest run finished {(r.LastResult == "warning" ? "with a warning" : "successfully")}." : "The job is no longer watched.";
                if (r.TicketId is long tk && desk != null) try { await desk.NoteAsync(tk, text, ct); } catch (Exception) { }
                if (r.Watched && s.MailResolved) await Mail(mail, s, "backup_resolved", r, "resolved", text, "", ct);
            }
        }
    }

    static string Word(string alert) => alert switch { "missed" => "missed", "warning" => "warning", "resolved" => "back to normal", _ => "failed" };

    static string Text(string alert, Row r, DateTime now, BackupSettings s) => alert switch
    {
        "missed" => r.LastAt is DateTime la
            ? $"No result has arrived for {BackupModule.Dur(now - la)}; the job is expected every {r.Every} hours (plus {s.GraceHours} hours' grace)."
            : $"No result has arrived since the job was first seen {BackupModule.Dur(now - r.Created)} ago; it is expected every {r.Every} hours.",
        "warning" => "The latest run finished with a warning. " + r.LastMessage,
        _ => (r.Fails > 1 ? $"The last {r.Fails} runs failed. " : "The latest run failed. ") + r.LastMessage,
    };

    static async Task Mail(IMailQueue? mail, BackupSettings s, string template, Row r, string alert, string text, string ticket, CancellationToken ct)
    {
        if (mail == null) return;
        var values = new Dictionary<string, string>
        {
            ["result"] = alert switch { "missed" => "Missed", "warning" => "Warning", "resolved" => "Back to normal", _ => "Failed" },
            ["job"] = r.Name, ["tool"] = BackupModule.ToolNames.GetValueOrDefault(r.Tool, r.Tool), ["device"] = r.AssetName == "" ? "-" : r.AssetName,
            ["company"] = r.Company == "" ? "-" : r.Company, ["message"] = text,
            ["last_success"] = r.LastSuccess is DateTime ls ? ls.ToString("yyyy-MM-dd HH:mm 'UTC'", Inv) : "never",
            ["ticket"] = ticket == "" ? "" : "Ticket: " + ticket, ["path"] = "#/backup/job/" + r.Id,
        };
        foreach (var to in s.For(r.CompanyId))
            try { await mail.EnqueueTemplateAsync(template, to, values, $"backup:{r.Id}", ct); } catch (Exception) { /* mail is best effort */ }
    }
}
