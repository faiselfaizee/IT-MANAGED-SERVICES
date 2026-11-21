using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DeskAsk.Platform.AdManager;

/// <summary>
/// Reads the people of a customer's domain controller (read only) and hands them to the Assets module, so they can be listed under Inventory › People and chosen when something is handed out.
/// Works for customers who have Assets or Monitoring; AD Manager is not needed.
/// </summary>
internal static class PeopleSync
{
    const string Filter = "(&(objectCategory=person)(objectClass=user))";
    static readonly string[] Attrs = { "sAMAccountName", "userPrincipalName", "mail", "displayName", "name", "cn", "title", "department", "userAccountControl" };
    public const int Max = 20000;

    public sealed record Result(bool Ok, int Count, string Error);

    /// <summary>Reads every enabled domain of the customer; only when all of them answer is the customer's list replaced, so a controller that is down never empties it.</summary>
    public static async Task<Result> SyncCompanyAsync(NpgsqlConnection db, int companyId, IDataProtectionProvider dp, ITenantContext t, IControllerRelay relay, IDirectoryPeople people, CancellationToken ct)
    {
        var ids = new List<int>();
        await using (var c = new NpgsqlCommand("select id from adsuite.domains where company_id = @co and enabled order by id", db))
        {
            AdModule.P(c, "co", companyId);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) ids.Add(r.GetInt32(0));
        }
        if (ids.Count == 0) return new Result(false, 0, "No domain controller is set for this customer.");

        var all = new List<DirectoryPerson>(); string? error = null;
        foreach (var id in ids)
        {
            string? err = null; var n = 0;
            try
            {
                var d = await Dir.LoadAsync(db, dp, t, id, ct);
                if (d == null) continue;
                var res = await Dir.RunAsync(relay, d, new[] { Dir.Search(d.BaseDn, Filter, Attrs, "sub", Max + 1) }, ct, 120);
                foreach (var one in res)
                    foreach (var e in Dir.Entries(one))
                    {
                        var sam = e.S("sAMAccountName"); if (sam == "") continue;
                        var name = e.S("displayName") != "" ? e.S("displayName") : e.S("name") != "" ? e.S("name") : e.S("cn");
                        if (name == "") continue;
                        var uac = (int)e.Num("userAccountControl");
                        all.Add(new DirectoryPerson(sam, name, e.S("mail"), e.S("userPrincipalName"), e.S("department"), e.S("title"), (uac & Dir.UacDisabled) == 0));
                        n++;
                    }
                if (n > Max) err = "More than " + Max + " people; the list is cut.";
            }
            catch (DirError e) { err = e.Message; error ??= e.Message; }
            await using var u = new NpgsqlCommand("update adsuite.domains set people_error = @e, people_synced_at = case when @e = '' or @e like 'More than%' then now() else people_synced_at end, people_count = case when @e = '' or @e like 'More than%' then @n else people_count end where id = @i", db);
            AdModule.P(u, "e", AdModule.Clip(err ?? "", 500)); AdModule.P(u, "n", n); AdModule.P(u, "i", id);
            await u.ExecuteNonQueryAsync(ct);
        }
        if (error != null) return new Result(false, 0, error);
        await people.ReplaceAsync(companyId, all, ct);
        return new Result(true, all.Count, "");
    }
}

/// <summary>Every few hours: refreshes the people of each customer that has a domain controller set.</summary>
internal sealed class PeopleSyncJob : IJobHandler
{
    public const string JobType = "adsuite.people";
    public string Type => JobType;

    public async Task RunAsync(JobContext job, CancellationToken ct)
    {
        var sp = job.Services;
        var lic = await sp.GetRequiredService<IEntitlements>().CurrentAsync(ct);
        if (!lic.Has(Modules.Assets) && !lic.Has(Modules.Monitoring)) return;
        var relay = sp.GetService<IControllerRelay>(); var people = sp.GetService<IDirectoryPeople>();
        if (relay == null || people == null) return;
        var t = sp.GetRequiredService<ITenantContext>(); var dp = sp.GetRequiredService<IDataProtectionProvider>();
        await using var db = new NpgsqlConnection(job.Tenant.ConnectionString); await db.OpenAsync(ct);
        var companies = new List<int>();
        try
        {
            await using var c = new NpgsqlCommand("select distinct company_id from adsuite.domains where enabled order by 1", db);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) companies.Add(r.GetInt32(0));
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedTable) { return; }
        foreach (var co in companies) await PeopleSync.SyncCompanyAsync(db, co, dp, t, relay, people, ct);
    }
}
