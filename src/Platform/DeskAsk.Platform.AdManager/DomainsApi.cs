using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace DeskAsk.Platform.AdManager;

/// <summary>Domains (one customer's Active Directory each) and who may do what in them.</summary>
internal static class DomainsApi
{
    static readonly Regex HostRx = new(@"^[A-Za-z0-9.\-:\[\]]{1,200}\z", RegexOptions.Compiled);
    static readonly Regex BaseRx = new(@"^(?i:dc=[^,=]{1,63})(,(?i:dc=[^,=]{1,63})){0,9}\z", RegexOptions.Compiled);

    static List<string> ReadServers(JsonElement body)
    {
        var raw = new List<string>();
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("servers", out var s))
        {
            if (s.ValueKind == JsonValueKind.Array) foreach (var x in s.EnumerateArray()) { if (x.ValueKind == JsonValueKind.String) raw.Add(x.GetString()!); }
            else if (s.ValueKind == JsonValueKind.String) raw.AddRange(s.GetString()!.Split(new[] { '\n', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }
        return raw.Select(x => x.Trim()).Where(x => x != "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        // ---------------------------------------------------------------- domains
        AdModule.Use(r.MapGet("/api/adsuite/domains", async (ClaimsPrincipal u, ITenantContext t, ICompanyDirectory companies, IControllerRelay relay, IAccess access, CancellationToken ct) =>
        {
            await using var db = await AdModule.Open(t, ct);
            var names = (await companies.ListAsync(ct)).ToDictionary(c => c.Id, c => c.Name);
            var ctrls = (await relay.ControllersAsync(ct)).ToDictionary(c => c.Id);
            var admin = AdModule.IsAdmin(u);
            var rows = new List<(int Id, int Company, string Name, string Dns, string BaseDn, string Servers, bool Tls, bool Skip, string BindDn, string Ctrl, bool Enabled, DateTime? Ok, string Err)>();
            await using (var c = new NpgsqlCommand("select id, company_id, name, dns_name, base_dn, servers, tls, skip_verify, bind_dn, controller_id, enabled, last_ok, last_error from adsuite.domains order by name", db))
            await using (var rd = await c.ExecuteReaderAsync(ct))
                while (await rd.ReadAsync(ct)) rows.Add((rd.GetInt32(0), rd.GetInt32(1), rd.GetString(2), rd.GetString(3), rd.GetString(4), rd.GetString(5), rd.GetBoolean(6), rd.GetBoolean(7), rd.GetString(8), rd.GetString(9), rd.GetBoolean(10), rd.IsDBNull(11) ? null : rd.GetDateTime(11), rd.GetString(12)));
            var list = new List<object>();
            foreach (var d in rows)
            {
                var held = new List<string>(); var scoped = false;
                foreach (var right in AdModule.Rights)
                {
                    var sc = await Perms.ScopeAsync(db, access, u, d.Id, right, ct);
                    if (sc == null || sc.Count > 0) { held.Add(right); if (sc != null) scoped = true; }
                }
                if (held.Count == 0) continue;
                ctrls.TryGetValue(d.Ctrl, out var ci);
                list.Add(new
                {
                    id = d.Id, companyId = d.Company, company = names.GetValueOrDefault(d.Company, ""), name = d.Name, dnsName = d.Dns, baseDn = d.BaseDn,
                    servers = admin ? d.Servers : "", tls = d.Tls, skipVerify = d.Skip, bindDn = admin ? d.BindDn : "", controllerId = admin ? d.Ctrl : "",
                    controller = ci?.Name ?? "", controllerOnline = ci?.Online ?? false, enabled = d.Enabled, lastOk = d.Ok, lastError = d.Err, rights = held, scoped
                });
            }
            return Results.Ok(list);
        }), key);

        async Task<IResult> Save(int? id, JsonElement body, ClaimsPrincipal u, ITenantContext t, IDataProtectionProvider dp, ICompanyDirectory companies, IControllerRelay relay, IAuditLog audit, CancellationToken ct)
        {
            var name = (AdModule.Str(body, "name") ?? "").Trim();
            var baseDn = (AdModule.Str(body, "baseDn") ?? "").Trim();
            var bindDn = (AdModule.Str(body, "bindDn") ?? "").Trim();
            var pw = AdModule.Str(body, "password") ?? "";
            var ctrl = (AdModule.Str(body, "controllerId") ?? "").Trim();
            var companyId = AdModule.Int(body, "companyId") ?? 0;
            var servers = ReadServers(body);
            if (name == "" || name.Length > 100) return AdModule.Bad("bad_input", "Give the domain a name.");
            if (!BaseRx.IsMatch(baseDn)) return AdModule.Bad("bad_input", "The base DN looks like DC=contoso,DC=com.");
            if (servers.Count is 0 or > 6 || servers.Any(s => !HostRx.IsMatch(s))) return AdModule.Bad("bad_input", "Give one to six domain controllers (name or address, one per line).");
            if (bindDn == "" || bindDn.Length > 300 || bindDn.Any(char.IsControl) || !(bindDn.Contains('@') || bindDn.Contains('='))) return AdModule.Bad("bad_input", "The service account is a user principal name (svc-deskask@contoso.com) or a full DN.");
            if (id == null && pw == "") return AdModule.Bad("bad_input", "The service account needs a password.");
            if (pw.Length > 256) return AdModule.Bad("bad_input", "That password is too long.");
            if (await companies.GetAsync(companyId, ct) == null) return AdModule.Bad("bad_input", "Choose the customer this domain belongs to.");
            var c0 = (await relay.ControllersAsync(ct)).FirstOrDefault(c => c.Id == ctrl);
            if (c0 == null) return AdModule.Bad("bad_input", "Choose the controller that sits in the customer's network. Install one under Assets > Agents & collectors first.");
            if (c0.CompanyId != null && c0.CompanyId != companyId) return AdModule.Bad("bad_input", "That controller belongs to another customer.");
            var dnsName = Dir.Rdns(baseDn).Select(x => x[(x.IndexOf('=') + 1)..]).Aggregate((a, b) => a + "." + b);
            await using var db = await AdModule.Open(t, ct);
            try
            {
                if (id == null)
                {
                    await using var c = new NpgsqlCommand("insert into adsuite.domains (company_id, name, dns_name, base_dn, servers, tls, skip_verify, bind_dn, secret_sealed, controller_id, enabled) values (@co,@n,@dn,@b,@s,@tls,@sv,@bd,@sec,@ct,@en) returning id", db);
                    Fill(c); AdModule.P(c, "sec", Dir.Seal(dp, t, pw));
                    var nid = (int)(await c.ExecuteScalarAsync(ct))!;
                    await audit.WriteAsync("adsuite.domain.add", name, baseDn, null, ct);
                    return Results.Ok(new { id = nid });
                }
                await using var u2 = new NpgsqlCommand("update adsuite.domains set company_id=@co, name=@n, dns_name=@dn, base_dn=@b, servers=@s, tls=@tls, skip_verify=@sv, bind_dn=@bd, controller_id=@ct, enabled=@en" + (pw != "" ? ", secret_sealed=@sec" : "") + " where id=@i", db);
                Fill(u2); AdModule.P(u2, "i", id); if (pw != "") AdModule.P(u2, "sec", Dir.Seal(dp, t, pw));
                if (await u2.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
                await audit.WriteAsync("adsuite.domain.update", name, pw != "" ? "service account password changed" : "", null, ct);
                return Results.Ok(new { id });
            }
            catch (PostgresException e) when (e.SqlState == "23505") { return Results.Json(new { error = "exists", detail = "This customer already has that domain." }, statusCode: 409); }

            void Fill(NpgsqlCommand c)
            {
                AdModule.P(c, "co", companyId); AdModule.P(c, "n", name); AdModule.P(c, "dn", dnsName); AdModule.P(c, "b", baseDn); AdModule.P(c, "s", string.Join("\n", servers));
                AdModule.P(c, "tls", AdModule.Bool(body, "tls", true)); AdModule.P(c, "sv", AdModule.Bool(body, "skipVerify")); AdModule.P(c, "bd", bindDn); AdModule.P(c, "ct", ctrl);
                AdModule.P(c, "en", AdModule.Bool(body, "enabled", true));
            }
        }
        AdModule.AdminOnly(r.MapPost("/api/adsuite/domains", (JsonElement body, ClaimsPrincipal u, ITenantContext t, IDataProtectionProvider dp, ICompanyDirectory co, IControllerRelay rl, IAuditLog a, CancellationToken ct) => Save(null, body, u, t, dp, co, rl, a, ct)), key);
        AdModule.AdminOnly(r.MapPut("/api/adsuite/domains/{id:int}", (int id, JsonElement body, ClaimsPrincipal u, ITenantContext t, IDataProtectionProvider dp, ICompanyDirectory co, IControllerRelay rl, IAuditLog a, CancellationToken ct) => Save(id, body, u, t, dp, co, rl, a, ct)), key);

        AdModule.AdminOnly(r.MapDelete("/api/adsuite/domains/{id:int}", async (int id, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await AdModule.Open(t, ct);
            await using var c = new NpgsqlCommand("delete from adsuite.domains where id = @i returning name", db); AdModule.P(c, "i", id);
            var name = await c.ExecuteScalarAsync(ct) as string; if (name == null) return Results.NotFound();
            await audit.WriteAsync("adsuite.domain.delete", name, "", null, ct);
            return Results.NoContent();
        }), key);

        // Try the connection: the service account signs in, the base DN is found, and the controller answers.
        AdModule.AdminOnly(r.MapPost("/api/adsuite/domains/{id:int}/test", async (int id, ITenantContext t, IDataProtectionProvider dp, IControllerRelay relay, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await AdModule.Open(t, ct);
            var d = await Dir.LoadAsync(db, dp, t, id, ct); if (d == null) return Results.NotFound();
            string? error = null; string hint = ""; string server = "", host = "", context = "";
            try
            {
                var res = await Dir.RunAsync(relay, d, new object[] { new { op = "rootdse" }, Dir.Search(d.BaseDn, "(objectClass=*)", new[] { "distinguishedName" }, "base", 1) }, ct, 30);
                server = res[0].TryGetProperty("server", out var sv) && sv.ValueKind == JsonValueKind.String ? sv.GetString()! : "";
                var root = Dir.Entries(res[0]).FirstOrDefault();
                host = root?.S("dnsHostName") ?? ""; context = root?.S("defaultNamingContext") ?? "";
            }
            catch (DirError e) { error = e.Message; hint = Dir.Hint(e.Code); if (e.Code == 32) error = "The service account signed in, but the base DN " + d.BaseDn + " does not exist on this domain controller."; }
            await using (var c = new NpgsqlCommand("update adsuite.domains set last_ok = case when @e = '' then now() else last_ok end, last_error = @e where id = @i", db)) { AdModule.P(c, "e", AdModule.Clip(error, 500)); AdModule.P(c, "i", id); await c.ExecuteNonQueryAsync(ct); }
            await audit.WriteAsync("adsuite.domain.test", d.Name, error ?? "ok", null, ct);
            return Results.Ok(new { ok = error == null, error, hint, server, host, namingContext = context, tls = d.Tls });
        }), key);

        AdModule.AdminOnly(r.MapGet("/api/adsuite/controllers", async (int? companyId, IControllerRelay relay, CancellationToken ct) =>
            Results.Ok((await relay.ControllersAsync(ct)).Where(c => companyId == null || c.CompanyId == null || c.CompanyId == companyId).Select(c => new { id = c.Id, name = c.Name, companyId = c.CompanyId, online = c.Online, version = c.Version }))), key);

        // ---------------------------------------------------------------- delegation
        AdModule.AdminOnly(r.MapGet("/api/adsuite/principals", async (IUserDirectory users, IAccess access, CancellationToken ct) =>
            Results.Ok(new
            {
                users = (await users.ListAsync(ct)).Where(x => x.Active && x.Role != "customer").OrderBy(x => x.Name).Select(x => new { id = x.Id, name = x.Name, email = x.Email, role = x.Role }),
                groups = (await access.GroupsAsync(ct)).OrderBy(g => g.Name).Select(g => new { id = g.Id, name = g.Name })
            })), key);

        AdModule.AdminOnly(r.MapGet("/api/adsuite/domains/{id:int}/delegations", async (int id, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await AdModule.Open(t, ct);
            await using var c = new NpgsqlCommand("select id, principal_kind, principal_id, principal_name, ou_dn, rights, created_by, created_at from adsuite.delegations where domain_id = @d order by principal_name, ou_dn", db); AdModule.P(c, "d", id);
            await using var rd = await c.ExecuteReaderAsync(ct);
            var list = new List<object>();
            while (await rd.ReadAsync(ct)) list.Add(new { id = rd.GetInt32(0), kind = rd.GetString(1), principalId = rd.GetGuid(2), name = rd.GetString(3), ou = rd.GetString(4), rights = rd.GetFieldValue<string[]>(5), by = rd.GetString(6), at = rd.GetDateTime(7) });
            return Results.Ok(list);
        }), key);

        AdModule.AdminOnly(r.MapPost("/api/adsuite/domains/{id:int}/delegations", async (int id, JsonElement body, ClaimsPrincipal u, ITenantContext t, IUserDirectory users, IAccess access, IDataProtectionProvider dp, IAuditLog audit, CancellationToken ct) =>
        {
            var kind = AdModule.Str(body, "kind") ?? ""; Guid.TryParse(AdModule.Str(body, "principalId"), out var pid);
            var ou = (AdModule.Str(body, "ou") ?? "").Trim();
            var rights = new List<string>();
            if (body.TryGetProperty("rights", out var rj) && rj.ValueKind == JsonValueKind.Array) foreach (var x in rj.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) rights.Add(x.GetString()!);
            rights = rights.Distinct().ToList();
            if (rights.Count == 0 || rights.Any(x => !AdModule.Rights.Contains(x))) return AdModule.Bad("bad_input", "Choose what this person or group may do.");
            if (!rights.Contains("read")) rights.Insert(0, "read"); // nothing works without seeing the object
            string pname;
            if (kind == "user") { var usr = await users.GetAsync(pid, ct); if (usr == null || usr.Role == "customer") return AdModule.Bad("bad_input", "Choose a member of staff."); pname = usr.Name; }
            else if (kind == "group") { var g = (await access.GroupsAsync(ct)).FirstOrDefault(x => x.Id == pid); if (g.Id == Guid.Empty) return AdModule.Bad("bad_input", "Choose a security group."); pname = g.Name; }
            else return AdModule.Bad("bad_input", "Choose a person or a security group.");
            await using var db = await AdModule.Open(t, ct);
            var d = await Dir.LoadAsync(db, dp, t, id, ct); if (d == null) return Results.NotFound();
            if (ou != "" && (!Dir.ValidDn(ou) || !Dir.IsUnder(ou, d.BaseDn))) return AdModule.Bad("bad_input", "The OU must be inside " + d.BaseDn + ".");
            await using var c = new NpgsqlCommand("insert into adsuite.delegations (domain_id, principal_kind, principal_id, principal_name, ou_dn, rights, created_by) values (@d,@k,@p,@n,@o,@r,@by) returning id", db);
            AdModule.P(c, "d", id); AdModule.P(c, "k", kind); AdModule.P(c, "p", pid); AdModule.P(c, "n", pname); AdModule.P(c, "o", ou); c.Parameters.AddWithValue("r", rights.ToArray()); AdModule.P(c, "by", AdModule.Actor(u));
            var nid = (int)(await c.ExecuteScalarAsync(ct))!;
            await audit.WriteAsync("adsuite.delegation.add", d.Name, $"{pname} may {string.Join(", ", rights)} in {(ou == "" ? "the whole domain" : ou)}", null, ct);
            return Results.Ok(new { id = nid });
        }), key);

        AdModule.AdminOnly(r.MapDelete("/api/adsuite/delegations/{id:int}", async (int id, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await AdModule.Open(t, ct);
            await using var c = new NpgsqlCommand("delete from adsuite.delegations where id = @i returning principal_name, ou_dn, domain_id", db); AdModule.P(c, "i", id);
            await using var rd = await c.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return Results.NotFound();
            var who = rd.GetString(0); var ou = rd.GetString(1);
            await rd.CloseAsync();
            await audit.WriteAsync("adsuite.delegation.remove", who, ou == "" ? "the whole domain" : ou, null, ct);
            return Results.NoContent();
        }), key);
    }
}
