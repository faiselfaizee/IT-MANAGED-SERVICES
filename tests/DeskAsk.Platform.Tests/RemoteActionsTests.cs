using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

/// <summary>
/// 6.25: ownership and hosting of an asset, number prefixes per kind, install scripts in the repository, and patching, print server and busiest
/// processes as agent jobs (a reason for every change, names checked, viewers refused, Linux and Windows each get their own script).
/// </summary>
public class RemoteActionsTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    const string Pw = "Correct-Horse-9";
    readonly WebApplicationFactory<Program> factory;
    public RemoteActionsTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) { var t = await r.Content.ReadAsStringAsync(); Assert.True(r.IsSuccessStatusCode, t); return JsonDocument.Parse(t).RootElement.Clone(); }
    static async Task<object?> Sql(string sql, params (string, object)[] p) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); foreach (var (n, v) in p) c.Parameters.AddWithValue(n, v); return await c.ExecuteScalarAsync(); }

    async Task<(HttpClient Agent, long Asset)> Enrol(HttpClient api, string host, string platform)
    {
        var tok = (await J(await api.PostAsync("/api/assets/enrolments", Json(new { })))).GetProperty("token").GetString()!;
        var agent = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var en = await J(await agent.PostAsync("/agent/v1/enroll", Json(new { token = tok, platform, version = "6.25.0", inventory = new { schema = 1, hostname = host, os_family = platform, serial_number = "SN-" + host } })));
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", en.GetProperty("key").GetString());
        agent.DefaultRequestHeaders.Add("X-Agent-Id", en.GetProperty("agentId").GetString());
        return (agent, long.Parse(en.GetProperty("assetId").GetString()!));
    }

    [PgFact]
    public async Task OwnershipHostingAndNumberPrefixesPerKind()
    {
        var api = await TestSupport.AdminClient(factory);
        var prefix = "Z" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        // a prefix is 2 to 8 letters or digits
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/assets/types", Json(new { name = "Bad " + prefix, tagPrefix = "a-b" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/assets/types", Json(new { name = "Bad2 " + prefix, tagPrefix = "1AB" }))).StatusCode);
        var typeId = (await J(await api.PostAsync("/api/assets/types", Json(new { name = "Rack server " + prefix, tagPrefix = prefix.ToLowerInvariant() })))).GetProperty("id").GetInt32();
        var types = await J(await api.GetAsync("/api/assets/types"));
        Assert.Equal(prefix, types.EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == typeId).GetProperty("tagPrefix").GetString());

        // new assets of that kind get SRV-style numbers in order; a kind without a prefix keeps AST-
        var a1 = await J(await api.PostAsync("/api/assets", Json(new { name = "Rack 1 " + prefix, typeId, ownershipKind = "leased", hostedAt = "Chennai data centre" })));
        var a2 = await J(await api.PostAsync("/api/assets", Json(new { name = "Rack 2 " + prefix, typeId })));
        Assert.Equal(prefix + "-0001", a1.GetProperty("tag").GetString()); Assert.Equal(prefix + "-0002", a2.GetProperty("tag").GetString());
        var plain = await J(await api.PostAsync("/api/assets", Json(new { name = "No kind " + prefix })));
        Assert.StartsWith("AST-", plain.GetProperty("tag").GetString());
        // a tag typed by a person is never replaced
        var typed = await J(await api.PostAsync("/api/assets", Json(new { name = "Typed " + prefix, typeId, tag = "MY-" + prefix })));
        Assert.Equal("MY-" + prefix, typed.GetProperty("tag").GetString());

        // ownership and hosting are kept, shown on the device page and can be changed or cleared
        var id1 = a1.GetProperty("id").GetInt64();
        var one = await J(await api.GetAsync($"/api/assets/{id1}"));
        Assert.Equal("leased", one.GetProperty("asset").GetProperty("ownershipKind").GetString()); Assert.Equal("Chennai data centre", one.GetProperty("asset").GetProperty("hostedAt").GetString());
        var dev = await J(await api.GetAsync($"/api/assets/{id1}/device"));
        Assert.Equal("leased", dev.GetProperty("ownership").GetProperty("kind").GetString()); Assert.Equal("Chennai data centre", dev.GetProperty("ownership").GetProperty("hostedAt").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PutAsync($"/api/assets/{id1}", Json(new { ownershipKind = "stolen" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PutAsync($"/api/assets/{id1}", Json(new { ownershipKind = "owned", hostedAt = "" }))).StatusCode);
        var after = (await J(await api.GetAsync($"/api/assets/{id1}"))).GetProperty("asset");
        Assert.Equal("owned", after.GetProperty("ownershipKind").GetString()); Assert.Equal("", after.GetProperty("hostedAt").GetString());

        // setting the kind of an existing asset numbers it, once
        var p3 = await J(await api.PostAsync("/api/assets", Json(new { name = "Later " + prefix })));
        var id3 = p3.GetProperty("id").GetInt64();
        Assert.StartsWith("AST-", p3.GetProperty("tag").GetString());
        Assert.Equal(HttpStatusCode.OK, (await api.PutAsync($"/api/assets/{id3}", Json(new { typeId }))).StatusCode);
        Assert.Equal(prefix + "-0003", (string)(await Sql("select tag from assets.assets where id = @i", ("i", id3)))!);
        Assert.Equal(HttpStatusCode.OK, (await api.PutAsync($"/api/assets/{id3}", Json(new { name = "Later renamed " + prefix }))).StatusCode);
        Assert.Equal(prefix + "-0003", (string)(await Sql("select tag from assets.assets where id = @i", ("i", id3)))!);
    }

    [PgFact]
    public async Task AnInstallScriptIsAProgramOfTheRepository()
    {
        var api = await TestSupport.AdminClient(factory);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/assets/packages", Json(new { name = "Agent X", os = "windows", kind = "script", script = "  " }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/assets/packages", Json(new { name = "Agent X", os = "windows", kind = "script", script = "a\u0001b" }))).StatusCode);
        var name = "Wazuh " + Guid.NewGuid().ToString("N")[..6];
        var script = "$ErrorActionPreference='Stop'\r\nInvoke-WebRequest https://example.test/a.msi -OutFile $env:TEMP\\a.msi\r\nStart-Process msiexec -ArgumentList '/i a.msi /qn' -Wait";
        var added = await J(await api.PostAsync("/api/assets/packages", Json(new { name, version = "4.9", os = "windows", kind = "script", script })));
        Assert.Equal(64, added.GetProperty("sha256").GetString()!.Length);
        var list = await J(await api.GetAsync("/api/assets/packages"));
        var p = list.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        Assert.Equal("script", p.GetProperty("kind").GetString()); Assert.Contains("Start-Process msiexec", p.GetProperty("script").GetString());
        var pid = p.GetProperty("id").GetInt32();

        // installing queues the script itself, with line breaks kept
        var host = "scr" + Guid.NewGuid().ToString("N")[..6];
        var (_, win) = await Enrol(api, host, "windows");
        var res = await J(await api.PostAsync($"/api/assets/packages/{pid}/install", Json(new { assetIds = new[] { win } })));
        var jid = res.GetProperty("queued")[0].GetProperty("job").GetString()!;
        var body = (string)(await Sql("select body from assets.jobs where id = @i", ("i", jid)))!;
        Assert.Contains("Invoke-WebRequest https://example.test/a.msi", body); Assert.Contains("Start-Process msiexec", body); Assert.Contains("\n", body);
        // a changed script replaces the old one and its hash
        var old = added.GetProperty("sha256").GetString();
        Assert.Equal(HttpStatusCode.OK, (await api.PutAsync($"/api/assets/packages/{pid}", Json(new { script = "Write-Output 'new'" }))).StatusCode);
        var again = (await J(await api.GetAsync("/api/assets/packages"))).GetProperty("items").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        Assert.Equal("Write-Output 'new'", again.GetProperty("script").GetString()); Assert.NotEqual(old, again.GetProperty("sha256").GetString());
        // a Linux computer cannot take a Windows script
        var (_, lin) = await Enrol(api, host + "l", "linux");
        var skipped = await J(await api.PostAsync($"/api/assets/packages/{pid}/install", Json(new { assetIds = new[] { lin } })));
        Assert.Equal(0, skipped.GetProperty("queued").GetArrayLength()); Assert.Equal(1, skipped.GetProperty("skipped").GetArrayLength());
        // a bash script for Linux runs under set -e
        var lp = (await J(await api.PostAsync("/api/assets/packages", Json(new { name = "Tool " + host, os = "linux", kind = "script", script = "echo hi\ntouch /tmp/x" })))).GetProperty("id").GetInt32();
        var lr = await J(await api.PostAsync($"/api/assets/packages/{lp}/install", Json(new { assetIds = new[] { lin } })));
        var lb = (string)(await Sql("select body from assets.jobs where id = @i", ("i", lr.GetProperty("queued")[0].GetProperty("job").GetString()!)))!;
        Assert.StartsWith("set -e", lb); Assert.Contains("touch /tmp/x", lb);
    }

    [PgFact]
    public async Task PatchingPrintServerAndProcessesRunAsCheckedAgentJobs()
    {
        var api = await TestSupport.AdminClient(factory);
        await api.GetAsync("/api/assets/types");
        await api.PostAsync("/api/platform/users", Json(new { email = "view@ra.test", name = "view", role = "viewer", password = Pw }));
        var host = "ra" + Guid.NewGuid().ToString("N")[..6];
        var (_, win) = await Enrol(api, host, "windows");
        var (_, lin) = await Enrol(api, host + "l", "linux");

        // Windows: the check is a Windows Update search; the result is kept with the asset and shown on the fleet page
        var c1 = (await J(await api.PostAsync($"/api/assets/{win}/patches/check", Json(new { })))).GetProperty("id").GetString()!;
        Assert.Equal("powershell", (string)(await Sql("select shell from assets.jobs where id = @i", ("i", c1)))!);
        Assert.Contains("Microsoft.Update.Session", (string)(await Sql("select body from assets.jobs where id = @i", ("i", c1)))!);
        await Sql("update assets.jobs set status = 'done', exit_code = 0, output = @o where id = @i", ("i", c1),
            ("o", "1 update(s) are waiting.\r\nDESKASK-DATA|{\"pending\":1,\"rebootRequired\":true,\"items\":[{\"title\":\"Security Update\",\"kb\":\"KB5034441\",\"severity\":\"Critical\",\"category\":\"Security Updates\",\"sizeMb\":40.5,\"reboot\":true}]}\r\n"));
        var r1 = await J(await api.GetAsync($"/api/assets/{win}/remote/{c1}"));
        Assert.Equal("done", r1.GetProperty("status").GetString()); Assert.Equal("1 update(s) are waiting.", r1.GetProperty("output").GetString());
        Assert.Equal(1, r1.GetProperty("data").GetProperty("pending").GetInt32());
        var kept = await J(await api.GetAsync($"/api/assets/{win}/patches"));
        Assert.Equal("KB5034441", kept.GetProperty("check").GetProperty("items")[0].GetProperty("kb").GetString()); Assert.True(kept.GetProperty("canControl").GetBoolean());
        var fleet = await J(await api.GetAsync("/api/assets/patching"));
        var row = fleet.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("id").GetInt64() == win);
        Assert.Equal(1, row.GetProperty("pending").GetInt32()); Assert.True(row.GetProperty("rebootRequired").GetBoolean());

        // installing needs a reason; KB numbers are checked; the numbers become quoted literals
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/assets/{win}/patches/install", Json(new { items = new[] { "KB5034441" } }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/assets/{win}/patches/install", Json(new { reason = "r", items = new[] { "KB1'; Remove-Item C:\\ #" } }))).StatusCode);
        var i1 = (await J(await api.PostAsync($"/api/assets/{win}/patches/install", Json(new { reason = "monthly patching CHG-5", restart = true, items = new[] { "kb5034441" } })))).GetProperty("id").GetString()!;
        var ib = (string)(await Sql("select body from assets.jobs where id = @i", ("i", i1)))!;
        Assert.Contains("$Kbs = @('KB5034441')", ib); Assert.Contains("$Restart = $true", ib); Assert.Contains("CreateUpdateInstaller", ib);
        Assert.Contains("monthly patching CHG-5", (string)(await Sql("select changes from assets.history where asset_id = @a and action = 'patch' order by id desc limit 1", ("a", win)))!);
        var all = (await J(await api.PostAsync($"/api/assets/{win}/patches/install", Json(new { reason = "all" })))).GetProperty("id").GetString()!;
        Assert.Contains("$Kbs = @()", (string)(await Sql("select body from assets.jobs where id = @i", ("i", all)))!);

        // Linux: package names only; apt, dnf or yum
        var lc = (await J(await api.PostAsync($"/api/assets/{lin}/patches/check", Json(new { })))).GetProperty("id").GetString()!;
        Assert.Equal("bash", (string)(await Sql("select shell from assets.jobs where id = @i", ("i", lc)))!);
        Assert.Contains("apt list --upgradable", (string)(await Sql("select body from assets.jobs where id = @i", ("i", lc)))!);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/assets/{lin}/patches/install", Json(new { reason = "r", items = new[] { "openssl; reboot" } }))).StatusCode);
        var li = (await J(await api.PostAsync($"/api/assets/{lin}/patches/install", Json(new { reason = "security", items = new[] { "openssl", "libssl3" } })))).GetProperty("id").GetString()!;
        Assert.Contains("PKGS=('openssl' 'libssl3')", (string)(await Sql("select body from assets.jobs where id = @i", ("i", li)))!);

        // print server: Windows only; changes need a reason; the printer name is a quoted literal
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/assets/{lin}/printserver", Json(new { action = "list" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/assets/{win}/printserver", Json(new { action = "format" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/assets/{win}/printserver", Json(new { action = "clear-all" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/assets/{win}/printserver", Json(new { action = "clear", reason = "r" }))).StatusCode);
        var pl = (await J(await api.PostAsync($"/api/assets/{win}/printserver", Json(new { action = "list" })))).GetProperty("id").GetString()!;
        Assert.Contains("Get-PrinterDriver", (string)(await Sql("select body from assets.jobs where id = @i", ("i", pl)))!);
        var pc = (await J(await api.PostAsync($"/api/assets/{win}/printserver", Json(new { action = "clear", printer = "Front desk 'A'", reason = "jammed job" })))).GetProperty("id").GetString()!;
        Assert.Contains("$Printer = 'Front desk ''A'''", (string)(await Sql("select body from assets.jobs where id = @i", ("i", pc)))!);
        var ps = (await J(await api.PostAsync($"/api/assets/{win}/printserver", Json(new { action = "restart-spooler", reason = "spooler hung" })))).GetProperty("id").GetString()!;
        Assert.Contains("Restart-Service -Name Spooler", (string)(await Sql("select body from assets.jobs where id = @i", ("i", ps)))!);

        // busiest processes
        var pr = (await J(await api.PostAsync($"/api/assets/{win}/processes", Json(new { })))).GetProperty("id").GetString()!;
        Assert.Contains("Get-Process", (string)(await Sql("select body from assets.jobs where id = @i", ("i", pr)))!);
        var lpr = (await J(await api.PostAsync($"/api/assets/{lin}/processes", Json(new { })))).GetProperty("id").GetString()!;
        Assert.Contains("top -b", (string)(await Sql("select body from assets.jobs where id = @i", ("i", lpr)))!);

        // viewers may read the fleet page but run nothing
        var view = await TestSupport.ClientFor(factory, "view@ra.test", Pw);
        Assert.Equal(HttpStatusCode.OK, (await view.GetAsync("/api/assets/patching")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await view.PostAsync($"/api/assets/{win}/patches/check", Json(new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await view.PostAsync($"/api/assets/{win}/processes", Json(new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await view.PostAsync($"/api/assets/{win}/printserver", Json(new { action = "list" }))).StatusCode);
    }
}
