using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DeskAsk.Platform.Backup;

/// <summary>
/// Backup (module "backup"), first part: watching the backup software customers already run. Results arrive four ways:
/// the agent reads Veeam, Windows Server Backup, Azure Backup Server (MABS/DPM) and the Azure Recovery Services agent (MARS) on the device;
/// the site controller receives syslog from Synology, QNAP and Veeam; report mails from known senders (Acronis, Synology, Veeam and others);
/// and a web address for scripts with a key. Each job gets its runs, a status (success, warning, failed, missed, waiting) and alerts by mail and ticket.
/// </summary>
public sealed class BackupModule : IPlatformModule
{
    public string Key => Modules.Backup;
    public string Schema => "backup";

    public void AddServices(IServiceCollection s, IConfiguration cfg)
    {
        s.AddSingleton<ITenantSchema, BackupSchema>();
        s.AddScoped<IBackupReports, BackupStore>();
        s.AddScoped<IInboundMailFilter, BackupMailFilter>();
        s.AddSingleton<IJobHandler, BackupCheckJob>();
        s.AddSingleton(new RecurringJob("backup.check", BackupCheckJob.JobType, TimeSpan.FromMinutes(5)));
    }

    public void MapEndpoints(IEndpointRouteBuilder r) => BackupApi.Map(r, Key);

    internal static IResult Bad(string error, string? detail = null) => Results.BadRequest(new { error, detail });
    internal static async Task<NpgsqlConnection> Open(ITenantContext t, CancellationToken ct) { var db = new NpgsqlConnection(t.Require().ConnectionString); await db.OpenAsync(ct); return db; }
    internal static void P(NpgsqlCommand c, string n, object? v) => c.Parameters.AddWithValue(n, v ?? DBNull.Value);
    internal static string Clip(string? s, int max) { s ??= ""; s = s.Trim(); return s.Length <= max ? s : s[..max]; }
    internal static string Actor(System.Security.Claims.ClaimsPrincipal u) => u.FindFirst("email")?.Value ?? u.FindFirst("name")?.Value ?? u.FindFirst("sub")?.Value ?? "unknown";

    public static readonly string[] Tools = { "veeam", "wsb", "mars", "mabs", "synology", "qnap", "acronis", "azure", "macrium", "other" };
    public static readonly Dictionary<string, string> ToolNames = new()
    {
        ["veeam"] = "Veeam", ["wsb"] = "Windows Server Backup", ["mars"] = "Azure Recovery Services agent (MARS)", ["mabs"] = "Azure Backup Server (MABS/DPM)",
        ["synology"] = "Synology", ["qnap"] = "QNAP", ["acronis"] = "Acronis", ["azure"] = "Microsoft Azure Backup", ["macrium"] = "Macrium", ["other"] = "Other",
    };
    internal static string Tool(string? t) { t = (t ?? "").Trim().ToLowerInvariant(); return Tools.Contains(t) ? t : "other"; }

    /// <summary>"5 minutes", "2 hours", "3 days".</summary>
    internal static string Dur(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        string U(long n, string w) => n + " " + w + (n == 1 ? "" : "s");
        if (t.TotalMinutes < 1) return "less than a minute";
        if (t.TotalHours < 1) return U((long)t.TotalMinutes, "minute");
        if (t.TotalDays < 1) return U((long)t.TotalHours, "hour");
        return U((long)t.TotalDays, "day");
    }
}

public sealed class BackupSchema : ITenantSchema
{
    public string Name => "backup";
    public static readonly Migration[] Migrations =
    {
        new(1, "jobs, runs, tools seen on devices, mail senders and report keys", @"
            create schema if not exists backup;
            create table if not exists backup.jobs (
              id bigserial primary key, jkey text not null unique,
              company_id int, company text not null default '', asset_id bigint, asset_name text not null default '',
              tool text not null, name text not null,
              source text not null check (source in ('agent','syslog','mail','api')),
              every_hours int not null default 24 check (every_hours between 0 and 744),
              watched boolean not null default true, notes text not null default '',
              last_result text not null default '' check (last_result in ('','success','warning','failed')),
              last_at timestamptz, last_success_at timestamptz, last_message text not null default '', last_bytes bigint not null default 0,
              fails int not null default 0,
              alert text not null default '' check (alert in ('','failed','warning','missed')), alert_at timestamptz,
              ticket_id bigint, ticket_number text not null default '',
              created_at timestamptz not null default now());
            create index if not exists jobs_company on backup.jobs (company_id);
            create index if not exists jobs_asset on backup.jobs (asset_id);
            create table if not exists backup.runs (
              id bigserial primary key, job_id bigint not null references backup.jobs(id) on delete cascade,
              started timestamptz, ended timestamptz not null,
              result text not null check (result in ('success','warning','failed')),
              message text not null default '', bytes bigint not null default 0, rkey text not null default '',
              created_at timestamptz not null default now());
            create unique index if not exists runs_key on backup.runs (job_id, rkey) where rkey <> '';
            create index if not exists runs_job on backup.runs (job_id, ended desc);
            create table if not exists backup.tools (
              asset_id bigint not null, tool text not null, seen_at timestamptz not null default now(), primary key (asset_id, tool));
            create table if not exists backup.senders (
              id serial primary key, pattern text not null unique, company_id int, tool text not null default '',
              created_at timestamptz not null default now());
            create table if not exists backup.keys (
              id serial primary key, name text not null, company_id int, hash text not null unique, prefix text not null,
              created_at timestamptz not null default now(), last_used timestamptz);"),
    };
    public Task EnsureAsync(IServiceProvider services, TenantInfo tenant, CancellationToken ct) => SchemaMigrator.RunAsync(tenant.ConnectionString, Name, Migrations, ct);
}

/// <summary>Alert settings (setting "backup.settings").</summary>
public sealed class BackupSettings
{
    public const string Key = "backup.settings";
    /// <summary>Everyone who hears about every customer's backups.</summary>
    public string Recipients { get; set; } = "";
    /// <summary>Extra recipients for one customer's backups, by customer id (they get theirs in addition to the list above).</summary>
    public Dictionary<string, string> Companies { get; set; } = new();
    /// <summary>A job alerts after this many failed runs in a row.</summary>
    public int FailuresBeforeAlert { get; set; } = 1;
    /// <summary>Whether a run that finished with a warning alerts too.</summary>
    public bool AlertWarnings { get; set; }
    /// <summary>A job is missed when nothing arrived for its interval plus this many hours.</summary>
    public int GraceHours { get; set; } = 6;
    public bool Ticket { get; set; } = true;
    public string TicketPriority { get; set; } = "high";
    public bool MailResolved { get; set; } = true;

    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    public static async Task<BackupSettings> LoadAsync(ISettings s, CancellationToken ct)
    {
        var raw = await s.GetAsync(Key, ct);
        if (string.IsNullOrWhiteSpace(raw)) return new();
        try { return JsonSerializer.Deserialize<BackupSettings>(raw, Web) ?? new(); } catch (JsonException) { return new(); }
    }
    public Task SaveAsync(ISettings s, CancellationToken ct) => s.SetAsync(Key, JsonSerializer.Serialize(this, Web), ct);

    public static List<string> Split(string list) => list.Split(new[] { ',', ';', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    public List<string> For(int? companyId)
    {
        var all = Split(Recipients);
        if (companyId is int c && Companies.TryGetValue(c.ToString(), out var own)) all.AddRange(Split(own));
        return all.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
