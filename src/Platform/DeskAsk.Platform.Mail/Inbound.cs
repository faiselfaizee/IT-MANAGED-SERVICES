using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using Npgsql;

namespace DeskAsk.Platform.Mail;

/// <summary>How a customer reads its support mailbox: provider none, imap or graph (Microsoft 365, using the same app as sending, which also needs Mail.ReadWrite).</summary>
public sealed record InboundConfig(string Provider, string Host, int Port, string Security, string User, string Password, string Folder, string Mailbox, bool AutoCreate)
{
    public const string Key = "mail.inbound";
    public static readonly InboundConfig None = new("none", "", 993, "ssl", "", "", "", "", false);
    public bool On => Provider is "imap" or "graph";
}

public static class InboundSettings
{
    public static async Task<InboundConfig> LoadAsync(ISettings s, IDataProtectionProvider dp, CancellationToken ct)
    {
        var raw = await s.GetAsync(InboundConfig.Key, ct);
        if (string.IsNullOrWhiteSpace(raw)) return InboundConfig.None;
        try
        {
            var e = JsonDocument.Parse(raw).RootElement;
            string T(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            string pw = ""; var enc = T("password"); if (enc != "") { try { pw = dp.CreateProtector("dk.mail").Unprotect(enc); } catch { } }
            return new InboundConfig(T("provider") is { Length: > 0 } p ? p : "none", T("host"), e.TryGetProperty("port", out var po) && po.TryGetInt32(out var pn) ? pn : 993,
                T("security") is { Length: > 0 } sc ? sc : "ssl", T("user"), pw, T("folder"), T("mailbox"), e.TryGetProperty("autoCreate", out var ac) && ac.ValueKind == JsonValueKind.True);
        }
        catch (JsonException) { return InboundConfig.None; }
    }

    public static Task SaveAsync(ISettings s, IDataProtectionProvider dp, InboundConfig c, CancellationToken ct) =>
        s.SetAsync(InboundConfig.Key, JsonSerializer.Serialize(new
        {
            provider = c.Provider, host = c.Host, port = c.Port, security = c.Security, user = c.User, password = c.Password == "" ? "" : dp.CreateProtector("dk.mail").Protect(c.Password),
            folder = c.Folder, mailbox = c.Mailbox, autoCreate = c.AutoCreate,
        }), ct);
}

/// <summary>Reads unread messages from the mailbox. Each message is handed to <c>store</c>; when that returns true the message is marked read.</summary>
public interface IInboundFetcher
{
    Task FetchAsync(InboundConfig inbound, MailConfig mail, Func<InboundMail, Task<bool>> store, CancellationToken ct);
    /// <summary>Connects and counts unread messages, changing nothing. Throws with a readable message when it cannot.</summary>
    Task<int> CountAsync(InboundConfig inbound, MailConfig mail, CancellationToken ct);
}

public sealed class RealInboundFetcher : IInboundFetcher
{
    const int Batch = 25;
    public const int MaxFiles = 10, MaxFileBytes = 10 * 1024 * 1024;

    public Task FetchAsync(InboundConfig i, MailConfig mail, Func<InboundMail, Task<bool>> store, CancellationToken ct) =>
        i.Provider == "graph" ? GraphFetch(i, mail, store, ct) : ImapFetch(i, store, ct);
    public Task<int> CountAsync(InboundConfig i, MailConfig mail, CancellationToken ct) => i.Provider == "graph" ? GraphCount(i, mail, ct) : ImapCount(i, ct);

    static async Task<ImapClient> Connect(InboundConfig i, CancellationToken ct)
    {
        var c = new ImapClient { Timeout = 20000 };
        try
        {
            var sec = i.Security switch { "none" => SecureSocketOptions.None, "starttls" => SecureSocketOptions.StartTls, _ => SecureSocketOptions.SslOnConnect };
            await c.ConnectAsync(i.Host, i.Port, sec, ct);
            if (i.User != "") await c.AuthenticateAsync(i.User, i.Password, ct);
            return c;
        }
        catch { c.Dispose(); throw; }
    }
    static async Task<IMailFolder> Open(ImapClient c, InboundConfig i, CancellationToken ct)
    {
        var f = i.Folder == "" ? c.Inbox : await c.GetFolderAsync(i.Folder, ct);
        await f.OpenAsync(FolderAccess.ReadWrite, ct); return f;
    }

    static async Task<int> ImapCount(InboundConfig i, CancellationToken ct)
    {
        using var c = await Connect(i, ct); var f = await Open(c, i, ct);
        var n = (await f.SearchAsync(SearchQuery.NotSeen, ct)).Count; await c.DisconnectAsync(true, ct); return n;
    }

    static async Task ImapFetch(InboundConfig i, Func<InboundMail, Task<bool>> store, CancellationToken ct)
    {
        using var c = await Connect(i, ct); var f = await Open(c, i, ct);
        var uids = await f.SearchAsync(SearchQuery.NotSeen, ct);
        foreach (var uid in uids.Take(Batch))
        {
            var m = await f.GetMessageAsync(uid, ct);
            var from = m.From.Mailboxes.FirstOrDefault();
            var auto = (m.Headers["Auto-Submitted"] is { } a && !a.Equals("no", StringComparison.OrdinalIgnoreCase)) || (m.Headers["Precedence"] is { } p && p.ToLowerInvariant() is "bulk" or "junk" or "list");
            var text = m.TextBody ?? InboundText.StripHtml(m.HtmlBody ?? "");
            var files = new List<InboundFile>();
            foreach (var part in m.Attachments.OfType<MimePart>().Take(MaxFiles))
            {
                using var ms = new MemoryStream();
                await part.Content.DecodeToAsync(ms, ct);
                if (ms.Length > 0 && ms.Length <= MaxFileBytes) files.Add(new InboundFile(part.FileName ?? "attachment", part.ContentType.MimeType, ms.ToArray()));
            }
            var mail = new InboundMail(string.IsNullOrWhiteSpace(m.MessageId) ? $"imap:{i.Host}:{uid.Id}:{m.Date.UtcTicks}" : m.MessageId, from?.Address ?? "", from?.Name ?? "", m.Subject ?? "", InboundText.Clean(text), m.Date == default ? DateTimeOffset.UtcNow : m.Date, auto, files);
            if (await store(mail)) await f.AddFlagsAsync(uid, MessageFlags.Seen, true, ct);
        }
        await c.DisconnectAsync(true, ct);
    }

    static string Box(InboundConfig i, MailConfig m) => Uri.EscapeDataString(i.Mailbox != "" ? i.Mailbox : m.From);
    static async Task<JsonElement> GetJson(HttpClient http, string url, CancellationToken ct)
    {
        var r = await http.GetAsync(url, ct); var t = await r.Content.ReadAsStringAsync(ct);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException("Microsoft 365 refused the request: " + MailSender.Short(t));
        return JsonDocument.Parse(t).RootElement.Clone();
    }
    static async Task<HttpClient> GraphClient(MailConfig m, CancellationToken ct)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await MailSender.GraphToken(m, http, ct));
        http.DefaultRequestHeaders.Add("Prefer", "outlook.body-content-type=\"text\"");
        return http;
    }

    static async Task<int> GraphCount(InboundConfig i, MailConfig m, CancellationToken ct)
    {
        using var http = await GraphClient(m, ct);
        var j = await GetJson(http, $"https://graph.microsoft.com/v1.0/users/{Box(i, m)}/mailFolders/inbox/messages?$filter=isRead eq false&$top=1&$count=true&$select=id", ct);
        return j.TryGetProperty("@odata.count", out var n) && n.TryGetInt32(out var x) ? x : j.GetProperty("value").GetArrayLength();
    }

    static async Task GraphFetch(InboundConfig i, MailConfig m, Func<InboundMail, Task<bool>> store, CancellationToken ct)
    {
        using var http = await GraphClient(m, ct);
        var j = await GetJson(http, $"https://graph.microsoft.com/v1.0/users/{Box(i, m)}/mailFolders/inbox/messages?$filter=isRead eq false&$top={Batch}&$select=id,internetMessageId,subject,from,body,receivedDateTime,hasAttachments", ct);
        foreach (var e in j.GetProperty("value").EnumerateArray())
        {
            string S(JsonElement x, string n) => x.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            var from = e.TryGetProperty("from", out var f) && f.TryGetProperty("emailAddress", out var ea) ? ea : default;
            var id = S(e, "id");
            var body = e.TryGetProperty("body", out var b) ? S(b, "content") : "";
            var subject = S(e, "subject");
            var files = new List<InboundFile>();
            if (e.TryGetProperty("hasAttachments", out var ha) && ha.ValueKind == JsonValueKind.True)
            {
                var aj = await GetJson(http, $"https://graph.microsoft.com/v1.0/users/{Box(i, m)}/messages/{Uri.EscapeDataString(id)}/attachments?$top={MaxFiles}", ct);
                foreach (var a in aj.GetProperty("value").EnumerateArray())
                {
                    if (S(a, "@odata.type") != "#microsoft.graph.fileAttachment" || (a.TryGetProperty("isInline", out var il) && il.ValueKind == JsonValueKind.True) || !a.TryGetProperty("contentBytes", out var cb) || cb.ValueKind != JsonValueKind.String) continue;
                    var bytes = Convert.FromBase64String(cb.GetString()!);
                    if (bytes.Length > 0 && bytes.Length <= MaxFileBytes) files.Add(new InboundFile(S(a, "name") is { Length: > 0 } fn ? fn : "attachment", S(a, "contentType") is { Length: > 0 } ct2 ? ct2 : "application/octet-stream", bytes));
                }
            }
            var mail = new InboundMail(S(e, "internetMessageId") is { Length: > 0 } mid ? mid : "graph:" + id, from.ValueKind == JsonValueKind.Object ? S(from, "address") : "", from.ValueKind == JsonValueKind.Object ? S(from, "name") : "",
                subject, InboundText.Clean(body), DateTimeOffset.TryParse(S(e, "receivedDateTime"), out var at) ? at : DateTimeOffset.UtcNow, false, files);
            if (await store(mail))
            {
                var req = new HttpRequestMessage(HttpMethod.Patch, $"https://graph.microsoft.com/v1.0/users/{Box(i, m)}/messages/{Uri.EscapeDataString(id)}") { Content = new StringContent("{\"isRead\":true}", Encoding.UTF8, "application/json") };
                (await http.SendAsync(req, ct)).EnsureSuccessStatusCode();
            }
        }
    }
}

public static class InboundText
{
    public static string StripHtml(string html)
    {
        var t = Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"<br\s*/?>|</p>|</div>|</tr>", "\n", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, "<[^>]+>", "");
        return System.Net.WebUtility.HtmlDecode(t);
    }

    static readonly Regex Cut = new(@"^\s*(On .{5,200} wrote:|-{2,}\s*Original Message\s*-{2,}|_{5,}|From:\s.+|Sent from my .+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>The new text of a reply: drops the quoted history under it and trims the length.</summary>
    public static string Clean(string text)
    {
        text = (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
        var m = Cut.Match(text);
        if (m.Success && m.Index > 0) text = text[..m.Index];
        text = string.Join("\n", text.Split('\n').Where(l => !l.StartsWith('>'))).Trim();
        return text.Length > 20000 ? text[..20000] : text;
    }
}

/// <summary>Reads the mailbox every minute and stores each message in the inbox; the Service Desk gets first look at each one.</summary>
public sealed class MailFetchJob : IJobHandler
{
    public const string JobType = "mail.fetch";
    public string Type => JobType;

    public async Task RunAsync(JobContext job, CancellationToken ct)
    {
        var sp = job.Services; var dp = sp.GetRequiredService<IDataProtectionProvider>(); var settings = sp.GetRequiredService<ISettings>();
        var inbound = await InboundSettings.LoadAsync(settings, dp, ct);
        if (!inbound.On) return;
        var mail = await MailSettings.LoadAsync(settings, dp, ct);
        var handler = sp.GetService<IInboundMailHandler>(); var fetcher = sp.GetRequiredService<IInboundFetcher>();
        await using var db = new NpgsqlConnection(job.Tenant.ConnectionString); await db.OpenAsync(ct);
        await fetcher.FetchAsync(inbound, mail, async m =>
        {
            if (string.IsNullOrWhiteSpace(m.FromAddr)) return true;   // nothing to answer; mark read
            // our own messages and automatic replies must never become tickets, or two mail systems could loop
            var ignore = m.Auto || string.Equals(m.FromAddr, mail.From, StringComparison.OrdinalIgnoreCase) || string.Equals(m.FromAddr, inbound.User, StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(m.Subject, @"^(automatic reply|auto:|out of office|undeliverable|delivery status notification)", RegexOptions.IgnoreCase);
            long id;
            await using (var c = new NpgsqlCommand(@"insert into mail.inbox (message_id, from_addr, from_name, subject, body, received_at, status) values (@m, @a, @n, @s, @b, @t, @st)
                on conflict (message_id) do nothing returning id", db))
            {
                c.Parameters.AddWithValue("m", m.MessageId); c.Parameters.AddWithValue("a", m.FromAddr); c.Parameters.AddWithValue("n", m.FromName);
                c.Parameters.AddWithValue("s", m.Subject.Length > 300 ? m.Subject[..300] : m.Subject); c.Parameters.AddWithValue("b", m.Body); c.Parameters.AddWithValue("t", m.At.UtcDateTime); c.Parameters.AddWithValue("st", ignore ? "ignored" : "new");
                if (await c.ExecuteScalarAsync(ct) is not long got) return true;   // seen before
                id = got;
            }
            if (ignore || handler == null) return true;
            // files wait with the message until a person decides, or go to the ticket at once
            foreach (var f in m.Files ?? Array.Empty<InboundFile>())
            {
                await using var fc = new NpgsqlCommand("insert into mail.inbox_files (inbox_id, name, content_type, data) values (@i, @n, @c, @d)", db);
                fc.Parameters.AddWithValue("i", id); fc.Parameters.AddWithValue("n", f.Name); fc.Parameters.AddWithValue("c", f.ContentType); fc.Parameters.AddWithValue("d", f.Data); await fc.ExecuteNonQueryAsync(ct);
            }
            try
            {
                if (await handler.HandleAsync(m, ct) is { } number)
                {
                    await using var u = new NpgsqlCommand("update mail.inbox set status = 'converted', ticket_number = @n where id = @i; delete from mail.inbox_files where inbox_id = @i", db); u.Parameters.AddWithValue("n", number); u.Parameters.AddWithValue("i", id); await u.ExecuteNonQueryAsync(ct);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {   // the message stays in the inbox for a person; the audit log says why the automatic step failed
                try { await sp.GetRequiredService<IAuditLog>().WriteAsync("mail.inbound_error", m.MessageId, e.Message.Length > 300 ? e.Message[..300] : e.Message, "system", ct); } catch { }
            }
            return true;
        }, ct);
    }
}

public static class InboundApi
{
    static IResult Bad(string error, string? detail = null) => Results.BadRequest(new { error, detail });

    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/inbound", async (ISettings s, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            var c = await InboundSettings.LoadAsync(s, dp, ct);
            return Results.Ok(new { provider = c.Provider, host = c.Host, port = c.Port, security = c.Security, user = c.User, hasPassword = c.Password != "", folder = c.Folder, mailbox = c.Mailbox, autoCreate = c.AutoCreate, on = c.On });
        });

        g.MapPut("/inbound", async (JsonElement b, ISettings s, IDataProtectionProvider dp, IAuditLog audit, CancellationToken ct) =>
        {
            var old = await InboundSettings.LoadAsync(s, dp, ct);
            string T(string n, string cur, int max) { if (!b.TryGetProperty(n, out var v) || v.ValueKind == JsonValueKind.Null) return cur; if (v.ValueKind != JsonValueKind.String) throw new FormatException(n + " must be text"); var x = v.GetString()!.Trim(); if (x.Length > max) throw new FormatException(n + " is too long"); return x; }
            try
            {
                var provider = T("provider", old.Provider, 10);
                if (provider is not ("none" or "imap" or "graph")) return Bad("bad_provider", "provider must be none, imap or graph");
                var port = b.TryGetProperty("port", out var pv) && pv.ValueKind == JsonValueKind.Number && pv.TryGetInt32(out var pn) ? pn : old.Port;
                var auto = b.TryGetProperty("autoCreate", out var av) ? av.ValueKind == JsonValueKind.True : old.AutoCreate;
                var c = new InboundConfig(provider, T("host", old.Host, 253), port, T("security", old.Security, 10), T("user", old.User, 254), T("password", old.Password, 500), T("folder", old.Folder, 200), T("mailbox", old.Mailbox, 254), auto);
                if (c.Security is not ("none" or "starttls" or "ssl")) return Bad("bad_security", "security must be none, starttls or ssl");
                if (provider == "imap" && (c.Host == "" || c.Port is < 1 or > 65535)) return Bad("imap_incomplete", "Enter the mail server and its port.");
                if (provider == "graph")
                {
                    var m = await MailSettings.LoadAsync(s, dp, ct);
                    if (m.Provider != "graph") return Bad("graph_not_set_up", "Set up sending with Microsoft 365 first; reading uses the same application.");
                }
                await InboundSettings.SaveAsync(s, dp, c, ct);
                await audit.WriteAsync("mail.inbound_changed", provider, c.Host, ct: ct);
                return Results.Ok(new { ok = true });
            }
            catch (FormatException e) { return Bad("bad_request", e.Message); }
        });

        g.MapPost("/inbound/test", async (ISettings s, IDataProtectionProvider dp, IInboundFetcher f, CancellationToken ct) =>
        {
            var c = await InboundSettings.LoadAsync(s, dp, ct);
            if (!c.On) return Bad("not_set_up", "Save the incoming mail settings first.");
            try { return Results.Ok(new { ok = true, unread = await f.CountAsync(c, await MailSettings.LoadAsync(s, dp, ct), ct) }); }
            catch (Exception e) when (e is not OperationCanceledException) { return Bad("connect_failed", e.Message); }
        });

        g.MapPost("/inbound/fetch", async (IJobQueue jobs, ISettings s, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            if (!(await InboundSettings.LoadAsync(s, dp, ct)).On) return Bad("not_set_up", "Save the incoming mail settings first.");
            await jobs.EnqueueAsync(MailFetchJob.JobType, "{}", null, "mail.fetch", ct);
            return Results.Ok(new { ok = true });
        });

        g.MapGet("/inbox", async (HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = new NpgsqlConnection(t.Require().ConnectionString); await db.OpenAsync(ct);
            var st = http.Request.Query["status"].ToString(); if (st == "") st = "new";
            await using var c = new NpgsqlCommand("select id, from_addr, from_name, subject, body, received_at, status, ticket_number, (select count(*) from mail.inbox_files f where f.inbox_id = mail.inbox.id) from mail.inbox where (@s = 'all' or status = @s) order by id desc limit 100", db);
            c.Parameters.AddWithValue("s", st);
            var items = new List<object>();
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) items.Add(new { id = rd.GetInt64(0), from = rd.GetString(1), name = rd.GetString(2), subject = rd.GetString(3), body = rd.GetString(4), receivedAt = rd.GetDateTime(5), status = rd.GetString(6), ticket = rd.GetString(7), files = rd.GetInt64(8) });
            return Results.Ok(new { items });
        });

        g.MapPost("/inbox/{id:long}/convert", async (long id, JsonElement b, ITenantContext t, IInboundMailHandler? handler, IAuditLog audit, CancellationToken ct) =>
        {
            if (handler == null) return Bad("no_service_desk", "Service Desk is not available.");
            await using var db = new NpgsqlConnection(t.Require().ConnectionString); await db.OpenAsync(ct);
            InboundMail? m = null;
            await using (var c = new NpgsqlCommand("select message_id, from_addr, from_name, subject, body, received_at from mail.inbox where id = @i and status = 'new'", db))
            {
                c.Parameters.AddWithValue("i", id);
                await using var rd = await c.ExecuteReaderAsync(ct);
                if (await rd.ReadAsync(ct)) m = new InboundMail(rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetString(3), rd.GetString(4), rd.GetDateTime(5));
            }
            if (m == null) return Results.NotFound(new { error = "not_found" });
            var files = new List<InboundFile>();
            await using (var fc = new NpgsqlCommand("select name, content_type, data from mail.inbox_files where inbox_id = @i order by id", db))
            {
                fc.Parameters.AddWithValue("i", id);
                await using var fr = await fc.ExecuteReaderAsync(ct);
                while (await fr.ReadAsync(ct)) files.Add(new InboundFile(fr.GetString(0), fr.GetString(1), (byte[])fr.GetValue(2)));
            }
            m = m with { Files = files };
            int? company = b.ValueKind == JsonValueKind.Object && b.TryGetProperty("companyId", out var cv) && cv.ValueKind == JsonValueKind.Number && cv.TryGetInt32(out var ci) ? ci : null;
            string number;
            try { number = await handler.CreateTicketAsync(m, company, ct); }
            catch (InvalidOperationException e) { return Bad("cannot_convert", e.Message); }
            await using (var u = new NpgsqlCommand("update mail.inbox set status = 'converted', ticket_number = @n where id = @i; delete from mail.inbox_files where inbox_id = @i", db)) { u.Parameters.AddWithValue("n", number); u.Parameters.AddWithValue("i", id); await u.ExecuteNonQueryAsync(ct); }
            await audit.WriteAsync("mail.inbox_converted", number, m.FromAddr, ct: ct);
            return Results.Ok(new { number });
        });

        g.MapPost("/inbox/{id:long}/reject", async (long id, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = new NpgsqlConnection(t.Require().ConnectionString); await db.OpenAsync(ct);
            await using var c = new NpgsqlCommand("update mail.inbox set status = 'rejected' where id = @i and status = 'new'; delete from mail.inbox_files where inbox_id = @i and exists (select 1 from mail.inbox where id = @i and status = 'rejected')", db); c.Parameters.AddWithValue("i", id);
            if (await c.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound(new { error = "not_found" });
            await audit.WriteAsync("mail.inbox_rejected", id.ToString(), "", ct: ct);
            return Results.Ok(new { ok = true });
        });
    }
}
