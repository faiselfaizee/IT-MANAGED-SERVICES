using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using System.Security.Claims;

namespace DeskAsk.Platform.ServiceDesk;

/// <summary>
/// Helps the support team with a ticket: earlier tickets that look alike (and how they were answered), a suggested category and priority, and a reply to start from.
/// It works from the customer's own tickets with plain word matching. Nothing leaves the server and nothing is sent without a person reading it.
/// </summary>
internal static class AssistApi
{
    static readonly HashSet<string> Stop = new("the and for you your with that this have has not are was were but can cant cannot from they them their there when what why how all any our out get got just please hello thanks thank dear regards been will would could should still again also into than then its it's".Split(' '));
    static readonly string[] Critical = { "ransomware", "data breach", "hacked", "all users", "everyone", "whole office", "production down", "server down", "outage" };
    static readonly string[] High = { "cannot work", "can't work", "not working", "down", "urgent", "asap", "blocked", "no internet", "cannot login", "can't login", "unable to login", "virus", "malware", "crash" };
    static readonly string[] Low = { "request", "would like", "when possible", "no rush", "question", "how to", "install", "new account" };

    internal static HashSet<string> Words(string text) => Regex.Matches((text ?? "").ToLowerInvariant(), "[a-z0-9]{3,}").Select(m => m.Value).Where(w => !Stop.Contains(w)).ToHashSet();
    internal static double Similarity(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var shared = a.Count(b.Contains); return (double)shared / (a.Count + b.Count - shared);
    }
    internal static string? GuessPriority(string text)
    {
        var t = (text ?? "").ToLowerInvariant();
        if (Critical.Any(t.Contains)) return "critical";
        if (High.Any(t.Contains)) return "high";
        if (Low.Any(t.Contains)) return "low";
        return null;
    }
    static string First(string name) { var n = (name ?? "").Trim(); var i = n.IndexOf(' '); return i > 0 ? n[..i] : n; }

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        r.MapGet("/api/servicedesk/tickets/{id:long}/assist", async (long id, ClaimsPrincipal u, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var w = await ServiceDeskModule.WhoAsync(u, db, ct);
            if (!w.Staff) return Results.Json(new { error = "forbidden" }, statusCode: 403);
            string subject, description, category, priority, requester; string number;
            await using (var c = new NpgsqlCommand("select number, subject, description, category, priority, requester_name from servicedesk.tickets where id = @i", db))
            {
                ServiceDeskModule.P(c, "i", id); await using var rd = await c.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) return Results.NotFound();
                number = rd.GetString(0); subject = rd.GetString(1); description = rd.GetString(2); category = rd.GetString(3); priority = rd.GetString(4); requester = rd.GetString(5);
            }
            var mine = Words(subject + " " + subject + " " + description);   // the subject counts double
            var scored = new List<(long id, string number, string subject, string status, string category, string priority, double score)>();
            await using (var c = new NpgsqlCommand("select id, number, subject, description, status, category, priority from servicedesk.tickets where id <> @i order by id desc limit 800", db))
            {
                ServiceDeskModule.P(c, "i", id); await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                {
                    var s = Similarity(mine, Words(rd.GetString(2) + " " + rd.GetString(2) + " " + rd.GetString(3)));
                    if (s >= 0.12) scored.Add((rd.GetInt64(0), rd.GetString(1), rd.GetString(2), rd.GetString(4), rd.GetString(5), rd.GetString(6), s));
                }
            }
            var top = scored.OrderByDescending(x => x.score).Take(5).ToList();
            var similar = new List<object>(); string? solution = null; string? solvedBy = null;
            foreach (var x in top)
            {
                string? answer = null;
                if (x.status is "resolved" or "closed")
                {
                    await using var c = new NpgsqlCommand("select body from servicedesk.comments where ticket_id = @i and kind = 'public' and author_name <> (select requester_name from servicedesk.tickets where id = @i) order by id desc limit 1", db);
                    ServiceDeskModule.P(c, "i", x.id);
                    if (await c.ExecuteScalarAsync(ct) is string b) { answer = b.Length > 500 ? b[..500] + "…" : b; if (solution == null) { solution = answer; solvedBy = x.number; } }
                }
                similar.Add(new { id = x.id, number = x.number, subject = x.subject, status = x.status, category = x.category, priority = x.priority, score = Math.Round(x.score, 2), answer });
            }
            var votes = top.Where(x => x.category != "").GroupBy(x => x.category).Select(g => (cat: g.Key, sum: g.Sum(x => x.score))).OrderByDescending(g => g.sum).ToList();
            var suggestedCategory = votes.Count > 0 && votes[0].cat != category ? votes[0].cat : null;
            var guess = GuessPriority(subject + " " + description) ?? (top.Count >= 2 ? top.GroupBy(x => x.priority).OrderByDescending(g => g.Sum(x => x.score)).First().Key : null);
            var suggestedPriority = guess != null && guess != priority ? guess : null;
            var hi = First(requester); var greeting = hi == "" ? "Hello," : "Hello " + hi + ",";
            var draft = greeting + "\n\nThank you for contacting us about " + number + " (" + subject + ").\n\n" +
                (solution != null ? "A similar request was solved earlier like this:\n" + solution + "\n\nPlease try this and tell us if it helps. If it does not, we will look into it further.\n\n" : "We are looking into this and will update you as soon as we know more. Could you tell us when it started and whether anything changed before that?\n\n") +
                "Kind regards";
            return Results.Ok(new { similar, suggestedCategory, suggestedPriority, solvedBy, replyDraft = draft });
        }).RequireAuthorization().RequireModule(key);
    }
}
