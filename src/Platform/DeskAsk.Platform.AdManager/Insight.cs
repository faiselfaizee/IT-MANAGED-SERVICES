using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DeskAsk.Platform.AdManager;

/// <summary>
/// Read-only insight into a directory (6.24.0): a health check with findings, Group Policy objects and where they are linked, DNS zones and records,
/// and a map of the directory (OUs with counts, domain controllers, trusts). Everything is read through the site controller with the same
/// search step the other pages use; nothing here changes the directory. It needs a right on the whole domain, not on one OU.
/// </summary>
internal static class InsightApi
{
    const string Uac = "1.2.840.113556.1.4.803";
    const string User = "(objectCategory=person)(objectClass=user)";
    static readonly Regex Guid = new(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}", RegexOptions.Compiled);
    static readonly Regex Link = new(@"\[LDAP://([^;\]]+);(\d+)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public sealed class Finding
    {
        public string Key { get; set; } = "";
        public string Title { get; set; } = "";
        /// <summary>bad, warn, info or ok.</summary>
        public string Level { get; set; } = "info";
        public string Text { get; set; } = "";
        public List<string> Items { get; set; } = new();
        public int? Count { get; set; }
    }

    static string Key(string dn) => string.Join(",", Dir.Rdns(dn)).ToLowerInvariant();
    static string Short(Ent e) => e.S("sAMAccountName") != "" ? e.S("sAMAccountName") : e.S("name") != "" ? e.S("name") : Dir.RdnValue(e.Dn);

    /// <summary>The domain, when the person may read all of it; otherwise the answer to send back.</summary>
    static async Task<(Domain? D, IResult? Fail)> Gate(int id, ClaimsPrincipal u, ITenantContext t, IDataProtectionProvider dp, IAccess access, CancellationToken ct)
    {
        await using var db = await AdModule.Open(t, ct);
        var d = await Dir.LoadAsync(db, dp, t, id, ct);
        if (d == null) return (null, Results.NotFound());
        if (!d.Enabled) return (null, AdModule.Bad("disabled", "This domain is switched off."));
        var scope = await Perms.ReadScopeAsync(db, access, u, id, ct);
        if (scope != null) return (null, AdModule.Forbid("This needs the right to look at the whole domain, not one OU."));
        return (d, null);
    }

    static double? DaysSince(DateTime? t) => t == null ? null : Math.Round((DateTime.UtcNow - t.Value).TotalDays);

    /// <summary>A server name from the DN of a role owner (the NTDS Settings object of the server).</summary>
    static string Owner(Ent? e) => e == null || e.S("fSMORoleOwner") == "" ? "" : Dir.RdnValue(Dir.Parent(e.S("fSMORoleOwner")));

    static async Task<List<JsonElementLite>> Try(IControllerRelay relay, Domain d, IEnumerable<object> ops, CancellationToken ct)
    {
        try { return (await Dir.RunAsync(relay, d, ops, ct, 60)).Select(x => new JsonElementLite(x)).ToList(); }
        catch (DirError) { return new(); }
    }
    readonly record struct JsonElementLite(System.Text.Json.JsonElement E);
    static Ent? First(List<JsonElementLite> r, int i) => i < r.Count ? Dir.Entries(r[i].E).FirstOrDefault() : null;
    static List<Ent> All(List<JsonElementLite> r, int i) => i < r.Count ? Dir.Entries(r[i].E).ToList() : new();
    static bool Cut(List<JsonElementLite> r, int i) => i < r.Count && Dir.Truncated(r[i].E);

    // ---- health

    static string LevelName(long v) => v switch { 0 => "Windows 2000", 1 => "Windows Server 2003 interim", 2 => "Windows Server 2003", 3 => "Windows Server 2008", 4 => "Windows Server 2008 R2", 5 => "Windows Server 2012", 6 => "Windows Server 2012 R2", 7 => "Windows Server 2016 or newer", 10 => "Windows Server 2025", _ => "level " + v };

    static async Task<object> Health(IControllerRelay relay, Domain d, CancellationToken ct)
    {
        var b = d.BaseDn;
        var ops = new List<object>
        {
            /*0*/ Dir.Search(b, "(objectClass=domainDNS)", new[] { "msDS-Behavior-Version", "lockoutThreshold", "minPwdLength", "pwdProperties", "pwdHistoryLength", "ms-DS-MachineAccountQuota", "fSMORoleOwner" }, "base", 1),
            /*1*/ Dir.Search(b, "(&(objectClass=user)(sAMAccountName=krbtgt))", new[] { "pwdLastSet" }, "sub", 1),
            /*2*/ Dir.Search(b, $"(&(objectClass=computer)(userAccountControl:{Uac}:=8192))", new[] { "name", "dNSHostName", "operatingSystem", "lastLogonTimestamp" }, "sub", 100),
            /*3*/ Dir.Search("CN=RID Manager$,CN=System," + b, "(objectClass=*)", new[] { "fSMORoleOwner" }, "base", 1),
            /*4*/ Dir.Search("CN=Infrastructure," + b, "(objectClass=*)", new[] { "fSMORoleOwner" }, "base", 1),
            /*5*/ Dir.Search(b, "(&(objectClass=group)(sAMAccountName=Domain Admins))", new[] { "member" }, "sub", 1),
            /*6*/ Dir.Search(b, $"(&{User}(adminCount=1)(!(userAccountControl:{Uac}:=2))(userAccountControl:{Uac}:=65536))", new[] { "sAMAccountName", "name" }, "sub", 51),
            /*7*/ Dir.Search(b, $"(&{User}(adminCount=1)(servicePrincipalName=*)(!(sAMAccountName=krbtgt)))", new[] { "sAMAccountName", "name" }, "sub", 51),
            /*8*/ Dir.Search(b, $"(&{User}(!(userAccountControl:{Uac}:=2))(userAccountControl:{Uac}:=32))", new[] { "sAMAccountName", "name" }, "sub", 51),
            /*9*/ Dir.Search(b, $"(&{User}(!(userAccountControl:{Uac}:=2))(userAccountControl:{Uac}:=4194304))", new[] { "sAMAccountName", "name" }, "sub", 51),
            /*10*/ Dir.Search(b, $"(&{User}(!(userAccountControl:{Uac}:=2))(|(userAccountControl:{Uac}:=128)(userAccountControl:{Uac}:=2097152)))", new[] { "sAMAccountName", "name" }, "sub", 51),
            /*11*/ Dir.Search(b, $"(&{User}(userAccountControl:{Uac}:=524288))", new[] { "sAMAccountName", "name" }, "sub", 51),
            /*12*/ Dir.Search(b, $"(&(objectClass=computer)(userAccountControl:{Uac}:=524288)(!(userAccountControl:{Uac}:=8192)))", new[] { "name" }, "sub", 51),
            /*13*/ Dir.Search(b, $"(&{User}(!(userAccountControl:{Uac}:=2))(lastLogonTimestamp<={Dir.ToFileTime(DateTime.UtcNow.AddDays(-180))}))", new[] { "sAMAccountName" }, "sub", 1001),
            /*14*/ Dir.Search(b, $"(&{User}(!(userAccountControl:{Uac}:=2))(userAccountControl:{Uac}:=65536))", new[] { "sAMAccountName" }, "sub", 1001),
        };
        var r = await Try(relay, d, ops, ct);
        if (r.Count == 0)   // the bundle failed as a whole (for example a role object that this directory does not have): ask for the essentials alone
            r = await Try(relay, d, ops.Where((_, i) => i is 0 or 1 or 2 or 5 or 6 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 14), ct);
        if (r.Count == 0) throw new DirError("The directory did not answer the health questions.");
        var essentials = r.Count == ops.Count;
        // when only the essentials ran, indexes shift: rebuild by name
        Ent? dom, krb, rid, infra, da; List<Ent> dcs, pwdNever, spn, noReq, noPre, weak, delU, delC, stale, neverExp; bool cutStale, cutNever;
        if (essentials)
        {
            dom = First(r, 0); krb = First(r, 1); dcs = All(r, 2); rid = First(r, 3); infra = First(r, 4); da = First(r, 5);
            pwdNever = All(r, 6); spn = All(r, 7); noReq = All(r, 8); noPre = All(r, 9); weak = All(r, 10); delU = All(r, 11); delC = All(r, 12); stale = All(r, 13); neverExp = All(r, 14); cutStale = Cut(r, 13); cutNever = Cut(r, 14);
        }
        else
        {
            dom = First(r, 0); krb = First(r, 1); dcs = All(r, 2); rid = null; infra = null; da = First(r, 3);
            pwdNever = All(r, 4); spn = All(r, 5); noReq = All(r, 6); noPre = All(r, 7); weak = All(r, 8); delU = All(r, 9); delC = All(r, 10); stale = All(r, 11); neverExp = All(r, 12); cutStale = Cut(r, 11); cutNever = Cut(r, 12);
        }

        var f = new List<Finding>();
        Finding Add(string key, string title, string level, string text, IEnumerable<string>? items = null, int? count = null)
        { var x = new Finding { Key = key, Title = title, Level = level, Text = text, Items = (items ?? Array.Empty<string>()).Take(20).ToList(), Count = count }; f.Add(x); return x; }

        // domain level and password policy
        if (dom != null)
        {
            var lvl = dom.Num("msDS-Behavior-Version", -1);
            if (lvl >= 0) Add("level", "Domain functional level", lvl < 6 ? "warn" : "ok", $"The domain runs at {LevelName(lvl)}." + (lvl < 6 ? " Raise it once every domain controller runs Windows Server 2012 R2 or newer." : ""));
            var minLen = dom.Num("minPwdLength", -1);
            if (minLen >= 0) Add("pwlen", "Minimum password length", minLen == 0 ? "bad" : minLen < 12 ? "warn" : "ok", minLen == 0 ? "Passwords may be empty." : $"Passwords need at least {minLen} characters." + (minLen < 12 ? " Twelve or more is recommended." : ""));
            var lockT = dom.Num("lockoutThreshold", -1);
            if (lockT >= 0) Add("lockout", "Account lockout", lockT == 0 ? "warn" : "ok", lockT == 0 ? "Accounts are never locked after wrong passwords, so guessing is not slowed down." : $"Accounts lock after {lockT} wrong passwords.");
            if (dom.A.ContainsKey("pwdProperties")) Add("complexity", "Password complexity", (dom.Num("pwdProperties") & 1) == 0 ? "warn" : "ok", (dom.Num("pwdProperties") & 1) == 0 ? "Password complexity is switched off." : "Password complexity is on.");
            var quota = dom.Num("ms-DS-MachineAccountQuota", -1);
            if (quota >= 0) Add("quota", "Joining computers to the domain", quota > 0 ? "warn" : "ok", quota > 0 ? $"Any signed-in user may add up to {quota} computers to the domain. Set ms-DS-MachineAccountQuota to 0 and delegate joining instead." : "Ordinary users cannot add computers to the domain.");
        }

        // krbtgt
        var kt = krb == null ? null : Dir.FileTime(krb.Num("pwdLastSet"));
        if (kt != null) { var days = DaysSince(kt)!.Value; Add("krbtgt", "Password of the krbtgt account", days > 365 ? "bad" : days > 180 ? "warn" : "ok", $"Last changed {days:0} days ago ({kt:yyyy-MM-dd})." + (days > 180 ? " Change it twice, with ten hours or more between, as part of regular hygiene." : "")); }

        // domain controllers and roles
        {
            var items = dcs.Select(e => $"{(e.S("dNSHostName") != "" ? e.S("dNSHostName") : e.S("name"))} · {(e.S("operatingSystem") == "" ? "system unknown" : e.S("operatingSystem"))}").OrderBy(x => x).ToList();
            var old = dcs.Where(e => Regex.IsMatch(e.S("operatingSystem"), @"200[03]|2008|2012")).Select(e => e.S("name")).ToList();
            var quiet = dcs.Where(e => Dir.FileTime(e.Num("lastLogonTimestamp")) is { } t && (DateTime.UtcNow - t).TotalDays > 60).Select(e => e.S("name")).ToList();
            Add("dcs", "Domain controllers", dcs.Count == 0 ? "info" : dcs.Count == 1 ? "warn" : "ok", dcs.Count == 0 ? "No domain controllers were found." : dcs.Count == 1 ? "There is only one domain controller. If it fails, nobody can sign in." : $"{dcs.Count} domain controllers.", items, dcs.Count);
            if (old.Count > 0) Add("dcs-old", "Domain controllers on old systems", "warn", "These run a Windows Server version that no longer gets security updates.", old, old.Count);
            if (quiet.Count > 0) Add("dcs-quiet", "Domain controllers not seen for 60 days", "warn", "These controllers have not signed in to the domain for two months. Remove them if they are gone.", quiet, quiet.Count);
            var roles = new List<string>();
            if (Owner(dom) != "") roles.Add("PDC emulator: " + Owner(dom));
            if (Owner(rid) != "") roles.Add("RID master: " + Owner(rid));
            if (Owner(infra) != "") roles.Add("Infrastructure master: " + Owner(infra));
            if (roles.Count > 0) Add("roles", "Domain roles", "info", "Which controller holds each single-master role.", roles);
        }

        // forest level: recycle bin, schema and naming masters
        try
        {
            var root = await Try(relay, d, new object[] { Dir.Search("", "(objectClass=*)", new[] { "configurationNamingContext", "schemaNamingContext" }, "base", 1) }, ct);
            var re = First(root, 0);
            var cfg = re?.S("configurationNamingContext") is { Length: > 0 } c ? c : "CN=Configuration," + string.Join(",", Dir.Rdns(b).Where(x => x.StartsWith("DC=", StringComparison.OrdinalIgnoreCase)));
            var schema = re?.S("schemaNamingContext") is { Length: > 0 } s ? s : "CN=Schema," + cfg;
            var fr = await Try(relay, d, new object[]
            {
                Dir.Search("CN=Recycle Bin Feature,CN=Optional Features,CN=Directory Service,CN=Windows NT,CN=Services," + cfg, "(objectClass=*)", new[] { "msDS-EnabledFeatureBL" }, "base", 1),
                Dir.Search(schema, "(objectClass=*)", new[] { "fSMORoleOwner" }, "base", 1),
                Dir.Search("CN=Partitions," + cfg, "(objectClass=*)", new[] { "fSMORoleOwner", "msDS-Behavior-Version" }, "base", 1),
            }, ct);
            if (fr.Count >= 3)
            {
                var rb = First(fr, 0);
                if (rb != null) Add("recycle", "AD Recycle Bin", rb.L("msDS-EnabledFeatureBL").Count > 0 ? "ok" : "warn", rb.L("msDS-EnabledFeatureBL").Count > 0 ? "The Recycle Bin is on, so a deleted account or group can be restored with all its settings." : "The Recycle Bin is off. A deleted account cannot be restored with its group memberships. It cannot be switched off again once on; enable it from Active Directory Administrative Center.");
                var fl = First(fr, 2);
                var fo = new List<string>(); if (Owner(First(fr, 1)) != "") fo.Add("Schema master: " + Owner(First(fr, 1))); if (Owner(fl) != "") fo.Add("Domain naming master: " + Owner(fl));
                if (fo.Count > 0) Add("forest-roles", "Forest roles", "info", "Which controller holds each forest-wide role.", fo);
                if (fl != null && fl.A.ContainsKey("msDS-Behavior-Version")) { var v = fl.Num("msDS-Behavior-Version"); Add("forest-level", "Forest functional level", v < 6 ? "warn" : "ok", $"The forest runs at {LevelName(v)}."); }
            }
        }
        catch (Exception) { /* the configuration partition could not be read with this account, or answered something unexpected */ }

        // privileged accounts
        var admins = da?.Ranged("member").Select(Dir.RdnValue).OrderBy(x => x).ToList() ?? new List<string>();
        if (da != null) Add("domain-admins", "Members of Domain Admins", admins.Count > 5 ? "warn" : "info", $"{admins.Count} account(s).{(admins.Count > 5 ? " Keep this group as small as you can." : "")}", admins, admins.Count);
        string Many(List<Ent> l, bool cut) => (cut || l.Count > 50 ? "More than 50 accounts. " : "");
        if (pwdNever.Count > 0) Add("priv-never", "Administrators whose password never expires", "bad", "Administrator accounts should change their password regularly.", pwdNever.Select(Short), pwdNever.Count);
        if (spn.Count > 0) Add("priv-spn", "Administrators with a service name", "bad", "An administrator account with a service principal name can have its password guessed offline by any user (Kerberoasting). Use a separate service account.", spn.Select(Short), spn.Count);
        if (noReq.Count > 0) Add("no-pwd", "Accounts that need no password", "bad", Many(noReq, false) + "These enabled accounts are allowed to have an empty password.", noReq.Select(Short), noReq.Count);
        if (noPre.Count > 0) Add("no-preauth", "Accounts without Kerberos pre-authentication", "bad", Many(noPre, false) + "Anyone can ask for data to guess these passwords offline.", noPre.Select(Short), noPre.Count);
        if (weak.Count > 0) Add("weak-crypto", "Accounts with weak encryption", "warn", Many(weak, false) + "These enabled accounts store the password reversibly or allow only DES.", weak.Select(Short), weak.Count);
        if (delU.Count + delC.Count > 0) Add("delegation", "Unconstrained delegation", "warn", "Accounts and computers (other than domain controllers) that may act as anyone to any service. Use constrained delegation instead.", delU.Select(Short).Concat(delC.Select(e => e.S("name") + " (computer)")), delU.Count + delC.Count);
        Add("stale", "Enabled users not signed in for 180 days", stale.Count == 0 ? "ok" : "warn", stale.Count == 0 ? "None." : (cutStale ? "More than 1000" : stale.Count.ToString()) + " enabled accounts have not signed in for six months. Review and disable them.", stale.Select(Short), stale.Count);
        Add("never-expire", "Enabled users whose password never expires", neverExp.Count == 0 ? "ok" : "info", neverExp.Count == 0 ? "None." : (cutNever ? "More than 1000" : neverExp.Count.ToString()) + " enabled accounts. Fine for service accounts; review the rest.", neverExp.Select(Short), neverExp.Count);

        var order = new[] { "bad", "warn", "info", "ok" };
        f = f.OrderBy(x => Array.IndexOf(order, x.Level)).ToList();
        return new { domain = d.Name, generated = DateTime.UtcNow, summary = new { bad = f.Count(x => x.Level == "bad"), warn = f.Count(x => x.Level == "warn"), ok = f.Count(x => x.Level == "ok") }, findings = f };
    }

    // ---- group policy

    static object Policies(List<Ent> gpos, List<Ent> holders, string baseDn)
    {
        var links = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
        var blocked = new List<string>();
        foreach (var h in holders)
        {
            var target = Dir.Same(h.Dn, baseDn) ? "(domain) " + Dir.RdnValue(h.Dn) : Dir.RdnValue(h.Dn);
            if ((h.Num("gPOptions") & 1) != 0) blocked.Add(target);
            foreach (Match m in Link.Matches(h.S("gPLink")))
            {
                var g = Guid.Match(m.Groups[1].Value); if (!g.Success) continue;
                var opt = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                if (!links.TryGetValue(g.Value, out var l)) links[g.Value] = l = new();
                l.Add(new { target, dn = h.Dn, enforced = (opt & 2) != 0, disabled = (opt & 1) != 0 });
            }
        }
        var rows = gpos.Select(e =>
        {
            var id = Guid.Match(e.Dn); var gid = id.Success ? id.Value.ToUpperInvariant() : e.S("name");
            var v = e.Num("versionNumber"); var flags = (int)e.Num("flags");
            links.TryGetValue(gid, out var l);
            return new
            {
                id = gid, name = e.S("displayName") != "" ? e.S("displayName") : gid, userVersion = (v >> 16) & 0xFFFF, computerVersion = v & 0xFFFF,
                status = flags switch { 0 => "Enabled", 1 => "User settings off", 2 => "Computer settings off", 3 => "All settings off", _ => "Enabled" },
                created = Dir.Generalized(e.S("whenCreated")), changed = Dir.Generalized(e.S("whenChanged")), wmi = e.S("gPCWQLFilter") != "",
                links = l ?? new List<object>(), empty = v == 0, unlinked = l == null || l.Count == 0, allOff = flags == 3
            };
        }).OrderBy(x => x.name, StringComparer.OrdinalIgnoreCase).ToList();
        return new
        {
            gpos = rows, blocked,
            summary = new { total = rows.Count, unlinked = rows.Count(x => x.unlinked), empty = rows.Count(x => x.empty), allOff = rows.Count(x => x.allOff), wmi = rows.Count(x => x.wmi) }
        };
    }

    // ---- DNS

    static string CountName(byte[] b, int at)
    {
        var parts = new List<string>(); var i = at + 2;   // total length, label count, then the labels
        while (i < b.Length && b[i] != 0) { var n = b[i]; if (i + 1 + n > b.Length) break; parts.Add(System.Text.Encoding.UTF8.GetString(b, i + 1, n)); i += 1 + n; }
        return string.Join(".", parts);
    }

    /// <summary>Reads one dnsRecord value (MS-DNSP): data length, type, then a 24-byte header, then the data.</summary>
    internal static (string Type, string Data, long Ttl)? Record(string b64)
    {
        if (!b64.StartsWith("base64:", StringComparison.Ordinal)) return null;
        byte[] b; try { b = Convert.FromBase64String(b64[7..]); } catch (FormatException) { return null; }
        if (b.Length < 24) return null;
        int len = b[0] | (b[1] << 8), type = b[2] | (b[3] << 8); long ttl = ((long)b[12] << 24) | ((long)b[13] << 16) | ((long)b[14] << 8) | b[15];
        if (24 + len > b.Length) return null;
        var d = new ArraySegment<byte>(b, 24, len).ToArray();
        string Data() => type switch
        {
            1 when d.Length == 4 => string.Join(".", d),
            28 when d.Length == 16 => new System.Net.IPAddress(d).ToString(),
            2 or 5 or 12 or 39 => CountName(d, 0),
            15 when d.Length > 3 => $"{(d[0] << 8) | d[1]} {CountName(d, 2)}",
            33 when d.Length > 7 => $"{(d[0] << 8) | d[1]} {(d[2] << 8) | d[3]} {(d[4] << 8) | d[5]} {CountName(d, 6)}",
            16 => TxtOf(d),
            6 => "start of authority",
            _ => "(type " + type + ")"
        };
        var name = type switch { 1 => "A", 28 => "AAAA", 2 => "NS", 5 => "CNAME", 12 => "PTR", 15 => "MX", 16 => "TXT", 33 => "SRV", 6 => "SOA", 39 => "DNAME", _ => "type " + type };
        return (name, Data(), ttl);
    }
    static string TxtOf(byte[] d)
    {
        var sb = new System.Text.StringBuilder(); var i = 0;
        while (i < d.Length) { var n = d[i]; if (i + 1 + n > d.Length) break; sb.Append(System.Text.Encoding.UTF8.GetString(d, i + 1, n)); i += 1 + n; }
        return sb.ToString();
    }

    static readonly string[] Partitions = { "domain", "forest", "legacy" };
    static string ZoneBase(string source, string baseDn) => source switch
    {
        "forest" => "CN=MicrosoftDNS,DC=ForestDnsZones," + string.Join(",", Dir.Rdns(baseDn).Where(x => x.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))),
        "legacy" => "CN=MicrosoftDNS,CN=System," + baseDn,
        _ => "CN=MicrosoftDNS,DC=DomainDnsZones," + baseDn
    };

    // ---- map

    sealed class Node
    {
        public string Dn { get; set; } = ""; public string Name { get; set; } = ""; public string Kind { get; set; } = "ou"; public string Description { get; set; } = "";
        public int Users { get; set; } public int Computers { get; set; } public int Groups { get; set; } public bool Linked { get; set; }
        public int TotalUsers { get; set; } public int TotalComputers { get; set; } public int TotalGroups { get; set; }
        public List<Node> Children { get; set; } = new();
    }

    static void Roll(Node n) { n.TotalUsers = n.Users; n.TotalComputers = n.Computers; n.TotalGroups = n.Groups; foreach (var c in n.Children) { Roll(c); n.TotalUsers += c.TotalUsers; n.TotalComputers += c.TotalComputers; n.TotalGroups += c.TotalGroups; } }

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        AdModule.Use(r.MapGet("/api/adsuite/domains/{id:int}/health", async (int id, ClaimsPrincipal u, ITenantContext t, IDataProtectionProvider dp, IControllerRelay relay, IAccess access, IAuditLog audit, CancellationToken ct) =>
        {
            var (d, fail) = await Gate(id, u, t, dp, access, ct); if (d == null) return fail!;
            try { var res = await Health(relay, d, ct); await audit.WriteAsync("adsuite.health", d.Name, "health check read", null, ct); return Results.Ok(res); }
            catch (DirError e) { return Dir.Explain(e); }
            catch (Exception e) { return Results.Json(new { error = "insight_failed", detail = e.GetType().Name + ": " + e.Message }, statusCode: 500); }
        }), key);

        AdModule.Use(r.MapGet("/api/adsuite/domains/{id:int}/group-policy", async (int id, ClaimsPrincipal u, ITenantContext t, IDataProtectionProvider dp, IControllerRelay relay, IAccess access, CancellationToken ct) =>
        {
            var (d, fail) = await Gate(id, u, t, dp, access, ct); if (d == null) return fail!;
            try
            {
                var res = await Dir.RunAsync(relay, d, new object[]
                {
                    Dir.Search("CN=Policies,CN=System," + d.BaseDn, "(objectClass=groupPolicyContainer)", new[] { "displayName", "name", "versionNumber", "flags", "whenCreated", "whenChanged", "gPCWQLFilter" }, "one", 3000),
                    Dir.Search(d.BaseDn, "(gPLink=*)", new[] { "gPLink", "gPOptions", "name" }, "sub", 5000),
                }, ct, 90);
                return Results.Ok(Policies(Dir.Entries(res[0]).ToList(), Dir.Entries(res[1]).ToList(), d.BaseDn));
            }
            catch (DirError e) { return Dir.Explain(e); }
        }), key);

        AdModule.Use(r.MapGet("/api/adsuite/domains/{id:int}/dns", async (int id, string? zone, string? source, ClaimsPrincipal u, ITenantContext t, IDataProtectionProvider dp, IControllerRelay relay, IAccess access, CancellationToken ct) =>
        {
            var (d, fail) = await Gate(id, u, t, dp, access, ct); if (d == null) return fail!;
            try
            {
                if (!string.IsNullOrEmpty(zone))
                {
                    if (!Regex.IsMatch(zone, @"^[A-Za-z0-9_*][A-Za-z0-9._*\-]{0,254}$") || !Partitions.Contains(source ?? "domain")) return AdModule.Bad("bad_input", "Unknown zone.");
                    var res = await Dir.RunAsync(relay, d, new object[] { Dir.Search($"DC={Dir.EscapeRdn(zone)},{ZoneBase(source ?? "domain", d.BaseDn)}", "(objectClass=dnsNode)", new[] { "name", "dnsRecord", "dNSTombstoned" }, "one", 3001) }, ct, 90);
                    var rows = new List<object>();
                    foreach (var e in Dir.Entries(res[0]))
                    {
                        if (e.S("dNSTombstoned").Equals("TRUE", StringComparison.OrdinalIgnoreCase)) continue;
                        foreach (var v in e.L("dnsRecord")) if (Record(v) is { } rec) rows.Add(new { name = e.S("name") == "@" || e.S("name") == "" ? "@" : e.S("name"), type = rec.Type, data = rec.Data, ttl = rec.Ttl });
                    }
                    return Results.Ok(new { zone, source = source ?? "domain", records = rows.Take(3000), truncated = Dir.Truncated(res[0]) || rows.Count > 3000 });
                }
                var zones = new List<(string Name, string Source, DateTime? Created)>();
                foreach (var p in Partitions)
                {
                    var one = await Try(relay, d, new object[] { Dir.Search(ZoneBase(p, d.BaseDn), "(objectClass=dnsZone)", new[] { "name", "whenCreated", "dNSProperty" }, "one", 1000) }, ct);
                    foreach (var e in All(one, 0)) if (!e.S("name").StartsWith("..") && e.S("name") != "RootDNSServers") zones.Add((e.S("name"), p, Dir.Generalized(e.S("whenCreated"))));
                }
                return Results.Ok(new { zones = zones.OrderBy(z => z.Name, StringComparer.OrdinalIgnoreCase).Select(z => new { name = z.Name, source = z.Source, created = z.Created }).ToList() });
            }
            catch (DirError e) { return Dir.Explain(e); }
        }), key);

        AdModule.Use(r.MapGet("/api/adsuite/domains/{id:int}/map", async (int id, ClaimsPrincipal u, ITenantContext t, IDataProtectionProvider dp, IControllerRelay relay, IAccess access, CancellationToken ct) =>
        {
            var (d, fail) = await Gate(id, u, t, dp, access, ct); if (d == null) return fail!;
            try
            {
                var res = await Dir.RunAsync(relay, d, new object[]
                {
                    Dir.Search(d.BaseDn, "(|(objectClass=organizationalUnit)(&(objectClass=container)(|(cn=Users)(cn=Computers))))", new[] { "name", "description", "gPLink", "objectClass" }, "sub", 3000),
                    Dir.Search(d.BaseDn, "(|(objectClass=user)(objectClass=computer)(objectClass=group))", new[] { "objectClass" }, "sub", 20000),
                    Dir.Search(d.BaseDn, $"(&(objectClass=computer)(userAccountControl:{Uac}:=8192))", new[] { "name", "dNSHostName", "operatingSystem" }, "sub", 100),
                    Dir.Search("CN=System," + d.BaseDn, "(objectClass=trustedDomain)", new[] { "trustPartner", "trustDirection", "trustType", "trustAttributes" }, "one", 100),
                }, ct, 120);
                var root = new Node { Dn = d.BaseDn, Name = d.DnsName != "" ? d.DnsName : d.Name, Kind = "domain" };
                var nodes = new Dictionary<string, Node>(); nodes[Key(d.BaseDn)] = root;
                foreach (var e in Dir.Entries(res[0]).OrderBy(e => Dir.Rdns(e.Dn).Count))
                    nodes[Key(e.Dn)] = new Node { Dn = e.Dn, Name = e.S("name") != "" ? e.S("name") : Dir.RdnValue(e.Dn), Kind = Shape.KindOf(e.L("objectClass")) == "ou" ? "ou" : "container", Description = e.S("description"), Linked = e.S("gPLink").Contains("LDAP://", StringComparison.OrdinalIgnoreCase) };
                foreach (var n in nodes.Values.Where(n => n != root).OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
                    (nodes.TryGetValue(Key(Dir.Parent(n.Dn)), out var p) ? p : root).Children.Add(n);
                var cutObjects = Dir.Truncated(res[1]);
                foreach (var e in Dir.Entries(res[1]))
                {
                    if (!nodes.TryGetValue(Key(Dir.Parent(e.Dn)), out var n)) continue;   // inside a built-in container: not drawn
                    switch (Shape.KindOf(e.L("objectClass"))) { case "computer": n.Computers++; break; case "group": n.Groups++; break; case "user": n.Users++; break; }
                }
                Roll(root);
                object Out(Node n) => new { dn = n.Dn, name = n.Name, kind = n.Kind, description = n.Description, linked = n.Linked, users = n.Users, computers = n.Computers, groups = n.Groups, totalUsers = n.TotalUsers, totalComputers = n.TotalComputers, totalGroups = n.TotalGroups, children = n.Children.Select(Out).ToList() };
                string Dirn(long v) => v switch { 1 => "incoming", 2 => "outgoing", 3 => "two-way", _ => "unknown" };
                var trusts = Dir.Entries(res[3]).Select(e => new { partner = e.S("trustPartner"), direction = Dirn(e.Num("trustDirection")), type = e.Num("trustType") switch { 1 => "Windows NT", 2 => "Active Directory", 3 => "Kerberos realm", _ => "other" }, forest = (e.Num("trustAttributes") & 8) != 0 }).ToList();
                var dcs = Dir.Entries(res[2]).Select(e => new { name = e.S("dNSHostName") != "" ? e.S("dNSHostName") : e.S("name"), os = e.S("operatingSystem"), dn = e.Dn }).OrderBy(x => x.name).ToList();
                return Results.Ok(new { tree = Out(root), dcs, trusts, truncated = cutObjects });
            }
            catch (DirError e) { return Dir.Explain(e); }
        }), key);
    }
}
