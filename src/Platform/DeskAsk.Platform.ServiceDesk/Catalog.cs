using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace DeskAsk.Platform.ServiceDesk;

/// <summary>
/// The service catalogue (ITIL request fulfilment): what people can ask for — a new laptop, access to a shared folder, a software
/// install — each with its own form, who it goes to, how fast it is done, and whether someone must approve it first. A request becomes
/// an SRV ticket; while it waits for approval it is on hold (the clock stops) and the approvers are mailed.
/// </summary>
internal static class CatalogApi
{
    static readonly Regex Key = new("^[a-z][a-z0-9_]{0,39}$", RegexOptions.Compiled);
    static readonly string[] Kinds = { "text", "longtext", "number", "date", "choice", "checkbox", "email" };
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    internal sealed record FieldDef(string Key, string Label, string Kind, string[]? Options, bool Required, string? Help);

    static IResult Forbidden(string d) => Results.Json(new { error = "forbidden", detail = d }, statusCode: 403);
    static void P(NpgsqlCommand c, string n, object? v) => ServiceDeskModule.P(c, n, v);
    static string S(JsonElement b, string n) => b.ValueKind == JsonValueKind.Object && b.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";

    const string Cols = "id, name, category, description, icon, fields::text, approvers, assignee_id, assignee_name, priority, resolve_hours, staff_only, active, position";
    static object Row(NpgsqlDataReader r, bool staff) => new
    {
        id = r.GetInt32(0), name = r.GetString(1), category = r.GetString(2), description = r.GetString(3), icon = r.GetString(4),
        fields = JsonDocument.Parse(r.GetString(5)).RootElement.Clone(), needsApproval = r.GetFieldValue<Guid[]>(6).Length > 0,
        approvers = staff ? r.GetFieldValue<Guid[]>(6) : Array.Empty<Guid>(), assigneeId = staff && !r.IsDBNull(7) ? r.GetGuid(7) : (Guid?)null, assigneeName = staff ? r.GetString(8) : "",
        priority = r.GetString(9), resolveHours = r.IsDBNull(10) ? (int?)null : r.GetInt32(10), staffOnly = r.GetBoolean(11), active = r.GetBoolean(12), position = r.GetInt32(13)
    };

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        RouteHandlerBuilder Any(RouteHandlerBuilder b) => b.RequireAuthorization().RequireModule(key);

        Any(r.MapGet("/api/servicedesk/catalog", async (bool? all, ClaimsPrincipal u, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var w = await ServiceDeskModule.WhoAsync(u, db, ct);
            var everything = all == true && w.Admin;
            await using var c = new NpgsqlCommand($"select {Cols} from servicedesk.catalog where (@all or (active and (not staff_only or @staff))) order by category, position, name", db);
            P(c, "all", everything); P(c, "staff", w.Staff);
            var list = new List<object>();
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) list.Add(Row(rd, w.Staff));
            return Results.Ok(list);
        }));

        Any(r.MapPost("/api/servicedesk/catalog", (JsonElement b, ClaimsPrincipal u, ITenantContext t, IUserDirectory users, IAuditLog audit, CancellationToken ct) => Save(null, b, u, t, users, audit, ct)));
        Any(r.MapPut("/api/servicedesk/catalog/{id:int}", (int id, JsonElement b, ClaimsPrincipal u, ITenantContext t, IUserDirectory users, IAuditLog audit, CancellationToken ct) => Save(id, b, u, t, users, audit, ct)));
        Any(r.MapDelete("/api/servicedesk/catalog/{id:int}", async (int id, ClaimsPrincipal u, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            if (!(await ServiceDeskModule.WhoAsync(u, db, ct)).Admin) return Forbidden("Only an administrator manages the catalogue.");
            // an item that was used is switched off, so its requests keep their history
            await using var used = new NpgsqlCommand("select exists (select 1 from servicedesk.tickets where catalog_id = @i)", db); P(used, "i", id);
            var hasRequests = (bool)(await used.ExecuteScalarAsync(ct))!;
            await using var c = new NpgsqlCommand(hasRequests ? "update servicedesk.catalog set active = false where id = @i returning name" : "delete from servicedesk.catalog where id = @i returning name", db); P(c, "i", id);
            if (await c.ExecuteScalarAsync(ct) is not string name) return Results.NotFound();
            await audit.WriteAsync("servicedesk.catalog_deleted", name, hasRequests ? "switched off (has requests)" : "", ct: ct);
            return Results.Ok(new { ok = true, switchedOff = hasRequests });
        }));

        // ask for something from the catalogue
        Any(r.MapPost("/api/servicedesk/catalog/{id:int}/request", async (int id, JsonElement b, ClaimsPrincipal u, ITenantContext t, ISettings s, ICompanyDirectory companies, IUserDirectory users, IMailQueue mail, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var w = await ServiceDeskModule.WhoAsync(u, db, ct);
            if (w.Role == "viewer" || (!w.Staff && !w.Customer)) return Forbidden("Your role cannot raise requests.");
            if (w.Customer && (w.CompanyId == null || !w.MayRaise)) return Forbidden(w.CompanyId == null ? "Your account is not linked to a company yet. Ask your administrator." : "You may not raise requests.");
            string name, description, priority, assigneeName; List<FieldDef> fields; Guid[] approvers; Guid? assignee; int? hours; bool staffOnly, active;
            await using (var c = new NpgsqlCommand("select name, description, fields::text, approvers, assignee_id, assignee_name, priority, resolve_hours, staff_only, active from servicedesk.catalog where id = @i", db))
            {
                P(c, "i", id);
                await using var rd = await c.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) return Results.NotFound();
                name = rd.GetString(0); description = rd.GetString(1); fields = JsonSerializer.Deserialize<List<FieldDef>>(rd.GetString(2), Web) ?? new();
                approvers = rd.GetFieldValue<Guid[]>(3); assignee = rd.IsDBNull(4) ? null : rd.GetGuid(4); assigneeName = rd.GetString(5); priority = rd.GetString(6);
                hours = rd.IsDBNull(7) ? null : rd.GetInt32(7); staffOnly = rd.GetBoolean(8); active = rd.GetBoolean(9);
            }
            if (!active || (staffOnly && !w.Staff)) return Results.NotFound();

            // the answers, checked against the item's form
            var answers = new Dictionary<string, object?>(); var lines = new StringBuilder();
            var given = b.ValueKind == JsonValueKind.Object && b.TryGetProperty("answers", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
            foreach (var f in fields)
            {
                JsonElement v = default; var has = given.ValueKind == JsonValueKind.Object && given.TryGetProperty(f.Key, out v) && v.ValueKind != JsonValueKind.Null;
                string text = "";
                if (has)
                {
                    switch (f.Kind)
                    {
                        case "checkbox": if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return ServiceDeskModule.Bad("bad_answer", f.Label + ": yes or no."); answers[f.Key] = v.GetBoolean(); text = v.GetBoolean() ? "Yes" : "No"; break;
                        case "number":
                            if (v.ValueKind != JsonValueKind.Number && !(v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out _))) return ServiceDeskModule.Bad("bad_answer", f.Label + ": a number.");
                            var num = v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : decimal.Parse(v.GetString()!, CultureInfo.InvariantCulture); answers[f.Key] = num; text = num.ToString(CultureInfo.InvariantCulture); break;
                        default:
                            if (v.ValueKind != JsonValueKind.String) return ServiceDeskModule.Bad("bad_answer", f.Label + ": text.");
                            text = v.GetString()!.Trim();
                            if (text.Length > (f.Kind == "longtext" ? 8000 : 500)) return ServiceDeskModule.Bad("bad_answer", f.Label + " is too long.");
                            if (f.Kind == "date" && text != "" && !DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return ServiceDeskModule.Bad("bad_answer", f.Label + ": a date.");
                            if (f.Kind == "email" && text != "" && !Regex.IsMatch(text, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) return ServiceDeskModule.Bad("bad_answer", f.Label + ": an email address.");
                            if (f.Kind == "choice" && text != "" && !(f.Options ?? Array.Empty<string>()).Contains(text)) return ServiceDeskModule.Bad("bad_answer", f.Label + ": choose one of the options.");
                            if (text != "") answers[f.Key] = text; break;
                    }
                }
                if (f.Required && (text == "" || (f.Kind == "checkbox" && text == "No"))) return ServiceDeskModule.Bad("answer_required", f.Label + " is needed.");
                if (text != "") lines.Append(f.Label).Append(": ").Append(text).Append('\n');
            }
            var note = S(b, "note"); if (note.Length > 4000) return ServiceDeskModule.Bad("bad_request", "The note is too long.");

            // who it is for: yourself, or (support team) someone else of a customer
            int? company = w.Customer ? w.CompanyId : null; Guid? requesterId = w.Id == Guid.Empty ? null : w.Id; string rName = w.Name, rEmail = w.Email;
            if (w.Staff)
            {
                if (b.TryGetProperty("companyId", out var cv) && cv.ValueKind == JsonValueKind.Number) company = cv.GetInt32();
                if (b.TryGetProperty("requesterId", out var rv) && rv.ValueKind == JsonValueKind.String && Guid.TryParse(rv.GetString(), out var rid))
                {
                    var ru = await users.GetAsync(rid, ct); if (ru == null) return ServiceDeskModule.Bad("bad_requester", "That person does not exist.");
                    requesterId = ru.Id; rName = ru.Name; rEmail = ru.Email;
                    if (company == null) { await using var pc = new NpgsqlCommand("select company_id from servicedesk.people where user_id = @u", db); P(pc, "u", ru.Id); if (await pc.ExecuteScalarAsync(ct) is int pci) company = pci; }
                }
            }
            string companyName = "";
            if (company is int ci) { var co = await companies.GetAsync(ci, ct); if (co == null) return ServiceDeskModule.Bad("bad_company", "That company does not exist."); companyName = co.Name; }

            var waits = approvers.Length > 0;
            var sla = await SlaConfig.LoadAsync(s, ct);
            var prio = SlaConfig.Prios.Contains(priority) ? priority : "medium";
            var body = (description != "" ? description + "\n\n" : "") + lines.ToString().TrimEnd() + (note != "" ? "\n\nNote: " + note : "");
            await using var ins = new NpgsqlCommand(@"insert into servicedesk.tickets
                (subject, description, priority, company_id, company_name, requester_id, requester_name, requester_email, source, created_by, kind, catalog_id, form,
                 assignee_id, assignee_name, status, approval, paused_at, respond_due, resolve_due)
                values (@s, @d, @p, @co, @con, @ri, @rn, @re, 'catalog', @by, 'request', @cat, @f::jsonb, @ai, @an, @st, @ap, case when @wait then now() end,
                        now() + make_interval(secs => @rp * 3600), now() + make_interval(secs => @rs * 3600)) returning id, number", db);
            P(ins, "s", name.Length > 200 ? name[..200] : name); P(ins, "d", body); P(ins, "p", prio); P(ins, "co", company); P(ins, "con", companyName);
            P(ins, "ri", requesterId); P(ins, "rn", rName); P(ins, "re", rEmail); P(ins, "by", w.Email); P(ins, "cat", id); P(ins, "f", JsonSerializer.Serialize(answers));
            P(ins, "ai", assignee); P(ins, "an", assigneeName); P(ins, "st", waits ? "hold" : "open"); P(ins, "ap", waits ? "pending" : ""); P(ins, "wait", waits);
            P(ins, "rp", sla.Respond[prio]); P(ins, "rs", hours ?? sla.Resolve[prio]);
            long tid; string number;
            await using (var rd = await ins.ExecuteReaderAsync(ct)) { await rd.ReadAsync(ct); tid = rd.GetInt64(0); number = rd.GetString(1); }
            if (waits)
                await using (var cm = new NpgsqlCommand("insert into servicedesk.comments (ticket_id, kind, author_name, body) values (@t, 'system', 'Service Desk', 'Waiting for approval.')", db)) { P(cm, "t", tid); await cm.ExecuteNonQueryAsync(ct); }
            await ServiceDeskModule.Event(db, tid, "ticket.created", new { number, priority = prio, company, kind = "request", catalog = id }, ct);
            await audit.WriteAsync("servicedesk.ticket_created", number, name, ct: ct);
            await ServiceDeskModule.Tell(mail, users, db, tid, "created", body, w, ct);
            if (waits)
                foreach (var ap in approvers)
                {
                    var au = await users.GetAsync(ap, ct);
                    if (au == null || !au.Active || string.IsNullOrWhiteSpace(au.Email) || au.Id == requesterId) continue;
                    try
                    {
                        await mail.EnqueueTemplateAsync("request_approval", au.Email, new Dictionary<string, string>
                        {
                            ["number"] = number, ["subject"] = name, ["requester"] = rName, ["company"] = companyName, ["item"] = name, ["details"] = lines.ToString().TrimEnd(), ["path"] = "#/servicedesk/approvals",
                        }, number, ct);
                    }
                    catch (Exception e) when (e is not OperationCanceledException) { /* mail is best effort */ }
                }
            return Results.Created("/api/servicedesk/tickets/" + tid, new { id = tid, number, approval = waits ? "pending" : "" });
        }));

        // what waits for my approval (administrators see all)
        Any(r.MapGet("/api/servicedesk/approvals", async (ClaimsPrincipal u, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var w = await ServiceDeskModule.WhoAsync(u, db, ct);
            await using var c = new NpgsqlCommand(@"select t.id, t.number, t.subject, t.requester_name, t.company_name, t.created_at, t.description, k.name
                from servicedesk.tickets t join servicedesk.catalog k on k.id = t.catalog_id
                where t.approval = 'pending' and (@admin or @me = any(k.approvers)) order by t.created_at", db);
            P(c, "admin", w.Admin); P(c, "me", w.Id);
            var list = new List<object>();
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) list.Add(new { id = rd.GetInt64(0), number = rd.GetString(1), subject = rd.GetString(2), requester = rd.GetString(3), company = rd.GetString(4), createdAt = rd.GetDateTime(5), details = rd.GetString(6), item = rd.GetString(7) });
            return Results.Ok(list);
        }));

        Any(r.MapPost("/api/servicedesk/tickets/{id:long}/approval", async (long id, JsonElement b, ClaimsPrincipal u, ITenantContext t, IUserDirectory users, IMailQueue mail, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var w = await ServiceDeskModule.WhoAsync(u, db, ct);
            string number, approval; Guid[] approvers; Guid? requester;
            await using (var c = new NpgsqlCommand("select t.number, t.approval, coalesce(k.approvers, '{}'), t.requester_id from servicedesk.tickets t left join servicedesk.catalog k on k.id = t.catalog_id where t.id = @i", db))
            {
                P(c, "i", id);
                await using var rd = await c.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) return Results.NotFound();
                number = rd.GetString(0); approval = rd.GetString(1); approvers = rd.GetFieldValue<Guid[]>(2); requester = rd.IsDBNull(3) ? null : rd.GetGuid(3);
            }
            if (approval != "pending") return ServiceDeskModule.Bad("not_pending", "This request is not waiting for approval.");
            if (!w.Admin && !approvers.Contains(w.Id)) return Forbidden("You are not an approver of this request.");
            if (!w.Admin && requester == w.Id) return Forbidden("You cannot approve your own request.");
            var decision = S(b, "decision"); var note = S(b, "note");
            if (decision is not ("approve" or "reject")) return ServiceDeskModule.Bad("bad_decision", "decision must be approve or reject");
            if (decision == "reject" && note == "") return ServiceDeskModule.Bad("note_required", "Say why the request is turned down.");
            if (note.Length > 2000) return ServiceDeskModule.Bad("bad_request", "The note is too long.");
            var ok = decision == "approve";
            // approved: the clock runs again from where it stopped; rejected: the request is closed
            await using (var c = new NpgsqlCommand(ok
                ? @"update servicedesk.tickets set approval = 'approved', approval_by = @by, approval_at = now(), approval_note = @n, status = 'open',
                      paused_s = paused_s + coalesce(extract(epoch from now() - paused_at)::bigint, 0),
                      resolve_due = resolve_due + coalesce(now() - paused_at, interval '0'), respond_due = respond_due + coalesce(now() - paused_at, interval '0'), paused_at = null, updated_at = now() where id = @i"
                : @"update servicedesk.tickets set approval = 'rejected', approval_by = @by, approval_at = now(), approval_note = @n, status = 'closed', closed_at = now(), paused_at = null, updated_at = now() where id = @i", db))
            { P(c, "by", w.Name); P(c, "n", note); P(c, "i", id); await c.ExecuteNonQueryAsync(ct); }
            var text = ok ? $"Approved by {w.Name}." + (note != "" ? " " + note : "") : $"Not approved by {w.Name}: {note}";
            await using (var c = new NpgsqlCommand("insert into servicedesk.comments (ticket_id, kind, author_id, author_name, body) values (@t, 'public', @a, @n, @b)", db))
            { P(c, "t", id); P(c, "a", w.Id); P(c, "n", w.Name); P(c, "b", text); await c.ExecuteNonQueryAsync(ct); }
            await ServiceDeskModule.Event(db, id, ok ? "ticket.approved" : "ticket.rejected", new { number, by = w.Name }, ct);
            await audit.WriteAsync(ok ? "servicedesk.request_approved" : "servicedesk.request_rejected", number, note, ct: ct);
            await ServiceDeskModule.Tell(mail, users, db, id, "reply", text, w, ct);
            return Results.Ok(new { approval = ok ? "approved" : "rejected" });
        }));
    }

    static async Task<IResult> Save(int? id, JsonElement b, ClaimsPrincipal u, ITenantContext t, IUserDirectory users, IAuditLog audit, CancellationToken ct)
    {
        await using var db = await ServiceDeskModule.Open(t, ct);
        if (!(await ServiceDeskModule.WhoAsync(u, db, ct)).Admin) return Forbidden("Only an administrator manages the catalogue.");
        var name = S(b, "name"); if (name == "" || name.Length > 120) return ServiceDeskModule.Bad("name_required", "Give the item a name.");
        var category = S(b, "category"); if (category == "") category = "General";
        if (category.Length > 60 || S(b, "description").Length > 4000 || S(b, "icon").Length > 8) return ServiceDeskModule.Bad("bad_request", "Too long.");
        var prio = S(b, "priority"); if (prio == "") prio = "medium"; if (!SlaConfig.Prios.Contains(prio)) return ServiceDeskModule.Bad("bad_priority");
        int? hours = b.TryGetProperty("resolveHours", out var hv) && hv.ValueKind == JsonValueKind.Number && hv.TryGetInt32(out var h) ? h : null;
        if (hours is < 1 or > 8760) return ServiceDeskModule.Bad("bad_hours", "Between 1 and 8,760 hours.");
        // the form
        var fields = new List<FieldDef>();
        if (b.TryGetProperty("fields", out var fs) && fs.ValueKind == JsonValueKind.Array)
            foreach (var f in fs.EnumerateArray())
            {
                var k = S(f, "key"); var label = S(f, "label"); var kind = S(f, "kind"); if (kind == "") kind = "text";
                if (!Key.IsMatch(k)) return ServiceDeskModule.Bad("bad_field", $"Field key \"{k}\": lower-case letters, digits and _, starting with a letter.");
                if (label == "" || label.Length > 120) return ServiceDeskModule.Bad("bad_field", "Every field needs a label.");
                if (!Kinds.Contains(kind)) return ServiceDeskModule.Bad("bad_field", "Field kind must be " + string.Join(", ", Kinds) + ".");
                string[]? opts = null;
                if (kind == "choice")
                {
                    opts = f.TryGetProperty("options", out var ov) && ov.ValueKind == JsonValueKind.Array ? ov.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(x => x != "").Distinct().ToArray() : Array.Empty<string>();
                    if (opts.Length == 0 || opts.Length > 50) return ServiceDeskModule.Bad("bad_field", label + ": give between 1 and 50 options.");
                }
                if (fields.Any(x => x.Key == k)) return ServiceDeskModule.Bad("bad_field", $"The key \"{k}\" is used twice.");
                fields.Add(new FieldDef(k, label, kind, opts, f.TryGetProperty("required", out var rq) && rq.ValueKind == JsonValueKind.True, S(f, "help") is { Length: > 0 } hp ? hp : null));
            }
        if (fields.Count > 30) return ServiceDeskModule.Bad("bad_field", "At most 30 fields.");
        // approvers and the person it goes to: active staff
        var approvers = new List<Guid>();
        if (b.TryGetProperty("approvers", out var av) && av.ValueKind == JsonValueKind.Array)
            foreach (var x in av.EnumerateArray())
            {
                if (x.ValueKind != JsonValueKind.String || !Guid.TryParse(x.GetString(), out var g)) return ServiceDeskModule.Bad("bad_approver");
                var au = await users.GetAsync(g, ct); if (au == null || !au.Active || au.Role is not ("admin" or "engineer")) return ServiceDeskModule.Bad("bad_approver", "Approvers are active staff members.");
                if (!approvers.Contains(g)) approvers.Add(g);
            }
        Guid? assignee = null; var assigneeName = "";
        if (b.TryGetProperty("assigneeId", out var asv) && asv.ValueKind == JsonValueKind.String && Guid.TryParse(asv.GetString(), out var ag))
        {
            var au = await users.GetAsync(ag, ct); if (au == null || !au.Active || au.Role is not ("admin" or "engineer")) return ServiceDeskModule.Bad("bad_assignee", "Requests go to an active engineer or administrator.");
            assignee = ag; assigneeName = au.Name;
        }
        await using var c = new NpgsqlCommand(id == null
            ? @"insert into servicedesk.catalog (name, category, description, icon, fields, approvers, assignee_id, assignee_name, priority, resolve_hours, staff_only, active, position)
                values (@n, @c, @d, @ic, @f::jsonb, @ap, @ai, @an, @p, @h, @so, @ac, @pos) returning id"
            : @"update servicedesk.catalog set name=@n, category=@c, description=@d, icon=@ic, fields=@f::jsonb, approvers=@ap, assignee_id=@ai, assignee_name=@an, priority=@p, resolve_hours=@h,
                staff_only=@so, active=@ac, position=@pos, updated_at=now() where id=@i returning id", db);
        P(c, "n", name); P(c, "c", category); P(c, "d", S(b, "description")); P(c, "ic", S(b, "icon")); P(c, "f", JsonSerializer.Serialize(fields, Web));
        c.Parameters.AddWithValue("ap", approvers.ToArray()); P(c, "ai", assignee); P(c, "an", assigneeName); P(c, "p", prio); P(c, "h", hours);
        P(c, "so", b.TryGetProperty("staffOnly", out var so) && so.ValueKind == JsonValueKind.True); P(c, "ac", !b.TryGetProperty("active", out var ac) || ac.ValueKind != JsonValueKind.False);
        P(c, "pos", b.TryGetProperty("position", out var po) && po.TryGetInt32(out var pi) ? pi : 0); P(c, "i", id ?? 0);
        if (await c.ExecuteScalarAsync(ct) is not int nid) return Results.NotFound();
        await audit.WriteAsync(id == null ? "servicedesk.catalog_created" : "servicedesk.catalog_changed", name, $"{fields.Count} fields, {approvers.Count} approvers", ct: ct);
        return Results.Ok(new { id = nid });
    }
}
