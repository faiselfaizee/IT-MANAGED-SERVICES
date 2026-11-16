using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DeskAsk.Platform.Identity;

/// <summary>
/// The licence limit on staff accounts (servicedesk:agents): active administrators and engineers. Viewers and customer users are not counted.
/// </summary>
public static class StaffLimit
{
    public static bool IsStaff(string role) => role is "admin" or "engineer";

    public static Task<int> CountAsync(IdentityDb db, CancellationToken ct) => db.Users.CountAsync(u => u.Active && (u.Role == "admin" || u.Role == "engineer"), ct);

    /// <summary>A 402 answer when adding this many staff accounts would go over the limit; null when it fits (or there is no limit).</summary>
    public static async Task<IResult?> RefuseAsync(IEntitlements ent, IdentityDb db, int adding, CancellationToken ct)
    {
        if (adding <= 0) return null;
        var lim = (await ent.CurrentAsync(ct)).Limit(Modules.ServiceDesk, LicenseLimits.Agents);
        if (lim == null || await CountAsync(db, ct) + adding <= lim) return null;
        return LimitResult.Reached(Modules.ServiceDesk, LicenseLimits.Agents, lim.Value, "staff accounts (administrators and engineers)");
    }

    /// <summary>For an import: refuses when the people not yet in DeskAsk would go over the limit with a staff role.</summary>
    public static async Task<IResult?> RefuseImportAsync(IEntitlements ent, IdentityDb db, string role, IEnumerable<string> emails, CancellationToken ct)
    {
        if (!IsStaff(role)) return null;
        var list = emails.Select(e => e.Trim().ToLowerInvariant()).Where(e => e.Contains('@')).Distinct().ToList();
        var have = await db.Users.Where(u => list.Contains(u.Email)).Select(u => u.Email).ToListAsync(ct);
        return await RefuseAsync(ent, db, list.Count - have.Count, ct);
    }
}

public sealed class IdentityLicenseUsage : ILicenseUsage
{
    readonly IdentityDb db;
    public IdentityLicenseUsage(IdentityDb d) { db = d; }
    public async Task<IReadOnlyDictionary<string, long>> UsageAsync(CancellationToken ct = default) =>
        new Dictionary<string, long> { [Modules.ServiceDesk + ":" + LicenseLimits.Agents] = await StaffLimit.CountAsync(db, ct) };
}
