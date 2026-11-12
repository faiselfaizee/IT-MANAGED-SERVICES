using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using Npgsql;

namespace DeskAsk.Platform.Mail;

public sealed class MailSchema : ITenantSchema
{
    public string Name => "mail";
    public static readonly Migration[] Migrations =
    {
        new(1, "outbox", @"
            create schema if not exists mail;
            create table if not exists mail.outbox (
              id bigserial primary key, to_addr text not null, subject text not null, body text not null, reference text not null default '',
              status text not null default 'queued' check (status in ('queued','sent','failed')),
              attempts int not null default 0, last_error text not null default '', next_try_at timestamptz not null default now(),
              created_at timestamptz not null default now(), sent_at timestamptz);
            create index if not exists outbox_due on mail.outbox (status, next_try_at);
            create index if not exists outbox_created on mail.outbox (created_at desc)"),
        new(2, "inbox", @"
            create table if not exists mail.inbox (
              id bigserial primary key, message_id text not null, from_addr text not null, from_name text not null default '', subject text not null default '', body text not null default '',
              received_at timestamptz not null default now(),
              status text not null default 'new' check (status in ('new','converted','rejected','ignored')),
              ticket_number text not null default '', created_at timestamptz not null default now());
            create unique index if not exists inbox_message on mail.inbox (message_id);
            create index if not exists inbox_status on mail.inbox (status, id desc)"),
        new(3, "html mail and incoming files", @"
            alter table mail.outbox add column if not exists body_html text not null default '';
            create table if not exists mail.inbox_files (
              id bigserial primary key, inbox_id bigint not null references mail.inbox(id) on delete cascade,
              name text not null, content_type text not null default 'application/octet-stream', data bytea not null);
            create index if not exists inbox_files_inbox on mail.inbox_files (inbox_id)"),
    };
    public Task EnsureAsync(IServiceProvider services, TenantInfo tenant, CancellationToken ct) => SchemaMigrator.RunAsync(tenant.ConnectionString, Name, Migrations, ct);
}

/// <summary>How a customer sends mail. Passwords and secrets are stored encrypted and never sent back to the browser.</summary>
public sealed record MailConfig(string Provider, string From, string FromName, string Host, int Port, string Security, string User, string Password,
    string TenantId, string ClientId, string Secret, string PublicUrl = "")
{
    public const string Key = "mail.config";
    public static readonly MailConfig None = new("none", "", "", "", 587, "starttls", "", "", "", "", "");
    public bool On => Provider != "none" && From.Contains('@');
}

public static class MailSettings
{
    const string Purpose = "dk.mail";
    public static async Task<MailConfig> LoadAsync(ISettings s, IDataProtectionProvider dp, CancellationToken ct)
    {
        var raw = await s.GetAsync(MailConfig.Key, ct);
        if (string.IsNullOrWhiteSpace(raw)) return MailConfig.None;
        try
        {
            var e = JsonDocument.Parse(raw).RootElement;
            string T(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            var p = dp.CreateProtector(Purpose);
            string Open(string n) { var x = T(n); if (x == "") return ""; try { return p.Unprotect(x); } catch { return ""; } }
            return new MailConfig(T("provider") is { Length: > 0 } pr ? pr : "none", T("from"), T("fromName"), T("host"), e.TryGetProperty("port", out var po) && po.TryGetInt32(out var pn) ? pn : 587,
                T("security") is { Length: > 0 } sc ? sc : "starttls", T("user"), Open("password"), T("tenantId"), T("clientId"), Open("secret"), T("publicUrl"));
        }
        catch (JsonException) { return MailConfig.None; }
    }

    public static Task SaveAsync(ISettings s, IDataProtectionProvider dp, MailConfig c, CancellationToken ct)
    {
        var p = dp.CreateProtector(Purpose);
        return s.SetAsync(MailConfig.Key, JsonSerializer.Serialize(new
        {
            provider = c.Provider, from = c.From, fromName = c.FromName, host = c.Host, port = c.Port, security = c.Security, user = c.User,
            password = c.Password == "" ? "" : p.Protect(c.Password), tenantId = c.TenantId, clientId = c.ClientId, secret = c.Secret == "" ? "" : p.Protect(c.Secret), publicUrl = c.PublicUrl,
        }), ct);
    }
}

public static class MailSender
{
    static string OneLine(string s) => s.Replace("\r", " ").Replace("\n", " ").Trim();

    public static async Task SendAsync(MailConfig c, string to, string subject, string body, CancellationToken ct, string? html = null)
    {
        subject = OneLine(subject);
        if (c.Provider == "smtp") await Smtp(c, to, subject, body, html, ct);
        else if (c.Provider == "graph") await Graph(c, to, subject, body, html, ct);
        else throw new InvalidOperationException("Mail is not set up.");
    }

    static async Task Smtp(MailConfig c, string to, string subject, string body, string? html, CancellationToken ct)
    {
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress(c.FromName, c.From)); m.To.Add(MailboxAddress.Parse(to)); m.Subject = subject;
        m.Body = new BodyBuilder { TextBody = body, HtmlBody = string.IsNullOrEmpty(html) ? null : html }.ToMessageBody();
        using var smtp = new SmtpClient { Timeout = 20000 };
        var sec = c.Security switch { "ssl" => SecureSocketOptions.SslOnConnect, "none" => SecureSocketOptions.None, _ => SecureSocketOptions.StartTls };
        await smtp.ConnectAsync(c.Host, c.Port, sec, ct);
        if (c.User != "") await smtp.AuthenticateAsync(c.User, c.Password, ct);
        await smtp.SendAsync(m, ct);
        await smtp.DisconnectAsync(true, ct);
    }

    internal static async Task<string> GraphToken(MailConfig c, HttpClient http, CancellationToken ct)
    {
        var tok = await http.PostAsync($"https://login.microsoftonline.com/{Uri.EscapeDataString(c.TenantId)}/oauth2/v2.0/token", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["client_id"] = c.ClientId, ["client_secret"] = c.Secret, ["scope"] = "https://graph.microsoft.com/.default", ["grant_type"] = "client_credentials" }), ct);
        var tj = await tok.Content.ReadAsStringAsync(ct);
        if (!tok.IsSuccessStatusCode) throw new InvalidOperationException("Microsoft 365 sign-in failed: " + Short(tj));
        return JsonDocument.Parse(tj).RootElement.GetProperty("access_token").GetString()!;
    }

    static async Task Graph(MailConfig c, string to, string subject, string body, string? html, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var access = await GraphToken(c, http, ct);
        var req = new HttpRequestMessage(HttpMethod.Post, $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(c.From)}/sendMail")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { message = new { subject, body = new { contentType = string.IsNullOrEmpty(html) ? "Text" : "HTML", content = string.IsNullOrEmpty(html) ? body : html }, toRecipients = new[] { new { emailAddress = new { address = to } } } }, saveToSentItems = false }), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException("Microsoft 365 refused the message: " + Short(await res.Content.ReadAsStringAsync(ct)));
    }

    internal static string Short(string s) => s.Length > 300 ? s[..300] : s;
}

/// <summary>Queues messages in the customer's outbox and wakes the sender.</summary>
public sealed class MailQueue : IMailQueue
{
    readonly ITenantContext tenant; readonly ISettings settings; readonly IDataProtectionProvider dp; readonly IJobQueue jobs;
    public MailQueue(ITenantContext t, ISettings s, IDataProtectionProvider d, IJobQueue j) { tenant = t; settings = s; dp = d; jobs = j; }

    public async Task<bool> EnqueueAsync(string to, string subject, string body, string? reference = null, CancellationToken ct = default)
    {
        to = (to ?? "").Trim();
        if (to.Length == 0 || to.Length > 254 || !to.Contains('@') || to.Any(char.IsWhiteSpace)) return false;
        var cfg = await MailSettings.LoadAsync(settings, dp, ct);
        if (!cfg.On) return false;
        await using var db = new NpgsqlConnection(tenant.Require().ConnectionString); await db.OpenAsync(ct);
        await Insert(db, to, subject, body, "", reference, ct);
        return true;
    }

    async Task Insert(NpgsqlConnection db, string to, string subject, string body, string html, string? reference, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("insert into mail.outbox (to_addr, subject, body, body_html, reference) values (@t, @s, @b, @h, @r)", db);
        c.Parameters.AddWithValue("t", to); c.Parameters.AddWithValue("s", subject.Length > 300 ? subject[..300] : subject); c.Parameters.AddWithValue("b", body); c.Parameters.AddWithValue("h", html); c.Parameters.AddWithValue("r", reference ?? "");
        await c.ExecuteNonQueryAsync(ct);
        await jobs.EnqueueAsync(MailJob.JobType, "{}", null, "mail.send", ct);
    }

    public async Task<bool> EnqueueTemplateAsync(string template, string to, IReadOnlyDictionary<string, string> values, string? reference = null, CancellationToken ct = default)
    {
        to = (to ?? "").Trim();
        if (to.Length == 0 || to.Length > 254 || !to.Contains('@') || to.Any(char.IsWhiteSpace)) return false;
        var cfg = await MailSettings.LoadAsync(settings, dp, ct);
        if (!cfg.On) return false;
        var def = MailTemplates.Find(template); if (def == null) return false;
        var (sub, body) = await MailTemplates.CurrentAsync(settings, def, ct);
        var t = tenant.Require();
        var (subject, text, html) = MailTemplates.Render(def, sub, body, values, MailTemplates.BaseUrl(cfg, t), cfg.FromName != "" ? cfg.FromName : t.Name);
        await using var db = new NpgsqlConnection(t.ConnectionString); await db.OpenAsync(ct);
        await Insert(db, to, subject, text, html, reference, ct);
        return true;
    }
}

/// <summary>Sends what is due in the outbox. A failed message is tried again after 2, 4, 8 ... minutes (at most an hour) and given up on after five tries.</summary>
public sealed class MailJob : IJobHandler
{
    public const string JobType = "mail.send";
    public string Type => JobType;
    public const int MaxTries = 5;

    public async Task RunAsync(JobContext job, CancellationToken ct)
    {
        var cfg = await MailSettings.LoadAsync(job.Services.GetRequiredService<ISettings>(), job.Services.GetRequiredService<IDataProtectionProvider>(), ct);
        if (!cfg.On) return;   // leave the messages waiting until mail is set up
        await using var db = new NpgsqlConnection(job.Tenant.ConnectionString); await db.OpenAsync(ct);
        var due = new List<(long id, string to, string subject, string body, string html, int attempts)>();
        await using (var c = new NpgsqlCommand("select id, to_addr, subject, body, body_html, attempts from mail.outbox where status = 'queued' and next_try_at <= now() order by id limit 20", db))
        await using (var r = await c.ExecuteReaderAsync(ct)) while (await r.ReadAsync(ct)) due.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5)));
        foreach (var m in due)
        {
            try
            {
                await MailSender.SendAsync(cfg, m.to, m.subject, m.body, ct, m.html);
                await using var u = new NpgsqlCommand("update mail.outbox set status = 'sent', sent_at = now(), attempts = attempts + 1, last_error = '' where id = @i", db); u.Parameters.AddWithValue("i", m.id); await u.ExecuteNonQueryAsync(ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                var n = m.attempts + 1; var gone = n >= MaxTries;
                await using var u = new NpgsqlCommand("update mail.outbox set attempts = @n, status = @s, last_error = @e, next_try_at = now() + make_interval(mins => @w) where id = @i", db);
                u.Parameters.AddWithValue("n", n); u.Parameters.AddWithValue("s", gone ? "failed" : "queued"); u.Parameters.AddWithValue("e", e.Message.Length > 500 ? e.Message[..500] : e.Message);
                u.Parameters.AddWithValue("w", Math.Min(60, 1 << n)); u.Parameters.AddWithValue("i", m.id); await u.ExecuteNonQueryAsync(ct);
            }
        }
    }
}

/// <summary>Mail (module "mail"): how a customer sends mail, the outbox, and a test button. Administrators only.</summary>
public sealed class MailModule : IPlatformModule
{
    public string Key => "mail";
    public string Schema => "mail";

    public void AddServices(IServiceCollection s, IConfiguration cfg)
    {
        s.AddSingleton<ITenantSchema, MailSchema>();
        s.AddScoped<IMailQueue, MailQueue>();
        s.AddSingleton<IInboundFetcher, RealInboundFetcher>();
        s.AddSingleton<IJobHandler, MailFetchJob>();
        s.AddSingleton(new RecurringJob("mail.fetch", MailFetchJob.JobType, TimeSpan.FromMinutes(1)));
        s.AddSingleton<IJobHandler, MailJob>();
        s.AddSingleton(new RecurringJob("mail.flush", MailJob.JobType, TimeSpan.FromMinutes(1)));
    }

    static IResult Bad(string error, string? detail = null) => Results.BadRequest(new { error, detail });

    public void MapEndpoints(IEndpointRouteBuilder r)
    {
        var g = r.MapGroup("/api/platform/mail").RequireAuthorization(p => p.RequireRole("admin"));
        InboundApi.Map(g);
        TemplatesApi.Map(g);

        g.MapGet("/config", async (ISettings s, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            var c = await MailSettings.LoadAsync(s, dp, ct);
            return Results.Ok(new { provider = c.Provider, from = c.From, fromName = c.FromName, host = c.Host, port = c.Port, security = c.Security, user = c.User, hasPassword = c.Password != "", tenantId = c.TenantId, clientId = c.ClientId, hasSecret = c.Secret != "", publicUrl = c.PublicUrl, on = c.On });
        });

        g.MapPut("/config", async (JsonElement b, ISettings s, IDataProtectionProvider dp, IAuditLog audit, CancellationToken ct) =>
        {
            var old = await MailSettings.LoadAsync(s, dp, ct);
            string T(string n, string cur, int max) { if (!b.TryGetProperty(n, out var v) || v.ValueKind == JsonValueKind.Null) return cur; if (v.ValueKind != JsonValueKind.String) throw new FormatException(n + " must be text"); var x = v.GetString()!.Trim(); if (x.Length > max) throw new FormatException(n + " is too long"); return x; }
            try
            {
                var provider = T("provider", old.Provider, 10);
                if (provider is not ("none" or "smtp" or "graph")) return Bad("bad_provider", "provider must be none, smtp or graph");
                var port = b.TryGetProperty("port", out var pv) && pv.ValueKind == JsonValueKind.Number && pv.TryGetInt32(out var pn) ? pn : old.Port;
                var c = new MailConfig(provider, T("from", old.From, 254), T("fromName", old.FromName, 100), T("host", old.Host, 253), port, T("security", old.Security, 10), T("user", old.User, 254),
                    T("password", old.Password, 500), T("tenantId", old.TenantId, 100), T("clientId", old.ClientId, 100), T("secret", old.Secret, 500), T("publicUrl", old.PublicUrl, 200).TrimEnd('/'));
                if (c.Security is not ("none" or "starttls" or "ssl")) return Bad("bad_security", "security must be none, starttls or ssl");
                if (c.PublicUrl != "" && !(Uri.TryCreate(c.PublicUrl, UriKind.Absolute, out var pu) && pu.Scheme is "http" or "https")) return Bad("bad_public_url", "The public address must start with https:// (or http://).");
                if (provider != "none" && !c.From.Contains('@')) return Bad("from_required", "Enter the address mail is sent from.");
                if (provider == "smtp" && (c.Host == "" || c.Port is < 1 or > 65535)) return Bad("smtp_incomplete", "Enter the mail server and its port.");
                if (provider == "graph" && (c.TenantId == "" || c.ClientId == "" || c.Secret == "")) return Bad("graph_incomplete", "Enter the tenant id, application id and secret.");
                await MailSettings.SaveAsync(s, dp, c, ct);
                await audit.WriteAsync("mail.config_changed", provider, c.From, ct: ct);
                return Results.Ok(new { ok = true });
            }
            catch (FormatException e) { return Bad("bad_request", e.Message); }
        });

        g.MapPost("/test", async (JsonElement b, ISettings s, IDataProtectionProvider dp, IAuditLog audit, CancellationToken ct) =>
        {
            var to = b.TryGetProperty("to", out var tv) && tv.ValueKind == JsonValueKind.String ? tv.GetString()!.Trim() : "";
            if (!to.Contains('@') || to.Any(char.IsWhiteSpace)) return Bad("bad_address", "Enter the address to send the test to.");
            var c = await MailSettings.LoadAsync(s, dp, ct);
            if (!c.On) return Bad("not_set_up", "Save the mail settings first.");
            try { await MailSender.SendAsync(c, to, "DeskAsk test message", "If you can read this, DeskAsk can send mail.", ct); }
            catch (Exception e) when (e is not OperationCanceledException) { return Bad("send_failed", e.Message); }
            await audit.WriteAsync("mail.test_sent", to, "", ct: ct);
            return Results.Ok(new { ok = true });
        });

        g.MapGet("/outbox", async (HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = new NpgsqlConnection(t.Require().ConnectionString); await db.OpenAsync(ct);
            var st = http.Request.Query["status"].ToString();
            await using var c = new NpgsqlCommand("select id, to_addr, subject, reference, status, attempts, last_error, created_at, sent_at from mail.outbox where (@s = '' or status = @s) order by id desc limit 100", db);
            c.Parameters.AddWithValue("s", st);
            var items = new List<object>();
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) items.Add(new { id = rd.GetInt64(0), to = rd.GetString(1), subject = rd.GetString(2), reference = rd.GetString(3), status = rd.GetString(4), attempts = rd.GetInt32(5), error = rd.GetString(6), createdAt = rd.GetDateTime(7), sentAt = rd.IsDBNull(8) ? (DateTime?)null : rd.GetDateTime(8) });
            return Results.Ok(new { items });
        });

        g.MapPost("/outbox/{id:long}/retry", async (long id, ITenantContext t, IJobQueue jobs, CancellationToken ct) =>
        {
            await using var db = new NpgsqlConnection(t.Require().ConnectionString); await db.OpenAsync(ct);
            await using var c = new NpgsqlCommand("update mail.outbox set status = 'queued', attempts = 0, next_try_at = now() where id = @i and status = 'failed'", db);
            c.Parameters.AddWithValue("i", id);
            if (await c.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound(new { error = "not_found" });
            await jobs.EnqueueAsync(MailJob.JobType, "{}", null, "mail.send", ct);
            return Results.Ok(new { ok = true });
        });
    }
}
