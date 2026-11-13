using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

/// <summary>Agent state on the asset list and the home page, and "Update agent now" with a live log.</summary>
public class AgentUpdateTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    static readonly string Dist = MakeDist();
    readonly WebApplicationFactory<Program> factory;
    public AgentUpdateTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b =>
    {
        TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); b.UseSetting("Assets:AgentDir", Dist);
    });

    // the server holds agent 2.9.3
    static string MakeDist()
    {
        var d = Path.Combine(Path.GetTempPath(), "deskask-agent-dist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "version.txt"), "2.9.3");
        File.WriteAllText(Path.Combine(d, "deskask-agent-windows-amd64.exe"), "MZ fake");
        return d;
    }

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }

    [PgFact]
    public async Task AnOldAgentShowsUpdateDueCanBeUpdatedFromTheAssetPageAndTheLogClosesWhenItComesBack()
    {
        var api = await TestSupport.AdminClient(factory);
        await api.GetAsync("/api/assets/types");
        await Sql("truncate assets.agent_upd_log, assets.shells, assets.rc_log, assets.jobs, assets.agent_q, assets.agents, assets.enroll_tokens, assets.history, assets.assets, assets.companies restart identity cascade");
        var co = (await J(await api.PostAsync("/api/assets/companies", Json(new { name = "Acme" })))).GetProperty("id").GetInt32();
        var tok = (await J(await api.PostAsync("/api/assets/enrolments", Json(new { companyId = co })))).GetProperty("token").GetString()!;
        var agent = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var en = await J(await agent.PostAsync("/agent/v1/enroll", Json(new { token = tok, platform = "windows", version = "2.8.6", inventory = new { schema = 1, hostname = "PC-OLD", os_family = "windows", serial_number = "SN-7654321" } })));
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", en.GetProperty("key").GetString()); agent.DefaultRequestHeaders.Add("X-Agent-Id", en.GetProperty("agentId").GetString());
        var asset = long.Parse(en.GetProperty("assetId").GetString()!);
        await api.PostAsync("/api/assets", Json(new { name = "PRINTER-1" })); // no agent

        // the list carries the agent state and can be filtered by it
        var list = await J(await api.GetAsync("/api/assets"));
        var row = list.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("id").GetInt64() == asset).GetProperty("agent");
        Assert.Equal("online", row.GetProperty("state").GetString()); Assert.Equal("2.8.6", row.GetProperty("version").GetString()); Assert.True(row.GetProperty("updateDue").GetBoolean());
        Assert.Equal("none", list.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "PRINTER-1").GetProperty("agent").GetProperty("state").GetString());
        Assert.Equal(1, (await J(await api.GetAsync("/api/assets?agent=update"))).GetProperty("total").GetInt64());
        Assert.Equal(1, (await J(await api.GetAsync("/api/assets?agent=none"))).GetProperty("total").GetInt64());
        Assert.Equal(HttpStatusCode.BadRequest, (await api.GetAsync("/api/assets?agent=sleepy")).StatusCode);

        // the home page counts
        var sum = await J(await api.GetAsync("/api/assets/summary"));
        Assert.Equal(1, sum.GetProperty("agents").GetProperty("updateDue").GetInt64()); Assert.Equal(1, sum.GetProperty("agents").GetProperty("none").GetInt64());
        Assert.Equal(0, sum.GetProperty("agents").GetProperty("online").GetInt64()); Assert.Equal(1, sum.GetProperty("compliance").GetProperty("unknown").GetInt64());

        // press Update agent now: the agent is told to update even a version it put back before, and the log says so
        await agent.GetAsync("/agent/v1/poll?wait=0&v=2.8.6"); // drain
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/assets/{asset}/agent-update", Json(new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/assets/{asset}/agent-update", Json(new { }))).StatusCode); // already running
        var msgs = (await J(await agent.GetAsync("/agent/v1/poll?wait=0&v=2.8.6"))).GetProperty("msgs").EnumerateArray().ToList();
        var forced = msgs.First(m => m.GetProperty("type").GetString() == "update" && m.TryGetProperty("force", out var f) && f.GetBoolean());
        Assert.Equal("2.9.3", forced.GetProperty("version").GetString());
        var log = await J(await api.GetAsync($"/api/assets/{asset}/agent-update/log"));
        Assert.Equal("running", log.GetProperty("status").GetString());
        var text = string.Join("\n", log.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("text").GetString()));
        Assert.Contains("2.8.6 to 2.9.3", text); Assert.Contains("older than 2.9.3", text);

        // the agent reports a step, then comes back with the new version: the log closes
        await agent.PostAsync("/agent/v1/update-log", Json(new { level = "info", msg = "Downloading the new program from the platform" }));
        await agent.GetAsync("/agent/v1/poll?wait=0&v=2.9.3");
        log = await J(await api.GetAsync($"/api/assets/{asset}/agent-update/log"));
        Assert.Equal("done", log.GetProperty("status").GetString()); Assert.Equal("2.9.3", log.GetProperty("version").GetString());
        text = string.Join("\n", log.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("level").GetString() + ": " + x.GetProperty("text").GetString()));
        Assert.Contains("Downloading the new program", text); Assert.Contains("ok: The agent is back and runs version 2.9.3", text);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/assets/{asset}/agent-update", Json(new { }))).StatusCode); // up to date now

        // a failure reported by the agent shows as failed
        await Sql("update assets.agents set version = '2.8.6'");
        await api.PostAsync($"/api/assets/{asset}/agent-update", Json(new { }));
        await agent.PostAsync("/agent/v1/update-log", Json(new { level = "warn", msg = "Update to 2.9.3 failed: the downloaded program does not start", state = "failed" }));
        Assert.Equal("failed", (await J(await api.GetAsync($"/api/assets/{asset}/agent-update/log"))).GetProperty("status").GetString());

        // an offline agent cannot be asked
        await Sql("update assets.agents set last_seen = now() - interval '2 hours', upd_state = ''");
        var off = await api.PostAsync($"/api/assets/{asset}/agent-update", Json(new { }));
        Assert.Equal(HttpStatusCode.BadRequest, off.StatusCode); Assert.Equal("agent_offline", (await J(off)).GetProperty("error").GetString());
    }
}
