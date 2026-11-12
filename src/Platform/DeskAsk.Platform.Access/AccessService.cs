using System.Security.Claims;
using Npgsql;

namespace DeskAsk.Platform.Access;

/// <summary>
/// The platform's one answer to "may this person do this?". The role gives a default, and grants to the person or to any group they belong to
/// (groups may hold users and other groups) add to it. A grant may be limited to one company's data.
/// </summary>
public sealed class AccessService : IAccess
{
    readonly ITenantContext tenant; readonly IEntitlements entitlements;
    public AccessService(ITenantContext t, IEntitlements e) { tenant = t; entitlements = e; }

    /// <summary>What each role may do before any grant. Modules not listed here are for administrators (and for grants).</summary>
    static (bool All, bool OwnCompany) Default(string role, string module, string action)
    {
        if (role == "admin") return (true, false);
        // engineers keep the asset records up to date (add, change) but cannot delete: they ask, and someone with the delete right approves
        if (module == Modules.Assets && role == "engineer" && (action == Acts.Add || action == Acts.Modify)) return (true, false);
        if (module == Modules.Assets && action == Acts.Read)
            return role switch { "engineer" or "viewer" => (true, false), "cadmin" => (false, true), _ => (false, false) };
        return (false, false);
    }

    /// <summary>Customer administrators may be given more than reading only when the licence includes it.</summary>
    static bool CustomerWriteLicensed(License lic, string module) => lic.Payload?.Plan == "development" || lic.Limit(module, "customer_write") > 0;

    async Task<NpgsqlConnection> Open(CancellationToken ct) { var db = new NpgsqlConnection(tenant.Require().ConnectionString); await db.OpenAsync(ct); return db; }

    public async Task<IReadOnlyList<int>?> ScopeAsync(ClaimsPrincipal user, string module, string action, CancellationToken ct = default)
    {
        var role = user.FindFirstValue("role") ?? "";
        Guid.TryParse(user.FindFirstValue("sub"), out var id);
        var (all, own) = Default(role, module, action);
        if (all) return null;
        var companies = new HashSet<int>();
        await using var db = await Open(ct);
        if (own && id != Guid.Empty)
        {
            await using var c = new NpgsqlCommand("select company_id from access.user_companies where user_id = @u", db); c.Parameters.AddWithValue("u", id);
            if (await c.ExecuteScalarAsync(ct) is int mine) companies.Add(mine);
        }
        if (id == Guid.Empty) return companies.ToList();
        if (role == "cadmin" && action != Acts.Read && !CustomerWriteLicensed(await entitlements.CurrentAsync(ct), module)) return companies.ToList();
        await using var g = new NpgsqlCommand(@"with recursive grp as (
              select group_id from access.members where kind = 'user' and member_id = @u
              union
              select m.group_id from access.members m join grp on m.kind = 'group' and m.member_id = grp.group_id)
            select company_id from access.grants where module = @m and action = @a
              and ((kind = 'user' and principal_id = @u) or (kind = 'group' and principal_id in (select group_id from grp)))", db);
        g.Parameters.AddWithValue("u", id); g.Parameters.AddWithValue("m", module); g.Parameters.AddWithValue("a", action);
        await using var rd = await g.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            if (rd.IsDBNull(0)) return null;      // a grant that is not limited to a company
            companies.Add(rd.GetInt32(0));
        }
        return companies.ToList();
    }

    public async Task SetCompanyAsync(Guid userId, int? companyId, CancellationToken ct = default)
    {
        await using var db = await Open(ct);
        await using var c = new NpgsqlCommand(companyId == null ? "delete from access.user_companies where user_id = @u"
            : "insert into access.user_companies (user_id, company_id) values (@u, @c) on conflict (user_id) do update set company_id = @c", db);
        c.Parameters.AddWithValue("u", userId); if (companyId != null) c.Parameters.AddWithValue("c", companyId.Value);
        await c.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> AllowedAsync(ClaimsPrincipal user, string module, string action, int? companyId = null, CancellationToken ct = default)
    {
        var scope = await ScopeAsync(user, module, action, ct);
        return scope == null || (companyId is int c ? scope.Contains(c) : scope.Count > 0);
    }
}
