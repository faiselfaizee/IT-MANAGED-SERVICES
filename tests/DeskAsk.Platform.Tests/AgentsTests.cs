using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using DeskAsk.Platform;
using Npgsql;
using Xunit;

public class AgentsTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    readonly WebApplicationFactory<Program> factory;
    public AgentsTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }

    async Task<HttpClient> Admin()
    {
        var api = await TestSupport.AdminClient(factory);
        await api.GetAsync("/api/assets/types");
        await Sql("truncate assets.shells, assets.rc_log, assets.jobs, assets.agent_q, assets.agents, assets.enroll_tokens, assets.amc, assets.rentals, assets.history, assets.assets, assets.companies, assets.types restart identity cascade; alter sequence assets.tag_seq restart");
        return api;
    }

    static object Inv(string host, string serial) => new
    {
        schema = 1, hostname = host, os_family = "windows", os_name = "Windows 11 Pro", make = "Gigabyte", model = "B760M", serial_number = serial,
        nics = new object[] { new { name = "Ethernet", mac = "30-56-0f-62-c2-47", connected = true, ips = new[] { "192.168.0.113" } } },
        installed_apps = new object[] { new { name = "7-Zip", version = "26.05", publisher = "Igor Pavlov" } }
    };

    async Task<(int Company, string Token)> Setup(HttpClient api, bool remote = true)
    {
        var co = (await J(await api.PostAsync("/api/assets/companies", Json(new { name = "Acme", remoteAllowed = remote })))).GetProperty("id").GetInt32();
        var tok = (await J(await api.PostAsync("/api/assets/enrolments", Json(new { companyId = co, label = "Office" })))).GetProperty("token").GetString()!;
        return (co, tok);
    }

    async Task<(string AgentId, string Key, long AssetId, HttpClient Client)> Enroll(string token, string host = "DESKTOP-1", string serial = "SN-1234567")
    {
        var c = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var r = await c.PostAsync("/agent/v1/enroll", Json(new { token, platform = "windows", version = "1.5.0", inventory = Inv(host, serial) }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await J(r);
        var key = j.GetProperty("key").GetString()!; var id = j.GetProperty("agentId").GetString()!;
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key); c.DefaultRequestHeaders.Add("X-Agent-Id", id);
        return (id, key, long.Parse(j.GetProperty("assetId").GetString()!), c);
    }

    [PgFact]
    public async Task EnrolCreatesTheAssetAndReinstallReusesIt()
    {
        var api = await Admin(); var (_, tok) = await Setup(api);
        var a = await Enroll(tok);
        var d = await J(await api.GetAsync("/api/assets/" + a.AssetId + "/device"));
        Assert.Equal("DESKTOP-1", d.GetProperty("system").GetProperty("hostname").GetString());
        Assert.True(d.GetProperty("agent").GetProperty("online").GetBoolean());
        Assert.Equal("Acme", d.GetProperty("company").GetString());
        Assert.Equal("agent", d.GetProperty("agent").GetProperty("source").GetString());

        var again = await Enroll(tok, "DESKTOP-1-RENAMED");   // same serial and MAC: the same asset, the old agent is replaced
        Assert.Equal(a.AssetId, again.AssetId);
        var agents = await J(await api.GetAsync("/api/assets/agents"));
        Assert.Equal(1, agents.GetArrayLength());
        Assert.Equal(again.AgentId, agents[0].GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await a.Client.PostAsync("/agent/v1/checkin", Json(new { version = "1.5.0" }))).StatusCode);

        var tokens = await J(await api.GetAsync("/api/assets/enrolments"));
        Assert.Equal(2, tokens[0].GetProperty("uses").GetInt32());
    }

    [PgFact]
    public async Task BadAndRevokedTokensAreRefused()
    {
        var api = await Admin(); var (_, tok) = await Setup(api);
        var anon = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/agent/v1/enroll", Json(new { token = "dkat_nope", inventory = Inv("X", "SN-9999999") }))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/agent/install.ps1?t=dkat_nope")).StatusCode);
        var ps = await (await anon.GetAsync("/agent/install.ps1?t=" + tok)).Content.ReadAsStringAsync();
        Assert.Contains(tok, ps); Assert.Contains("/agent/download/windows", ps);
        var sh = await (await anon.GetAsync("/agent/install.sh?t=" + tok)).Content.ReadAsStringAsync();
        Assert.Contains("collector", "collector"); Assert.Contains(tok, sh);

        var id = (await J(await api.GetAsync("/api/assets/enrolments")))[0].GetProperty("id").GetInt32();
        var scripts = await J(await api.GetAsync("/api/assets/enrolments/" + id + "/scripts"));
        Assert.Equal(tok, scripts.GetProperty("token").GetString());
        Assert.Equal(HttpStatusCode.OK, (await api.DeleteAsync("/api/assets/enrolments/" + id)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/agent/v1/enroll", Json(new { token = tok, inventory = Inv("X", "SN-9999999") }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.GetAsync("/api/assets/enrolments/" + id + "/scripts")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/agent/v1/poll")).StatusCode);
    }

    [PgFact]
    public async Task CheckinUpdatesInventory()
    {
        var api = await Admin(); var (_, tok) = await Setup(api);
        var a = await Enroll(tok);
        var inv = new { schema = 1, hostname = "DESKTOP-1", os_family = "windows", serial_number = "SN-1234567", installed_apps = new object[] { new { name = "Chrome", version = "156", publisher = "Google" } } };
        var r = await a.Client.PostAsync("/agent/v1/checkin", Json(new { version = "1.5.0", inventory = inv }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await J(r);
        Assert.True(j.GetProperty("ok").GetBoolean()); Assert.True(j.GetProperty("metrics").GetProperty("on").GetBoolean());
        var apps = await J(await api.GetAsync("/api/assets/" + a.AssetId + "/applications"));
        Assert.Equal("Chrome", apps.GetProperty("items")[0].GetProperty("name").GetString());
    }

    [PgFact]
    public async Task CommandLibraryIsSeeded()
    {
        var api = await Admin();
        var lib = await J(await api.GetAsync("/api/assets/commands"));
        Assert.True(lib.GetProperty("count").GetInt32() >= 70);
        var found = await J(await api.GetAsync("/api/assets/commands?q=bitlocker"));
        Assert.Equal("BitLocker status", found.GetProperty("items")[0].GetProperty("title").GetString());
        var made = await api.PostAsync("/api/assets/commands", Json(new { title = "Say hi", body = "echo {{who}}", os = "windows", shell = "cmd", @params = new[] { new { name = "who", label = "Who", @default = "world" } } }));
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/assets/commands", Json(new { title = "Bad", body = "echo {{nope}}", os = "windows", shell = "cmd" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/assets/commands", Json(new { title = "Bad", body = "ls", os = "linux", shell = "cmd" }))).StatusCode);
        var anon = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/assets/commands")).StatusCode);
    }

    async Task<int> CommandId(HttpClient api, string title) => (await J(await api.GetAsync("/api/assets/commands?q=" + Uri.EscapeDataString(title)))).GetProperty("items").EnumerateArray().First(x => x.GetProperty("os").GetString() == "windows").GetProperty("id").GetInt32();

    [PgFact]
    public async Task RunCommandReachesTheAgentAndTheResultComesBack()
    {
        var api = await Admin(); var (_, tok) = await Setup(api);
        var a = await Enroll(tok);
        var uptime = await CommandId(api, "Uptime and last boot");
        var run = await api.PostAsync("/api/assets/" + a.AssetId + "/run", Json(new { commandId = uptime }));
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        var jid = (await J(run)).GetProperty("id").GetString()!;

        var poll = await J(await a.Client.GetAsync("/agent/v1/poll?wait=0"));
        var msg = poll.GetProperty("msgs")[0];
        Assert.Equal("job", msg.GetProperty("type").GetString()); Assert.Equal(jid, msg.GetProperty("id").GetString());
        Assert.Equal("powershell", msg.GetProperty("shell").GetString());
        Assert.Equal(0, (await J(await a.Client.GetAsync("/agent/v1/poll?wait=0"))).GetProperty("msgs").GetArrayLength());   // delivered once
        Assert.Equal("sent", (await J(await api.GetAsync("/api/assets/jobs/" + jid))).GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.OK, (await a.Client.PostAsync("/agent/v1/job", Json(new { id = jid, status = "done", exit = 0, output = "Up since Monday" }))).StatusCode);
        var job = await J(await api.GetAsync("/api/assets/jobs/" + jid));
        Assert.Equal("done", job.GetProperty("status").GetString()); Assert.Equal("Up since Monday", job.GetProperty("output").GetString()); Assert.Equal(0, job.GetProperty("exit").GetInt32());
        Assert.Equal(1, (await J(await api.GetAsync("/api/assets/" + a.AssetId + "/jobs"))).GetArrayLength());
        var log = await J(await api.GetAsync("/api/assets/remote-log"));
        Assert.Equal("Uptime and last boot", log.GetProperty("items")[0].GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await a.Client.PostAsync("/agent/v1/job", Json(new { id = "j-other", status = "done" }))).StatusCode);
    }

    [PgFact]
    public async Task DisruptiveCommandsNeedConfirmationAndParametersAreChecked()
    {
        var api = await Admin(); var (_, tok) = await Setup(api);
        var a = await Enroll(tok);
        var restart = await CommandId(api, "Restart in a few minutes");
        var r = await api.PostAsync("/api/assets/" + a.AssetId + "/run", Json(new { commandId = restart }));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode); Assert.Equal("confirm_needed", (await J(r)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync("/api/assets/" + a.AssetId + "/run", Json(new { commandId = restart, confirm = true }))).StatusCode);

        var ping = await CommandId(api, "Ping a host");
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/assets/" + a.AssetId + "/run", Json(new { commandId = ping, @params = new { host = "8.8.8.8 & del *" } }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync("/api/assets/" + a.AssetId + "/run", Json(new { commandId = ping, @params = new { host = "example.com" } }))).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/assets/" + a.AssetId + "/run", Json(new { shell = "bash", body = "ls" }))).StatusCode);   // a Windows computer does not run bash
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync("/api/assets/" + a.AssetId + "/run", Json(new { shell = "powershell", body = "Get-Date", script = true }))).StatusCode);
    }

    [PgFact]
    public async Task RemoteGuardrailsBlockTheRun()
    {
        var api = await Admin(); var (co, tok) = await Setup(api, remote: false);
        var a = await Enroll(tok);
        var uptime = await CommandId(api, "Uptime and last boot");
        async Task<(HttpStatusCode, string)> Try() { var r = await api.PostAsync("/api/assets/" + a.AssetId + "/run", Json(new { commandId = uptime })); return (r.StatusCode, (await J(r)).GetProperty("error").GetString()!); }

        Assert.Equal((HttpStatusCode.Forbidden, "company_blocked"), await Try());                // the customer has not allowed remote access
        await api.PutAsync("/api/assets/companies/" + co, Json(new { remoteAllowed = true }));
        await api.PostAsync("/api/assets/" + a.AssetId + "/remote", Json(new { on = false }));
        Assert.Equal((HttpStatusCode.Forbidden, "remote_blocked"), await Try());                 // this computer is switched off
        await api.PostAsync("/api/assets/" + a.AssetId + "/remote", Json(new { on = true }));
        await Sql("update assets.assets set last_seen = now() - interval '2 hours'");
        Assert.Equal((HttpStatusCode.BadRequest, "agent_offline"), await Try());                 // offline computers do not take commands
        await Sql("update assets.assets set last_seen = now()");
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync("/api/assets/" + a.AssetId + "/run", Json(new { commandId = uptime }))).StatusCode);

        var tok2 = (await J(await api.PostAsync("/api/assets/enrolments", Json(new { companyId = co, monitorOnly = true })))).GetProperty("token").GetString()!;
        var b = await Enroll(tok2, "KIOSK-1", "SN-7654321");
        Assert.Equal(HttpStatusCode.Forbidden, (await api.PostAsync("/api/assets/" + b.AssetId + "/run", Json(new { commandId = uptime }))).StatusCode);   // installed as monitoring-only
        var noAgent = (await J(await api.PostAsync("/api/assets", Json(new { name = "Printer" })))).GetProperty("id").GetInt64();
        var r2 = await api.PostAsync("/api/assets/" + noAgent + "/run", Json(new { commandId = uptime }));
        Assert.Equal("no_agent", (await J(r2)).GetProperty("error").GetString());
    }

    [PgFact]
    public async Task LongPollWakesWhenSomethingIsQueued()
    {
        var api = await Admin(); var (_, tok) = await Setup(api);
        var a = await Enroll(tok);
        var waiting = a.Client.GetAsync("/agent/v1/poll?wait=20");
        await Task.Delay(500);
        var started = DateTime.UtcNow;
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync("/api/assets/" + a.AssetId + "/refresh", Json(new { }))).StatusCode);
        var res = await waiting;
        Assert.True((DateTime.UtcNow - started).TotalSeconds < 10);
        Assert.Equal("collect", (await J(res)).GetProperty("msgs")[0].GetProperty("type").GetString());
    }

    [PgFact]
    public async Task RemovingAnAgentUnlinksTheAsset()
    {
        var api = await Admin(); var (_, tok) = await Setup(api);
        var a = await Enroll(tok);
        Assert.Equal(HttpStatusCode.OK, (await api.DeleteAsync("/api/assets/agents/" + a.AgentId)).StatusCode);
        Assert.Equal(0, (await J(await api.GetAsync("/api/assets/agents"))).GetArrayLength());
        var d = await J(await api.GetAsync("/api/assets/" + a.AssetId + "/device"));
        Assert.Equal("", d.GetProperty("agent").GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await a.Client.GetAsync("/agent/v1/poll?wait=0")).StatusCode);
    }
}
