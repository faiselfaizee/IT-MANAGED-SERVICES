using System.DirectoryServices.Protocols;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;

namespace DeskAsk.Platform.Identity;

/// <summary>How DeskAsk reaches an on-premise Active Directory (or any LDAP directory with the same attributes).</summary>
public sealed record AdConfig(bool Enabled, string Server, bool UseSsl, bool TrustAnyCertificate, string BaseDn, string BindUser, string BindPassword, string Filter, bool SignIn)
{
    public const string Key = "identity.ad";
    public static readonly AdConfig None = new(false, "", true, false, "", "", "", "", true);
    public bool Ready => Enabled && Server != "" && BaseDn != "" && BindUser != "" && BindPassword != "";

    public static async Task<AdConfig> LoadAsync(ISettings s, IDataProtectionProvider dp, CancellationToken ct)
    {
        var raw = await s.GetAsync(Key, ct);
        if (string.IsNullOrWhiteSpace(raw)) return None;
        try
        {
            var e = JsonDocument.Parse(raw).RootElement;
            string T(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            bool B(string n, bool d) => e.TryGetProperty(n, out var v) ? v.ValueKind == JsonValueKind.True : d;
            var pw = T("bindPassword"); try { pw = pw == "" ? "" : dp.CreateProtector("identity.ad").Unprotect(pw); } catch { pw = ""; }
            return new AdConfig(B("enabled", false), T("server"), B("useSsl", true), B("trustAnyCertificate", false), T("baseDn"), T("bindUser"), pw, T("filter"), B("signIn", true));
        }
        catch (JsonException) { return None; }
    }

    public static Task SaveAsync(ISettings s, IDataProtectionProvider dp, AdConfig c, CancellationToken ct) =>
        s.SetAsync(Key, JsonSerializer.Serialize(new { enabled = c.Enabled, server = c.Server, useSsl = c.UseSsl, trustAnyCertificate = c.TrustAnyCertificate, baseDn = c.BaseDn, bindUser = c.BindUser,
            bindPassword = c.BindPassword == "" ? "" : dp.CreateProtector("identity.ad").Protect(c.BindPassword), filter = c.Filter, signIn = c.SignIn }), ct);
}

public sealed record AdUser(string Guid, string Name, string Email, string Dn, bool Enabled);

/// <summary>The directory, as DeskAsk needs it. Tests replace it with a fake; the real one speaks LDAP.</summary>
public interface ILdapClient
{
    /// <summary>Null when the account can read the directory, otherwise what went wrong.</summary>
    Task<string?> TestAsync(AdConfig c, CancellationToken ct);
    Task<IReadOnlyList<AdUser>> UsersAsync(AdConfig c, CancellationToken ct);
    /// <summary>True when the directory accepts this person's password. The person is found by their object GUID.</summary>
    Task<bool> AuthenticateAsync(AdConfig c, string guid, string password, CancellationToken ct);
}

public sealed class LdapClient : ILdapClient
{
    static LdapConnection Connect(AdConfig c, string user, string password)
    {
        var host = c.Server.Trim(); var port = c.UseSsl ? 636 : 389;
        var colon = host.LastIndexOf(':');
        if (colon > 0 && int.TryParse(host[(colon + 1)..], out var p)) { port = p; host = host[..colon]; }
        var con = new LdapConnection(new LdapDirectoryIdentifier(host, port)) { Timeout = TimeSpan.FromSeconds(15) };
        con.SessionOptions.ProtocolVersion = 3;
        if (c.UseSsl)
        {
            con.SessionOptions.SecureSocketLayer = true;
            if (c.TrustAnyCertificate) con.SessionOptions.VerifyServerCertificate = (_, _) => true;
            con.AuthType = AuthType.Basic;
        }
        else con.AuthType = AuthType.Negotiate;
        con.Credential = new NetworkCredential(user, password);
        con.Bind();
        return con;
    }

    static string Escape(string s) => s.Replace("\\", "\\5c").Replace("*", "\\2a").Replace("(", "\\28").Replace(")", "\\29").Replace("\0", "\\00");
    static string Text(SearchResultEntry e, string a) => e.Attributes.Contains(a) && e.Attributes[a].Count > 0 ? e.Attributes[a][0]?.ToString() ?? "" : "";

    static string Message(Exception e) => e switch
    {
        LdapException { ErrorCode: 49 } => "The directory refused the user name or password.",
        LdapException { ErrorCode: 81 or 91 } => "The directory server could not be reached. Check the address, the port and the firewall.",
        LdapException le => "The directory said: " + le.Message,
        DirectoryOperationException de => "The directory said: " + de.Message,
        _ => e.Message,
    };

    public Task<string?> TestAsync(AdConfig c, CancellationToken ct) => Task.Run<string?>(() =>
    {
        try
        {
            using var con = Connect(c, c.BindUser, c.BindPassword);
            var r = (SearchResponse)con.SendRequest(new SearchRequest(c.BaseDn, "(objectClass=*)", SearchScope.Base, "distinguishedName"));
            return r.Entries.Count == 0 ? "The base DN was not found." : null;
        }
        catch (Exception e) { return Message(e); }
    }, ct);

    public Task<IReadOnlyList<AdUser>> UsersAsync(AdConfig c, CancellationToken ct) => Task.Run<IReadOnlyList<AdUser>>(() =>
    {
        using var con = Connect(c, c.BindUser, c.BindPassword);
        var filter = "(&(objectCategory=person)(objectClass=user)" + (c.Filter.Trim().StartsWith('(') ? c.Filter.Trim() : "") + ")";
        var req = new SearchRequest(c.BaseDn, filter, SearchScope.Subtree, "objectGUID", "displayName", "mail", "userPrincipalName", "distinguishedName", "userAccountControl");
        var page = new PageResultRequestControl(500); req.Controls.Add(page);
        var res = new List<AdUser>();
        while (res.Count < 3000)
        {
            ct.ThrowIfCancellationRequested();
            var resp = (SearchResponse)con.SendRequest(req);
            foreach (SearchResultEntry e in resp.Entries)
            {
                if (!e.Attributes.Contains("objectGUID") || e.Attributes["objectGUID"][0] is not byte[] gb) continue;
                var mail = Text(e, "mail") is { Length: > 0 } m ? m : Text(e, "userPrincipalName");
                var uac = int.TryParse(Text(e, "userAccountControl"), out var u) ? u : 0;
                res.Add(new AdUser(new Guid(gb).ToString(), Text(e, "displayName"), mail.Trim().ToLowerInvariant(), Text(e, "distinguishedName"), (uac & 2) == 0));
            }
            var pc = resp.Controls.OfType<PageResultResponseControl>().FirstOrDefault();
            if (pc == null || pc.Cookie.Length == 0) break;
            page.Cookie = pc.Cookie;
        }
        return res;
    }, ct);

    public Task<bool> AuthenticateAsync(AdConfig c, string guid, string password, CancellationToken ct) => Task.Run(() =>
    {
        if (password.Length == 0 || !Guid.TryParse(guid, out var g)) return false;   // an empty password would be an anonymous bind, which always works
        try
        {
            string dn;
            using (var svc = Connect(c, c.BindUser, c.BindPassword))
            {
                // objectGUID is matched in its binary form
                var hex = string.Concat(g.ToByteArray().Select(b => "\\" + b.ToString("x2")));
                var r = (SearchResponse)svc.SendRequest(new SearchRequest(c.BaseDn, "(objectGUID=" + hex + ")", SearchScope.Subtree, "distinguishedName"));
                if (r.Entries.Count != 1) return false;
                dn = r.Entries[0].DistinguishedName;
            }
            using var con = Connect(c, dn, password);
            return true;
        }
        catch (LdapException) { return false; }
        catch (DirectoryOperationException) { return false; }
    }, ct);
}

public static class AdEndpoints
{
    static string Str(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    static string Rnd() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static async Task<bool> SignInReadyAsync(HttpContext h)
    {
        var c = await AdConfig.LoadAsync(h.RequestServices.GetRequiredService<ISettings>(), h.RequestServices.GetRequiredService<IDataProtectionProvider>(), h.RequestAborted);
        return c.Ready && c.SignIn;
    }

    /// <summary>Checks a person's directory password at sign-in. Only people who came from the directory (their id starts with "ad:") may use it.</summary>
    public static async Task<bool> PasswordOkAsync(HttpContext h, UserAccount user, string password)
    {
        if (user.ExternalId == null || !user.ExternalId.StartsWith("ad:", StringComparison.Ordinal) || password.Length == 0) return false;
        var c = await AdConfig.LoadAsync(h.RequestServices.GetRequiredService<ISettings>(), h.RequestServices.GetRequiredService<IDataProtectionProvider>(), h.RequestAborted);
        if (!c.Ready || !c.SignIn) return false;
        try { return await h.RequestServices.GetRequiredService<ILdapClient>().AuthenticateAsync(c, user.ExternalId[3..], password, h.RequestAborted); }
        catch { return false; }
    }

    public static void Map(IEndpointRouteBuilder r)
    {
        var g = r.MapGroup("/api/platform/ad").RequireAuthorization(p => p.RequireRole("admin"));
        g.MapGet("", async (ISettings st, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            var c = await AdConfig.LoadAsync(st, dp, ct);
            return Results.Ok(new { enabled = c.Enabled, server = c.Server, useSsl = c.UseSsl, trustAnyCertificate = c.TrustAnyCertificate, baseDn = c.BaseDn, bindUser = c.BindUser, hasPassword = c.BindPassword != "", filter = c.Filter, signIn = c.SignIn });
        });
        g.MapPut("", async (JsonElement b, ISettings st, IDataProtectionProvider dp, IAuditLog audit, CancellationToken ct) =>
        {
            var old = await AdConfig.LoadAsync(st, dp, ct);
            bool Has(string n, out JsonElement v) { v = default; return b.ValueKind == JsonValueKind.Object && b.TryGetProperty(n, out v); }
            string T(string n, string cur) => Has(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : cur;
            bool B(string n, bool cur) => Has(n, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.ValueKind == JsonValueKind.True : cur;
            var pw = Has("bindPassword", out var pv) && pv.ValueKind == JsonValueKind.String && pv.GetString() != "" ? pv.GetString()! : old.BindPassword;
            var c = new AdConfig(B("enabled", old.Enabled), T("server", old.Server), B("useSsl", old.UseSsl), B("trustAnyCertificate", old.TrustAnyCertificate), T("baseDn", old.BaseDn), T("bindUser", old.BindUser), pw, T("filter", old.Filter), B("signIn", old.SignIn));
            if (c.Filter != "" && !(c.Filter.StartsWith('(') && c.Filter.EndsWith(')'))) return Results.BadRequest(new { error = "An extra filter is written in LDAP form, for example (memberOf=CN=DeskAsk,OU=Groups,DC=acme,DC=local)." });
            if (c.Enabled && !c.Ready) return Results.BadRequest(new { error = "Enter the server, base DN, user and password before switching it on." });
            await AdConfig.SaveAsync(st, dp, c, ct);
            await audit.WriteAsync("identity.ad_changed", ct: ct);
            return Results.Ok(new { ok = true });
        });
        g.MapPost("/test", async (ISettings st, IDataProtectionProvider dp, ILdapClient ldap, CancellationToken ct) =>
        {
            var c = await AdConfig.LoadAsync(st, dp, ct);
            if (c.Server == "" || c.BaseDn == "" || c.BindUser == "" || c.BindPassword == "") return Results.BadRequest(new { error = "Save the server, base DN, user and password first." });
            var err = await ldap.TestAsync(c, ct);
            return err == null ? Results.Ok(new { ok = true }) : Results.BadRequest(new { error = err });
        });
        g.MapGet("/users", async (ISettings st, IDataProtectionProvider dp, ILdapClient ldap, IdentityDb db, CancellationToken ct) =>
        {
            var c = await AdConfig.LoadAsync(st, dp, ct);
            if (!c.Ready) return Results.BadRequest(new { error = "Set up and switch on the directory first." });
            IReadOnlyList<AdUser> users;
            try { users = await ldap.UsersAsync(c, ct); } catch (Exception e) when (e is LdapException or DirectoryOperationException) { return Results.BadRequest(new { error = "The directory said: " + e.Message }); }
            var have = (await db.Users.AsNoTracking().Select(x => x.Email).ToListAsync(ct)).ToHashSet();
            return Results.Ok(new { items = users.Where(u => u.Email.Contains('@')).Select(u => new { id = u.Guid, name = u.Name, email = u.Email, enabled = u.Enabled, guest = false, exists = have.Contains(u.Email) }) });
        });
        g.MapPost("/import", async (JsonElement b, IServiceProvider sp, IdentityDb db, IPasswordHasher<UserAccount> hasher, IAuditLog audit, IEntitlements ent, CancellationToken ct) =>
        {
            var role = Str(b, "role").Trim().ToLowerInvariant();
            if (!UsersEndpoints.Roles.Contains(role)) return Results.BadRequest(new { error = "Choose a role." });
            if (!b.TryGetProperty("users", out var us) || us.ValueKind != JsonValueKind.Array || us.GetArrayLength() is 0 or > 500) return Results.BadRequest(new { error = "Choose 1 to 500 people." });
            if (await StaffLimit.RefuseImportAsync(ent, db, role, us.EnumerateArray().Select(x => Str(x, "email")), ct) is { } over) return over;
            int created = 0, linked = 0, skipped = 0; var madeUsers = new List<UserAccount>();
            foreach (var u in us.EnumerateArray())
            {
                var email = Str(u, "email").Trim().ToLowerInvariant(); var id = Str(u, "id"); var name = Str(u, "name").Trim();
                if (!email.Contains('@') || email.Length > 320 || !Guid.TryParse(id, out var gid)) { skipped++; continue; }
                var ext = "ad:" + gid;
                var ex = await db.Users.FirstOrDefaultAsync(x => x.Email == email, ct);
                if (ex != null) { if (ex.ExternalId != ext && ex.ExternalId == null) { ex.ExternalId = ext; ex.PasswordHash = hasher.HashPassword(ex, Rnd()); linked++; } else skipped++; continue; }   // the old local password is thrown away: from now on the directory decides
                var acc = new UserAccount { Email = email, Name = name == "" ? email : (name.Length > 200 ? name[..200] : name), Role = role, ExternalId = ext };
                acc.PasswordHash = hasher.HashPassword(acc, Rnd());
                db.Users.Add(acc); created++; madeUsers.Add(acc);
            }
            await db.SaveChangesAsync(ct);
            await SingleCompanyLink.TryAsync(sp, madeUsers, ct);
            await audit.WriteAsync("user.imported", "ad", $"created {created}, linked {linked}, skipped {skipped}, role {role}", ct: ct);
            return Results.Ok(new { created, linked, skipped });
        });
    }
}
