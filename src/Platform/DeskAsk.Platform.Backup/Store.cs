using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DeskAsk.Platform.Backup;

/// <summary>Where a run came from, and whose it is.</summary>
public sealed record RunOrigin(int? CompanyId, string Company, long? AssetId, string AssetName, string Source);

/// <summary>Stores backup runs: one job per customer, device, tool and job name, and each run once (by its key).</summary>
public static class BackupRecorder
{
    static string JobKey(RunOrigin o, string tool, string name) =>
        $"{o.CompanyId?.ToString() ?? "-"}|{(o.AssetId is long a && a > 0 ? a.ToString() : (o.AssetName == "" ? "-" : "n:" + o.AssetName.ToLowerInvariant()))}|{tool}|{name.Trim().ToLowerInvariant()}";

    /// <summary>A key for a run that came without one: the same text at the same minute is the same run.</summary>
    public static string Fingerprint(params string[] parts) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001f", parts))))[..32];

    /// <summary>Stores one run. Returns the job id and whether the run was new.</summary>
    public static async Task<(long JobId, bool New)> RecordAsync(NpgsqlConnection db, RunOrigin o, BackupReport r, CancellationToken ct)
    {
        var tool = BackupModule.Tool(r.Tool);
        var name = BackupModule.Clip(r.Job, 200); if (name == "") name = "Backup";
        var ended = (r.Ended ?? r.Started ?? DateTime.UtcNow).ToUniversalTime();
        if (ended > DateTime.UtcNow.AddHours(1)) ended = DateTime.UtcNow;   // a device clock far ahead must not hide later runs
        var started = r.Started?.ToUniversalTime();
        var key = BackupModule.Clip(r.Key, 200);
        if (key == "") key = Fingerprint(tool, name, ended.ToString("yyyy-MM-ddTHH:mm"), r.Result);
        long jobId;
        await using (var c = new NpgsqlCommand(@"insert into backup.jobs (jkey, company_id, company, asset_id, asset_name, tool, name, source)
            values (@k, @co, @cn, @a, @an, @t, @n, @s)
            on conflict (jkey) do update set company = excluded.company, asset_name = case when excluded.asset_name <> '' then excluded.asset_name else backup.jobs.asset_name end
            returning id", db))
        {
            BackupModule.P(c, "k", JobKey(o, tool, name)); BackupModule.P(c, "co", o.CompanyId); BackupModule.P(c, "cn", o.Company);
            BackupModule.P(c, "a", o.AssetId is long a && a > 0 ? a : null); BackupModule.P(c, "an", BackupModule.Clip(o.AssetName, 200));
            BackupModule.P(c, "t", tool); BackupModule.P(c, "n", name); BackupModule.P(c, "s", o.Source);
            jobId = (long)(await c.ExecuteScalarAsync(ct))!;
        }
        long runId;
        await using (var c = new NpgsqlCommand(@"insert into backup.runs (job_id, started, ended, result, message, bytes, rkey) values (@j, @st, @e, @r, @m, @b, @k)
            on conflict (job_id, rkey) where rkey <> '' do nothing returning id", db))
        {
            BackupModule.P(c, "j", jobId); BackupModule.P(c, "st", started); BackupModule.P(c, "e", ended); BackupModule.P(c, "r", r.Result);
            BackupModule.P(c, "m", BackupModule.Clip(r.Message, 2000)); BackupModule.P(c, "b", Math.Max(0, r.Bytes)); BackupModule.P(c, "k", key);
            if (await c.ExecuteScalarAsync(ct) is not long id) return (jobId, false);
            runId = id;
        }
        // the job shows its newest run; failures in a row count towards an alert, a good run starts again from nothing
        await using (var c = new NpgsqlCommand(@"update backup.jobs set
                last_result = @r, last_at = @e, last_message = @m, last_bytes = @b,
                last_success_at = case when @r in ('success','warning') then greatest(coalesce(last_success_at, @e), @e) else last_success_at end,
                fails = case when @r = 'failed' then fails + 1 else 0 end
            where id = @j and (last_at is null or last_at <= @e)", db))
        {
            BackupModule.P(c, "j", jobId); BackupModule.P(c, "r", r.Result); BackupModule.P(c, "e", ended);
            BackupModule.P(c, "m", BackupModule.Clip(r.Message, 2000)); BackupModule.P(c, "b", Math.Max(0, r.Bytes));
            if (await c.ExecuteNonQueryAsync(ct) == 0 && r.Result is "success" or "warning")
            {   // an older good run that arrived late still counts as the last success
                await using var u = new NpgsqlCommand("update backup.jobs set last_success_at = @e where id = @j and (last_success_at is null or last_success_at < @e)", db);
                BackupModule.P(u, "j", jobId); BackupModule.P(u, "e", ended); await u.ExecuteNonQueryAsync(ct);
            }
        }
        _ = runId;
        return (jobId, true);
    }
}

/// <summary>Backup results from agents (passed on by the Assets module).</summary>
public sealed class BackupStore : IBackupReports
{
    readonly ITenantContext tenant; readonly IServiceProvider sp;
    public BackupStore(ITenantContext t, IServiceProvider services) { tenant = t; sp = services; }

    public async Task<int> FromAgentAsync(long assetId, IReadOnlyList<string> tools, IReadOnlyList<BackupReport> reports, CancellationToken ct = default)
    {
        var dev = (await sp.GetRequiredService<IDeviceHealth>().SnapshotAsync(null, assetId, ct)).FirstOrDefault();
        if (dev == null) return 0;
        await using var db = await BackupModule.Open(tenant, ct);
        foreach (var t in tools.Select(BackupModule.Tool).Distinct())
        {
            await using var c = new NpgsqlCommand("insert into backup.tools (asset_id, tool) values (@a, @t) on conflict (asset_id, tool) do update set seen_at = now()", db);
            BackupModule.P(c, "a", assetId); BackupModule.P(c, "t", t); await c.ExecuteNonQueryAsync(ct);
        }
        var origin = new RunOrigin(dev.CompanyId, dev.Company, dev.Id, dev.Name, "agent");
        var n = 0;
        foreach (var r in reports) if ((await BackupRecorder.RecordAsync(db, origin, r, ct)).New) n++;
        if (n > 0) await BackupChecker.RunAsync(sp, ct);
        return n;
    }
}

/// <summary>
/// Keeps report mails from the senders listed under Backup › Settings (an address, or @domain for a whole domain): the result and job come
/// from the subject, the customer from the sender. Mail from anyone else goes on to the Service Desk as before.
/// </summary>
public sealed class BackupMailFilter : IInboundMailFilter
{
    readonly ITenantContext tenant; readonly IServiceProvider sp;
    public BackupMailFilter(ITenantContext t, IServiceProvider services) { tenant = t; sp = services; }

    public static bool Matches(string pattern, string from)
    {
        pattern = pattern.Trim().ToLowerInvariant(); from = from.Trim().ToLowerInvariant();
        if (pattern == "" || from == "") return false;
        if (pattern.StartsWith("*@")) pattern = pattern[1..];
        return pattern.StartsWith('@') ? from.EndsWith(pattern) : from == pattern;
    }

    public async Task<string?> TakeAsync(InboundMail m, CancellationToken ct = default)
    {
        // only senders listed under Backup › Settings are taken (an empty list takes nothing)
        await using var db = await BackupModule.Open(tenant, ct);
        (int? Company, string Tool)? sender = null;
        await using (var c = new NpgsqlCommand("select pattern, company_id, tool from backup.senders order by length(pattern) desc", db))
        await using (var rd = await c.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct))
                if (Matches(rd.GetString(0), m.FromAddr)) { sender = (rd.IsDBNull(1) ? null : rd.GetInt32(1), rd.GetString(2)); break; }
        if (sender == null) return null;
        var p = BackupParse.Mail(m.FromAddr, m.Subject, m.Body, sender.Value.Tool);
        if (p == null) return "backup";   // a known sender's mail that reports no result (a summary, a notice): kept, never a ticket
        var company = "";
        if (sender.Value.Company is int ci) company = (await sp.GetRequiredService<ICompanyDirectory>().GetAsync(ci, ct))?.Name ?? "";
        long? assetId = null; var assetName = p.Device;
        if (p.Device != "")
        {
            var devices = await sp.GetRequiredService<IDeviceHealth>().SnapshotAsync(sender.Value.Company is int c0 ? new[] { c0 } : null, null, ct);
            var hit = devices.FirstOrDefault(d => string.Equals(d.Name, p.Device, StringComparison.OrdinalIgnoreCase) || string.Equals(d.Name.Split('.')[0], p.Device.Split('.')[0], StringComparison.OrdinalIgnoreCase));
            if (hit != null) { assetId = hit.Id; assetName = hit.Name; }
        }
        var at = m.At.UtcDateTime;
        await BackupRecorder.RecordAsync(db, new RunOrigin(sender.Value.Company, company, assetId, assetName, "mail"),
            new BackupReport(p.Tool, p.Job, null, at, p.Result, p.Message, 0, "mail:" + BackupModule.Clip(m.MessageId, 180)), ct);
        await BackupChecker.RunAsync(sp, ct);
        return "backup";
    }
}
