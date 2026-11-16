using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DeskAsk.Platform.Mail;

/// <summary>One kind of message DeskAsk writes by itself, with the wording it starts with and the values that may be placed in it.</summary>
public sealed record TemplateDef(string Key, string Name, string When, string[] Values, string Subject, string Body);

public static class MailTemplates
{
    public const string SettingKey = "mail.templates";
    static readonly string[] Ticket = { "number", "subject", "status", "priority", "message", "requester", "company", "assignee", "by", "link", "site" };
    static readonly string[] Alert = { "severity", "device", "tag", "company", "rule", "message", "opened", "duration", "ticket", "link", "site" };
    static readonly string[] Renew = { "name", "items", "count", "first", "days", "link", "site" };
    static readonly string[] RenewVendor = { "vendor", "items", "count", "first", "days", "reply_to", "site" };
    static readonly string[] VaultReq = { "requester", "vault", "reason", "ticket", "minutes", "decision", "by", "note", "link", "site" };
    static readonly string[] VaultRot = { "account", "device", "vault", "error", "link", "site" };
    static readonly string[] Approval = { "number", "subject", "requester", "company", "item", "details", "link", "site" };
    static readonly string[] ChangeAp = { "number", "title", "type", "risk", "owner", "start", "link", "site" };
    static readonly string[] BackupAl = { "result", "job", "tool", "device", "company", "message", "last_success", "ticket", "link", "site" };
    static readonly string[] Report = { "company", "month", "availability", "devices", "outages", "alerts", "link", "site" };

    public static readonly TemplateDef[] All =
    {
        new("ticket_created", "Ticket opened", "Sent to the person who raised a ticket when it is opened.", Ticket,
            "[{{number}}] We received your request: {{subject}}",
            "Hello {{requester}},\n\nWe have received your request and opened ticket {{number}}.\n\nSubject: {{subject}}\nPriority: {{priority}}\n\n{{message}}\n\nYou can follow it here:\n{{link}}\n\nTo add information, reply to this email and keep the ticket number in the subject.\n\n{{site}}"),
        new("ticket_reply", "Reply from the support team", "Sent to the requester when the support team writes a public reply.", Ticket,
            "[{{number}}] New reply: {{subject}}",
            "Hello {{requester}},\n\n{{by}} replied to ticket {{number}}:\n\n{{message}}\n\nOpen the ticket:\n{{link}}\n\nYou can answer by replying to this email.\n\n{{site}}"),
        new("ticket_resolved", "Ticket resolved", "Sent to the requester when the support team marks a ticket resolved.", Ticket,
            "[{{number}}] Resolved: {{subject}}",
            "Hello {{requester}},\n\nTicket {{number}} has been marked as resolved.\n\n{{message}}\n\nIf the problem is not fixed, reply to this email and we will reopen it.\n{{link}}\n\n{{site}}"),
        new("ticket_customer_reply", "Customer replied", "Sent to the assigned engineer when the customer answers.", Ticket,
            "[{{number}}] Customer replied: {{subject}}",
            "Hello {{assignee}},\n\n{{by}} ({{company}}) replied to ticket {{number}}:\n\n{{message}}\n\nOpen the ticket:\n{{link}}"),
        new("ticket_assigned", "Ticket assigned", "Sent to an engineer when a ticket is assigned to them.", Ticket,
            "[{{number}}] Assigned to you: {{subject}}",
            "Hello {{assignee}},\n\n{{by}} assigned ticket {{number}} to you.\n\nSubject: {{subject}}\nPriority: {{priority}}\nRaised by: {{requester}} ({{company}})\n\n{{message}}\n\nOpen the ticket:\n{{link}}"),
        new("request_approval", "Request waiting for approval", "Sent to the approvers of a service catalogue item when someone asks for it.", Approval,
            "[{{number}}] Approval needed: {{item}} for {{requester}}",
            "{{requester}} ({{company}}) asked for {{item}}.\n\n{{details}}\n\nApprove or turn it down:\n{{link}}\n\n{{site}}"),
        new("change_approval", "Change waiting for approval", "Sent to the change advisory board (or every staff member when there is no board) when a change is submitted for approval.", ChangeAp,
            "[{{number}}] Change for approval: {{title}}",
            "{{owner}} submitted a {{type}} change for approval (risk: {{risk}}).\n\n{{title}}\nPlanned start: {{start}}\n\nReview and vote:\n{{link}}\n\n{{site}}"),
        new("monitoring_alert", "Monitoring alert", "Sent to the alert recipients when Monitoring opens an alert or raises it to critical.", Alert,
            "[{{severity}}] {{company}}: {{device}} - {{rule}}",
            "{{severity}} alert for {{device}} ({{tag}}, {{company}})\n\n{{message}}\n\nRule: {{rule}}\nSince: {{opened}}\n{{ticket}}\nOpen the device:\n{{link}}\n\n{{site}}"),
        new("monitoring_escalated", "Monitoring alert not acknowledged", "Sent to the escalation list when a critical alert has not been acknowledged in time.", Alert,
            "[Not acknowledged] {{company}}: {{device}} - {{rule}}",
            "Nobody has acknowledged this critical alert for {{duration}}.\n\n{{device}} ({{tag}}, {{company}})\n{{message}}\n\nRule: {{rule}}\nSince: {{opened}}\n\nOpen the device and acknowledge the alert:\n{{link}}\n\n{{site}}"),
        new("monitoring_report", "Monthly uptime report", "Sent on the 1st of the month with the uptime report attached as a PDF, if switched on in Monitoring settings.", Report,
            "Uptime report {{month}}: {{company}}",
            "Hello,\n\nThe uptime report for {{company}} for {{month}} is attached.\n\nDevices watched: {{devices}}\nAverage availability: {{availability}}\nOutages: {{outages}}\nAlerts: {{alerts}}\n\nMore detail in Monitoring:\n{{link}}\n\n{{site}}"),
        new("monitoring_resolved", "Monitoring alert cleared", "Sent to the alert recipients when an alert clears, if that is switched on in Monitoring settings.", Alert,
            "[Cleared] {{company}}: {{device}} - {{rule}}",
            "The alert for {{device}} ({{tag}}, {{company}}) has cleared after {{duration}}.\n\n{{message}}\n\nOpen the device:\n{{link}}\n\n{{site}}"),
        new("backup_alert", "Backup failed or missed", "Sent to the backup recipients when a backup job fails (after the number of failures set in Backup settings), finishes with a warning (if that alerts) or sends nothing in time.", BackupAl,
            "[Backup {{result}}] {{company}}: {{job}}",
            "Backup {{result}}: {{job}}\n\nSoftware: {{tool}}\nDevice: {{device}}\nCustomer: {{company}}\n\n{{message}}\n\nLast good backup: {{last_success}}\n{{ticket}}\nOpen the job:\n{{link}}\n\n{{site}}"),
        new("backup_resolved", "Backup back to normal", "Sent to the backup recipients when a job that alerted runs well again, if that is switched on in Backup settings.", BackupAl,
            "[Backup OK] {{company}}: {{job}}",
            "{{job}} ({{tool}}, {{device}}, {{company}}) is back to normal.\n\n{{message}}\n\nOpen the job:\n{{link}}\n\n{{site}}"),
        new("asset_renewal", "Warranty or AMC ending", "Sent to your team, the customer's contact and the asset's user one month and 15 days (Assets › Renewals settings) before a warranty or AMC contract ends.", Renew,
            "{{count}} renewal(s) due: {{first}}",
            "Hello {{name}},\n\nThese warranties and AMC contracts end soon:\n\n{{items}}\n\nPlease arrange the renewal in good time.\n\nOpen the renewals:\n{{link}}\n\n{{site}}"),
        new("asset_renewal_vendor", "Warranty or AMC ending (to the vendor)", "Sent to a vendor about the warranties and AMC contracts of assets bought from them, at the same times.", RenewVendor,
            "Renewal quotation requested: {{first}}",
            "Dear {{vendor}},\n\nThe following warranties and AMC contracts for equipment bought from you end soon:\n\n{{items}}\n\nPlease send us a renewal quotation. You can reply to {{reply_to}}.\n\nThank you,\n{{site}}"),
        new("vault_request", "Vault access requested", "Sent to the people who can approve when someone asks for access to an item in a vault. The item's name is encrypted, so the message names the vault.", VaultReq,
            "Access request: {{requester}} in {{vault}}",
            "{{requester}} asks for access to an item in the vault {{vault}} for {{minutes}} minutes.\n\nReason: {{reason}}\nTicket: {{ticket}}\n\nApprove or deny it in the Vault:\n{{link}}\n\n{{site}}"),
        new("vault_decision", "Vault request answered", "Sent to the person who asked for access when the request is approved or denied.", VaultReq,
            "Your access request was {{decision}}",
            "Hello {{requester}},\n\n{{by}} {{decision}} your request for access to an item in {{vault}}.\n{{note}}\n\nOpen the Vault:\n{{link}}\n\n{{site}}"),
        new("vault_rotation_failed", "Password change failed", "Sent to the vault's managers when the agent could not change a managed account's password.", VaultRot,
            "Password change failed: {{account}} on {{device}}",
            "The scheduled password change for {{account}} on {{device}} (vault {{vault}}) did not work.\n\n{{error}}\n\nThe password in the vault has not changed. Open the account:\n{{link}}\n\n{{site}}"),
    };

    public static TemplateDef? Find(string key) => All.FirstOrDefault(t => t.Key == key);

    static readonly Regex Slot = new(@"\{\{\s*(\w+)\s*\}\}", RegexOptions.Compiled);

    /// <summary>The words in use: the customer's own, or the defaults.</summary>
    public static async Task<(string subject, string body)> CurrentAsync(ISettings s, TemplateDef def, CancellationToken ct)
    {
        var all = await CustomAsync(s, ct);
        return all.TryGetValue(def.Key, out var c) ? c : (def.Subject, def.Body);
    }

    public static async Task<Dictionary<string, (string subject, string body)>> CustomAsync(ISettings s, CancellationToken ct)
    {
        var d = new Dictionary<string, (string, string)>();
        var raw = await s.GetAsync(SettingKey, ct);
        if (string.IsNullOrWhiteSpace(raw)) return d;
        try
        {
            foreach (var p in JsonDocument.Parse(raw).RootElement.EnumerateObject())
                if (p.Value.TryGetProperty("subject", out var sj) && p.Value.TryGetProperty("body", out var bd) && sj.ValueKind == JsonValueKind.String && bd.ValueKind == JsonValueKind.String) d[p.Name] = (sj.GetString()!, bd.GetString()!);
        }
        catch (JsonException) { }
        return d;
    }

    public static async Task SaveCustomAsync(ISettings s, Dictionary<string, (string subject, string body)> all, CancellationToken ct) =>
        await s.SetAsync(SettingKey, JsonSerializer.Serialize(all.ToDictionary(x => x.Key, x => new { subject = x.Value.subject, body = x.Value.body })), ct);

    public static string BaseUrl(MailConfig c, TenantInfo t) => c.PublicUrl != "" ? c.PublicUrl.TrimEnd('/') : "https://" + t.Host;

    /// <summary>Unknown {{names}} in a text, so a typo is caught when saving instead of reaching a customer.</summary>
    public static string[] Unknown(TemplateDef def, string text) => Slot.Matches(text).Select(m => m.Groups[1].Value).Where(n => !def.Values.Contains(n)).Distinct().ToArray();

    static string Fill(string text, Dictionary<string, string> v) => Slot.Replace(text, m => v.TryGetValue(m.Groups[1].Value, out var x) ? x : "");

    /// <summary>Writes the subject, a plain text body and a styled HTML body. Values are plain text; the HTML version escapes them.</summary>
    public static (string subject, string text, string html) Render(TemplateDef def, string subject, string body, IReadOnlyDictionary<string, string> values, string baseUrl, string site)
    {
        var v = new Dictionary<string, string>(values, StringComparer.Ordinal);
        v["site"] = site;
        v["link"] = v.TryGetValue("path", out var path) && path != "" ? baseUrl + "/" + path.TrimStart('/') : baseUrl;
        var sub = Regex.Replace(Fill(subject, v), @"\s+", " ").Trim();
        var text = Regex.Replace(Fill(body, v).Replace("\r\n", "\n"), @"\n{3,}", "\n\n").Trim();
        return (sub, text, Html(text, v["link"], site, v.ContainsKey("path")));
    }

    static readonly Regex Url = new(@"https?://[^\s<]+", RegexOptions.Compiled);

    static string Html(string text, string link, string site, bool button)
    {
        var esc = WebUtility.HtmlEncode(text);
        esc = Url.Replace(esc, m => { var u = m.Value.TrimEnd('.', ',', ')'); var rest = m.Value[u.Length..]; return $"<a href=\"{u}\" style=\"color:#0f766e\">{u}</a>{rest}"; });
        esc = esc.Replace("\n", "<br>");
        var sb = new StringBuilder();
        sb.Append("<div style=\"font-family:'Segoe UI',Arial,sans-serif;background:#f1f5f9;padding:24px\"><div style=\"max-width:600px;margin:0 auto;background:#ffffff;border-radius:12px;overflow:hidden;border:1px solid #e2e8f0\">");
        sb.Append($"<div style=\"background:#0f766e;color:#ffffff;padding:14px 22px;font-size:16px;font-weight:600\">{WebUtility.HtmlEncode(site)}</div>");
        sb.Append($"<div style=\"padding:22px;color:#1f2937;font-size:14px;line-height:1.6\">{esc}");
        if (button) sb.Append($"<p style=\"margin:22px 0 4px\"><a href=\"{WebUtility.HtmlEncode(link)}\" style=\"background:#0f766e;color:#ffffff;text-decoration:none;padding:10px 18px;border-radius:8px;display:inline-block\">Open in DeskAsk</a></p>");
        sb.Append("</div><div style=\"padding:12px 22px;color:#64748b;font-size:12px;border-top:1px solid #eef2f6\">Reply to this email to answer. Please keep the ticket number in the subject.</div></div></div>");
        return sb.ToString();
    }

    public static readonly Dictionary<string, string> Sample = new()
    {
        ["number"] = "INC-0123", ["subject"] = "Printer on the third floor is down", ["status"] = "Open", ["priority"] = "High",
        ["message"] = "The printer stopped working this morning. Paper is loaded and the display shows an error.", ["requester"] = "Pat Morgan", ["company"] = "Acme Ltd",
        ["assignee"] = "Sam Rivers", ["by"] = "Sam Rivers", ["path"] = "#/servicedesk/123",
        ["name"] = "Pat Morgan", ["vendor"] = "Acme Systems Pvt Ltd", ["count"] = "2", ["first"] = "Warranty of LAP-0042 (Dell Latitude 5440)", ["days"] = "30", ["reply_to"] = "renewals@yourcompany.example",
        ["item"] = "New laptop", ["details"] = "Model: Standard laptop\nNeeded by: 2026-12-01", ["title"] = "Patch the file server", ["type"] = "normal", ["risk"] = "medium", ["owner"] = "Sam Rivers", ["start"] = "20 Dec 2026 22:00 UTC",
        ["items"] = "- Warranty: LAP-0042 Dell Latitude 5440, serial 8HX2LQ3 (Acme Ltd, Bengaluru HQ) ends 14 December 2026, bought 15 December 2023, PO 4500012\n- AMC contract: Server room AMC, contract AMC-2025-118 (Acme Ltd) ends 20 December 2026",
        ["result"] = "Failed", ["job"] = "Nightly file server backup", ["tool"] = "Veeam", ["device"] = "FS01", ["last_success"] = "2026-11-14 23:10 UTC", ["ticket"] = "Ticket: INC-0124",
    };
}

public static class TemplatesApi
{
    static IResult Bad(string error, string? detail = null) => Results.BadRequest(new { error, detail });

    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/templates", async (ISettings s, CancellationToken ct) =>
        {
            var custom = await MailTemplates.CustomAsync(s, ct);
            return Results.Ok(new
            {
                items = MailTemplates.All.Select(d =>
                {
                    var has = custom.TryGetValue(d.Key, out var c);
                    return new { key = d.Key, name = d.Name, when = d.When, values = d.Values, subject = has ? c.subject : d.Subject, body = has ? c.body : d.Body, custom = has, defaultSubject = d.Subject, defaultBody = d.Body };
                }),
            });
        });

        g.MapPut("/templates/{key}", async (string key, JsonElement b, ISettings s, IAuditLog audit, CancellationToken ct) =>
        {
            var def = MailTemplates.Find(key); if (def == null) return Results.NotFound(new { error = "not_found" });
            var sub = b.ValueKind == JsonValueKind.Object && b.TryGetProperty("subject", out var sv) && sv.ValueKind == JsonValueKind.String ? sv.GetString()!.Trim() : "";
            var body = b.ValueKind == JsonValueKind.Object && b.TryGetProperty("body", out var bv) && bv.ValueKind == JsonValueKind.String ? bv.GetString()!.Trim() : "";
            if (sub == "") return Bad("subject_required", "The subject cannot be empty.");
            if (body == "") return Bad("body_required", "The message cannot be empty.");
            if (sub.Length > 200 || sub.Contains('\n')) return Bad("bad_subject", "The subject must be one line of at most 200 characters.");
            if (body.Length > 8000) return Bad("body_too_long", "The message is too long.");
            var bad = MailTemplates.Unknown(def, sub).Concat(MailTemplates.Unknown(def, body)).Distinct().ToArray();
            if (bad.Length > 0) return Bad("unknown_value", "These names are not available in this message: " + string.Join(", ", bad.Select(x => "{{" + x + "}}")) + ". You can use: " + string.Join(", ", def.Values.Select(x => "{{" + x + "}}")));
            var all = await MailTemplates.CustomAsync(s, ct); all[key] = (sub, body);
            await MailTemplates.SaveCustomAsync(s, all, ct);
            await audit.WriteAsync("mail.template_changed", key, "", ct: ct);
            return Results.Ok(new { ok = true });
        });

        g.MapDelete("/templates/{key}", async (string key, ISettings s, IAuditLog audit, CancellationToken ct) =>
        {
            if (MailTemplates.Find(key) == null) return Results.NotFound(new { error = "not_found" });
            var all = await MailTemplates.CustomAsync(s, ct);
            if (all.Remove(key)) { await MailTemplates.SaveCustomAsync(s, all, ct); await audit.WriteAsync("mail.template_reset", key, "", ct: ct); }
            return Results.Ok(new { ok = true });
        });

        g.MapPost("/templates/{key}/preview", async (string key, JsonElement b, ISettings s, Microsoft.AspNetCore.DataProtection.IDataProtectionProvider dp, ITenantContext t, CancellationToken ct) =>
        {
            var def = MailTemplates.Find(key); if (def == null) return Results.NotFound(new { error = "not_found" });
            var sub = b.TryGetProperty("subject", out var sv) && sv.ValueKind == JsonValueKind.String ? sv.GetString()! : def.Subject;
            var body = b.TryGetProperty("body", out var bv) && bv.ValueKind == JsonValueKind.String ? bv.GetString()! : def.Body;
            var cfg = await MailSettings.LoadAsync(s, dp, ct); var ten = t.Require();
            var (subject, text, html) = MailTemplates.Render(def, sub, body, MailTemplates.Sample, MailTemplates.BaseUrl(cfg, ten), cfg.FromName != "" ? cfg.FromName : ten.Name);
            return Results.Ok(new { subject, text, html });
        });

        g.MapPost("/templates/{key}/test", async (string key, JsonElement b, ISettings s, Microsoft.AspNetCore.DataProtection.IDataProtectionProvider dp, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var def = MailTemplates.Find(key); if (def == null) return Results.NotFound(new { error = "not_found" });
            var to = b.TryGetProperty("to", out var tv) && tv.ValueKind == JsonValueKind.String ? tv.GetString()!.Trim() : "";
            if (!to.Contains('@') || to.Any(char.IsWhiteSpace)) return Bad("bad_address", "Enter the address to send the example to.");
            var cfg = await MailSettings.LoadAsync(s, dp, ct); if (!cfg.On) return Bad("not_set_up", "Save the mail settings first.");
            var (sub, body) = await MailTemplates.CurrentAsync(s, def, ct); var ten = t.Require();
            var (subject, text, html) = MailTemplates.Render(def, sub, body, MailTemplates.Sample, MailTemplates.BaseUrl(cfg, ten), cfg.FromName != "" ? cfg.FromName : ten.Name);
            try { await MailSender.SendAsync(cfg, to, "[Example] " + subject, text, ct, html); }
            catch (Exception e) when (e is not OperationCanceledException) { return Bad("send_failed", e.Message); }
            await audit.WriteAsync("mail.template_test", key, to, ct: ct);
            return Results.Ok(new { ok = true });
        });
    }
}
