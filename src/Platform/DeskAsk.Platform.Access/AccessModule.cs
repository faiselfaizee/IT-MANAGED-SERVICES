using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Security.Claims;
using System.Text.Json;

namespace DeskAsk.Platform.Access;

public sealed class AccessSchema : ITenantSchema
{
    public string Name => "access";
    public static readonly Migration[] Migrations =
    {
        new(1, "groups, members, grants, user companies", @"
            create schema if not exists access;
            create table if not exists access.groups (id uuid primary key, name text not null, description text not null default '', created_at timestamptz not null default now());
            create unique index if not exists groups_name on access.groups (lower(name));
            create table if not exists access.members (
              group_id uuid not null references access.groups(id) on delete cascade,
              kind text not null check (kind in ('user','group')), member_id uuid not null,
              primary key (group_id, kind, member_id));
            create table if not exists access.grants (
              id bigserial primary key, kind text not null check (kind in ('user','group')), principal_id uuid not null,
              module text not null, action text not null check (action in ('read','add','modify','delete')), company_id int,
              created_at timestamptz not null default now(), created_by text not null default '');
            create unique index if not exists grants_unique on access.grants (kind, principal_id, module, action, coalesce(company_id, 0));
            create table if not exists access.user_companies (user_id uuid primary key, company_id int not null)"),
    };
    public Task EnsureAsync(IServiceProvider services, TenantInfo tenant, CancellationToken ct) => SchemaMigrator.RunAsync(tenant.ConnectionString, Name, Migrations, ct);
}

/// <summary>
/// Delegation. Administrators make security groups (users and other groups as members) and grant a user or a group the right to read, add,
/// modify or delete in a module, for every company or for one. Customer administrators are tied to one company here.
/// </summary>
public sealed class AccessModule : IPlatformModule
{
    public string Key => "access";
    public string Schema => "access";

    public void AddServices(IServiceCollection s, IConfiguration cfg)
    {
        s.AddSingleton<ITenantSchema, AccessSchema>();
        s.AddScoped<IAccess, AccessService>();
    }

    static readonly string[] GrantModules = { Modules.Assets, Modules.ServiceDesk };
    static IResult Bad(string error, string? detail = null) => Results.BadRequest(new { error, detail });
    static async Task<NpgsqlConnection> Open(ITenantContext t, CancellationToken ct) { var db = new NpgsqlConnection(t.Require().ConnectionString); await db.OpenAsync(ct); return db; }
    static void P(NpgsqlCommand c, string n, object? v) => c.Parameters.AddWithValue(n, v ?? DBNull.Value);

    public void MapEndpoints(IEndpointRouteBuilder r)
    {
        var g = r.MapGroup("/api/platform/access").RequireAuthorization(p => p.RequireRole("admin"));

        // ---------------------------------------------------------------- groups
        g.MapGet("/groups", async (ITenantContext t, IUserDirectory users, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            var names = (await users.ListAsync(ct)).ToDictionary(u => u.Id, u => u.Name);
            var groups = new List<(Guid Id, string Name, string Desc)>();
            await using (var c = new NpgsqlCommand("select id, name, description from access.groups order by lower(name)", db))
            await using (var rd = await c.ExecuteReaderAsync(ct)) while (await rd.ReadAsync(ct)) groups.Add((rd.GetGuid(0), rd.GetString(1), rd.GetString(2)));
            var members = new Dictionary<Guid, List<object>>(); var gname = groups.ToDictionary(x => x.Id, x => x.Name);
            await using (var c = new NpgsqlCommand("select group_id, kind, member_id from access.members", db))
            await using (var rd = await c.ExecuteReaderAsync(ct))
                while (await rd.ReadAsync(ct))
                {
                    var (gid, kind, mid) = (rd.GetGuid(0), rd.GetString(1), rd.GetGuid(2));
                    var label = kind == "user" ? names.GetValueOrDefault(mid, "(removed user)") : gname.GetValueOrDefault(mid, "(removed group)");
                    if (!members.TryGetValue(gid, out var l)) members[gid] = l = new();
                    l.Add(new { kind, id = mid, name = label });
                }
            return Results.Ok(groups.Select(x => new { id = x.Id, name = x.Name, description = x.Desc, members = members.GetValueOrDefault(x.Id) ?? new() }));
        });

        g.MapPost("/groups", async (JsonElement b, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var (name, desc, err) = Fields(b); if (err != null) return Bad("bad_request", err);
            await using var db = await Open(t, ct);
            var id = Guid.NewGuid();
            try
            {
                await using var c = new NpgsqlCommand("insert into access.groups (id, name, description) values (@i, @n, @d)", db); P(c, "i", id); P(c, "n", name); P(c, "d", desc);
                await c.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException e) when (e.SqlState == "23505") { return Results.Conflict(new { error = "duplicate", detail = "A group with this name already exists." }); }
            await audit.WriteAsync("access.group_created", name, ct: ct);
            return Results.Created("/api/platform/access/groups/" + id, new { id, name, description = desc });
        });

        g.MapPut("/groups/{id:guid}", async (Guid id, JsonElement b, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var (name, desc, err) = Fields(b); if (err != null) return Bad("bad_request", err);
            await using var db = await Open(t, ct);
            try
            {
                await using var c = new NpgsqlCommand("update access.groups set name = @n, description = @d where id = @i", db); P(c, "i", id); P(c, "n", name); P(c, "d", desc);
                if (await c.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            }
            catch (PostgresException e) when (e.SqlState == "23505") { return Results.Conflict(new { error = "duplicate", detail = "A group with this name already exists." }); }
            await audit.WriteAsync("access.group_saved", name, ct: ct);
            return Results.Ok(new { ok = true });
        });

        g.MapDelete("/groups/{id:guid}", async (Guid id, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            await using var tx = await db.BeginTransactionAsync(ct);
            string? name;
            await using (var c = new NpgsqlCommand("delete from access.groups where id = @i returning name", db, tx)) { P(c, "i", id); name = await c.ExecuteScalarAsync(ct) as string; }
            if (name == null) return Results.NotFound();
            // a deleted group also stops being a member of other groups and loses its grants
            await using (var c = new NpgsqlCommand("delete from access.members where kind = 'group' and member_id = @i; delete from access.grants where kind = 'group' and principal_id = @i", db, tx)) { P(c, "i", id); await c.ExecuteNonQueryAsync(ct); }
            await tx.CommitAsync(ct);
            await audit.WriteAsync("access.group_deleted", name, ct: ct);
            return Results.Ok(new { ok = true });
        });

        g.MapPost("/groups/{id:guid}/members", async (Guid id, JsonElement b, ITenantContext t, IUserDirectory users, IAuditLog audit, CancellationToken ct) =>
        {
            var kind = b.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
            if (kind is not ("user" or "group") || !b.TryGetProperty("id", out var m) || m.ValueKind != JsonValueKind.String || !Guid.TryParse(m.GetString(), out var member)) return Bad("bad_request", "Give kind (user or group) and the member's id.");
            await using var db = await Open(t, ct);
            await using (var e = new NpgsqlCommand("select 1 from access.groups where id = @i", db)) { P(e, "i", id); if (await e.ExecuteScalarAsync(ct) == null) return Results.NotFound(); }
            string label;
            if (kind == "user") { var u = await users.GetAsync(member, ct); if (u == null) return Bad("bad_member", "That user does not exist."); label = u.Email; }
            else
            {
                if (member == id) return Bad("bad_member", "A group cannot contain itself.");
                await using var e = new NpgsqlCommand("select name from access.groups where id = @i", db); P(e, "i", member);
                label = await e.ExecuteScalarAsync(ct) as string ?? ""; if (label == "") return Bad("bad_member", "That group does not exist.");
                // refuse a loop: the new member must not already contain this group
                await using var loop = new NpgsqlCommand(@"with recursive down as (select member_id from access.members where group_id = @m and kind = 'group'
                    union select x.member_id from access.members x join down on x.group_id = down.member_id and x.kind = 'group') select 1 from down where member_id = @g limit 1", db);
                P(loop, "m", member); P(loop, "g", id);
                if (await loop.ExecuteScalarAsync(ct) != null) return Bad("bad_member", "That would put the groups in a loop.");
            }
            await using var c = new NpgsqlCommand("insert into access.members (group_id, kind, member_id) values (@g, @k, @m) on conflict do nothing", db); P(c, "g", id); P(c, "k", kind); P(c, "m", member);
            await c.ExecuteNonQueryAsync(ct);
            await audit.WriteAsync("access.member_added", label, "group=" + id, ct: ct);
            return Results.Ok(new { ok = true });
        });

        g.MapDelete("/groups/{id:guid}/members/{kind}/{memberId:guid}", async (Guid id, string kind, Guid memberId, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            await using var c = new NpgsqlCommand("delete from access.members where group_id = @g and kind = @k and member_id = @m", db); P(c, "g", id); P(c, "k", kind); P(c, "m", memberId);
            if (await c.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            await audit.WriteAsync("access.member_removed", memberId.ToString(), "group=" + id, ct: ct);
            return Results.Ok(new { ok = true });
        });

        // ---------------------------------------------------------------- grants
        g.MapGet("/grants", async (ITenantContext t, IUserDirectory users, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            var names = (await users.ListAsync(ct)).ToDictionary(u => u.Id, u => u.Name);
            var gnames = new Dictionary<Guid, string>();
            await using (var c = new NpgsqlCommand("select id, name from access.groups", db)) await using (var rd = await c.ExecuteReaderAsync(ct)) while (await rd.ReadAsync(ct)) gnames[rd.GetGuid(0)] = rd.GetString(1);
            var rows = new List<object>();
            await using (var c = new NpgsqlCommand("select id, kind, principal_id, module, action, company_id, created_at, created_by from access.grants order by module, kind, id", db))
            await using (var rd = await c.ExecuteReaderAsync(ct))
                while (await rd.ReadAsync(ct))
                {
                    var (kind, pid) = (rd.GetString(1), rd.GetGuid(2));
                    rows.Add(new { id = rd.GetInt64(0), kind, principalId = pid, principal = kind == "user" ? names.GetValueOrDefault(pid, "(removed user)") : gnames.GetValueOrDefault(pid, "(removed group)"),
                        module = rd.GetString(3), action = rd.GetString(4), companyId = rd.IsDBNull(5) ? (int?)null : rd.GetInt32(5), createdAt = rd.GetDateTime(6), createdBy = rd.GetString(7) });
                }
            return Results.Ok(rows);
        });

        g.MapPost("/grants", async (JsonElement b, System.Security.Claims.ClaimsPrincipal me, ITenantContext t, IUserDirectory users, ICompanyDirectory companies, IAuditLog audit, CancellationToken ct) =>
        {
            string? S(string n) => b.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var kind = S("kind"); var module = S("module"); var action = S("action");
            if (kind is not ("user" or "group") || !Guid.TryParse(S("principalId"), out var pid)) return Bad("bad_request", "Give kind (user or group) and principalId.");
            if (module == null || !GrantModules.Contains(module)) return Bad("bad_module", "module must be one of " + string.Join(", ", GrantModules) + ".");
            if (action == null || !Acts.All.Contains(action)) return Bad("bad_action", "action must be read, add, modify or delete.");
            int? company = null;
            if (b.TryGetProperty("companyId", out var cv) && cv.ValueKind != JsonValueKind.Null)
            {
                if (cv.ValueKind != JsonValueKind.Number || !cv.TryGetInt32(out var cid)) return Bad("bad_company", "companyId must be a number.");
                if (await companies.GetAsync(cid, ct) == null) return Bad("bad_company", "That company does not exist.");
                company = cid;
            }
            await using var db = await Open(t, ct);
            string label;
            if (kind == "user") { var u = await users.GetAsync(pid, ct); if (u == null) return Bad("bad_principal", "That user does not exist."); label = u.Email; }
            else { await using var e = new NpgsqlCommand("select name from access.groups where id = @i", db); P(e, "i", pid); label = await e.ExecuteScalarAsync(ct) as string ?? ""; if (label == "") return Bad("bad_principal", "That group does not exist."); }
            long id;
            try
            {
                await using var c = new NpgsqlCommand("insert into access.grants (kind, principal_id, module, action, company_id, created_by) values (@k, @p, @m, @a, @c, @by) returning id", db);
                P(c, "k", kind); P(c, "p", pid); P(c, "m", module); P(c, "a", action); P(c, "c", company); P(c, "by", me.FindFirstValue("email") ?? "");
                id = (long)(await c.ExecuteScalarAsync(ct))!;
            }
            catch (PostgresException e) when (e.SqlState == "23505") { return Results.Conflict(new { error = "duplicate", detail = "This permission is already granted." }); }
            await audit.WriteAsync("access.granted", label, $"{module}/{action}/company={(company?.ToString() ?? "all")}", ct: ct);
            return Results.Created("/api/platform/access/grants/" + id, new { id });
        });

        g.MapDelete("/grants/{id:long}", async (long id, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            await using var c = new NpgsqlCommand("delete from access.grants where id = @i returning module || '/' || action", db); P(c, "i", id);
            if (await c.ExecuteScalarAsync(ct) is not string what) return Results.NotFound();
            await audit.WriteAsync("access.revoked", id.ToString(), what, ct: ct);
            return Results.Ok(new { ok = true });
        });

        // ---------------------------------------------------------------- which company a customer user belongs to
        g.MapGet("/user-companies", async (ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            var rows = new List<object>();
            await using var c = new NpgsqlCommand("select user_id, company_id from access.user_companies", db);
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) rows.Add(new { userId = rd.GetGuid(0), companyId = rd.GetInt32(1) });
            return Results.Ok(rows);
        });

        g.MapPut("/user-companies/{userId:guid}", async (Guid userId, JsonElement b, ITenantContext t, IUserDirectory users, ICompanyDirectory companies, IAuditLog audit, CancellationToken ct) =>
        {
            var u = await users.GetAsync(userId, ct); if (u == null) return Results.NotFound();
            await using var db = await Open(t, ct);
            if (!b.TryGetProperty("companyId", out var cv) || cv.ValueKind == JsonValueKind.Null)
            {
                await using var d = new NpgsqlCommand("delete from access.user_companies where user_id = @u", db); P(d, "u", userId); await d.ExecuteNonQueryAsync(ct);
                await audit.WriteAsync("access.company_cleared", u.Email, ct: ct);
                return Results.Ok(new { ok = true });
            }
            if (cv.ValueKind != JsonValueKind.Number || !cv.TryGetInt32(out var cid)) return Bad("bad_company", "companyId must be a number.");
            if (await companies.GetAsync(cid, ct) == null) return Bad("bad_company", "That company does not exist.");
            await using var c = new NpgsqlCommand("insert into access.user_companies (user_id, company_id) values (@u, @c) on conflict (user_id) do update set company_id = @c", db); P(c, "u", userId); P(c, "c", cid);
            await c.ExecuteNonQueryAsync(ct);
            await audit.WriteAsync("access.company_set", u.Email, "company=" + cid, ct: ct);
            return Results.Ok(new { ok = true });
        });

        // ---------------------------------------------------------------- what the signed-in person can do (any signed-in person may ask)
        r.MapGet("/api/platform/access/mine", async (System.Security.Claims.ClaimsPrincipal u, IAccess access, CancellationToken ct) =>
        {
            var result = new Dictionary<string, object>();
            foreach (var m in GrantModules)
            {
                var acts = new Dictionary<string, object?>();
                foreach (var a in Acts.All) { var s = await access.ScopeAsync(u, m, a, ct); acts[a] = s == null ? "all" : s.Count == 0 ? "none" : s; }
                result[m] = acts;
            }
            return Results.Ok(result);
        }).RequireAuthorization();
    }

    static (string name, string desc, string? error) Fields(JsonElement b)
    {
        if (b.ValueKind != JsonValueKind.Object) return ("", "", "The body must be a JSON object.");
        var name = b.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()!.Trim() : "";
        var desc = b.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()!.Trim() : "";
        if (name.Length is 0 or > 100) return ("", "", "Give the group a name (up to 100 characters).");
        if (desc.Length > 500) return ("", "", "The description can be up to 500 characters.");
        return (name, desc, null);
    }
}
