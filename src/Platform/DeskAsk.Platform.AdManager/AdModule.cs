using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DeskAsk.Platform.AdManager;

/// <summary>
/// AD Manager (module "adsuite"): manage a customer's Active Directory from the platform.
///
/// The platform never connects to a domain controller itself. Each customer's controller (the small program already installed in their
/// network) does the LDAP work and answers the job (IControllerRelay), so no inbound path to the customer is needed and it works the same
/// for the hosted and the on-premises edition. The service account's password is kept sealed here and travels only inside a job.
///
/// What people may do is decided per domain by delegations: a person or security group, an OU (or the whole domain) and a list of rights.
/// Administrators may do everything. Deleting, and changing privileged groups or privileged accounts, needs a second person with the
/// "approve" right.
/// </summary>
public sealed class AdModule : IPlatformModule
{
    public string Key => Modules.AdSuite;
    public string Schema => "adsuite";

    public void AddServices(IServiceCollection s, IConfiguration cfg)
    {
        s.AddSingleton<ITenantSchema, AdSchema>();
        s.AddScoped<ILicenseUsage, AdLicenseUsage>();
        s.AddSingleton<IJobHandler, AutomationJob>();
        s.AddSingleton(new RecurringJob("adsuite.automation", AutomationJob.JobType, TimeSpan.FromHours(24)));
    }

    public void MapEndpoints(IEndpointRouteBuilder r)
    {
        DomainsApi.Map(r, Key);
        BrowseApi.Map(r, Key);
        ReportsApi.Map(r, Key);
        RequestsApi.Map(r, Key);
        ManageApi.Map(r, Key);
    }

    // ---- shared helpers

    internal static IResult Bad(string error, string? detail = null) => Results.BadRequest(new { error, detail });
    internal static IResult Forbid(string detail) => Results.Json(new { error = "forbidden", detail }, statusCode: 403);
    internal static async Task<NpgsqlConnection> Open(ITenantContext t, CancellationToken ct) { var db = new NpgsqlConnection(t.Require().ConnectionString); await db.OpenAsync(ct); return db; }
    internal static void P(NpgsqlCommand c, string n, object? v) => c.Parameters.AddWithValue(n, v ?? DBNull.Value);
    internal static Guid Me(ClaimsPrincipal u) => Guid.TryParse(u.FindFirst("sub")?.Value, out var g) ? g : Guid.Empty;
    internal static string Actor(ClaimsPrincipal u) => u.FindFirst("email")?.Value ?? u.FindFirst("name")?.Value ?? u.FindFirst("sub")?.Value ?? "unknown";
    internal static string Role(ClaimsPrincipal u) => u.FindFirst("role")?.Value ?? "";
    internal static bool IsAdmin(ClaimsPrincipal u) => Role(u) == "admin";
    internal static string? Str(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    internal static int? Int(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    internal static bool Bool(JsonElement e, string n, bool d = false) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) ? v.ValueKind == JsonValueKind.True || (v.ValueKind != JsonValueKind.False && d) : d;
    internal static string Clip(string? s, int n) => s == null ? "" : s.Length <= n ? s : s[..n];

    /// <summary>Staff and customer administrators may be given rights; customer users (role customer) never.</summary>
    internal static RouteHandlerBuilder Use(RouteHandlerBuilder b, string key) =>
        b.RequireAuthorization(p => p.RequireRole("admin", "engineer", "viewer", "cadmin")).RequireModule(key);
    internal static RouteHandlerBuilder AdminOnly(RouteHandlerBuilder b, string key) =>
        b.RequireAuthorization(p => p.RequireRole("admin")).RequireModule(key);

    /// <summary>The things a person can be allowed to do, from least to most.</summary>
    public static readonly string[] Rights = { "read", "unlock", "reset", "enable", "modify", "members", "create", "move", "delete", "approve" };
}

public sealed class AdSchema : ITenantSchema
{
    public string Name => "adsuite";
    public static readonly Migration[] Migrations =
    {
        new(1, "domains, delegations, account requests, approvals", @"
            create schema if not exists adsuite;
            create table if not exists adsuite.domains (
              id serial primary key, company_id int not null, name text not null, dns_name text not null default '',
              base_dn text not null, servers text not null, tls boolean not null default true, skip_verify boolean not null default false,
              bind_dn text not null, secret_sealed text not null, controller_id text not null,
              enabled boolean not null default true, created_at timestamptz not null default now(),
              last_ok timestamptz, last_error text not null default '', unique (company_id, base_dn));
            create table if not exists adsuite.delegations (
              id serial primary key, domain_id int not null references adsuite.domains(id) on delete cascade,
              principal_kind text not null check (principal_kind in ('user','group')), principal_id uuid not null, principal_name text not null default '',
              ou_dn text not null default '', rights text[] not null, created_by text not null default '', created_at timestamptz not null default now());
            create index if not exists delegations_d on adsuite.delegations (domain_id);
            create table if not exists adsuite.requests (
              id serial primary key, domain_id int not null references adsuite.domains(id) on delete cascade,
              kind text not null check (kind in ('internal','external','service')),
              status text not null default 'pending' check (status in ('pending','rejected','created','failed')),
              first_name text not null default '', last_name text not null default '', display_name text not null default '',
              sam text not null, upn text not null default '', email text not null default '', ou_dn text not null,
              title text not null default '', department text not null default '', manager_dn text not null default '',
              expires_on date, groups jsonb not null default '[]', notes text not null default '',
              requested_by text not null, requested_by_id uuid, decided_by text not null default '', decided_at timestamptz,
              decision_note text not null default '', result_dn text not null default '', error text not null default '', created_at timestamptz not null default now());
            create table if not exists adsuite.approvals (
              id serial primary key, domain_id int not null references adsuite.domains(id) on delete cascade,
              action text not null, target_dn text not null, target_name text not null default '', reason text not null default '', body jsonb not null,
              status text not null default 'pending' check (status in ('pending','rejected','done','failed')),
              requested_by text not null, requested_by_id uuid, decided_by text not null default '', decided_at timestamptz,
              decision_note text not null default '', error text not null default '', created_at timestamptz not null default now())"),
        new(2, "a trusted LDAPS certificate per domain", @"
            alter table adsuite.domains add column if not exists cert_sha256 text not null default '';"),
        new(3, "user templates and automation policies", @"
            create table if not exists adsuite.templates (
              id serial primary key, domain_id int not null references adsuite.domains(id) on delete cascade,
              name text not null, description text not null default '', ou text not null default '', groups text[] not null default '{}',
              attrs jsonb not null default '{}', naming jsonb not null default '{}', options jsonb not null default '{}',
              created_by text not null default '', created_at timestamptz not null default now(), unique (domain_id, name));
            create table if not exists adsuite.policies (
              id serial primary key, domain_id int not null references adsuite.domains(id) on delete cascade,
              name text not null, kind text not null check (kind in ('disable-inactive-users','disable-stale-computers','move-disabled-users')),
              days int not null default 90, ou text not null default '', move_to text not null default '', enabled boolean not null default false,
              last_run timestamptz, last_result text not null default '', created_by text not null default '', created_at timestamptz not null default now());"),
    };
    public async Task EnsureAsync(IServiceProvider services, TenantInfo tenant, CancellationToken ct) =>
        await SchemaMigrator.RunAsync(tenant.ConnectionString, Name, Migrations, ct);
}
