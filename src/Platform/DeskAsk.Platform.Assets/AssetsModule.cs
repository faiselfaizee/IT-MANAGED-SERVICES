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
    const string Cols = "a.id, a.tag, a.name, a.type_id, t.name, a.status, a.manufacturer, a.model, a.serial, a.location, a.assigned_to, a.purchase_date, a.purchase_cost, a.warranty_end, a.notes, a.custom, a.source, a.created_at, a.updated_at";
    const string From = "assets.assets a left join assets.types t on t.id = a.type_id";

    public void AddServices(IServiceCollection s, IConfiguration cfg)
    {
        s.AddSingleton<ITenantSchema, AssetsSchema>();
        s.AddSingleton<AgentHub>();
        s.AddSingleton<IJobHandler, AssetsJanitorJob>();
        s.AddSingleton(new RecurringJob("assets.janitor", AssetsJanitorJob.JobType, TimeSpan.FromMinutes(1)));
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
    public void MapEndpoints(IEndpointRouteBuilder r)
    {
        var admin = (RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole("admin")).RequireModule(Key);
        var anyone = (RouteHandlerBuilder b) => b.RequireAuthorization().RequireModule(Key);

        AssetsDevice.Map(r, Key);
        AgentsApi.Map(r, Key);
        ShellApi.Map(r, Key);

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
        anyone(r.MapGet("/api/assets", async (HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            var q = http.Request.Query;
            var search = q["search"].ToString().Trim(); var status = q["status"].ToString(); var assigned = q["assignedTo"].ToString().Trim();
            int.TryParse(q["typeId"], out var typeId); int.TryParse(q["take"], out var take); int.TryParse(q["skip"], out var skip);
            if (status != "" && !Statuses.Contains(status)) return Bad("bad_input", "status");
            take = take <= 0 ? 50 : Math.Min(take, 200); skip = Math.Max(skip, 0);
            await using var db = await Open(t, ct);
            await using var c = new NpgsqlCommand($@"select {Cols}, count(*) over() from {From}
                where (@q = '' or a.name ilike @l or a.tag ilike @l or a.serial ilike @l or a.model ilike @l or a.assigned_to ilike @l or a.location ilike @l)
                  and (@st = '' or a.status = @st) and (@ty = 0 or a.type_id = @ty) and (@as = '' or lower(a.assigned_to) = lower(@as))
                order by a.id desc offset @sk limit @tk", db);
            P(c, "q", search); P(c, "l", Like(search)); P(c, "st", status); P(c, "ty", typeId); P(c, "as", assigned); P(c, "sk", skip); P(c, "tk", take);
            var items = new List<object>(); long total = 0;
            await using (var rd = await c.ExecuteReaderAsync(ct))
                while (await rd.ReadAsync(ct)) { items.Add(Row(rd)); total = rd.GetInt64(19); }
            if (items.Count == 0 && skip > 0)
            {
                await using var cc = new NpgsqlCommand("select count(*) from assets.assets", db);
                total = (long)(await cc.ExecuteScalarAsync(ct))!;
            }
            return Results.Ok(new { total, items });
        }));

        anyone(r.MapGet("/api/assets/summary", async (ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            var byStatus = Statuses.ToDictionary(s => s, _ => 0L); var byType = new List<object>();
            await using (var c = new NpgsqlCommand("select status, count(*) from assets.assets group by status", db))
            await using (var rd = await c.ExecuteReaderAsync(ct)) while (await rd.ReadAsync(ct)) byStatus[rd.GetString(0)] = rd.GetInt64(1);
            await using (var c = new NpgsqlCommand("select coalesce(t.name, '(no type)'), count(*) from assets.assets a left join assets.types t on t.id = a.type_id group by 1 order by 2 desc, 1", db))
            await using (var rd = await c.ExecuteReaderAsync(ct)) while (await rd.ReadAsync(ct)) byType.Add(new { type = rd.GetString(0), count = rd.GetInt64(1) });
            long w30, w90, expired;
            await using (var c = new NpgsqlCommand(@"select count(*) filter (where warranty_end >= current_date and warranty_end < current_date + 30),
                count(*) filter (where warranty_end >= current_date and warranty_end < current_date + 90), count(*) filter (where warranty_end < current_date)
                from assets.assets where status not in ('retired','lost') and warranty_end is not null", db))
            await using (var rd = await c.ExecuteReaderAsync(ct)) { await rd.ReadAsync(ct); (w30, w90, expired) = (rd.GetInt64(0), rd.GetInt64(1), rd.GetInt64(2)); }
            return Results.Ok(new { total = byStatus.Values.Sum(), byStatus, byType, warrantyEndsIn30Days = w30, warrantyEndsIn90Days = w90, warrantyExpired = expired });
        }));

        anyone(r.MapGet("/api/assets/{id:long}", async (long id, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            object? asset = null;
            await using (var c = new NpgsqlCommand($"select {Cols} from {From} where a.id = @i", db))
            {
                P(c, "i", id);
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
        admin(r.MapPost("/api/assets", async (JsonElement body, ClaimsPrincipal u, ITenantContext t, IEntitlements ent, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            var v = await Read(body, true, db, null, ct);
            if (v.Error != null) return Bad("bad_input", v.Error);
            var (_, active) = await Counts(db, ct);
            if (!await ent.WithinLimitAsync(Key, "max_assets", active, ct)) return Results.Json(new { error = "limit_reached", limit = "max_assets" }, statusCode: 402);
            var created = await Insert(db, v, Actor(u), "manual", ct);
            if (created.Error != null) return created.Error == "duplicate" ? Results.Conflict(new { error = "duplicate", detail = created.Detail }) : Bad(created.Error, created.Detail);
            await audit.WriteAsync("assets.created", created.Id.ToString(), created.Tag, ct: ct);
            return Results.Ok(new { id = created.Id, tag = created.Tag });
        }));

        admin(r.MapPut("/api/assets/{id:long}", async (long id, JsonElement body, ClaimsPrincipal u, ITenantContext t, IEntitlements ent, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
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

        admin(r.MapDelete("/api/assets/{id:long}", async (long id, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            await using var c = new NpgsqlCommand("delete from assets.assets where id = @i returning tag", db); P(c, "i", id);
            if (await c.ExecuteScalarAsync(ct) is not string tag) return Results.NotFound();
            await audit.WriteAsync("assets.deleted", id.ToString(), tag, ct: ct);
            return Results.Ok(new { ok = true });
        }));

        // ---- CSV
        anyone(r.MapGet("/api/assets/export.csv", async (HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            await using var c = new NpgsqlCommand($"select {Cols} from {From} order by a.id", db);
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

        admin(r.MapPost("/api/assets/import", async (HttpRequest req, ClaimsPrincipal u, ITenantContext t, IEntitlements ent, IAuditLog audit, CancellationToken ct) =>
        {
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
