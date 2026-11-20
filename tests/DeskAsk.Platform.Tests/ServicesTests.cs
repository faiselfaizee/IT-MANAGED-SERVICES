using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DeskAsk.Platform.Assets;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

/// <summary>
/// 6.3: services and programs on a computer with the agent. Start, stop, restart and startup type become agent jobs whose scripts quote the
/// name; the Log On account goes through the one-time sealed hand-over (never into a job); uninstall needs a reason; viewers can do none of it.
/// </summary>
public class ServicesTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    const string Pw = "Correct-Horse-9";
    readonly WebApplicationFactory<Program> factory;
    public ServicesTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) { var t = await r.Content.ReadAsStringAsync(); Assert.True(r.IsSuccessStatusCode, t); return JsonDocument.Parse(t).RootElement.Clone(); }
    static async Task<object?> Sql(string sql, params (string, object)[] p) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); foreach (var (n, v) in p) c.Parameters.AddWithValue(n, v); return await c.ExecuteScalarAsync(); }

    [Fact]
    public void ValuesBecomeQuotedLiteralsNotCode()
    {
        Assert.Equal("'Spooler'", ServicesApi.Ps("Spooler"));
        Assert.Equal("'it''s; Remove-Item C:\\'", ServicesApi.Ps("it's; Remove-Item C:\\"));
        Assert.Equal("'a\u2019\u2019b'", ServicesApi.Ps("a\u2019b"));
        Assert.Equal("'x'\"'\"'; rm -rf /'", ServicesApi.Sh("x'; rm -rf /"));
    }

    async Task<(HttpClient Agent, long Asset)> Enrol(HttpClient api, string host, string platform)
    {
        var tok = (await J(await api.PostAsync("/api/assets/enrolments", Json(new { })))).GetProperty("token").GetString()!;
        var agent = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var en = await J(await agent.PostAsync("/agent/v1/enroll", Json(new { token = tok, platform, version = "6.3.0", inventory = new { schema = 1, hostname = host, os_family = platform, serial_number = "SN-" + host,
            installed_apps = new[] { new { name = "7-Zip 23.01 (x64)", version = "23.01", publisher = "Igor Pavlov" } } } })));
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", en.GetProperty("key").GetString());
        agent.DefaultRequestHeaders.Add("X-Agent-Id", en.GetProperty("agentId").GetString());
        return (agent, long.Parse(en.GetProperty("assetId").GetString()!));
    }

    [PgFact]
    public async Task ServicesAndUninstallGoThroughTheAgentSafely()
    {
        var api = await TestSupport.AdminClient(factory);
        await api.GetAsync("/api/assets/types");
        foreach (var (email, role) in new[] { ("eng@svc.test", "engineer"), ("view@svc.test", "viewer") })
            await api.PostAsync("/api/platform/users", Json(new { email, name = email.Split('@')[0], role, password = Pw }));
        var host = "svc" + Guid.NewGuid().ToString("N")[..6];
        var (agent, id) = await Enrol(api, host, "windows");

        // the agent reports its services, with the Log On account and delayed start
        Assert.Equal(HttpStatusCode.OK, (await agent.PostAsync("/agent/v1/metrics", Json(new { cpu = 5, mem_used = 1, mem_total = 10, uptime = 60, disks = Array.Empty<object>(),
            inventory = new object[] { new { name = "MSSQLSERVER", display = "SQL Server", state = "running", start = "delayed", logon = "CORP\\svc_sql" }, new { name = "Spooler", display = "Print Spooler", state = "stopped", start = "auto", logon = "LocalSystem" } } }))).StatusCode);
        var list = await J(await api.GetAsync($"/api/assets/{id}/services"));
        Assert.True(list.GetProperty("canControl").GetBoolean()); Assert.True(list.GetProperty("canLogon").GetBoolean()); Assert.Equal("windows", list.GetProperty("platform").GetString());
        var sql = list.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "MSSQLSERVER");
        Assert.Equal("CORP\\svc_sql", sql.GetProperty("logon").GetString()); Assert.Equal("delayed", sql.GetProperty("start").GetString());

        // start: a PowerShell job with the name as a quoted literal
        var jid = (await J(await api.PostAsync($"/api/assets/{id}/services/action", Json(new { name = "Spooler", action = "start" })))).GetProperty("id").GetString()!;
        var body = (string)(await Sql("select body from assets.jobs where id = @i", ("i", jid)))!;
        Assert.Contains("$Name = 'Spooler'", body); Assert.Contains("Start-Service", body);
        Assert.Equal("powershell", (string)(await Sql("select shell from assets.jobs where id = @i", ("i", jid)))!);
        // the computer's answer: the state line is read out, the rest is the message
        await Sql("update assets.jobs set status = 'done', exit_code = 0, output = @o where id = @i", ("i", jid), ("o", "Print Spooler (Spooler) is now Running.\r\nDESKASK-SERVICE|{\"name\":\"Spooler\",\"state\":\"running\",\"start\":\"auto\",\"logon\":\"LocalSystem\"}\r\n"));
        var st = await J(await api.GetAsync($"/api/assets/{id}/actions/{jid}"));
        Assert.Equal("done", st.GetProperty("status").GetString()); Assert.Equal("Print Spooler (Spooler) is now Running.", st.GetProperty("output").GetString());
        Assert.Equal("running", st.GetProperty("service").GetProperty("state").GetString());

        // startup type
        var j2 = (await J(await api.PostAsync($"/api/assets/{id}/services/action", Json(new { name = "MSSQLSERVER", action = "startup", startup = "delayed" })))).GetProperty("id").GetString()!;
        Assert.Contains("$Mode = 'delayed-auto'", (string)(await Sql("select body from assets.jobs where id = @i", ("i", j2)))!);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/assets/{id}/services/action", Json(new { name = "Spooler", action = "startup", startup = "sometimes" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/assets/{id}/services/action", Json(new { name = "Spool'er\"; x", action = "start" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/assets/{id}/services/action", Json(new { name = "Spooler", action = "delete" }))).StatusCode);

        // Log On: a reason is needed; a built-in account is a job; a real account is a sealed one-time hand-over, never a job
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/assets/{id}/services/logon", Json(new { name = "MSSQLSERVER", account = "LocalSystem" }))).StatusCode);
        var b1 = await J(await api.PostAsync($"/api/assets/{id}/services/logon", Json(new { name = "Spooler", account = "NetworkService", reason = "least privilege" })));
        Assert.Equal("job", b1.GetProperty("kind").GetString());
        Assert.Contains("$Account = 'NT AUTHORITY\\NetworkService'", (string)(await Sql("select body from assets.jobs where id = @i", ("i", b1.GetProperty("id").GetString()!)))!);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/assets/{id}/services/logon", Json(new { name = "MSSQLSERVER", account = "a\\b\\c", password = "x", reason = "r" }))).StatusCode);
        var secret = "Sql-Service-Pw-" + Guid.NewGuid().ToString("N")[..6];
        var h = await J(await api.PostAsync($"/api/assets/{id}/services/logon", Json(new { name = "MSSQLSERVER", account = "CORP\\svc_sql2", password = secret, reason = "new service account (CHG-0042)" })));
        Assert.Equal("handoff", h.GetProperty("kind").GetString());
        var hid = h.GetProperty("id").GetString()!;
        Assert.Equal("service:MSSQLSERVER", (string)(await Sql("select target from assets.secret_handoff where id = @i", ("i", hid)))!);
        Assert.DoesNotContain(secret, (string)(await Sql("select secret_sealed from assets.secret_handoff where id = @i", ("i", hid)))!);
        Assert.Equal(0L, await Sql("select count(*) from assets.jobs where body like @s", ("s", "%" + secret + "%")));
        Assert.Equal(0L, await Sql("select count(*) from core.audit where detail like @s", ("s", "%" + secret + "%")));
        // the agent fetches it once and reports back
        var got = await J(await agent.GetAsync("/agent/v1/secret/" + hid));
        Assert.Equal(secret, got.GetProperty("password").GetString()); Assert.Equal("service:MSSQLSERVER", got.GetProperty("target").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await agent.GetAsync("/agent/v1/secret/" + hid)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await agent.PostAsync($"/agent/v1/secret/{hid}/result", Json(new { ok = true }))).StatusCode);
        Assert.Equal("done", (await J(await api.GetAsync($"/api/assets/{id}/services/logon/{hid}"))).GetProperty("state").GetString());

        // uninstall: a reason, then a job; the history keeps it
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/assets/{id}/apps/uninstall", Json(new { name = "7-Zip 23.01 (x64)", version = "23.01" }))).StatusCode);
        var u = (await J(await api.PostAsync($"/api/assets/{id}/apps/uninstall", Json(new { name = "7-Zip 23.01 (x64)", version = "23.01", reason = "not needed on servers" })))).GetProperty("id").GetString()!;
        Assert.Contains("$Name = '7-Zip 23.01 (x64)'", (string)(await Sql("select body from assets.jobs where id = @i", ("i", u)))!);
        Assert.Contains("not needed on servers", (string)(await Sql("select changes from assets.history where asset_id = @a and action = 'uninstall' order by id desc limit 1", ("a", id)))!);

        // who may: engineers control services and uninstall, but not Log On; viewers nothing
        var eng = await TestSupport.ClientFor(factory, "eng@svc.test", Pw);
        Assert.Equal(HttpStatusCode.OK, (await eng.PostAsync($"/api/assets/{id}/services/action", Json(new { name = "Spooler", action = "restart" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await eng.PostAsync($"/api/assets/{id}/services/logon", Json(new { name = "Spooler", account = "LocalSystem", reason = "r" }))).StatusCode);
        Assert.False((await J(await eng.GetAsync($"/api/assets/{id}/services"))).GetProperty("canLogon").GetBoolean());
        var view = await TestSupport.ClientFor(factory, "view@svc.test", Pw);
        Assert.Equal(HttpStatusCode.Forbidden, (await view.PostAsync($"/api/assets/{id}/services/action", Json(new { name = "Spooler", action = "stop" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await view.PostAsync($"/api/assets/{id}/apps/uninstall", Json(new { name = "x", reason = "r" }))).StatusCode);

        // Linux: systemd units, no Log On, package names only
        var (_, lin) = await Enrol(api, host + "l", "linux");
        var lj = (await J(await api.PostAsync($"/api/assets/{lin}/services/action", Json(new { name = "nginx.service", action = "startup", startup = "auto" })))).GetProperty("id").GetString()!;
        var lb = (string)(await Sql("select body from assets.jobs where id = @i", ("i", lj)))!;
        Assert.Contains("NAME='nginx.service'", lb); Assert.Contains("ACTION='enable'", lb);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/assets/{lin}/services/logon", Json(new { name = "nginx", account = "LocalSystem", reason = "r" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/assets/{lin}/apps/uninstall", Json(new { name = "nginx; reboot", reason = "r" }))).StatusCode);
    }
}
