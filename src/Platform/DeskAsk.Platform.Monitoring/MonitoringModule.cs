using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DeskAsk.Platform.Monitoring;

/// <summary>
/// Monitoring (module "monitoring"). Watches the devices the Assets module knows (agents, site controllers, service checks, hardware readers)
/// against alert rules, every minute. An alert opens when a rule's condition holds, mails the recipients, can open a Service Desk ticket,
/// and closes by itself when the condition has cleared twice in a row. A device can be muted (maintenance).
/// </summary>
public sealed class MonitoringModule : IPlatformModule
{
    public string Key => Modules.Monitoring;
    public string Schema => "monitoring";

    public void AddServices(IServiceCollection s, IConfiguration cfg)
    {
        s.AddSingleton<ITenantSchema, MonitoringSchema>();
        s.AddSingleton<IJobHandler, EvaluateJob>();
        s.AddSingleton(new RecurringJob("monitoring.evaluate", EvaluateJob.JobType, TimeSpan.FromMinutes(1)));
    }

    public void MapEndpoints(IEndpointRouteBuilder r)
    {
        MonitoringApi.Map(r, Key);
        RulesApi.Map(r, Key);
    }

    internal static IResult Bad(string error, string? detail = null) => Results.BadRequest(new { error, detail });
    internal static async Task<NpgsqlConnection> Open(ITenantContext t, CancellationToken ct) { var db = new NpgsqlConnection(t.Require().ConnectionString); await db.OpenAsync(ct); return db; }
    internal static void P(NpgsqlCommand c, string n, object? v) => c.Parameters.AddWithValue(n, v ?? DBNull.Value);
    internal static string Actor(System.Security.Claims.ClaimsPrincipal u) => u.FindFirst("email")?.Value ?? u.FindFirst("name")?.Value ?? u.FindFirst("sub")?.Value ?? "unknown";

    /// <summary>"5 minutes", "2 hours 10 minutes", "3 days 4 hours".</summary>
    internal static string Dur(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        string U(long n, string w) => n + " " + w + (n == 1 ? "" : "s");
        if (t.TotalMinutes < 1) return "less than a minute";
        if (t.TotalHours < 1) return U((long)t.TotalMinutes, "minute");
        if (t.TotalDays < 1) return U((long)t.TotalHours, "hour") + (t.Minutes > 0 ? " " + U(t.Minutes, "minute") : "");
        return U((long)t.TotalDays, "day") + (t.Hours > 0 ? " " + U(t.Hours, "hour") : "");
    }

    internal static string Bytes(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB", "PB" }; double v = b; var i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return (i == 0 ? v.ToString("0") : v.ToString(v >= 100 ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture)) + " " + u[i];
    }
}

public sealed class MonitoringSchema : ITenantSchema
{
    public string Name => "monitoring";
    public static readonly Migration[] Migrations =
    {
        new(1, "rules, alerts, mutes and the starting rules", @"
            create schema if not exists monitoring;
            create table if not exists monitoring.rules (
              id serial primary key, name text not null,
              enabled boolean not null default true,
              metric text not null check (metric in ('offline','cpu','memory','disk','check','cert','hardware')),
              threshold real not null default 0, minutes int not null default 0,
              severity text not null check (severity in ('warning','critical')),
              kinds text[] not null default '{}', company_id int,
              mail boolean not null default true, ticket boolean not null default false, ticket_priority text not null default 'high',
              builtin boolean not null default false, created_at timestamptz not null default now(), updated_at timestamptz not null default now());
            create table if not exists monitoring.alerts (
              id bigserial primary key, akey text not null, rule_id int references monitoring.rules(id) on delete set null, rule_name text not null,
              metric text not null, severity text not null, asset_id bigint not null, asset_name text not null, asset_tag text not null default '',
              company_id int, company_name text not null default '', subject text not null default '', text text not null, value real,
              opened_at timestamptz not null default now(), last_at timestamptz not null default now(), clear_count int not null default 0,
              closed_at timestamptz, close_reason text not null default '', ack_by text not null default '', ack_at timestamptz,
              ticket_id bigint, ticket_number text not null default '', mailed boolean not null default false);
            create unique index if not exists alerts_open on monitoring.alerts (akey) where closed_at is null;
            create index if not exists alerts_asset on monitoring.alerts (asset_id, opened_at desc);
            create index if not exists alerts_opened on monitoring.alerts (opened_at desc);
            create table if not exists monitoring.mutes (
              asset_id bigint primary key, until timestamptz, reason text not null default '', by_name text not null default '', at timestamptz not null default now());
            insert into monitoring.rules (name, enabled, metric, threshold, minutes, severity, kinds, mail, ticket, ticket_priority, builtin) values
              ('Server or network device down', true, 'offline', 0, 5, 'critical', '{server,network}', true, true, 'critical', true),
              ('Computer not reporting for a day', false, 'offline', 0, 1440, 'warning', '{computer}', true, false, 'medium', true),
              ('Processor above 90% for 15 minutes', true, 'cpu', 90, 15, 'warning', '{server}', true, false, 'high', true),
              ('Memory above 90% for 15 minutes', true, 'memory', 90, 15, 'warning', '{server}', true, false, 'high', true),
              ('Disk space below 10%', true, 'disk', 10, 0, 'warning', '{}', true, false, 'medium', true),
              ('Disk space below 5%', true, 'disk', 5, 0, 'critical', '{}', true, true, 'high', true),
              ('Service check failing', true, 'check', 2, 0, 'critical', '{}', true, true, 'high', true),
              ('Certificate expires within 14 days', true, 'cert', 14, 0, 'warning', '{}', true, false, 'medium', true),
              ('Hardware fault', true, 'hardware', 0, 0, 'critical', '{}', true, true, 'high', true)"),
    };
    public Task EnsureAsync(IServiceProvider services, TenantInfo tenant, CancellationToken ct) => SchemaMigrator.RunAsync(tenant.ConnectionString, Name, Migrations, ct);
}

/// <summary>Who gets alert mail: the default list, or a customer's own list when one is set. Kept in the customer's settings.</summary>
public sealed class MonitorSettings
{
    public const string Key = "monitoring.settings";
    public string Recipients { get; set; } = "";
    public bool MailResolved { get; set; } = true;
    public Dictionary<string, string> Companies { get; set; } = new();

    public static async Task<MonitorSettings> LoadAsync(ISettings s, CancellationToken ct)
    {
        var raw = await s.GetAsync(Key, ct);
        if (string.IsNullOrWhiteSpace(raw)) return new();
        try { return JsonSerializer.Deserialize<MonitorSettings>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new(); } catch (JsonException) { return new(); }
    }
    public Task SaveAsync(ISettings s, CancellationToken ct) => s.SetAsync(Key, JsonSerializer.Serialize(this, new JsonSerializerOptions(JsonSerializerDefaults.Web)), ct);

    public static List<string> Split(string list) => list.Split(new[] { ',', ';', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    public List<string> For(int? companyId) => Split(companyId is int c && Companies.TryGetValue(c.ToString(), out var own) && own.Trim() != "" ? own : Recipients);
}
