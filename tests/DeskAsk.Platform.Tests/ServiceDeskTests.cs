using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeskAsk.Platform;
using DeskAsk.Platform.ServiceDesk;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

public class ServiceDeskTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    const string Pw = "Long-Enough-1";
    readonly WebApplicationFactory<Program> factory;
    public ServiceDeskTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }
    static async Task<T> Scalar<T>(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); return (T)(await c.ExecuteScalarAsync())!; }
    static Task<HttpResponseMessage> Patch(HttpClient c, string url, object body) => c.SendAsync(new HttpRequestMessage(HttpMethod.Patch, url) { Content = Json(body) });

    sealed record Ctx(HttpClient Admin, HttpClient Eng, HttpClient Viewer, HttpClient CustA, HttpClient CustB, HttpClient NoCompany, int CoA, int CoB, string EngId, string CustAId);

    async Task<Ctx> Start()
    {
        var admin = await TestSupport.AdminClient(factory);
        await Sql("truncate servicedesk.tickets, servicedesk.people, servicedesk.events restart identity cascade");
        await Sql("delete from core.settings where key like 'servicedesk.%'");
        await Sql("delete from identity.users where \"Email\" like '%@sd.test'");
        async Task<int> Company(string name)
        {
            var r = await admin.PostAsync("/api/assets/companies", Json(new { name }));
            if (r.StatusCode == HttpStatusCode.Conflict || r.StatusCode == HttpStatusCode.BadRequest)
                return await Scalar<int>("select id from assets.companies where lower(name) = lower('" + name + "')");
            return (await J(r)).GetProperty("id").GetInt32();
        }
        var a = await Company("SD Alpha"); var b = await Company("SD Beta");
        async Task<string> User(string email, string role)
        {
            var r = await admin.PostAsync("/api/platform/users", Json(new { email, name = email.Split('@')[0], role, password = Pw }));
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);
            return (await J(r)).GetProperty("id").GetString()!;
        }
        var eng = await User("eng@sd.test", "engineer"); await User("view@sd.test", "viewer");
        var ca = await User("a@sd.test", "customer"); var cb = await User("b@sd.test", "cadmin"); await User("none@sd.test", "customer");
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/servicedesk/people/" + ca, Json(new { companyId = a }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/servicedesk/people/" + cb, Json(new { companyId = b }))).StatusCode);
        return new Ctx(admin, await TestSupport.ClientFor(factory, "eng@sd.test", Pw), await TestSupport.ClientFor(factory, "view@sd.test", Pw),
            await TestSupport.ClientFor(factory, "a@sd.test", Pw), await TestSupport.ClientFor(factory, "b@sd.test", Pw), await TestSupport.ClientFor(factory, "none@sd.test", Pw), a, b, eng, ca);
    }

    static async Task<long> Open(HttpClient c, object body)
    {
        var r = await c.PostAsync("/api/servicedesk/tickets", Json(body));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await J(r)).GetProperty("id").GetInt64();
    }
    static async Task<JsonElement> Get(HttpClient c, long id) { var r = await c.GetAsync("/api/servicedesk/tickets/" + id); Assert.Equal(HttpStatusCode.OK, r.StatusCode); return await J(r); }
    static Task<HttpResponseMessage> Status(HttpClient c, long id, string to, string? note = null) => c.PostAsync("/api/servicedesk/tickets/" + id + "/status", Json(new { status = to, note }));
    static Task<HttpResponseMessage> Comment(HttpClient c, long id, string body, bool @internal = false) => c.PostAsync("/api/servicedesk/tickets/" + id + "/comments", Json(new { body, @internal }));

    [PgFact]
    public async Task AnonymousCallersAreRefused()
    {
        var c = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/servicedesk/tickets")).StatusCode);
    }

    [PgFact]
    public async Task StaffOpenATicketWithANumberAndRunningClocks()
    {
        var x = await Start();
        var r = await x.Admin.PostAsync("/api/servicedesk/tickets", Json(new { subject = "Printer down", description = "Third floor", priority = "critical", category = "Hardware", companyId = x.CoA, requesterName = "Pat", requesterEmail = "pat@alpha.test" }));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var made = await J(r);
        Assert.Matches(new Regex("^TKT-\\d{6}$"), made.GetProperty("number").GetString()!);
        var t = (await Get(x.Admin, made.GetProperty("id").GetInt64())).GetProperty("ticket");
        Assert.Equal("open", t.GetProperty("status").GetString()); Assert.Equal("SD Alpha", t.GetProperty("company").GetString()); Assert.Equal("Pat", t.GetProperty("requester").GetString());
        var created = t.GetProperty("createdAt").GetDateTime();
        Assert.InRange((t.GetProperty("respondDue").GetDateTime() - created).TotalMinutes, 59, 61);    // critical: first reply within 1 hour
        Assert.InRange((t.GetProperty("resolveDue").GetDateTime() - created).TotalMinutes, 239, 241);  // and resolved within 4 hours
        Assert.Equal("ok", t.GetProperty("sla").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await x.Admin.PostAsync("/api/servicedesk/tickets", Json(new { description = "no subject" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await x.Admin.PostAsync("/api/servicedesk/tickets", Json(new { subject = "s", priority = "urgent" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await x.Admin.PostAsync("/api/servicedesk/tickets", Json(new { subject = "s", category = "Pizza" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await x.Admin.PostAsync("/api/servicedesk/tickets", Json(new { subject = "s", companyId = 987654 }))).StatusCode);

        await Open(x.Eng, new { subject = "Slow wifi", priority = "low", companyId = x.CoB });
        var all = await J(await x.Admin.GetAsync("/api/servicedesk/tickets"));
        Assert.Equal(2, all.GetProperty("total").GetInt64());
        var onlyA = await J(await x.Admin.GetAsync("/api/servicedesk/tickets?company=" + x.CoA));
        Assert.Equal(1, onlyA.GetProperty("total").GetInt64());
        Assert.Equal(1, (await J(await x.Admin.GetAsync("/api/servicedesk/tickets?q=wifi"))).GetProperty("total").GetInt64());
        Assert.Equal(1, (await J(await x.Admin.GetAsync("/api/servicedesk/tickets?priority=critical"))).GetProperty("total").GetInt64());
        Assert.Equal(HttpStatusCode.BadRequest, (await x.Admin.GetAsync("/api/servicedesk/tickets?priority=nope")).StatusCode);
        var sum = await J(await x.Admin.GetAsync("/api/servicedesk/summary"));
        Assert.Equal(2, sum.GetProperty("open").GetInt64()); Assert.Equal(2, sum.GetProperty("unassigned").GetInt64()); Assert.Equal(1, sum.GetProperty("critical").GetInt64());
    }

    [PgFact]
    public async Task CustomersSeeOnlyTheirCompanyAndNeverInternalNotes()
    {
        var x = await Start();
        var mine = await Open(x.CustA, new { subject = "Cannot log in", companyId = x.CoB, priority = "high" });   // the company in the request is ignored
        var t = (await Get(x.Admin, mine)).GetProperty("ticket");
        Assert.Equal(x.CoA, t.GetProperty("companyId").GetInt32()); Assert.Equal(x.CustAId, t.GetProperty("requesterId").GetString());
        var other = await Open(x.CustB, new { subject = "Beta's ticket" });

        Assert.Equal(HttpStatusCode.NotFound, (await x.CustA.GetAsync("/api/servicedesk/tickets/" + other)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await x.CustB.GetAsync("/api/servicedesk/tickets/" + mine)).StatusCode);
        Assert.Equal(1, (await J(await x.CustA.GetAsync("/api/servicedesk/tickets"))).GetProperty("total").GetInt64());
        Assert.Equal(2, (await J(await x.Admin.GetAsync("/api/servicedesk/tickets"))).GetProperty("total").GetInt64());
        Assert.Equal(HttpStatusCode.NotFound, (await Comment(x.CustA, other, "sneaky")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Comment(x.Eng, mine, "Looking into it")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Comment(x.Eng, mine, "Probably the VPN again", true)).StatusCode);
        var staffView = await Get(x.Eng, mine); var custView = await Get(x.CustA, mine);
        Assert.Contains(staffView.GetProperty("comments").EnumerateArray(), c => c.GetProperty("kind").GetString() == "internal");
        Assert.DoesNotContain(custView.GetProperty("comments").EnumerateArray(), c => c.GetProperty("kind").GetString() == "internal");
        Assert.Contains(custView.GetProperty("comments").EnumerateArray(), c => c.GetProperty("body").GetString() == "Looking into it");

        Assert.Equal(HttpStatusCode.Forbidden, (await Comment(x.CustA, mine, "my own note", true)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Patch(x.CustA, "/api/servicedesk/tickets/" + mine, new { priority = "low" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Status(x.CustA, mine, "progress")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await x.CustA.GetAsync("/api/servicedesk/staff")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await x.CustA.PutAsync("/api/servicedesk/settings", Json(new { categories = new[] { "X" } }))).StatusCode);

        // a customer who belongs to no company can neither see nor open anything
        Assert.Equal(HttpStatusCode.Forbidden, (await x.NoCompany.PostAsync("/api/servicedesk/tickets", Json(new { subject = "hello" }))).StatusCode);
        Assert.Equal(0, (await J(await x.NoCompany.GetAsync("/api/servicedesk/tickets"))).GetProperty("total").GetInt64());

        var me = await J(await x.CustA.GetAsync("/api/servicedesk/me"));
        Assert.Equal("SD Alpha", me.GetProperty("company").GetString()); Assert.True(me.GetProperty("canRaise").GetBoolean()); Assert.False(me.GetProperty("staff").GetBoolean());
        Assert.False((await J(await x.NoCompany.GetAsync("/api/servicedesk/me"))).GetProperty("canRaise").GetBoolean());

        // a customer may be limited to reading
        Assert.Equal(HttpStatusCode.OK, (await x.Admin.PutAsync("/api/servicedesk/people/" + x.CustAId, Json(new { companyId = x.CoA, mayRaise = false, mayModify = false }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await x.CustA.PostAsync("/api/servicedesk/tickets", Json(new { subject = "again" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Comment(x.CustA, mine, "reply")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await x.CustA.GetAsync("/api/servicedesk/tickets/" + mine)).StatusCode);
    }

    [PgFact]
    public async Task ViewersReadButChangeNothing()
    {
        var x = await Start();
        var id = await Open(x.Admin, new { subject = "For viewing" });
        Assert.Equal(1, (await J(await x.Viewer.GetAsync("/api/servicedesk/tickets"))).GetProperty("total").GetInt64());
        Assert.Equal(HttpStatusCode.OK, (await x.Viewer.GetAsync("/api/servicedesk/tickets/" + id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await x.Viewer.PostAsync("/api/servicedesk/tickets", Json(new { subject = "no" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Comment(x.Viewer, id, "no")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Status(x.Viewer, id, "progress")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Patch(x.Viewer, "/api/servicedesk/tickets/" + id, new { priority = "low" })).StatusCode);
        Assert.True((await J(await x.Viewer.GetAsync("/api/servicedesk/me"))).GetProperty("readOnly").GetBoolean());
    }

    [PgFact]
    public async Task StatusFlowPausesTheClockWhileWaitingForTheCustomer()
    {
        var x = await Start();
        var id = await Open(x.CustA, new { subject = "Needs info", priority = "medium" });
        var before = (await Get(x.Admin, id)).GetProperty("ticket").GetProperty("resolveDue").GetDateTime();

        Assert.Equal(HttpStatusCode.OK, (await Status(x.Eng, id, "hold", "Which printer?")).StatusCode);
        var held = (await Get(x.Admin, id)).GetProperty("ticket");
        Assert.True(held.GetProperty("paused").GetBoolean()); Assert.Equal("paused", held.GetProperty("sla").GetString());
        Assert.Contains((await Get(x.CustA, id)).GetProperty("comments").EnumerateArray(), c => c.GetProperty("body").GetString() == "Which printer?");

        await Sql("update servicedesk.tickets set paused_at = now() - interval '2 hours' where id = " + id);
        Assert.Equal(HttpStatusCode.OK, (await Status(x.Eng, id, "progress")).StatusCode);
        var after = (await Get(x.Admin, id)).GetProperty("ticket");
        Assert.False(after.GetProperty("paused").GetBoolean());
        Assert.InRange((after.GetProperty("resolveDue").GetDateTime() - before).TotalMinutes, 119, 121);   // two hours of waiting do not count

        // the customer answering a ticket that waits for them puts it back to open
        await Status(x.Eng, id, "hold");
        Assert.Equal(HttpStatusCode.OK, (await Comment(x.CustA, id, "The one on floor 3")).StatusCode);
        Assert.Equal("open", (await Get(x.Admin, id)).GetProperty("ticket").GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.OK, (await Status(x.Eng, id, "resolved")).StatusCode);
        var res = (await Get(x.Admin, id)).GetProperty("ticket");
        Assert.NotEqual(JsonValueKind.Null, res.GetProperty("resolvedAt").ValueKind); Assert.Equal("met", res.GetProperty("sla").GetString());
        Assert.Equal(HttpStatusCode.OK, (await Status(x.CustA, id, "open")).StatusCode);     // the customer says it is not fixed
        Assert.Equal(JsonValueKind.Null, (await Get(x.Admin, id)).GetProperty("ticket").GetProperty("resolvedAt").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await Status(x.CustA, id, "closed")).StatusCode);   // or closes it
        Assert.Equal(HttpStatusCode.Conflict, (await Comment(x.CustA, id, "one more thing")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Comment(x.Eng, id, "Noted")).StatusCode);       // staff may still write on a closed ticket
        Assert.Equal(HttpStatusCode.BadRequest, (await Status(x.Eng, id, "sleeping")).StatusCode);
        var log = (await Get(x.Admin, id)).GetProperty("comments").EnumerateArray().Where(c => c.GetProperty("kind").GetString() == "system").Select(c => c.GetProperty("body").GetString()).ToList();
        Assert.Contains(log, l => l!.Contains("open → hold")); Assert.Contains(log, l => l!.Contains("resolved → open"));
    }

    [PgFact]
    public async Task OnlyAStaffReplyCountsAsTheFirstResponse()
    {
        var x = await Start();
        var id = await Open(x.CustA, new { subject = "Hello?" });
        await Comment(x.CustA, id, "Anyone there?");
        await Comment(x.Eng, id, "Note to self", true);
        Assert.Equal(JsonValueKind.Null, (await Get(x.Admin, id)).GetProperty("ticket").GetProperty("firstResponseAt").ValueKind);
        await Comment(x.Eng, id, "Hi, on it");
        Assert.NotEqual(JsonValueKind.Null, (await Get(x.Admin, id)).GetProperty("ticket").GetProperty("firstResponseAt").ValueKind);
    }

    [PgFact]
    public async Task AssigningChecksThePersonAndPriorityMovesTheDueDates()
    {
        var x = await Start();
        var id = await Open(x.Admin, new { subject = "Triage me", priority = "low" });
        var low = (await Get(x.Admin, id)).GetProperty("ticket");
        Assert.Equal(HttpStatusCode.OK, (await Patch(x.Admin, "/api/servicedesk/tickets/" + id, new { assigneeId = x.EngId })).StatusCode);
        Assert.Equal("eng", (await Get(x.Admin, id)).GetProperty("ticket").GetProperty("assignee").GetString());
        Assert.Equal(1, (await J(await x.Eng.GetAsync("/api/servicedesk/tickets?assignee=me"))).GetProperty("total").GetInt64());
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(x.Admin, "/api/servicedesk/tickets/" + id, new { assigneeId = x.CustAId })).StatusCode);   // a customer cannot be the assignee
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(x.Admin, "/api/servicedesk/tickets/" + id, new { assigneeId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Patch(x.Admin, "/api/servicedesk/tickets/" + id, new { priority = "critical" })).StatusCode);
        var crit = (await Get(x.Admin, id)).GetProperty("ticket");
        Assert.InRange((crit.GetProperty("resolveDue").GetDateTime() - crit.GetProperty("createdAt").GetDateTime()).TotalMinutes, 239, 241);
        Assert.True(crit.GetProperty("resolveDue").GetDateTime() < low.GetProperty("resolveDue").GetDateTime());
        Assert.Equal(HttpStatusCode.OK, (await Patch(x.Admin, "/api/servicedesk/tickets/" + id, new { assigneeId = (string?)null })).StatusCode);
        Assert.Equal("", (await Get(x.Admin, id)).GetProperty("ticket").GetProperty("assignee").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(x.Admin, "/api/servicedesk/tickets/" + id, new { subject = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Patch(x.Admin, "/api/servicedesk/tickets/999999", new { subject = "x" })).StatusCode);
        var staff = await J(await x.Eng.GetAsync("/api/servicedesk/staff"));
        Assert.Contains(staff.EnumerateArray(), s => s.GetProperty("name").GetString() == "eng");
        Assert.DoesNotContain(staff.EnumerateArray(), s => s.GetProperty("name").GetString() == "a");
    }

    [PgFact]
    public async Task TheSlaJobRecordsWarningsAndMissesOnce()
    {
        var x = await Start();
        var late = await Open(x.Admin, new { subject = "Missed everything", priority = "medium" });
        var near = await Open(x.Admin, new { subject = "Nearly late", priority = "medium" });
        var waiting = await Open(x.Admin, new { subject = "Waiting on customer", priority = "medium" });
        var fine = await Open(x.Admin, new { subject = "Plenty of time", priority = "medium" });
        await Sql($"update servicedesk.tickets set respond_due = now() - interval '2 hours', resolve_due = now() - interval '1 minute' where id = {late}");
        await Sql($"update servicedesk.tickets set created_at = now() - interval '20 hours', respond_due = now() - interval '16 hours', first_response_at = now() - interval '19 hours', resolve_due = now() + interval '4 hours' where id = {near}");
        await Sql($"update servicedesk.tickets set status = 'hold', paused_at = now() - interval '30 hours', respond_due = now() - interval '1 hour', resolve_due = now() - interval '1 hour' where id = {waiting}");

        var job = new SlaJob(); var tenant = new TenantInfo("t", "t", "h", Pg);
        await job.RunAsync(new JobContext(tenant, 1, "", 1, null!), CancellationToken.None);
        await job.RunAsync(new JobContext(tenant, 2, "", 1, null!), CancellationToken.None);   // a second run adds nothing

        Assert.Equal(2, await Scalar<int>($"select sla_warned from servicedesk.tickets where id = {late}"));
        Assert.True(await Scalar<bool>($"select resp_flag from servicedesk.tickets where id = {late}"));
        Assert.Equal(1, await Scalar<int>($"select sla_warned from servicedesk.tickets where id = {near}"));
        Assert.False(await Scalar<bool>($"select resp_flag from servicedesk.tickets where id = {near}"));     // it was answered
        Assert.Equal(0, await Scalar<int>($"select sla_warned from servicedesk.tickets where id = {waiting}")); // the clock is stopped
        Assert.Equal(0, await Scalar<int>($"select sla_warned from servicedesk.tickets where id = {fine}"));
        Assert.Equal(2, await Scalar<int>($"select count(*)::int from servicedesk.comments where ticket_id = {late} and author_name = 'SLA'"));
        Assert.Equal(1, await Scalar<int>($"select count(*)::int from servicedesk.comments where ticket_id = {near} and author_name = 'SLA'"));
        Assert.Equal(1, await Scalar<int>("select count(*)::int from servicedesk.events where kind = 'sla.breached'"));
        Assert.Equal("breached", (await Get(x.Admin, late)).GetProperty("ticket").GetProperty("sla").GetString());
        Assert.Equal("warning", (await Get(x.Admin, near)).GetProperty("ticket").GetProperty("sla").GetString());
        Assert.Equal(1, (await J(await x.Admin.GetAsync("/api/servicedesk/tickets?overdue=1"))).GetProperty("total").GetInt64());
    }

    [PgFact]
    public async Task ServiceLevelsAndCategoriesAreSetByAdministratorsAndApplyToOpenTickets()
    {
        var x = await Start();
        var s0 = await J(await x.Eng.GetAsync("/api/servicedesk/settings"));
        Assert.Contains(s0.GetProperty("categories").EnumerateArray(), c => c.GetString() == "Hardware");
        Assert.Equal(4, s0.GetProperty("sla").GetProperty("resolve").GetProperty("critical").GetDouble());

        Assert.Equal(HttpStatusCode.Forbidden, (await x.Eng.PutAsync("/api/servicedesk/settings", Json(new { categories = new[] { "A" } }))).StatusCode);
        object SlaBody(double respondCritical, double resolveCritical) => new { respond = new { critical = respondCritical, high = 2, medium = 4, low = 8 }, resolve = new { critical = resolveCritical, high = 8, medium = 24, low = 72 }, warnPct = 80, pause = true, escalate = true };
        object Sla(double a, double b) => new { sla = SlaBody(a, b) };
        Assert.Equal(HttpStatusCode.BadRequest, (await x.Admin.PutAsync("/api/servicedesk/settings", Json(Sla(5, 2)))).StatusCode);        // the first reply cannot take longer than the fix
        Assert.Equal(HttpStatusCode.BadRequest, (await x.Admin.PutAsync("/api/servicedesk/settings", Json(Sla(0.1, 2)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await x.Admin.PutAsync("/api/servicedesk/settings", Json(new { categories = new[] { "Dup", "dup" } }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await x.Admin.PutAsync("/api/servicedesk/settings", Json(new { categories = Array.Empty<string>() }))).StatusCode);

        var id = await Open(x.Admin, new { subject = "Critical one", priority = "critical" });
        Assert.Equal(HttpStatusCode.OK, (await x.Admin.PutAsync("/api/servicedesk/settings", Json(new { categories = new[] { "Billing", "Other" }, sla = SlaBody(1, 10) }))).StatusCode);
        var t = (await Get(x.Admin, id)).GetProperty("ticket");
        Assert.InRange((t.GetProperty("resolveDue").GetDateTime() - t.GetProperty("createdAt").GetDateTime()).TotalMinutes, 599, 601);
        Assert.Equal(HttpStatusCode.Created, (await x.Admin.PostAsync("/api/servicedesk/tickets", Json(new { subject = "Invoice question", category = "Billing" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await x.Admin.PostAsync("/api/servicedesk/tickets", Json(new { subject = "x", category = "Hardware" }))).StatusCode);    // no longer listed
        Assert.Equal(80, (await J(await x.Admin.GetAsync("/api/servicedesk/settings"))).GetProperty("sla").GetProperty("warnPct").GetInt32());
    }

    [PgFact]
    public async Task FilesAreStoredShownToTheRightPeopleAndNeverRunAsPages()
    {
        var x = await Start();
        var id = await Open(x.CustA, new { subject = "Screenshot attached" });
        var bytes = Encoding.UTF8.GetBytes("<html>hello</html>");
        async Task<HttpResponseMessage> Up(HttpClient c, string query, byte[] data)
        {
            var content = new ByteArrayContent(data); content.Headers.ContentType = new("text/html");
            return await c.PostAsync("/api/servicedesk/tickets/" + id + "/attachments" + query, content);
        }
        var ok = await Up(x.CustA, "?name=" + Uri.EscapeDataString("../../shot.html"), bytes);
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var fid = (await J(ok)).GetProperty("id").GetInt64();
        Assert.Equal("shot.html", (await J(ok)).GetProperty("name").GetString());       // folders in the name are dropped
        var dl = await x.Eng.GetAsync($"/api/servicedesk/tickets/{id}/attachments/{fid}");
        Assert.Equal(bytes, await dl.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/octet-stream", dl.Content.Headers.ContentType!.MediaType);

        Assert.Equal(HttpStatusCode.BadRequest, (await Up(x.CustA, "", bytes)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Up(x.CustA, "?name=empty.txt", Array.Empty<byte>())).StatusCode);
        Assert.Equal((HttpStatusCode)413, (await Up(x.CustA, "?name=big.bin", new byte[10 * 1024 * 1024 + 1])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Up(x.CustA, "?name=n.txt&internal=1", bytes)).StatusCode);

        var inner = await Up(x.Eng, "?name=internal.txt&internal=1", bytes);
        var iid = (await J(inner)).GetProperty("id").GetInt64();
        Assert.Equal(2, (await Get(x.Eng, id)).GetProperty("attachments").GetArrayLength());
        var custFiles = (await Get(x.CustA, id)).GetProperty("attachments");
        Assert.Equal(1, custFiles.GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await x.CustA.GetAsync($"/api/servicedesk/tickets/{id}/attachments/{iid}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await x.CustB.GetAsync($"/api/servicedesk/tickets/{id}/attachments/{fid}")).StatusCode);
    }

    [PgFact]
    public async Task TicketsCanPointAtAnAssetAndPeopleNeedARealCompany()
    {
        var x = await Start();
        var asset = await x.Admin.PostAsync("/api/assets", Json(new { name = "Reception PC" }));
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        var aj = await J(asset); var aid = aj.GetProperty("id").GetInt64();
        var id = await Open(x.Admin, new { subject = "PC will not boot", assetId = aid });
        Assert.Contains("Reception PC", (await Get(x.Admin, id)).GetProperty("ticket").GetProperty("asset").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await x.Admin.PostAsync("/api/servicedesk/tickets", Json(new { subject = "x", assetId = 99999999 }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(x.Admin, "/api/servicedesk/tickets/" + id, new { assetId = 99999999 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Patch(x.Admin, "/api/servicedesk/tickets/" + id, new { assetId = (long?)null })).StatusCode);
        Assert.Equal("", (await Get(x.Admin, id)).GetProperty("ticket").GetProperty("asset").GetString());
        // customers do not choose assets
        var cid = await Open(x.CustA, new { subject = "Mine", assetId = aid });
        Assert.Equal("", (await Get(x.Admin, cid)).GetProperty("ticket").GetProperty("asset").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await x.Admin.PutAsync("/api/servicedesk/people/" + x.CustAId, Json(new { companyId = 987654 }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await x.Admin.PutAsync("/api/servicedesk/people/" + Guid.NewGuid(), Json(new { companyId = x.CoA }))).StatusCode);
        var people = await J(await x.Admin.GetAsync("/api/servicedesk/people"));
        var a = people.EnumerateArray().Single(p => p.GetProperty("email").GetString() == "a@sd.test");
        Assert.Equal("SD Alpha", a.GetProperty("company").GetString());
        Assert.DoesNotContain(people.EnumerateArray(), p => p.GetProperty("email").GetString() == "eng@sd.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await x.Eng.GetAsync("/api/servicedesk/people")).StatusCode);
    }
}
