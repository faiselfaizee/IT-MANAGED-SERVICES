using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DeskAsk.Platform.AdManager;

/// <summary>Ready-made questions about a directory: who is inactive, who never expires, who is privileged, which groups are empty.</summary>
internal static class ReportsApi
{
    const string Uac = "1.2.840.113556.1.4.803";
    const int Limit = 2000;

    sealed record Def(string Key, string Title, string Description, string Kind, bool Days, int DefaultDays);

    static readonly Def[] Catalog =
    {
        new("inactive-users", "Inactive users", "Enabled accounts that have not signed in for a number of days. The directory updates this about every two weeks, so allow a margin.", "user", true, 90),
        new("never-logged-on", "Never signed in", "Enabled accounts that have never signed in.", "user", false, 0),
        new("password-never-expires", "Passwords that never expire", "Enabled accounts whose password never expires.", "user", false, 0),
        new("privileged-users", "Privileged accounts", "Accounts the directory marks as administrators (members of Domain Admins, Enterprise Admins and other protected groups).", "user", false, 0),
        new("disabled-users", "Disabled users", "Accounts that are switched off.", "user", false, 0),
        new("locked-users", "Locked-out users", "Accounts locked by too many wrong passwords.", "user", false, 0),
        new("expired-users", "Expired accounts", "Accounts whose expiry date has passed.", "user", false, 0),
        new("empty-groups", "Empty groups", "Groups with no members.", "group", false, 0),
        new("stale-computers", "Stale computers", "Enabled computers that have not signed in to the domain for a number of days.", "computer", true, 90),
        new("disabled-computers", "Disabled computers", "Computer accounts that are switched off.", "computer", false, 0),
    };

    static string Filter(string key, int days)
    {
        var cutoff = Dir.ToFileTime(DateTime.UtcNow.AddDays(-days));
        const string user = "(objectCategory=person)(objectClass=user)";
        return key switch
        {
            "inactive-users" => $"(&{user}(!(userAccountControl:{Uac}:=2))(lastLogonTimestamp<={cutoff}))",
            "never-logged-on" => $"(&{user}(!(userAccountControl:{Uac}:=2))(!(lastLogonTimestamp=*)))",
            "password-never-expires" => $"(&{user}(!(userAccountControl:{Uac}:=2))(userAccountControl:{Uac}:=65536))",
            "privileged-users" => $"(&{user}(adminCount=1))",
            "disabled-users" => $"(&{user}(userAccountControl:{Uac}:=2))",
            "locked-users" => $"(&{user}(lockoutTime>=1))",
            "expired-users" => $"(&{user}(accountExpires>=1)(accountExpires<={Dir.ToFileTime(DateTime.UtcNow)})(!(accountExpires=9223372036854775807)))",
            "empty-groups" => "(&(objectClass=group)(!(member=*)))",
            "stale-computers" => $"(&(objectClass=computer)(!(userAccountControl:{Uac}:=2))(lastLogonTimestamp<={cutoff}))",
            "disabled-computers" => $"(&(objectClass=computer)(userAccountControl:{Uac}:=2))",
            _ => ""
        };
    }

    /// <summary>A cell safe to open in a spreadsheet: values that start like a formula are made plain text.</summary>
    internal static string Cell(string? v)
    {
        v ??= "";
        if (v.Length > 0 && "=+-@\t\r".Contains(v[0])) v = "'" + v;
        return v.Contains('"') || v.Contains(',') || v.Contains('\n') || v.Contains('\r') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    static string Date(DateTime? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        AdModule.Use(r.MapGet("/api/adsuite/reports", () => Results.Ok(Catalog.Select(c => new { key = c.Key, title = c.Title, description = c.Description, kind = c.Kind, days = c.Days, defaultDays = c.DefaultDays }))), key);

        AdModule.Use(r.MapGet("/api/adsuite/domains/{id:int}/report", async (int id, string report, int? days, string? format, ClaimsPrincipal u, ITenantContext t, IDataProtectionProvider dp, IControllerRelay relay, IAccess access, IAuditLog audit, CancellationToken ct) =>
        {
            var def = Catalog.FirstOrDefault(c => c.Key == report); if (def == null) return AdModule.Bad("bad_input", "Unknown report.");
            await using var db = await AdModule.Open(t, ct);
            var d = await Dir.LoadAsync(db, dp, t, id, ct); if (d == null) return Results.NotFound();
            var scope = await Perms.ReadScopeAsync(db, access, u, id, ct);
            if (scope is { Count: 0 }) return AdModule.Forbid("You have no access to this domain.");
            var n = def.Days ? Math.Clamp(days ?? def.DefaultDays, 1, 3650) : 0;
            var filter = Filter(def.Key, n);
            try
            {
                var res = await Dir.RunAsync(relay, d, (scope ?? new List<string> { d.BaseDn }).Select(b => Dir.Search(b, filter, Shape.ListAttrs, "sub", Limit + 1)), ct, 90);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var rows = new List<Obj>(); var cut = false;
                foreach (var one in res) { cut |= Dir.Truncated(one); foreach (var e in Dir.Entries(one)) if (seen.Add(e.Dn)) rows.Add(Shape.ToObj(e)); }
                if (def.Key == "locked-users") rows = rows.Where(x => x.Locked).ToList();
                rows = rows.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
                if (rows.Count > Limit) { rows = rows.Take(Limit).ToList(); cut = true; }
                if (format == "csv")
                {
                    await audit.WriteAsync("adsuite.report.export", $"{d.Name}: {def.Title}", $"{rows.Count} rows", null, ct);
                    var sb = new StringBuilder("name,sign-in name,user principal name,email,title,department,description,enabled,locked,last sign-in,created,account expires,distinguished name\r\n");
                    foreach (var x in rows)
                        sb.Append(string.Join(",", new[] { x.Name, x.Sam, x.Upn, x.Mail, x.Title, x.Department, x.Description, x.Disabled ? "no" : "yes", x.Locked ? "yes" : "no", Date(x.LastLogon), Date(x.Created), Date(x.AccountExpires), x.Dn }.Select(Cell))).Append("\r\n");
                    return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv; charset=utf-8", $"{def.Key}-{d.Name}-{DateTime.UtcNow:yyyyMMdd}.csv");
                }
                return Results.Ok(new { key = def.Key, title = def.Title, days = n, items = rows, truncated = cut });
            }
            catch (DirError e) { return Dir.Explain(e); }
        }), key);
    }
}
