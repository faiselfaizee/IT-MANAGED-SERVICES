using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DeskAsk.Platform;
using DeskAsk.Platform.Backup;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

/// <summary>Reading backup results out of syslog lines and report mails, and job status (no database).</summary>
public class BackupParseTests
{
    [Fact]
    public void SyslogLinesFromVeeamQnapAndSynologyAreRead()
    {
        var v = BackupParse.Syslog("vbr", @"<13>1 2026-11-16T02:00:00Z vbr01 Veeam_MP - - [origin enterpriseId=""31023""] [categoryId=0 instanceId=190 JobSessionID=""c0d9"" JobID=""c441"" JobResult=""2"" JobName=""SQL nightly"" Description=""Backup job 'SQL nightly' finished with Failed. Error: no space""]")!;
        Assert.Equal(("veeam", "SQL nightly", "failed"), (v.Tool, v.Job, v.Result));
        Assert.Contains("no space", v.Message);

        var q = BackupParse.Syslog("nas", "[Hybrid Backup Sync] Failed to complete Backup job: “XXY_Backup”. Folder pairs are invalid or inaccessible. Error code: -50")!;
        Assert.Equal(("qnap", "XXY_Backup", "failed"), (q.Tool, q.Job, q.Result));
        Assert.Equal("warning", BackupParse.Syslog("nas", "[Hybrid Backup Sync] Backup job \"Backup\" completed with errors. Folder pairs are invalid or inaccessible.")!.Result);
        Assert.Equal("success", BackupParse.Syslog("nas", "[Hybrid Backup Sync] Finished Backup job: \"Daily\".")!.Result);

        var s = BackupParse.Syslog("nas", "[Network][Synology NAS 1] Failed to start backup task. (The backup service is not available.)")!;
        Assert.Equal(("synology", "Synology NAS 1", "failed"), (s.Tool, s.Job, s.Result));
        Assert.Equal("success", BackupParse.Syslog("nas", "[Local][Daily] Backup task finished successfully. [12 files]")!.Result);
        Assert.Equal("warning", BackupParse.Syslog("nas", "[C2][Office] Backup task was partially completed.")!.Result);

        Assert.Null(BackupParse.Syslog("fw", "Interface port2 went down"));
        Assert.Null(BackupParse.Syslog("nas", "[Local][Daily] Relink task started."));
    }

    [Fact]
    public void ReportMailsGiveTheResultJobAndDevice()
    {
        var v = BackupParse.Mail("vbr@acme.test", "[Failed] Nightly SQL (1 machine) 1 failed", "Veeam Backup & Replication", "")!;
        Assert.Equal(("veeam", "Nightly SQL", "failed"), (v.Tool, v.Job, v.Result));
        var va = BackupParse.Mail("agent@acme.test", "[Success] Backup Job LAPTOP-7 on LAPTOP-7", "Veeam Agent for Microsoft Windows", "")!;
        Assert.Equal(("success", "LAPTOP-7"), (va.Result, va.Device));

        var a = BackupParse.Mail("noreply@acronis.example", "Backup failed: plan 'Daily servers' on machine 'SRV01'", "", "")!;
        Assert.Equal(("acronis", "Daily servers", "failed", "SRV01"), (a.Tool, a.Job, a.Result, a.Device));

        var g = BackupParse.Mail("nas@acme.test", "RE: Hyper Backup - Office docs completed successfully 16/11/2026 02:00", "", "")!;
        Assert.Equal(("synology", "success"), (g.Tool, g.Result));
        Assert.DoesNotContain("2026", g.Job);
        Assert.Equal("warning", BackupParse.Mail("x@acme.test", "Backup completed with warnings: Files", "", "other")!.Result);
        Assert.Equal("other", BackupParse.Mail("x@acme.test", "Backup completed with warnings: Files", "", "other")!.Tool);
        Assert.Null(BackupParse.Mail("x@acme.test", "Your weekly newsletter", "Nothing about it here", ""));
    }

    [Fact]
    public void StatusAndAlerts()
    {
        var now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("success", BackupChecker.Status(true, 24, "success", now.AddHours(-20), now.AddDays(-9), 6, now));
        Assert.Equal("missed", BackupChecker.Status(true, 24, "success", now.AddHours(-31), now.AddDays(-9), 6, now));
        Assert.Equal("success", BackupChecker.Status(true, 0, "success", now.AddDays(-30), now.AddDays(-90), 6, now));   // no schedule: never missed
        Assert.Equal("waiting", BackupChecker.Status(true, 24, "", null, now.AddHours(-2), 6, now));
        Assert.Equal("missed", BackupChecker.Status(true, 24, "", null, now.AddDays(-3), 6, now));
        Assert.Equal("ignored", BackupChecker.Status(false, 24, "failed", now.AddDays(-3), now.AddDays(-3), 6, now));
        var s = new BackupSettings { FailuresBeforeAlert = 2 };
        Assert.Equal("", BackupChecker.Wanted("failed", 1, s));
        Assert.Equal("failed", BackupChecker.Wanted("failed", 2, s));
        Assert.Equal("", BackupChecker.Wanted("warning", 0, s));
        Assert.Equal("warning", BackupChecker.Wanted("warning", 0, new BackupSettings { AlertWarnings = true }));
        Assert.True(BackupMailFilter.Matches("@nas.acme.test", "Alerts@NAS.acme.test"));
        Assert.True(BackupMailFilter.Matches("*@acme.test", "vbr@acme.test"));
        Assert.False(BackupMailFilter.Matches("vbr@acme.test", "evil-vbr@acme.test"));
        Assert.False(BackupMailFilter.Matches("@acme.test", "x@notacme.test.evil"));
    }
}

/// <summary>From the agent, syslog, a report mail and the report address to jobs, alerts with mail and a ticket, and back to normal.</summary>
public class BackupTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    const string Pw = "Correct-Horse-9";
    readonly WebApplicationFactory<Program> factory;
    public BackupTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b =>
    {
        TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); b.UseSetting("Platform:Roles", "all"); b.UseSetting("Jobs:PollSeconds", "1");
    });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }
    static async Task<long> Scalar(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); return Convert.ToInt64(await c.ExecuteScalarAsync()); }
    static async Task<string> Text(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); return (await c.ExecuteScalarAsync())?.ToString() ?? ""; }
    static async Task Until(Func<Task<bool>> ok) { for (var i = 0; i < 40 && !await ok(); i++) await Task.Delay(500); }

    static JsonElement Job(JsonElement list, string name) => list.GetProperty("items").EnumerateArray().First(j => j.GetProperty("name").GetString() == name);

    [PgFact]
    public async Task ResultsFromEveryRouteBecomeJobsThatAlertAndRecover()
    {
        var api = await TestSupport.AdminClient(factory);
        await api.GetAsync("/api/backup/jobs");   // schema in place
        await Sql("truncate backup.jobs, backup.runs, backup.tools, backup.senders, backup.keys restart identity cascade; delete from core.settings where key in ('backup.settings','backup.syslog_cursor','mail.inbound')");
        await Sql("truncate mail.outbox, mail.outbox_files, mail.inbox restart identity cascade; delete from identity.users where \"Email\" like '%@bk.test'");
        FakeFetcher.Shared.Pending.Clear();

        using var smtp = new FakeSmtp();
        Assert.Equal(HttpStatusCode.OK, (await api.PutAsync("/api/platform/mail/config", Json(new { provider = "smtp", from = "desk@example.test", fromName = "DeskAsk", host = "127.0.0.1", port = smtp.Port, security = "none", user = "", password = "" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PutAsync("/api/backup/settings", Json(new { recipients = "ops@bk.test, nope", failuresBeforeAlert = 1, graceHours = 6, ticket = true, ticketPriority = "high", mailResolved = true }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PutAsync("/api/backup/settings", Json(new { recipients = "ops@bk.test", failuresBeforeAlert = 0, graceHours = 6, ticketPriority = "high" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PutAsync("/api/backup/settings", Json(new { recipients = "ops@bk.test", failuresBeforeAlert = 1, graceHours = 6, ticket = true, ticketPriority = "high", mailResolved = true }))).StatusCode);

        // an agent on a server reports Veeam and Windows Server Backup
        var acme = (await J(await api.PostAsync("/api/assets/companies", Json(new { name = "Backup Acme " + Guid.NewGuid().ToString("N")[..6] })))).GetProperty("id").GetInt32();
        var tok = (await J(await api.PostAsync("/api/assets/enrolments", Json(new { companyId = acme })))).GetProperty("token").GetString()!;
        var agent = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var host = "SRV-BK" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var en = await J(await agent.PostAsync("/agent/v1/enroll", Json(new { token = tok, platform = "windows", version = "5.1.0", inventory = new { schema = 1, hostname = host, os_family = "windows", os_name = "Windows Server 2022 Standard", serial_number = "SN-" + host } })));
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", en.GetProperty("key").GetString()); agent.DefaultRequestHeaders.Add("X-Agent-Id", en.GetProperty("agentId").GetString());
        var asset = long.Parse(en.GetProperty("assetId").GetString()!);
        var ago = DateTime.UtcNow.AddHours(-1).ToString("o");
        object Report(params object[] reports) => new { tools = new[] { "veeam", "wsb" }, reports };
        var first = Report(
            new { tool = "veeam", job = "Nightly", started = DateTime.UtcNow.AddHours(-2).ToString("o"), ended = ago, result = "failed", message = "Not enough space on the repository", bytes = 0, key = "veeam:101" },
            new { tool = "wsb", job = "Windows Server Backup", started = DateTime.UtcNow.AddHours(-3).ToString("o"), ended = ago, result = "success", message = "", bytes = 52_000_000_000L, key = "wsb:1" });
        var r1 = await agent.PostAsync("/agent/v1/backup", Json(first)); Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Equal(2, (await J(r1)).GetProperty("stored").GetInt32());
        Assert.Equal(0, (await J(await agent.PostAsync("/agent/v1/backup", Json(first)))).GetProperty("stored").GetInt32());   // the same runs again: stored once

        var jobs = await J(await api.GetAsync("/api/backup/jobs?company=" + acme));
        Assert.Equal(2, jobs.GetProperty("total").GetInt32());
        var nightly = Job(jobs, "Nightly");
        Assert.Equal("failed", nightly.GetProperty("status").GetString());
        Assert.Equal(jobs.GetProperty("items")[0].GetProperty("name").GetString(), "Nightly");   // problems first
        Assert.Equal("failed", nightly.GetProperty("alert").GetString());
        Assert.StartsWith("INC-", nightly.GetProperty("ticketNumber").GetString());
        Assert.Equal(host, nightly.GetProperty("assetName").GetString());
        Assert.Equal("success", Job(jobs, "Windows Server Backup").GetProperty("status").GetString());
        Assert.Equal(1, await Scalar("select count(*) from mail.outbox where to_addr = 'ops@bk.test' and subject like '[Backup Failed]%Nightly'"));

        var dash = await J(await api.GetAsync("/api/backup/dashboard"));
        var tile = dash.GetProperty("tiles").EnumerateArray().First(t => t.GetProperty("companyId").ValueKind == JsonValueKind.Number && t.GetProperty("companyId").GetInt32() == acme);
        Assert.Equal((1, 1, 1), (tile.GetProperty("failed").GetInt32(), tile.GetProperty("success").GetInt32(), tile.GetProperty("devicesWithTools").GetInt32()));
        var devs = await J(await api.GetAsync("/api/backup/devices"));
        Assert.Contains(devs.EnumerateArray(), d => d.GetProperty("assetId").GetInt64() == asset && d.GetProperty("tools").GetArrayLength() == 2 && d.GetProperty("jobs").GetInt32() == 2);
        var detail = await J(await api.GetAsync("/api/backup/jobs/" + nightly.GetProperty("id").GetInt64()));
        Assert.Equal(1, detail.GetProperty("runs").GetArrayLength());
        Assert.Equal(60, detail.GetProperty("runs")[0].GetProperty("minutes").GetInt32());

        // the next run succeeds: back to normal, a mail says so, the ticket gets a note
        var ticketId = await Scalar("select ticket_id from backup.jobs where name = 'Nightly'");
        await agent.PostAsync("/agent/v1/backup", Json(Report(new { tool = "veeam", job = "Nightly", ended = DateTime.UtcNow.ToString("o"), result = "success", message = "", bytes = 0, key = "veeam:102" })));
        nightly = Job(await J(await api.GetAsync("/api/backup/jobs?company=" + acme)), "Nightly");
        Assert.Equal(("success", ""), (nightly.GetProperty("status").GetString(), nightly.GetProperty("alert").GetString()));
        Assert.Equal(1, await Scalar("select count(*) from mail.outbox where to_addr = 'ops@bk.test' and subject like '[Backup OK]%Nightly'"));
        Assert.Contains("back to normal", await Text($"select string_agg(body, ' ') from servicedesk.comments where ticket_id = {ticketId}"));

        // nothing for longer than the interval plus grace: missed
        await Sql("update backup.jobs set every_hours = 1, last_at = now() - interval '10 hours' where name = 'Windows Server Backup'");
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync("/api/backup/check", null)).StatusCode);
        var wsb = Job(await J(await api.GetAsync("/api/backup/jobs?company=" + acme)), "Windows Server Backup");
        Assert.Equal(("missed", "missed"), (wsb.GetProperty("status").GetString(), wsb.GetProperty("alert").GetString()));
        // not watched: no status, the alert goes quietly
        Assert.Equal(HttpStatusCode.OK, (await api.PutAsync("/api/backup/jobs/" + wsb.GetProperty("id").GetInt64(), Json(new { watched = false }))).StatusCode);
        wsb = Job(await J(await api.GetAsync("/api/backup/jobs?company=" + acme)), "Windows Server Backup");
        Assert.Equal(("ignored", ""), (wsb.GetProperty("status").GetString(), wsb.GetProperty("alert").GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PutAsync("/api/backup/jobs/" + wsb.GetProperty("id").GetInt64(), Json(new { everyHours = 9999 }))).StatusCode);

        // a syslog line from a Synology NAS the site controller stored for this device
        await Sql($"insert into assets.events (asset_id, kind, severity, source, event_id, text) values ({asset}, 'syslog', 6, 'nas', 0, '[Network][Offsite] Backup task finished successfully.')");
        await api.PostAsync("/api/backup/check", null);
        var offsite = Job(await J(await api.GetAsync("/api/backup/jobs?company=" + acme)), "Offsite");
        Assert.Equal(("synology", "success", "syslog"), (offsite.GetProperty("tool").GetString(), offsite.GetProperty("status").GetString(), offsite.GetProperty("source").GetString()));

        // the report address: a key per customer, shown once
        var key = await J(await api.PostAsync("/api/backup/keys", Json(new { name = "Acme scripts", companyId = acme })));
        var token = key.GetProperty("token").GetString()!;
        Assert.DoesNotContain(token, await Text("select string_agg(hash || prefix, ' ') from backup.keys"));
        var anon = factory.CreateClient();
        HttpRequestMessage Post(string tk, object body) { var m = new HttpRequestMessage(HttpMethod.Post, "/api/public/backup/report") { Content = Json(body) }; m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tk); return m; }
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.SendAsync(Post("dbk_wrong", new { job = "x", result = "failed" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.SendAsync(Post(token, new { job = "x", result = "maybe" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.SendAsync(Post(token, new { job = "Robocopy to USB", result = "ok", device = host, tool = "other", key = "r1" }))).StatusCode);
        var robo = Job(await J(await api.GetAsync("/api/backup/jobs?company=" + acme)), "Robocopy to USB");
        Assert.Equal(("success", host, "api"), (robo.GetProperty("status").GetString(), robo.GetProperty("assetName").GetString(), robo.GetProperty("source").GetString()));

        // report mail from a listed sender: a job, never a ticket; mail from anyone else still reaches the Service Desk
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/backup/senders", Json(new { pattern = "not a sender" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync("/api/backup/senders", Json(new { pattern = "@nas.bk.test", companyId = acme, tool = "qnap" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PutAsync("/api/platform/mail/inbound", Json(new { provider = "imap", host = "imap.example.test", port = 993, user = "support@bk.test", password = "p4ss-word" }))).StatusCode);
        var tickets = await Scalar("select count(*) from servicedesk.tickets");
        FakeFetcher.Shared.Pending.Add(new InboundMail("bk-mail-1@nas", "alerts@nas.bk.test", "NAS", "[Hybrid Backup Sync] Backup job \"Daily\" completed with errors.", "Folder pairs are invalid.", DateTimeOffset.UtcNow));
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync("/api/platform/mail/inbound/fetch", null)).StatusCode);
        await Until(async () => await Scalar("select count(*) from backup.jobs where name = 'Daily'") > 0);
        var daily = Job(await J(await api.GetAsync("/api/backup/jobs?company=" + acme)), "Daily");
        Assert.Equal(("qnap", "warning", "mail"), (daily.GetProperty("tool").GetString(), daily.GetProperty("status").GetString(), daily.GetProperty("source").GetString()));
        Assert.Equal("backup", await Text("select ticket_number from mail.inbox where message_id = 'bk-mail-1@nas'"));
        Assert.Equal(tickets, await Scalar("select count(*) from servicedesk.tickets"));

        // access: a viewer reads but cannot change; nobody signed out reads anything
        Assert.Equal(HttpStatusCode.Created, (await api.PostAsync("/api/platform/users", Json(new { email = "view@bk.test", name = "Viewer", role = "viewer", password = Pw }))).StatusCode);
        var viewer = await TestSupport.ClientFor(factory, "view@bk.test", Pw);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/backup/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsync("/api/backup/jobs/" + robo.GetProperty("id").GetInt64(), Json(new { watched = false }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/backup/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/backup/jobs")).StatusCode);
    }

    [PgFact]
    public async Task AFilePlanReachesItsAgentWithItsSecretsAndRestoresAreHandedOver()
    {
        var api = await TestSupport.AdminClient(factory);
        await api.GetAsync("/api/backup/plans");
        await Sql("truncate backup.plans, backup.snapshots, backup.restores restart identity cascade; delete from identity.users where \"Email\" like '%@bkp.test'");
        var acme = (await J(await api.PostAsync("/api/assets/companies", Json(new { name = "Plan Acme " + Guid.NewGuid().ToString("N")[..6] })))).GetProperty("id").GetInt32();
        var tok = (await J(await api.PostAsync("/api/assets/enrolments", Json(new { companyId = acme })))).GetProperty("token").GetString()!;
        var agent = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var host = "PC-FB" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var en = await J(await agent.PostAsync("/agent/v1/enroll", Json(new { token = tok, platform = "windows", version = "5.2.0", inventory = new { schema = 1, hostname = host, os_family = "windows", os_name = "Windows 11 Pro", serial_number = "SN-" + host } })));
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", en.GetProperty("key").GetString()); agent.DefaultRequestHeaders.Add("X-Agent-Id", en.GetProperty("agentId").GetString());
        var asset = long.Parse(en.GetProperty("assetId").GetString()!);
        var agentId = en.GetProperty("agentId").GetString();

        // checks: folders, storage
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/backup/plans", Json(new { name = "Docs", assetId = asset, paths = Array.Empty<string>(), storage = new { kind = "local", path = "E:\\" } }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/backup/plans", Json(new { name = "Docs", assetId = asset, paths = new[] { "C:\\Users" }, storage = new { kind = "smb", path = "nas\\backup" } }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/backup/plans", Json(new { name = "Docs", assetId = asset, paths = new[] { "C:\\Users" }, storage = new { kind = "s3", bucket = "acme-backup", accessKey = "AK" } }))).StatusCode);
        var created = await api.PostAsync("/api/backup/plans", Json(new { name = "Documents", assetId = asset, paths = "C:\\Users\nD:\\Data", excludes = new[] { "*.iso" }, everyHours = 24, atHour = 21, keepLast = 14, keepDays = 30,
            storage = new { kind = "smb", path = "\\\\nas01\\backup", user = "ACME\\svc-backup", password = "Sh4re-pass" } }));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var planId = (await J(created)).GetProperty("id").GetInt64();
        Assert.DoesNotContain("Sh4re-pass", await Text("select storage_sealed || password_sealed from backup.plans"));
        Assert.Equal(1, await Scalar($"select count(*) from assets.agent_q where agent_id = '{agentId}' and kind = 'backupsync'"));
        var list = await J(await api.GetAsync("/api/backup/plans?asset=" + asset));
        Assert.Equal("\\\\nas01\\backup", list[0].GetProperty("storageWhere").GetString());
        Assert.DoesNotContain("Sh4re-pass", list.ToString());

        // the agent gets its plan with the secrets and the backup password
        var plans = await J(await agent.GetAsync("/agent/v1/backup/plans"));
        var p0 = plans[0];
        Assert.Equal(("Documents", 2, "Sh4re-pass", 21), (p0.GetProperty("name").GetString(), p0.GetProperty("paths").GetArrayLength(), p0.GetProperty("storage").GetProperty("password").GetString(), p0.GetProperty("atHour").GetInt32()));
        var password = p0.GetProperty("password").GetString()!;
        Assert.True(password.Length >= 30);

        // changing the plan without the share password keeps it
        Assert.Equal(HttpStatusCode.OK, (await api.PutAsync("/api/backup/plans/" + planId, Json(new { name = "Documents", paths = new[] { "C:\\Users" }, everyHours = 24, atHour = 22, keepLast = 14, keepDays = 30,
            storage = new { kind = "smb", path = "\\\\nas01\\backup", user = "ACME\\svc-backup", password = "" } }))).StatusCode);
        Assert.Equal("Sh4re-pass", (await J(await agent.GetAsync("/agent/v1/backup/plans")))[0].GetProperty("storage").GetProperty("password").GetString());

        // a run and its snapshot; the job on the dashboard is the plan's
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/backup/plans/{planId}/run", null)).StatusCode);
        Assert.Equal(1, await Scalar($"select count(*) from assets.agent_q where agent_id = '{agentId}' and kind = 'backuprun' and payload like '%\"{planId}\"%'"));
        await agent.PostAsync("/agent/v1/backup", Json(new { tools = new[] { "deskask" }, reports = new[] { new { tool = "deskask", job = "Documents", ended = DateTime.UtcNow.ToString("o"), result = "success", message = "120 files", bytes = 1000, key = "deskask:S1" } } }));
        Assert.Equal(HttpStatusCode.OK, (await agent.PostAsync("/agent/v1/backup/snapshot", Json(new { plan = planId, id = "20261116T210000.000000Z-aa", at = DateTime.UtcNow.ToString("o"), files = 120, bytes = 1000, newBytes = 600, warnings = 0, removed = Array.Empty<string>() }))).StatusCode);
        var detail = await J(await api.GetAsync($"/api/backup/plans/{planId}"));
        Assert.Equal(1, detail.GetProperty("snapshots").GetArrayLength());
        Assert.Equal("success", detail.GetProperty("plan").GetProperty("status").GetString());

        // a restore: refused without a target unless replacing is confirmed; the agent takes it once and reports
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/backup/plans/{planId}/restore", Json(new { snapshot = "20261116T210000.000000Z-aa" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/backup/plans/{planId}/restore", Json(new { snapshot = "nope", target = "D:\\Restore" }))).StatusCode);
        var rid = (await J(await api.PostAsync($"/api/backup/plans/{planId}/restore", Json(new { snapshot = "20261116T210000.000000Z-aa", include = "C:\\Users\\pat\\Documents", target = "D:\\Restore" })))).GetProperty("id").GetInt64();
        var job = await J(await agent.GetAsync("/agent/v1/backup/restore?id=" + rid));
        Assert.Equal(("D:\\Restore", "Sh4re-pass"), (job.GetProperty("target").GetString(), job.GetProperty("plan").GetProperty("storage").GetProperty("password").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await agent.GetAsync("/agent/v1/backup/restore?id=" + rid)).StatusCode);   // only once
        await agent.PostAsync("/agent/v1/backup/restore?id=" + rid, Json(new { ok = true, message = "12 files restored", files = 12, bytes = 3000 }));
        var restores = (await J(await api.GetAsync($"/api/backup/plans/{planId}"))).GetProperty("restores");
        Assert.Equal(("done", 12), (restores[0].GetProperty("state").GetString(), restores[0].GetProperty("files").GetInt32()));

        // the password is shown to an administrator and recorded; a viewer cannot see it, change plans or restore
        var shown = await J(await api.PostAsync($"/api/backup/plans/{planId}/password", null));
        Assert.Equal((password, "deskask-plan-" + planId), (shown.GetProperty("password").GetString(), shown.GetProperty("folder").GetString()));
        Assert.Equal(1, await Scalar("select count(*) from core.audit where action = 'backup.password_shown' and target = 'Documents' and at > now() - interval '5 minutes'"));
        Assert.Equal(HttpStatusCode.Created, (await api.PostAsync("/api/platform/users", Json(new { email = "view@bkp.test", name = "Viewer", role = "viewer", password = Pw }))).StatusCode);
        var viewer = await TestSupport.ClientFor(factory, "view@bkp.test", Pw);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/backup/plans")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"/api/backup/plans/{planId}/password", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"/api/backup/plans/{planId}/restore", Json(new { snapshot = "20261116T210000.000000Z-aa", target = "D:\\X" }))).StatusCode);
        // another agent never sees this plan
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/agent/v1/backup/plans")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await api.DeleteAsync($"/api/backup/plans/{planId}")).StatusCode);
        Assert.Equal(0, (await J(await agent.GetAsync("/agent/v1/backup/plans"))).GetArrayLength());
    }
}
