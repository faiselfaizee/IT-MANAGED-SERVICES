using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DeskAsk.Platform.Vault;

/// <summary>
/// DeskAsk Vault (module "vault"): a zero-knowledge password vault in the Bitwarden model, made for an MSP.
///
/// Encryption happens in the browser. Each person has a key pair; the private key is encrypted with a key derived from their master
/// password (PBKDF2-SHA256, 600 000 rounds) and, separately, with a recovery code. Each vault has two random AES-256 keys: the item key
/// (everything secret) and the directory key (item titles, so people who may only ask for access can see what exists). Vault keys reach
/// a member wrapped with that member's public key (RSA-OAEP). The server stores only ciphertext and cannot read any of it.
///
/// The one exception is a managed account: its password is also kept sealed on the server, so the agent can change it on a schedule.
/// Reading a managed password goes through the server and is always written to the audit log.
/// </summary>
public sealed class VaultModule : IPlatformModule
{
    public string Key => Modules.Vault;
    public string Schema => "vault";

    public void AddServices(IServiceCollection s, IConfiguration cfg)
    {
        s.AddSingleton<ITenantSchema, VaultSchema>();
        s.AddSingleton<IJobHandler, VaultJob>();
        s.AddSingleton(new RecurringJob("vault.tick", VaultJob.JobType, TimeSpan.FromMinutes(1)));
    }

    public void MapEndpoints(IEndpointRouteBuilder r)
    {
        KeysApi.Map(r, Key);
        VaultsApi.Map(r, Key);
        ItemsApi.Map(r, Key);
        ManagedApi.Map(r, Key);
        RequestsApi.Map(r, Key);
        SendsApi.Map(r, Key);
        ToolsApi.Map(r, Key);
    }

    // ---- shared helpers

    internal static IResult Bad(string error, string? detail = null) => Results.BadRequest(new { error, detail });
    internal static IResult Forbid(string detail) => Results.Json(new { error = "forbidden", detail }, statusCode: 403);
    internal static async Task<NpgsqlConnection> Open(ITenantContext t, CancellationToken ct) { var db = new NpgsqlConnection(t.Require().ConnectionString); await db.OpenAsync(ct); return db; }
    internal static void P(NpgsqlCommand c, string n, object? v) => c.Parameters.AddWithValue(n, v ?? DBNull.Value);
    internal static Guid Me(ClaimsPrincipal u) => Guid.TryParse(u.FindFirst("sub")?.Value, out var g) ? g : Guid.Empty;
    internal static string Actor(ClaimsPrincipal u) => u.FindFirst("email")?.Value ?? u.FindFirst("name")?.Value ?? u.FindFirst("sub")?.Value ?? "unknown";
    internal static string Role(ClaimsPrincipal u) => u.FindFirst("role")?.Value ?? "";
    internal static string? Str(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    internal static int? Int(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    internal static long? Long(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : null;
    internal static bool Bool(JsonElement e, string n, bool d = false) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) ? v.ValueKind == JsonValueKind.True || (v.ValueKind != JsonValueKind.False && d) : d;

    /// <summary>
    /// An encrypted value as the browser makes it: {"v":1,"iv":"...","ct":"..."} (AES-GCM) or a wrapped key {"v":1,"k":"..."} (RSA-OAEP).
    /// The server only checks the shape and size; it never has the key.
    /// </summary>
    internal static bool Blob(string? s, int max) =>
        s != null && s.Length <= max && s.StartsWith("{\"v\":", StringComparison.Ordinal) && s.EndsWith("}", StringComparison.Ordinal) && IsJson(s);
    static bool IsJson(string s) { try { using var d = JsonDocument.Parse(s); return d.RootElement.ValueKind == JsonValueKind.Object; } catch (JsonException) { return false; } }

    /// <summary>Roles in a vault, lowest first. request: sees titles and asks; view: reads; edit: changes items; manage: changes members too.</summary>
    internal static readonly string[] Roles = { "request", "view", "edit", "manage" };
    internal static int Rank(string? role) => role == null ? 0 : Array.IndexOf(Roles, role) + 1;

    /// <summary>Who uses the vault: staff and customer administrators. Customer users (role customer) never do.</summary>
    internal static RouteHandlerBuilder Use(RouteHandlerBuilder b, string key) =>
        b.RequireAuthorization(p => p.RequireRole("admin", "engineer", "viewer", "cadmin")).RequireModule(key);
}

public sealed class VaultSchema : ITenantSchema
{
    public string Name => "vault";
    public static readonly Migration[] Migrations =
    {
        new(1, "keys, vaults, members, items, history, managed accounts, requests, sends", @"
            create schema if not exists vault;
            create table if not exists vault.keys (
              user_id uuid primary key, public_key text not null, wrapped_private text not null, wrapped_recovery text not null,
              kdf text not null, created_at timestamptz not null default now(), updated_at timestamptz not null default now());
            create table if not exists vault.vaults (
              id serial primary key, name text not null, kind text not null check (kind in ('customer','internal','personal')),
              company_id int, owner_user uuid, key_version int not null default 1, needs_rotation boolean not null default false,
              created_by text not null default '', created_at timestamptz not null default now());
            create unique index if not exists vaults_personal on vault.vaults (owner_user) where kind = 'personal';
            create table if not exists vault.members (
              vault_id int not null references vault.vaults(id) on delete cascade, ptype text not null check (ptype in ('user','group')),
              pid uuid not null, role text not null check (role in ('request','view','edit','manage')), primary key (vault_id, ptype, pid));
            create table if not exists vault.vault_keys (
              vault_id int not null references vault.vaults(id) on delete cascade, user_id uuid not null, key_version int not null,
              wrapped_key text, wrapped_dir_key text not null, granted_by text not null default '', at timestamptz not null default now(),
              primary key (vault_id, user_id));
            create table if not exists vault.folders (
              id serial primary key, vault_id int not null references vault.vaults(id) on delete cascade, name_enc text not null, key_version int not null default 1);
            create table if not exists vault.items (
              id bigserial primary key, vault_id int not null references vault.vaults(id) on delete cascade, folder_id int references vault.folders(id) on delete set null,
              type text not null, data_enc text not null, title_enc text not null, key_version int not null, revision int not null default 1,
              asset_id bigint, managed boolean not null default false, requires_approval boolean not null default false,
              pw_changed_at timestamptz, created_by text not null default '', updated_by text not null default '',
              created_at timestamptz not null default now(), updated_at timestamptz not null default now(), deleted_at timestamptz);
            create index if not exists items_vault on vault.items (vault_id);
            create index if not exists items_asset on vault.items (asset_id) where asset_id is not null;
            create table if not exists vault.history (
              item_id bigint not null references vault.items(id) on delete cascade, revision int not null, data_enc text not null, key_version int not null,
              changed_by text not null default '', at timestamptz not null default now(), primary key (item_id, revision));
            create table if not exists vault.events (
              id bigserial primary key, vault_id int, item_id bigint, user_name text not null, action text not null, detail text not null default '',
              at timestamptz not null default now());
            create index if not exists events_item on vault.events (item_id, id desc);
            create index if not exists events_vault on vault.events (vault_id, id desc);
            create table if not exists vault.managed (
              item_id bigint primary key references vault.items(id) on delete cascade, account text not null, target text not null check (target in ('local','domain','linux')),
              asset_id bigint not null, secret_sealed text not null, pending_sealed text, handoff text, started_at timestamptz,
              rotate_days int not null default 0, reset_after_use boolean not null default false, next_at timestamptz,
              last_rotated_at timestamptz, last_error text not null default '', state text not null default 'ok');
            create table if not exists vault.managed_history (
              id bigserial primary key, item_id bigint not null references vault.items(id) on delete cascade, secret_sealed text not null, at timestamptz not null default now(), by_name text not null, note text not null default '');
            create table if not exists vault.requests (
              id bigserial primary key, item_id bigint not null references vault.items(id) on delete cascade, vault_id int not null,
              user_id uuid not null, user_name text not null, reason text not null, ticket text not null default '', minutes int not null,
              status text not null default 'pending' check (status in ('pending','approved','denied','expired','cancelled')),
              decided_by text not null default '', decided_at timestamptz, note text not null default '', expires_at timestamptz, created_at timestamptz not null default now());
            create table if not exists vault.grants (
              id bigserial primary key, request_id bigint references vault.requests(id) on delete cascade, item_id bigint not null references vault.items(id) on delete cascade,
              user_id uuid not null, data_enc text not null, expires_at timestamptz not null, granted_by text not null, at timestamptz not null default now());
            create table if not exists vault.sends (
              id text primary key, data_enc text, label text not null default '', created_by text not null, created_by_id uuid not null,
              created_at timestamptz not null default now(), expires_at timestamptz not null, max_views int not null, views int not null default 0,
              passcode boolean not null default false, passcode_check text, tries int not null default 0, deleted_at timestamptz);
            create table if not exists vault.breach_cache (prefix text primary key, body text not null, at timestamptz not null default now())"),
    };
    public Task EnsureAsync(IServiceProvider services, TenantInfo tenant, CancellationToken ct) => SchemaMigrator.RunAsync(tenant.ConnectionString, Name, Migrations, ct);
}

/// <summary>Vault settings an administrator chooses.</summary>
public sealed class VaultSettings
{
    public const string Key = "vault.settings";
    /// <summary>The vault locks itself after this many idle minutes (the browser forgets the keys).</summary>
    public int AutoLockMinutes { get; set; } = 15;
    /// <summary>Check passwords against Have I Been Pwned (only the first five characters of a SHA-1 hash leave the server).</summary>
    public bool BreachCheck { get; set; } = true;
    public int RotationLength { get; set; } = 24;
    public int DefaultRotateDays { get; set; } = 30;
    /// <summary>The longest time an access request can be granted for.</summary>
    public int RequestMaxMinutes { get; set; } = 480;
    /// <summary>Who may create vaults: "admins" or "engineers" (administrators and engineers).</summary>
    public string Creators { get; set; } = "admins";

    public static async Task<VaultSettings> LoadAsync(ISettings s, CancellationToken ct)
    {
        var raw = await s.GetAsync(Key, ct);
        if (string.IsNullOrWhiteSpace(raw)) return new();
        try { return JsonSerializer.Deserialize<VaultSettings>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new(); } catch (JsonException) { return new(); }
    }
    public Task SaveAsync(ISettings s, CancellationToken ct) => s.SetAsync(Key, JsonSerializer.Serialize(this, new JsonSerializerOptions(JsonSerializerDefaults.Web)), ct);
}

/// <summary>Who has which role in which vault, worked out from direct and group membership.</summary>
internal static class Access
{
    /// <summary>My role per vault (the highest of my direct and group grants); my personal vault counts as manage.</summary>
    public static async Task<Dictionary<int, string>> MyRoles(NpgsqlConnection db, HttpContext http, CancellationToken ct)
    {
        var me = VaultModule.Me(http.User);
        var groups = await http.RequestServices.GetRequiredService<IAccess>().GroupsOfAsync(me, ct);
        var o = new Dictionary<int, string>();
        await using var c = new NpgsqlCommand(@"select vault_id, role from vault.members where (ptype = 'user' and pid = @u) or (ptype = 'group' and pid = any(@g))
            union all select id, 'manage' from vault.vaults where kind = 'personal' and owner_user = @u", db);
        VaultModule.P(c, "u", me); VaultModule.P(c, "g", groups.ToArray());
        await using var rd = await c.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var v = rd.GetInt32(0); var r = rd.GetString(1);
            if (!o.TryGetValue(v, out var cur) || VaultModule.Rank(r) > VaultModule.Rank(cur)) o[v] = r;
        }
        return o;
    }

    public static async Task<string?> MyRole(NpgsqlConnection db, HttpContext http, int vaultId, CancellationToken ct) =>
        (await MyRoles(db, http, ct)).TryGetValue(vaultId, out var r) ? r : null;

    /// <summary>Everyone who should hold this vault's keys, with their role.</summary>
    public static async Task<Dictionary<Guid, string>> Entitled(NpgsqlConnection db, IAccess access, int vaultId, CancellationToken ct)
    {
        var o = new Dictionary<Guid, string>();
        void Add(Guid u, string r) { if (!o.TryGetValue(u, out var cur) || VaultModule.Rank(r) > VaultModule.Rank(cur)) o[u] = r; }
        var grants = new List<(string T, Guid P, string R)>();
        await using (var c = new NpgsqlCommand("select ptype, pid, role from vault.members where vault_id = @v union all select 'user', owner_user, 'manage' from vault.vaults where id = @v and kind = 'personal'", db))
        {
            VaultModule.P(c, "v", vaultId); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) grants.Add((rd.GetString(0), rd.GetGuid(1), rd.GetString(2)));
        }
        foreach (var g in grants)
            if (g.T == "user") Add(g.P, g.R);
            else foreach (var u in await access.GroupUsersAsync(g.P, ct)) Add(u, g.R);
        return o;
    }

    /// <summary>Writes a vault event (who did what to which item). Item contents never go in here.</summary>
    public static async Task Event(NpgsqlConnection db, int? vaultId, long? itemId, string user, string action, string detail, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("insert into vault.events (vault_id, item_id, user_name, action, detail) values (@v, @i, @u, @a, @d)", db);
        VaultModule.P(c, "v", vaultId); VaultModule.P(c, "i", itemId); VaultModule.P(c, "u", user); VaultModule.P(c, "a", action); VaultModule.P(c, "d", detail.Length > 400 ? detail[..400] : detail);
        await c.ExecuteNonQueryAsync(ct);
    }
}
