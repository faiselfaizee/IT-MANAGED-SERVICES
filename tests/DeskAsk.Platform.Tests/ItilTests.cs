using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

/// <summary>ITIL in the Service Desk: INC/SRV/PRB/CHG numbers, the service catalogue with approvals, the change board, calendar and standard changes, known errors.</summary>
public class ItilTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    const string Pw = "Long-Enough-1";
    readonly WebApplicationFactory<Program> factory;
    public ItilTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }
    static async Task<object?> Scalar(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); return await c.ExecuteScalarAsync(); }
    static Task<HttpResponseMessage> Patch(HttpClient c, string url, object body) => c.SendAsync(new HttpRequestMessage(HttpMethod.Patch, url) { Content = Json(body) });

    [PgFact]
    public async Task NumbersCatalogueApprovalsChangeBoardCalendarStandardChangesAndKnownErrors()
    {
        var admin = await TestSupport.AdminClient(factory);
        await admin.GetAsync("/api/servicedesk/me");
        await Sql("truncate servicedesk.changes, servicedesk.problems, servicedesk.tickets, servicedesk.item_notes, servicedesk.events, servicedesk.catalog, servicedesk.change_templates restart identity cascade");
        await Sql("delete from core.settings where key = 'servicedesk.cab'; delete from identity.users where \"Email\" like '%@itil.test'; truncate mail.outbox, mail.outbox_files restart identity");
        await Sql("alter sequence servicedesk.inc_seq restart; alter sequence servicedesk.srv_seq restart; alter sequence servicedesk.problem_seq restart");
        using var smtp = new FakeSmtp();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/platform/mail/config", Json(new { provider = "smtp", from = "desk@example.test", fromName = "DeskAsk", host = "127.0.0.1", port = smtp.Port, security = "none", user = "", password = "" }))).StatusCode);
        string Id(JsonElement e) => e.GetProperty("id").GetString()!;
        var ids = new Dictionary<string, string>();
        foreach (var (e, role) in new[] { ("eng1", "engineer"), ("eng2", "engineer"), ("eng3", "engineer") })
        {
            var r = await admin.PostAsync("/api/platform/users", Json(new { email = e + "@itil.test", name = e, role, password = Pw }));
            Assert.Equal(HttpStatusCode.Created, r.StatusCode); ids[e] = Id(await J(r));
        }
        var eng1 = await TestSupport.ClientFor(factory, "eng1@itil.test", Pw); var eng2 = await TestSupport.ClientFor(factory, "eng2@itil.test", Pw); var eng3 = await TestSupport.ClientFor(factory, "eng3@itil.test", Pw);

        // ---- numbers: INC for incidents, SRV for requests; changing the type moves it to the other series and keeps the old number as an alias
        var inc = await J(await eng1.PostAsync("/api/servicedesk/tickets", Json(new { subject = "Outlook cannot connect to the mailbox", kind = "incident" })));
        Assert.Matches(new Regex("^INC-\\d{4,}$"), inc.GetProperty("number").GetString()!);
        var srv = await J(await eng1.PostAsync("/api/servicedesk/tickets", Json(new { subject = "Install Visio", kind = "request" })));
        Assert.Equal("SRV-0001", srv.GetProperty("number").GetString());
        Assert.Equal(HttpStatusCode.OK, (await Patch(eng1, $"/api/servicedesk/tickets/{srv.GetProperty("id").GetInt64()}", new { kind = "incident" })).StatusCode);
        Assert.Equal("INC-0002", (string)(await Scalar($"select number from servicedesk.tickets where id = {srv.GetProperty("id").GetInt64()}"))!);
        Assert.Equal(1L, await Scalar($"select count(*) from servicedesk.tickets where 'SRV-0001' = any(aliases)"));
        Assert.Equal(1, (await J(await eng1.GetAsync("/api/servicedesk/tickets?q=SRV-0001"))).GetProperty("items").GetArrayLength());
        var prb = await J(await eng1.PostAsync("/api/servicedesk/problems", Json(new { title = "Outlook mailbox connection drops" })));
        Assert.Equal("PRB-0001", prb.GetProperty("number").GetString());

        // ---- the service catalogue: a form, an approver; a request waits on hold, the approver is mailed, decides, and the clock runs
        Assert.Equal(HttpStatusCode.Forbidden, (await eng1.PostAsync("/api/servicedesk/catalog", Json(new { name = "Laptop" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/servicedesk/catalog", Json(new { name = "Laptop", fields = new object[] { new { key = "Bad Key", label = "x" } } }))).StatusCode);
        var item = await J(await admin.PostAsync("/api/servicedesk/catalog", Json(new
        {
            name = "New laptop", category = "Hardware", description = "A laptop for a new or existing employee.", approvers = new[] { ids["eng2"] }, assigneeId = ids["eng3"], resolveHours = 72,
            fields = new object[] { new { key = "model", label = "Model", kind = "choice", options = new[] { "Standard", "Developer" }, required = true }, new { key = "needed_by", label = "Needed by", kind = "date" } }
        })));
        var itemId = item.GetProperty("id").GetInt32();
        var cat = await J(await eng1.GetAsync("/api/servicedesk/catalog"));
        Assert.True(cat[0].GetProperty("needsApproval").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await eng1.PostAsync($"/api/servicedesk/catalog/{itemId}/request", Json(new { answers = new { needed_by = "2026-12-01" } }))).StatusCode);   // model is needed
        Assert.Equal(HttpStatusCode.BadRequest, (await eng1.PostAsync($"/api/servicedesk/catalog/{itemId}/request", Json(new { answers = new { model = "Gaming" } }))).StatusCode);
        var req = await J(await eng1.PostAsync($"/api/servicedesk/catalog/{itemId}/request", Json(new { answers = new { model = "Developer", needed_by = "2026-12-01" }, note = "For the new hire" })));
        var rid = req.GetProperty("id").GetInt64();
        Assert.StartsWith("SRV-", req.GetProperty("number").GetString()); Assert.Equal("pending", req.GetProperty("approval").GetString());
        Assert.Equal("hold", (string)(await Scalar($"select status from servicedesk.tickets where id = {rid}"))!);
        Assert.Equal(ids["eng3"], (await Scalar($"select assignee_id::text from servicedesk.tickets where id = {rid}"))!.ToString());
        Assert.Contains("Model: Developer", (string)(await Scalar($"select description from servicedesk.tickets where id = {rid}"))!);
        Assert.Equal(1L, await Scalar("select count(*) from mail.outbox where to_addr = 'eng2@itil.test' and subject like '%Approval needed: New laptop%'"));
        Assert.Equal(1, (await J(await eng2.GetAsync("/api/servicedesk/approvals"))).GetArrayLength());
        Assert.Equal(0, (await J(await eng3.GetAsync("/api/servicedesk/approvals"))).GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await eng3.PostAsync($"/api/servicedesk/tickets/{rid}/approval", Json(new { decision = "approve" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await eng2.PostAsync($"/api/servicedesk/tickets/{rid}/approval", Json(new { decision = "reject" }))).StatusCode);   // reason needed
        Assert.Equal(HttpStatusCode.OK, (await eng2.PostAsync($"/api/servicedesk/tickets/{rid}/approval", Json(new { decision = "approve" }))).StatusCode);
        Assert.Equal("open", (string)(await Scalar($"select status from servicedesk.tickets where id = {rid}"))!);
        Assert.Equal(HttpStatusCode.BadRequest, (await eng2.PostAsync($"/api/servicedesk/tickets/{rid}/approval", Json(new { decision = "approve" }))).StatusCode);
        var req2 = (await J(await eng1.PostAsync($"/api/servicedesk/catalog/{itemId}/request", Json(new { answers = new { model = "Standard" } })))).GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await eng2.PostAsync($"/api/servicedesk/tickets/{req2}/approval", Json(new { decision = "reject", note = "Use a spare" }))).StatusCode);
        Assert.Equal("closed", (string)(await Scalar($"select status from servicedesk.tickets where id = {req2}"))!);

        // ---- the change board: only its members approve; it is mailed when a change is submitted; the quorum is kept
        await Sql("insert into core.settings (key, value, updated_at, updated_by) values ('servicedesk.cab', '{\"quorum\":1}', now(), '') on conflict (key) do update set value = excluded.value");
        Assert.Equal(HttpStatusCode.Forbidden, (await eng1.PutAsync("/api/servicedesk/cab", Json(new { members = new[] { ids["eng2"] } }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/servicedesk/cab", Json(new { members = new[] { ids["eng2"] } }))).StatusCode);
        Assert.Equal(1, (await J(await eng1.GetAsync("/api/servicedesk/cab"))).GetProperty("quorum").GetInt32());
        var start = DateTime.UtcNow.Date.AddDays(3).AddHours(20);
        var chg = (await J(await eng1.PostAsync("/api/servicedesk/changes", Json(new { title = "Upgrade the mail server", type = "normal", risk = "medium", impact = "medium", plan = "Upgrade", rollback = "Restore", companyId = (int?)null, startAt = start, endAt = start.AddHours(2) })))).GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await eng1.PostAsync($"/api/servicedesk/changes/{chg}/submit", null)).StatusCode);
        Assert.Equal(1L, await Scalar("select count(*) from mail.outbox where to_addr = 'eng2@itil.test' and subject like '%Change for approval%'"));
        Assert.Equal(0L, await Scalar("select count(*) from mail.outbox where to_addr = 'eng3@itil.test' and subject like '%Change for approval%'"));
        Assert.Equal(HttpStatusCode.Forbidden, (await eng3.PostAsync($"/api/servicedesk/changes/{chg}/vote", Json(new { decision = "approve" }))).StatusCode);
        Assert.Equal("approved", (await J(await eng2.PostAsync($"/api/servicedesk/changes/{chg}/vote", Json(new { decision = "approve" })))).GetProperty("status").GetString());

        // ---- standard changes: pre-approved and scheduled at once; the calendar shows both changes clashing on the shared server
        var asset = (await J(await admin.PostAsync("/api/assets", Json(new { name = "MAIL01 " + Guid.NewGuid().ToString("N")[..6] })))).GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/servicedesk/change-templates", Json(new { name = "Patch", plan = "x" }))).StatusCode);
        var tpl = (await J(await admin.PostAsync("/api/servicedesk/change-templates", Json(new { name = "Monthly Windows patching", plan = "Install updates, reboot", rollback = "Uninstall the update", testPlan = "Services start" })))).GetProperty("id").GetInt32();
        var std = await J(await eng1.PostAsync($"/api/servicedesk/change-templates/{tpl}/use", Json(new { startAt = start.AddHours(1).ToString("o"), endAt = start.AddHours(3).ToString("o") })));
        var stdId = std.GetProperty("id").GetInt64();
        Assert.Equal("scheduled", (string)(await Scalar($"select status from servicedesk.changes where id = {stdId}"))!);
        Assert.Equal("standard", (string)(await Scalar($"select type from servicedesk.changes where id = {stdId}"))!);
        foreach (var c in new[] { chg, stdId }) Assert.Equal(HttpStatusCode.OK, (await eng1.PutAsync($"/api/servicedesk/changes/{c}/details", Json(new { category = "Servers", assetIds = new[] { asset } }))).StatusCode);
        var calendar = await J(await eng1.GetAsync($"/api/servicedesk/changes/calendar?from={start.AddDays(-1):yyyy-MM-dd}&to={start.AddDays(2):yyyy-MM-dd}"));
        var mine = calendar.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("id").GetInt64() == chg);
        Assert.Contains(std.GetProperty("number").GetString(), mine.GetProperty("clashes").EnumerateArray().Select(x => x.GetString()));
        var det = await J(await eng1.GetAsync($"/api/servicedesk/changes/{chg}/details"));
        Assert.Equal("Servers", det.GetProperty("category").GetString()); Assert.Equal(1, det.GetProperty("assets").GetArrayLength()); Assert.Equal(1, det.GetProperty("clashes").GetArrayLength());

        // ---- problems: impact and urgency set the priority; a known error is found from the incident; resolving it closes the incidents
        var pid = prb.GetProperty("id").GetInt64(); var incId = inc.GetProperty("id").GetInt64();
        Assert.Equal("critical", (await J(await eng1.PutAsync($"/api/servicedesk/problems/{pid}/impact", Json(new { impact = "high", urgency = "high" })))).GetProperty("priority").GetString());
        Assert.Equal(HttpStatusCode.OK, (await Patch(eng1, $"/api/servicedesk/problems/{pid}", new { workaround = "Use Outlook on the web until the fix", status = "known_error" })).StatusCode);
        var kedb = await J(await eng1.GetAsync("/api/servicedesk/known-errors?q=outlook mailbox"));
        Assert.Equal("PRB-0001", kedb[0].GetProperty("number").GetString());
        var hint = await J(await eng1.GetAsync($"/api/servicedesk/tickets/{incId}/known-errors"));
        Assert.Equal(pid, hint[0].GetProperty("id").GetInt64()); Assert.False(hint[0].GetProperty("linked").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await eng1.PostAsync($"/api/servicedesk/tickets/{incId}/problem", Json(new { problemId = pid }))).StatusCode);
        var done = await J(await eng1.PostAsync($"/api/servicedesk/problems/{pid}/resolve-incidents", Json(new { })));
        Assert.Equal(1, done.GetProperty("resolved").GetInt32());
        Assert.Equal("resolved", (string)(await Scalar($"select status from servicedesk.tickets where id = {incId}"))!);
        Assert.Contains("Workaround: Use Outlook on the web", (string)(await Scalar($"select body from servicedesk.comments where ticket_id = {incId} and kind = 'public' order by id desc limit 1"))!);
    }
}
