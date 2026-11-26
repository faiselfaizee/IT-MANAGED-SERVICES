using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeskAsk.Platform;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

/// <summary>A controller that holds a small directory in memory and answers the same jobs the real one does.</summary>
public sealed class FakeController : IControllerRelay
{
    public sealed class Obj : Dictionary<string, List<string>> { public Obj() : base(StringComparer.OrdinalIgnoreCase) { } }
    public readonly Dictionary<string, Obj> Dir = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Log = new();
    public readonly Dictionary<string, string> Passwords = new(StringComparer.OrdinalIgnoreCase);
    public string LastConnPassword = "";
    public string Pinned = "", UntrustedCert = "";   // a controller whose DC shows a certificate it does not trust
    public bool Down, Refuse, OldAgent;

    const string Base = "DC=acme,DC=test";

    static Obj O(params (string, string[])[] a) { var o = new Obj(); foreach (var (k, v) in a) o[k] = v.ToList(); return o; }
    static (string, string[]) C(string k, params string[] v) => (k, v);
    static readonly string[] UserClass = { "top", "person", "organizationalPerson", "user" };

    public void Reset()
    {
        Dir.Clear(); Log.Clear(); Passwords.Clear(); Down = Refuse = OldAgent = false; UntrustedCert = "";
        Dir[Base] = O(C("objectClass", "top", "domain", "domainDNS"), C("name", "acme"));
        Dir["OU=Staff," + Base] = O(C("objectClass", "top", "organizationalUnit"), C("name", "Staff"));
        Dir["OU=Sales,OU=Staff," + Base] = O(C("objectClass", "top", "organizationalUnit"), C("name", "Sales"));
        Dir["OU=Servers," + Base] = O(C("objectClass", "top", "organizationalUnit"), C("name", "Servers"));
        Dir["CN=Users," + Base] = O(C("objectClass", "top", "container"), C("name", "Users"));
        Dir["CN=Ana Lee,OU=Sales,OU=Staff," + Base] = O(C("objectClass", UserClass), C("cn", "Ana Lee"), C("name", "Ana Lee"), C("sAMAccountName", "ana"), C("userPrincipalName", "ana@acme.test"), C("mail", "ana@acme.test"),
            C("userAccountControl", "512"), C("objectSid", "S-1-5-21-1-2-3-1105"), C("lastLogonTimestamp", Dt(DateTime.UtcNow.AddDays(-2))), C("memberOf", "CN=Sales Team,OU=Sales,OU=Staff," + Base));
        Dir["CN=Bo Kim,OU=Sales,OU=Staff," + Base] = O(C("objectClass", UserClass), C("cn", "Bo Kim"), C("name", "Bo Kim"), C("sAMAccountName", "bo"), C("userPrincipalName", "bo@acme.test"),
            C("userAccountControl", "514"), C("objectSid", "S-1-5-21-1-2-3-1106"), C("lastLogonTimestamp", Dt(DateTime.UtcNow.AddDays(-400))));
        Dir["CN=Cy Ode,OU=Servers," + Base] = O(C("objectClass", UserClass), C("cn", "Cy Ode"), C("name", "Cy Ode"), C("sAMAccountName", "cy"), C("userAccountControl", "512"),
            C("objectSid", "S-1-5-21-1-2-3-1107"), C("msDS-User-Account-Control-Computed", "16"), C("lockoutTime", "133000000000000000"), C("lastLogonTimestamp", Dt(DateTime.UtcNow.AddDays(-200))));
        Dir["CN=Dee Admin,CN=Users," + Base] = O(C("objectClass", UserClass), C("cn", "Dee Admin"), C("name", "Dee Admin"), C("sAMAccountName", "dee"), C("userAccountControl", "66048"), C("adminCount", "1"),
            C("objectSid", "S-1-5-21-1-2-3-1108"));
        Dir["CN=Administrator,CN=Users," + Base] = O(C("objectClass", UserClass), C("cn", "Administrator"), C("name", "Administrator"), C("sAMAccountName", "Administrator"), C("userAccountControl", "512"), C("objectSid", "S-1-5-21-1-2-3-500"));
        Dir["CN=svc-deskask,CN=Users," + Base] = O(C("objectClass", UserClass), C("cn", "svc-deskask"), C("name", "svc-deskask"), C("sAMAccountName", "svc-deskask"), C("userPrincipalName", "svc-deskask@acme.test"),
            C("userAccountControl", "512"), C("objectSid", "S-1-5-21-1-2-3-1200"));
        Dir["CN=Sales Team,OU=Sales,OU=Staff," + Base] = O(C("objectClass", "top", "group"), C("cn", "Sales Team"), C("name", "Sales Team"), C("sAMAccountName", "Sales Team"), C("objectSid", "S-1-5-21-1-2-3-1300"),
            C("member", "CN=Ana Lee,OU=Sales,OU=Staff," + Base));
        Dir["CN=Empty Group,OU=Staff," + Base] = O(C("objectClass", "top", "group"), C("cn", "Empty Group"), C("name", "Empty Group"), C("sAMAccountName", "Empty Group"), C("objectSid", "S-1-5-21-1-2-3-1301"));
        Dir["CN=Domain Admins,CN=Users," + Base] = O(C("objectClass", "top", "group"), C("cn", "Domain Admins"), C("name", "Domain Admins"), C("sAMAccountName", "Domain Admins"), C("objectSid", "S-1-5-21-1-2-3-512"),
            C("member", "CN=Dee Admin,CN=Users," + Base));
        Dir["CN=SRV01,OU=Servers," + Base] = O(C("objectClass", "top", "person", "organizationalPerson", "user", "computer"), C("cn", "SRV01"), C("name", "SRV01"), C("sAMAccountName", "SRV01$"), C("operatingSystem", "Windows Server 2022"),
            C("dNSHostName", "srv01.acme.test"), C("userAccountControl", "4096"), C("objectSid", "S-1-5-21-1-2-3-1400"), C("lastLogonTimestamp", Dt(DateTime.UtcNow.AddDays(-300))));
    }
    static string Dt(DateTime d) => d.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture);

    public Task<IReadOnlyList<ControllerInfo>> ControllersAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ControllerInfo>>(new[] { new ControllerInfo("ctl1", "HQ controller", null, true, "3.7.0") });

    static JsonElement Err(string m, int code = 0, int step = 0) => JsonSerializer.SerializeToElement(new { message = m, code, step });

    bool Under(string dn, string anc) => dn.Equals(anc, StringComparison.OrdinalIgnoreCase) || dn.EndsWith("," + anc, StringComparison.OrdinalIgnoreCase);
    static string Parent(string dn) { var i = dn.IndexOf(','); return i < 0 ? "" : dn[(i + 1)..]; }

    public Task<RelayReply> CallAsync(string controllerId, string kind, object payload, TimeSpan timeout, CancellationToken ct = default)
    {
        if (Down) return Task.FromResult(new RelayReply(false, null, Err("The controller did not answer in time."), true));
        var p = JsonSerializer.SerializeToElement(payload);
        var conn = p.GetProperty("conn"); LastConnPassword = conn.GetProperty("password").GetString() ?? "";
        if (Refuse) return Task.FromResult(new RelayReply(false, null, Err("the domain controller dc01 refused the service account: bad password", 49), false));
        Pinned = conn.TryGetProperty("certSha256", out var pin) ? pin.GetString() ?? "" : "";
        if (UntrustedCert != "" && Pinned != UntrustedCert)
            return Task.FromResult(new RelayReply(false, null, JsonSerializer.SerializeToElement(new { message = "The certificate of dc01:636 was issued by a certificate authority this controller does not trust.", code = -2, step = 0, tls = "untrusted",
                cert = new { subject = "CN=dc01.acme.test", names = new[] { "dc01.acme.test" }, issuer = "CN=Acme Root", notAfter = "2027-01-01T00:00:00Z", sha256 = UntrustedCert } }), false));
        var results = new List<object>(); var step = 0;
        foreach (var op in p.GetProperty("ops").EnumerateArray())
        {
            var name = op.GetProperty("op").GetString()!;
            string dn = op.TryGetProperty("dn", out var dd) ? dd.GetString()! : "";
            int fail = 0; var entries = new List<object>();
            switch (name)
            {
                case "rootdse": entries.Add(new { dn = "", attrs = new Dictionary<string, string[]> { ["defaultNamingContext"] = new[] { Base }, ["dnsHostName"] = new[] { "dc01.acme.test" } } }); break;
                case "search": entries = Search(op); Log.Add("search " + op.GetProperty("base").GetString() + " " + op.GetProperty("filter").GetString()); break;
                case "modify":
                {
                    Log.Add("modify " + dn + " " + op.GetProperty("changes").GetRawText());
                    if (!Dir.TryGetValue(dn, out var o)) { fail = 32; break; }
                    var ignore = op.TryGetProperty("ignore", out var ig) && ig.ValueKind == JsonValueKind.Array ? ig.EnumerateArray().Select(x => x.GetInt32()).ToList() : new List<int>();
                    foreach (var ch in op.GetProperty("changes").EnumerateArray())
                    {
                        var attr = ch.GetProperty("attr").GetString()!; var vals = ch.GetProperty("values").EnumerateArray().Select(x => x.GetString()!).ToList(); var mode = ch.GetProperty("op").GetString();
                        if (mode == "replace") { if (vals.Count == 0) o.Remove(attr); else o[attr] = vals; }
                        else if (mode == "add")
                        {
                            if (!o.TryGetValue(attr, out var cur)) o[attr] = cur = new List<string>();
                            foreach (var v in vals) { if (cur.Contains(v, StringComparer.OrdinalIgnoreCase)) { if (!ignore.Contains(68)) fail = 68; } else cur.Add(v); }
                        }
                        else if (mode == "delete") { if (o.TryGetValue(attr, out var cur)) cur.RemoveAll(v => vals.Contains(v, StringComparer.OrdinalIgnoreCase)); }
                    }
                    break;
                }
                case "add":
                {
                    Log.Add("add " + dn);
                    if (Dir.ContainsKey(dn)) { fail = 68; break; }
                    var o = new Obj(); foreach (var a in op.GetProperty("entry").EnumerateObject()) o[a.Name] = a.Value.EnumerateArray().Select(x => x.GetString()!).ToList();
                    o["name"] = new List<string> { Regex.Replace(dn.Split(',')[0].Split('=')[1], @"\\(.)", "$1") }; o["objectSid"] = new List<string> { "S-1-5-21-1-2-3-" + (2000 + Dir.Count) };
                    Dir[dn] = o; break;
                }
                case "delete":
                    Log.Add("delete " + dn);
                    if (!Dir.ContainsKey(dn)) fail = 32; else if (Dir.Keys.Any(k => !k.Equals(dn, StringComparison.OrdinalIgnoreCase) && Under(k, dn))) fail = 66; else Dir.Remove(dn);
                    break;
                case "rename":
                {
                    var np = op.GetProperty("newParent").GetString()!; var rdn = op.GetProperty("newRdn").GetString()!; Log.Add("rename " + dn + " -> " + rdn + "," + np);
                    if (!Dir.TryGetValue(dn, out var o)) { fail = 32; break; }
                    Dir.Remove(dn); Dir[rdn + "," + np] = o; break;
                }
                case "verify":   // agent 6.17: bind as the person to check their own password
                {
                    Log.Add("verify " + dn);
                    if (OldAgent) break;   // an older controller does not know the step and answers without a verdict
                    var given = op.GetProperty("password").GetString()!;
                    var right = Passwords.TryGetValue(dn, out var known) && known == given;
                    var why = right ? "" : Dir.TryGetValue(dn, out var vo) && vo.TryGetValue("userAccountControl", out var uac) && uac.FirstOrDefault() == "514" ? "disabled" : "wrong";
                    results.Add(new { server = "dc01", valid = right, reason = why }); step++;
                    continue;
                }
                case "setpw":
                    Log.Add("setpw " + dn);
                    if (!conn.GetProperty("tls").GetBoolean()) { fail = 53; break; }
                    Passwords[dn] = op.GetProperty("password").GetString()!; if (op.GetProperty("mustChange").GetBoolean() && Dir.TryGetValue(dn, out var pu)) pu["pwdLastSet"] = new List<string> { "0" };
                    break;
            }
            if (fail != 0) return Task.FromResult(new RelayReply(false, JsonSerializer.SerializeToElement(new { results }), Err("fake ldap result " + fail, fail, step), false));
            results.Add(new { server = "dc01", entries, truncated = false });
            step++;
        }
        return Task.FromResult(new RelayReply(true, JsonSerializer.SerializeToElement(new { results }), null, false));
    }

    List<object> Search(JsonElement op)
    {
        var b = op.GetProperty("base").GetString()!; var scope = op.GetProperty("scope").GetString(); var f = op.GetProperty("filter").GetString()!; var max = op.GetProperty("max").GetInt32();
        IEnumerable<KeyValuePair<string, Obj>> c = Dir;
        c = scope switch { "base" => c.Where(x => x.Key.Equals(b, StringComparison.OrdinalIgnoreCase)), "one" => c.Where(x => Parent(x.Key).Equals(b, StringComparison.OrdinalIgnoreCase)), _ => c.Where(x => Under(x.Key, b)) };
        bool Class(KeyValuePair<string, Obj> x, string k) => x.Value.TryGetValue("objectClass", out var l) && l.Contains(k, StringComparer.OrdinalIgnoreCase);
        if (f.Contains("(objectClass=organizationalUnit)") && f.Contains("(objectClass=container)")) c = c.Where(x => Class(x, "organizationalUnit") || Class(x, "container"));
        else if (f != "(objectClass=*)")
        {
            var user = f.Contains("(objectCategory=person)"); var group = f.Contains("(objectClass=group)"); var comp = f.Contains("(objectClass=computer)") && !user; var ou = f.Contains("(objectClass=organizationalUnit)");
            if (user && group && comp) c = c.Where(x => Class(x, "group") || Class(x, "computer") || Class(x, "organizationalUnit") || (Class(x, "user") && !Class(x, "computer")));
            else if (user) c = c.Where(x => Class(x, "user") && !Class(x, "computer"));
            else if (comp) c = c.Where(x => Class(x, "computer"));
            else if (group) c = c.Where(x => Class(x, "group"));
            else if (ou) c = c.Where(x => Class(x, "organizationalUnit"));
            var m = Regex.Match(f, @"\(cn=\*([^*)]*)\*\)");
            if (m.Success && m.Groups[1].Value != "") { var q = m.Groups[1].Value; c = c.Where(x => x.Value.Any(a => a.Key != "objectClass" && a.Value.Any(v => v.Contains(q, StringComparison.OrdinalIgnoreCase)))); }
            if (f.Contains("(!(userAccountControl:1.2.840.113556.1.4.803:=2))")) c = c.Where(x => (int.Parse(x.Value.GetValueOrDefault("userAccountControl")?.FirstOrDefault() ?? "512") & 2) == 0);
            else if (f.Contains("(userAccountControl:1.2.840.113556.1.4.803:=2)")) c = c.Where(x => (int.Parse(x.Value.GetValueOrDefault("userAccountControl")?.FirstOrDefault() ?? "512") & 2) != 0);
            if (f.Contains(":=65536)")) c = c.Where(x => (int.Parse(x.Value.GetValueOrDefault("userAccountControl")?.FirstOrDefault() ?? "0") & 65536) != 0);
            if (f.Contains("(adminCount=1)")) c = c.Where(x => x.Value.GetValueOrDefault("adminCount")?.FirstOrDefault() == "1");
            if (f.Contains("(!(member=*))")) c = c.Where(x => !x.Value.ContainsKey("member") || x.Value["member"].Count == 0);
            if (f.Contains("(lockoutTime>=1)")) c = c.Where(x => x.Value.ContainsKey("lockoutTime"));
            var sams = Regex.Matches(f, @"\(sAMAccountName=([^)*]+)\)").Select(x => x.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (sams.Count > 0) c = c.Where(x => sams.Contains(x.Value.GetValueOrDefault("sAMAccountName")?.FirstOrDefault() ?? ""));
            var le = Regex.Match(f, @"\(lastLogonTimestamp<=(\d+)\)");
            if (f.Contains("(|(lastLogonTimestamp<="))   // inactive, or never signed in (the fake ignores whenCreated)
                c = c.Where(x => !x.Value.ContainsKey("lastLogonTimestamp") || long.Parse(x.Value["lastLogonTimestamp"][0]) <= long.Parse(le.Groups[1].Value));
            else
            {
                if (le.Success) c = c.Where(x => x.Value.ContainsKey("lastLogonTimestamp") && long.Parse(x.Value["lastLogonTimestamp"][0]) <= long.Parse(le.Groups[1].Value));
                if (f.Contains("(!(lastLogonTimestamp=*))")) c = c.Where(x => !x.Value.ContainsKey("lastLogonTimestamp") && Class(x, "user"));
            }
        }
        return c.Take(max).Select(x => (object)new { dn = x.Key, attrs = x.Value.ToDictionary(a => a.Key, a => a.Value.ToArray()) }).ToList();
    }
}

public class AdManagerTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    const string Base = "DC=acme,DC=test", Sales = "OU=Sales,OU=Staff,DC=acme,DC=test", Ana = "CN=Ana Lee,OU=Sales,OU=Staff,DC=acme,DC=test", Bo = "CN=Bo Kim,OU=Sales,OU=Staff,DC=acme,DC=test",
        Cy = "CN=Cy Ode,OU=Servers,DC=acme,DC=test", Team = "CN=Sales Team,OU=Sales,OU=Staff,DC=acme,DC=test";
    readonly WebApplicationFactory<Program> factory;
    static readonly FakeController Fake = new();

    public AdManagerTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b =>
    {
        TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true");
        b.ConfigureServices(s => { s.RemoveAll<IControllerRelay>(); s.AddSingleton<IControllerRelay>(Fake); });
    });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }

    /// <summary>An administrator, a domain, an engineer (eve) and a viewer; the directory is reset.</summary>
    async Task<(HttpClient Admin, int Dom, HttpClient Eve, string EveId, string Pw)> Start()
    {
        Fake.Reset();
        var admin = await TestSupport.AdminClient(factory);
        await admin.GetAsync("/api/assets/types");
        await Sql("truncate adsuite.domains restart identity cascade; delete from identity.users where \"Email\" like '%@ad.test'");
        var co = (await J(await admin.PostAsync("/api/assets/companies", Json(new { name = "Acme AD Ltd " + Guid.NewGuid().ToString("N")[..6] })))).GetProperty("id").GetInt32();
        var made = await admin.PostAsync("/api/adsuite/domains", Json(new { companyId = co, name = "acme.test", baseDn = Base, servers = "dc01.acme.test\ndc02.acme.test", tls = true, bindDn = "svc-deskask@acme.test", password = "Secret-Pw-1", controllerId = "ctl1" }));
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var dom = (await J(made)).GetProperty("id").GetInt32();
        var eveId = (await J(await admin.PostAsync("/api/platform/users", Json(new { email = "eve@ad.test", name = "Eve Engineer", role = "engineer", password = "Long-Enough-1" })))).GetProperty("id").GetString()!;
        var eve = await TestSupport.ClientFor(factory, "eve@ad.test", "Long-Enough-1");
        return (admin, dom, eve, eveId, "Long-Enough-1");
    }

    static async Task Delegate(HttpClient admin, int dom, string who, string ou, params string[] rights)
    {
        var r = await admin.PostAsync($"/api/adsuite/domains/{dom}/delegations", Json(new { kind = "user", principalId = who, ou, rights }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }
    static Task<HttpResponseMessage> Act(HttpClient c, int dom, object body) => c.PostAsync($"/api/adsuite/domains/{dom}/actions", Json(body));
    static List<string> Names(JsonElement items) => items.EnumerateArray().Select(x => x.GetProperty("sam").GetString()!).OrderBy(x => x).ToList();

    [PgFact]
    public async Task DomainsAreCheckedKeepTheirPasswordSecretAndCanBeTested()
    {
        var (admin, dom, eve, _, _) = await Start();
        var good = new { companyId = 1, name = "x", baseDn = Base, servers = "dc01", tls = true, bindDn = "svc@acme.test", password = "p", controllerId = "ctl1" };
        Assert.Equal(HttpStatusCode.Forbidden, (await eve.PostAsync("/api/adsuite/domains", Json(good))).StatusCode);   // only administrators
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/adsuite/domains", Json(new { good.companyId, name = "", good.baseDn, good.servers, good.tls, good.bindDn, good.password, good.controllerId }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/adsuite/domains", Json(new { good.companyId, good.name, baseDn = "acme.test", good.servers, good.tls, good.bindDn, good.password, good.controllerId }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/adsuite/domains", Json(new { good.companyId, good.name, good.baseDn, servers = "", good.tls, good.bindDn, good.password, good.controllerId }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/adsuite/domains", Json(new { good.companyId, good.name, good.baseDn, good.servers, good.tls, bindDn = "svc", good.password, good.controllerId }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/adsuite/domains", Json(new { good.companyId, good.name, good.baseDn, good.servers, good.tls, good.bindDn, password = "", good.controllerId }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/adsuite/domains", Json(new { good.companyId, good.name, good.baseDn, good.servers, good.tls, good.bindDn, good.password, controllerId = "nope" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/adsuite/domains", Json(new { companyId = 987654, good.name, good.baseDn, good.servers, good.tls, good.bindDn, good.password, good.controllerId }))).StatusCode);

        var list = await admin.GetAsync("/api/adsuite/domains");
        var text = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Secret-Pw-1", text);
        var d = JsonDocument.Parse(text).RootElement[0];
        Assert.Equal("acme.test", d.GetProperty("name").GetString()); Assert.Equal("acme.test", d.GetProperty("dnsName").GetString()); Assert.True(d.GetProperty("controllerOnline").GetBoolean());
        Assert.Equal(2, d.GetProperty("servers").GetString()!.Split('\n').Length);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/adsuite/domains", Json(new { companyId = d.GetProperty("companyId").GetInt32(), name = "again", baseDn = Base, servers = "dc01", tls = true, bindDn = "svc@acme.test", password = "p", controllerId = "ctl1" }))).StatusCode);

        // changing the name keeps the password; the controller gets it only inside the job
        var co = d.GetProperty("companyId").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/adsuite/domains/{dom}", Json(new { companyId = co, name = "acme main", baseDn = Base, servers = "dc01", tls = true, bindDn = "svc-deskask@acme.test", password = "", controllerId = "ctl1" }))).StatusCode);
        var t1 = await J(await admin.PostAsync($"/api/adsuite/domains/{dom}/test", Json(new { })));
        Assert.True(t1.GetProperty("ok").GetBoolean()); Assert.Equal("dc01.acme.test", t1.GetProperty("host").GetString()); Assert.Equal("Secret-Pw-1", Fake.LastConnPassword);
        Fake.Refuse = true;
        var t2 = await J(await admin.PostAsync($"/api/adsuite/domains/{dom}/test", Json(new { })));
        Assert.False(t2.GetProperty("ok").GetBoolean()); Assert.Contains("refused", t2.GetProperty("error").GetString()); Assert.Contains("password", t2.GetProperty("hint").GetString());
        Assert.Contains("refused", (await J(await admin.GetAsync("/api/adsuite/domains")))[0].GetProperty("lastError").GetString());
        Fake.Refuse = false;

        // LDAPS on a certificate the controller does not trust: the reason and the certificate come back, and trusting it makes it work
        Fake.UntrustedCert = new string('a', 64);
        var t3 = await J(await admin.PostAsync($"/api/adsuite/domains/{dom}/test", Json(new { })));
        Assert.False(t3.GetProperty("ok").GetBoolean()); Assert.Equal("untrusted", t3.GetProperty("tlsProblem").GetString());
        Assert.Equal(Fake.UntrustedCert, t3.GetProperty("cert").GetProperty("sha256").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await eve.PostAsync($"/api/adsuite/domains/{dom}/trust", Json(new { sha256 = Fake.UntrustedCert }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/adsuite/domains/{dom}/trust", Json(new { sha256 = "abc" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/adsuite/domains/{dom}/trust", Json(new { sha256 = Fake.UntrustedCert.ToUpperInvariant() }))).StatusCode);
        var t4 = await J(await admin.PostAsync($"/api/adsuite/domains/{dom}/test", Json(new { })));
        Assert.True(t4.GetProperty("ok").GetBoolean()); Assert.Equal(Fake.UntrustedCert, Fake.Pinned); Assert.Equal(Fake.UntrustedCert, t4.GetProperty("trusted").GetString());
        Fake.UntrustedCert = "";
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/adsuite/domains/{dom}/trust", Json(new { sha256 = "" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/adsuite/domains/{dom}", Json(new { companyId = co, name = "acme main", baseDn = Base, servers = "dc01", tls = true, bindDn = "svc-deskask@acme.test", password = "New-Secret-2", controllerId = "ctl1" }))).StatusCode);
        await admin.PostAsync($"/api/adsuite/domains/{dom}/test", Json(new { }));
        Assert.Equal("New-Secret-2", Fake.LastConnPassword);

        // an engineer sees nothing until a right is given
        Assert.Empty((await J(await eve.GetAsync("/api/adsuite/domains"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await eve.GetAsync($"/api/adsuite/domains/{dom}/search")).StatusCode);
        var ctl = await J(await admin.GetAsync("/api/adsuite/controllers"));
        Assert.Equal("HQ controller", ctl[0].GetProperty("name").GetString());
        var audit = await (await admin.GetAsync("/api/platform/audit?take=100")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Secret-Pw-1", audit); Assert.DoesNotContain("New-Secret-2", audit);
        Assert.Contains("adsuite.domain.test", audit);
    }

    [PgFact]
    public async Task DelegationLimitsWhatAPersonSeesAndDoes()
    {
        var (admin, dom, eve, eveId, _) = await Start();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/adsuite/domains/{dom}/delegations", Json(new { kind = "user", principalId = eveId, ou = "OU=Other,DC=elsewhere,DC=com", rights = new[] { "read" } }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/adsuite/domains/{dom}/delegations", Json(new { kind = "user", principalId = eveId, ou = Sales, rights = new[] { "fly" } }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await eve.PostAsync($"/api/adsuite/domains/{dom}/delegations", Json(new { kind = "user", principalId = eveId, ou = Sales, rights = new[] { "read" } }))).StatusCode);
        await Delegate(admin, dom, eveId, Sales, "unlock", "reset", "enable", "modify");   // read is added by itself

        var doms = await J(await eve.GetAsync("/api/adsuite/domains"));
        Assert.Single(doms.EnumerateArray()); Assert.True(doms[0].GetProperty("scoped").GetBoolean()); Assert.Equal("", doms[0].GetProperty("bindDn").GetString());   // no connection details for staff
        var tree = await J(await eve.GetAsync($"/api/adsuite/domains/{dom}/tree"));
        Assert.Equal(new[] { "Sales" }, tree.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await eve.GetAsync($"/api/adsuite/domains/{dom}/tree?base={Uri.EscapeDataString("OU=Servers," + Base)}")).StatusCode);

        var found = await J(await eve.GetAsync($"/api/adsuite/domains/{dom}/search"));
        Assert.Equal(new[] { "ana", "bo" }, Names(found.GetProperty("items")));   // Cy is in Servers
        Assert.Equal(HttpStatusCode.Forbidden, (await eve.GetAsync($"/api/adsuite/domains/{dom}/search?base={Uri.EscapeDataString("OU=Servers," + Base)}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await eve.GetAsync($"/api/adsuite/domains/{dom}/object?dn={Uri.EscapeDataString(Cy)}")).StatusCode);
        var obj = await J(await eve.GetAsync($"/api/adsuite/domains/{dom}/object?dn={Uri.EscapeDataString(Ana)}"));
        Assert.Equal("user", obj.GetProperty("obj").GetProperty("kind").GetString());
        var held = obj.GetProperty("held").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Contains("unlock", held); Assert.DoesNotContain("delete", held);

        // what Eve was given works; what she was not given and what is outside her OU does not
        Assert.Equal(HttpStatusCode.OK, (await Act(eve, dom, new { action = "unlock", dn = Ana })).StatusCode);
        Assert.Contains(Fake.Log, l => l.StartsWith("modify " + Ana) && l.Contains("lockoutTime"));
        Assert.Equal(HttpStatusCode.Forbidden, (await Act(eve, dom, new { action = "unlock", dn = Cy })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Act(eve, dom, new { action = "delete", dn = Ana })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Act(eve, dom, new { action = "create-ou", ou = Sales, name = "Temp" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Act(eve, dom, new { action = "disable", dn = Ana })).StatusCode); Assert.Equal("514", Fake.Dir[Ana]["userAccountControl"][0]);
        Assert.Equal(HttpStatusCode.OK, (await Act(eve, dom, new { action = "enable", dn = Ana })).StatusCode); Assert.Equal("512", Fake.Dir[Ana]["userAccountControl"][0]);
        Assert.Equal(HttpStatusCode.OK, (await Act(eve, dom, new { action = "update", dn = Ana, attrs = new { title = "Team lead", telephoneNumber = "" } })).StatusCode); Assert.Equal("Team lead", Fake.Dir[Ana]["title"][0]);
        Assert.Equal(HttpStatusCode.BadRequest, (await Act(eve, dom, new { action = "update", dn = Ana, attrs = new { userAccountControl = "0" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Act(eve, dom, new { action = "update", dn = Ana, attrs = new { mail = "not an address" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Act(eve, dom, new { action = "frobnicate", dn = Ana })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Act(eve, dom, new { action = "unlock", dn = "CN=Ana,DC=other,DC=com" })).StatusCode);

        // a password: made for her, shown once, never written to the audit log
        var rp = await J(await Act(eve, dom, new { action = "reset-password", dn = Ana }));
        var pw = rp.GetProperty("password").GetString()!; Assert.True(pw.Length >= 12); Assert.Equal(pw, Fake.Passwords[Ana]); Assert.Equal("0", Fake.Dir[Ana]["pwdLastSet"][0]);
        var typed = await J(await Act(eve, dom, new { action = "reset-password", dn = Ana, password = "Typed-By-Eve-9", mustChange = false }));
        Assert.Equal(JsonValueKind.Null, typed.GetProperty("password").ValueKind); Assert.Equal("Typed-By-Eve-9", Fake.Passwords[Ana]);
        var audit = await (await admin.GetAsync("/api/platform/audit?take=200")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(pw, audit); Assert.DoesNotContain("Typed-By-Eve-9", audit); Assert.Contains("adsuite.reset-password", audit);

        // setting a password needs TLS: with it off the directory refuses and the answer says why
        var dd = (await J(await admin.GetAsync("/api/adsuite/domains")))[0];
        await admin.PutAsync($"/api/adsuite/domains/{dom}", Json(new { companyId = dd.GetProperty("companyId").GetInt32(), name = "acme.test", baseDn = Base, servers = "dc01", tls = false, bindDn = "svc-deskask@acme.test", password = "", controllerId = "ctl1" }));
        var notls = await Act(eve, dom, new { action = "reset-password", dn = Ana });
        Assert.Equal((HttpStatusCode)422, notls.StatusCode); Assert.Contains("LDAPS", await notls.Content.ReadAsStringAsync());
    }

    [PgFact]
    public async Task DeletesAndPrivilegedChangesWaitForASecondPerson_AndBuiltInObjectsAreLeftAlone()
    {
        var (admin, dom, eve, eveId, _) = await Start();
        await Delegate(admin, dom, eveId, "", "delete", "members", "enable", "reset", "move", "modify");
        const string Admins = "CN=Domain Admins,CN=Users,DC=acme,DC=test", Dee = "CN=Dee Admin,CN=Users,DC=acme,DC=test";

        // a delete is asked for, not done
        var ask = await Act(eve, dom, new { action = "delete", dn = Bo, reason = "left the company" });
        Assert.Equal(HttpStatusCode.Accepted, ask.StatusCode); Assert.True(Fake.Dir.ContainsKey(Bo));
        var pend = await J(await eve.GetAsync("/api/adsuite/approvals?status=pending"));
        Assert.Single(pend.EnumerateArray()); var ap = pend[0].GetProperty("id").GetInt32(); Assert.False(pend[0].GetProperty("canApprove").GetBoolean()); Assert.True(pend[0].GetProperty("mine").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await eve.PostAsync($"/api/adsuite/approvals/{ap}/approve", Json(new { }))).StatusCode);   // not her own
        Assert.True(Fake.Dir.ContainsKey(Bo));

        // privileged account and privileged group: the same
        var reset = await Act(eve, dom, new { action = "reset-password", dn = Dee, password = "Typed-Secret-77" });
        Assert.Equal(HttpStatusCode.Accepted, reset.StatusCode); Assert.False(Fake.Passwords.ContainsKey(Dee));
        var join = await Act(eve, dom, new { action = "member-add", dn = Admins, members = new[] { Ana } });
        Assert.Equal(HttpStatusCode.Accepted, join.StatusCode); Assert.DoesNotContain(Ana, Fake.Dir[Admins]["member"]);
        Assert.Equal(3, (await J(await admin.GetAsync("/api/adsuite/approvals?status=pending"))).GetArrayLength());
        var stored = await (await admin.GetAsync("/api/adsuite/approvals")).Content.ReadAsStringAsync(); Assert.DoesNotContain("Typed-Secret-77", stored);

        // the administrator agrees to each
        var done = await admin.PostAsync($"/api/adsuite/approvals/{ap}/approve", Json(new { }));
        Assert.Equal(HttpStatusCode.OK, done.StatusCode); Assert.False(Fake.Dir.ContainsKey(Bo));
        var pending = (await J(await admin.GetAsync("/api/adsuite/approvals?status=pending"))).EnumerateArray().ToList();
        var resetId = pending.First(x => x.GetProperty("action").GetString() == "reset-password").GetProperty("id").GetInt32();
        var joinId = pending.First(x => x.GetProperty("action").GetString() == "member-add").GetProperty("id").GetInt32();
        var pr = await J(await admin.PostAsync($"/api/adsuite/approvals/{resetId}/approve", Json(new { })));
        Assert.True(pr.GetProperty("generated").GetBoolean()); Assert.Equal(pr.GetProperty("password").GetString(), Fake.Passwords[Dee]);   // a made password, handed to the approver once
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/adsuite/approvals/{joinId}/approve", Json(new { }))).StatusCode); Assert.Contains(Ana, Fake.Dir[Admins]["member"]);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/adsuite/approvals/{ap}/approve", Json(new { }))).StatusCode);   // already done
        var rej = (await Act(eve, dom, new { action = "delete", dn = Ana })).StatusCode; Assert.Equal(HttpStatusCode.Accepted, rej);
        var last = (await J(await admin.GetAsync("/api/adsuite/approvals?status=pending")))[0].GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/adsuite/approvals/{last}/reject", Json(new { note = "no" }))).StatusCode); Assert.True(Fake.Dir.ContainsKey(Ana));

        // built into the directory, or the account DeskAsk signs in with
        var admn = "CN=Administrator,CN=Users,DC=acme,DC=test"; var svc = "CN=svc-deskask,CN=Users,DC=acme,DC=test";
        Assert.Equal((HttpStatusCode)422, (await Act(admin, dom, new { action = "delete", dn = admn })).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await Act(admin, dom, new { action = "delete", dn = Admins })).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await Act(admin, dom, new { action = "disable", dn = svc })).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await Act(admin, dom, new { action = "reset-password", dn = svc })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Act(admin, dom, new { action = "unlock", dn = Team })).StatusCode);   // a group is not a user
        Assert.Equal(HttpStatusCode.BadRequest, (await Act(admin, dom, new { action = "member-add", dn = Ana, members = new[] { Bo } })).StatusCode);   // members go in groups
    }

    [PgFact]
    public async Task ObjectsAreMovedChangedAndCreated()
    {
        var (admin, dom, _, _, _) = await Start();
        // members: adding twice is fine, removing someone who is not in is fine
        Assert.Equal(HttpStatusCode.OK, (await Act(admin, dom, new { action = "member-add", dn = Team, members = new[] { Bo, Ana } })).StatusCode);
        Assert.Equal(2, Fake.Dir[Team]["member"].Count);
        Assert.Equal(HttpStatusCode.OK, (await Act(admin, dom, new { action = "member-remove", dn = Team, members = new[] { Bo, Cy } })).StatusCode);
        Assert.Single(Fake.Dir[Team]["member"]);
        Assert.Equal(HttpStatusCode.BadRequest, (await Act(admin, dom, new { action = "member-add", dn = Team, members = new[] { "CN=X,DC=other,DC=com" } })).StatusCode);

        var exp = await Act(admin, dom, new { action = "set-expiry", dn = Ana, expires = "2031-03-15" });
        Assert.Equal(HttpStatusCode.OK, exp.StatusCode);
        Assert.Equal(new DateTime(2031, 3, 16, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc().ToString(CultureInfo.InvariantCulture), Fake.Dir[Ana]["accountExpires"][0]);   // at the end of the day
        Assert.Equal(HttpStatusCode.OK, (await Act(admin, dom, new { action = "set-expiry", dn = Ana, expires = "" })).StatusCode); Assert.Equal("0", Fake.Dir[Ana]["accountExpires"][0]);
        Assert.Equal(HttpStatusCode.BadRequest, (await Act(admin, dom, new { action = "set-expiry", dn = Ana, expires = "15/03/2031" })).StatusCode);

        // move: into itself is refused, elsewhere in the domain works and the new place is returned
        Assert.Equal(HttpStatusCode.BadRequest, (await Act(admin, dom, new { action = "move", dn = Sales, newParent = Ana })).StatusCode);
        var mv = await J(await Act(admin, dom, new { action = "move", dn = Ana, newParent = "OU=Servers," + Base }));
        Assert.Equal("CN=Ana Lee,OU=Servers," + Base, mv.GetProperty("dn").GetString()); Assert.True(Fake.Dir.ContainsKey("CN=Ana Lee,OU=Servers," + Base)); Assert.False(Fake.Dir.ContainsKey(Ana));

        // create a user: made switched off, given the password, then switched on; groups joined
        var made = await J(await Act(admin, dom, new { action = "create-user", ou = Sales, first = "Nia", last = "Park, Jr.", sam = "nia.park", email = "nia@acme.test", title = "Analyst", kind = "internal", groups = new[] { Team } }));
        var ndn = made.GetProperty("dn").GetString()!; Assert.Equal("CN=Nia Park\\, Jr.," + Sales, ndn);
        Assert.Equal("512", Fake.Dir[ndn]["userAccountControl"][0]); Assert.Equal("nia.park@acme.test", Fake.Dir[ndn]["userPrincipalName"][0]); Assert.Equal(made.GetProperty("password").GetString(), Fake.Passwords[ndn]);
        Assert.Equal("0", Fake.Dir[ndn]["pwdLastSet"][0]); Assert.Contains(ndn, Fake.Dir[Team]["member"]);
        var svc = await J(await Act(admin, dom, new { action = "create-user", ou = Sales, sam = "svc-backup", kind = "service", password = "Chosen-Pass-123", expires = "" }));
        var sdn = "CN=svc-backup," + Sales; Assert.Equal(JsonValueKind.Null, svc.GetProperty("password").ValueKind); Assert.Equal("66048", Fake.Dir[sdn]["userAccountControl"][0]); Assert.False(Fake.Dir[sdn].ContainsKey("pwdLastSet"));
        var dup = await Act(admin, dom, new { action = "create-user", ou = Sales, first = "Nia", last = "Park, Jr.", sam = "nia2" });
        Assert.Equal((HttpStatusCode)422, dup.StatusCode); Assert.Contains("already exists", await dup.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await Act(admin, dom, new { action = "create-user", ou = Sales, first = "A", last = "B", sam = "bad name!" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Act(admin, dom, new { action = "create-user", ou = "OU=X,DC=other,DC=com", first = "A", last = "B", sam = "ab" })).StatusCode);

        var grp = await J(await Act(admin, dom, new { action = "create-group", ou = Sales, name = "Project X", scope = "universal", type = "security" }));
        Assert.Equal(unchecked((int)0x80000008).ToString(CultureInfo.InvariantCulture), Fake.Dir[grp.GetProperty("dn").GetString()!]["groupType"][0]);
        var ou = await J(await Act(admin, dom, new { action = "create-ou", ou = Base, name = "Contractors" }));
        Assert.Equal("OU=Contractors," + Base, ou.GetProperty("dn").GetString());
        Assert.Equal((HttpStatusCode)422, (await Act(admin, dom, new { action = "delete", dn = Sales })).StatusCode);   // not empty: the directory says so
    }

    [PgFact]
    public async Task ReportsListWhoIsInactiveAndExportSafely()
    {
        var (admin, dom, eve, eveId, _) = await Start();
        var cat = await J(await admin.GetAsync("/api/adsuite/reports")); Assert.True(cat.GetArrayLength() >= 8);
        async Task<List<string>> Run(string key, string extra = "") => Names((await J(await admin.GetAsync($"/api/adsuite/domains/{dom}/report?report={key}{extra}"))).GetProperty("items"));
        Assert.Equal(new[] { "cy" }, await Run("inactive-users", "&days=90"));   // Bo is disabled, Ana signed in lately
        Assert.Equal(new[] { "ana", "cy" }, await Run("inactive-users", "&days=1"));
        Assert.Contains("Administrator", await Run("never-logged-on"));
        Assert.Equal(new[] { "dee" }, await Run("password-never-expires"));
        Assert.Equal(new[] { "dee" }, await Run("privileged-users"));
        Assert.Equal(new[] { "bo" }, await Run("disabled-users"));
        Assert.Equal(new[] { "cy" }, await Run("locked-users"));
        Assert.Equal(new[] { "Empty Group" }, await Run("empty-groups"));
        Assert.Equal(new[] { "SRV01$" }, await Run("stale-computers", "&days=30"));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/api/adsuite/domains/{dom}/report?report=nope")).StatusCode);

        // a cell that starts like a formula is made plain text; the export is audited
        await Act(admin, dom, new { action = "update", dn = Bo, attrs = new { title = "=HYPERLINK(\"http://x\")" } });
        var csv = await admin.GetAsync($"/api/adsuite/domains/{dom}/report?report=disabled-users&format=csv");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        var text = Encoding.UTF8.GetString(await csv.Content.ReadAsByteArrayAsync());
        Assert.Contains("'=HYPERLINK", text); Assert.DoesNotContain(",=HYPERLINK", text); Assert.DoesNotContain("\"=HYPERLINK", text);
        Assert.Contains("adsuite.report.export", await (await admin.GetAsync("/api/platform/audit?take=50")).Content.ReadAsStringAsync());

        // Eve sees reports only of the OU she may read
        await Delegate(admin, dom, eveId, Sales, "read");
        Assert.Equal(new[] { "bo" }, Names((await J(await eve.GetAsync($"/api/adsuite/domains/{dom}/report?report=disabled-users"))).GetProperty("items")));
        Assert.Empty(Names((await J(await eve.GetAsync($"/api/adsuite/domains/{dom}/report?report=locked-users"))).GetProperty("items")));
    }

    [PgFact]
    public async Task NewAccountRequestsAreCheckedThenApprovedBySomeoneElse()
    {
        var (admin, dom, eve, eveId, _) = await Start();
        await Delegate(admin, dom, eveId, Sales, "read");
        object Req(object? over = null) => new { domainId = dom, kind = "internal", first = "Ola", last = "Nord", sam = "ola.nord", email = "ola@acme.test", ou = Sales, title = "Sales rep", groups = new[] { Team }, notes = "starts Monday" };
        Assert.Equal(HttpStatusCode.BadRequest, (await eve.PostAsync("/api/adsuite/requests", Json(new { domainId = dom, kind = "external", first = "Ex", last = "Ternal", sam = "ex.ternal", ou = Sales }))).StatusCode);   // external needs an expiry
        Assert.Equal(HttpStatusCode.BadRequest, (await eve.PostAsync("/api/adsuite/requests", Json(new { domainId = dom, kind = "service", sam = "svc-x", ou = Sales, notes = "x" }))).StatusCode);   // say what it is for
        Assert.Equal(HttpStatusCode.BadRequest, (await eve.PostAsync("/api/adsuite/requests", Json(new { domainId = dom, kind = "internal", first = "A", sam = "bad name", ou = Sales }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await eve.PostAsync("/api/adsuite/requests", Json(new { domainId = dom, kind = "internal", first = "A", sam = "a.b", ou = "OU=Servers," + Base }))).StatusCode);   // not her OU
        var made = await eve.PostAsync("/api/adsuite/requests", Json(Req())); Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var id = (await J(made)).GetProperty("id").GetInt32();
        var ext = await J(await eve.PostAsync("/api/adsuite/requests", Json(new { domainId = dom, kind = "external", first = "Ex", last = "Ternal", sam = "ex.ternal", ou = Sales, expires = "2031-01-31", notes = "audit" })));

        var mine = await J(await eve.GetAsync("/api/adsuite/requests?status=pending"));
        Assert.Equal(2, mine.GetArrayLength()); Assert.False(mine[0].GetProperty("canApprove").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await eve.PostAsync($"/api/adsuite/requests/{id}/approve", Json(new { }))).StatusCode);   // she may not approve
        Assert.Equal(HttpStatusCode.Forbidden, (await eve.PostAsync($"/api/adsuite/requests/{id}/reject", Json(new { }))).StatusCode);
        await Delegate(admin, dom, eveId, Sales, "approve");
        Assert.Equal(HttpStatusCode.Forbidden, (await eve.PostAsync($"/api/adsuite/requests/{id}/approve", Json(new { }))).StatusCode);   // nor her own, even with the right
        Assert.False(Fake.Dir.ContainsKey("CN=Ola Nord," + Sales));

        var ok = await J(await admin.PostAsync($"/api/adsuite/requests/{id}/approve", Json(new { })));
        Assert.Equal("CN=Ola Nord," + Sales, ok.GetProperty("dn").GetString()); Assert.True(ok.GetProperty("generated").GetBoolean());
        Assert.Equal("512", Fake.Dir["CN=Ola Nord," + Sales]["userAccountControl"][0]); Assert.Contains("CN=Ola Nord," + Sales, Fake.Dir[Team]["member"]);
        var done = await J(await admin.GetAsync("/api/adsuite/requests?status=created")); Assert.Equal("CN=Ola Nord," + Sales, done[0].GetProperty("resultDn").GetString());
        Assert.DoesNotContain(ok.GetProperty("password").GetString()!, await (await admin.GetAsync("/api/adsuite/requests")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/adsuite/requests/{id}/approve", Json(new { }))).StatusCode);   // once

        // the directory refuses: it stays waiting with the reason, so it can be tried again
        Fake.Dir["CN=Ex Ternal," + Sales] = new FakeController.Obj();
        var xid = ext.GetProperty("id").GetInt32();
        Assert.Equal((HttpStatusCode)422, (await admin.PostAsync($"/api/adsuite/requests/{xid}/approve", Json(new { }))).StatusCode);
        var wait = (await J(await admin.GetAsync("/api/adsuite/requests?status=pending")))[0]; Assert.Contains("already exists", wait.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/adsuite/requests/{xid}/reject", Json(new { note = "duplicate" }))).StatusCode);
        Assert.Equal("rejected", (await J(await admin.GetAsync("/api/adsuite/requests?status=rejected")))[0].GetProperty("status").GetString());

        // an unreachable controller is reported as such
        Fake.Down = true;
        Assert.Equal((HttpStatusCode)503, (await Act(admin, dom, new { action = "unlock", dn = Ana })).StatusCode);
    }

    [PgFact]
    public async Task AccountsAreCreatedInFullFromTemplatesCheckedInBulkOffboardedAndTidiedByPolicy()
    {
        var (admin, dom, eve, eveId, _) = await Start();
        const string Servers = "OU=Servers," + Base;

        // the overview counts
        var ov = await J(await admin.GetAsync($"/api/adsuite/domains/{dom}/overview"));
        Assert.Equal(1, ov.GetProperty("users").GetProperty("disabled").GetInt32()); Assert.Equal(1, ov.GetProperty("users").GetProperty("locked").GetInt32());
        Assert.Equal(1, ov.GetProperty("computers").GetProperty("stale").GetInt32()); Assert.Equal(1, ov.GetProperty("users").GetProperty("privileged").GetInt32());

        // the form's facts about the domain
        var info = await J(await admin.GetAsync($"/api/adsuite/domains/{dom}/info"));
        Assert.Contains("acme.test", info.GetProperty("upnSuffixes").EnumerateArray().Select(x => x.GetString()));

        // templates: administrators make them; someone without the create right sees none
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/adsuite/domains/{dom}/templates", Json(new { name = "Bad", attrs = new { manager = Ana } }))).StatusCode);
        var tpl = await admin.PostAsync($"/api/adsuite/domains/{dom}/templates", Json(new { name = "Sales staff", ou = Sales, groups = new[] { Team }, attrs = new { department = "Sales", company = "Acme" }, naming = new { display = "{first} {last}", sam = "{first}.{last}" } }));
        Assert.Equal(HttpStatusCode.OK, tpl.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/adsuite/domains/{dom}/templates", Json(new { name = "Sales staff" }))).StatusCode);
        var tl = await J(await admin.GetAsync($"/api/adsuite/domains/{dom}/templates"));
        Assert.Equal("Sales", tl[0].GetProperty("attrs").GetProperty("department").GetString()); Assert.Equal(Team, tl[0].GetProperty("groups")[0].GetString());
        Assert.Equal(0, (await J(await eve.GetAsync($"/api/adsuite/domains/{dom}/templates"))).GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await eve.PostAsync($"/api/adsuite/domains/{dom}/templates", Json(new { name = "Mine" }))).StatusCode);

        // a new account with every field, its groups, and a password that never expires (so it need not be changed at first sign-in)
        Assert.Equal(HttpStatusCode.BadRequest, (await Act(admin, dom, new { action = "create-user", ou = Sales, first = "Nia", last = "Ray", sam = "nia.ray", attrs = new { objectSid = "x" } })).StatusCode);
        var made = await J(await Act(admin, dom, new { action = "create-user", ou = Sales, first = "Nia", last = "Ray", sam = "nia.ray", password = "Typed-Pw-123", pwdNeverExpires = true, mustChange = true,
            groups = new[] { Team }, attrs = new { telephoneNumber = "+91 44 1234", l = "Chennai", homeDirectory = @"\\fs01\home\nia.ray", homeDrive = "H:", department = "Sales" } }));
        var nia = made.GetProperty("dn").GetString()!;
        var n = Fake.Dir[nia];
        Assert.Equal("Chennai", n["l"][0]); Assert.Equal("H:", n["homeDrive"][0]); Assert.Equal("66048", n["userAccountControl"][0]);
        Assert.False(n.ContainsKey("pwdLastSet"));   // "never expires" wins over "must change"
        Assert.Contains(nia, Fake.Dir[Team]["member"]); Assert.Equal("Typed-Pw-123", Fake.Passwords[nia]);
        var obj = await J(await admin.GetAsync($"/api/adsuite/domains/{dom}/object?dn={Uri.EscapeDataString(nia)}"));
        Assert.Equal("Chennai", obj.GetProperty("values").GetProperty("l").GetString()); Assert.Equal("", obj.GetProperty("values").GetProperty("pager").GetString());

        // password flags on their own
        Assert.Equal(HttpStatusCode.OK, (await Act(admin, dom, new { action = "set-flags", dn = Bo, mustChange = true })).StatusCode);
        Assert.Equal("0", Fake.Dir[Bo]["pwdLastSet"][0]);
        Assert.Equal(HttpStatusCode.OK, (await Act(admin, dom, new { action = "set-flags", dn = Bo, pwdNeverExpires = true })).StatusCode);
        Assert.Equal("66050", Fake.Dir[Bo]["userAccountControl"][0]);   // 514 | 65536

        // a bulk file is checked before anything changes
        var chk = await J(await admin.PostAsync($"/api/adsuite/domains/{dom}/bulk/check", Json(new { mode = "create", rows = new object[] {
            new { sam = "ana", ou = Sales, first = "A" }, new { sam = "new.one", ou = Sales, first = "N" }, new { sam = "new.one", ou = Sales, first = "N" },
            new { sam = "bad name!", ou = Sales, first = "B" }, new { sam = "x2", ou = "OU=Else,DC=other,DC=test", first = "X" }, new { sam = "x3", ou = Sales } } })));
        Assert.Equal(1, chk.GetProperty("ok").GetInt32());
        var probs = chk.GetProperty("rows").EnumerateArray().Select(x => string.Join("; ", x.GetProperty("problems").EnumerateArray().Select(p => p.GetString()))).ToList();
        Assert.Contains("already exists", probs[0]); Assert.Equal("", probs[1]); Assert.Contains("twice", probs[2]); Assert.Contains("sign-in name", probs[3]); Assert.Contains("not inside", probs[4]); Assert.Contains("first name", probs[5]);
        var mod = await J(await admin.PostAsync($"/api/adsuite/domains/{dom}/bulk/check", Json(new { mode = "modify", rows = new object[] { new { sam = "bo", attrs = new { title = "Lead" } }, new { sam = "nobody" }, new { sam = "cy", attrs = new { objectSid = "1" } } } })));
        var mr = mod.GetProperty("rows");
        Assert.Equal(Bo, mr[0].GetProperty("dn").GetString()); Assert.True(mr[0].GetProperty("ok").GetBoolean());
        Assert.Contains("no account", mr[1].GetProperty("problems")[0].GetString()); Assert.Contains("cannot be set", mr[2].GetProperty("problems")[0].GetString());

        // offboarding: switched off, a new unknown password, out of every group, a note, then moved
        var off = await J(await Act(admin, dom, new { action = "offboard", dn = Ana, newParent = Servers, note = "last day 15 Nov" }));
        var gone = "CN=Ana Lee," + Servers;
        Assert.Equal(gone, off.GetProperty("dn").GetString());
        Assert.True((int.Parse(Fake.Dir[gone]["userAccountControl"][0]) & 2) != 0);
        Assert.DoesNotContain(Ana, Fake.Dir[Team]["member"]); Assert.True(Fake.Passwords.ContainsKey(Ana));
        Assert.StartsWith("Left ", Fake.Dir[gone]["description"][0]); Assert.Contains("last day 15 Nov", Fake.Dir[gone]["description"][0]);
        // a privileged account waits for a second person; built-in accounts are left alone
        await Delegate(admin, dom, eveId, Base, "read", "enable", "reset", "members");
        Assert.Equal(HttpStatusCode.Accepted, (await Act(eve, dom, new { action = "offboard", dn = "CN=Dee Admin,CN=Users," + Base })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Act(admin, dom, new { action = "offboard", dn = "CN=Administrator,CN=Users," + Base })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Act(eve, dom, new { action = "offboard", dn = Bo, newParent = Servers })).StatusCode);   // no move right

        // automation: a preview first, then a run; privileged, built-in and DeskAsk's own account are never touched
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/adsuite/domains/{dom}/policies", Json(new { name = "Too soon", kind = "disable-inactive-users", days = 10 }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await eve.PostAsync($"/api/adsuite/domains/{dom}/policies", Json(new { name = "Mine", kind = "disable-inactive-users", days = 90 }))).StatusCode);
        var pid = (await J(await admin.PostAsync($"/api/adsuite/domains/{dom}/policies", Json(new { name = "Inactive 90", kind = "disable-inactive-users", days = 90, enabled = false })))).GetProperty("id").GetInt32();
        var pv = await J(await admin.PostAsync($"/api/adsuite/policies/{pid}/preview", Json(new { })));
        var would = pv.GetProperty("would").EnumerateArray().Select(x => x.GetProperty("dn").GetString()).ToList();
        var skipped = pv.GetProperty("skipped").EnumerateArray().ToDictionary(x => x.GetProperty("name").GetString()!, x => x.GetProperty("why").GetString());
        Assert.Contains(Cy, would); Assert.DoesNotContain(Bo, would);   // Bo is already off
        Assert.Equal("privileged", skipped["Dee Admin"]); Assert.Equal("built in", skipped["Administrator"]); Assert.Equal("DeskAsk's service account", skipped["svc-deskask"]);
        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync($"/api/adsuite/policies/{pid}/run", Json(new { }))).StatusCode);
        var tenant = (await factory.Services.GetRequiredService<ITenantCatalog>().ListActiveAsync()).First();
        await factory.Services.GetRequiredService<DeskAsk.Platform.Jobs.JobRunner>().RunDueAsync(tenant, default);
        Assert.True((int.Parse(Fake.Dir[Cy]["userAccountControl"][0]) & 2) != 0); Assert.Contains("automation", Fake.Dir[Cy]["description"][0]);
        Assert.Equal(0, int.Parse(Fake.Dir["CN=Dee Admin,CN=Users," + Base]["userAccountControl"][0]) & 2);
        var pl = await J(await admin.GetAsync($"/api/adsuite/domains/{dom}/policies"));
        Assert.Contains("changed", pl[0].GetProperty("lastResult").GetString());
    }

    [PgFact]
    public async Task HealthPoliciesDnsAndTheMapAreReadFromTheDirectory()
    {
        var (admin, dom, eve, _, _) = await Start();
        const string Gpo = "{31B2F340-016D-11D2-945F-00C04FB984F9}", Old = "{6AC1786C-016F-11D2-945F-00C04FB984F9}";
        FakeController.Obj Make(params (string, string)[] a) { var o = new FakeController.Obj(); foreach (var (k, v) in a) o[k] = new List<string> { v }; return o; }
        Fake.Dir[$"CN={Gpo},CN=Policies,CN=System," + Base] = Make(("objectClass", "groupPolicyContainer"), ("displayName", "Default Domain Policy"), ("versionNumber", "131074"), ("flags", "0"));
        Fake.Dir[$"CN={Old},CN=Policies,CN=System," + Base] = Make(("objectClass", "groupPolicyContainer"), ("displayName", "Old Policy"), ("versionNumber", "0"), ("flags", "3"));
        Fake.Dir[Base]["gPLink"] = new List<string> { $"[LDAP://CN={Gpo},CN=Policies,CN=System,{Base};2]" };
        Fake.Dir["DC=acme.test,CN=MicrosoftDNS,DC=DomainDnsZones," + Base] = Make(("objectClass", "dnsZone"), ("name", "acme.test"));
        var rec = new byte[28]; rec[0] = 4; rec[2] = 1; rec[4] = 5; rec[5] = 240; rec[14] = 0x0E; rec[15] = 0x10; rec[24] = 10; rec[27] = 5;   // an A record, 10.0.0.5, TTL 3600
        Fake.Dir["DC=www,DC=acme.test,CN=MicrosoftDNS,DC=DomainDnsZones," + Base] = Make(("objectClass", "dnsNode"), ("name", "www"), ("dnsRecord", "base64:" + Convert.ToBase64String(rec)));

        // group policy: what exists, where it applies, what is unused
        var pol = await J(await admin.GetAsync($"/api/adsuite/domains/{dom}/policies"));
        Assert.Equal(2, pol.GetProperty("summary").GetProperty("total").GetInt32()); Assert.Equal(1, pol.GetProperty("summary").GetProperty("unlinked").GetInt32());
        var def = pol.GetProperty("gpos").EnumerateArray().First(x => x.GetProperty("name").GetString() == "Default Domain Policy");
        Assert.Equal(2, def.GetProperty("userVersion").GetInt32()); Assert.True(def.GetProperty("links")[0].GetProperty("enforced").GetBoolean()); Assert.Contains("(domain)", def.GetProperty("links")[0].GetProperty("target").GetString());
        var old = pol.GetProperty("gpos").EnumerateArray().First(x => x.GetProperty("name").GetString() == "Old Policy");
        Assert.True(old.GetProperty("unlinked").GetBoolean()); Assert.True(old.GetProperty("empty").GetBoolean()); Assert.True(old.GetProperty("allOff").GetBoolean());

        // dns: the zone, then its record
        var zones = await J(await admin.GetAsync($"/api/adsuite/domains/{dom}/dns"));
        Assert.Equal("acme.test", zones.GetProperty("zones")[0].GetProperty("name").GetString());
        var recs = await J(await admin.GetAsync($"/api/adsuite/domains/{dom}/dns?zone=acme.test&source=domain"));
        var a = recs.GetProperty("records")[0]; Assert.Equal("www", a.GetProperty("name").GetString()); Assert.Equal("A", a.GetProperty("type").GetString()); Assert.Equal("10.0.0.5", a.GetProperty("data").GetString()); Assert.Equal(3600, a.GetProperty("ttl").GetInt64());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/api/adsuite/domains/{dom}/dns?zone=a,b&source=domain")).StatusCode);

        // the map and the health check answer with findings
        var map = await J(await admin.GetAsync($"/api/adsuite/domains/{dom}/map"));
        var kids = map.GetProperty("tree").GetProperty("children").EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToList();
        Assert.Contains("Staff", kids); Assert.Contains("Servers", kids);
        var health = await J(await admin.GetAsync($"/api/adsuite/domains/{dom}/health"));
        var keys = health.GetProperty("findings").EnumerateArray().Select(x => x.GetProperty("key").GetString()).ToList();
        Assert.Contains("dcs", keys); Assert.Contains("stale", keys);

        // these read the whole domain, so a person with no rights on it is refused
        Assert.Equal(HttpStatusCode.Forbidden, (await eve.GetAsync($"/api/adsuite/domains/{dom}/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await eve.GetAsync($"/api/adsuite/domains/{dom}/policies")).StatusCode);
    }
}
