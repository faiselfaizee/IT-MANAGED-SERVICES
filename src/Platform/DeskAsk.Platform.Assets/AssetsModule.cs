using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace DeskAsk.Platform.Assets;

/// <summary>Schema "assets" in every customer database: asset types (with their own extra fields), assets and their history.</summary>
public sealed class AssetsSchema : ITenantSchema
{
    public string Name => "assets";
    public static readonly Migration[] Migrations =
    {
        new(1, "types, assets, history", @"
            create schema if not exists assets;
            create sequence if not exists assets.tag_seq;
            create table if not exists assets.types (
              id serial primary key, name text not null, fields text not null default '[]', created_at timestamptz not null default now());
            create unique index if not exists types_name on assets.types (lower(name));
            create table if not exists assets.assets (
              id bigserial primary key,
              tag text not null default ('AST-' || lpad(nextval('assets.tag_seq')::text, 6, '0')),
              name text not null, type_id int references assets.types(id),
              status text not null default 'stock' check (status in ('stock','in_use','repair','retired','lost')),
              manufacturer text not null default '', model text not null default '', serial text not null default '',
              location text not null default '', assigned_to text not null default '',
              purchase_date date, purchase_cost numeric(12,2), warranty_end date,
              notes text not null default '', custom text not null default '{}', source text not null default 'manual',
              created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
              created_by text not null default '', updated_by text not null default '');
            create unique index if not exists assets_tag on assets.assets (tag);
            create unique index if not exists assets_serial on assets.assets (lower(serial)) where serial <> '';
            create index if not exists assets_status on assets.assets (status);
            create index if not exists assets_type on assets.assets (type_id);
            create index if not exists assets_assigned on assets.assets (lower(assigned_to));
            create table if not exists assets.history (
              id bigserial primary key, asset_id bigint not null references assets.assets(id) on delete cascade,
              at timestamptz not null default now(), actor text not null, action text not null, changes text not null default '{}');
            create index if not exists history_asset on assets.history (asset_id, id)"),
        new(2, "companies, device fields, AMC, rentals", @"
            create table if not exists assets.companies (
              id serial primary key, name text not null, code text not null default '', contact_name text not null default '', contact_email text not null default '',
              phone text not null default '', address text not null default '', notes text not null default '',
              status text not null default 'active' check (status in ('active','inactive')), remote_allowed boolean not null default false,
              created_at timestamptz not null default now());
            create unique index if not exists companies_name on assets.companies (lower(name));
            alter table assets.assets
              add column if not exists company_id int references assets.companies(id) on delete set null,
              add column if not exists category text not null default '',
              add column if not exists hostname text not null default '',
              add column if not exists dns_name text not null default '',
              add column if not exists ip_address text not null default '',
              add column if not exists os_family text not null default '',
              add column if not exists os_name text not null default '',
              add column if not exists os_version text not null default '',
              add column if not exists cpu_make text not null default '',
              add column if not exists cpu_model text not null default '',
              add column if not exists cpu_socket text not null default '',
              add column if not exists total_cpu int not null default 0,
              add column if not exists cpu_cores int not null default 0,
              add column if not exists cores_per_cpu int not null default 0,
              add column if not exists logical_processors int not null default 0,
              add column if not exists ram_bytes bigint not null default 0,
              add column if not exists total_storage_bytes bigint not null default 0,
              add column if not exists disks jsonb not null default '[]',
              add column if not exists nics jsonb not null default '[]',
              add column if not exists mac_addresses jsonb not null default '[]',
              add column if not exists connected_ips jsonb not null default '[]',
              add column if not exists installed_apps jsonb not null default '[]',
              add column if not exists antivirus jsonb not null default '[]',
              add column if not exists antivirus_status text not null default '',
              add column if not exists domain_type text not null default '',
              add column if not exists domain_workgroup text not null default '',
              add column if not exists bios_version text not null default '',
              add column if not exists last_patch_date date,
              add column if not exists inventory_at timestamptz,
              add column if not exists agent_id text not null default '',
              add column if not exists remote_ok boolean not null default true,
              add column if not exists last_seen timestamptz,
              add column if not exists uptime_s bigint not null default 0;
            create index if not exists assets_company on assets.assets (company_id);
            create index if not exists assets_host on assets.assets (lower(hostname)) where hostname <> '';
            create table if not exists assets.amc (
              id serial primary key, company_id int references assets.companies(id) on delete set null, title text not null,
              start_date date, end_date date, coverage text not null default '', visits int not null default 0, sla_pct int not null default 0,
              notes text not null default '', asset_ids jsonb not null default '[]', updated_at timestamptz not null default now());
            create table if not exists assets.rentals (
              id serial primary key, company_id int references assets.companies(id) on delete set null, title text not null,
              start_date date, end_date date, notes text not null default '', asset_ids jsonb not null default '[]', updated_at timestamptz not null default now())"),
        new(3, "agents, enrolment tokens, remote commands", @"
            create table if not exists assets.enroll_tokens (
              id serial primary key, company_id int references assets.companies(id) on delete cascade, label text not null default '', token_hash text not null, prefix text not null,
              token_enc text not null default '', created_at timestamptz not null default now(), expires_at timestamptz, uses int not null default 0, revoked boolean not null default false,
              created_by text not null default '', asset_id bigint references assets.assets(id) on delete cascade, monitor_only boolean not null default false);
            create index if not exists enroll_tokens_hash on assets.enroll_tokens (token_hash);
            create table if not exists assets.agents (
              id text primary key, company_id int references assets.companies(id) on delete set null, asset_id bigint references assets.assets(id) on delete cascade,
              hostname text not null default '', platform text not null default '', version text not null default '', key_hash text not null,
              last_seen timestamptz, ip text not null default '', created_at timestamptz not null default now(), shell_ok boolean not null default true, locked boolean not null default false,
              rd_id text not null default '', rd_state text not null default '', rd_mode text not null default '', rd_pw text not null default '', rd_msg text not null default '', rd_at timestamptz);
            create index if not exists agents_asset on assets.agents (asset_id);
            create table if not exists assets.agent_q (
              id bigserial primary key, agent_id text not null references assets.agents(id) on delete cascade, kind text not null, payload text not null,
              created_at timestamptz not null default now(), delivered boolean not null default false);
            create index if not exists agent_q_pending on assets.agent_q (agent_id) where not delivered;
            create table if not exists assets.commands (
              id serial primary key, title text not null, description text not null default '', category text not null default 'General',
              os text not null default 'any' check (os in ('windows','linux','any')), shell text not null check (shell in ('cmd','powershell','bash')), body text not null,
              risk text not null default 'safe' check (risk in ('safe','caution','disruptive')), params text not null default '[]', timeout_s int not null default 120,
              builtin boolean not null default false, enabled boolean not null default true, pos int not null default 0);
            create unique index if not exists commands_builtin on assets.commands (lower(title), os) where builtin;
            create table if not exists assets.jobs (
              id text primary key, asset_id bigint not null references assets.assets(id) on delete cascade, agent_id text not null, shell text not null, title text not null default '', body text not null,
              status text not null default 'queued', by_name text not null default '', created_at timestamptz not null default now(), started_at timestamptz, finished_at timestamptz,
              exit_code int, output text not null default '', command_id int, timeout_s int not null default 120);
            create index if not exists jobs_asset on assets.jobs (asset_id, created_at desc)"),
        new(4, "live shells and remote control", @"
            create table if not exists assets.shells (
              id text primary key, asset_id bigint not null references assets.assets(id) on delete cascade, agent_id text not null, shell text not null,
              by_name text not null, created_at timestamptz not null default now(), closed boolean not null default false, last_in timestamptz not null default now(),
              seq int not null default 0, input_log text not null default '');
            create table if not exists assets.shell_out (
              sid text not null references assets.shells(id) on delete cascade, seq int not null, at timestamptz not null default now(), data text not null, primary key (sid, seq));
            create table if not exists assets.rc_log (
              id bigserial primary key, agent_id text not null, at timestamptz not null default now(), level text not null default 'info', text text not null default '');
            create index if not exists rc_log_agent on assets.rc_log (agent_id, id)"),
        new(5, "discovery, collectors, service checks, handovers, ticket links", @"
            alter table assets.enroll_tokens add column if not exists kind text not null default 'agent';
            alter table assets.assets
              add column if not exists collector_id text not null default '',
              add column if not exists monitor text not null default '',
              add column if not exists check_up boolean,
              add column if not exists check_rtt int not null default 0,
              add column if not exists checked_at timestamptz,
              add column if not exists unalloc_at timestamptz,
              add column if not exists prev_holder text not null default '';
            create table if not exists assets.creds (
              id serial primary key, name text not null, kind text not null default 'windows' check (kind in ('windows','ssh','snmp','login')),
              username text not null default '', secret text not null, company_id int references assets.companies(id) on delete set null, created_at timestamptz not null default now());
            create table if not exists assets.collectors (
              id text primary key, company_id int references assets.companies(id) on delete set null, name text not null default '', key_hash text not null,
              platform text not null default '', version text not null default '', hostname text not null default '', ip text not null default '',
              last_seen timestamptz, created_at timestamptz not null default now(), monitor_default text not null default 'ping',
              scan_cidrs text not null default '', scan_req boolean not null default false);
            create table if not exists assets.found (
              collector_id text not null references assets.collectors(id) on delete cascade, ip text not null, hostname text not null default '', open jsonb not null default '[]',
              mac text not null default '', cat text not null default '', sub text not null default '', seen_at timestamptz not null default now(), primary key (collector_id, ip));
            create table if not exists assets.collector_log (
              id bigserial primary key, collector_id text not null references assets.collectors(id) on delete cascade, at timestamptz not null default now(),
              level text not null default 'info', text text not null default '');
            create index if not exists collector_log_c on assets.collector_log (collector_id, id);
            create table if not exists assets.deploys (
              asset_id bigint primary key references assets.assets(id) on delete cascade, collector_id text not null, status text not null default 'queued',
              message text not null default '', at timestamptz not null default now());
            create table if not exists assets.service_checks (
              id serial primary key, asset_id bigint not null references assets.assets(id) on delete cascade, name text not null,
              type text not null check (type in ('tcp','http','ssl','dns')), host text not null default '', port int not null default 0, url text not null default '',
              expect text not null default '', status text not null default '', insecure boolean not null default false, dns text not null default '',
              timeout_s int not null default 10, enabled boolean not null default true, created_at timestamptz not null default now(),
              last_at timestamptz, last_ok boolean, last_ms real not null default 0, last_days real, last_detail text not null default '');
            create unique index if not exists service_checks_name on assets.service_checks (asset_id, lower(name));
            create table if not exists assets.check_results (
              id bigserial primary key, check_id int not null references assets.service_checks(id) on delete cascade, at timestamptz not null default now(),
              ok boolean not null, ms real not null default 0, days real);
            create index if not exists check_results_c on assets.check_results (check_id, id);
            create table if not exists assets.handovers (
              id bigserial primary key, asset_id bigint not null references assets.assets(id) on delete cascade, kind text not null check (kind in ('assign','handover')),
              holder text not null default '', at timestamptz not null default now(), by_name text not null default '', cond text not null default 'good',
              accessories jsonb not null default '[]', notes text not null default '');
            create index if not exists handovers_asset on assets.handovers (asset_id, id);
            create table if not exists assets.ticket_links (
              id bigserial primary key, asset_id bigint not null references assets.assets(id) on delete cascade, ref text not null default '', title text not null,
              priority text not null default 'normal', status text not null default 'open', source text not null default 'manual', note text not null default '',
              created_at timestamptz not null default now(), created_by text not null default '');
            create index if not exists ticket_links_asset on assets.ticket_links (asset_id, id)"),
        new(6, "renewal reminders", @"
            create table if not exists assets.renewal_notices (
              kind text not null, ref_id bigint not null, due date not null, days int not null, sent_at timestamptz not null default now(),
              primary key (kind, ref_id, due, days))"),
        new(7, "delete approvals, SSH password logins", @"
            create table if not exists assets.delete_requests (
              id serial primary key, asset_id bigint not null, tag text not null default '', name text not null default '',
              requested_by text not null, requested_by_id text not null default '', reason text not null default '',
              status text not null default 'pending' check (status in ('pending','approved','rejected','cancelled')),
              decided_by text not null default '', note text not null default '', decided_at timestamptz, created_at timestamptz not null default now());
            create index if not exists delete_requests_status on assets.delete_requests (status, created_at desc);
            alter table assets.creds drop constraint if exists creds_kind_check;
            alter table assets.creds add constraint creds_kind_check check (kind in ('windows','ssh','sshpw','snmp','login'))"),
        new(8, "credential account types", @"
            alter table assets.creds add column if not exists account_type text not null default ''"),
        new(9, "recycle bin for agents and enrolment tokens", @"
            alter table assets.agents add column if not exists deleted_at timestamptz;
            alter table assets.enroll_tokens add column if not exists deleted_at timestamptz"),
        new(10, "live install log, instant wake-up and automatic agent install for controllers", @"
            alter table assets.collectors add column if not exists auto_install boolean not null default false;
            alter table assets.collectors add column if not exists wake_req boolean not null default false;
            alter table assets.deploys add column if not exists attempts int not null default 0;
            create table if not exists assets.deploy_log (
              id bigserial primary key, asset_id bigint not null references assets.assets(id) on delete cascade, at timestamptz not null default now(),
              level text not null default 'info', text text not null default '');
            create index if not exists deploy_log_a on assets.deploy_log (asset_id, id)"),
        new(11, "credentials by customer and hardware category", @"
            alter table assets.creds drop constraint if exists creds_kind_check;
            alter table assets.creds add constraint creds_kind_check check (kind in ('windows','ssh','sshpw','snmp','login','bmc','vmware','nutanix','netssh','netapi'));
            alter table assets.creds add column if not exists category text not null default '';
            alter table assets.creds add column if not exists vendor text not null default '';
            alter table assets.creds add column if not exists notes text not null default '';
            create index if not exists creds_company_category on assets.creds (company_id, category)"),
        new(12, "update an agent from the asset page with a live log", @"
            alter table assets.agents add column if not exists upd_state text not null default '';
            alter table assets.agents add column if not exists upd_to text not null default '';
            alter table assets.agents add column if not exists upd_at timestamptz;
            create table if not exists assets.agent_upd_log (
              id bigserial primary key, agent_id text not null references assets.agents(id) on delete cascade, at timestamptz not null default now(),
              level text not null default 'info', text text not null default '');
            create index if not exists agent_upd_log_a on assets.agent_upd_log (agent_id, id)"),
        new(13, "hardware read by a controller (VMware, later servers and network gear) and read now", @"
            alter table assets.assets add column if not exists hw_extra jsonb not null default '{}';
            alter table assets.assets add column if not exists read_req boolean not null default false"),
        new(14, "a server's management controller (iLO, iDRAC, XClarity) read beside the main read", @"
            alter table assets.assets add column if not exists bmc_address text not null default '';
            alter table assets.assets add column if not exists bmc_extra jsonb not null default '{}';
            alter table assets.assets add column if not exists bmc_at timestamptz"),
        new(15, "logins for Proxmox VE and XCP-ng / Citrix Hypervisor", @"
            alter table assets.creds drop constraint if exists creds_kind_check;
            alter table assets.creds add constraint creds_kind_check check (kind in ('windows','ssh','sshpw','snmp','login','bmc','vmware','nutanix','proxmox','xen','netssh','netapi'))"),
        new(16, "configuration backups of network devices", @"
            create table if not exists assets.configs (
              id bigserial primary key, asset_id bigint not null references assets.assets(id) on delete cascade, at timestamptz not null default now(),
              source text not null default '', vendor text not null default '', text text not null, size int not null default 0, lines int not null default 0, hash text not null);
            create index if not exists configs_asset on assets.configs (asset_id, id);
            alter table assets.assets add column if not exists config_checked_at timestamptz;
            alter table assets.assets add column if not exists config_error text not null default ''"),
        new(17, "collectors in the recycle bin", @"
            alter table assets.collectors add column if not exists deleted_at timestamptz;
            alter table assets.collectors add column if not exists binned_assets jsonb not null default '[]'"),
        new(18, "SNMPv3 logins", @"
            alter table assets.creds drop constraint if exists creds_kind_check;
            alter table assets.creds add constraint creds_kind_check check (kind in ('windows','ssh','sshpw','snmp','snmp3','login','bmc','vmware','nutanix','proxmox','xen','netssh','netapi'));
            alter table assets.creds add column if not exists auth_proto text not null default '';
            alter table assets.creds add column if not exists priv_proto text not null default '';
            alter table assets.creds add column if not exists secret2 text not null default ''"),
        new(19, "an uninstalled agent keeps its record until the order is delivered", @"
            alter table assets.agents add column if not exists uninstall_at timestamptz"),
        new(20, "performance data for Monitoring", @"
            create table if not exists assets.metrics (
              asset_id bigint not null references assets.assets(id) on delete cascade, at timestamptz not null default now(),
              cpu real, mem real, disks jsonb not null default '[]', primary key (asset_id, at));
            create index if not exists metrics_at on assets.metrics (at);
            create table if not exists assets.metrics_hour (
              asset_id bigint not null references assets.assets(id) on delete cascade, hour timestamptz not null,
              cpu_avg real, cpu_max real, mem_avg real, mem_max real, disk_free_min real, n int not null default 0, primary key (asset_id, hour));
            alter table assets.assets add column if not exists m_at timestamptz;
            alter table assets.assets add column if not exists m_cpu real;
            alter table assets.assets add column if not exists m_mem real;
            alter table assets.assets add column if not exists m_mem_total bigint not null default 0;
            alter table assets.assets add column if not exists m_disks jsonb not null default '[]';
            alter table assets.assets add column if not exists m_uptime bigint not null default 0;
            alter table assets.assets add column if not exists check_down_at timestamptz;
            alter table assets.service_checks add column if not exists fails int not null default 0"),
    };
    public async Task EnsureAsync(IServiceProvider services, TenantInfo tenant, CancellationToken ct)
    {
        await SchemaMigrator.RunAsync(tenant.ConnectionString, Name, Migrations, ct);
        await CommandLibrary.SeedAsync(tenant.ConnectionString, ct);
    }
}

/// <summary>
/// Asset management (module "assets", licence limit "max_assets"). Types define extra fields per kind of asset (text, number, date, yes/no, choice).
/// Everyone signed in may look; administrators change. Every change is kept in the asset's history and in the audit log.
/// </summary>
public sealed class AssetsModule : IPlatformModule
{
    public string Key => Modules.Assets;
    public string Schema => "assets";

    static readonly string[] Statuses = { "stock", "in_use", "repair", "retired", "lost" };
    static readonly string[] TextColumns = { "manufacturer", "model", "serial", "location", "assigned_to" };
    static readonly Regex TagRx = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,39}$", RegexOptions.Compiled);
    /// <summary>Mails everyone who may approve the deletion (administrators, and people or groups granted Assets delete for that customer), except the requester.</summary>
    static async Task NotifyApprovers(HttpContext http, NpgsqlConnection db, long id, string tag, string name, string by, string bySub, string reason, CancellationToken ct)
    {
        var sp = http.RequestServices; var users = sp.GetService<IUserDirectory>(); var mail = sp.GetService<IMailQueue>(); var access = sp.GetService<IAccess>();
        if (users == null || mail == null || access == null) return;
        int? company = null;
        await using (var c = new NpgsqlCommand("select company_id from assets.assets where id = @i", db)) { P(c, "i", id); if (await c.ExecuteScalarAsync(ct) is int co) company = co; }
        var link = $"{http.Request.Scheme}://{http.Request.Host}/#/assets/delete-requests";
        var body = $"{by} asked to delete the asset {tag} ({name}).\n\nReason: {reason}\n\nApprove or reject it under Assets > Delete requests:\n{link}\n";
        var sent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in await users.ListAsync(ct))
        {
            if (!u.Active || !u.Email.Contains('@') || u.Id.ToString() == bySub || u.Role is "customer" || !sent.Add(u.Email)) continue;
            var who = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", u.Id.ToString()), new Claim("role", u.Role), new Claim("email", u.Email) }, "approver"));
            try { if (!await access.AllowedAsync(who, Modules.Assets, Acts.Delete, company, ct)) { sent.Remove(u.Email); continue; } }
            catch (Exception) { sent.Remove(u.Email); continue; }
            try { await mail.EnqueueAsync(u.Email, $"Asset deletion to approve: {tag}", body, "asset-delete:" + id, ct); } catch (Exception) { /* mail is best effort */ }
        }
    }

    const string Cols = "a.id, a.tag, a.name, a.type_id, t.name, a.status, a.manufacturer, a.model, a.serial, a.location, a.assigned_to, a.purchase_date, a.purchase_cost, a.warranty_end, a.notes, a.custom, a.source, a.created_at, a.updated_at";
    const string From = "assets.assets a left join assets.types t on t.id = a.type_id";

    public void AddServices(IServiceCollection s, IConfiguration cfg)
    {
        s.AddSingleton<ITenantSchema, AssetsSchema>();
        s.AddSingleton<AgentHub>();
        s.AddScoped<AssetsDirectory>();
        s.AddScoped<ICompanyDirectory>(sp => sp.GetRequiredService<AssetsDirectory>());
        s.AddScoped<IAssetDirectory>(sp => sp.GetRequiredService<AssetsDirectory>());
        s.AddScoped<IDeviceHealth, DeviceHealth>();
        s.AddSingleton<IJobHandler, AssetsJanitorJob>();
        s.AddSingleton(new RecurringJob("assets.janitor", AssetsJanitorJob.JobType, TimeSpan.FromMinutes(1)));
        s.AddSingleton<IJobHandler, RenewalsJob>();
        s.AddSingleton(new RecurringJob("assets.renewals", RenewalsJob.JobType, TimeSpan.FromHours(6)));
    }

    sealed record FieldDef(string Key, string Label, string Kind, string[] Options);

    static IResult Bad(string error, string? detail = null) => Results.BadRequest(new { error, detail });
    static string Actor(ClaimsPrincipal u) => u.FindFirstValue("email") ?? u.FindFirstValue("sub") ?? "unknown";
    static async Task<NpgsqlConnection> Open(ITenantContext t, CancellationToken ct) { var db = new NpgsqlConnection(t.Require().ConnectionString); await db.OpenAsync(ct); return db; }
    static void P(NpgsqlCommand c, string n, object? v) => c.Parameters.AddWithValue(n, v ?? DBNull.Value);
    static string Money(decimal m) => decimal.Round(m, 2).ToString("0.00", CultureInfo.InvariantCulture);
    static string D(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";

    // ------------------------------------------------------------------ type definitions
    static List<FieldDef>? ParseFields(JsonElement el, out string? error)
    {
        error = null; var list = new List<FieldDef>();
        if (el.ValueKind != JsonValueKind.Array) { error = "fields must be a list"; return null; }
        if (el.GetArrayLength() > 30) { error = "at most 30 extra fields per type"; return null; }
        foreach (var f in el.EnumerateArray())
        {
            string Get(string n) => f.ValueKind == JsonValueKind.Object && f.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            var key = Get("key"); var label = Get("label"); var kind = Get("kind");
            if (!Regex.IsMatch(key, "^[a-z][a-z0-9_]{0,29}$")) { error = "field key '" + key + "': lower case letters, digits, '_', starting with a letter"; return null; }
            if (list.Any(x => x.Key == key)) { error = "field key '" + key + "' is used twice"; return null; }
            if (kind is not ("text" or "number" or "date" or "bool" or "choice")) { error = "field '" + key + "': kind must be text, number, date, bool or choice"; return null; }
            var opts = Array.Empty<string>();
            if (kind == "choice")
            {
                if (!f.TryGetProperty("options", out var o) || o.ValueKind != JsonValueKind.Array || o.GetArrayLength() is 0 or > 50) { error = "field '" + key + "': choice needs 1 to 50 options"; return null; }
                opts = o.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()!.Trim() : "").ToArray();
                if (opts.Any(x => x == "" || x.Length > 100)) { error = "field '" + key + "': options must be text of 1 to 100 characters"; return null; }
            }
            list.Add(new FieldDef(key, label == "" ? key : label[..Math.Min(label.Length, 100)], kind, opts));
        }
        return list;
    }

    static async Task<List<FieldDef>> FieldsOf(NpgsqlConnection db, int? typeId, CancellationToken ct)
    {
        if (typeId == null) return new();
        await using var c = new NpgsqlCommand("select fields from assets.types where id = @i", db); P(c, "i", typeId);
        var raw = await c.ExecuteScalarAsync(ct) as string ?? "[]";
        return ParseFields(JsonDocument.Parse(raw).RootElement, out _) ?? new();
    }

    // ------------------------------------------------------------------ reading values of an asset from JSON
    sealed class Values
    {
        public Dictionary<string, object?> Set = new();   // column -> value
        public string? CustomJson;
        public string? Error;
    }

    static string? Str(JsonElement b, string name, int max, out bool present)
    {
        present = b.TryGetProperty(name, out var v);
        if (!present) return null;
        if (v.ValueKind == JsonValueKind.Null) return "";
        if (v.ValueKind != JsonValueKind.String) throw new FormatException(name + " must be text");
        var s = v.GetString()!.Trim();
        if (s.Length > max) throw new FormatException(name + " is longer than " + max + " characters");
        return s;
    }

    static DateOnly? Date(JsonElement b, string name, out bool present)
    {
        present = b.TryGetProperty(name, out var v);
        if (!present || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind == JsonValueKind.String && v.GetString() == "") return null;
        if (v.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(v.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d.Year is >= 1990 and <= 2100) return d;
        throw new FormatException(name + " must be a date like 2026-11-09");
    }

    static async Task<Values> Read(JsonElement b, bool create, NpgsqlConnection db, int? currentTypeId, CancellationToken ct)
    {
        var r = new Values();
        try
        {
            if (b.ValueKind != JsonValueKind.Object) throw new FormatException("the body must be a JSON object");
            var name = Str(b, "name", 200, out var hasName);
            if (create && string.IsNullOrEmpty(name)) throw new FormatException("name is needed");
            if (hasName) { if (string.IsNullOrEmpty(name)) throw new FormatException("name cannot be empty"); r.Set["name"] = name; }

            var typeId = currentTypeId;
            if (b.TryGetProperty("typeId", out var tv))
            {
                if (tv.ValueKind == JsonValueKind.Null) { typeId = null; r.Set["type_id"] = null; }
                else if (tv.ValueKind == JsonValueKind.Number && tv.TryGetInt32(out var tid))
                {
                    await using var c = new NpgsqlCommand("select 1 from assets.types where id = @i", db); P(c, "i", tid);
                    if (await c.ExecuteScalarAsync(ct) == null) throw new FormatException("typeId " + tid + " does not exist");
                    typeId = tid; r.Set["type_id"] = tid;
                }
                else throw new FormatException("typeId must be a number");
            }

            var status = Str(b, "status", 20, out var hasStatus);
            if (hasStatus) { if (!Statuses.Contains(status)) throw new FormatException("status must be one of " + string.Join(", ", Statuses)); r.Set["status"] = status; }

            foreach (var (json, col) in new[] { ("manufacturer", "manufacturer"), ("model", "model"), ("serial", "serial"), ("location", "location"), ("assignedTo", "assigned_to") })
            { var v = Str(b, json, 200, out var has); if (has) r.Set[col] = v; }
            var notes = Str(b, "notes", 4000, out var hasNotes); if (hasNotes) r.Set["notes"] = notes;

            var pd = Date(b, "purchaseDate", out var hasPd); if (hasPd) r.Set["purchase_date"] = pd;
            var we = Date(b, "warrantyEnd", out var hasWe); if (hasWe) r.Set["warranty_end"] = we;
            if (b.TryGetProperty("purchaseCost", out var pc))
            {
                if (pc.ValueKind == JsonValueKind.Null) r.Set["purchase_cost"] = null;
                else if (pc.ValueKind == JsonValueKind.Number && pc.TryGetDecimal(out var cost) && cost is >= 0 and < 1_000_000_000m) r.Set["purchase_cost"] = Math.Round(cost, 2);
                else throw new FormatException("purchaseCost must be a number of 0 or more");
            }

            if (create && b.TryGetProperty("tag", out var tg) && tg.ValueKind == JsonValueKind.String && tg.GetString()!.Trim() != "")
            {
                var tag = tg.GetString()!.Trim();
                if (!TagRx.IsMatch(tag)) throw new FormatException("tag: letters, digits, '.', '_' and '-', up to 40 characters");
                r.Set["tag"] = tag;
            }

            if (b.TryGetProperty("custom", out var cu))
            {
                var defs = await FieldsOf(db, typeId, ct);
                var o = new Dictionary<string, object?>();
                if (cu.ValueKind == JsonValueKind.Object)
                    foreach (var p in cu.EnumerateObject())
                    {
                        var def = defs.FirstOrDefault(x => x.Key == p.Name) ?? throw new FormatException("unknown extra field '" + p.Name + "' for this type");
                        if (p.Value.ValueKind == JsonValueKind.Null) continue;
                        o[p.Name] = def.Kind switch
                        {
                            "text" when p.Value.ValueKind == JsonValueKind.String && p.Value.GetString()!.Length <= 500 => p.Value.GetString(),
                            "number" when p.Value.ValueKind == JsonValueKind.Number => p.Value.GetDecimal(),
                            "date" when p.Value.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(p.Value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) => p.Value.GetString(),
                            "bool" when p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False => p.Value.GetBoolean(),
                            "choice" when p.Value.ValueKind == JsonValueKind.String && def.Options.Contains(p.Value.GetString()) => p.Value.GetString(),
                            _ => throw new FormatException("extra field '" + p.Name + "' does not fit its kind (" + def.Kind + ")"),
                        };
                    }
                else if (cu.ValueKind != JsonValueKind.Null) throw new FormatException("custom must be an object");
                r.CustomJson = JsonSerializer.Serialize(o);
                r.Set["custom"] = r.CustomJson;
            }
        }
        catch (FormatException e) { r.Error = e.Message; }
        return r;
    }

    // ------------------------------------------------------------------ rows
    static object Row(NpgsqlDataReader r) => new
    {
        id = r.GetInt64(0), tag = r.GetString(1), name = r.GetString(2),
        typeId = r.IsDBNull(3) ? (int?)null : r.GetInt32(3), type = r.IsDBNull(4) ? null : r.GetString(4),
        status = r.GetString(5), manufacturer = r.GetString(6), model = r.GetString(7), serial = r.GetString(8), location = r.GetString(9), assignedTo = r.GetString(10),
        purchaseDate = r.IsDBNull(11) ? null : D(r.GetFieldValue<DateOnly>(11)), purchaseCost = r.IsDBNull(12) ? (decimal?)null : r.GetDecimal(12),
        warrantyEnd = r.IsDBNull(13) ? null : D(r.GetFieldValue<DateOnly>(13)), notes = r.GetString(14),
        custom = JsonDocument.Parse(r.GetString(15)).RootElement.Clone(), source = r.GetString(16), createdAt = r.GetDateTime(17), updatedAt = r.GetDateTime(18),
    };

    static string Like(string q) => "%" + q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    static async Task<(long total, long active)> Counts(NpgsqlConnection db, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("select count(*), count(*) filter (where status not in ('retired','lost')) from assets.assets", db);
        await using var r = await c.ExecuteReaderAsync(ct); await r.ReadAsync(ct);
        return (r.GetInt64(0), r.GetInt64(1));
    }

    // ------------------------------------------------------------------ endpoints
    /// <summary>
    /// The asset list filters (search, status, type, person, agent state, customer and a dashboard ring part), shared by the list and its exports
    /// so an export holds what the screen shows. Needs the agents joined as g. Error is set when a filter value is not known.
    /// </summary>
    internal static (string? Error, string Sql, Action<NpgsqlCommand> Bind) ListFilter(IQueryCollection q, IConfiguration cfg)
    {
        var search = q["search"].ToString().Trim(); var status = q["status"].ToString(); var assigned = q["assignedTo"].ToString().Trim();
        int.TryParse(q["typeId"], out var typeId);
        if (status != "" && !Statuses.Contains(status)) return ("status", "", _ => { });
        var agentF = q["agent"].ToString(); if (agentF is not ("" or "online" or "offline" or "none" or "update")) return ("agent must be online, offline, none or update", "", _ => { });
        var coF = q["company"].ToString(); int coId = 0;
        if (coF != "" && coF != "none" && !int.TryParse(coF, out coId)) return ("company must be a customer id or none", "", _ => { });
        var w = new List<string>
        {
            "(@q = '' or a.name ilike @l or a.tag ilike @l or a.serial ilike @l or a.model ilike @l or a.assigned_to ilike @l or a.location ilike @l)",
            "(@st = '' or a.status = @st) and (@ty = 0 or a.type_id = @ty) and (@as = '' or lower(a.assigned_to) = lower(@as))",
        };
        if (agentF != "") w.Add("a.status not in ('retired','lost') and " + AssetDash.Where("agent", agentF));
        if (coF == "none") w.Add("a.company_id is null"); else if (coF != "") w.Add("a.company_id = @coF");
        foreach (var ring in new[] { "compliance", "warranty", "kind", "health" })
        {
            var part = q[ring].ToString(); if (part == "") continue;
            var cond = AssetDash.Where(ring, part); if (cond == null) return (ring + " has no part " + part, "", _ => { });
            w.Add("a.status not in ('retired','lost') and " + cond);
        }
        var latest = AgentsApi.AgentVersion(cfg); var off = AgentsApi.Offline(cfg);
        return (null, string.Join(" and ", w.Select(x => "(" + x + ")")), c =>
        {
            P(c, "q", search); P(c, "l", Like(search)); P(c, "st", status); P(c, "ty", typeId); P(c, "as", assigned);
            P(c, "off", (double)off); P(c, "lv", latest); if (coF != "" && coF != "none") P(c, "coF", coId);
        });
    }

    public void MapEndpoints(IEndpointRouteBuilder r)
    {
        var admin = (RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole("admin")).RequireModule(Key);
        var delegated = (string action, RouteHandlerBuilder b) => b.RequireAuthorization().RequireModule(Key).RequireAccess(Key, action);
        var anyone = (RouteHandlerBuilder b) => b.RequireAuthorization().RequireModule(Key).RequireAccess(Key, Acts.Read);

        AssetsDevice.Map(r, Key);
        AgentsApi.Map(r, Key);
        ShellApi.Map(r, Key);
        DiscoveryApi.Map(r, Key);
        CollectorsApi.Map(r, Key);
        ExtrasApi.Map(r, Key);
        AssetExport.Map(r, Key);
        AgentUpdateApi.Map(r, Key);
        ConfigsApi.Map(r, Key);
        AssetDash.Map(r, Key);
        DeviceHealth.Map(r, Key);
        RenewalsApi.Map(r, Key);

        // ---- types
        anyone(r.MapGet("/api/assets/types", async (ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            await using var c = new NpgsqlCommand("select t.id, t.name, t.fields, (select count(*) from assets.assets a where a.type_id = t.id) from assets.types t order by lower(t.name)", db);
            var rows = new List<object>();
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) rows.Add(new { id = rd.GetInt32(0), name = rd.GetString(1), fields = JsonDocument.Parse(rd.GetString(2)).RootElement.Clone(), assets = rd.GetInt64(3) });
            return Results.Ok(rows);
        }));

        admin(r.MapPost("/api/assets/types", async (JsonElement body, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var name = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()!.Trim() : "";
            if (name.Length is < 1 or > 100) return Bad("bad_input", "name: 1 to 100 characters");
            string? err = null;
            var fields = body.TryGetProperty("fields", out var f) ? ParseFields(f, out err) : new List<FieldDef>();
            if (fields == null) return Bad("bad_input", err);
            await using var db = await Open(t, ct);
            await using var c = new NpgsqlCommand("insert into assets.types (name, fields) values (@n, @f) on conflict do nothing returning id", db);
            P(c, "n", name); P(c, "f", FieldsJson(fields));
            if (await c.ExecuteScalarAsync(ct) is not int id) return Results.Conflict(new { error = "type_exists" });
            await audit.WriteAsync("assets.type_created", id.ToString(), name, ct: ct);
            return Results.Ok(new { id, name });
        }));

        admin(r.MapPut("/api/assets/types/{id:int}", async (int id, JsonElement body, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            var name = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()!.Trim() : null;
            if (name != null && name.Length is < 1 or > 100) return Bad("bad_input", "name: 1 to 100 characters");
            string? fieldsJson = null;
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("fields", out var f))
            {
                var fields = ParseFields(f, out var err); if (fields == null) return Bad("bad_input", err);
                fieldsJson = FieldsJson(fields);
            }
            try
            {
                await using var c = new NpgsqlCommand("update assets.types set name = coalesce(@n, name), fields = coalesce(@f, fields) where id = @i", db);
                P(c, "n", name); P(c, "f", fieldsJson); P(c, "i", id);
                if (await c.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            }
            catch (PostgresException e) when (e.SqlState == "23505") { return Results.Conflict(new { error = "type_exists" }); }
            await audit.WriteAsync("assets.type_updated", id.ToString(), name ?? "", ct: ct);
            return Results.Ok(new { ok = true });
        }));

        admin(r.MapDelete("/api/assets/types/{id:int}", async (int id, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            await using var used = new NpgsqlCommand("select count(*) from assets.assets where type_id = @i", db); P(used, "i", id);
            if ((long)(await used.ExecuteScalarAsync(ct))! > 0) return Results.Conflict(new { error = "type_in_use" });
            await using var c = new NpgsqlCommand("delete from assets.types where id = @i", db); P(c, "i", id);
            if (await c.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            await audit.WriteAsync("assets.type_deleted", id.ToString(), ct: ct);
            return Results.Ok(new { ok = true });
        }));

        // ---- assets: list, one, summary
        anyone(r.MapGet("/api/assets", async (HttpContext http, ITenantContext t, IConfiguration cfg, CancellationToken ct) =>
        {
            var scope = await AssetsAccess.ScopeAsync(http, Acts.Read);
            var q = http.Request.Query;
            int.TryParse(q["take"], out var take); int.TryParse(q["skip"], out var skip);
            take = take <= 0 ? 50 : Math.Min(take, 200); skip = Math.Max(skip, 0);
            var f = ListFilter(q, cfg); if (f.Error != null) return Bad("bad_input", f.Error);
            await using var db = await Open(t, ct);
            var latest = AgentsApi.AgentVersion(cfg); var offSec = AgentsApi.Offline(cfg);
            await using var c = new NpgsqlCommand($@"select {Cols}, count(*) over(), g.id, g.version, g.last_seen from {From} left join assets.agents g on g.id = a.agent_id and g.deleted_at is null
                where (not @sc or a.company_id = any(@scope)) and {f.Sql}
                order by a.id desc offset @sk limit @tk", db);
            AssetsAccess.Bind(c, scope); f.Bind(c); P(c, "sk", skip); P(c, "tk", take);
            var items = new List<object>(); long total = 0;
            await using (var rd = await c.ExecuteReaderAsync(ct))
                while (await rd.ReadAsync(ct))
                {
                    var row = System.Text.Json.JsonSerializer.SerializeToNode(Row(rd))!.AsObject(); total = rd.GetInt64(19);
                    if (rd.IsDBNull(20)) row["agent"] = System.Text.Json.JsonSerializer.SerializeToNode(new { state = "none" });
                    else
                    {
                        var seen = rd.IsDBNull(22) ? (DateTime?)null : rd.GetDateTime(22); var ver = rd.GetString(21);
                        var online = seen != null && seen.Value > DateTime.UtcNow.AddSeconds(-offSec);
                        row["agent"] = System.Text.Json.JsonSerializer.SerializeToNode(new { state = online ? "online" : "offline", version = ver, lastSeen = seen, updateDue = latest != "" && ver != "" && ver != latest, latest });
                    }
                    items.Add(row);
                }
            if (items.Count == 0 && skip > 0)
            {
                await using var cc = new NpgsqlCommand("select count(*) from assets.assets a where (not @sc or a.company_id = any(@scope))", db); AssetsAccess.Bind(cc, scope);
                total = (long)(await cc.ExecuteScalarAsync(ct))!;
            }
            return Results.Ok(new { total, items });
        }));

        anyone(r.MapGet("/api/assets/summary", async (HttpContext http, ITenantContext t, IConfiguration cfg, CancellationToken ct) =>
        {
            var scope = await AssetsAccess.ScopeAsync(http, Acts.Read);
            await using var db = await Open(t, ct);
            var byStatus = Statuses.ToDictionary(s => s, _ => 0L); var byType = new List<object>();
            await using (var c = new NpgsqlCommand("select status, count(*) from assets.assets a where (not @sc or a.company_id = any(@scope)) group by status", db).Scoped(scope))
            await using (var rd = await c.ExecuteReaderAsync(ct)) while (await rd.ReadAsync(ct)) byStatus[rd.GetString(0)] = rd.GetInt64(1);
            await using (var c = new NpgsqlCommand("select coalesce(t.name, '(no type)'), count(*) from assets.assets a left join assets.types t on t.id = a.type_id where (not @sc or a.company_id = any(@scope)) group by 1 order by 2 desc, 1", db).Scoped(scope))
            await using (var rd = await c.ExecuteReaderAsync(ct)) while (await rd.ReadAsync(ct)) byType.Add(new { type = rd.GetString(0), count = rd.GetInt64(1) });
            long w30, w90, expired;
            await using (var c = new NpgsqlCommand(@"select count(*) filter (where warranty_end >= current_date and warranty_end < current_date + 30),
                count(*) filter (where warranty_end >= current_date and warranty_end < current_date + 90), count(*) filter (where warranty_end < current_date)
                from assets.assets where status not in ('retired','lost') and warranty_end is not null and (not @sc or company_id = any(@scope))", db).Scoped(scope))
            await using (var rd = await c.ExecuteReaderAsync(ct)) { await rd.ReadAsync(ct); (w30, w90, expired) = (rd.GetInt64(0), rd.GetInt64(1), rd.GetInt64(2)); }
            // agents and compliance count the assets in service (not retired or lost); the four agent states do not overlap
            var latest = AgentsApi.AgentVersion(cfg); var off = (double)AgentsApi.Offline(cfg);
            long online, due, offline, none, comp, nonComp, unknown, avOff, patchOld, inWarranty, noWarranty;
            await using (var c = new NpgsqlCommand(@"select
                  count(*) filter (where g.id is not null and g.last_seen > now() - make_interval(secs => @off) and (@lv = '' or g.version = @lv)),
                  count(*) filter (where g.id is not null and g.last_seen > now() - make_interval(secs => @off) and @lv <> '' and g.version <> @lv),
                  count(*) filter (where g.id is not null and (g.last_seen is null or g.last_seen <= now() - make_interval(secs => @off))),
                  count(*) filter (where g.id is null),
                  count(*) filter (where g.id is not null and a.antivirus_status = 'Protected' and a.last_patch_date >= current_date - 30),
                  count(*) filter (where g.id is not null and (coalesce(a.antivirus_status, '') not in ('', 'Protected') or coalesce(a.last_patch_date < current_date - 30, false))),
                  count(*) filter (where g.id is not null),
                  count(*) filter (where g.id is not null and coalesce(a.antivirus_status, '') not in ('', 'Protected')),
                  count(*) filter (where g.id is not null and a.last_patch_date < current_date - 30),
                  count(*) filter (where a.warranty_end >= current_date + 30),
                  count(*) filter (where a.warranty_end is null)
                from assets.assets a left join assets.agents g on g.id = a.agent_id and g.deleted_at is null
                where a.status not in ('retired','lost') and (not @sc or a.company_id = any(@scope))", db).Scoped(scope))
            {
                P(c, "off", off); P(c, "lv", latest);
                await using var rd = await c.ExecuteReaderAsync(ct); await rd.ReadAsync(ct);
                (online, due, offline, none, comp, nonComp) = (rd.GetInt64(0), rd.GetInt64(1), rd.GetInt64(2), rd.GetInt64(3), rd.GetInt64(4), rd.GetInt64(5)); unknown = rd.GetInt64(6) - comp - nonComp; // reporting but no antivirus or patch data yet
                (avOff, patchOld, inWarranty, noWarranty) = (rd.GetInt64(7), rd.GetInt64(8), rd.GetInt64(9), rd.GetInt64(10));
            }
            return Results.Ok(new
            {
                total = byStatus.Values.Sum(), byStatus, byType, warrantyEndsIn30Days = w30, warrantyEndsIn90Days = w90, warrantyExpired = expired,
                agents = new { online, updateDue = due, offline, none, latest },
                compliance = new { compliant = comp, nonCompliant = nonComp, unknown, antivirusOff = avOff, patchesOlderThan30Days = patchOld },
                warranty = new { covered = inWarranty, endingSoon = w30, expired, unknown = noWarranty },
            });
        }));

        anyone(r.MapGet("/api/assets/{id:long}", async (HttpContext http, long id, ITenantContext t, CancellationToken ct) =>
        {
            var scope = await AssetsAccess.ScopeAsync(http, Acts.Read);
            await using var db = await Open(t, ct);
            object? asset = null;
            await using (var c = new NpgsqlCommand($"select {Cols} from {From} where a.id = @i and (not @sc or a.company_id = any(@scope))", db))
            {
                P(c, "i", id); AssetsAccess.Bind(c, scope);
                await using var rd = await c.ExecuteReaderAsync(ct);
                if (await rd.ReadAsync(ct)) asset = Row(rd);
            }
            if (asset == null) return Results.NotFound();
            var history = new List<object>();
            await using (var c = new NpgsqlCommand("select at, actor, action, changes from assets.history where asset_id = @i order by id desc limit 200", db))
            {
                P(c, "i", id);
                await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) history.Add(new { at = rd.GetDateTime(0), actor = rd.GetString(1), action = rd.GetString(2), changes = JsonDocument.Parse(rd.GetString(3)).RootElement.Clone() });
            }
            return Results.Ok(new { asset, history });
        }));

        // ---- assets: create, change, delete
        delegated(Acts.Add, r.MapPost("/api/assets", async (HttpContext http, JsonElement body, ClaimsPrincipal u, ITenantContext t, IEntitlements ent, IAuditLog audit, CancellationToken ct) =>
        {
            var scope = await AssetsAccess.ScopeAsync(http, Acts.Add);
            if (scope != null && scope.Count != 1) return Results.Json(new { error = "forbidden", detail = "You may add assets for one company only; ask an administrator." }, statusCode: 403);
            await using var db = await Open(t, ct);
            var v = await Read(body, true, db, null, ct);
            if (v.Error != null) return Bad("bad_input", v.Error);
            var (_, active) = await Counts(db, ct);
            if (!await ent.WithinLimitAsync(Key, "max_assets", active, ct)) return Results.Json(new { error = "limit_reached", limit = "max_assets" }, statusCode: 402);
            var created = await Insert(db, v, Actor(u), "manual", ct);
            if (created.Error != null) return created.Error == "duplicate" ? Results.Conflict(new { error = "duplicate", detail = created.Detail }) : Bad(created.Error, created.Detail);
            if (scope != null)
            {   // someone limited to one company adds assets to that company
                await using var own = new NpgsqlCommand("update assets.assets set company_id = @c where id = @i", db); P(own, "c", scope[0]); P(own, "i", created.Id); await own.ExecuteNonQueryAsync(ct);
            }
            await audit.WriteAsync("assets.created", created.Id.ToString(), created.Tag, ct: ct);
            return Results.Ok(new { id = created.Id, tag = created.Tag });
        }));

        delegated(Acts.Modify, r.MapPut("/api/assets/{id:long}", async (HttpContext http, long id, JsonElement body, ClaimsPrincipal u, ITenantContext t, IEntitlements ent, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            if (!await AssetsAccess.InScope(db, id, await AssetsAccess.ScopeAsync(http, Acts.Modify), ct)) return Results.NotFound();
            await using var tx = await db.BeginTransactionAsync(ct);
            Dictionary<string, string> before = new(); int? typeId = null;
            await using (var c = new NpgsqlCommand("select name, type_id, status, manufacturer, model, serial, location, assigned_to, purchase_date, purchase_cost, warranty_end, notes, custom from assets.assets where id = @i for update", db, tx))
            {
                P(c, "i", id);
                await using var rd = await c.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) return Results.NotFound();
                typeId = rd.IsDBNull(1) ? null : rd.GetInt32(1);
                string[] names = { "name", "type_id", "status", "manufacturer", "model", "serial", "location", "assigned_to", "purchase_date", "purchase_cost", "warranty_end", "notes", "custom" };
                for (var i = 0; i < names.Length; i++) before[names[i]] = rd.IsDBNull(i) ? "" : rd.GetValue(i) is DateTime dt ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : rd.GetValue(i) is decimal dm ? Money(dm) : Convert.ToString(rd.GetValue(i), CultureInfo.InvariantCulture) ?? "";
            }
            var v = await Read(body, false, db, typeId, ct);
            if (v.Error != null) return Bad("bad_input", v.Error);
            var changes = new Dictionary<string, object>();
            foreach (var (col, val) in v.Set)
            {
                var now = val switch { null => "", DateOnly d => D(d), decimal m => Money(m), _ => Convert.ToString(val, CultureInfo.InvariantCulture) ?? "" };
                var was = before[col];
                if (now != was) changes[col] = new { from = was, to = now };
            }
            if (changes.Count == 0) return Results.Ok(new { ok = true, changed = 0 });
            if (v.Set.TryGetValue("status", out var ns) && (ns as string) is not ("retired" or "lost") && (before["status"] is "retired" or "lost"))
            {   // bringing an asset back counts against the licensed number again
                var (_, active) = await Counts(db, ct);
                if (!await ent.WithinLimitAsync(Key, "max_assets", active, ct)) return Results.Json(new { error = "limit_reached", limit = "max_assets" }, statusCode: 402);
            }
            var sets = string.Join(", ", v.Set.Keys.Select((k, i) => $"{k} = @p{i}"));
            await using (var up = new NpgsqlCommand($"update assets.assets set {sets}, updated_at = now(), updated_by = @by where id = @id", db, tx))
            {
                var i = 0;
                foreach (var (k, val) in v.Set)
                {
                    var prm = new NpgsqlParameter("p" + i++, val ?? DBNull.Value);
                    if (val is null) prm.NpgsqlDbType = k is "purchase_date" or "warranty_end" ? NpgsqlDbType.Date : k == "purchase_cost" ? NpgsqlDbType.Numeric : NpgsqlDbType.Integer;
                    up.Parameters.Add(prm);
                }
                P(up, "by", Actor(u)); P(up, "id", id);
                try { await up.ExecuteNonQueryAsync(ct); }
                catch (PostgresException e) when (e.SqlState == "23505") { return Results.Conflict(new { error = "duplicate", detail = "serial number is already used by another asset" }); }
            }
            await AddHistory(db, tx, id, Actor(u), changes.ContainsKey("status") && changes.Count == 1 ? "status" : "updated", JsonSerializer.Serialize(changes), ct);
            await tx.CommitAsync(ct);
            await audit.WriteAsync("assets.updated", id.ToString(), string.Join(",", changes.Keys), ct: ct);
            return Results.Ok(new { ok = true, changed = changes.Count });
        }));

        delegated(Acts.Delete, r.MapDelete("/api/assets/{id:long}", async (HttpContext http, long id, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            if (!await AssetsAccess.InScope(db, id, await AssetsAccess.ScopeAsync(http, Acts.Delete), ct)) return Results.NotFound();
            await using var c = new NpgsqlCommand("delete from assets.assets where id = @i returning tag", db); P(c, "i", id);
            if (await c.ExecuteScalarAsync(ct) is not string tag) return Results.NotFound();
            await audit.WriteAsync("assets.deleted", id.ToString(), tag, ct: ct);
            return Results.Ok(new { ok = true });
        }));

        // ---- deleting needs the delete right (administrators, or people and groups it was granted to); everyone else asks and an approver decides
        var who = (ClaimsPrincipal u) => u.FindFirstValue("email") ?? u.FindFirstValue("sub") ?? "";
        delegated(Acts.Modify, r.MapPost("/api/assets/{id:long}/delete-request", async (HttpContext http, long id, JsonElement body, ClaimsPrincipal u, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            if (!await AssetsAccess.InScope(db, id, await AssetsAccess.ScopeAsync(http, Acts.Modify), ct)) return Results.NotFound();
            var reason = AgentsApi.Clip(AgentsApi.Str(body, "reason"), 500).Trim();
            if (reason == "") return Results.BadRequest(new { error = "bad_input", detail = "Say why this asset should be deleted." });
            string tag, name;
            await using (var c = new NpgsqlCommand("select tag, name from assets.assets where id = @i", db))
            {
                P(c, "i", id); await using var rd = await c.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) return Results.NotFound();
                tag = rd.GetString(0); name = rd.GetString(1);
            }
            await using (var c = new NpgsqlCommand("select 1 from assets.delete_requests where asset_id = @i and status = 'pending'", db)) { P(c, "i", id); if (await c.ExecuteScalarAsync(ct) != null) return Results.Json(new { error = "already_requested", detail = "A request to delete this asset is already waiting for approval." }, statusCode: 409); }
            await using var ins = new NpgsqlCommand("insert into assets.delete_requests (asset_id, tag, name, requested_by, requested_by_id, reason) values (@a,@t,@n,@u,@s,@r) returning id", db);
            P(ins, "a", id); P(ins, "t", tag); P(ins, "n", name); P(ins, "u", who(u)); P(ins, "s", u.FindFirstValue("sub") ?? ""); P(ins, "r", reason);
            var rid = (int)(await ins.ExecuteScalarAsync(ct))!;
            await audit.WriteAsync("assets.delete_requested", id.ToString(), tag + ": " + reason, ct: ct);
            await NotifyApprovers(http, db, id, tag, name, who(u), u.FindFirstValue("sub") ?? "", reason, ct);
            return Results.Json(new { id = rid }, statusCode: 201);
        }));
        anyone(r.MapGet("/api/assets/delete-requests", async (HttpContext http, string? status, ITenantContext t, CancellationToken ct) =>
        {
            var scope = await AssetsAccess.ScopeAsync(http, Acts.Read); var del = await AssetsAccess.ScopeAsync(http, Acts.Delete);
            await using var db = await Open(t, ct);
            var items = new List<object>();
            await using var c = new NpgsqlCommand(@"select q.id, q.asset_id, q.tag, q.name, q.requested_by, q.reason, q.status, q.decided_by, q.note, q.decided_at, q.created_at,
                  (a.id is not null and (not @dc or a.company_id = any(@dscope))) as can_decide
                from assets.delete_requests q left join assets.assets a on a.id = q.asset_id
                where (@st = '' or q.status = @st) and (a.id is null or not @sc or a.company_id = any(@scope)) order by q.created_at desc limit 200", db);
            AssetsAccess.Bind(c, scope); P(c, "st", status ?? ""); P(c, "dc", del != null); P(c, "dscope", del?.ToArray() ?? Array.Empty<int>());
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                items.Add(new { id = rd.GetInt32(0), assetId = rd.GetInt64(1), tag = rd.GetString(2), name = rd.GetString(3), requestedBy = rd.GetString(4), reason = rd.GetString(5), status = rd.GetString(6),
                    decidedBy = rd.GetString(7), note = rd.GetString(8), decidedAt = rd.IsDBNull(9) ? (DateTime?)null : rd.GetDateTime(9), createdAt = rd.GetDateTime(10), canDecide = rd.GetBoolean(11) && (del == null || del.Count > 0) });
            return Results.Ok(new { items });
        }));
        delegated(Acts.Delete, r.MapPost("/api/assets/delete-requests/{rid:int}/{verb}", async (HttpContext http, int rid, string verb, JsonElement body, ClaimsPrincipal u, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            if (verb is not ("approve" or "reject")) return Results.NotFound();
            await using var db = await Open(t, ct);
            long assetId; string tag, requester, requesterId;
            await using (var c = new NpgsqlCommand("select asset_id, tag, requested_by, requested_by_id from assets.delete_requests where id = @i and status = 'pending'", db))
            {
                P(c, "i", rid); await using var rd = await c.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) return Results.Json(new { error = "not_pending", detail = "This request was already decided." }, statusCode: 409);
                assetId = rd.GetInt64(0); tag = rd.GetString(1); requester = rd.GetString(2); requesterId = rd.GetString(3);
            }
            if (!await AssetsAccess.InScope(db, assetId, await AssetsAccess.ScopeAsync(http, Acts.Delete), ct)) return Results.NotFound();
            var isAdmin = u.IsInRole("admin") || u.FindFirstValue("role") == "admin";
            if (!isAdmin && requesterId != "" && requesterId == u.FindFirstValue("sub")) return Results.Json(new { error = "forbidden", detail = "You cannot approve your own request; ask another approver." }, statusCode: 403);
            var note = AgentsApi.Clip(AgentsApi.Str(body, "note"), 500).Trim();
            await using var tx = await db.BeginTransactionAsync(ct);
            if (verb == "approve")
            {
                await using var d = new NpgsqlCommand("delete from assets.assets where id = @i", db, tx); P(d, "i", assetId); await d.ExecuteNonQueryAsync(ct);
            }
            await using (var c = new NpgsqlCommand("update assets.delete_requests set status = @s, decided_by = @b, note = @n, decided_at = now() where id = @i", db, tx))
            { P(c, "s", verb == "approve" ? "approved" : "rejected"); P(c, "b", who(u)); P(c, "n", note); P(c, "i", rid); await c.ExecuteNonQueryAsync(ct); }
            await tx.CommitAsync(ct);
            await audit.WriteAsync(verb == "approve" ? "assets.deleted" : "assets.delete_rejected", assetId.ToString(), $"{tag}, asked by {requester}" + (note == "" ? "" : ": " + note), ct: ct);
            return Results.Ok(new { ok = true });
        }));

        // ---- CSV
        anyone(r.MapGet("/api/assets/export.csv", async (HttpContext http, ITenantContext t, IConfiguration cfg, CancellationToken ct) =>
        {
            var scope = await AssetsAccess.ScopeAsync(http, Acts.Read);
            var f = ListFilter(http.Request.Query, cfg); if (f.Error != null) return Bad("bad_input", f.Error);
            await using var db = await Open(t, ct);
            await using var c = new NpgsqlCommand($"select {Cols} from {From} left join assets.agents g on g.id = a.agent_id and g.deleted_at is null where (not @sc or a.company_id = any(@scope)) and {f.Sql} order by a.id", db); AssetsAccess.Bind(c, scope); f.Bind(c);
            var sb = new StringBuilder("tag,name,type,status,manufacturer,model,serial,location,assigned_to,purchase_date,purchase_cost,warranty_end,notes\r\n");
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var cells = new[] { rd.GetString(1), rd.GetString(2), rd.IsDBNull(4) ? "" : rd.GetString(4), rd.GetString(5), rd.GetString(6), rd.GetString(7), rd.GetString(8), rd.GetString(9), rd.GetString(10),
                    rd.IsDBNull(11) ? "" : D(rd.GetFieldValue<DateOnly>(11)), rd.IsDBNull(12) ? "" : rd.GetDecimal(12).ToString(CultureInfo.InvariantCulture), rd.IsDBNull(13) ? "" : D(rd.GetFieldValue<DateOnly>(13)), rd.GetString(14) };
                sb.Append(string.Join(",", cells.Select(Csv.Cell))).Append("\r\n");
            }
            return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8", "assets.csv");
        }));

        delegated(Acts.Add, r.MapPost("/api/assets/import", async (HttpContext http, HttpRequest req, ClaimsPrincipal u, ITenantContext t, IEntitlements ent, IAuditLog audit, CancellationToken ct) =>
        {
            if (await AssetsAccess.ScopeAsync(http, Acts.Add) != null) return Results.Json(new { error = "forbidden", detail = "Importing needs permission to add assets for every company." }, statusCode: 403);
            if ((req.ContentLength ?? 0) > 5 * 1024 * 1024) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            using var sr = new StreamReader(req.Body, Encoding.UTF8);
            var text = await sr.ReadToEndAsync(ct);
            var rows = Csv.Parse(text);
            if (rows.Count == 0) return Bad("bad_input", "the file is empty");
            if (rows.Count > 5001) return Bad("bad_input", "at most 5000 rows per import");
            var head = rows[0].Select(h => h.Trim().ToLowerInvariant().Replace(' ', '_')).ToList();
            if (!head.Contains("name")) return Bad("bad_input", "the first line must name the columns, and one of them must be 'name'");
            string Cell(List<string> row, string col) { var i = head.IndexOf(col); return i >= 0 && i < row.Count ? row[i].Trim() : ""; }
            var createTypes = req.Query["createTypes"] == "true";
            await using var db = await Open(t, ct);
            var typeIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            await using (var c = new NpgsqlCommand("select id, name from assets.types", db))
            await using (var rd = await c.ExecuteReaderAsync(ct)) while (await rd.ReadAsync(ct)) typeIds[rd.GetString(1)] = rd.GetInt32(0);
            var (_, active) = await Counts(db, ct);
            int created = 0, skipped = 0; var errors = new List<object>();
            for (var n = 1; n < rows.Count; n++)
            {
                var row = rows[n]; var line = n + 1;
                if (row.All(x => string.IsNullOrWhiteSpace(x))) continue;
                if (!await ent.WithinLimitAsync(Key, "max_assets", active + created, ct)) { errors.Add(new { line, error = "limit_reached" }); break; }
                var tname = Cell(row, "type"); int? tid = null;
                if (tname != "")
                {
                    if (!typeIds.TryGetValue(tname, out var found))
                    {
                        if (!createTypes) { errors.Add(new { line, error = "unknown_type", detail = tname }); continue; }
                        await using var ct1 = new NpgsqlCommand("insert into assets.types (name) values (@n) returning id", db); P(ct1, "n", tname);
                        found = (int)(await ct1.ExecuteScalarAsync(ct))!; typeIds[tname] = found;
                    }
                    tid = found;
                }
                var obj = new Dictionary<string, object?> { ["name"] = Cell(row, "name"), ["typeId"] = tid };
                foreach (var (col, json) in new[] { ("tag", "tag"), ("status", "status"), ("manufacturer", "manufacturer"), ("model", "model"), ("serial", "serial"), ("location", "location"), ("assigned_to", "assignedTo"), ("purchase_date", "purchaseDate"), ("warranty_end", "warrantyEnd"), ("notes", "notes") })
                { var c = Cell(row, col); if (c != "") obj[json] = c; }
                var cost = Cell(row, "purchase_cost");
                if (cost != "") { if (decimal.TryParse(cost, NumberStyles.Number, CultureInfo.InvariantCulture, out var m)) obj["purchaseCost"] = m; else { errors.Add(new { line, error = "bad_input", detail = "purchase_cost" }); continue; } }
                var v = await Read(JsonSerializer.SerializeToElement(obj), true, db, null, ct);
                if (v.Error != null) { errors.Add(new { line, error = "bad_input", detail = v.Error }); continue; }
                var res = await Insert(db, v, Actor(u), "import", ct);
                if (res.Error == "duplicate") { skipped++; errors.Add(new { line, error = "duplicate", detail = res.Detail }); }
                else if (res.Error != null) errors.Add(new { line, error = res.Error, detail = res.Detail });
                else created++;
            }
            await audit.WriteAsync("assets.imported", "", $"created {created}, skipped {skipped}, problems {errors.Count - skipped}", ct: ct);
            return Results.Ok(new { created, skipped, errors });
        }));
    }

    static string FieldsJson(List<FieldDef> f) => JsonSerializer.Serialize(f.Select(x => new { key = x.Key, label = x.Label, kind = x.Kind, options = x.Kind == "choice" ? x.Options : null }));

    sealed record Inserted(long Id, string Tag, string? Error, string? Detail);

    static async Task<Inserted> Insert(NpgsqlConnection db, Values v, string actor, string source, CancellationToken ct)
    {
        var cols = new List<string>(); var vals = new List<string>(); var cmd = new NpgsqlCommand { Connection = db };
        var i = 0;
        foreach (var (k, val) in v.Set)
        {
            cols.Add(k); vals.Add("@p" + i);
            var prm = new NpgsqlParameter("p" + i++, val ?? DBNull.Value);
            if (val is null) prm.NpgsqlDbType = k is "purchase_date" or "warranty_end" ? NpgsqlDbType.Date : k == "purchase_cost" ? NpgsqlDbType.Numeric : NpgsqlDbType.Integer;
            cmd.Parameters.Add(prm);
        }
        cols.AddRange(new[] { "source", "created_by", "updated_by" }); vals.AddRange(new[] { "@src", "@by", "@by" });
        P(cmd, "src", source); P(cmd, "by", actor);
        cmd.CommandText = $"insert into assets.assets ({string.Join(", ", cols)}) values ({string.Join(", ", vals)}) returning id, tag";
        await using var tx = await db.BeginTransactionAsync(ct);
        cmd.Transaction = tx;
        try
        {
            long id; string tag;
            await using (var rd = await cmd.ExecuteReaderAsync(ct)) { await rd.ReadAsync(ct); (id, tag) = (rd.GetInt64(0), rd.GetString(1)); }
            await AddHistory(db, tx, id, actor, "created", "{}", ct);
            await tx.CommitAsync(ct);
            return new Inserted(id, tag, null, null);
        }
        catch (PostgresException e) when (e.SqlState == "23505")
        {
            return new Inserted(0, "", "duplicate", e.ConstraintName == "assets_tag" ? "tag is already used" : "serial number is already used by another asset");
        }
    }

    static async Task AddHistory(NpgsqlConnection db, NpgsqlTransaction tx, long id, string actor, string action, string changes, CancellationToken ct)
    {
        await using var h = new NpgsqlCommand("insert into assets.history (asset_id, actor, action, changes) values (@i, @a, @c, @x)", db, tx);
        P(h, "i", id); P(h, "a", actor); P(h, "c", action); P(h, "x", changes);
        await h.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>Small CSV reader and writer (RFC 4180: quotes, commas and line breaks inside cells). Cells that a spreadsheet would run as a formula get a leading apostrophe when exported.</summary>
public static class Csv
{
    public static string Cell(string s)
    {
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r') s = "'" + s;
        return s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    public static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>(); var row = new List<string>(); var cell = new StringBuilder(); var q = false;
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (q)
            {
                if (ch == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else q = false; }
                else cell.Append(ch);
            }
            else if (ch == '"') q = true;
            else if (ch == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (ch == '\r' || ch == '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = new List<string>();
            }
            else cell.Append(ch);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }
}
