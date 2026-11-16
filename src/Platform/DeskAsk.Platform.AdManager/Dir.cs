using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace DeskAsk.Platform.AdManager;

internal sealed record Domain(int Id, int CompanyId, string Name, string DnsName, string BaseDn, string[] Servers, bool Tls, bool SkipVerify, string BindDn, string Password, string ControllerId, bool Enabled, string CertSha256 = "");

/// <summary>The directory (or the controller) refused or failed. Code is the LDAP result code when there is one; Step the position in the job.</summary>
internal sealed class DirError(string message, int code = 0, int step = 0, bool unreachable = false) : Exception(message)
{
    /// <summary>Why LDAPS failed (no_certificate, name_mismatch, untrusted, expired, pin_changed, tls_version), and the certificate shown.</summary>
    public string Tls { get; init; } = "";
    public JsonElement? Cert { get; init; }
    public int Code { get; } = code;
    public int Step { get; } = step;
    public bool Unreachable { get; } = unreachable;
}

/// <summary>An object as the directory returned it, with attribute names matched without regard to case.</summary>
internal sealed class Ent
{
    public string Dn { get; init; } = "";
    public Dictionary<string, List<string>> A { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string S(string n) => A.TryGetValue(n, out var l) && l.Count > 0 ? l[0] : "";
    public List<string> L(string n) => A.TryGetValue(n, out var l) ? l : new();
    /// <summary>Values of an attribute that may come in ranges (member;range=0-1499).</summary>
    public List<string> Ranged(string n) => A.Where(k => k.Key.Equals(n, StringComparison.OrdinalIgnoreCase) || k.Key.StartsWith(n + ";", StringComparison.OrdinalIgnoreCase)).SelectMany(k => k.Value).ToList();
    public long Num(string n, long d = 0) => long.TryParse(S(n), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : d;
}

internal static class Dir
{
    // ---- user account control and times
    public const int UacDisabled = 0x2, UacNormal = 0x200, UacDontExpire = 0x10000, UacLocked = 0x10, UacPwdExpired = 0x800000, UacNoPwdRequired = 0x20;

    public static DateTime? FileTime(long v) => v <= 0 || v >= 2650467743999999999L ? null : DateTime.FromFileTimeUtc(v);
    public static long ToFileTime(DateTime utc) => utc.ToFileTimeUtc();
    public static DateTime? Generalized(string s) =>
        DateTime.TryParseExact(s, "yyyyMMddHHmmss'.0Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;

    // ---- distinguished names
    /// <summary>Splits a DN at commas that are not escaped.</summary>
    public static List<string> Rdns(string dn)
    {
        var list = new List<string>(); var sb = new StringBuilder();
        for (var i = 0; i < dn.Length; i++)
        {
            if (dn[i] == '\\' && i + 1 < dn.Length) { sb.Append(dn[i]).Append(dn[i + 1]); i++; continue; }
            if (dn[i] == ',') { list.Add(sb.ToString().Trim()); sb.Clear(); continue; }
            sb.Append(dn[i]);
        }
        list.Add(sb.ToString().Trim());
        return list;
    }
    static string Norm(string dn) => string.Join(",", Rdns(dn).Select(r => { var k = r.IndexOf('='); return k < 0 ? r.ToLowerInvariant() : r[..k].Trim().ToLowerInvariant() + "=" + r[(k + 1)..].Trim().ToLowerInvariant(); }));
    public static bool Same(string a, string b) => Norm(a) == Norm(b);
    public static bool IsUnder(string dn, string ancestor)
    {
        var d = Norm(dn); var a = Norm(ancestor);
        return d == a || d.EndsWith("," + a, StringComparison.Ordinal);
    }
    public static string Parent(string dn) { var r = Rdns(dn); return r.Count <= 1 ? "" : string.Join(",", r.Skip(1)); }
    public static string RdnValue(string dn)
    {
        var first = Rdns(dn)[0]; var k = first.IndexOf('=');
        var v = k < 0 ? first : first[(k + 1)..];
        return Regex.Replace(v, @"\\(.)", "$1");
    }
    public static bool ValidDn(string? dn) => !string.IsNullOrWhiteSpace(dn) && dn.Length <= 1000 && dn.Contains('=') && !dn.Any(char.IsControl);

    /// <summary>Makes a value safe to use as the value of a name part (RFC 4514).</summary>
    public static string EscapeRdn(string v)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < v.Length; i++)
        {
            var c = v[i];
            if (",+\"\\<>;=".Contains(c) || (i == 0 && (c == '#' || c == ' ')) || (i == v.Length - 1 && c == ' ')) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Makes a value safe to put inside a search filter.</summary>
    public static string Esc(string v)
    {
        var sb = new StringBuilder();
        foreach (var c in v)
        {
            if (c is '*' or '(' or ')' or '\\' or '\0') sb.Append('\\').Append(((int)c).ToString("x2"));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    public static readonly Regex Sam = new(@"^[A-Za-z0-9][A-Za-z0-9._\-$]{0,19}\z", RegexOptions.Compiled);

    /// <summary>A password with every kind of character, from the system's random source.</summary>
    public static string GeneratePassword(int length = 16)
    {
        const string up = "ABCDEFGHJKLMNPQRSTUVWXYZ", lo = "abcdefghijkmnpqrstuvwxyz", dg = "23456789", sy = "!#$%&*+-=?@";
        var all = up + lo + dg + sy;
        var chars = new List<char> { up[RandomNumberGenerator.GetInt32(up.Length)], lo[RandomNumberGenerator.GetInt32(lo.Length)], dg[RandomNumberGenerator.GetInt32(dg.Length)], sy[RandomNumberGenerator.GetInt32(sy.Length)] };
        while (chars.Count < length) chars.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);
        for (var i = chars.Count - 1; i > 0; i--) { var j = RandomNumberGenerator.GetInt32(i + 1); (chars[i], chars[j]) = (chars[j], chars[i]); }
        return new string(chars.ToArray());
    }

    // ---- domains
    static IDataProtector Protector(IDataProtectionProvider dp, ITenantContext t) => dp.CreateProtector("adsuite.bind:" + t.Require().Id);
    public static string Seal(IDataProtectionProvider dp, ITenantContext t, string secret) => Protector(dp, t).Protect(secret);

    const string DomainCols = "id, company_id, name, dns_name, base_dn, servers, tls, skip_verify, bind_dn, secret_sealed, controller_id, enabled, cert_sha256";
    static Domain? ReadDomain(NpgsqlDataReader r, IDataProtectionProvider dp, ITenantContext t)
    {
        string pw;
        try { pw = Protector(dp, t).Unprotect(r.GetString(9)); } catch (Exception) { pw = ""; }
        return new Domain(r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4),
            r.GetString(5).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), r.GetBoolean(6), r.GetBoolean(7), r.GetString(8), pw, r.GetString(10), r.GetBoolean(11), r.GetString(12));
    }
    public static async Task<Domain?> LoadAsync(NpgsqlConnection db, IDataProtectionProvider dp, ITenantContext t, int id, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("select " + DomainCols + " from adsuite.domains where id = @i", db); AdModule.P(c, "i", id);
        await using var r = await c.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadDomain(r, dp, t) : null;
    }

    // ---- talking to the directory through the controller
    static object Conn(Domain d) => new { servers = d.Servers, tls = d.Tls, skipVerify = d.SkipVerify, certSha256 = d.CertSha256, bindDn = d.BindDn, password = d.Password };

    public static object Search(string baseDn, string filter, string[] attrs, string scope = "sub", int max = 500, int pageSize = 500) =>
        new { op = "search", @base = baseDn, scope, filter, attrs, max, pageSize };
    public static object Modify(string dn, params (string Op, string Attr, string[] Values)[] changes) =>
        new { op = "modify", dn, changes = changes.Select(c => new { op = c.Op, attr = c.Attr, values = c.Values }).ToArray() };

    /// <summary>Runs the steps on the customer's controller, in order, on one connection. Throws DirError when the directory refuses a step.</summary>
    public static async Task<List<JsonElement>> RunAsync(IControllerRelay relay, Domain d, IEnumerable<object> ops, CancellationToken ct, int seconds = 40)
    {
        if (d.ControllerId == "") throw new DirError("No controller is set for this domain.", unreachable: true);
        var reply = await relay.CallAsync(d.ControllerId, "directory", new { conn = Conn(d), ops = ops.ToArray() }, TimeSpan.FromSeconds(seconds), ct);
        if (!reply.Ok)
        {
            var msg = "The directory request failed."; var code = 0; var step = 0;
            if (reply.Error is { ValueKind: JsonValueKind.Object } e)
            {
                if (e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) msg = m.GetString()!;
                if (e.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci)) code = ci;
                if (e.TryGetProperty("step", out var s) && s.TryGetInt32(out var si)) step = si;
            }
            string tls = ""; JsonElement? cert = null;
            if (reply.Error is { ValueKind: JsonValueKind.Object } e2)
            {
                if (e2.TryGetProperty("tls", out var tk) && tk.ValueKind == JsonValueKind.String) tls = tk.GetString()!;
                if (e2.TryGetProperty("cert", out var ce) && ce.ValueKind == JsonValueKind.Object) cert = ce.Clone();
            }
            throw new DirError(msg, code, step, reply.TimedOut || msg.Contains("could be reached", StringComparison.Ordinal) || msg.Contains("did not", StringComparison.Ordinal)) { Tls = tls, Cert = cert };
        }
        var list = new List<JsonElement>();
        if (reply.Result is { ValueKind: JsonValueKind.Object } r && r.TryGetProperty("results", out var rs) && rs.ValueKind == JsonValueKind.Array) foreach (var x in rs.EnumerateArray()) list.Add(x);
        return list;
    }

    public static IEnumerable<Ent> Entries(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("entries", out var es) || es.ValueKind != JsonValueKind.Array) yield break;
        foreach (var e in es.EnumerateArray())
        {
            var ent = new Ent { Dn = e.TryGetProperty("dn", out var dn) && dn.ValueKind == JsonValueKind.String ? dn.GetString()! : "" };
            if (e.TryGetProperty("attrs", out var at) && at.ValueKind == JsonValueKind.Object)
                foreach (var p in at.EnumerateObject())
                    ent.A[p.Name] = p.Value.ValueKind == JsonValueKind.Array ? p.Value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList() : new();
            yield return ent;
        }
    }
    public static bool Truncated(JsonElement result) => result.ValueKind == JsonValueKind.Object && result.TryGetProperty("truncated", out var t) && t.ValueKind == JsonValueKind.True;

    /// <summary>What a refusal usually means, in words a technician can act on.</summary>
    public static string Hint(int code) => code switch
    {
        49 => "The service account was refused. Check its name and password under Domains.",
        50 => "The service account is not allowed to do this in the directory. Delegate the right to it in Active Directory (Delegation of Control).",
        53 => "The domain controller would not do this. Setting a password needs LDAPS, or a site controller on Windows with LDAPS turned off; the password must also meet the domain's rules.",
        8 => "This domain controller requires signed or encrypted LDAP. Turn on LDAPS for this domain.",
        -2 => "",
        19 => "The change breaks a rule of the directory (for example a password that is too simple or a name that is not allowed).",
        68 => "An object with that name already exists.",
        32 => "That object no longer exists.",
        66 => "The object has children: remove them first.",
        _ => ""
    };

    public static IResult Explain(DirError e) =>
        Results.Json(new { error = e.Unreachable ? "unreachable" : "directory", detail = e.Message, code = e.Code, hint = Hint(e.Code) }, statusCode: e.Unreachable ? 503 : 422);
}

/// <summary>An object, shaped for lists.</summary>
internal sealed record Obj(string Dn, string Kind, string Name, string Sam, string Upn, string Mail, string Title, string Department, string Description,
    bool Disabled, bool Locked, bool Expired, bool PwdExpired, bool PwdNeverExpires, DateTime? LastLogon, DateTime? Created, DateTime? AccountExpires, string Os, string Dns);

internal static class Shape
{
    public static readonly string[] ListAttrs =
    {
        "objectClass", "cn", "name", "sAMAccountName", "userPrincipalName", "mail", "title", "department", "description", "userAccountControl",
        "msDS-User-Account-Control-Computed", "lockoutTime", "accountExpires", "lastLogonTimestamp", "whenCreated", "displayName", "operatingSystem", "dNSHostName"
    };

    public static string KindOf(IEnumerable<string> classes)
    {
        var c = classes.Select(x => x.ToLowerInvariant()).ToHashSet();
        return c.Contains("computer") ? "computer" : c.Contains("group") ? "group" : c.Contains("organizationalunit") ? "ou" : c.Contains("user") ? "user"
            : c.Contains("container") || c.Contains("builtindomain") ? "container" : c.Contains("domaindns") ? "domain" : "other";
    }

    public static Obj ToObj(Ent e)
    {
        var uac = (int)e.Num("userAccountControl");
        var computed = e.A.ContainsKey("msDS-User-Account-Control-Computed") ? (int)e.Num("msDS-User-Account-Control-Computed") : -1;
        var locked = computed >= 0 ? (computed & Dir.UacLocked) != 0 : e.Num("lockoutTime") > 0;
        var exp = Dir.FileTime(e.Num("accountExpires"));
        var name = e.S("displayName") != "" ? e.S("displayName") : e.S("name") != "" ? e.S("name") : e.S("cn") != "" ? e.S("cn") : e.Dn == "" ? "" : Dir.RdnValue(e.Dn);
        return new Obj(e.Dn, KindOf(e.L("objectClass")), name, e.S("sAMAccountName"), e.S("userPrincipalName"), e.S("mail"), e.S("title"), e.S("department"), e.S("description"),
            (uac & Dir.UacDisabled) != 0, locked, exp != null && exp < DateTime.UtcNow, computed >= 0 && (computed & Dir.UacPwdExpired) != 0, (uac & Dir.UacDontExpire) != 0,
            Dir.FileTime(e.Num("lastLogonTimestamp")), Dir.Generalized(e.S("whenCreated")), exp, e.S("operatingSystem"), e.S("dNSHostName"));
    }
}

/// <summary>Who may do what in which domain: delegations (a person or security group, an OU or the whole domain, a list of rights).</summary>
internal static class Perms
{
    /// <summary>The OUs in which the person holds the right: null means the whole domain, an empty list means nowhere.</summary>
    public static async Task<List<string>?> ScopeAsync(NpgsqlConnection db, IAccess access, ClaimsPrincipal u, int domainId, string right, CancellationToken ct)
    {
        if (AdModule.IsAdmin(u)) return null;
        var me = AdModule.Me(u);
        var groups = me == Guid.Empty ? new List<Guid>() : (await access.GroupsOfAsync(me, ct)).ToList();
        var ous = new List<string>();
        await using var c = new NpgsqlCommand(@"select ou_dn from adsuite.delegations where domain_id = @d and @r = any(rights)
            and ((principal_kind = 'user' and principal_id = @u) or (principal_kind = 'group' and principal_id = any(@g)))", db);
        AdModule.P(c, "d", domainId); AdModule.P(c, "r", right); AdModule.P(c, "u", me); c.Parameters.AddWithValue("g", groups.ToArray());
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) { var ou = r.GetString(0); if (ou == "") return null; ous.Add(ou); }
        return ous;
    }

    public static async Task<bool> CanAsync(NpgsqlConnection db, IAccess access, ClaimsPrincipal u, int domainId, string? dn, string right, CancellationToken ct)
    {
        var scope = await ScopeAsync(db, access, u, domainId, right, ct);
        return scope == null || (dn != null && scope.Any(ou => Dir.IsUnder(dn, ou)));
    }

    /// <summary>Every right the person holds on one object.</summary>
    public static async Task<HashSet<string>> HeldAsync(NpgsqlConnection db, IAccess access, ClaimsPrincipal u, int domainId, string dn, CancellationToken ct)
    {
        var held = new HashSet<string>();
        foreach (var r in AdModule.Rights) if (await CanAsync(db, access, u, domainId, dn, r, ct)) held.Add(r);
        return held;
    }

    /// <summary>The places a search may start: the domain (null) or the OUs the person may read.</summary>
    public static Task<List<string>?> ReadScopeAsync(NpgsqlConnection db, IAccess access, ClaimsPrincipal u, int domainId, CancellationToken ct) => ScopeAsync(db, access, u, domainId, "read", ct);

    /// <summary>Groups whose members can administer the domain: changes to them or to their members need a second person.</summary>
    public static readonly string[] PrivilegedGroups =
    {
        "Domain Admins", "Enterprise Admins", "Schema Admins", "Administrators", "Account Operators", "Backup Operators", "Server Operators", "Print Operators",
        "Group Policy Creator Owners", "DnsAdmins", "Key Admins", "Enterprise Key Admins"
    };
    public static bool IsPrivilegedGroupName(string name) => PrivilegedGroups.Any(g => g.Equals(name, StringComparison.OrdinalIgnoreCase));
}
