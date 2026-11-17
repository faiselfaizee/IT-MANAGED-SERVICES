using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace DeskAsk.Platform.Monitoring;

/// <summary>
/// Microsoft Teams messages. A webhook (from a Teams Workflow "Post to a channel when a webhook request is received", or an older
/// incoming webhook) receives an Adaptive Card. Posts are queued and tried again (after 1, 2, 4, 8 minutes) when Teams does not answer.
/// Webhook addresses are stored sealed.
/// </summary>
public static class Teams
{
    public const string Purpose = "dk.monitoring";
    static readonly HttpClient Http = new(OutboundGuard.Handler()) { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Why a webhook address cannot be used, or null. Plain http is allowed only on this computer (for testing).</summary>
    public static string? Check(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "The Teams webhook must be a full address starting with https://.";
        if (u.Scheme == Uri.UriSchemeHttps) return null;
        if (u.Scheme == Uri.UriSchemeHttp && (u.IsLoopback || u.Host == "localhost")) return null;
        return "The Teams webhook must start with https://.";
    }

    /// <summary>An Adaptive Card message: a coloured title, the text, a few facts and a button that opens the device.</summary>
    public static string Card(string title, string tone, string text, IEnumerable<(string Title, string Value)> facts, string? link, string linkTitle = "Open in DeskAsk")
    {
        var body = new List<object>
        {
            new { type = "TextBlock", text = title, weight = "Bolder", size = "Medium", wrap = true, color = tone },
            new { type = "TextBlock", text, wrap = true },
            new { type = "FactSet", facts = facts.Where(f => f.Value != "").Select(f => new { title = f.Title, value = f.Value }).ToArray() },
        };
        var card = new Dictionary<string, object>
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json", ["type"] = "AdaptiveCard", ["version"] = "1.4", ["body"] = body,
            ["msteams"] = new { width = "Full" },
        };
        if (link != null) card["actions"] = new[] { new { type = "Action.OpenUrl", title = linkTitle, url = link } };
        return JsonSerializer.Serialize(new { type = "message", attachments = new[] { new { contentType = "application/vnd.microsoft.card.adaptive", contentUrl = (string?)null, content = card } } });
    }

    /// <summary>Posts one message now. Returns null when Teams accepted it, else what went wrong.</summary>
    public static async Task<string?> PostAsync(string url, string json, CancellationToken ct)
    {
        try
        {
            using var res = await Http.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"), ct);
            if ((int)res.StatusCode is >= 200 and < 300) return null;
            var text = await res.Content.ReadAsStringAsync(ct);
            return $"Teams answered {(int)res.StatusCode} {res.StatusCode}" + (text != "" ? ": " + (text.Length > 200 ? text[..200] : text) : "");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return "Teams could not be reached: " + e.Message; }
    }

    /// <summary>Queues a message for a sealed webhook address.</summary>
    public static async Task QueueAsync(NpgsqlConnection db, long? alertId, string sealedUrl, string json, CancellationToken ct)
    {
        if (sealedUrl == "") return;
        await using var c = new NpgsqlCommand("insert into monitoring.outbox (alert_id, url, body) values (@a, @u, @b)", db);
        MonitoringModule.P(c, "a", alertId); MonitoringModule.P(c, "u", sealedUrl); MonitoringModule.P(c, "b", json);
        await c.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Sends what is due. Gives up on a message after five tries; sent and failed messages are kept seven days.</summary>
    public static async Task SendDueAsync(NpgsqlConnection db, IDataProtector p, CancellationToken ct)
    {
        var due = new List<(long Id, string Url, string Body, int Attempts)>();
        await using (var c = new NpgsqlCommand("select id, url, body, attempts from monitoring.outbox where sent_at is null and attempts < 5 and next_at <= now() order by id limit 50", db))
        {
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) due.Add((rd.GetInt64(0), rd.GetString(1), rd.GetString(2), rd.GetInt32(3)));
        }
        foreach (var d in due)
        {
            string? err; string url;
            try { url = p.Unprotect(d.Url); } catch (Exception) { url = ""; }
            err = url == "" ? "The saved Teams webhook cannot be opened: save it again in Monitoring settings." : await PostAsync(url, d.Body, ct);
            await using var u = new NpgsqlCommand(err == null ? "update monitoring.outbox set sent_at = now(), attempts = attempts + 1, error = '' where id = @i"
                : "update monitoring.outbox set attempts = attempts + 1, error = @e, next_at = now() + make_interval(mins => power(2, attempts)::int) where id = @i", db);
            MonitoringModule.P(u, "i", d.Id); if (err != null) MonitoringModule.P(u, "e", err);
            await u.ExecuteNonQueryAsync(ct);
        }
        await using (var c = new NpgsqlCommand("delete from monitoring.outbox where created_at < now() - interval '7 days'", db)) await c.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The address links point to: the public address set in Mail settings, else the customer's own host name.</summary>
    public static async Task<string> BaseAsync(ISettings s, TenantInfo t, CancellationToken ct)
    {
        var raw = await s.GetAsync("mail.config", ct);
        if (!string.IsNullOrWhiteSpace(raw))
            try { var e = JsonDocument.Parse(raw).RootElement; if (e.TryGetProperty("publicUrl", out var v) && v.GetString() is { Length: > 0 } pu) return pu.TrimEnd('/'); } catch (JsonException) { }
        return "https://" + t.Host;
    }
}
