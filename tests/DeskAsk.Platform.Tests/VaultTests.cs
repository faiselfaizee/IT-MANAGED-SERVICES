using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using DeskAsk.Platform.Vault;
using Npgsql;
using Xunit;

/// <summary>The password generator and blob checks on their own.</summary>
public class VaultUnitTests
{
    [Fact]
    public void GeneratedPasswordsMeetWindowsComplexity()
    {
        for (var i = 0; i < 200; i++)
        {
            var p = Rotation.Generate(24);
            Assert.Equal(24, p.Length);
            Assert.Contains(p, char.IsUpper); Assert.Contains(p, char.IsLower); Assert.Contains(p, char.IsDigit); Assert.Contains(p, c => !char.IsLetterOrDigit(c));
            Assert.DoesNotContain(p, c => c is '"' or '\'' or ':' or '\\' or ' ' or '`');
        }
        Assert.Equal(14, Rotation.Generate(4).Length);
        Assert.NotEqual(Rotation.Generate(24), Rotation.Generate(24));
    }

    [Fact]
    public void RolesAndReading()
    {
        Assert.True(ItemsApi.CanRead("view", false)); Assert.False(ItemsApi.CanRead("view", true)); Assert.True(ItemsApi.CanRead("edit", true));
        Assert.False(ItemsApi.CanRead("request", false)); Assert.False(ItemsApi.CanRead(null, false));
    }
}

public class VaultTests : IClassFixture<WebApplicationFactory<Program>>
{
    const string Pw = "Vault-Test-Pass-9";
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    readonly WebApplicationFactory<Program> factory;
    public VaultTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b =>
    {
        TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); b.UseSetting("Platform:Roles", "all"); b.UseSetting("Jobs:PollSeconds", "1");
    });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }
    static async Task<object?> Scalar(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); return await c.ExecuteScalarAsync(); }
    static async Task Until(Func<Task<bool>> ok, int seconds = 30) { for (var i = 0; i < seconds * 4; i++) { if (await ok()) return; await Task.Delay(250); } Assert.Fail("timed out"); }

    // what a browser would send; the server only checks the shape
    static string Enc(string tag) => JsonSerializer.Serialize(new { v = 1, iv = "aXZpdml2aXZpdml2", ct = Convert.ToBase64String(Encoding.UTF8.GetBytes(tag)) });
    static string Wrap(string tag) => JsonSerializer.Serialize(new { v = 1, k = Convert.ToBase64String(Encoding.UTF8.GetBytes(tag)) });
    static object Keys(string who) => new
    {
        publicKey = "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEA" + new string('Q', 480) + who,
        wrappedPrivate = Enc("priv-" + who), wrappedRecovery = Enc("rec-" + who),
        kdf = new { alg = "pbkdf2-sha256", iterations = 600000, salt = "c2FsdHNhbHRzYWx0c2FsdA" },
    };

    [PgFact]
    public async Task VaultsSharingRequestsRotationAndSendLinks()
    {
        var admin = await TestSupport.AdminClient(factory);
        await admin.GetAsync("/api/vault/me"); await admin.GetAsync("/api/assets/types");
        await Sql("truncate vault.keys, vault.vaults, vault.events, vault.sends, vault.breach_cache restart identity cascade");
        await Sql("delete from identity.users where \"Email\" like '%@vt.test'");
        await Sql("truncate assets.events, assets.ifstats, assets.port_watch, assets.metrics, assets.collectors, assets.creds, assets.agent_upd_log, assets.shells, assets.rc_log, assets.jobs, assets.agent_q, assets.agents, assets.enroll_tokens, assets.history, assets.assets, assets.companies restart identity cascade");
        await Sql("delete from access.groups where name = 'Vault engineers VT'");
        await Sql(@"insert into jobs.recurring (name, type, interval_seconds, next_run) values ('vault.tick', 'vault.tick', 60, now() + interval '1 day')
            on conflict (name) do update set next_run = now() + interval '1 day'; delete from jobs.queue where type = 'vault.tick' and status = 'queued'");
        async Task Tick() { await Sql("update jobs.recurring set next_run = now() where name = 'vault.tick'"); await Until(async () => (await Scalar("select next_run > now() + interval '30 seconds' from jobs.recurring where name = 'vault.tick'")) is true); await Until(async () => Convert.ToInt64(await Scalar("select count(*) from jobs.queue where type = 'vault.tick' and status in ('queued','running')")) == 0); }

        async Task<(string Id, HttpClient C)> User(string email, string role)
        {
            var r = await admin.PostAsync("/api/platform/users", Json(new { email, name = email.Split('@')[0], role, password = Pw }));
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);
            return ((await J(r)).GetProperty("id").GetString()!, await TestSupport.ClientFor(factory, email, Pw));
        }
        var (engId, eng) = await User("eng@vt.test", "engineer");
        var (viewId, view) = await User("view@vt.test", "viewer");
        var (_, cust) = await User("cust@vt.test", "customer");
        Assert.Equal(HttpStatusCode.Forbidden, (await cust.GetAsync("/api/vault/me")).StatusCode);

        // set-up: once; a second time only as a deliberate reset
        Assert.False((await J(await admin.GetAsync("/api/vault/me"))).GetProperty("setUp").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/vault/keys", Json(new { publicKey = "short", wrappedPrivate = Enc("p"), wrappedRecovery = Enc("r"), kdf = new { alg = "pbkdf2-sha256", iterations = 1000, salt = "c2FsdHNhbHRzYWx0c2FsdA" } }))).StatusCode);
        foreach (var (c, who) in new[] { (admin, "admin"), (eng, "eng"), (view, "view") }) Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/api/vault/keys", Json(Keys(who)))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/vault/keys", Json(Keys("admin")))).StatusCode);
        var me = await J(await admin.GetAsync("/api/vault/me"));
        Assert.True(me.GetProperty("setUp").GetBoolean()); Assert.Equal(Enc("priv-admin"), me.GetProperty("wrappedPrivate").GetString());
        Assert.Equal(600000, me.GetProperty("kdf").GetProperty("iterations").GetInt32());

        // a customer, an asset with a Windows agent, and the customer's vault
        var acme = (await J(await admin.PostAsync("/api/assets/companies", Json(new { name = "Vault Acme" })))).GetProperty("id").GetInt32();
        var other = (await J(await admin.PostAsync("/api/assets/companies", Json(new { name = "Vault Other" })))).GetProperty("id").GetInt32();
        var etok = (await J(await admin.PostAsync("/api/assets/enrolments", Json(new { companyId = acme })))).GetProperty("token").GetString()!;
        var agent = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var aen = await J(await agent.PostAsync("/agent/v1/enroll", Json(new { token = etok, platform = "windows", version = "3.2.0", inventory = new { schema = 1, hostname = "SRV-SQL", os_family = "windows", os_name = "Windows Server 2022", serial_number = "SN-VT1" } })));
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", aen.GetProperty("key").GetString()); agent.DefaultRequestHeaders.Add("X-Agent-Id", aen.GetProperty("agentId").GetString());
        var srv = long.Parse(aen.GetProperty("assetId").GetString()!);
        var elsewhere = (await J(await admin.PostAsync("/api/assets", Json(new { name = "Other laptop" })))).GetProperty("id").GetInt64();
        await Sql($"update assets.assets set company_id = {other} where id = {elsewhere}");

        Assert.Equal(HttpStatusCode.Forbidden, (await eng.PostAsync("/api/vault/vaults", Json(new { kind = "customer", companyId = acme, wrappedKey = Wrap("k"), wrappedDirKey = Wrap("d") }))).StatusCode); // only admins by default
        var r1 = await admin.PostAsync("/api/vault/vaults", Json(new { kind = "customer", companyId = acme, wrappedKey = Wrap("k1-admin"), wrappedDirKey = Wrap("d1-admin") }));
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var vid = (await J(r1)).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await eng.PostAsync("/api/vault/vaults", Json(new { kind = "personal", wrappedKey = Wrap("pk"), wrappedDirKey = Wrap("pd") }))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await eng.PostAsync("/api/vault/vaults", Json(new { kind = "personal", wrappedKey = Wrap("pk"), wrappedDirKey = Wrap("pd") }))).StatusCode);

        // members: the engineers' group may edit, the viewer may only ask
        var grp = (await J(await admin.PostAsync("/api/platform/access/groups", Json(new { name = "Vault engineers VT" })))).GetProperty("id").GetString()!;
        await admin.PostAsync($"/api/platform/access/groups/{grp}/members", Json(new { kind = "user", id = engId }));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync($"/api/vault/vaults/{vid}/members", Json(new { members = new[] { new { type = "group", id = grp, role = "edit" } } }))).StatusCode); // no manager left
        var adminId = me.GetProperty("settings").GetProperty("userId").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/vault/vaults/{vid}/members", Json(new { members = new object[] {
            new { type = "user", id = adminId, role = "manage" }, new { type = "group", id = grp, role = "edit" }, new { type = "user", id = viewId, role = "request" } } }))).StatusCode);
        var pending = (await J(await admin.GetAsync("/api/vault/pending"))).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, pending.Count);
        Assert.True(pending.Single(p => p.GetProperty("userId").GetString() == engId).GetProperty("full").GetBoolean());
        Assert.False(pending.Single(p => p.GetProperty("userId").GetString() == viewId).GetProperty("full").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await eng.PostAsync($"/api/vault/vaults/{vid}/keys", Json(new { keys = Array.Empty<object>() }))).StatusCode);
        Assert.Equal(2, (await J(await admin.PostAsync($"/api/vault/vaults/{vid}/keys", Json(new { keys = new object[] {
            new { userId = engId, keyVersion = 1, wrappedKey = Wrap("k1-eng"), wrappedDirKey = Wrap("d1-eng") },
            new { userId = viewId, keyVersion = 1, wrappedKey = Wrap("k1-view-should-be-dropped"), wrappedDirKey = Wrap("d1-view") } } })))).GetProperty("given").GetInt32());
        Assert.Empty((await J(await admin.GetAsync("/api/vault/pending"))).GetProperty("items").EnumerateArray());
        JsonElement V(JsonElement list) => list.GetProperty("items").EnumerateArray().Single(v => v.GetProperty("id").GetInt32() == vid);
        var ev = V(await J(await eng.GetAsync("/api/vault/vaults")));
        Assert.Equal("edit", ev.GetProperty("role").GetString()); Assert.Equal(Wrap("k1-eng"), ev.GetProperty("wrappedKey").GetString()); Assert.Equal("Vault Acme", ev.GetProperty("name").GetString());
        var vv = V(await J(await view.GetAsync("/api/vault/vaults")));
        Assert.Equal(JsonValueKind.Null, vv.GetProperty("wrappedKey").ValueKind); Assert.Equal(Wrap("d1-view"), vv.GetProperty("wrappedDirKey").GetString());
        var people = (await J(await admin.GetAsync($"/api/vault/vaults/{vid}/members"))).GetProperty("people").EnumerateArray().ToList();
        Assert.All(people, p => Assert.Equal("has_key", p.GetProperty("state").GetString()));

        // items: linked to the customer's own assets only; edits check the revision and keep the old version
        Assert.Equal(HttpStatusCode.BadRequest, (await eng.PostAsync($"/api/vault/vaults/{vid}/items", Json(new { type = "server", keyVersion = 1, dataEnc = Enc("d"), titleEnc = Enc("t"), assetId = elsewhere }))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await eng.PostAsync($"/api/vault/vaults/{vid}/items", Json(new { type = "server", keyVersion = 2, dataEnc = Enc("d"), titleEnc = Enc("t") }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await eng.PostAsync($"/api/vault/vaults/{vid}/items", Json(new { type = "server", keyVersion = 1, dataEnc = "plain text", titleEnc = Enc("t") }))).StatusCode);
        async Task<long> Item(object o) { var r = await eng.PostAsync($"/api/vault/vaults/{vid}/items", Json(o)); Assert.Equal(HttpStatusCode.OK, r.StatusCode); return (await J(r)).GetProperty("id").GetInt64(); }
        var fw = await Item(new { type = "network", keyVersion = 1, dataEnc = Enc("firewall-v1"), titleEnc = Enc("Firewall"), hasPassword = true });
        var sql = await Item(new { type = "server", keyVersion = 1, dataEnc = Enc("sql-no-password"), titleEnc = Enc("SQL service account"), assetId = srv, requiresApproval = true });
        Assert.Equal(HttpStatusCode.Forbidden, (await view.PostAsync($"/api/vault/vaults/{vid}/items", Json(new { type = "note", keyVersion = 1, dataEnc = Enc("x"), titleEnc = Enc("x") }))).StatusCode);
        var seen = (await J(await view.GetAsync($"/api/vault/items?vault={vid}"))).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, seen.Count); Assert.All(seen, i => Assert.Equal(JsonValueKind.Null, i.GetProperty("dataEnc").ValueKind)); Assert.Equal(Enc("Firewall"), seen[0].GetProperty("titleEnc").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await view.GetAsync($"/api/vault/items/{fw}/history")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await eng.PutAsync($"/api/vault/items/{fw}", Json(new { revision = 0, keyVersion = 1, dataEnc = Enc("firewall-v2"), titleEnc = Enc("Firewall") }))).StatusCode);
        var put = await J(await eng.PutAsync($"/api/vault/items/{fw}", Json(new { revision = 1, keyVersion = 1, dataEnc = Enc("firewall-v2"), titleEnc = Enc("Firewall"), passwordChanged = true })));
        Assert.Equal(2, put.GetProperty("revision").GetInt32());
        Assert.Equal(Enc("firewall-v1"), (await J(await eng.GetAsync($"/api/vault/items/{fw}/history"))).GetProperty("items")[0].GetProperty("dataEnc").GetString());
        Assert.Equal(HttpStatusCode.OK, (await eng.PostAsync($"/api/vault/items/{fw}/events", Json(new { action = "copied", field = "password" }))).StatusCode);
        Assert.Contains((await J(await admin.GetAsync($"/api/vault/items/{fw}/events"))).GetProperty("items").EnumerateArray(), e => e.GetProperty("action").GetString() == "copied" && e.GetProperty("user").GetString() == "eng@vt.test");
        Assert.Single((await J(await eng.GetAsync($"/api/vault/assets/{srv}/items"))).GetProperty("items").EnumerateArray());

        // an access request: the viewer asks, an editor approves by encrypting a copy for the viewer
        Assert.Equal(HttpStatusCode.BadRequest, (await eng.PostAsync($"/api/vault/items/{fw}/requests", Json(new { reason = "need it", minutes = 30 }))).StatusCode); // can read it already
        var rq = await view.PostAsync($"/api/vault/items/{fw}/requests", Json(new { reason = "Firmware upgrade tonight", ticket = "SD-1042", minutes = 30 }));
        Assert.Equal(HttpStatusCode.OK, rq.StatusCode);
        var rid = (await J(rq)).GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.Conflict, (await view.PostAsync($"/api/vault/items/{fw}/requests", Json(new { reason = "again please", minutes = 30 }))).StatusCode);
        var waiting = (await J(await eng.GetAsync("/api/vault/requests?scope=approve"))).GetProperty("items").EnumerateArray().Single();
        Assert.Equal("Firmware upgrade tonight", waiting.GetProperty("reason").GetString()); Assert.EndsWith("view", waiting.GetProperty("publicKey").GetString());
        Assert.Empty((await J(await view.GetAsync("/api/vault/requests?scope=approve"))).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await eng.PostAsync($"/api/vault/requests/{rid}/approve", Json(new { dataEnc = Enc("firewall-for-view"), minutes = 20, note = "Go ahead" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/vault/requests/{rid}/deny", Json(new { }))).StatusCode);
        var grant = (await J(await view.GetAsync("/api/vault/grants"))).GetProperty("items").EnumerateArray().Single();
        Assert.Equal(Enc("firewall-for-view"), grant.GetProperty("dataEnc").GetString()); Assert.Equal(fw, grant.GetProperty("itemId").GetInt64());
        Assert.Equal("approved", (await J(await view.GetAsync("/api/vault/requests"))).GetProperty("items")[0].GetProperty("status").GetString());

        // a managed account: the server keeps the password sealed; the agent changes it
        Assert.Equal(HttpStatusCode.BadRequest, (await eng.PostAsync($"/api/vault/items/{fw}/managed", Json(new { account = "admin", target = "local", password = "x" }))).StatusCode); // no asset
        Assert.Equal(HttpStatusCode.BadRequest, (await eng.PostAsync($"/api/vault/items/{sql}/managed", Json(new { account = "DOMAIN\\svc", target = "local", password = "Old-Pass-1" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await eng.PostAsync($"/api/vault/items/{sql}/managed", Json(new { account = "svc-sql", target = "local", password = "Old-Pass-1", rotateDays = 30, resetAfterUse = false }))).StatusCode);
        Assert.Equal("Old-Pass-1", (await J(await eng.GetAsync($"/api/vault/items/{sql}/secret"))).GetProperty("password").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await view.GetAsync($"/api/vault/items/{sql}/secret")).StatusCode);
        Assert.NotNull(await Scalar("select 1 from core.audit where action = 'vault.managed_password_read' limit 1"));
        Assert.DoesNotContain("Old-Pass-1", (string)(await Scalar("select secret_sealed from vault.managed"))!);
        Assert.Equal("rotating", (await J(await eng.PostAsync($"/api/vault/items/{sql}/rotate", Json(new { })))).GetProperty("state").GetString());
        var msg = (await J(await agent.GetAsync("/agent/v1/poll?v=3.2.0"))).GetProperty("msgs").EnumerateArray().Single(m => m.GetProperty("type").GetString() == "setpw");
        var hid = msg.GetProperty("id").GetString()!;
        Assert.DoesNotContain("password", msg.ToString());
        var handed = await J(await agent.GetAsync($"/agent/v1/secret/{hid}"));
        Assert.Equal("svc-sql", handed.GetProperty("account").GetString()); var newPw = handed.GetProperty("password").GetString()!;
        Assert.Equal(24, newPw.Length);
        Assert.Equal(HttpStatusCode.NotFound, (await agent.GetAsync($"/agent/v1/secret/{hid}")).StatusCode); // once only
        Assert.Equal(0L, Convert.ToInt64(await Scalar("select count(*) from assets.secret_handoff where secret_sealed is not null")));
        Assert.Equal("Old-Pass-1", (await J(await eng.GetAsync($"/api/vault/items/{sql}/secret"))).GetProperty("password").GetString()); // not yet confirmed
        Assert.Equal(HttpStatusCode.OK, (await agent.PostAsync($"/agent/v1/secret/{hid}/result", Json(new { ok = true }))).StatusCode);
        await Tick();
        var managed = (await J(await eng.GetAsync($"/api/vault/items/{sql}/managed")));
        Assert.Equal("ok", managed.GetProperty("account").GetProperty("state").GetString());
        Assert.Equal(newPw, (await J(await eng.GetAsync($"/api/vault/items/{sql}/secret"))).GetProperty("password").GetString());
        Assert.Equal("replaced", managed.GetProperty("history")[0].GetProperty("note").GetString());

        // a failed change keeps the old password and tells the vault's managers
        await Sql("truncate mail.outbox restart identity cascade");
        await eng.PostAsync($"/api/vault/items/{sql}/rotate", Json(new { }));
        var msg2 = (await J(await agent.GetAsync("/agent/v1/poll?v=3.2.0"))).GetProperty("msgs").EnumerateArray().Single(m => m.GetProperty("type").GetString() == "setpw");
        await agent.GetAsync($"/agent/v1/secret/{msg2.GetProperty("id").GetString()}");
        await agent.PostAsync($"/agent/v1/secret/{msg2.GetProperty("id").GetString()}/result", Json(new { ok = false, error = "the new password does not meet the password policy" }));
        await Tick();
        var failed = (await J(await eng.GetAsync("/api/vault/managed"))).GetProperty("items").EnumerateArray().Single();
        Assert.Equal("failed", failed.GetProperty("state").GetString()); Assert.Contains("password policy", failed.GetProperty("lastError").GetString());
        Assert.Equal(newPw, (await J(await eng.GetAsync($"/api/vault/items/{sql}/secret"))).GetProperty("password").GetString());

        // stop managing: the password comes back once for the browser to store encrypted
        Assert.Equal(newPw, (await J(await eng.DeleteAsync($"/api/vault/items/{sql}/managed"))).GetProperty("password").GetString());
        Assert.Equal(0L, Convert.ToInt64(await Scalar("select count(*) from vault.managed")));

        // the viewer leaves: their keys go and the vault asks for new keys; re-encrypting must cover everything
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/vault/vaults/{vid}/members", Json(new { members = new object[] { new { type = "user", id = adminId, role = "manage" }, new { type = "group", id = grp, role = "edit" } } }))).StatusCode);
        Assert.False(V(await J(await admin.GetAsync("/api/vault/vaults"))).GetProperty("needsRotation").GetBoolean()); // the viewer never held the item key
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/vault/vaults/{vid}/members", Json(new { members = new object[] { new { type = "user", id = adminId, role = "manage" } } }))).StatusCode);
        Assert.True(V(await J(await admin.GetAsync("/api/vault/vaults"))).GetProperty("needsRotation").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/vault/vaults/{vid}/rotate", Json(new { keyVersion = 2, items = new[] { new { id = fw, dataEnc = Enc("a"), titleEnc = Enc("b") } }, keys = new[] { new { userId = adminId, wrappedKey = Wrap("k2"), wrappedDirKey = Wrap("d2") } } }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/vault/vaults/{vid}/rotate", Json(new
        {
            keyVersion = 2,
            items = new[] { new { id = fw, dataEnc = Enc("fw-k2"), titleEnc = Enc("Firewall") }, new { id = sql, dataEnc = Enc("sql-k2"), titleEnc = Enc("SQL") } },
            history = new[] { new { itemId = fw, revision = 1, dataEnc = Enc("fw-v1-k2") } },
            keys = new[] { new { userId = adminId, wrappedKey = Wrap("k2-admin"), wrappedDirKey = Wrap("d2-admin") }, new { userId = engId, wrappedKey = Wrap("k2-eng-gone"), wrappedDirKey = Wrap("x") } },
        }))).StatusCode);
        var after = V(await J(await admin.GetAsync("/api/vault/vaults")));
        Assert.Equal(2, after.GetProperty("keyVersion").GetInt32()); Assert.False(after.GetProperty("needsRotation").GetBoolean()); Assert.Equal(Wrap("k2-admin"), after.GetProperty("wrappedKey").GetString());
        Assert.DoesNotContain((await J(await eng.GetAsync("/api/vault/vaults"))).GetProperty("items").EnumerateArray(), v => v.GetProperty("id").GetInt32() == vid);
        Assert.Equal(1L, Convert.ToInt64(await Scalar($"select count(*) from vault.vault_keys where vault_id = {vid}")));

        // a send link: opened once, then gone; the public page needs no sign-in
        var anon = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var sid = (await J(await eng.PostAsync("/api/vault/sends", Json(new { dataEnc = Enc("wifi password"), label = "Wi-Fi for the new starter", hours = 2, maxViews = 1 })))).GetProperty("id").GetString()!;
        Assert.Equal(22, sid.Length);
        Assert.Equal(1, (await J(await anon.GetAsync($"/api/public/vault/sends/{sid}"))).GetProperty("viewsLeft").GetInt32());
        Assert.Equal(1, (await J(await anon.GetAsync($"/api/public/vault/sends/{sid}"))).GetProperty("viewsLeft").GetInt32()); // looking does not use it up
        Assert.Equal(Enc("wifi password"), (await J(await anon.PostAsync($"/api/public/vault/sends/{sid}/open", Json(new { })))).GetProperty("dataEnc").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await anon.PostAsync($"/api/public/vault/sends/{sid}/open", Json(new { }))).StatusCode);
        Assert.Equal(0L, Convert.ToInt64(await Scalar($"select count(*) from vault.sends where id = '{sid}' and data_enc is not null")));
        Assert.False((await J(await eng.GetAsync("/api/vault/sends"))).GetProperty("items")[0].GetProperty("active").GetBoolean());

        // trash: delete, restore, purge
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/vault/items/{fw}")).StatusCode);
        Assert.Single((await J(await admin.GetAsync($"/api/vault/items?vault={vid}&trash=true"))).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/vault/items/{fw}/restore", Json(new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync($"/api/vault/items/{fw}?purge=true")).StatusCode);

        // settings: administrators only
        Assert.Equal(HttpStatusCode.Forbidden, (await eng.PutAsync("/api/vault/settings", Json(new { creators = "engineers" }))).StatusCode);
        Assert.Equal("engineers", (await J(await admin.PutAsync("/api/vault/settings", Json(new { creators = "engineers", autoLockMinutes = 5 })))).GetProperty("creators").GetString());
        Assert.True((await J(await eng.GetAsync("/api/vault/me"))).GetProperty("settings").GetProperty("canCreate").GetBoolean());
        await admin.PutAsync("/api/vault/settings", Json(new { creators = "admins", autoLockMinutes = 15 }));
    }
}
