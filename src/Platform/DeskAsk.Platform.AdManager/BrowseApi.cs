using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace DeskAsk.Platform.AdManager;

/// <summary>Look through a domain: the OU tree, a search, one object with what a person may do to it, and the actions themselves.</summary>
internal static class BrowseApi
{
    const string Uac = "1.2.840.113556.1.4.803";

    static readonly string[] DetailAttrs = Shape.ListAttrs.Concat(new[]
    {
        "givenName", "sn", "initials", "telephoneNumber", "mobile", "physicalDeliveryOfficeName", "company", "manager", "streetAddress", "l", "st", "postalCode", "co",
        "homeDirectory", "homeDrive", "scriptPath", "profilePath", "pwdLastSet", "badPwdCount", "lastLogon", "logonCount", "memberOf", "member", "adminCount", "objectGUID", "objectSid",
        "msDS-UserPasswordExpiryTimeComputed", "info", "employeeID", "employeeType", "division", "wWWHomePage", "groupType", "managedBy", "whenChanged", "primaryGroupID", "userWorkstations"
    }).Concat(Actions.EditableAttrs).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    static string KindFilter(string kind) => kind switch
    {
        "group" => "(objectClass=group)",
        "computer" => "(objectClass=computer)",
        "ou" => "(objectClass=organizationalUnit)",
        "all" => "(|(&(objectCategory=person)(objectClass=user))(objectClass=computer)(objectClass=group)(objectClass=organizationalUnit))",
        _ => "(&(objectCategory=person)(objectClass=user))"
    };

    static string TextFilter(string kind, string q)
    {
        var e = Dir.Esc(q);
        var attrs = kind switch
        {
            "group" => new[] { "cn", "sAMAccountName", "description", "mail" },
            "computer" => new[] { "cn", "dNSHostName", "sAMAccountName", "operatingSystem", "description" },
            "ou" => new[] { "name", "description" },
            "all" => new[] { "cn", "sAMAccountName", "displayName", "mail", "userPrincipalName", "description", "dNSHostName" },
            _ => new[] { "cn", "sAMAccountName", "displayName", "mail", "userPrincipalName", "givenName", "sn", "description" }
        };
        return "(|" + string.Concat(attrs.Select(a => $"({a}=*{e}*)")) + ")";
    }

    static string StatusFilter(string status) => status switch
    {
        "disabled" => $"(userAccountControl:{Uac}:=2)",
        "enabled" => $"(!(userAccountControl:{Uac}:=2))",
        "locked" => "(lockoutTime>=1)",
        "expired" => $"(&(accountExpires>=1)(accountExpires<={Dir.ToFileTime(DateTime.UtcNow)})(!(accountExpires=9223372036854775807)))",
        "neverexpires" => $"(userAccountControl:{Uac}:=65536)",
        _ => ""
    };

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        AdModule.Use(r.MapGet("/api/adsuite/domains/{id:int}/tree", async (int id, string? @base, ClaimsPrincipal u, ITenantContext t, IDataProtectionProvider dp, IControllerRelay relay, IAccess access, CancellationToken ct) =>
        {
            await using var db = await AdModule.Open(t, ct);
            var d = await Dir.LoadAsync(db, dp, t, id, ct); if (d == null) return Results.NotFound();
            var scope = await Perms.ReadScopeAsync(db, access, u, id, ct);
            if (scope is { Count: 0 }) return AdModule.Forbid("You have no access to this domain.");
            var b = (@base ?? "").Trim();
            if (b == "")
            {
                if (scope != null) return Results.Ok(new { baseDn = "", items = scope.Select(ou => new { dn = ou, name = Dir.RdnValue(ou), kind = "ou", description = "" }) });
                b = d.BaseDn;
            }
            if (!Dir.ValidDn(b) || !Dir.IsUnder(b, d.BaseDn)) return AdModule.Bad("bad_input", "That is not inside this domain.");
            if (scope != null && !scope.Any(ou => Dir.IsUnder(b, ou))) return AdModule.Forbid("You have no access to this part of the directory.");
            try
            {
                var res = await Dir.RunAsync(relay, d, new[] { Dir.Search(b, "(|(objectClass=organizationalUnit)(objectClass=container)(objectClass=builtinDomain))", new[] { "name", "description", "objectClass" }, "one", 2000) }, ct);
                var items = Dir.Entries(res[0]).Select(e => new { dn = e.Dn, name = e.S("name") != "" ? e.S("name") : Dir.RdnValue(e.Dn), kind = Shape.KindOf(e.L("objectClass")), description = e.S("description") })
                    .OrderBy(x => x.name, StringComparer.OrdinalIgnoreCase).ToList();
                return Results.Ok(new { baseDn = b, items });
            }
            catch (DirError e) { return Dir.Explain(e); }
        }), key);

        AdModule.Use(r.MapGet("/api/adsuite/domains/{id:int}/search", async (int id, string? kind, string? q, string? @base, string? status, int? limit, ClaimsPrincipal u, ITenantContext t, IDataProtectionProvider dp, IControllerRelay relay, IAccess access, CancellationToken ct) =>
        {
            await using var db = await AdModule.Open(t, ct);
            var d = await Dir.LoadAsync(db, dp, t, id, ct); if (d == null) return Results.NotFound();
            var scope = await Perms.ReadScopeAsync(db, access, u, id, ct);
            if (scope is { Count: 0 }) return AdModule.Forbid("You have no access to this domain.");
            kind = kind is "user" or "group" or "computer" or "ou" or "all" ? kind : "user";
            var max = Math.Clamp(limit ?? 100, 1, 500);
            var bases = new List<string>(); var b = (@base ?? "").Trim();
            if (b != "")
            {
                if (!Dir.ValidDn(b) || !Dir.IsUnder(b, d.BaseDn)) return AdModule.Bad("bad_input", "That is not inside this domain.");
                if (scope != null && !scope.Any(ou => Dir.IsUnder(b, ou))) return AdModule.Forbid("You have no access to this part of the directory.");
                bases.Add(b);
            }
            else bases.AddRange(scope ?? new List<string> { d.BaseDn });
            var filter = "(&" + KindFilter(kind) + ((q ?? "").Trim() != "" ? TextFilter(kind, q!.Trim()) : "") + (kind == "user" && status != null ? StatusFilter(status) : "") + ")";
            try
            {
                var res = await Dir.RunAsync(relay, d, bases.Select(x => Dir.Search(x, filter, Shape.ListAttrs, "sub", max + 1)), ct);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var items = new List<Obj>(); var cut = false;
                foreach (var one in res)
                {
                    cut |= Dir.Truncated(one);
                    foreach (var e in Dir.Entries(one)) if (seen.Add(e.Dn)) items.Add(Shape.ToObj(e));
                }
                if (kind == "user" && status == "locked") items = items.Where(x => x.Locked).ToList();
                items = items.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
                if (items.Count > max) { items = items.Take(max).ToList(); cut = true; }
                return Results.Ok(new { items, truncated = cut });
            }
            catch (DirError e) { return Dir.Explain(e); }
        }), key);

        AdModule.Use(r.MapGet("/api/adsuite/domains/{id:int}/object", async (int id, string dn, ClaimsPrincipal u, ITenantContext t, IDataProtectionProvider dp, IControllerRelay relay, IAccess access, CancellationToken ct) =>
        {
            await using var db = await AdModule.Open(t, ct);
            var d = await Dir.LoadAsync(db, dp, t, id, ct); if (d == null) return Results.NotFound();
            if (!Dir.ValidDn(dn) || !Dir.IsUnder(dn, d.BaseDn)) return AdModule.Bad("bad_input", "That is not inside this domain.");
            if (!await Perms.CanAsync(db, access, u, id, dn, "read", ct)) return AdModule.Forbid("You may not look at this object.");
            try
            {
                var res = await Dir.RunAsync(relay, d, new[] { Dir.Search(dn, "(objectClass=*)", DetailAttrs, "base", 1) }, ct);
                var e = Dir.Entries(res[0]).FirstOrDefault(); if (e == null) return Results.NotFound();
                var o = Shape.ToObj(e);
                var memberOf = e.L("memberOf").Select(x => new { dn = x, name = Dir.RdnValue(x) }).OrderBy(x => x.name, StringComparer.OrdinalIgnoreCase).ToList();
                var members = e.Ranged("member");
                var sid = e.S("objectSid"); var rid = Rid(sid);
                var neverExp = o.PwdNeverExpires;
                var pwdExpires = neverExp ? null : Dir.FileTime(e.Num("msDS-UserPasswordExpiryTimeComputed"));
                var privileged = e.Num("adminCount") == 1 || memberOf.Any(m => Perms.IsPrivilegedGroupName(m.name)) || (o.Kind == "group" && Perms.IsPrivilegedGroupName(o.Name));
                string[] show = { "givenName", "sn", "initials", "telephoneNumber", "mobile", "physicalDeliveryOfficeName", "company", "streetAddress", "l", "st", "postalCode", "co", "homeDirectory", "homeDrive", "scriptPath", "profilePath", "info", "employeeID", "employeeType", "division", "wWWHomePage", "userWorkstations", "badPwdCount", "logonCount", "primaryGroupID" };
                var fields = show.Where(k => e.S(k) != "").ToDictionary(k => k, k => e.S(k));
                var manager = e.S("manager");
                return Results.Ok(new
                {
                    obj = o, dn = e.Dn, guid = e.S("objectGUID"), sid, groupType = e.S("groupType"), fields,
                    values = Actions.EditableAttrs.ToDictionary(k => k, k => e.S(k)),   // every editable field, empty ones too, for the edit form
                    manager = manager == "" ? null : new { dn = manager, name = Dir.RdnValue(manager) },
                    memberOf,
                    members = o.Kind == "group" ? members.Take(500).Select(x => new { dn = x, name = Dir.RdnValue(x) }).OrderBy(x => x.name, StringComparer.OrdinalIgnoreCase).ToList() : null,
                    membersTotal = members.Count,
                    passwordLastSet = Dir.FileTime(e.Num("pwdLastSet")), passwordExpires = pwdExpires, lastLogonLocal = Dir.FileTime(e.Num("lastLogon")), changed = Dir.Generalized(e.S("whenChanged")),
                    privileged, protectedObject = rid is > 0 and < 1000,
                    held = (await Perms.HeldAsync(db, access, u, id, dn, ct)).OrderBy(x => Array.IndexOf(AdModule.Rights, x)).ToList()
                });
            }
            catch (DirError e) { return Dir.Explain(e); }
        }), key);

        AdModule.Use(r.MapPost("/api/adsuite/domains/{id:int}/actions", async (int id, JsonElement body, ClaimsPrincipal u, ITenantContext t, IDataProtectionProvider dp, IControllerRelay relay, IAccess access, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await AdModule.Open(t, ct);
            var d = await Dir.LoadAsync(db, dp, t, id, ct); if (d == null) return Results.NotFound();
            if (!d.Enabled) return AdModule.Bad("disabled", "This domain is switched off.");
            var o = await Actions.RunAsync(body, d, u, db, access, relay, audit, approved: false, ct);
            if (o.NeedsApproval)
            {
                var ap = await Actions.AskApprovalAsync(db, d, body, u, o.Target, ct);
                await audit.WriteAsync("adsuite.approval.ask", $"{d.Name}: {o.Target}", $"{AdModule.Str(body, "action")} waits for a second person", null, ct);
                return Results.Json(new { ok = true, approval = ap, message = "This needs a second person with the approve right. It has been sent for approval." }, statusCode: 202);
            }
            return Results.Json(o.Body, statusCode: o.Status);
        }), key);
    }

    internal static long Rid(string sid) { var k = sid.LastIndexOf('-'); return k > 0 && long.TryParse(sid[(k + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0; }
}
