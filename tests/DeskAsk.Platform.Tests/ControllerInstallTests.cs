using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

/// <summary>Installing the agent through a controller: instant wake-up, a live log of every step, and automatic install after a scan.</summary>
public class ControllerInstallTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    readonly WebApplicationFactory<Program> factory;
    public ControllerInstallTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }

    async Task<(HttpClient Api, HttpClient Col, string Cid, int Company)> Start()
    {
        var api = await TestSupport.AdminClient(factory);
        await api.GetAsync("/api/assets/types");
        await Sql("truncate assets.collectors, assets.creds, assets.shells, assets.rc_log, assets.jobs, assets.agent_q, assets.agents, assets.enroll_tokens, assets.amc, assets.rentals, assets.history, assets.assets, assets.companies, assets.types restart identity cascade");
        var co = (await J(await api.PostAsync("/api/assets/companies", Json(new { name = "Acme Ltd" })))).GetProperty("id").GetInt32();
        var tok = (await J(await api.PostAsync("/api/assets/collectors/tokens", Json(new { companyId = co, label = "HQ" })))).GetProperty("token").GetString()!;
        var anon = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var en = await J(await anon.PostAsync("/collector/v1/enroll", Json(new { token = tok, name = "HQ controller", hostname = "COL-1", platform = "windows", version = "2.9.0" })));
        var cid = en.GetProperty("collectorId").GetString()!;
        var col = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        col.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", en.GetProperty("key").GetString()); col.DefaultRequestHeaders.Add("X-Collector-Id", cid);
        return (api, col, cid, co);
    }

    static string Texts(JsonElement log) => string.Join("\n", log.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("level").GetString() + ": " + x.GetProperty("text").GetString()));

    [PgFact]
    public async Task AnInstallWakesTheControllerKeepsALiveLogAndClosesWhenTheAgentEnrols()
    {
        var (api, col, cid, co) = await Start();
        await api.PatchAsync($"/api/assets/collectors/{cid}", Json(new { scanCidrs = "192.168.7.0/24" }));
        await col.PostAsync("/collector/v1/checkin", Json(new { found = new object[] { new { ip = "192.168.7.40", hostname = "PC-INSTALL", open = new[] { 135, 445, 5985 }, mac = "aa-bb-cc-00-11-33" } } }));
        var ad = await J(await api.PostAsync($"/api/assets/collectors/{cid}/adopt", Json(new { ips = new[] { "192.168.7.40" } })));
        Assert.Equal(1, ad.GetProperty("added").GetInt32());
        var assets = await J(await api.GetAsync("/api/assets"));
        var aid = assets.GetProperty("items").EnumerateArray().First(x => x.GetProperty("name").GetString() == "PC-INSTALL").GetProperty("id").GetInt64();
        await col.GetAsync("/collector/v1/wait?wait=1"); // drain the wake-up left by earlier requests

        // nothing yet
        var none = await J(await api.GetAsync($"/api/assets/{aid}/agent-install/log"));
        Assert.Equal("none", none.GetProperty("status").GetString());

        // press the button: queued, a first line in the log, and the controller is woken at once
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/assets/{aid}/agent-install", Json(new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/assets/{aid}/agent-install", Json(new { }))).StatusCode); // already running
        var q = await J(await api.GetAsync($"/api/assets/{aid}/agent-install/log"));
        Assert.Equal("queued", q.GetProperty("status").GetString());
        Assert.Contains("Queued by", Texts(q));
        var started = DateTime.UtcNow;
        Assert.True((await J(await col.GetAsync("/collector/v1/wait?wait=20"))).GetProperty("now").GetBoolean());
        Assert.True((DateTime.UtcNow - started).TotalSeconds < 10, "the wake-up must not wait for the long poll to time out");
        Assert.False((await J(await col.GetAsync("/collector/v1/wait?wait=1"))).GetProperty("now").GetBoolean()); // consumed

        // the controller picks the work up: running, and handed over only once
        var w = await J(await col.PostAsync("/collector/v1/checkin", Json(new { })));
        Assert.Equal(1, w.GetProperty("deploys").GetArrayLength());
        Assert.Equal(0, (await J(await col.PostAsync("/collector/v1/checkin", Json(new { })))).GetProperty("deploys").GetArrayLength());
        var run = await J(await api.GetAsync($"/api/assets/{aid}/agent-install/log"));
        Assert.Equal("running", run.GetProperty("status").GetString());
        Assert.Contains("Handed to the controller", Texts(run));

        // progress lines appear one by one and are read incrementally
        var seen = run.GetProperty("items").EnumerateArray().Last().GetProperty("seq").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await col.PostAsync("/collector/v1/progress", Json(new { asset = aid.ToString(), level = "warn", msg = "Port 5985 does not answer on PC-INSTALL" }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await col.PostAsync("/collector/v1/progress", Json(new { asset = "999999", msg = "x" }))).StatusCode);
        var more = await J(await api.GetAsync($"/api/assets/{aid}/agent-install/log?after={seen}"));
        Assert.Equal(1, more.GetProperty("items").GetArrayLength());
        Assert.Equal("warn: Port 5985 does not answer on PC-INSTALL", Texts(more));
        var anon = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/collector/v1/progress", Json(new { asset = aid.ToString(), msg = "x" }))).StatusCode);

        // a failure is final, shown in the log, and the device can be tried again with a clean log
        await col.PostAsync("/collector/v1/checkin", Json(new { deploys = new[] { new { asset = aid.ToString(), ok = false, message = "WinRM refused" } } }));
        var failed = await J(await api.GetAsync($"/api/assets/{aid}/agent-install/log"));
        Assert.Equal("failed", failed.GetProperty("status").GetString());
        Assert.Contains("error: Failed: WinRM refused", Texts(failed));
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/assets/{aid}/agent-install", Json(new { }))).StatusCode);
        var again = await J(await api.GetAsync($"/api/assets/{aid}/agent-install/log"));
        Assert.DoesNotContain("WinRM refused", Texts(again));
        await col.PostAsync("/collector/v1/checkin", Json(new { })); // handed over again

        // the agent starts on that computer and enrols: the log closes with success
        var tok = (await J(await api.PostAsync("/api/assets/enrolments", Json(new { companyId = co, label = "Office" })))).GetProperty("token").GetString()!;
        var inv = new { schema = 1, hostname = "PC-INSTALL", os_family = "windows", os_name = "Windows 11 Pro", serial_number = "SN-INSTALL-1", nics = new object[] { new { name = "Ethernet", mac = "AA-BB-CC-00-11-33", connected = true, ips = new[] { "192.168.7.40" } } } };
        Assert.Equal(HttpStatusCode.OK, (await anon.PostAsync("/agent/v1/enroll", Json(new { token = tok, platform = "windows", version = "2.9.0", inventory = inv }))).StatusCode);
        var done = await J(await api.GetAsync($"/api/assets/{aid}/agent-install/log"));
        Assert.Equal("done", done.GetProperty("status").GetString());
        Assert.Contains("ok: The agent started on PC-INSTALL and enrolled with the platform", Texts(done));
    }

    [PgFact]
    public async Task AnOfflineControllerIsSaidSoInTheLog()
    {
        var (api, _, cid, _) = await Start();
        await Sql($"update assets.collectors set last_seen = now() - interval '2 hours' where id = '{cid}'");
        var aid = (await J(await api.PostAsync("/api/assets", Json(new { name = "Server A" })))).GetProperty("id").GetInt64();
        await api.PostAsync($"/api/assets/{aid}/monitor", Json(new { mode = "ping", collectorId = cid }));
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/assets/{aid}/agent-install", Json(new { }))).StatusCode);
        Assert.Contains("warn: The controller \"HQ controller\" was last seen", Texts(await J(await api.GetAsync($"/api/assets/{aid}/agent-install/log"))));
    }

    [PgFact]
    public async Task AutomaticInstallAddsNewComputersOnceALoginExistsAndLeavesPrintersAlone()
    {
        var (api, col, cid, _) = await Start();
        await api.PatchAsync($"/api/assets/collectors/{cid}", Json(new { scanCidrs = "192.168.7.0/24" }));
        var list = await J(await api.GetAsync("/api/assets/collectors"));
        Assert.False(list.GetProperty("items")[0].GetProperty("autoInstall").GetBoolean());

        object[] scan(params (string Ip, string Host, int[] Open)[] f) => f.Select(x => (object)new { ip = x.Ip, hostname = x.Host, open = x.Open }).ToArray();

        // switched off: a scan only lists what it found
        await col.PostAsync("/collector/v1/checkin", Json(new { found = scan(("192.168.7.40", "PC40", new[] { 135, 445, 5985 })) }));
        Assert.DoesNotContain("PC40", await api.GetStringAsync("/api/assets"));

        // switched on, but no Windows login is saved: nothing is added and the activity says what to do
        Assert.Equal(HttpStatusCode.OK, (await api.PatchAsync($"/api/assets/collectors/{cid}", Json(new { autoInstall = true }))).StatusCode);
        Assert.True((await J(await api.GetAsync("/api/assets/collectors"))).GetProperty("items")[0].GetProperty("autoInstall").GetBoolean());
        await col.PostAsync("/collector/v1/checkin", Json(new { found = scan(("192.168.7.40", "PC40", new[] { 135, 445, 5985 })) }));
        Assert.DoesNotContain("PC40", await api.GetStringAsync("/api/assets"));
        Assert.Contains("no Windows login is saved", await api.GetStringAsync($"/api/assets/collectors/{cid}/log"));

        // with a saved login, a Windows computer is added and queued; the printer is not touched
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync("/api/assets/discover/creds", Json(new { name = "Domain admin", kind = "windows", username = "CORP\\svc", secret = "S3cret-Pw!" }))).StatusCode);
        await col.GetAsync("/collector/v1/wait?wait=1"); // drain
        var w = await J(await col.PostAsync("/collector/v1/checkin", Json(new { found = scan(("192.168.7.40", "PC40", new[] { 135, 445, 5985 }), ("192.168.7.41", "prn1", new[] { 9100, 80 })) })));
        var assets = await api.GetStringAsync("/api/assets");
        Assert.Contains("PC40", assets); Assert.DoesNotContain("prn1", assets);
        Assert.Equal(1, w.GetProperty("deploys").GetArrayLength()); // the same answer that reports the scan already carries the install
        var d = w.GetProperty("deploys")[0];
        Assert.Equal("192.168.7.40", d.GetProperty("ip").GetString()); Assert.Equal("windows", d.GetProperty("platform").GetString()); Assert.Equal(1, d.GetProperty("logins").GetArrayLength());
        var aid = long.Parse(d.GetProperty("asset").GetString()!);
        Assert.Contains("added automatically (Windows)", Texts(await J(await api.GetAsync($"/api/assets/{aid}/agent-install/log"))));

        // a second scan does not add it again
        var again = await J(await col.PostAsync("/collector/v1/checkin", Json(new { found = scan(("192.168.7.40", "PC40", new[] { 135, 445, 5985 })) })));
        Assert.Equal(0, again.GetProperty("deploys").GetArrayLength());
    }
}
