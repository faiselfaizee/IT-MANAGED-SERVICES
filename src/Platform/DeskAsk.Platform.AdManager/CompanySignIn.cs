using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace DeskAsk.Platform.AdManager;

/// <summary>
/// A company's employees picked from its domain controller sign in to DeskAsk with their Windows password. The person is found by their account name
/// in the company's domains (in order), then the site controller binds as them on a second connection (directory step "verify", agent 6.17 and later).
/// </summary>
internal sealed class CompanySignIn(ITenantContext tenant, IDataProtectionProvider dp, IControllerRelay relay) : ICompanyDirectorySignIn
{
    public async Task<DirectorySignIn> CheckAsync(int companyId, string account, string password, CancellationToken ct = default)
    {
        if (password.Length == 0 || !Dir.Sam.IsMatch(account)) return new(false, "wrong");
        await using var db = new NpgsqlConnection(tenant.Require().ConnectionString); await db.OpenAsync(ct);
        var ids = new List<int>();
        await using (var c = new NpgsqlCommand("select id from adsuite.domains where company_id = @co and enabled order by id", db))
        {
            AdModule.P(c, "co", companyId);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) ids.Add(r.GetInt32(0));
        }
        if (ids.Count == 0) return new(false, "not_set_up");
        var unreachable = false;
        foreach (var id in ids)
        {
            var d = await Dir.LoadAsync(db, dp, tenant, id, ct); if (d == null) continue;
            try
            {
                var found = await Dir.RunAsync(relay, d, new[] { Dir.Search(d.BaseDn, "(&(objectCategory=person)(objectClass=user)(sAMAccountName=" + Dir.Esc(account) + "))", new[] { "sAMAccountName" }, "sub", 2) }, ct, 20);
                var dn = found.SelectMany(Dir.Entries).Select(e => e.Dn).FirstOrDefault(x => x != "");
                if (dn == null) continue;
                var res = await Dir.RunAsync(relay, d, new object[] { new { op = "verify", dn, password } }, ct, 20);
                var one = res.FirstOrDefault();
                if (one.ValueKind == JsonValueKind.Object && one.TryGetProperty("valid", out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    return v.ValueKind == JsonValueKind.True ? new(true, "") : new(false, one.TryGetProperty("reason", out var why) && why.ValueKind == JsonValueKind.String ? why.GetString()! : "wrong");
                return new(false, "old_controller");   // an older controller answers the step without a verdict
            }
            catch (DirError e) when (e.Message.Contains("unknown directory step", StringComparison.Ordinal)) { return new(false, "old_controller"); }
            catch (DirError) { unreachable = true; }
        }
        return new(false, unreachable ? "unreachable" : "not_found");
    }
}
