using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

public class ChangesTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    const string Pw = "Long-Enough-1";
    readonly WebApplicationFactory<Program> factory;
    public ChangesTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }
    static Task<HttpResponseMessage> Patch(HttpClient c, string url, object body) => c.SendAsync(new HttpRequestMessage(HttpMethod.Patch, url) { Content = Json(body) });

    sealed record Ctx(HttpClient Admin, HttpClient Eng, HttpClient Eng2, HttpClient Viewer, HttpClient Cust);

    async Task<Ctx> Start()
    {
        var admin = await TestSupport.AdminClient(factory);
        await Sql("truncate servicedesk.changes, servicedesk.problems, servicedesk.tickets, servicedesk.item_notes, servicedesk.events restart identity cascade");
        await Sql("delete from core.settings where key like 'servicedesk.cab'");
        await Sql("delete from identity.users where \"Email\" like '%@chg.test'");
        foreach (var (e, role) in new[] { ("eng1", "engineer"), ("eng2", "engineer"), ("view", "viewer"), ("cust", "customer") })
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync("/api/platform/users", Json(new { email = e + "@chg.test", name = e, role, password = Pw }))).StatusCode);
        return new Ctx(admin, await TestSupport.ClientFor(factory, "eng1@chg.test", Pw), await TestSupport.ClientFor(factory, "eng2@chg.test", Pw),
            await TestSupport.ClientFor(factory, "view@chg.test", Pw), await TestSupport.ClientFor(factory, "cust@chg.test", Pw));
    }

    static async Task<long> NewChange(HttpClient c, object body)
    {
        var r = await c.PostAsync("/api/servicedesk/changes", Json(body));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await J(r)).GetProperty("id").GetInt64();
    }
    static readonly object Planned = new { title = "Patch the file server", type = "normal", risk = "medium", impact = "medium", plan = "Install the update at night", rollback = "Restore the snapshot" };
    static Task<HttpResponseMessage> Vote(HttpClient c, long id, string d, string? comment = null) => c.PostAsync($"/api/servicedesk/changes/{id}/vote", Json(new { decision = d, comment }));

    [PgFact]
    public async Task OnlyTheSupportTeamSeesChangesAndProblems()
    {
        var x = await Start();
        Assert.Equal(HttpStatusCode.Forbidden, (await x.Cust.GetAsync("/api/servicedesk/changes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await x.Cust.GetAsync("/api/servicedesk/problems")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await x.Viewer.GetAsync("/api/servicedesk/changes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await x.Viewer.PostAsync("/api/servicedesk/changes", Json(Planned))).StatusCode);
    }

    [PgFact]
    public async Task ANormalChangeNeedsTwoApprovalsFromOthersThanItsOwner()
    {
        var x = await Start();
        var id = await NewChange(x.Eng, Planned);
        Assert.Equal(HttpStatusCode.OK, (await x.Eng.PostAsync($"/api/servicedesk/changes/{id}/submit", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Vote(x.Eng, id, "approve")).StatusCode);
        var first = await J(await Vote(x.Eng2, id, "approve"));
        Assert.Equal("review", first.GetProperty("status").GetString());
        var second = await J(await Vote(x.Admin, id, "approve"));
        Assert.Equal("approved", second.GetProperty("status").GetString());
    }

    [PgFact]
    public async Task ARejectionNeedsAReasonAndSendsTheChangeBack()
    {
        var x = await Start();
        var id = await NewChange(x.Eng, Planned);
        await x.Eng.PostAsync($"/api/servicedesk/changes/{id}/submit", null);
        Assert.Equal(HttpStatusCode.BadRequest, (await Vote(x.Eng2, id, "reject")).StatusCode);
        Assert.Equal("rejected", (await J(await Vote(x.Eng2, id, "reject", "Too risky on a Friday"))).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await x.Eng.PostAsync($"/api/servicedesk/changes/{id}/submit", null)).StatusCode);
    }

    [PgFact]
    public async Task SubmittingNeedsAPlanAndARollback()
    {
        var x = await Start();
        var id = await NewChange(x.Eng, new { title = "No plan yet" });
        var r = await x.Eng.PostAsync($"/api/servicedesk/changes/{id}/submit", null);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("plan_required", (await J(r)).GetProperty("error").GetString());
    }

    [PgFact]
    public async Task StandardChangesArePreApprovedAndEmergencyNeedsOneAdministrator()
    {
        var x = await Start();
        var std = await NewChange(x.Eng, new { title = "Add a user", type = "standard", plan = "Use the template", rollback = "Delete the user" });
        Assert.Equal("approved", (await J(await x.Eng.PostAsync($"/api/servicedesk/changes/{std}/submit", null))).GetProperty("status").GetString());
        var em = await NewChange(x.Eng, new { title = "Firewall hotfix", type = "emergency", plan = "Apply the fix", rollback = "Revert the rule" });
        await x.Eng.PostAsync($"/api/servicedesk/changes/{em}/submit", null);
        Assert.Equal(HttpStatusCode.Forbidden, (await Vote(x.Eng2, em, "approve")).StatusCode);
        Assert.Equal("approved", (await J(await Vote(x.Admin, em, "approve"))).GetProperty("status").GetString());
    }

    [PgFact]
    public async Task AnApprovedChangeIsScheduledRunAndClosedWithAnOutcome()
    {
        var x = await Start();
        var id = await NewChange(x.Eng, new { title = "Reboot", type = "standard", plan = "p", rollback = "r" });
        await x.Eng.PostAsync($"/api/servicedesk/changes/{id}/submit", null);
        string Url(string s) => $"/api/servicedesk/changes/{id}/{s}";
        Assert.Equal(HttpStatusCode.BadRequest, (await x.Eng.PostAsync(Url("status"), Json(new { status = "scheduled" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Patch(x.Eng, $"/api/servicedesk/changes/{id}", new { startAt = "2030-01-01T22:00:00Z", endAt = "2030-01-01T23:00:00Z" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(x.Eng, $"/api/servicedesk/changes/{id}", new { endAt = "2030-01-01T21:00:00Z" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await x.Eng.PostAsync(Url("status"), Json(new { status = "scheduled" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await x.Eng.PostAsync(Url("status"), Json(new { status = "implementing" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await x.Eng.PostAsync(Url("status"), Json(new { status = "completed" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await x.Eng.PostAsync(Url("status"), Json(new { status = "completed", outcome = "Done, no rollback needed" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await x.Eng.PostAsync(Url("status"), Json(new { status = "cancelled" }))).StatusCode);
        var d = await J(await x.Eng.GetAsync($"/api/servicedesk/changes/{id}"));
        Assert.Equal("Done, no rollback needed", d.GetProperty("change").GetProperty("outcome").GetString());
    }

    [PgFact]
    public async Task AChangeMovesThroughPlanningApprovalImplementationReviewAndClose()
    {
        var x = await Start();
        var id = await NewChange(x.Eng, Planned);
        string Url(string s) => $"/api/servicedesk/changes/{id}/{s}";
        Task<JsonElement> Detail() => x.Eng.GetAsync($"/api/servicedesk/changes/{id}").ContinueWith(t => J(t.Result)).Unwrap();
        Assert.Equal("planning", (await Detail()).GetProperty("change").GetProperty("stage").GetString());

        // Planning: impact analysis and a checklist
        Assert.Equal(HttpStatusCode.OK, (await Patch(x.Eng, $"/api/servicedesk/changes/{id}", new { impactAnalysis = "File server unavailable for 20 minutes" })).StatusCode);
        var check = await J(await x.Eng.PostAsync(Url("tasks"), Json(new { stage = "planning", title = "Backup taken" })));
        Assert.Equal(HttpStatusCode.OK, (await Patch(x.Eng, Url("tasks/" + check.GetProperty("id").GetInt64()), new { done = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await x.Eng.PostAsync(Url("tasks"), Json(new { stage = "bogus", title = "x" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await x.Eng.PostAsync(Url("tasks"), Json(new { title = "" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await x.Viewer.PostAsync(Url("tasks"), Json(new { title = "x" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await x.Cust.PostAsync(Url("tasks"), Json(new { title = "x" }))).StatusCode);

        // Approval
        var step = await J(await x.Eng.PostAsync(Url("tasks"), Json(new { stage = "implementation", title = "Install the update" })));
        var stepId = step.GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await x.Eng.PostAsync(Url("submit"), null)).StatusCode);
        Assert.Equal("approval", (await Detail()).GetProperty("change").GetProperty("stage").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await x.Eng.PostAsync(Url("tasks"), Json(new { stage = "planning", title = "late" }))).StatusCode);   // checklist is closed once planning ends
        await Vote(x.Eng2, id, "approve"); await Vote(x.Admin, id, "approve");

        // Implementation: the change cannot be completed while a task is open
        await Patch(x.Eng, $"/api/servicedesk/changes/{id}", new { startAt = "2030-01-01T22:00:00Z", endAt = "2030-01-01T23:00:00Z" });
        Assert.Equal(HttpStatusCode.OK, (await x.Eng.PostAsync(Url("status"), Json(new { status = "scheduled" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await x.Eng.PostAsync(Url("status"), Json(new { status = "implementing" }))).StatusCode);
        Assert.Equal("implementation", (await Detail()).GetProperty("change").GetProperty("stage").GetString());
        var open = await x.Eng.PostAsync(Url("status"), Json(new { status = "completed", outcome = "Done" }));
        Assert.Equal(HttpStatusCode.BadRequest, open.StatusCode); Assert.Contains("tasks_open", await open.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await Patch(x.Eng, Url("tasks/" + stepId), new { done = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await x.Eng.PostAsync(Url("status"), Json(new { status = "completed", outcome = "Done" }))).StatusCode);

        // Review, then close
        Assert.Equal("review", (await Detail()).GetProperty("change").GetProperty("stage").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await x.Eng.PostAsync(Url("status"), Json(new { status = "closed" }))).StatusCode);   // needs a closure code
        Assert.Equal(HttpStatusCode.BadRequest, (await x.Eng.PostAsync(Url("status"), Json(new { status = "closed", closureCode = "successful" }))).StatusCode);   // and review notes
        Assert.Equal(HttpStatusCode.OK, (await Patch(x.Eng, $"/api/servicedesk/changes/{id}", new { reviewNotes = "Went to plan. Start the backup earlier next time." })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await x.Eng.PostAsync(Url("status"), Json(new { status = "closed", closureCode = "successful" }))).StatusCode);
        var d = (await Detail()).GetProperty("change");
        Assert.Equal("close", d.GetProperty("stage").GetString()); Assert.Equal("successful", d.GetProperty("closureCode").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(x.Eng, $"/api/servicedesk/changes/{id}", new { reviewNotes = "again" })).StatusCode);
        Assert.Equal(2, (await Detail()).GetProperty("tasks").GetArrayLength());
    }

    [PgFact]
    public async Task ProblemsNeedAWorkaroundToBeAKnownErrorAndLinkToTickets()
    {
        var x = await Start();
        var t = await J(await x.Admin.PostAsync("/api/servicedesk/tickets", Json(new { subject = "VPN drops", category = "Network" })));
        var tid = t.GetProperty("id").GetInt64();
        var pr = await J(await x.Eng.PostAsync("/api/servicedesk/problems", Json(new { title = "VPN instability", category = "Network", tickets = new[] { tid } })));
        var pid = pr.GetProperty("id").GetInt64();
        Assert.StartsWith("PRB-", pr.GetProperty("number").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(x.Eng, $"/api/servicedesk/problems/{pid}", new { status = "known_error" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Patch(x.Eng, $"/api/servicedesk/problems/{pid}", new { status = "known_error", workaround = "Reconnect with the backup profile", rootCause = "Old firmware" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(x.Eng, $"/api/servicedesk/problems/{pid}", new { status = "resolved" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Patch(x.Eng, $"/api/servicedesk/problems/{pid}", new { status = "resolved", resolution = "Firmware updated" })).StatusCode);
        var d = await J(await x.Eng.GetAsync($"/api/servicedesk/problems/{pid}"));
        Assert.Equal(1, d.GetProperty("tickets").GetArrayLength());
        var rel = await J(await x.Eng.GetAsync($"/api/servicedesk/tickets/{tid}/related"));
        Assert.Equal(1, rel.GetProperty("problems").GetArrayLength());
    }

    [PgFact]
    public async Task RepeatedTicketsInOneCategorySuggestAProblem()
    {
        var x = await Start();
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.Created, (await x.Admin.PostAsync("/api/servicedesk/tickets", Json(new { subject = "Wi-Fi slow " + i, category = "Network" }))).StatusCode);
        var s = await J(await x.Eng.GetAsync("/api/servicedesk/problems/suggestions"));
        Assert.Equal("Network", s.GetProperty("items")[0].GetProperty("category").GetString());
        Assert.Equal(3, s.GetProperty("items")[0].GetProperty("count").GetInt64());
    }
}
