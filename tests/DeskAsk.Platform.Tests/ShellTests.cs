using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using DeskAsk.Platform;
using DeskAsk.Platform.Assets;
using Npgsql;
using Xunit;

public class ShellTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    readonly WebApplicationFactory<Program> factory;
    public ShellTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }

    async Task<(HttpClient Api, HttpClient Agent, long AssetId, string AgentId)> Start()
    {
        var api = await TestSupport.AdminClient(factory);
        await api.GetAsync("/api/assets/types");
        await Sql("truncate assets.shells, assets.rc_log, assets.jobs, assets.agent_q, assets.agents, assets.enroll_tokens, assets.amc, assets.rentals, assets.history, assets.assets, assets.companies, assets.types restart identity cascade");
        var co = (await J(await api.PostAsync("/api/assets/companies", Json(new { name = "Acme", remoteAllowed = true })))).GetProperty("id").GetInt32();
        var tok = (await J(await api.PostAsync("/api/assets/enrolments", Json(new { companyId = co })))).GetProperty("token").GetString()!;
        var c = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var r = await c.PostAsync("/agent/v1/enroll", Json(new { token = tok, platform = "windows", version = "1.6.0", inventory = new { schema = 1, hostname = "PC-1", os_family = "windows", serial_number = "SN-1234567" } }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await J(r);
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", j.GetProperty("key").GetString()); c.DefaultRequestHeaders.Add("X-Agent-Id", j.GetProperty("agentId").GetString());
        return (api, c, long.Parse(j.GetProperty("assetId").GetString()!), j.GetProperty("agentId").GetString()!);
    }
    static async Task<JsonElement> Poll(HttpClient agent) => (await J(await agent.GetAsync("/agent/v1/poll?wait=0"))).GetProperty("msgs");

    [PgFact]
    public async Task LiveShellRelaysInputAndOutputAndAuditsEveryLine()
    {
        var (api, agent, asset, _) = await Start();
        var open = await api.PostAsync("/api/assets/" + asset + "/shell", Json(new { shell = "powershell" }));
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        var sid = (await J(open)).GetProperty("sid").GetString()!;
        var m = (await Poll(agent))[0];
        Assert.Equal("open", m.GetProperty("type").GetString()); Assert.Equal(sid, m.GetProperty("sid").GetString()); Assert.Equal("powershell", m.GetProperty("shell").GetString());

        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync("/api/assets/shell/" + sid + "/input", Json(new { data = "whoami\nGet-Date\n" }))).StatusCode);
        var inp = (await Poll(agent))[0];
        Assert.Equal("input", inp.GetProperty("type").GetString()); Assert.Equal("whoami\nGet-Date\n", inp.GetProperty("data").GetString());
        var audit = await J(await api.GetAsync("/api/platform/audit?action=assets.shell_input&take=10"));
        Assert.Equal(new[] { "Get-Date", "whoami" }, audit.EnumerateArray().Select(x => x.GetProperty("detail").GetString()!).ToArray());

        Assert.Equal(HttpStatusCode.OK, (await agent.PostAsync("/agent/v1/shell", Json(new { sid, data = "nt authority\\system\r\n" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await agent.PostAsync("/agent/v1/shell", Json(new { sid, data = "PS> " }))).StatusCode);
        var o = await J(await api.GetAsync("/api/assets/shell/" + sid + "/output?since=0&wait=0"));
        Assert.Equal(2, o.GetProperty("chunks").GetArrayLength()); Assert.Equal("nt authority\\system\r\n", o.GetProperty("chunks")[0].GetProperty("data").GetString());
        var seq = o.GetProperty("seq").GetInt32();
        Assert.Equal(0, (await J(await api.GetAsync("/api/assets/shell/" + sid + "/output?since=" + seq + "&wait=0"))).GetProperty("chunks").GetArrayLength());

        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync("/api/assets/shell/" + sid + "/close", Json(new { }))).StatusCode);
        Assert.Equal("close", (await Poll(agent))[0].GetProperty("type").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/assets/shell/" + sid + "/input", Json(new { data = "dir" }))).StatusCode);
        Assert.True((await J(await api.GetAsync("/api/assets/shell/" + sid + "/output?since=" + seq + "&wait=0"))).GetProperty("closed").GetBoolean());
    }

    [PgFact]
    public async Task ShellRulesAndTheIdleClose()
    {
        var (api, agent, asset, _) = await Start();
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/assets/" + asset + "/shell", Json(new { shell = "bash" }))).StatusCode);   // Windows has no bash
        var ids = new List<string>();
        for (int i = 0; i < 3; i++) ids.Add((await J(await api.PostAsync("/api/assets/" + asset + "/shell", Json(new { })))).GetProperty("sid").GetString()!);
        var fourth = await api.PostAsync("/api/assets/" + asset + "/shell", Json(new { }));
        Assert.Equal(HttpStatusCode.BadRequest, fourth.StatusCode); Assert.Equal("too_many_shells", (await J(fourth)).GetProperty("error").GetString());

        await Sql("update assets.shells set last_in = now() - interval '31 minutes' where id = '" + ids[0] + "'");
        var tenant = new TenantInfo("t", "t", "h", Pg);
        await new AssetsJanitorJob().RunAsync(new JobContext(tenant, 1, "", 1, null!), CancellationToken.None);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/assets/shell/" + ids[0] + "/input", Json(new { data = "x" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync("/api/assets/shell/" + ids[1] + "/input", Json(new { data = "x" }))).StatusCode);

        // another agent cannot write into this session; no sign-in, no shell
        var other = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.PostAsync("/agent/v1/shell", Json(new { sid = ids[1], data = "x" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.PostAsync("/api/assets/shell/" + ids[1] + "/input", Json(new { data = "x" }))).StatusCode);
        await Sql("update assets.assets set remote_ok = false");
        Assert.Equal(HttpStatusCode.Forbidden, (await api.PostAsync("/api/assets/" + asset + "/shell", Json(new { }))).StatusCode);
    }

    [PgFact]
    public async Task RemoteControlSetupConnectAndRemove()
    {
        var (api, agent, asset, agentId) = await Start();
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/assets/" + asset + "/remote-control/setup", Json(new { mode = "unattended" }))).StatusCode);   // not switched on
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PutAsync("/api/assets/settings/remote-control", Json(new { enabled = true }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PutAsync("/api/assets/settings/remote-control", Json(new { idServer = "bad host!" }))).StatusCode);
        var save = await api.PutAsync("/api/assets/settings/remote-control", Json(new { enabled = true, idServer = "rd.example.com", installer = "https://rd.example.com/rustdesk.exe", sha256 = new string('a', 64) }));
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync("/api/assets/" + asset + "/remote-control/setup", Json(new { mode = "unattended" }))).StatusCode);
        var m = (await Poll(agent))[0];
        Assert.Equal("rdsetup", m.GetProperty("type").GetString());
        var rd = m.GetProperty("rd"); var pw = rd.GetProperty("password").GetString()!;
        Assert.Equal("rd.example.com", rd.GetProperty("idServer").GetString()); Assert.True(pw.Length >= 12); Assert.Equal("unattended", rd.GetProperty("mode").GetString());
        Assert.Equal("pending", (await J(await api.GetAsync("/api/assets/" + asset + "/remote-control"))).GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/assets/" + asset + "/remote-control/connect", Json(new { }))).StatusCode);   // not ready yet

        await agent.PostAsync("/agent/v1/remote", Json(new { state = "log", msg = "Downloading installer" }));
        await agent.PostAsync("/agent/v1/remote", Json(new { state = "ready", id = "123 456 789" }));   // spaces are dropped
        var st = await J(await api.GetAsync("/api/assets/" + asset + "/remote-control"));
        Assert.Equal("ready", st.GetProperty("state").GetString()); Assert.Equal("123456789", st.GetProperty("id").GetString());
        var log = await J(await api.GetAsync("/api/assets/" + asset + "/remote-control/log"));
        Assert.Contains(log.GetProperty("items").EnumerateArray(), x => x.GetProperty("text").GetString() == "Downloading installer");

        var c = await J(await api.PostAsync("/api/assets/" + asset + "/remote-control/connect", Json(new { via = "browser" })));
        Assert.Equal(pw, c.GetProperty("password").GetString()); Assert.Equal("rustdesk://connection/new/123456789?password=" + Uri.EscapeDataString(pw), c.GetProperty("link").GetString());
        var audit = await J(await api.GetAsync("/api/platform/audit?action=assets.remote_session"));
        Assert.Equal(1, audit.GetArrayLength());
        await using (var db = new NpgsqlConnection(Pg)) { await db.OpenAsync(); await using var q = new NpgsqlCommand("select rd_pw from assets.agents where id = '" + agentId + "'", db); Assert.DoesNotContain(pw, (string)(await q.ExecuteScalarAsync())!); }   // sealed in the database

        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync("/api/assets/" + asset + "/remote-control/remove", Json(new { }))).StatusCode);
        Assert.Equal("rdremove", (await Poll(agent))[0].GetProperty("type").GetString());
        Assert.Equal("", (await J(await api.GetAsync("/api/assets/" + asset + "/remote-control"))).GetProperty("state").GetString());

        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync("/api/assets/" + asset + "/remote-control/setup", Json(new { mode = "attended" }))).StatusCode);
        Assert.Equal("", (await Poll(agent))[0].GetProperty("rd").GetProperty("password").GetString());   // attended: nothing to type in
    }
}
