using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

public class IntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    const string Pw = "Long-Enough-1";
    readonly WebApplicationFactory<Program> factory;
    public IntegrationTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); b.UseSetting("Platform:Roles", "all"); b.UseSetting("Jobs:PollSeconds", "1"); });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }

    static HttpRequestMessage Keyed(HttpMethod m, string url, string key, object? body = null)
    {
        var r = new HttpRequestMessage(m, url); r.Headers.Add("X-Api-Key", key);
        if (body != null) r.Content = Json(body);
        return r;
    }

    async Task<HttpClient> Admin()
    {
        var admin = await TestSupport.AdminClient(factory);
        await Sql("truncate servicedesk.tickets, servicedesk.events, servicedesk.webhook_deliveries, servicedesk.webhook_state restart identity cascade");
        await Sql("delete from servicedesk.webhooks; delete from servicedesk.api_keys");
        return admin;
    }

    static async Task<string> NewKey(HttpClient admin, string name, string scope)
    {
        var r = await admin.PostAsync("/api/servicedesk/integration/keys", Json(new { name, scope }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var key = (await J(r)).GetProperty("key").GetString()!;
        Assert.StartsWith("dk_", key);
        return key;
    }

    [PgFact]
    public async Task AnApiKeyReadsAndWritesTicketsWithinItsScope()
    {
        var admin = await Admin(); var anon = factory.CreateClient();
        var read = await NewKey(admin, "Dashboard", "read"); var write = await NewKey(admin, "Monitoring tool", "write");

        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/tickets")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.SendAsync(Keyed(HttpMethod.Get, "/api/v1/tickets", "dk_notakey"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.SendAsync(Keyed(HttpMethod.Get, "/api/v1/tickets", read))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await anon.SendAsync(Keyed(HttpMethod.Post, "/api/v1/tickets", read, new { subject = "x" }))).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await anon.SendAsync(Keyed(HttpMethod.Post, "/api/v1/tickets", write, new { subject = "" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.SendAsync(Keyed(HttpMethod.Post, "/api/v1/tickets", write, new { subject = "x", priority = "urgent" }))).StatusCode);
        var made = await anon.SendAsync(Keyed(HttpMethod.Post, "/api/v1/tickets", write, new { subject = "Disk almost full on SRV01", description = "92% used", priority = "high", requesterEmail = "alerts@tool.test" }));
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var number = (await J(made)).GetProperty("number").GetString()!;

        Assert.Equal(HttpStatusCode.OK, (await anon.SendAsync(Keyed(HttpMethod.Post, $"/api/v1/tickets/{number}/comments", write, new { body = "Cleared 20 GB." }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.SendAsync(Keyed(HttpMethod.Post, $"/api/v1/tickets/{number}/comments", write, new { body = "Only for staff", @internal = true }))).StatusCode);

        var one = await J(await anon.SendAsync(Keyed(HttpMethod.Get, $"/api/v1/tickets/{number}", read)));
        Assert.Equal("Disk almost full on SRV01", one.GetProperty("ticket").GetProperty("subject").GetString());
        Assert.Equal("api", one.GetProperty("ticket").GetProperty("source").GetString());
        Assert.Equal(2, one.GetProperty("comments").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await anon.SendAsync(Keyed(HttpMethod.Get, "/api/v1/tickets/TKT-999999", read))).StatusCode);

        var list = await J(await anon.SendAsync(Keyed(HttpMethod.Get, "/api/v1/tickets?status=active", read)));
        Assert.Equal(1, list.GetProperty("total").GetInt32());
    }

    [PgFact]
    public async Task ARevokedKeyStopsWorkingAndOnlyAdministratorsManageKeys()
    {
        var admin = await Admin(); var anon = factory.CreateClient();
        await Sql("delete from identity.users where \"Email\" like '%@int.test'");
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync("/api/platform/users", Json(new { email = "eng@int.test", name = "eng", role = "engineer", password = Pw }))).StatusCode);
        var eng = await TestSupport.ClientFor(factory, "eng@int.test", Pw);
        Assert.Equal(HttpStatusCode.Forbidden, (await eng.GetAsync("/api/servicedesk/integration/keys")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await eng.PostAsync("/api/servicedesk/integration/keys", Json(new { name = "n", scope = "read" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/servicedesk/integration/keys", Json(new { name = "", scope = "read" }))).StatusCode);

        var key = await NewKey(admin, "Temp", "read");
        var listed = await J(await admin.GetAsync("/api/servicedesk/integration/keys"));
        Assert.DoesNotContain(key, listed.GetRawText());
        var id = listed.GetProperty("items")[0].GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await anon.SendAsync(Keyed(HttpMethod.Get, "/api/v1/tickets", key))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/servicedesk/integration/keys/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.SendAsync(Keyed(HttpMethod.Get, "/api/v1/tickets", key))).StatusCode);
    }

    [PgFact]
    public async Task AWebhookReceivesSignedTicketEvents()
    {
        var admin = await Admin();
        var tcp = new TcpListener(IPAddress.Loopback, 0); tcp.Start(); var port = ((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop();
        var received = new ConcurrentQueue<(string Event, string Signature, string Body)>();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var pump = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { return; }
                using var rd = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                received.Enqueue((ctx.Request.Headers["X-DeskAsk-Event"] ?? "", ctx.Request.Headers["X-DeskAsk-Signature"] ?? "", await rd.ReadToEndAsync()));
                ctx.Response.StatusCode = 200; ctx.Response.Close();
            }
        });

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/servicedesk/integration/webhooks", Json(new { url = "ftp://x" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/servicedesk/integration/webhooks", Json(new { url = $"http://127.0.0.1:{port}/", events = "ticket.exploded" }))).StatusCode);
        var created = await J(await admin.PostAsync("/api/servicedesk/integration/webhooks", Json(new { url = $"http://127.0.0.1:{port}/", events = "ticket.created,ticket.status" })));
        var hookId = created.GetProperty("id").GetInt32(); var secret = created.GetProperty("secret").GetString()!;
        Assert.StartsWith("whsec_", secret);

        // The test message also starts the first run, which sets the starting point: older events are not sent.
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/servicedesk/integration/webhooks/{hookId}/test", null)).StatusCode);
        await Until(() => received.Any(x => x.Event == "ping"));

        var t = await J(await admin.PostAsync("/api/servicedesk/tickets", Json(new { subject = "Printer offline", priority = "low" })));
        var id = t.GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/servicedesk/tickets/{id}/comments", Json(new { body = "Looking", kind = "public" }))).StatusCode);   // not subscribed
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/servicedesk/integration/webhooks/{hookId}/test", null)).StatusCode);
        await Until(() => received.Any(x => x.Event == "ticket.created"));

        var msg = received.First(x => x.Event == "ticket.created");
        var expected = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(msg.Body))).ToLowerInvariant();
        Assert.Equal(expected, msg.Signature);
        var body = JsonDocument.Parse(msg.Body).RootElement;
        Assert.Equal("Printer offline", body.GetProperty("ticket").GetProperty("subject").GetString());
        Assert.DoesNotContain(received, x => x.Event == "ticket.comment");

        var log = await J(await admin.GetAsync($"/api/servicedesk/integration/webhooks/{hookId}/deliveries"));
        Assert.Contains(log.GetProperty("items").EnumerateArray(), d => d.GetProperty("kind").GetString() == "ticket.created" && d.GetProperty("status").GetString() == "sent");
        listener.Stop(); await pump;
    }

    [PgFact]
    public async Task AFailingWebhookIsRetriedLaterAndShowsTheError()
    {
        var admin = await Admin();
        var created = await J(await admin.PostAsync("/api/servicedesk/integration/webhooks", Json(new { url = "http://127.0.0.1:1/nothing" })));
        var hookId = created.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/servicedesk/integration/webhooks/{hookId}/test", null)).StatusCode);
        JsonElement first = default;
        await Until(async () =>
        {
            var log = await J(await admin.GetAsync($"/api/servicedesk/integration/webhooks/{hookId}/deliveries"));
            foreach (var d in log.GetProperty("items").EnumerateArray()) if (d.GetProperty("attempts").GetInt32() >= 1) { first = d.Clone(); return true; }
            return false;
        });
        Assert.Equal("pending", first.GetProperty("status").GetString());
        Assert.NotEqual("", first.GetProperty("error").GetString());
    }

    [PgFact]
    public async Task TheAssistantFindsSimilarTicketsAndDraftsAReplyFromTheirAnswer()
    {
        var admin = await Admin();
        await Sql("delete from identity.users where \"Email\" like '%@int.test'");
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync("/api/platform/users", Json(new { email = "cust@int.test", name = "cust", role = "customer", password = Pw }))).StatusCode);
        var cust = await TestSupport.ClientFor(factory, "cust@int.test", Pw);
        static async Task<long> Make(HttpClient c, object b) => (await J(await c.PostAsync("/api/servicedesk/tickets", Json(b)))).GetProperty("id").GetInt64();
        var old = await Make(admin, new { subject = "VPN cannot connect from home", description = "The VPN client fails to connect from the home network", category = "Network", priority = "medium", requesterName = "Sam Lee" });
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/servicedesk/tickets/{old}/comments", Json(new { body = "Reset the VPN profile and sign in again." }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/servicedesk/tickets/{old}/status", Json(new { status = "resolved" }))).StatusCode);
        await Make(admin, new { subject = "Printer toner", description = "Out of toner on the second floor", category = "Hardware" });
        var fresh = await Make(admin, new { subject = "VPN not connecting from home laptop", description = "VPN client fails from home, cannot work", requesterName = "Pat Doe" });

        var a = await J(await admin.GetAsync($"/api/servicedesk/tickets/{fresh}/assist"));
        var similar = a.GetProperty("similar");
        Assert.Equal(1, similar.GetArrayLength());
        Assert.Equal(old, similar[0].GetProperty("id").GetInt64());
        Assert.Contains("Reset the VPN profile", similar[0].GetProperty("answer").GetString());
        Assert.Equal("Network", a.GetProperty("suggestedCategory").GetString());
        Assert.Equal("high", a.GetProperty("suggestedPriority").GetString());
        var draft = a.GetProperty("replyDraft").GetString()!;
        Assert.StartsWith("Hello Pat,", draft); Assert.Contains("Reset the VPN profile", draft);
        Assert.Equal(HttpStatusCode.Forbidden, (await cust.GetAsync($"/api/servicedesk/tickets/{fresh}/assist")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/servicedesk/tickets/999999/assist")).StatusCode);
    }

    [PgFact]
    public async Task TheMonthlyReportCountsTicketsAndIsLimitedToStaffAndCompanyAdmins()
    {
        var admin = await Admin();
        await Sql("delete from identity.users where \"Email\" like '%@int.test'");
        foreach (var (e, role) in new[] { ("cust2", "customer"), ("view2", "viewer") })
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync("/api/platform/users", Json(new { email = e + "@int.test", name = e, role, password = Pw }))).StatusCode);
        static async Task<long> Make(HttpClient c, object b) => (await J(await c.PostAsync("/api/servicedesk/tickets", Json(b)))).GetProperty("id").GetInt64();
        var a = await Make(admin, new { subject = "Mail not syncing", category = "Email", priority = "high", requesterName = "Sam" });
        await Make(admin, new { subject = "Printer jam", category = "Hardware", requesterName = "Sam" });
        await Make(admin, new { subject = "Need a monitor", category = "Hardware", requesterName = "Pat" });
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/servicedesk/tickets/{a}/comments", Json(new { body = "On it" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/servicedesk/tickets/{a}/status", Json(new { status = "resolved" }))).StatusCode);
        await Sql("update servicedesk.tickets set created_at = created_at - interval '1 month' where subject = 'Need a monitor'");   // last month

        var thisMonth = DateTime.UtcNow.ToString("yyyy-MM");
        var rep = await J(await admin.GetAsync("/api/servicedesk/reports/monthly?month=" + thisMonth));
        var t = rep.GetProperty("totals");
        Assert.Equal(2, t.GetProperty("opened").GetInt32()); Assert.Equal(1, t.GetProperty("resolved").GetInt32()); Assert.Equal(2, t.GetProperty("backlogEnd").GetInt32());
        Assert.Equal(2, rep.GetProperty("tickets").GetArrayLength());
        var cat = rep.GetProperty("byCategory").EnumerateArray().First(x => x.GetProperty("name").GetString() == "Hardware");
        Assert.Equal(1, cat.GetProperty("count").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await (await TestSupport.ClientFor(factory, "view2@int.test", Pw)).GetAsync("/api/servicedesk/reports/monthly?month=" + thisMonth)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await TestSupport.ClientFor(factory, "cust2@int.test", Pw)).GetAsync("/api/servicedesk/reports/monthly?month=" + thisMonth)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/servicedesk/reports/monthly?month=2026-13")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/servicedesk/reports/monthly?month=" + thisMonth + "&companyId=99999")).StatusCode);
    }

    static Task Until(Func<bool> ok) => Until(() => Task.FromResult(ok()));
    static async Task Until(Func<Task<bool>> ok)
    {
        for (var i = 0; i < 100; i++) { if (await ok()) return; await Task.Delay(200); }
        Assert.Fail("Timed out waiting for the background job.");
    }
}
