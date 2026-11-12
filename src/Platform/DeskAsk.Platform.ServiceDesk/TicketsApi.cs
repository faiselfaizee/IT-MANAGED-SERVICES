using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using static DeskAsk.Platform.ServiceDesk.ServiceDeskModule;

namespace DeskAsk.Platform.ServiceDesk;

/// <summary>Tickets: list, open, change, status, comments, attachments. See the module summary for who may do what.</summary>
public static class TicketsApi
{
    static readonly string[] Statuses = { "open", "progress", "hold", "resolved", "closed" };
    static readonly string[] Prios = SlaConfig.Prios;
    const int MaxAttachment = 10 * 1024 * 1024;

    const string Cols = @"t.id, t.number, t.subject, t.description, t.status, t.priority, t.category, t.company_id, t.company_name, t.requester_id, t.requester_name, t.requester_email,
        t.assignee_id, t.assignee_name, t.asset_id, t.asset_name, t.source, t.created_at, t.updated_at, t.first_response_at, t.resolved_at, t.closed_at, t.respond_due, t.resolve_due, t.paused_at, t.paused_s, t.created_by";

    static DateTime? Dt(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDateTime(i);

    static object Ticket(NpgsqlDataReader r, SlaConfig sla, bool full)
    {
        var status = r.GetString(4); var created = r.GetDateTime(17); var due = Dt(r, 23); var paused = Dt(r, 24); var resolved = Dt(r, 20); var ps = r.GetInt64(25);
        var state = sla.State(status, created, due, paused, resolved, ps, DateTime.UtcNow);
        var respondState = Dt(r, 19) != null ? "met" : Dt(r, 22) is DateTime rd && DateTime.UtcNow > rd && paused == null && status is not ("resolved" or "closed") ? "breached" : "ok";
        return new
        {
            id = r.GetInt64(0), number = r.GetString(1), subject = r.GetString(2), description = full ? r.GetString(3) : null, status, priority = r.GetString(5), category = r.GetString(6),
            companyId = r.IsDBNull(7) ? (int?)null : r.GetInt32(7), company = r.GetString(8),
            requesterId = r.IsDBNull(9) ? (Guid?)null : r.GetGuid(9), requester = r.GetString(10), requesterEmail = r.GetString(11),
            assigneeId = r.IsDBNull(12) ? (Guid?)null : r.GetGuid(12), assignee = r.GetString(13),
            assetId = r.IsDBNull(14) ? (long?)null : r.GetInt64(14), asset = r.GetString(15), source = r.GetString(16),
            createdAt = created, updatedAt = r.GetDateTime(18), firstResponseAt = Dt(r, 19), resolvedAt = resolved, closedAt = Dt(r, 21),
            respondDue = Dt(r, 22), resolveDue = due, paused = paused != null, sla = state, respond = respondState,
        };
    }

    /// <summary>Customers only ever see their own company's tickets.</summary>
    static string Scope(Who w, NpgsqlCommand c)
    {
        if (w.Staff || w.Role == "viewer") return "true";
        if (w.Customer && w.CompanyId is int id) { P(c, "scope_company", id); return "t.company_id = @scope_company"; }
        return "false";
    }

    static async Task<(object? ticket, string number, string status, Who who)> Load(long id, ClaimsPrincipal u, NpgsqlConnection db, ISettings s, bool full, CancellationToken ct)
    {
        var w = await WhoAsync(u, db, ct);
        await using var c = new NpgsqlCommand($"select {Cols} from servicedesk.tickets t where t.id = @i and ({{0}})", db); P(c, "i", id);
        c.CommandText = c.CommandText.Replace("{0}", Scope(w, c));
        await using var r = await c.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return (null, "", "", w);
        return (Ticket(r, await SlaConfig.LoadAsync(s, ct), full), r.GetString(1), r.GetString(4), w);
    }

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

    static async Task Note(NpgsqlConnection db, long ticket, Who? by, string kind, string body, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("insert into servicedesk.comments (ticket_id, author_id, author_name, kind, body) values (@t, @a, @n, @k, @b)", db);
        P(c, "t", ticket); P(c, "a", by == null || by.Id == Guid.Empty ? null : by.Id); P(c, "n", by?.Name ?? "System"); P(c, "k", kind); P(c, "b", body);
        await c.ExecuteNonQueryAsync(ct);
    }

    static async Task Touch(NpgsqlConnection db, long ticket, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("update servicedesk.tickets set updated_at = now() where id = @i", db); P(c, "i", ticket); await c.ExecuteNonQueryAsync(ct);
    }

    static IResult Forbidden(string why) => Results.Json(new { error = "forbidden", detail = why }, statusCode: 403);

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        var anyone = (RouteHandlerBuilder b) => b.RequireAuthorization().RequireModule(key);

        // ---------------------------------------------------------------- list
        anyone(r.MapGet("/api/servicedesk/tickets", async (HttpContext http, ClaimsPrincipal u, ITenantContext t, ISettings s, CancellationToken ct) =>
        {
            var q = http.Request.Query;
            await using var db = await Open(t, ct);
            var w = await WhoAsync(u, db, ct);
            var sla = await SlaConfig.LoadAsync(s, ct);
            await using var c = new NpgsqlCommand { Connection = db };
            var where = new List<string> { Scope(w, c) };
            var st = q["status"].ToString();
            if (st == "active") where.Add("t.status not in ('resolved','closed')");
            else if (st != "")
            {
                var l = st.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(Statuses.Contains).ToArray();
                if (l.Length == 0) return Bad("bad_status", "status must be active or a list of " + string.Join(", ", Statuses));
                P(c, "st", l); where.Add("t.status = any(@st)");
            }
            if (q["priority"].ToString() is { Length: > 0 } pr) { if (!Prios.Contains(pr)) return Bad("bad_priority"); P(c, "pr", pr); where.Add("t.priority = @pr"); }
            if (q["category"].ToString() is { Length: > 0 } cat) { P(c, "cat", cat); where.Add("t.category = @cat"); }
            if (q["company"].ToString() is { Length: > 0 } co && int.TryParse(co, out var coId)) { P(c, "co", coId); where.Add("t.company_id = @co"); }
            var a = q["assignee"].ToString();
            if (a == "me") { P(c, "me", w.Id); where.Add("t.assignee_id = @me"); }
            else if (a == "none") where.Add("t.assignee_id is null");
            else if (Guid.TryParse(a, out var ag)) { P(c, "ag", ag); where.Add("t.assignee_id = @ag"); }
            if (long.TryParse(q["assetId"], out var assetFilter)) { P(c, "asf", assetFilter); where.Add("t.asset_id = @asf"); }
            if (q["mine"] == "1") { P(c, "mine", w.Id); where.Add("t.requester_id = @mine"); }
            if (q["overdue"] == "1") where.Add("t.status not in ('resolved','closed') and t.paused_at is null and t.resolve_due < now()");
            if (q["q"].ToString().Trim() is { Length: > 0 } text)
            {
                P(c, "q", "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
                where.Add("(t.number ilike @q or t.subject ilike @q or t.requester_name ilike @q or t.company_name ilike @q or t.asset_name ilike @q)");
            }
            var take = Math.Clamp(int.TryParse(q["take"], out var tk) ? tk : 50, 1, 200); var skip = Math.Max(0, int.TryParse(q["skip"], out var sk) ? sk : 0);
            var cond = string.Join(" and ", where);
            c.CommandText = $"select count(*) from servicedesk.tickets t where {cond}";
            var total = (long)(await c.ExecuteScalarAsync(ct))!;
            c.CommandText = $"select {Cols} from servicedesk.tickets t where {cond} order by t.updated_at desc, t.id desc limit {take} offset {skip}";
            var items = new List<object>();
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) items.Add(Ticket(rd, sla, false));
            return Results.Ok(new { total, items });
        }));

        anyone(r.MapGet("/api/servicedesk/summary", async (ClaimsPrincipal u, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            var w = await WhoAsync(u, db, ct);
            await using var c = new NpgsqlCommand { Connection = db };
            var scope = Scope(w, c); P(c, "me", w.Id);
            c.CommandText = $@"select count(*) filter (where status = 'open'), count(*) filter (where status = 'progress'), count(*) filter (where status = 'hold'),
                count(*) filter (where status = 'resolved'), count(*) filter (where status = 'closed'),
                count(*) filter (where status not in ('resolved','closed') and assignee_id is null),
                count(*) filter (where status not in ('resolved','closed') and assignee_id = @me),
                count(*) filter (where status not in ('resolved','closed') and paused_at is null and resolve_due < now()),
                count(*) filter (where status not in ('resolved','closed') and priority = 'critical')
                from servicedesk.tickets t where {scope}";
            await using var rd = await c.ExecuteReaderAsync(ct); await rd.ReadAsync(ct);
            return Results.Ok(new { open = rd.GetInt64(0), progress = rd.GetInt64(1), hold = rd.GetInt64(2), resolved = rd.GetInt64(3), closed = rd.GetInt64(4), unassigned = rd.GetInt64(5), mine = rd.GetInt64(6), overdue = rd.GetInt64(7), critical = rd.GetInt64(8) });
        }));

        // ---------------------------------------------------------------- one ticket
        anyone(r.MapGet("/api/servicedesk/tickets/{id:long}", async (long id, ClaimsPrincipal u, ITenantContext t, ISettings s, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            var (ticket, _, _, w) = await Load(id, u, db, s, true, ct);
            if (ticket == null) return Results.NotFound();
            var comments = new List<object>();
            await using (var c = new NpgsqlCommand("select id, at, author_id, author_name, kind, body from servicedesk.comments where ticket_id = @i" + (w.Customer ? " and kind <> 'internal'" : "") + " order by id", db))
            {
                P(c, "i", id);
                await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) comments.Add(new { id = rd.GetInt64(0), at = rd.GetDateTime(1), authorId = rd.IsDBNull(2) ? (Guid?)null : rd.GetGuid(2), author = rd.GetString(3), kind = rd.GetString(4), body = rd.GetString(5) });
            }
            var files = new List<object>();
            await using (var c = new NpgsqlCommand("select id, name, content_type, size, at, by_name, internal from servicedesk.attachments where ticket_id = @i" + (w.Customer ? " and not internal" : "") + " order by id", db))
            {
                P(c, "i", id);
                await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) files.Add(new { id = rd.GetInt64(0), name = rd.GetString(1), contentType = rd.GetString(2), size = rd.GetInt32(3), at = rd.GetDateTime(4), by = rd.GetString(5), @internal = rd.GetBoolean(6) });
            }
            var canModify = w.Staff || (w.Customer && w.MayModify);
            return Results.Ok(new { ticket, comments, attachments = files, can = new { edit = w.Staff, comment = canModify, internalNote = w.Staff, status = w.Staff || canModify } });
        }));

        // ---------------------------------------------------------------- open a ticket
        anyone(r.MapPost("/api/servicedesk/tickets", async (JsonElement b, ClaimsPrincipal u, ITenantContext t, ISettings s, ICompanyDirectory companies, IAssetDirectory assets, IUserDirectory users, IMailQueue mailq, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            var w = await WhoAsync(u, db, ct);
            if (w.Role == "viewer" || (!w.Staff && !w.Customer)) return Forbidden("Your role cannot open tickets.");
            if (w.Customer && (w.CompanyId == null || !w.MayRaise)) return Forbidden(w.CompanyId == null ? "Your account is not linked to a company yet. Ask your administrator." : "You may not open tickets.");
            string subject, description, category, priority; int? company; long? assetId = null; string assetName = ""; Guid? requesterId = w.Id == Guid.Empty ? null : w.Id; string rName = w.Name, rEmail = w.Email;
            try
            {
                if (b.ValueKind != JsonValueKind.Object) throw new FormatException("the body must be a JSON object");
                subject = Text(b, "subject", 200, out _) ?? ""; if (subject == "") throw new FormatException("subject is needed");
                description = Text(b, "description", 20000, out _) ?? "";
                category = Text(b, "category", 60, out _) ?? "";
                priority = Text(b, "priority", 20, out var hasP) ?? "medium"; if (!hasP || priority == "") priority = "medium";
                if (!Prios.Contains(priority)) throw new FormatException("priority must be one of " + string.Join(", ", Prios));
                company = w.Customer ? w.CompanyId : null;
                if (w.Staff)
                {
                    if (b.TryGetProperty("companyId", out var cv) && cv.ValueKind != JsonValueKind.Null)
                    { if (cv.ValueKind != JsonValueKind.Number || !cv.TryGetInt32(out var cid)) throw new FormatException("companyId must be a number"); company = cid; }
                    var name = Text(b, "requesterName", 200, out var hasN); var mail = Text(b, "requesterEmail", 320, out var hasM);
                    if (b.TryGetProperty("requesterId", out var rv) && rv.ValueKind == JsonValueKind.String && Guid.TryParse(rv.GetString(), out var rid))
                    {
                        var ru = await users.GetAsync(rid, ct) ?? throw new FormatException("requesterId does not exist");
                        requesterId = ru.Id; rName = ru.Name; rEmail = ru.Email;
                    }
                    else if (hasN || hasM) { requesterId = null; rName = name ?? ""; rEmail = mail ?? ""; }
                    if (b.TryGetProperty("assetId", out var av) && av.ValueKind != JsonValueKind.Null)
                    {
                        if (av.ValueKind != JsonValueKind.Number || !av.TryGetInt64(out var aid)) throw new FormatException("assetId must be a number");
                        var asset = await assets.GetAsync(aid, ct) ?? throw new FormatException("assetId " + aid + " does not exist");
                        assetId = asset.Id; assetName = asset.Tag + " " + asset.Name; company ??= asset.CompanyId;
                    }
                }
            }
            catch (FormatException e) { return Bad("bad_request", e.Message); }
            if (category != "" && !(await SlaConfig.CategoriesAsync(s, ct)).Contains(category, StringComparer.OrdinalIgnoreCase)) return Bad("bad_category", "Choose one of the listed categories.");
            string companyName = "";
            if (company is int ci) { var co = await companies.GetAsync(ci, ct); if (co == null) return Bad("bad_company", "That company does not exist."); companyName = co.Name; }

            var sla = await SlaConfig.LoadAsync(s, ct);
            await using var c = new NpgsqlCommand(@"insert into servicedesk.tickets
                (subject, description, priority, category, company_id, company_name, requester_id, requester_name, requester_email, asset_id, asset_name, source, created_by, respond_due, resolve_due)
                values (@s, @d, @p, @cat, @co, @con, @ri, @rn, @re, @ai, @an, 'web', @by, now() + make_interval(secs => @rp * 3600), now() + make_interval(secs => @rs * 3600))
                returning id, number", db);
            P(c, "s", subject); P(c, "d", description); P(c, "p", priority); P(c, "cat", category); P(c, "co", company); P(c, "con", companyName);
            P(c, "ri", requesterId); P(c, "rn", rName); P(c, "re", rEmail); P(c, "ai", assetId); P(c, "an", assetName); P(c, "by", w.Email);
            P(c, "rp", sla.Respond[priority]); P(c, "rs", sla.Resolve[priority]);
            long id; string number;
            await using (var rd = await c.ExecuteReaderAsync(ct)) { await rd.ReadAsync(ct); id = rd.GetInt64(0); number = rd.GetString(1); }
            await Event(db, id, "ticket.created", new { number, priority, company }, ct);
            await audit.WriteAsync("servicedesk.ticket_created", number, subject, ct: ct);
            await Tell(mailq, users, db, id, "created", description, w, ct);
            return Results.Created("/api/servicedesk/tickets/" + id, new { id, number });
        }));

        // ---------------------------------------------------------------- change fields (staff)
        anyone(r.MapMethods("/api/servicedesk/tickets/{id:long}", new[] { "PATCH" }, async (long id, JsonElement b, ClaimsPrincipal u, ITenantContext t, ISettings s, ICompanyDirectory companies, IAssetDirectory assets, IUserDirectory users, IMailQueue mailq, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            var (found, number, status, w) = await Load(id, u, db, s, true, ct);
            if (found == null) return Results.NotFound();
            if (!w.Staff) return Forbidden("Only staff can change a ticket's details.");
            var sets = new List<string>(); var notes = new List<string>(); await using var c = new NpgsqlCommand { Connection = db }; P(c, "id", id);
            var priorityChanged = false; Guid? assignedTo = null;
            try
            {
                if (b.ValueKind != JsonValueKind.Object) throw new FormatException("the body must be a JSON object");
                var subject = Text(b, "subject", 200, out var hasS); if (hasS) { if (subject == "") throw new FormatException("subject cannot be empty"); sets.Add("subject = @subject"); P(c, "subject", subject); notes.Add("subject changed"); }
                var desc = Text(b, "description", 20000, out var hasD); if (hasD) { sets.Add("description = @description"); P(c, "description", desc); notes.Add("description changed"); }
                var cat = Text(b, "category", 60, out var hasC);
                if (hasC)
                {
                    if (cat != "" && !(await SlaConfig.CategoriesAsync(s, ct)).Contains(cat, StringComparer.OrdinalIgnoreCase)) throw new FormatException("category: choose one of the listed categories");
                    sets.Add("category = @category"); P(c, "category", cat); notes.Add("category: " + (cat == "" ? "none" : cat));
                }
                var pr = Text(b, "priority", 20, out var hasP);
                if (hasP) { if (!Prios.Contains(pr)) throw new FormatException("priority must be one of " + string.Join(", ", Prios)); sets.Add("priority = @priority"); P(c, "priority", pr); notes.Add("priority: " + pr); priorityChanged = true; }
                if (b.TryGetProperty("assigneeId", out var av))
                {
                    if (av.ValueKind == JsonValueKind.Null) { sets.Add("assignee_id = null, assignee_name = ''"); notes.Add("unassigned"); }
                    else if (av.ValueKind == JsonValueKind.String && Guid.TryParse(av.GetString(), out var ag))
                    {
                        var au = await users.GetAsync(ag, ct);
                        if (au == null || !au.Active || au.Role is not ("admin" or "engineer")) throw new FormatException("assigneeId must be an active engineer or administrator");
                        assignedTo = ag; sets.Add("assignee_id = @assignee, assignee_name = @assigneeName"); P(c, "assignee", ag); P(c, "assigneeName", au.Name); notes.Add("assigned to " + au.Name);
                    }
                    else throw new FormatException("assigneeId must be an id or null");
                }
                if (b.TryGetProperty("companyId", out var cv))
                {
                    if (cv.ValueKind == JsonValueKind.Null) { sets.Add("company_id = null, company_name = ''"); notes.Add("company removed"); }
                    else if (cv.ValueKind == JsonValueKind.Number && cv.TryGetInt32(out var cid))
                    {
                        var co = await companies.GetAsync(cid, ct) ?? throw new FormatException("companyId " + cid + " does not exist");
                        sets.Add("company_id = @company, company_name = @companyName"); P(c, "company", cid); P(c, "companyName", co.Name); notes.Add("company: " + co.Name);
                    }
                    else throw new FormatException("companyId must be a number or null");
                }
                if (b.TryGetProperty("assetId", out var sv))
                {
                    if (sv.ValueKind == JsonValueKind.Null) { sets.Add("asset_id = null, asset_name = ''"); notes.Add("asset removed"); }
                    else if (sv.ValueKind == JsonValueKind.Number && sv.TryGetInt64(out var aid))
                    {
                        var asset = await assets.GetAsync(aid, ct) ?? throw new FormatException("assetId " + aid + " does not exist");
                        sets.Add("asset_id = @asset, asset_name = @assetName"); P(c, "asset", aid); P(c, "assetName", asset.Tag + " " + asset.Name); notes.Add("asset: " + asset.Tag);
                    }
                    else throw new FormatException("assetId must be a number or null");
                }
            }
            catch (FormatException e) { return Bad("bad_request", e.Message); }
            if (sets.Count == 0) return Results.Ok(new { ok = true, changed = false });
            sets.Add("updated_at = now()");
            c.CommandText = "update servicedesk.tickets set " + string.Join(", ", sets) + " where id = @id";
            await c.ExecuteNonQueryAsync(ct);
            if (priorityChanged)
            {
                var sla = await SlaConfig.LoadAsync(s, ct);
                await using var d = new NpgsqlCommand($"update servicedesk.tickets set respond_due = {SlaConfig.RespondDue}, resolve_due = {SlaConfig.ResolveDue} where id = @id", db);
                sla.Bind(d); P(d, "id", id); await d.ExecuteNonQueryAsync(ct);
            }
            await Note(db, id, w, "system", string.Join("; ", notes) + ".", ct);
            await Event(db, id, "ticket.updated", new { changes = notes }, ct);
            await audit.WriteAsync("servicedesk.ticket_updated", number, string.Join("; ", notes), ct: ct);
            if (assignedTo != null) await Tell(mailq, users, db, id, "assigned", "", w, ct);
            return Results.Ok(new { ok = true, changed = true });
        }));

        // ---------------------------------------------------------------- status
        anyone(r.MapPost("/api/servicedesk/tickets/{id:long}/status", async (long id, JsonElement b, ClaimsPrincipal u, ITenantContext t, ISettings s, IUserDirectory users, IMailQueue mailq, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            var (found, number, status, w) = await Load(id, u, db, s, false, ct);
            if (found == null) return Results.NotFound();
            var canModify = w.Staff || (w.Customer && w.MayModify);
            if (!canModify) return Forbidden("You may not change this ticket.");
            string to, note; bool internalNote = false;
            try
            {
                if (b.ValueKind != JsonValueKind.Object) throw new FormatException("the body must be a JSON object");
                to = Text(b, "status", 20, out _) ?? ""; if (!Statuses.Contains(to)) throw new FormatException("status must be one of " + string.Join(", ", Statuses));
                note = Text(b, "note", 20000, out _) ?? "";
                internalNote = w.Staff && b.TryGetProperty("internal", out var iv) && iv.ValueKind == JsonValueKind.True;
            }
            catch (FormatException e) { return Bad("bad_request", e.Message); }
            var prev = status;
            if (prev == to) return Results.Ok(new { ok = true, changed = false });
            // customers may close their ticket, or reopen one that was resolved or closed; nothing else
            if (w.Customer && !(to == "closed" || (to == "open" && prev is "resolved" or "closed"))) return Forbidden("You can close a ticket or reopen a resolved one.");
            var sla = await SlaConfig.LoadAsync(s, ct);
            var sets = new List<string> { "status = @to", "updated_at = now()" };
            var leavingHold = prev == "hold" && to != "hold";
            if (to == "hold" && sla.Pause) sets.Add("paused_at = coalesce(paused_at, now())");
            if (leavingHold || (to is "resolved" or "closed")) sets.Add("paused_s = paused_s + coalesce(extract(epoch from now() - paused_at)::bigint, 0), paused_at = null");
            if (to == "resolved") sets.Add("resolved_at = now()");
            if (to == "closed") sets.Add("closed_at = now(), resolved_at = coalesce(resolved_at, now())");
            if (prev is "resolved" or "closed" && to is not ("resolved" or "closed")) sets.Add("resolved_at = null, closed_at = null");
            if (prev == "closed" && to == "resolved") sets.Add("closed_at = null");
            await using (var c = new NpgsqlCommand("update servicedesk.tickets set " + string.Join(", ", sets) + " where id = @id", db)) { P(c, "id", id); P(c, "to", to); await c.ExecuteNonQueryAsync(ct); }
            // the clock moves by the time spent waiting; work the due dates out again from what is stored
            await using (var d = new NpgsqlCommand($"update servicedesk.tickets set respond_due = {SlaConfig.RespondDue}, resolve_due = {SlaConfig.ResolveDue} where id = @id", db)) { sla.Bind(d); P(d, "id", id); await d.ExecuteNonQueryAsync(ct); }
            await Note(db, id, w, "system", $"Status: {prev} → {to}.", ct);
            if (note != "") await Note(db, id, w, internalNote ? "internal" : "public", note, ct);
            await Event(db, id, "ticket.status", new { @from = prev, to }, ct);
            await audit.WriteAsync("servicedesk.ticket_status", number, prev + " → " + to, ct: ct);
            if (w.Staff && to == "resolved") await Tell(mailq, users, db, id, "resolved", note, w, ct);
            else if (w.Staff && note != "" && !internalNote) await Tell(mailq, users, db, id, "reply", note, w, ct);
            return Results.Ok(new { ok = true, changed = true });
        }));

        // ---------------------------------------------------------------- comments
        anyone(r.MapPost("/api/servicedesk/tickets/{id:long}/comments", async (long id, JsonElement b, ClaimsPrincipal u, ITenantContext t, ISettings s, IUserDirectory users, IMailQueue mailq, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            var (found, number, status, w) = await Load(id, u, db, s, false, ct);
            if (found == null) return Results.NotFound();
            if (!(w.Staff || (w.Customer && w.MayModify))) return Forbidden("You may not reply to this ticket.");
            string body; bool isInternal;
            try
            {
                if (b.ValueKind != JsonValueKind.Object) throw new FormatException("the body must be a JSON object");
                body = Text(b, "body", 20000, out _) ?? ""; if (body == "") throw new FormatException("body is needed");
                isInternal = b.TryGetProperty("internal", out var iv) && iv.ValueKind == JsonValueKind.True;
            }
            catch (FormatException e) { return Bad("bad_request", e.Message); }
            if (isInternal && !w.Staff) return Forbidden("Only staff can write internal notes.");
            if (status == "closed" && w.Customer) return Results.Conflict(new { error = "ticket_closed", detail = "This ticket is closed. Reopen it to reply." });
            await Note(db, id, w, isInternal ? "internal" : "public", body, ct);
            if (w.Staff && !isInternal)
            {
                await using var c = new NpgsqlCommand("update servicedesk.tickets set first_response_at = coalesce(first_response_at, now()) where id = @i", db); P(c, "i", id); await c.ExecuteNonQueryAsync(ct);
            }
            if (w.Customer && status is "hold" or "resolved")
            {   // the customer answered: work continues, and the clock runs again
                var sla = await SlaConfig.LoadAsync(s, ct);
                await using (var c = new NpgsqlCommand("update servicedesk.tickets set status = 'open', paused_s = paused_s + coalesce(extract(epoch from now() - paused_at)::bigint, 0), paused_at = null, resolved_at = null where id = @i", db)) { P(c, "i", id); await c.ExecuteNonQueryAsync(ct); }
                await using (var d = new NpgsqlCommand($"update servicedesk.tickets set respond_due = {SlaConfig.RespondDue}, resolve_due = {SlaConfig.ResolveDue} where id = @i", db)) { sla.Bind(d); P(d, "i", id); await d.ExecuteNonQueryAsync(ct); }
                await Note(db, id, null, "system", $"Status: {status} → open (the customer replied).", ct);
            }
            await Touch(db, id, ct);
            await Event(db, id, isInternal ? "ticket.note" : "ticket.comment", new { by = w.Name, customer = w.Customer }, ct);
            await audit.WriteAsync("servicedesk.ticket_comment", number, isInternal ? "internal" : "public", ct: ct);
            if (!isInternal) await Tell(mailq, users, db, id, w.Staff ? "reply" : "customer_reply", body, w, ct);
            return Results.Ok(new { ok = true });
        }));

        // ---------------------------------------------------------------- attachments
        anyone(r.MapPost("/api/servicedesk/tickets/{id:long}/attachments", async (long id, HttpRequest req, ClaimsPrincipal u, ITenantContext t, ISettings s, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            var (found, number, status, w) = await Load(id, u, db, s, false, ct);
            if (found == null) return Results.NotFound();
            if (!(w.Staff || (w.Customer && w.MayModify))) return Forbidden("You may not add files to this ticket.");
            var name = Path.GetFileName((req.Query["name"].ToString() ?? "").Trim());
            if (name == "" || name.Length > 200) return Bad("bad_name", "Give the file name in ?name= (up to 200 characters).");
            var internalFile = req.Query["internal"] == "1";
            if (internalFile && !w.Staff) return Forbidden("Only staff can add internal files.");
            if (req.ContentLength > MaxAttachment) return Results.Json(new { error = "too_large", detail = "Files can be up to 10 MB." }, statusCode: 413);
            using var ms = new MemoryStream(); var buf = new byte[81920]; int n;
            while ((n = await req.Body.ReadAsync(buf, ct)) > 0)
            {
                ms.Write(buf, 0, n);
                if (ms.Length > MaxAttachment) return Results.Json(new { error = "too_large", detail = "Files can be up to 10 MB." }, statusCode: 413);
            }
            if (ms.Length == 0) return Bad("empty_file", "The file is empty.");
            var type = string.IsNullOrWhiteSpace(req.ContentType) ? "application/octet-stream" : req.ContentType!;
            await using var c = new NpgsqlCommand("insert into servicedesk.attachments (ticket_id, name, content_type, size, data, by_name, internal) values (@t, @n, @c, @s, @d, @b, @i) returning id", db);
            P(c, "t", id); P(c, "n", name); P(c, "c", type.Length > 200 ? "application/octet-stream" : type); P(c, "s", (int)ms.Length); P(c, "d", ms.ToArray()); P(c, "b", w.Name); P(c, "i", internalFile);
            var aid = (long)(await c.ExecuteScalarAsync(ct))!;
            await Note(db, id, w, "system", "Added file " + name + ".", ct);
            await Touch(db, id, ct);
            await audit.WriteAsync("servicedesk.ticket_file", number, name, ct: ct);
            return Results.Created("/api/servicedesk/tickets/" + id + "/attachments/" + aid, new { id = aid, name, size = (int)ms.Length });
        }));

        anyone(r.MapGet("/api/servicedesk/tickets/{id:long}/attachments/{aid:long}", async (long id, long aid, ClaimsPrincipal u, ITenantContext t, ISettings s, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            var (found, number, status, w) = await Load(id, u, db, s, false, ct);
            if (found == null) return Results.NotFound();
            await using var c = new NpgsqlCommand("select name, content_type, data, internal from servicedesk.attachments where id = @a and ticket_id = @t", db); P(c, "a", aid); P(c, "t", id);
            await using var rd = await c.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct) || (rd.GetBoolean(3) && w.Customer)) return Results.NotFound();
            // never let the browser run an uploaded file as a page
            return Results.File((byte[])rd["data"], "application/octet-stream", rd.GetString(0));
        }));
    }
}
