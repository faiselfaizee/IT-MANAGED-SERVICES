using Microsoft.Extensions.DependencyInjection;

namespace DeskAsk.Platform.Identity;

/// <summary>
/// Single-company mode: a customer login has no company to choose, so every new customer or customer-admin login
/// (first Microsoft or Google sign-in, an import, an administrator adding someone) is tied to the one company.
/// </summary>
internal static class SingleCompanyLink
{
    public static async Task TryAsync(IServiceProvider sp, IEnumerable<UserAccount> users, CancellationToken ct)
    {
        var people = users.Where(u => u.Role is "customer" or "cadmin").ToList();
        if (people.Count == 0) return;
        var ent = sp.GetService<IEntitlements>(); var companies = sp.GetService<ICompanyDirectory>(); var access = sp.GetService<IAccess>();
        if (ent == null || companies == null || access == null || !(await ent.CurrentAsync(ct)).SingleCompany) return;
        var list = await companies.ListAsync(ct);
        var one = list.FirstOrDefault(c => c.Active) ?? list.FirstOrDefault();
        if (one == null) return;
        foreach (var u in people) await access.SetCompanyAsync(u.Id, one.Id, ct);
    }
}
