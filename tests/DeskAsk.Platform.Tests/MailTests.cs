using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

/// <summary>A tiny SMTP server that accepts every message and keeps what it was given.</summary>
sealed class FakeSmtp : IDisposable
{
    readonly TcpListener l = new(IPAddress.Loopback, 0);
    public readonly List<string> Messages = new();
    public int Port => ((IPEndPoint)l.LocalEndpoint).Port;
    public FakeSmtp() { l.Start(); _ = Task.Run(Loop); }
    async Task Loop()
    {
        while (true)
        {
            TcpClient c; try { c = await l.AcceptTcpClientAsync(); } catch { return; }
            _ = Task.Run(() => Serve(c));
        }
    }
    async Task Serve(TcpClient c)
    {
        try
        {
            using var _ = c; var s = c.GetStream(); using var rd = new StreamReader(s, Encoding.ASCII); using var wr = new StreamWriter(s, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };
            await wr.WriteLineAsync("220 fake ready");
            string? line;
            while ((line = await rd.ReadLineAsync()) != null)
            {
                var cmd = line.ToUpperInvariant();
                if (cmd.StartsWith("EHLO") || cmd.StartsWith("HELO")) await wr.WriteLineAsync("250 fake");
                else if (cmd.StartsWith("MAIL") || cmd.StartsWith("RCPT") || cmd.StartsWith("RSET") || cmd.StartsWith("NOOP")) await wr.WriteLineAsync("250 ok");
                else if (cmd.StartsWith("DATA"))
                {
                    await wr.WriteLineAsync("354 go");
                    var sb = new StringBuilder();
                    while ((line = await rd.ReadLineAsync()) != null && line != ".") sb.AppendLine(line);
                    lock (Messages) Messages.Add(sb.ToString());
                    await wr.WriteLineAsync("250 queued");
                }
                else if (cmd.StartsWith("QUIT")) { await wr.WriteLineAsync("221 bye"); return; }
                else await wr.WriteLineAsync("250 ok");
            }
        }
        catch { }
    }
    public string[] Snapshot() { lock (Messages) return Messages.ToArray(); }
    public void Dispose() => l.Stop();
}

public class MailTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    const string Pw = "Long-Enough-1";
    readonly WebApplicationFactory<Program> factory;
    public MailTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); b.UseSetting("Platform:Roles", "all"); b.UseSetting("Jobs:PollSeconds", "1"); });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }
    static async Task<T> Scalar<T>(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); return (T)(await c.ExecuteScalarAsync())!; }
    static async Task Until(Func<Task<bool>> ok) { for (var i = 0; i < 40 && !await ok(); i++) await Task.Delay(500); }

    async Task<HttpClient> Start()
    {
        var admin = await TestSupport.AdminClient(factory);
        await Sql("truncate mail.outbox restart identity");
        await Sql("delete from core.settings where key = 'mail.config'");
        return admin;
    }
    static object Smtp(FakeSmtp f, string password = "") => new { provider = "smtp", from = "desk@example.test", fromName = "DeskAsk", host = "127.0.0.1", port = f.Port, security = "none", user = "", password };

    [PgFact]
    public async Task OnlyAdministratorsManageMail()
    {
        var admin = await Start();
        await Sql("delete from identity.users where \"Email\" = 'eng@mail.test'");
        await admin.PostAsync("/api/platform/users", Json(new { email = "eng@mail.test", name = "eng", role = "engineer", password = Pw }));
        var eng = await TestSupport.ClientFor(factory, "eng@mail.test", Pw);
        Assert.Equal(HttpStatusCode.Forbidden, (await eng.GetAsync("/api/platform/mail/config")).StatusCode);
        var anon = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/platform/mail/outbox")).StatusCode);
    }

    [PgFact]
    public async Task SettingsAreCheckedAndSecretsNeverComeBack()
    {
        var admin = await Start(); using var smtp = new FakeSmtp();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/platform/mail/config", Json(new { provider = "smtp", from = "no-at-sign" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/platform/mail/config", Json(new { provider = "smtp", from = "a@b.test" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/platform/mail/config", Json(new { provider = "graph", from = "a@b.test", tenantId = "t" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/platform/mail/config", Json(Smtp(smtp, "s3cret-pass")))).StatusCode);
        var raw = await (await admin.GetAsync("/api/platform/mail/config")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("s3cret-pass", raw);
        var c = JsonDocument.Parse(raw).RootElement;
        Assert.True(c.GetProperty("hasPassword").GetBoolean()); Assert.True(c.GetProperty("on").GetBoolean());
        Assert.DoesNotContain("s3cret-pass", await Scalar<string>("select value from core.settings where key = 'mail.config'"));
        // leaving the password out keeps it
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/platform/mail/config", Json(new { fromName = "Support" }))).StatusCode);
        Assert.True((await J(await admin.GetAsync("/api/platform/mail/config"))).GetProperty("hasPassword").GetBoolean());
    }

    [PgFact]
    public async Task TheTestButtonSendsAMessageAndReportsFailures()
    {
        var admin = await Start(); using var smtp = new FakeSmtp();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/platform/mail/test", Json(new { to = "x@y.test" }))).StatusCode);   // not set up yet
        await admin.PutAsync("/api/platform/mail/config", Json(Smtp(smtp)));
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/platform/mail/test", Json(new { to = "boss@example.test" }))).StatusCode);
        Assert.Contains(smtp.Snapshot(), m => m.Contains("DeskAsk test message"));
        await admin.PutAsync("/api/platform/mail/config", Json(new { port = 1 }));
        var bad = await admin.PostAsync("/api/platform/mail/test", Json(new { to = "boss@example.test" }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode); Assert.Equal("send_failed", (await J(bad)).GetProperty("error").GetString());
    }

    [PgFact]
    public async Task ATicketMailsTheRequesterAndAFailedMessageIsRetried()
    {
        var admin = await Start(); using var smtp = new FakeSmtp();
        await Sql("truncate servicedesk.tickets restart identity cascade");
        // mail not set up: the ticket still opens and nothing is queued
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync("/api/servicedesk/tickets", Json(new { subject = "Quiet one", requesterName = "Pat", requesterEmail = "pat@acme.test" }))).StatusCode);
        Assert.Equal(0L, await Scalar<long>("select count(*) from mail.outbox"));

        await admin.PutAsync("/api/platform/mail/config", Json(Smtp(smtp)));
        var t = await J(await admin.PostAsync("/api/servicedesk/tickets", Json(new { subject = "Printer down", description = "Third floor", requesterName = "Pat", requesterEmail = "pat@acme.test" })));
        var number = t.GetProperty("number").GetString()!;
        await Until(async () => smtp.Snapshot().Any(m => m.Contains(number)));
        Assert.Contains(smtp.Snapshot(), m => m.Contains(number) && m.Contains("Printer down"));
        Assert.Contains(smtp.Snapshot(), m => m.Contains("text/html") && m.Contains("text/plain") && m.Contains("#/servicedesk/"));   // both versions, with a link to the ticket
        Assert.Equal("sent", await Scalar<string>("select status from mail.outbox where reference = '" + number + "' order by id limit 1"));

        // a staff reply mails the requester; an internal note does not
        var id = t.GetProperty("id").GetInt64();
        await admin.PostAsync($"/api/servicedesk/tickets/{id}/comments", Json(new { body = "Secret staff chatter", @internal = true }));
        await admin.PostAsync($"/api/servicedesk/tickets/{id}/comments", Json(new { body = "We are on it" }));
        Assert.Equal(2L, await Scalar<long>("select count(*) from mail.outbox where reference = '" + number + "'"));
        await Until(async () => smtp.Snapshot().Any(m => m.Contains("We are on it")));
        Assert.DoesNotContain(smtp.Snapshot(), m => m.Contains("Secret staff chatter"));

        // a failure is kept with its reason and goes back to waiting
        await admin.PutAsync("/api/platform/mail/config", Json(new { port = 1 }));
        await admin.PostAsync($"/api/servicedesk/tickets/{id}/comments", Json(new { body = "Second reply" }));
        await Until(async () => await Scalar<long>("select count(*) from mail.outbox where attempts > 0 and status = 'queued' and last_error <> ''") > 0);
        Assert.True(await Scalar<long>("select count(*) from mail.outbox where attempts > 0 and status = 'queued' and last_error <> ''") > 0);
        var failed = await J(await admin.GetAsync("/api/platform/mail/outbox?status=queued"));
        Assert.True(failed.GetProperty("items").GetArrayLength() >= 1);

        // giving up, then retrying by hand
        await Sql("update mail.outbox set status = 'failed', attempts = 5 where status = 'queued'");
        var fid = await Scalar<long>("select id from mail.outbox where status = 'failed' order by id limit 1");
        await admin.PutAsync("/api/platform/mail/config", Json(new { port = smtp.Port }));
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/platform/mail/outbox/{fid}/retry", null)).StatusCode);
        await Until(async () => await Scalar<string>($"select status from mail.outbox where id = {fid}") == "sent");
        Assert.Equal("sent", await Scalar<string>($"select status from mail.outbox where id = {fid}"));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync($"/api/platform/mail/outbox/{fid}/retry", null)).StatusCode);
    }
}

public class TemplateTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    readonly WebApplicationFactory<Program> factory;
    public TemplateTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); });
    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }

    [PgFact]
    public async Task TemplatesCanBeEditedPreviewedAndReset()
    {
        var admin = await TestSupport.AdminClient(factory);
        await Sql("delete from core.settings where key = 'mail.templates'");
        var list = (await J(await admin.GetAsync("/api/platform/mail/templates"))).GetProperty("items");
        Assert.Contains(list.EnumerateArray(), t => t.GetProperty("key").GetString() == "ticket_reply" && !t.GetProperty("custom").GetBoolean());
        // a typo in a name is refused, and so is an empty message
        var bad = await admin.PutAsync("/api/platform/mail/templates/ticket_reply", Json(new { subject = "Re: {{number}}", body = "Hi {{requestor}}" }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode); Assert.Contains("requestor", (await J(bad)).GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/platform/mail/templates/ticket_reply", Json(new { subject = "x", body = " " }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsync("/api/platform/mail/templates/nope", Json(new { subject = "x", body = "y" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/platform/mail/templates/ticket_reply", Json(new { subject = "Update on {{number}}", body = "Dear {{requester}}, {{message}} {{link}}" }))).StatusCode);
        var prev = await J(await admin.PostAsync("/api/platform/mail/templates/ticket_reply/preview", Json(new { subject = "Update on {{number}}", body = "Dear {{requester}}, {{message}}\n{{link}}" })));
        Assert.Equal("Update on TKT-000123", prev.GetProperty("subject").GetString());
        Assert.Contains("Dear Pat Morgan", prev.GetProperty("text").GetString());
        Assert.Contains("https://", prev.GetProperty("text").GetString()); Assert.Contains("#/servicedesk/123", prev.GetProperty("text").GetString());
        Assert.Contains("<a href=", prev.GetProperty("html").GetString());
        // what people type is never turned into markup
        var xss = await J(await admin.PostAsync("/api/platform/mail/templates/ticket_reply/preview", Json(new { subject = "s", body = "<script>alert(1)</script> {{requester}}" })));
        Assert.DoesNotContain("<script>", xss.GetProperty("html").GetString());
        var after = (await J(await admin.GetAsync("/api/platform/mail/templates"))).GetProperty("items").EnumerateArray().First(t => t.GetProperty("key").GetString() == "ticket_reply");
        Assert.True(after.GetProperty("custom").GetBoolean()); Assert.Equal("Update on {{number}}", after.GetProperty("subject").GetString());
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync("/api/platform/mail/templates/ticket_reply")).StatusCode);
        var reset = (await J(await admin.GetAsync("/api/platform/mail/templates"))).GetProperty("items").EnumerateArray().First(t => t.GetProperty("key").GetString() == "ticket_reply");
        Assert.False(reset.GetProperty("custom").GetBoolean());
    }
}

public class SignOutTests : IClassFixture<WebApplicationFactory<Program>>
{
    readonly WebApplicationFactory<Program> factory;
    public SignOutTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => TestSupport.Configure(b));

    [PgFact]
    public async Task SigningOutEndsTheSessionAndReturnsToTheStartPage()
    {
        var c = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var r = await c.GetAsync("/connect/logout");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/", r.Headers.Location?.OriginalString);
    }
}
