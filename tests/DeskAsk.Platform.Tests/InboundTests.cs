using System.Net;
using System.Text;
using System.Text.Json;
using DeskAsk.Platform;
using DeskAsk.Platform.Mail;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

sealed class FakeFetcher : IInboundFetcher
{
    public readonly List<InboundMail> Pending = new();
    public async Task FetchAsync(InboundConfig inbound, MailConfig mail, Func<InboundMail, Task<bool>> store, CancellationToken ct)
    {
        foreach (var m in Pending.ToArray()) if (await store(m)) Pending.Remove(m);
    }
    public Task<int> CountAsync(InboundConfig inbound, MailConfig mail, CancellationToken ct) => Task.FromResult(Pending.Count);
}

public class InboundTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    const string Pw = "Long-Enough-1";
    readonly WebApplicationFactory<Program> factory; readonly FakeFetcher fake = new();
    public InboundTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b =>
    {
        TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); b.UseSetting("Platform:Roles", "all"); b.UseSetting("Jobs:PollSeconds", "1");
        b.ConfigureServices(s => { s.RemoveAll<IInboundFetcher>(); s.AddSingleton<IInboundFetcher>(fake); });
    });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }
    static async Task<T> Scalar<T>(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); return (T)(await c.ExecuteScalarAsync())!; }
    static async Task Until(Func<Task<bool>> ok) { for (var i = 0; i < 40 && !await ok(); i++) await Task.Delay(500); }
    static InboundMail Msg(string id, string from, string subject, string body = "Hello") => new(id, from, "Sender", subject, body, DateTimeOffset.UtcNow);

    async Task<HttpClient> Start()
    {
        var admin = await TestSupport.AdminClient(factory);
        await Sql("truncate mail.inbox, mail.outbox restart identity; truncate servicedesk.tickets restart identity cascade");
        await Sql("delete from core.settings where key in ('mail.config','mail.inbound')");
        await Sql("delete from identity.users where \"Email\" like '%@in.test'");
        fake.Pending.Clear();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/platform/mail/inbound", Json(new { provider = "imap", host = "imap.example.test", port = 993, user = "support@in.test", password = "p4ss-word" }))).StatusCode);
        return admin;
    }
    async Task Fetch(HttpClient admin) { Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/platform/mail/inbound/fetch", null)).StatusCode); await Until(async () => fake.Pending.Count == 0); await Task.Delay(1500); }
    static async Task<long> Ticket(HttpClient c, object body) => (await J(await c.PostAsync("/api/servicedesk/tickets", Json(body)))).GetProperty("id").GetInt64();

    [PgFact]
    public async Task SettingsAreCheckedAndThePasswordNeverComesBack()
    {
        var admin = await Start();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/platform/mail/inbound", Json(new { provider = "imap", host = "" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/platform/mail/inbound", Json(new { provider = "graph" }))).StatusCode);   // sending is not set up
        var raw = await (await admin.GetAsync("/api/platform/mail/inbound")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("p4ss-word", raw); Assert.True(JsonDocument.Parse(raw).RootElement.GetProperty("hasPassword").GetBoolean());
        Assert.DoesNotContain("p4ss-word", await Scalar<string>("select value from core.settings where key = 'mail.inbound'"));
        fake.Pending.Add(Msg("a", "x@y.test", "hi"));
        Assert.Equal(1, (await J(await admin.PostAsync("/api/platform/mail/inbound/test", null))).GetProperty("unread").GetInt32());
    }

    [PgFact]
    public async Task ARepliedTicketTakesTheMessageAndAStrangerWaitsInTheInbox()
    {
        var admin = await Start();
        var id = await Ticket(admin, new { subject = "Printer down", requesterName = "Pat", requesterEmail = "pat@acme.test" });
        var no = await Scalar<string>($"select number from servicedesk.tickets where id = {id}");
        await admin.PostAsync($"/api/servicedesk/tickets/{id}/status", Json(new { status = "hold" }));
        fake.Pending.Add(Msg("m1", "pat@acme.test", $"Re: [{no}] Printer down", "It is the third floor one"));
        fake.Pending.Add(Msg("m2", "stranger@evil.test", $"Re: [{no}] Printer down", "Hello, I am not involved"));
        fake.Pending.Add(Msg("m3", "new@acme.test", "Need a laptop", "Please order one"));
        fake.Pending.Add(Msg("m1", "pat@acme.test", $"Re: [{no}] Printer down", "duplicate delivery"));
        await Fetch(admin);
        var d = await J(await admin.GetAsync($"/api/servicedesk/tickets/{id}"));
        var why = await Scalar<string>("select coalesce(string_agg(target || ': ' || detail, ' | '), 'no errors') from core.audit where action = 'mail.inbound_error'") + " / inbox: " + await Scalar<string>("select coalesce(string_agg(message_id || '=' || status, ','), 'empty') from mail.inbox");
        Assert.True("open" == d.GetProperty("ticket").GetProperty("status").GetString(), why);   // the customer answered
        Assert.Contains(d.GetProperty("comments").EnumerateArray(), c => c.GetProperty("body").GetString() == "It is the third floor one");
        Assert.DoesNotContain(d.GetProperty("comments").EnumerateArray(), c => (c.GetProperty("body").GetString() ?? "").Contains("not involved"));
        Assert.Equal(2L, await Scalar<long>("select count(*) from mail.inbox where status = 'new'"));
        Assert.Equal(3L, await Scalar<long>("select count(*) from mail.inbox"));   // the duplicate was dropped
        Assert.Equal("converted", await Scalar<string>("select status from mail.inbox where message_id = 'm1'"));
    }

    [PgFact]
    public async Task OurOwnAndAutomaticMessagesNeverBecomeTickets()
    {
        var admin = await Start();
        fake.Pending.Add(Msg("o1", "support@in.test", "Re: loop", "from ourselves"));
        fake.Pending.Add(Msg("o2", "someone@acme.test", "Automatic reply: away", "I am out"));
        fake.Pending.Add(new InboundMail("o3", "robot@acme.test", "Robot", "Newsletter", "Buy now", DateTimeOffset.UtcNow, true));
        await Fetch(admin);
        Assert.Equal(0L, await Scalar<long>("select count(*) from mail.inbox where status = 'new'"));
        Assert.Equal(3L, await Scalar<long>("select count(*) from mail.inbox where status = 'ignored'"));
    }

    [PgFact]
    public async Task APersonConvertsOrRejectsWhatWaits()
    {
        var admin = await Start();
        fake.Pending.Add(Msg("c1", "new@acme.test", "Need a laptop", "Please order one"));
        fake.Pending.Add(Msg("c2", "spam@bad.test", "Win a prize", "Click here"));
        await Fetch(admin);
        var items = (await J(await admin.GetAsync("/api/platform/mail/inbox"))).GetProperty("items");
        var a = items.EnumerateArray().First(x => x.GetProperty("subject").GetString() == "Need a laptop").GetProperty("id").GetInt64();
        var b = items.EnumerateArray().First(x => x.GetProperty("subject").GetString() == "Win a prize").GetProperty("id").GetInt64();
        var conv = await admin.PostAsync($"/api/platform/mail/inbox/{a}/convert", Json(new { }));
        Assert.Equal(HttpStatusCode.OK, conv.StatusCode);
        var number = (await J(conv)).GetProperty("number").GetString()!;
        Assert.Equal("Need a laptop", await Scalar<string>($"select subject from servicedesk.tickets where number = '{number}'"));
        Assert.Equal("mail", await Scalar<string>($"select source from servicedesk.tickets where number = '{number}'"));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync($"/api/platform/mail/inbox/{a}/convert", Json(new { }))).StatusCode);   // already done
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/platform/mail/inbox/{b}/reject", null)).StatusCode);
        Assert.Equal(0L, await Scalar<long>("select count(*) from mail.inbox where status = 'new'"));
    }

    [PgFact]
    public async Task AutomaticOpeningOnlyForKnownCustomersTiedToACompany()
    {
        var admin = await Start();
        var co = await J(await admin.PostAsync("/api/assets/companies", Json(new { name = "In Co " + Guid.NewGuid().ToString("N")[..6] })));
        var coId = co.GetProperty("id").GetInt32();
        var u = await J(await admin.PostAsync("/api/platform/users", Json(new { email = "sam@in.test", name = "Sam", role = "customer", password = Pw })));
        await admin.PutAsync("/api/servicedesk/people/" + u.GetProperty("id").GetString(), Json(new { companyId = coId }));
        await admin.PutAsync("/api/platform/mail/inbound", Json(new { autoCreate = true }));
        fake.Pending.Add(Msg("s1", "sam@in.test", "Wi-Fi is slow", "Since this morning"));
        fake.Pending.Add(Msg("s2", "unknown@elsewhere.test", "Hello", "Who are you"));
        await Fetch(admin);
        Assert.Equal(1L, await Scalar<long>($"select count(*) from servicedesk.tickets where company_id = {coId} and source = 'mail' and requester_email = 'sam@in.test'"));
        Assert.Equal("converted", await Scalar<string>("select status from mail.inbox where message_id = 's1'"));
        Assert.Equal("new", await Scalar<string>("select status from mail.inbox where message_id = 's2'"));
    }
}
