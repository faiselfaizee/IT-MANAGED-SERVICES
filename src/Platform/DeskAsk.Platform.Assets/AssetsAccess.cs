using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DeskAsk.Platform.Assets;

/// <summary>
/// Who may use Assets. The access module decides (roles give the defaults, grants add to them): customer employees have none, a customer
/// administrator reads only the assets of their own company, and delegated people or groups may add, modify and delete.
/// </summary>
public static class AssetsAccess
{
    /// <summary>Roles that may read the endpoints that are limited by company (assets, companies, one device).</summary>
    public static readonly string[] Readers = { "admin", "engineer", "viewer", "cadmin" };
    /// <summary>Roles that may read everything else (software lists, contracts, agents ...), which is not limited by company.</summary>
    public static readonly string[] StaffReaders = { "admin", "engineer", "viewer" };

    public static Task<IReadOnlyList<int>?> ScopeAsync(HttpContext h, string action) =>
        h.RequestServices.GetRequiredService<IAccess>().ScopeAsync(h.User, Modules.Assets, action, h.RequestAborted);

    /// <summary>Gives a command the companies the caller may see: use "(not @sc or &lt;column&gt; = any(@scope))" in the SQL.</summary>
    public static void Bind(NpgsqlCommand c, IReadOnlyList<int>? scope)
    {
        c.Parameters.AddWithValue("sc", scope != null);
        c.Parameters.AddWithValue("scope", scope?.ToArray() ?? Array.Empty<int>());
    }

    public static NpgsqlCommand Scoped(this NpgsqlCommand c, IReadOnlyList<int>? scope) { Bind(c, scope); return c; }

    public static async Task<bool> InScope(NpgsqlConnection db, long assetId, IReadOnlyList<int>? scope, CancellationToken ct)
    {
        if (scope == null) return true;
        await using var c = new NpgsqlCommand("select 1 from assets.assets where id = @i and company_id = any(@scope)", db);
        c.Parameters.AddWithValue("i", assetId); c.Parameters.AddWithValue("scope", scope.ToArray());
        return await c.ExecuteScalarAsync(ct) != null;
    }
}
