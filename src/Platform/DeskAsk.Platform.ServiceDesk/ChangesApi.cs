using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace DeskAsk.Platform.ServiceDesk;

/// <summary>
/// Changes and problems. Staff (admin, engineer) work them, viewers read, customers never see them.
/// A normal change needs approvals from the change board (any staff other than its owner) up to its quorum; an emergency change needs one;
/// a standard change is pre-approved. Problems record root cause, workaround and the tickets and changes they relate to.
/// </summary>
public static class ChangesApi
{
    static readonly string[] Types = { "normal", "standard", "emergency" };
    static readonly string[] Levels = { "low", "medium", "high" };
    static readonly string[] Prios = { "critical", "high", "medium", "low" };
    static readonly string[] PStatuses = { "open", "investigating", "known_error", "resolved", "closed" };
    const string CCols = "c.id, c.number, c.title, c.description, c.type, c.status, c.risk, c.impact, c.reason, c.plan, c.rollback, c.outcome, c.company_id, c.company_name, c.owner_id, c.owner_name, c.start_at, c.end_at, c.quorum, c.created_at, c.updated_at, c.created_by";
    const string PCols = "p.id, p.number, p.title, p.description, p.status, p.priority, p.category, p.root_cause, p.workaround, p.resolution, p.owner_id, p.owner_name, p.created_at, p.updated_at, p.created_by";

    static object Change(NpgsqlDataReader r) => new
    {
        id = r.GetInt64(0), number = r.GetString(1), title = r.GetString(2), description = r.GetString(3), type = r.GetString(4), status = r.GetString(5),
        risk = r.GetString(6), impact = r.GetString(7), reason = r.GetString(8), plan = r.GetString(9), rollback = r.GetString(10), outcome = r.GetString(11),
        companyId = r.IsDBNull(12) ? (int?)null : r.GetInt32(12), companyName = r.GetString(13), ownerId = r.IsDBNull(14) ? (Guid?)null : r.GetGuid(14), ownerName = r.GetString(15),
        startAt = r.IsDBNull(16) ? (DateTime?)null : r.GetDateTime(16), endAt = r.IsDBNull(17) ? (DateTime?)null : r.GetDateTime(17), quorum = r.GetInt32(18),
        createdAt = r.GetDateTime(19), updatedAt = r.GetDateTime(20), createdBy = r.GetString(21),
    };

    static object Problem(NpgsqlDataReader r) => new
    {
        id = r.GetInt64(0), number = r.GetString(1), title = r.GetString(2), description = r.GetString(3), status = r.GetString(4), priority = r.GetString(5),
        category = r.GetString(6), rootCause = r.GetString(7), workaround = r.GetString(8), resolution = r.GetString(9),
        ownerId = r.IsDBNull(10) ? (Guid?)null : r.GetGuid(10), ownerName = r.GetString(11), createdAt = r.GetDateTime(12), updatedAt = r.GetDateTime(13), createdBy = r.GetString(14),
    };

    static string? Text(JsonElement b, string name, int max, out bool present)
    {
        present = b.TryGetProperty(name, out var v);
        if (!present) return null;
        if (v.ValueKind == JsonValueKind.Null) return "";
        if (v.ValueKind != JsonValueKind.String) throw new FormatException(name + " must be text");
        var s = v.GetString()!.Trim();
        if (s.Length > max) throw new FormatException(name + " is longer than " + max + " characters");
        return s;
    }

    static DateTime? When(JsonElement b, string name, out bool present)
    {
        present = b.TryGetProperty(name, out var v);
        if (!present || v.ValueKind == JsonValueKind.Null || (v.ValueKind == JsonValueKind.String && v.GetString() == "")) return null;
        if (v.ValueKind != JsonValueKind.String || !DateTime.TryParse(v.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d)) throw new FormatException(name + " must be a date and time");
        return d;
    }

    static List<long> Ids(JsonElement b, string name)
    {
        var l = new List<long>();
        if (b.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array) foreach (var e in v.EnumerateArray()) if (e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var n)) l.Add(n);
        return l.Distinct().ToList();
    }

    static async Task<int> Quorum(ISettings s, string type, CancellationToken ct)
    {
        if (type == "standard") return 0;
        if (type == "emergency") return 1;
        var raw = await s.GetAsync("servicedesk.cab", ct);
        try { if (!string.IsNullOrWhiteSpace(raw) && JsonDocument.Parse(raw).RootElement.TryGetProperty("quorum", out var q) && q.TryGetInt32(out var n)) return Math.Clamp(n, 1, 9); } catch (JsonException) { }
        return 2;
    }

    static async Task Note(NpgsqlConnection db, string kind, long id, string by, string body, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("insert into servicedesk.item_notes (kind, item_id, author_name, body) values (@k, @i, @a, @b)", db);
        ServiceDeskModule.P(c, "k", kind); ServiceDeskModule.P(c, "i", id); ServiceDeskModule.P(c, "a", by); ServiceDeskModule.P(c, "b", body);
        await c.ExecuteNonQueryAsync(ct);
    }

    static async Task<List<object>> Notes(NpgsqlConnection db, string kind, long id, CancellationToken ct)
    {
        var l = new List<object>();
        await using var c = new NpgsqlCommand("select id, at, author_name, body from servicedesk.item_notes where kind = @k and item_id = @i order by id", db);
        ServiceDeskModule.P(c, "k", kind); ServiceDeskModule.P(c, "i", id);
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) l.Add(new { id = r.GetInt64(0), at = r.GetDateTime(1), author = r.GetString(2), body = r.GetString(3) });
        return l;
    }

    static async Task<List<object>> TicketLinks(NpgsqlConnection db, string table, string col, long id, CancellationToken ct)
    {
        var l = new List<object>();
        await using var c = new NpgsqlCommand($"select t.id, t.number, t.subject, t.status, t.priority from servicedesk.{table} l join servicedesk.tickets t on t.id = l.ticket_id where l.{col} = @i order by t.id", db);
        ServiceDeskModule.P(c, "i", id);
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) l.Add(new { id = r.GetInt64(0), number = r.GetString(1), subject = r.GetString(2), status = r.GetString(3), priority = r.GetString(4) });
        return l;
    }

    static async Task SetLinks(NpgsqlConnection db, string table, string col, long id, string other, List<long> ids, CancellationToken ct)
    {
        await using (var d = new NpgsqlCommand($"delete from servicedesk.{table} where {col} = @i", db)) { ServiceDeskModule.P(d, "i", id); await d.ExecuteNonQueryAsync(ct); }
        if (ids.Count == 0) return;
        var src = other == "ticket_id" ? "servicedesk.tickets" : "servicedesk.problems";
        await using var a = new NpgsqlCommand($"insert into servicedesk.{table} ({col}, {other}) select @i, x.id from {src} x where x.id = any(@ids) on conflict do nothing", db);
        ServiceDeskModule.P(a, "i", id); ServiceDeskModule.P(a, "ids", ids.ToArray());
        await a.ExecuteNonQueryAsync(ct);
    }

    static IResult Forbidden(string why) => Results.Json(new { error = "forbidden", detail = why }, statusCode: 403);
    static IResult NotFound() => Results.NotFound(new { error = "not_found" });

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        var read = (RouteHandlerBuilder b) => b.RequireAuthorization().RequireModule(key);

        // ================================================================ changes
        read(r.MapGet("/api/servicedesk/changes", async (HttpContext http, ClaimsPrincipal u, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var w = await ServiceDeskModule.WhoAsync(u, db, ct);
            if (w.Customer || w.Role == "") return Forbidden("Changes are for the support team.");
            var q = http.Request.Query;
            await using var c = new NpgsqlCommand { Connection = db };
            var where = new List<string> { "true" };
            if (q["status"].ToString() == "open") where.Add("c.status not in ('completed','failed','cancelled','rejected')");
            else if (q["status"].ToString() is { Length: > 0 } st) { ServiceDeskModule.P(c, "st", st.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); where.Add("c.status = any(@st)"); }
            if (q["type"].ToString() is { Length: > 0 } ty) { ServiceDeskModule.P(c, "ty", ty); where.Add("c.type = @ty"); }
            if (q["q"].ToString().Trim() is { Length: > 0 } text) { ServiceDeskModule.P(c, "q", "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"); where.Add("(c.number ilike @q or c.title ilike @q or c.company_name ilike @q)"); }
            c.CommandText = $"select {CCols}, (select count(*) from servicedesk.change_votes v where v.change_id = c.id and v.decision = 'approve') from servicedesk.changes c where {string.Join(" and ", where)} order by coalesce(c.start_at, c.updated_at) desc, c.id desc limit 200";
            var items = new List<object>();
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) { var o = Change(rd); items.Add(new { change = o, approvals = rd.GetInt64(22) }); }
            return Results.Ok(new { items = items.Select(x => x).ToList() });
        }));

        read(r.MapGet("/api/servicedesk/changes/{id:long}", async (long id, ClaimsPrincipal u, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var w = await ServiceDeskModule.WhoAsync(u, db, ct);
            if (w.Customer || w.Role == "") return Forbidden("Changes are for the support team.");
            object? change = null;
            await using (var c = new NpgsqlCommand($"select {CCols} from servicedesk.changes c where c.id = @i", db))
            {
                ServiceDeskModule.P(c, "i", id);
                await using var rd = await c.ExecuteReaderAsync(ct);
                if (await rd.ReadAsync(ct)) change = Change(rd);
            }
            if (change == null) return NotFound();
            var votes = new List<object>();
            await using (var c = new NpgsqlCommand("select user_id, user_name, decision, comment, at from servicedesk.change_votes where change_id = @i order by at", db))
            {
                ServiceDeskModule.P(c, "i", id);
                await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) votes.Add(new { userId = rd.GetGuid(0), name = rd.GetString(1), decision = rd.GetString(2), comment = rd.GetString(3), at = rd.GetDateTime(4) });
            }
            var problems = new List<object>();
            await using (var c = new NpgsqlCommand("select p.id, p.number, p.title, p.status from servicedesk.change_problems l join servicedesk.problems p on p.id = l.problem_id where l.change_id = @i order by p.id", db))
            {
                ServiceDeskModule.P(c, "i", id);
                await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) problems.Add(new { id = rd.GetInt64(0), number = rd.GetString(1), title = rd.GetString(2), status = rd.GetString(3) });
            }
            return Results.Ok(new { change, votes, notes = await Notes(db, "change", id, ct), tickets = await TicketLinks(db, "change_tickets", "change_id", id, ct), problems });
        }));

        read(r.MapPost("/api/servicedesk/changes", async (JsonElement b, ClaimsPrincipal u, ITenantContext t, ISettings s, ICompanyDirectory companies, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var w = await ServiceDeskModule.WhoAsync(u, db, ct);
            if (!w.Staff) return Forbidden("Only the support team can raise changes.");
            try
            {
                var title = Text(b, "title", 200, out _) ?? "";
                if (title == "") return ServiceDeskModule.Bad("title_required", "Give the change a title.");
                var type = Text(b, "type", 20, out _) is { Length: > 0 } ty ? ty : "normal";
                if (!Types.Contains(type)) return ServiceDeskModule.Bad("bad_type", "type must be " + string.Join(", ", Types));
                var risk = Text(b, "risk", 20, out _) is { Length: > 0 } rk ? rk : "low"; var impact = Text(b, "impact", 20, out _) is { Length: > 0 } im ? im : "low";
                if (!Levels.Contains(risk) || !Levels.Contains(impact)) return ServiceDeskModule.Bad("bad_level", "risk and impact must be low, medium or high");
                var start = When(b, "startAt", out _); var end = When(b, "endAt", out _);
                if (start != null && end != null && end <= start) return ServiceDeskModule.Bad("bad_window", "The end must be after the start.");
                int? companyId = null; var companyName = "";
                if (b.TryGetProperty("companyId", out var co) && co.ValueKind == JsonValueKind.Number)
                {
                    var cr = await companies.GetAsync(co.GetInt32(), ct);
                    if (cr == null) return ServiceDeskModule.Bad("bad_company"); companyId = cr.Id; companyName = cr.Name;
                }
                await using var c = new NpgsqlCommand(@"insert into servicedesk.changes (title, description, type, risk, impact, reason, plan, rollback, company_id, company_name, owner_id, owner_name, start_at, end_at, quorum, created_by)
                    values (@t, @d, @ty, @rk, @im, @re, @pl, @rb, @ci, @cn, @oi, @on, @s, @e, @q, @cb) returning id, number", db);
                ServiceDeskModule.P(c, "t", title); ServiceDeskModule.P(c, "d", Text(b, "description", 8000, out _) ?? ""); ServiceDeskModule.P(c, "ty", type); ServiceDeskModule.P(c, "rk", risk); ServiceDeskModule.P(c, "im", impact);
                ServiceDeskModule.P(c, "re", Text(b, "reason", 4000, out _) ?? ""); ServiceDeskModule.P(c, "pl", Text(b, "plan", 8000, out _) ?? ""); ServiceDeskModule.P(c, "rb", Text(b, "rollback", 8000, out _) ?? "");
                ServiceDeskModule.P(c, "ci", companyId); ServiceDeskModule.P(c, "cn", companyName); ServiceDeskModule.P(c, "oi", w.Id); ServiceDeskModule.P(c, "on", w.Name);
                ServiceDeskModule.P(c, "s", start); ServiceDeskModule.P(c, "e", end); ServiceDeskModule.P(c, "q", await Quorum(s, type, ct)); ServiceDeskModule.P(c, "cb", w.Name);
                await using var rd = await c.ExecuteReaderAsync(ct);
                await rd.ReadAsync(ct);
                var id = rd.GetInt64(0); var number = rd.GetString(1);
                await rd.CloseAsync();
                var tickets = Ids(b, "tickets"); if (tickets.Count > 0) await SetLinks(db, "change_tickets", "change_id", id, "ticket_id", tickets, ct);
                await audit.WriteAsync("servicedesk.change_created", number, title, ct: ct);
                return Results.Ok(new { id, number });
            }
            catch (FormatException e) { return ServiceDeskModule.Bad("bad_request", e.Message); }
        }));

        read(r.MapMethods("/api/servicedesk/changes/{id:long}", new[] { "PATCH" }, async (long id, JsonElement b, ClaimsPrincipal u, ITenantContext t, IUserDirectory users, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var w = await ServiceDeskModule.WhoAsync(u, db, ct);
            if (!w.Staff) return Forbidden("Only the support team can edit changes.");
            string status, number;
            await using (var c = new NpgsqlCommand("select status, number from servicedesk.changes where id = @i", db))
            {
                ServiceDeskModule.P(c, "i", id);
                await using var rd = await c.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) return NotFound();
                status = rd.GetString(0); number = rd.GetString(1);
            }
            try
            {
                var sets = new List<string>(); await using var u2 = new NpgsqlCommand { Connection = db }; ServiceDeskModule.P(u2, "i", id);
                var planning = status is "draft" or "rejected" or "review";
                void Set(string col, string prm, object? v) { sets.Add(col + " = @" + prm); ServiceDeskModule.P(u2, prm, v); }
                foreach (var (name, col, max) in new[] { ("title", "title", 200), ("description", "description", 8000), ("reason", "reason", 4000), ("plan", "plan", 8000), ("rollback", "rollback", 8000) })
                {
                    var v = Text(b, name, max, out var has); if (!has) continue;
                    if (!planning && name != "description") return ServiceDeskModule.Bad("locked", "A change that is " + status + " can no longer have its plan edited.");
                    if (name == "title" && v == "") return ServiceDeskModule.Bad("title_required");
                    Set(col, col, v);
                }
                foreach (var name in new[] { "risk", "impact" })
                {
                    var v = Text(b, name, 20, out var has); if (!has) continue;
                    if (!Levels.Contains(v!)) return ServiceDeskModule.Bad("bad_level", name + " must be low, medium or high");
                    if (!planning) return ServiceDeskModule.Bad("locked", "A change that is " + status + " can no longer have its risk edited.");
                    Set(name, name, v);
                }
                var ownerSet = b.TryGetProperty("ownerId", out var ow);
                if (ownerSet && ow.ValueKind == JsonValueKind.String && Guid.TryParse(ow.GetString(), out var og)) { Set("owner_id", "oi", og); Set("owner_name", "on", await OwnerName(users, og, ct)); }
                var s2 = When(b, "startAt", out var hs); var e2 = When(b, "endAt", out var he);
                if (hs) Set("start_at", "s", s2); if (he) Set("end_at", "e", e2);
                if (sets.Count == 0 && !b.TryGetProperty("tickets", out _) && !b.TryGetProperty("problems", out _)) return ServiceDeskModule.Bad("nothing_to_change");
                if (hs || he)
                {
                    await using var chk = new NpgsqlCommand("select start_at, end_at from servicedesk.changes where id = @i", db); ServiceDeskModule.P(chk, "i", id);
                    await using var rd = await chk.ExecuteReaderAsync(ct); await rd.ReadAsync(ct);
                    var fs = hs ? s2 : (rd.IsDBNull(0) ? (DateTime?)null : rd.GetDateTime(0)); var fe = he ? e2 : (rd.IsDBNull(1) ? (DateTime?)null : rd.GetDateTime(1));
                    if (fs != null && fe != null && fe <= fs) return ServiceDeskModule.Bad("bad_window", "The end must be after the start.");
                }
                if (sets.Count > 0) { u2.CommandText = "update servicedesk.changes set " + string.Join(", ", sets) + ", updated_at = now() where id = @i"; await u2.ExecuteNonQueryAsync(ct); }
                if (b.TryGetProperty("tickets", out _)) await SetLinks(db, "change_tickets", "change_id", id, "ticket_id", Ids(b, "tickets"), ct);
                if (b.TryGetProperty("problems", out _)) await SetLinks(db, "change_problems", "change_id", id, "problem_id", Ids(b, "problems"), ct);
                await audit.WriteAsync("servicedesk.change_updated", number, "", ct: ct);
                return Results.Ok(new { ok = true });
            }
            catch (FormatException e) { return ServiceDeskModule.Bad("bad_request", e.Message); }
        }));

        read(r.MapPost("/api/servicedesk/changes/{id:long}/submit", async (long id, ClaimsPrincipal u, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var w = await ServiceDeskModule.WhoAsync(u, db, ct);
            if (!w.Staff) return Forbidden("Only the support team can submit changes.");
            string status, type, number, plan, rollback;
            await using (var c = new NpgsqlCommand("select status, type, number, plan, rollback from servicedesk.changes where id = @i", db))
            {
                ServiceDeskModule.P(c, "i", id);
                await using var rd = await c.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) return NotFound();
                status = rd.GetString(0); type = rd.GetString(1); number = rd.GetString(2); plan = rd.GetString(3); rollback = rd.GetString(4);
            }
            if (status is not ("draft" or "rejected")) return ServiceDeskModule.Bad("bad_status", "Only a draft or rejected change can be submitted.");
            if (plan == "" || rollback == "") return ServiceDeskModule.Bad("plan_required", "Write the implementation plan and the rollback plan before submitting.");
            var next = type == "standard" ? "approved" : "review";
            await using (var d = new NpgsqlCommand("delete from servicedesk.change_votes where change_id = @i", db)) { ServiceDeskModule.P(d, "i", id); await d.ExecuteNonQueryAsync(ct); }
            await using (var c = new NpgsqlCommand("update servicedesk.changes set status = @s, updated_at = now() where id = @i", db)) { ServiceDeskModule.P(c, "s", next); ServiceDeskModule.P(c, "i", id); await c.ExecuteNonQueryAsync(ct); }
            await Note(db, "change", id, w.Name, next == "approved" ? "Submitted. Standard changes are pre-approved." : "Submitted for approval.", ct);
            await ServiceDeskModule.Event(db, id, "change.submitted", new { number, status = next }, ct);
            await audit.WriteAsync("servicedesk.change_submitted", number, next, ct: ct);
            return Results.Ok(new { status = next });
        }));

        read(r.MapPost("/api/servicedesk/changes/{id:long}/vote", async (long id, JsonElement b, ClaimsPrincipal u, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var w = await ServiceDeskModule.WhoAsync(u, db, ct);
            if (!w.Staff) return Forbidden("Only the support team can vote on changes.");
            string status, number, type; Guid? owner; int quorum;
            await using (var c = new NpgsqlCommand("select status, number, type, owner_id, quorum from servicedesk.changes where id = @i", db))
            {
                ServiceDeskModule.P(c, "i", id);
                await using var rd = await c.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) return NotFound();
                status = rd.GetString(0); number = rd.GetString(1); type = rd.GetString(2); owner = rd.IsDBNull(3) ? null : rd.GetGuid(3); quorum = rd.GetInt32(4);
            }
            if (status != "review") return ServiceDeskModule.Bad("bad_status", "This change is not waiting for approval.");
            if (owner == w.Id) return Forbidden("You cannot approve your own change.");
            if (type == "emergency" && !w.Admin) return Forbidden("Only an administrator can approve an emergency change.");
            var decision = b.TryGetProperty("decision", out var dv) && dv.ValueKind == JsonValueKind.String ? dv.GetString() : "";
            if (decision is not ("approve" or "reject")) return ServiceDeskModule.Bad("bad_decision", "decision must be approve or reject");
            var comment = b.TryGetProperty("comment", out var cv) && cv.ValueKind == JsonValueKind.String ? cv.GetString()!.Trim() : "";
            if (decision == "reject" && comment == "") return ServiceDeskModule.Bad("comment_required", "Say why you are rejecting the change.");
            if (comment.Length > 2000) return ServiceDeskModule.Bad("bad_request", "comment is too long");
            await using (var c = new NpgsqlCommand(@"insert into servicedesk.change_votes (change_id, user_id, user_name, decision, comment) values (@i, @u, @n, @d, @c)
                on conflict (change_id, user_id) do update set decision = excluded.decision, comment = excluded.comment, at = now(), user_name = excluded.user_name", db))
            { ServiceDeskModule.P(c, "i", id); ServiceDeskModule.P(c, "u", w.Id); ServiceDeskModule.P(c, "n", w.Name); ServiceDeskModule.P(c, "d", decision); ServiceDeskModule.P(c, "c", comment); await c.ExecuteNonQueryAsync(ct); }
            long approvals;
            await using (var c = new NpgsqlCommand("select count(*) from servicedesk.change_votes where change_id = @i and decision = 'approve'", db)) { ServiceDeskModule.P(c, "i", id); approvals = (long)(await c.ExecuteScalarAsync(ct))!; }
            var next = decision == "reject" ? "rejected" : approvals >= quorum ? "approved" : "review";
            if (next != "review")
            {
                await using var c = new NpgsqlCommand("update servicedesk.changes set status = @s, updated_at = now() where id = @i", db); ServiceDeskModule.P(c, "s", next); ServiceDeskModule.P(c, "i", id); await c.ExecuteNonQueryAsync(ct);
            }
            await Note(db, "change", id, w.Name, (decision == "approve" ? "Approved" : "Rejected: " + comment) + (next == "approved" ? ". The change board quorum is met." : ""), ct);
            if (next != "review") await ServiceDeskModule.Event(db, id, "change." + next, new { number }, ct);
            await audit.WriteAsync("servicedesk.change_vote", number, decision, ct: ct);
            return Results.Ok(new { status = next, approvals, quorum });
        }));

        read(r.MapPost("/api/servicedesk/changes/{id:long}/status", async (long id, JsonElement b, ClaimsPrincipal u, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var w = await ServiceDeskModule.WhoAsync(u, db, ct);
            if (!w.Staff) return Forbidden("Only the support team can move changes forward.");
            string status, number; DateTime? start, end;
            await using (var c = new NpgsqlCommand("select status, number, start_at, end_at from servicedesk.changes where id = @i", db))
            {
                ServiceDeskModule.P(c, "i", id);
                await using var rd = await c.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) return NotFound();
                status = rd.GetString(0); number = rd.GetString(1); start = rd.IsDBNull(2) ? null : rd.GetDateTime(2); end = rd.IsDBNull(3) ? null : rd.GetDateTime(3);
            }
            var to = b.TryGetProperty("status", out var sv) && sv.ValueKind == JsonValueKind.String ? sv.GetString()! : "";
            var allowed = status switch
            {
                "draft" => new[] { "cancelled" }, "review" => new[] { "cancelled" }, "rejected" => new[] { "cancelled" },
                "approved" => new[] { "scheduled", "implementing", "cancelled" }, "scheduled" => new[] { "implementing", "cancelled" },
                "implementing" => new[] { "completed", "failed" }, _ => Array.Empty<string>(),
            };
            if (!allowed.Contains(to)) return ServiceDeskModule.Bad("bad_status", $"A change that is {status} cannot become {(to == "" ? "that" : to)}.");
            if (to == "scheduled" && (start == null || end == null)) return ServiceDeskModule.Bad("window_required", "Set the start and end of the change window first.");
            string? outcome = null;
            if (to is "completed" or "failed")
            {
                outcome = b.TryGetProperty("outcome", out var ov) && ov.ValueKind == JsonValueKind.String ? ov.GetString()!.Trim() : "";
                if (outcome == "") return ServiceDeskModule.Bad("outcome_required", "Record the outcome: what happened and whether the rollback was used.");
                if (outcome.Length > 8000) return ServiceDeskModule.Bad("bad_request", "outcome is too long");
            }
            await using (var c = new NpgsqlCommand("update servicedesk.changes set status = @s, outcome = coalesce(@o, outcome), updated_at = now() where id = @i", db))
            { ServiceDeskModule.P(c, "s", to); ServiceDeskModule.P(c, "o", outcome); ServiceDeskModule.P(c, "i", id); await c.ExecuteNonQueryAsync(ct); }
            await Note(db, "change", id, w.Name, $"Status: {status} → {to}", ct);
            await ServiceDeskModule.Event(db, id, "change." + to, new { number }, ct);
            await audit.WriteAsync("servicedesk.change_status", number, status + " → " + to, ct: ct);
            return Results.Ok(new { status = to });
        }));

        read(r.MapPost("/api/servicedesk/changes/{id:long}/notes", (long id, JsonElement b, ClaimsPrincipal u, ITenantContext t, CancellationToken ct) => AddNote("change", id, b, u, t, ct)));

        // ================================================================ problems
        read(r.MapGet("/api/servicedesk/problems", async (HttpContext http, ClaimsPrincipal u, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var w = await ServiceDeskModule.WhoAsync(u, db, ct);
            if (w.Customer || w.Role == "") return Forbidden("Problems are for the support team.");
            var q = http.Request.Query;
            await using var c = new NpgsqlCommand { Connection = db };
            var where = new List<string> { "true" };
            if (q["status"].ToString() == "open") where.Add("p.status not in ('resolved','closed')");
            else if (q["status"].ToString() is { Length: > 0 } st) { ServiceDeskModule.P(c, "st", st.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); where.Add("p.status = any(@st)"); }
            if (q["q"].ToString().Trim() is { Length: > 0 } text) { ServiceDeskModule.P(c, "q", "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"); where.Add("(p.number ilike @q or p.title ilike @q or p.category ilike @q)"); }
            c.CommandText = $"select {PCols}, (select count(*) from servicedesk.problem_tickets l where l.problem_id = p.id) from servicedesk.problems p where {string.Join(" and ", where)} order by p.updated_at desc, p.id desc limit 200";
            var items = new List<object>();
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) items.Add(new { problem = Problem(rd), tickets = rd.GetInt64(15) });
            return Results.Ok(new { items });
        }));

        read(r.MapGet("/api/servicedesk/problems/suggestions", async (ClaimsPrincipal u, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var w = await ServiceDeskModule.WhoAsync(u, db, ct);
            if (!w.Staff) return Forbidden("Problems are for the support team.");
            // The same category raised three or more times in 30 days, with those tickets not yet tied to any problem.
            await using var c = new NpgsqlCommand(@"select t.category, count(*), array_agg(t.id order by t.id) from servicedesk.tickets t
                where t.category <> '' and t.created_at > now() - interval '30 days' and not exists (select 1 from servicedesk.problem_tickets l where l.ticket_id = t.id)
                group by t.category having count(*) >= 3 order by count(*) desc limit 20", db);
            var items = new List<object>();
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) items.Add(new { category = rd.GetString(0), count = rd.GetInt64(1), tickets = rd.GetFieldValue<long[]>(2) });
            return Results.Ok(new { items });
        }));

        read(r.MapGet("/api/servicedesk/problems/{id:long}", async (long id, ClaimsPrincipal u, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var w = await ServiceDeskModule.WhoAsync(u, db, ct);
            if (w.Customer || w.Role == "") return Forbidden("Problems are for the support team.");
            object? problem = null;
            await using (var c = new NpgsqlCommand($"select {PCols} from servicedesk.problems p where p.id = @i", db))
            {
                ServiceDeskModule.P(c, "i", id);
                await using var rd = await c.ExecuteReaderAsync(ct);
                if (await rd.ReadAsync(ct)) problem = Problem(rd);
            }
            if (problem == null) return NotFound();
            var changes = new List<object>();
            await using (var c = new NpgsqlCommand("select ch.id, ch.number, ch.title, ch.status from servicedesk.change_problems l join servicedesk.changes ch on ch.id = l.change_id where l.problem_id = @i order by ch.id", db))
            {
                ServiceDeskModule.P(c, "i", id);
                await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) changes.Add(new { id = rd.GetInt64(0), number = rd.GetString(1), title = rd.GetString(2), status = rd.GetString(3) });
            }
            return Results.Ok(new { problem, notes = await Notes(db, "problem", id, ct), tickets = await TicketLinks(db, "problem_tickets", "problem_id", id, ct), changes });
        }));

        read(r.MapPost("/api/servicedesk/problems", async (JsonElement b, ClaimsPrincipal u, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var w = await ServiceDeskModule.WhoAsync(u, db, ct);
            if (!w.Staff) return Forbidden("Only the support team can raise problems.");
            try
            {
                var title = Text(b, "title", 200, out _) ?? "";
                if (title == "") return ServiceDeskModule.Bad("title_required", "Give the problem a title.");
                var prio = Text(b, "priority", 20, out _) is { Length: > 0 } pr ? pr : "medium";
                if (!Prios.Contains(prio)) return ServiceDeskModule.Bad("bad_priority");
                await using var c = new NpgsqlCommand(@"insert into servicedesk.problems (title, description, priority, category, owner_id, owner_name, created_by) values (@t, @d, @p, @c, @oi, @on, @cb) returning id, number", db);
                ServiceDeskModule.P(c, "t", title); ServiceDeskModule.P(c, "d", Text(b, "description", 8000, out _) ?? ""); ServiceDeskModule.P(c, "p", prio); ServiceDeskModule.P(c, "c", Text(b, "category", 100, out _) ?? "");
                ServiceDeskModule.P(c, "oi", w.Id); ServiceDeskModule.P(c, "on", w.Name); ServiceDeskModule.P(c, "cb", w.Name);
                await using var rd = await c.ExecuteReaderAsync(ct);
                await rd.ReadAsync(ct);
                var id = rd.GetInt64(0); var number = rd.GetString(1);
                await rd.CloseAsync();
                var tickets = Ids(b, "tickets"); if (tickets.Count > 0) await SetLinks(db, "problem_tickets", "problem_id", id, "ticket_id", tickets, ct);
                await audit.WriteAsync("servicedesk.problem_created", number, title, ct: ct);
                return Results.Ok(new { id, number });
            }
            catch (FormatException e) { return ServiceDeskModule.Bad("bad_request", e.Message); }
        }));

        read(r.MapMethods("/api/servicedesk/problems/{id:long}", new[] { "PATCH" }, async (long id, JsonElement b, ClaimsPrincipal u, ITenantContext t, IUserDirectory users, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var w = await ServiceDeskModule.WhoAsync(u, db, ct);
            if (!w.Staff) return Forbidden("Only the support team can edit problems.");
            string status, number, workaround, resolution;
            await using (var c = new NpgsqlCommand("select status, number, workaround, resolution from servicedesk.problems where id = @i", db))
            {
                ServiceDeskModule.P(c, "i", id);
                await using var rd = await c.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) return NotFound();
                status = rd.GetString(0); number = rd.GetString(1); workaround = rd.GetString(2); resolution = rd.GetString(3);
            }
            try
            {
                var sets = new List<string>(); await using var u2 = new NpgsqlCommand { Connection = db }; ServiceDeskModule.P(u2, "i", id);
                void Set(string col, object? v) { sets.Add(col + " = @" + col); ServiceDeskModule.P(u2, col, v); }
                string? newStatus = null;
                foreach (var (name, col, max) in new[] { ("title", "title", 200), ("description", "description", 8000), ("category", "category", 100), ("rootCause", "root_cause", 8000), ("workaround", "workaround", 8000), ("resolution", "resolution", 8000) })
                {
                    var v = Text(b, name, max, out var has); if (!has) continue;
                    if (name == "title" && v == "") return ServiceDeskModule.Bad("title_required");
                    if (name == "workaround") workaround = v!; if (name == "resolution") resolution = v!;
                    Set(col, v);
                }
                if (Text(b, "priority", 20, out var hp) is { } pv && hp) { if (!Prios.Contains(pv)) return ServiceDeskModule.Bad("bad_priority"); Set("priority", pv); }
                if (Text(b, "status", 20, out var hs) is { } sv && hs)
                {
                    if (!PStatuses.Contains(sv)) return ServiceDeskModule.Bad("bad_status", "status must be " + string.Join(", ", PStatuses));
                    if (sv == "known_error" && workaround == "") return ServiceDeskModule.Bad("workaround_required", "A known error needs a workaround people can use meanwhile.");
                    if (sv is "resolved" or "closed" && resolution == "") return ServiceDeskModule.Bad("resolution_required", "Record how the problem was resolved.");
                    newStatus = sv; Set("status", sv);
                }
                if (b.TryGetProperty("ownerId", out var ow) && ow.ValueKind == JsonValueKind.String && Guid.TryParse(ow.GetString(), out var og)) { Set("owner_id", og); Set("owner_name", await OwnerName(users, og, ct)); }
                if (sets.Count == 0 && !b.TryGetProperty("tickets", out _) && !b.TryGetProperty("changes", out _)) return ServiceDeskModule.Bad("nothing_to_change");
                if (sets.Count > 0) { u2.CommandText = "update servicedesk.problems set " + string.Join(", ", sets) + ", updated_at = now() where id = @i"; await u2.ExecuteNonQueryAsync(ct); }
                if (b.TryGetProperty("tickets", out _)) await SetLinks(db, "problem_tickets", "problem_id", id, "ticket_id", Ids(b, "tickets"), ct);
                if (newStatus != null && newStatus != status)
                {
                    await Note(db, "problem", id, w.Name, $"Status: {status} → {newStatus}", ct);
                    await ServiceDeskModule.Event(db, id, "problem." + newStatus, new { number }, ct);
                }
                await audit.WriteAsync("servicedesk.problem_updated", number, newStatus ?? "", ct: ct);
                return Results.Ok(new { ok = true });
            }
            catch (FormatException e) { return ServiceDeskModule.Bad("bad_request", e.Message); }
        }));

        read(r.MapPost("/api/servicedesk/problems/{id:long}/notes", (long id, JsonElement b, ClaimsPrincipal u, ITenantContext t, CancellationToken ct) => AddNote("problem", id, b, u, t, ct)));

        // What a ticket is tied to, for the ticket screen (support team only).
        read(r.MapGet("/api/servicedesk/tickets/{id:long}/related", async (long id, ClaimsPrincipal u, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var w = await ServiceDeskModule.WhoAsync(u, db, ct);
            if (w.Customer || w.Role == "") return Results.Ok(new { problems = Array.Empty<object>(), changes = Array.Empty<object>() });
            var problems = new List<object>(); var changes = new List<object>();
            await using (var c = new NpgsqlCommand("select p.id, p.number, p.title, p.status from servicedesk.problem_tickets l join servicedesk.problems p on p.id = l.problem_id where l.ticket_id = @i order by p.id", db))
            {
                ServiceDeskModule.P(c, "i", id);
                await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) problems.Add(new { id = rd.GetInt64(0), number = rd.GetString(1), title = rd.GetString(2), status = rd.GetString(3) });
            }
            await using (var c = new NpgsqlCommand("select ch.id, ch.number, ch.title, ch.status from servicedesk.change_tickets l join servicedesk.changes ch on ch.id = l.change_id where l.ticket_id = @i order by ch.id", db))
            {
                ServiceDeskModule.P(c, "i", id);
                await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) changes.Add(new { id = rd.GetInt64(0), number = rd.GetString(1), title = rd.GetString(2), status = rd.GetString(3) });
            }
            return Results.Ok(new { problems, changes });
        }));
    }

    static async Task<string> OwnerName(IUserDirectory users, Guid id, CancellationToken ct) => (await users.GetAsync(id, ct))?.Name ?? "";

    static async Task<IResult> AddNote(string kind, long id, JsonElement b, ClaimsPrincipal u, ITenantContext t, CancellationToken ct)
    {
        await using var db = await ServiceDeskModule.Open(t, ct);
        var w = await ServiceDeskModule.WhoAsync(u, db, ct);
        if (!w.Staff) return Forbidden("Only the support team can add notes.");
        await using (var c = new NpgsqlCommand($"select 1 from servicedesk.{(kind == "change" ? "changes" : "problems")} where id = @i", db)) { ServiceDeskModule.P(c, "i", id); if (await c.ExecuteScalarAsync(ct) == null) return NotFound(); }
        var body = b.TryGetProperty("body", out var bv) && bv.ValueKind == JsonValueKind.String ? bv.GetString()!.Trim() : "";
        if (body == "") return ServiceDeskModule.Bad("body_required", "Write a note first.");
        if (body.Length > 8000) return ServiceDeskModule.Bad("bad_request", "The note is too long.");
        await Note(db, kind, id, w.Name, body, ct);
        await using var up = new NpgsqlCommand($"update servicedesk.{(kind == "change" ? "changes" : "problems")} set updated_at = now() where id = @i", db); ServiceDeskModule.P(up, "i", id); await up.ExecuteNonQueryAsync(ct);
        return Results.Ok(new { ok = true });
    }
}
