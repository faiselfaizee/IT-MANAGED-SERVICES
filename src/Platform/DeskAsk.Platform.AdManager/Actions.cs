using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace DeskAsk.Platform.AdManager;

/// <summary>What an action came to. Body is what the caller gets back; NeedsApproval means nothing was done and a second person must agree.</summary>
internal sealed record Outcome(bool Ok, int Status, object Body, string Error = "", bool NeedsApproval = false, string Target = "", string GeneratedPassword = "");

/// <summary>
/// The things a person can do to objects in a directory. One place runs them for a direct request and for an approved one, so the
/// checks are the same: the right on the object, the object is inside the domain, protected objects are left alone, and the risky
/// ones (delete, privileged groups and accounts) wait for a second person unless the person holds the approve right.
/// </summary>
internal static class Actions
{
    static readonly string[] Editable =
    {
        "displayName", "givenName", "sn", "initials", "description", "mail", "telephoneNumber", "mobile", "title", "department", "company", "physicalDeliveryOfficeName",
        "streetAddress", "l", "st", "postalCode", "co", "manager", "employeeID", "employeeType", "wWWHomePage", "info", "division",
        "homePhone", "ipPhone", "facsimileTelephoneNumber", "pager", "postOfficeBox", "profilePath", "scriptPath", "homeDrive", "homeDirectory", "employeeNumber", "extensionAttribute1",
        "extensionAttribute2", "extensionAttribute3", "extensionAttribute4", "extensionAttribute5"
    };
    /// <summary>The attributes a new account may be given beyond the ones the form names (the same list as editing, minus the name ones set apart).</summary>
    internal static IReadOnlyList<string> EditableAttrs => Editable;
    static readonly string[] LookupAttrs = Shape.ListAttrs.Concat(new[] { "objectSid", "adminCount", "memberOf", "groupType" }).ToArray();
    static readonly Regex Mail = new(@"^[^@\s]{1,64}@[^@\s]{1,255}\z", RegexOptions.Compiled);

    static Outcome Fail(int status, string error, string detail, int code = 0) => new(false, status, new { error, detail, code, hint = Dir.Hint(code) }, detail);
    static Outcome Forbidden(string detail) => new(false, 403, new { error = "forbidden", detail }, detail);
    static Outcome Done(object body) => new(true, 200, body);

    static string Action(string a) => a switch
    {
        "unlock" => "unlock", "reset-password" => "reset", "enable" or "disable" => "enable", "update" or "set-expiry" => "modify",
        "member-add" or "member-remove" => "members", "move" => "move", "delete" => "delete", "create-user" or "create-group" or "create-ou" => "create",
        "set-flags" => "modify", "offboard" => "enable", _ => ""
    };

    public static async Task<Outcome> RunAsync(JsonElement body, Domain d, ClaimsPrincipal who, NpgsqlConnection db, IAccess access, IControllerRelay relay, IAuditLog audit, bool approved, CancellationToken ct)
    {
        var action = AdModule.Str(body, "action") ?? "";
        var right = Action(action);
        if (right == "") return Fail(400, "bad_input", "Unknown action.");
        var creating = right == "create";
        var target = ((creating ? AdModule.Str(body, "ou") : AdModule.Str(body, "dn")) ?? "").Trim();
        if (!Dir.ValidDn(target) || !Dir.IsUnder(target, d.BaseDn) || (!creating && Dir.Same(target, d.BaseDn))) return Fail(400, "bad_input", "That object is not inside this domain.");
        var actor = AdModule.Actor(who);

        // the right: the person's own, or (for an approved request) the approve right of whoever agreed
        if (!await Perms.CanAsync(db, access, who, d.Id, target, approved ? "approve" : right, ct))
            return Forbidden(approved ? "You may not approve changes here." : "You may not do this to " + target + ".");
        if (action == "offboard" && !approved)
        {
            // each step needs its own right: removing groups (members on each group is checked when it runs), a new password, a move
            if (AdModule.Bool(body, "resetPassword", true) && !await Perms.CanAsync(db, access, who, d.Id, target, "reset", ct)) return Forbidden("You may not reset passwords here.");
            if (AdModule.Bool(body, "removeGroups", true) && !await Perms.CanAsync(db, access, who, d.Id, target, "members", ct)) return Forbidden("You may not change group members here.");
        }
        if (action == "move" || (action == "offboard" && (AdModule.Str(body, "newParent") ?? "").Trim() != ""))
        {
            var np = (AdModule.Str(body, "newParent") ?? "").Trim();
            if (!Dir.ValidDn(np) || !Dir.IsUnder(np, d.BaseDn)) return Fail(400, "bad_input", "Choose where to move it, inside this domain.");
            if (Dir.IsUnder(np, target)) return Fail(400, "bad_input", "An object cannot be moved into itself.");
            if (!await Perms.CanAsync(db, access, who, d.Id, np, approved ? "approve" : "move", ct)) return Forbidden("You may not move things into " + np + ".");
        }

        try
        {
            if (creating) return await CreateAsync(action, body, d, actor, relay, audit, ct);

            // look at the object first: it must exist, and what it is decides what is allowed
            var look = await Dir.RunAsync(relay, d, new[] { Dir.Search(target, "(objectClass=*)", LookupAttrs, "base", 1) }, ct);
            var e = Dir.Entries(look[0]).FirstOrDefault();
            if (e == null) return Fail(404, "not_found", "That object no longer exists.");
            var kind = Shape.KindOf(e.L("objectClass"));
            var rid = BrowseApi.Rid(e.S("objectSid"));
            var name = e.S("name") != "" ? e.S("name") : Dir.RdnValue(e.Dn);
            var isBind = IsBind(d, e);
            var privAccount = kind == "user" && e.Num("adminCount") == 1;
            var privGroup = kind == "group" && Perms.IsPrivilegedGroupName(name);

            if (action is "delete" or "move" && (rid is > 0 and < 1000 || isBind)) return Fail(422, "protected", "This object is built into the directory (or is the service account DeskAsk uses) and is left alone.");
            if (action is "disable" or "reset-password" && (rid == 502 || isBind)) return Fail(422, "protected", rid == 502 ? "The krbtgt account is left alone." : "That is the service account DeskAsk signs in with.");
            if (action is "offboard" && (rid is > 0 and < 1000 || isBind)) return Fail(422, "protected", "This account is built into the directory (or is the service account DeskAsk uses) and is left alone.");
            if (action is "unlock" or "reset-password" or "set-expiry" or "set-flags" or "offboard" && kind != "user") return Fail(400, "bad_input", "That works on user accounts.");
            if (action is "enable" or "disable" && kind is not ("user" or "computer")) return Fail(400, "bad_input", "That works on user and computer accounts.");
            if (action is "member-add" or "member-remove" && kind != "group") return Fail(400, "bad_input", "Members are changed on a group.");

            var risky = action == "delete" || (action is "reset-password" or "disable" or "offboard" && privAccount) || (action is "member-add" or "member-remove" && privGroup);
            if (risky && !approved && !await Perms.CanAsync(db, access, who, d.Id, target, "approve", ct))
                return new Outcome(false, 202, new { }, "", true, target);

            return await DoAsync(action, body, d, e, kind, name, actor, relay, audit, ct);
        }
        catch (DirError ex) { return new Outcome(false, ex.Unreachable ? 503 : 422, new { error = ex.Unreachable ? "unreachable" : "directory", detail = ex.Message, code = ex.Code, hint = Dir.Hint(ex.Code) }, Say(ex)); }
    }

    static string Say(DirError e) => e.Message + (Dir.Hint(e.Code) is { Length: > 0 } h ? " " + h : "");

    /// <summary>Is this object the account DeskAsk itself signs in with?</summary>
    static bool IsBind(Domain d, Ent e)
    {
        if (Dir.Same(e.Dn, d.BindDn) || string.Equals(e.S("userPrincipalName"), d.BindDn, StringComparison.OrdinalIgnoreCase)) return true;
        var at = d.BindDn.IndexOf('@');
        return at > 0 && !d.BindDn.Contains('=') && string.Equals(e.S("sAMAccountName"), d.BindDn[..at], StringComparison.OrdinalIgnoreCase);
    }

    static async Task<Outcome> DoAsync(string action, JsonElement body, Domain d, Ent e, string kind, string name, string actor, IControllerRelay relay, IAuditLog audit, CancellationToken ct)
    {
        var dn = e.Dn; var label = $"{d.Name}: {name}";
        var uac = (int)e.Num("userAccountControl", Dir.UacNormal);
        switch (action)
        {
            case "unlock":
                await Dir.RunAsync(relay, d, new[] { Dir.Modify(dn, ("replace", "lockoutTime", new[] { "0" })) }, ct);
                await audit.WriteAsync("adsuite.unlock", label, dn, actor, ct);
                return Done(new { ok = true });
            case "enable":
            case "disable":
            {
                var next = action == "enable" ? uac & ~Dir.UacDisabled : uac | Dir.UacDisabled;
                if (next == uac) return Done(new { ok = true, unchanged = true });
                await Dir.RunAsync(relay, d, new[] { Dir.Modify(dn, ("replace", "userAccountControl", new[] { next.ToString(CultureInfo.InvariantCulture) })) }, ct);
                await audit.WriteAsync("adsuite." + action, label, dn, actor, ct);
                return Done(new { ok = true });
            }
            case "reset-password":
            {
                var supplied = AdModule.Str(body, "password") ?? "";
                var privileged = e.Num("adminCount") == 1;
                var generate = supplied == "" || privileged; // a privileged account always gets a generated one
                var pw = generate ? Dir.GeneratePassword() : supplied;
                if (pw.Length > 256 || pw.Any(char.IsControl)) return Fail(400, "bad_input", "That password cannot be used.");
                var ops = new List<object> { new { op = "setpw", dn, password = pw, mustChange = AdModule.Bool(body, "mustChange", true) } };
                if (AdModule.Bool(body, "unlock", true)) ops.Add(Dir.Modify(dn, ("replace", "lockoutTime", new[] { "0" })));
                await Dir.RunAsync(relay, d, ops, ct);
                await audit.WriteAsync("adsuite.reset-password", label, dn + (AdModule.Bool(body, "mustChange", true) ? " (must change at next sign-in)" : ""), actor, ct);
                return new Outcome(true, 200, new { ok = true, password = generate ? pw : null, generated = generate }, GeneratedPassword: generate ? pw : "");
            }
            case "set-expiry":
            {
                var v = (AdModule.Str(body, "expires") ?? "").Trim(); string value;
                if (v == "") value = "0";
                else
                {
                    if (!DateOnly.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) return Fail(400, "bad_input", "The date is year-month-day.");
                    value = Dir.ToFileTime(day.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).ToString(CultureInfo.InvariantCulture); // expires at the end of that day
                }
                await Dir.RunAsync(relay, d, new[] { Dir.Modify(dn, ("replace", "accountExpires", new[] { value })) }, ct);
                await audit.WriteAsync("adsuite.set-expiry", label, v == "" ? "never expires" : "expires after " + v, actor, ct);
                return Done(new { ok = true });
            }
            case "update":
            {
                if (!body.TryGetProperty("attrs", out var at) || at.ValueKind != JsonValueKind.Object) return Fail(400, "bad_input", "Nothing to change.");
                var changes = new List<(string, string, string[])>(); var names = new List<string>();
                foreach (var p in at.EnumerateObject())
                {
                    var attr = Editable.FirstOrDefault(x => x.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
                    if (attr == null) return Fail(400, "bad_input", p.Name + " cannot be changed here.");
                    var val = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()!.Trim() : "";
                    if (val.Length > 1024 || val.Any(char.IsControl)) return Fail(400, "bad_input", attr + " is too long or has unusable characters.");
                    if (attr == "mail" && val != "" && !Mail.IsMatch(val)) return Fail(400, "bad_input", "That is not an email address.");
                    if (attr == "manager" && val != "" && (!Dir.ValidDn(val) || !Dir.IsUnder(val, d.BaseDn))) return Fail(400, "bad_input", "The manager is a person in this domain.");
                    changes.Add(("replace", attr, val == "" ? Array.Empty<string>() : new[] { val })); names.Add(attr);
                }
                if (changes.Count == 0) return Fail(400, "bad_input", "Nothing to change.");
                await Dir.RunAsync(relay, d, new[] { Dir.Modify(dn, changes.ToArray()) }, ct);
                await audit.WriteAsync("adsuite.update", label, string.Join(", ", names), actor, ct);
                return Done(new { ok = true });
            }
            case "move":
            {
                var np = AdModule.Str(body, "newParent")!.Trim();
                await Dir.RunAsync(relay, d, new object[] { new { op = "rename", dn, newRdn = Dir.Rdns(dn)[0], newParent = np } }, ct);
                await audit.WriteAsync("adsuite.move", label, $"{Dir.Parent(dn)} -> {np}", actor, ct);
                return Done(new { ok = true, dn = Dir.Rdns(dn)[0] + "," + np });
            }
            case "set-flags":
            {
                var changes = new List<(string, string, string[])>(); var said = new List<string>();
                if (body.TryGetProperty("pwdNeverExpires", out var ne) && ne.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    var next = ne.ValueKind == JsonValueKind.True ? uac | Dir.UacDontExpire : uac & ~Dir.UacDontExpire;
                    if (next != uac) { changes.Add(("replace", "userAccountControl", new[] { next.ToString(CultureInfo.InvariantCulture) })); said.Add(ne.ValueKind == JsonValueKind.True ? "password never expires" : "password expires"); }
                }
                if (body.TryGetProperty("mustChange", out var mc) && mc.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    changes.Add(("replace", "pwdLastSet", new[] { mc.ValueKind == JsonValueKind.True ? "0" : "-1" })); said.Add(mc.ValueKind == JsonValueKind.True ? "must change password at next sign-in" : "need not change password");
                }
                if (changes.Count == 0) return Done(new { ok = true, unchanged = true });
                await Dir.RunAsync(relay, d, changes.Select(c => Dir.Modify(dn, c)).ToArray(), ct);
                await audit.WriteAsync("adsuite.set-flags", label, string.Join(", ", said), actor, ct);
                return Done(new { ok = true });
            }
            case "offboard":
            {
                // one job, in this order: switch off, new unknown password, out of every group, note, then the move (the name changes last)
                var ops = new List<object>();
                var steps = new List<string>();
                ops.Add(Dir.Modify(dn, ("replace", "userAccountControl", new[] { (uac | Dir.UacDisabled).ToString(CultureInfo.InvariantCulture) }))); steps.Add("disabled");
                if (AdModule.Bool(body, "resetPassword", true)) { ops.Add(new { op = "setpw", dn, password = Dir.GeneratePassword(), mustChange = false }); steps.Add("password replaced"); }
                var groups = e.L("memberOf").Where(g => Dir.IsUnder(g, d.BaseDn)).ToList();
                if (AdModule.Bool(body, "removeGroups", true))
                {
                    foreach (var g in groups) ops.Add(new { op = "modify", dn = g, ignore = new[] { 16, 53 }, changes = new[] { new { op = "delete", attr = "member", values = new[] { dn } } } });
                    steps.Add($"removed from {groups.Count} group(s)");
                }
                if (AdModule.Bool(body, "clearManager", false)) { ops.Add(Dir.Modify(dn, ("replace", "manager", Array.Empty<string>()))); steps.Add("manager cleared"); }
                var note = AdModule.Clip((AdModule.Str(body, "note") ?? "").Trim(), 300);
                if (note.Any(char.IsControl)) return Fail(400, "bad_input", "The note has unusable characters.");
                ops.Add(Dir.Modify(dn, ("replace", "description", new[] { $"Left {DateTime.UtcNow:yyyy-MM-dd} ({actor}){(note == "" ? "" : ": " + note)}" })));
                var np = (AdModule.Str(body, "newParent") ?? "").Trim(); var newDn = dn;
                if (np != "" && !Dir.Same(Dir.Parent(dn), np)) { ops.Add(new { op = "rename", dn, newRdn = Dir.Rdns(dn)[0], newParent = np }); newDn = Dir.Rdns(dn)[0] + "," + np; steps.Add("moved to " + np); }
                await Dir.RunAsync(relay, d, ops, ct, 90);
                await audit.WriteAsync("adsuite.offboard", label, string.Join("; ", steps) + (groups.Count > 0 && AdModule.Bool(body, "removeGroups", true) ? " [" + string.Join(", ", groups.Select(Dir.RdnValue)) + "]" : ""), actor, ct);
                return Done(new { ok = true, dn = newDn, steps, groups = groups.Select(Dir.RdnValue).ToList() });
            }
            case "delete":
                await Dir.RunAsync(relay, d, new object[] { new { op = "delete", dn } }, ct);
                await audit.WriteAsync("adsuite.delete", label, kind + " " + dn, actor, ct);
                return Done(new { ok = true });
            case "member-add":
            case "member-remove":
            {
                var members = new List<string>();
                if (body.TryGetProperty("members", out var ms) && ms.ValueKind == JsonValueKind.Array) foreach (var m in ms.EnumerateArray()) if (m.ValueKind == JsonValueKind.String) members.Add(m.GetString()!.Trim());
                members = members.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (members.Count is 0 or > 200 || members.Any(m => !Dir.ValidDn(m) || !Dir.IsUnder(m, d.BaseDn))) return Fail(400, "bad_input", "Choose between 1 and 200 members of this domain.");
                var add = action == "member-add";
                var ops = members.Select(m => (object)new
                {
                    op = "modify", dn, ignore = add ? new[] { 68 } : new[] { 16, 53 },
                    changes = new[] { new { op = add ? "add" : "delete", attr = "member", values = new[] { m } } }
                }).ToList();
                await Dir.RunAsync(relay, d, ops, ct);
                await audit.WriteAsync("adsuite." + action, label, string.Join("; ", members.Select(Dir.RdnValue)), actor, ct);
                return Done(new { ok = true, count = members.Count });
            }
        }
        return Fail(400, "bad_input", "Unknown action.");
    }

    static async Task<Outcome> CreateAsync(string action, JsonElement body, Domain d, string actor, IControllerRelay relay, IAuditLog audit, CancellationToken ct)
    {
        var parent = AdModule.Str(body, "ou")!.Trim();
        string S(string n) => (AdModule.Str(body, n) ?? "").Trim();
        foreach (var k in new[] { "name", "first", "last", "display", "sam", "upn", "email", "title", "department", "description" })
            if (S(k).Length > 256 || S(k).Any(char.IsControl)) return Fail(400, "bad_input", k + " is too long or has unusable characters.");
        if (action == "create-ou")
        {
            if (S("name") == "") return Fail(400, "bad_input", "Give the OU a name.");
            var ouDn = "OU=" + Dir.EscapeRdn(S("name")) + "," + parent;
            var attrs = new Dictionary<string, string[]> { ["objectClass"] = new[] { "top", "organizationalUnit" }, ["ou"] = new[] { S("name") } };
            if (S("description") != "") attrs["description"] = new[] { S("description") };
            await Dir.RunAsync(relay, d, new object[] { new { op = "add", dn = ouDn, entry = attrs } }, ct);
            await audit.WriteAsync("adsuite.create-ou", $"{d.Name}: {S("name")}", ouDn, actor, ct);
            return Done(new { ok = true, dn = ouDn });
        }
        if (action == "create-group")
        {
            var name = S("name"); var sam = S("sam") != "" ? S("sam") : name;
            if (name == "" || sam.Length > 64 || sam.Any(c => "\"/\\[]:;|=,+*?<>@".Contains(c))) return Fail(400, "bad_input", "Give the group a name (the pre-Windows 2000 name cannot hold \" / \\ [ ] : ; | = , + * ? < > @).");
            var scope = S("scope") switch { "universal" => 8, "domainlocal" => 4, _ => 2 };
            var security = S("type") != "distribution";
            var gt = unchecked(scope | (security ? (int)0x80000000 : 0));
            var gdn = "CN=" + Dir.EscapeRdn(name) + "," + parent;
            var attrs = new Dictionary<string, string[]> { ["objectClass"] = new[] { "top", "group" }, ["cn"] = new[] { name }, ["sAMAccountName"] = new[] { sam }, ["groupType"] = new[] { gt.ToString(CultureInfo.InvariantCulture) } };
            if (S("description") != "") attrs["description"] = new[] { S("description") };
            await Dir.RunAsync(relay, d, new object[] { new { op = "add", dn = gdn, entry = attrs } }, ct);
            await audit.WriteAsync("adsuite.create-group", $"{d.Name}: {name}", gdn, actor, ct);
            return Done(new { ok = true, dn = gdn });
        }

        // create-user: the account is made disabled, given its password, then switched on, so it is never live without one
        var sam2 = S("sam");
        if (!Dir.Sam.IsMatch(sam2)) return Fail(400, "bad_input", "The sign-in name has letters, digits and . _ - only, up to 20 characters.");
        var first = S("first"); var last = S("last");
        var display = S("display") != "" ? S("display") : (first + " " + last).Trim();
        if (display == "") display = sam2;
        var upn = S("upn") != "" ? S("upn") : sam2 + "@" + d.DnsName;
        if (!upn.Contains('@')) return Fail(400, "bad_input", "The user principal name looks like name@contoso.com.");
        if (S("email") != "" && !Mail.IsMatch(S("email"))) return Fail(400, "bad_input", "That is not an email address.");
        var service = S("kind") == "service";
        var expires = S("expires"); long expFt = 0;
        if (expires != "")
        {
            if (!DateOnly.TryParseExact(expires, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) return Fail(400, "bad_input", "The date is year-month-day.");
            expFt = Dir.ToFileTime(day.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        }
        var supplied = AdModule.Str(body, "password") ?? "";
        var generate = supplied == ""; var pw = generate ? Dir.GeneratePassword() : supplied;
        if (pw.Length > 256 || pw.Any(char.IsControl)) return Fail(400, "bad_input", "That password cannot be used.");
        var groups = new List<string>();
        if (body.TryGetProperty("groups", out var gs) && gs.ValueKind == JsonValueKind.Array) foreach (var g in gs.EnumerateArray()) if (g.ValueKind == JsonValueKind.String) groups.Add(g.GetString()!.Trim());
        if (groups.Count > 50 || groups.Any(g => !Dir.ValidDn(g) || !Dir.IsUnder(g, d.BaseDn))) return Fail(400, "bad_input", "Choose up to 50 groups of this domain.");
        var mgr = S("manager"); if (mgr != "" && (!Dir.ValidDn(mgr) || !Dir.IsUnder(mgr, d.BaseDn))) return Fail(400, "bad_input", "The manager is a person in this domain.");

        var udn = "CN=" + Dir.EscapeRdn(display) + "," + parent;
        var a = new Dictionary<string, string[]>
        {
            ["objectClass"] = new[] { "top", "person", "organizationalPerson", "user" }, ["cn"] = new[] { display }, ["sAMAccountName"] = new[] { sam2 },
            ["userPrincipalName"] = new[] { upn }, ["displayName"] = new[] { display }, ["userAccountControl"] = new[] { (Dir.UacNormal | Dir.UacDisabled).ToString(CultureInfo.InvariantCulture) }
        };
        void Opt(string attr, string v) { if (v != "") a[attr] = new[] { v }; }
        Opt("givenName", first); Opt("sn", last); Opt("mail", S("email")); Opt("title", S("title")); Opt("department", S("department")); Opt("description", S("description")); Opt("manager", mgr);
        // everything else the form offers (phones, address, organisation, profile), from the same list editing allows
        if (body.TryGetProperty("attrs", out var more) && more.ValueKind == JsonValueKind.Object)
            foreach (var p in more.EnumerateObject())
            {
                var attr = Editable.FirstOrDefault(x => x.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
                if (attr == null || attr is "displayName" or "manager") return Fail(400, "bad_input", p.Name + " cannot be set here.");
                var val = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()!.Trim() : "";
                if (val.Length > 1024 || val.Any(char.IsControl)) return Fail(400, "bad_input", attr + " is too long or has unusable characters.");
                if (attr == "mail" && val != "" && !Mail.IsMatch(val)) return Fail(400, "bad_input", "That is not an email address.");
                Opt(attr, val);
            }
        var live = Dir.UacNormal | (service || AdModule.Bool(body, "pwdNeverExpires") ? Dir.UacDontExpire : 0) | (AdModule.Bool(body, "disabled") ? Dir.UacDisabled : 0);
        var ops = new List<object>
        {
            new { op = "add", dn = udn, entry = a },
            new { op = "setpw", dn = udn, password = pw, mustChange = !service && !AdModule.Bool(body, "pwdNeverExpires") && AdModule.Bool(body, "mustChange", true) },
            Dir.Modify(udn, ("replace", "userAccountControl", new[] { live.ToString(CultureInfo.InvariantCulture) }))
        };
        if (expFt > 0) ops.Add(Dir.Modify(udn, ("replace", "accountExpires", new[] { expFt.ToString(CultureInfo.InvariantCulture) })));
        foreach (var g in groups) ops.Add(new { op = "modify", dn = g, ignore = new[] { 68 }, changes = new[] { new { op = "add", attr = "member", values = new[] { udn } } } });
        try { await Dir.RunAsync(relay, d, ops, ct); }
        catch (DirError ex) when (ex.Step >= 1)
        {
            await audit.WriteAsync("adsuite.create-user", $"{d.Name}: {display}", udn + " created but not finished: " + ex.Message, actor, ct);
            return new Outcome(false, 422, new { error = "partial", detail = "The account " + udn + " was created, but a later step failed (" + ex.Message + "). It is still switched off; finish it by hand or delete it.", code = ex.Code, hint = Dir.Hint(ex.Code), dn = udn }, ex.Message);
        }
        await audit.WriteAsync("adsuite.create-user", $"{d.Name}: {display}", $"{udn} ({(service ? "service" : S("kind") == "external" ? "external" : "internal")})", actor, ct);
        return new Outcome(true, 200, new { ok = true, dn = udn, password = generate ? pw : null, generated = generate }, GeneratedPassword: generate ? pw : "");
    }

    /// <summary>Asks a second person to agree. A password someone typed is never kept: an approved reset makes a new one.</summary>
    public static async Task<int> AskApprovalAsync(NpgsqlConnection db, Domain d, JsonElement body, ClaimsPrincipal who, string target, CancellationToken ct)
    {
        var obj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body.GetRawText()) ?? new();
        obj.Remove("password");
        var action = AdModule.Str(body, "action") ?? "";
        var reason = AdModule.Clip((AdModule.Str(body, "reason") ?? "").Trim(), 500);
        await using var c = new NpgsqlCommand("insert into adsuite.approvals (domain_id, action, target_dn, target_name, reason, body, requested_by, requested_by_id) values (@d,@a,@t,@n,@r,@b::jsonb,@by,@id) returning id", db);
        AdModule.P(c, "d", d.Id); AdModule.P(c, "a", action); AdModule.P(c, "t", target); AdModule.P(c, "n", Dir.RdnValue(target)); AdModule.P(c, "r", reason);
        AdModule.P(c, "b", JsonSerializer.Serialize(obj)); AdModule.P(c, "by", AdModule.Actor(who)); AdModule.P(c, "id", AdModule.Me(who));
        return (int)(await c.ExecuteScalarAsync(ct))!;
    }
}
