using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace DeskAsk.Platform.ServiceDesk;

/// <summary>
/// The ITIL parts around changes and problems: the change advisory board (who may approve), the change calendar with clashes, standard
/// (pre-approved) change templates, the items a change touches; for problems, impact and urgency, the known error database and closing
/// the incidents a resolved problem caused.
/// </summary>
internal static class ItilApi
{
    public const string CabKey = "servicedesk.cab";
    static IResult Forbidden(string d) => Results.Json(new { error = "forbidden", detail = d }, statusCode: 403);
    static void P(NpgsqlCommand c, string n, object? v) => ServiceDeskModule.P(c, n, v);
    static string S(JsonElement b, string n) => b.ValueKind == JsonValueKind.Object && b.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";

    /// <summary>The change board's members; empty means any staff member may approve (as before).</summary>
    public static async Task<List<Guid>> CabAsync(ISettings s, CancellationToken ct)
    {
        var raw = await s.GetAsync(CabKey, ct);
        if (string.IsNullOrWhiteSpace(raw)) return new();
        try
        {
            using var d = JsonDocument.Parse(raw);
            return d.RootElement.TryGetProperty("members", out var m) && m.ValueKind == JsonValueKind.Array
                ? m.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String && Guid.TryParse(x.GetString(), out _)).Select(x => Guid.Parse(x.GetString()!)).ToList() : new();
        }
        catch (JsonException) { return new(); }
    }

    /// <summary>Mails the change board (or every active staff member when there is no board) that a change waits for approval.</summary>
    public static async Task TellCabAsync(ISettings s, IUserDirectory users, IMailQueue mail, NpgsqlConnection db, long id, Guid? owner, CancellationToken ct)
    {
        try
        {
            string number, title, type, risk, ownerName; DateTime? start;
            await using (var c = new NpgsqlCommand("select number, title, type, risk, owner_name, start_at from servicedesk.changes where id = @i", db))
            {
                P(c, "i", id); await using var rd = await c.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) return;
                number = rd.GetString(0); title = rd.GetString(1); type = rd.GetString(2); risk = rd.GetString(3); ownerName = rd.GetString(4); start = rd.IsDBNull(5) ? null : rd.GetDateTime(5);
            }
            var cab = await CabAsync(s, ct);
            var people = (await users.ListAsync(ct)).Where(u => u.Active && u.Email.Contains('@') && u.Role is "admin" or "engineer" && u.Id != owner && (cab.Count == 0 || cab.Contains(u.Id)))
                .Where(u => type != "emergency" || u.Role == "admin");
            foreach (var u in people)
                await mail.EnqueueTemplateAsync("change_approval", u.Email, new Dictionary<string, string>
                {
                    ["number"] = number, ["title"] = title, ["type"] = type, ["risk"] = risk, ["owner"] = ownerName,
                    ["start"] = start?.ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture) + (start == null ? "not scheduled" : " UTC"), ["path"] = "#/servicedesk/changes/" + id,
                }, number, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException) { /* mail is best effort */ }
    }

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        RouteHandlerBuilder Any(RouteHandlerBuilder b) => b.RequireAuthorization().RequireModule(key);

        // ---- change advisory board
        Any(r.MapGet("/api/servicedesk/cab", async (ClaimsPrincipal u, ITenantContext t, ISettings s, IUserDirectory users, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var w = await ServiceDeskModule.WhoAsync(u, db, ct);
            if (!w.Staff && w.Role != "viewer") return Forbidden("The change board is for the support team.");
            var ids = await CabAsync(s, ct);
            var all = await users.ListAsync(ct);
            var quorum = await ChangesApi.Quorum(s, "normal", ct);
            return Results.Ok(new
            {
                quorum,
                members = ids.Select(i => all.FirstOrDefault(x => x.Id == i)).Where(x => x != null).Select(x => new { id = x!.Id, name = x.Name, email = x.Email }),
                staff = all.Where(x => x.Active && x.Role is "admin" or "engineer").OrderBy(x => x.Name).Select(x => new { id = x.Id, name = x.Name, email = x.Email, role = x.Role }),
            });
        }));
        Any(r.MapPut("/api/servicedesk/cab", async (JsonElement b, ClaimsPrincipal u, ITenantContext t, ISettings s, IUserDirectory users, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            if (!(await ServiceDeskModule.WhoAsync(u, db, ct)).Admin) return Forbidden("Only an administrator sets the change board.");
            var ids = new List<Guid>();
            if (b.TryGetProperty("members", out var m) && m.ValueKind == JsonValueKind.Array)
                foreach (var x in m.EnumerateArray())
                {
                    if (x.ValueKind != JsonValueKind.String || !Guid.TryParse(x.GetString(), out var g)) return ServiceDeskModule.Bad("bad_member");
                    var p = await users.GetAsync(g, ct); if (p == null || !p.Active || p.Role is not ("admin" or "engineer")) return ServiceDeskModule.Bad("bad_member", "Members are active staff members.");
                    if (!ids.Contains(g)) ids.Add(g);
                }
            // the same setting holds the quorum (approvals needed for a normal change): keep it
            int? quorum = null;
            try { var raw = await s.GetAsync(CabKey, ct); if (!string.IsNullOrWhiteSpace(raw) && JsonDocument.Parse(raw).RootElement.TryGetProperty("quorum", out var q) && q.TryGetInt32(out var qn)) quorum = qn; }
            catch (JsonException) { }
            if (b.TryGetProperty("quorum", out var nq) && nq.TryGetInt32(out var nqv)) { if (nqv is < 1 or > 9) return ServiceDeskModule.Bad("bad_quorum", "Between 1 and 9 approvals."); quorum = nqv; }
            if (quorum != null && ids.Count > 0 && quorum > ids.Count) return ServiceDeskModule.Bad("bad_quorum", $"The board has {ids.Count} member(s) but a normal change needs {quorum} approvals.");
            await s.SetAsync(CabKey, quorum == null ? JsonSerializer.Serialize(new { members = ids }) : JsonSerializer.Serialize(new { members = ids, quorum }), ct);
            await audit.WriteAsync("servicedesk.cab_set", "cab", ids.Count + " members", ct: ct);
            return Results.Ok(new { ok = true });
        }));

        // ---- change calendar: what is planned when, and which changes clash (same customer or a shared asset, overlapping windows)
        Any(r.MapGet("/api/servicedesk/changes/calendar", async (DateTime? from, DateTime? to, ClaimsPrincipal u, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var w = await ServiceDeskModule.WhoAsync(u, db, ct);
            if (!w.Staff && w.Role != "viewer") return Forbidden("Changes are for the support team.");
            var f = (from ?? DateTime.UtcNow.Date.AddDays(-7)).ToUniversalTime(); var e = (to ?? f.AddDays(42)).ToUniversalTime();
            if (e <= f || (e - f).TotalDays > 400) return ServiceDeskModule.Bad("bad_range", "Choose up to 400 days.");
            var rows = new List<(long Id, string Number, string Title, string Type, string Status, string Risk, int? Company, string CompanyName, DateTime Start, DateTime End, long[] Assets)>();
            await using (var c = new NpgsqlCommand(@"select id, number, title, type, status, risk, company_id, company_name, start_at, coalesce(end_at, start_at + interval '1 hour'), asset_ids
                from servicedesk.changes where start_at is not null and status not in ('cancelled','rejected') and start_at < @e and coalesce(end_at, start_at + interval '1 hour') > @f order by start_at", db))
            {
                P(c, "f", f); P(c, "e", e);
                await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                    rows.Add((rd.GetInt64(0), rd.GetString(1), rd.GetString(2), rd.GetString(3), rd.GetString(4), rd.GetString(5), rd.IsDBNull(6) ? null : rd.GetInt32(6), rd.GetString(7), rd.GetDateTime(8), rd.GetDateTime(9), rd.GetFieldValue<long[]>(10)));
            }
            var open = rows.Where(x => x.Status is not ("completed" or "failed" or "closed")).ToList();
            return Results.Ok(new
            {
                from = f, to = e,
                items = rows.Select(x => new
                {
                    id = x.Id, number = x.Number, title = x.Title, type = x.Type, status = x.Status, risk = x.Risk, companyId = x.Company, company = x.CompanyName, startAt = x.Start, endAt = x.End,
                    clashes = x.Status is "completed" or "failed" or "closed" ? new List<string>() : open.Where(y => y.Id != x.Id && y.Start < x.End && x.Start < y.End
                        && ((x.Company != null && x.Company == y.Company) || x.Assets.Intersect(y.Assets).Any())).Select(y => y.Number).ToList()
                })
            });
        }));

        // ---- what a change touches (category, test plan, configuration items) and its clashes
        Any(r.MapGet("/api/servicedesk/changes/{id:long}/details", async (long id, ClaimsPrincipal u, ITenantContext t, IAssetDirectory assets, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var w = await ServiceDeskModule.WhoAsync(u, db, ct);
            if (!w.Staff && w.Role != "viewer") return Forbidden("Changes are for the support team.");
            string category, testPlan; long[] ids; int? tpl; DateTime? s0, e0; int? company;
            await using (var c = new NpgsqlCommand("select category, test_plan, asset_ids, template_id, start_at, coalesce(end_at, start_at + interval '1 hour'), company_id from servicedesk.changes where id = @i", db))
            {
                P(c, "i", id); await using var rd = await c.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) return Results.NotFound();
                category = rd.GetString(0); testPlan = rd.GetString(1); ids = rd.GetFieldValue<long[]>(2); tpl = rd.IsDBNull(3) ? null : rd.GetInt32(3);
                s0 = rd.IsDBNull(4) ? null : rd.GetDateTime(4); e0 = rd.IsDBNull(5) ? null : rd.GetDateTime(5); company = rd.IsDBNull(6) ? null : rd.GetInt32(6);
            }
            var list = new List<object>();
            foreach (var a in ids) if (await assets.GetAsync(a, ct) is { } x) list.Add(new { id = x.Id, tag = x.Tag, name = x.Name });
            var clashes = new List<object>();
            if (s0 != null)
            {
                await using var c = new NpgsqlCommand(@"select id, number, title, start_at, coalesce(end_at, start_at + interval '1 hour') from servicedesk.changes
                    where id <> @i and start_at is not null and status not in ('cancelled','rejected','completed','failed','closed')
                      and start_at < @e and coalesce(end_at, start_at + interval '1 hour') > @s and ((@co::int is not null and company_id = @co) or asset_ids && @a) order by start_at", db);
                P(c, "i", id); P(c, "s", s0); P(c, "e", e0); P(c, "co", company); c.Parameters.AddWithValue("a", ids);
                await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) clashes.Add(new { id = rd.GetInt64(0), number = rd.GetString(1), title = rd.GetString(2), startAt = rd.GetDateTime(3), endAt = rd.GetDateTime(4) });
            }
            return Results.Ok(new { category, testPlan, assets = list, templateId = tpl, clashes });
        }));
        Any(r.MapPut("/api/servicedesk/changes/{id:long}/details", async (long id, JsonElement b, ClaimsPrincipal u, ITenantContext t, IAssetDirectory assets, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var w = await ServiceDeskModule.WhoAsync(u, db, ct);
            if (!w.Staff) return Forbidden("Only the support team edits changes.");
            var category = S(b, "category"); var test = S(b, "testPlan");
            if (category.Length > 100 || test.Length > 8000) return ServiceDeskModule.Bad("bad_request", "Too long.");
            var ids = new List<long>();
            if (b.TryGetProperty("assetIds", out var av) && av.ValueKind == JsonValueKind.Array)
                foreach (var x in av.EnumerateArray())
                {
                    if (!x.TryGetInt64(out var a) || await assets.GetAsync(a, ct) == null) return ServiceDeskModule.Bad("bad_asset", "One of the assets does not exist.");
                    if (!ids.Contains(a)) ids.Add(a);
                }
            if (ids.Count > 100) return ServiceDeskModule.Bad("bad_asset", "At most 100 assets.");
            await using var c = new NpgsqlCommand("update servicedesk.changes set category = @c, test_plan = @tp, asset_ids = @a, updated_at = now() where id = @i and status not in ('closed','cancelled') returning number", db);
            P(c, "c", category); P(c, "tp", test); c.Parameters.AddWithValue("a", ids.ToArray()); P(c, "i", id);
            if (await c.ExecuteScalarAsync(ct) is not string number) return ServiceDeskModule.Bad("bad_status", "A closed or cancelled change is not edited.");
            await audit.WriteAsync("servicedesk.change_updated", number, "details", ct: ct);
            return Results.Ok(new { ok = true });
        }));

        // ---- standard changes: pre-approved templates
        Any(r.MapGet("/api/servicedesk/change-templates", async (ClaimsPrincipal u, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var w = await ServiceDeskModule.WhoAsync(u, db, ct);
            if (!w.Staff && w.Role != "viewer") return Forbidden("Changes are for the support team.");
            await using var c = new NpgsqlCommand("select id, name, category, description, plan, rollback, test_plan, risk, active from servicedesk.change_templates where active or @admin order by name", db); P(c, "admin", w.Admin);
            var list = new List<object>();
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) list.Add(new { id = rd.GetInt32(0), name = rd.GetString(1), category = rd.GetString(2), description = rd.GetString(3), plan = rd.GetString(4), rollback = rd.GetString(5), testPlan = rd.GetString(6), risk = rd.GetString(7), active = rd.GetBoolean(8) });
            return Results.Ok(list);
        }));
        async Task<IResult> SaveTemplate(int? id, JsonElement b, ClaimsPrincipal u, ITenantContext t, IAuditLog audit, CancellationToken ct)
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var w = await ServiceDeskModule.WhoAsync(u, db, ct);
            if (!w.Admin) return Forbidden("Only an administrator manages standard changes.");
            var name = S(b, "name"); if (name == "" || name.Length > 200) return ServiceDeskModule.Bad("name_required", "Give the standard change a name.");
            var plan = S(b, "plan"); var rollback = S(b, "rollback");
            if (plan == "" || rollback == "") return ServiceDeskModule.Bad("plan_required", "A standard change has a tested implementation plan and a rollback plan.");
            var risk = S(b, "risk"); if (risk == "") risk = "low"; if (risk is not ("low" or "medium")) return ServiceDeskModule.Bad("bad_level", "Standard changes are low or medium risk.");
            if (plan.Length > 8000 || rollback.Length > 8000 || S(b, "testPlan").Length > 8000 || S(b, "description").Length > 4000 || S(b, "category").Length > 100) return ServiceDeskModule.Bad("bad_request", "Too long.");
            try
            {
                await using var c = new NpgsqlCommand(id == null
                    ? "insert into servicedesk.change_templates (name, category, description, plan, rollback, test_plan, risk, active, created_by) values (@n,@c,@d,@p,@r,@tp,@rk,@a,@by) returning id"
                    : "update servicedesk.change_templates set name=@n, category=@c, description=@d, plan=@p, rollback=@r, test_plan=@tp, risk=@rk, active=@a where id=@i returning id", db);
                P(c, "n", name); P(c, "c", S(b, "category")); P(c, "d", S(b, "description")); P(c, "p", plan); P(c, "r", rollback); P(c, "tp", S(b, "testPlan")); P(c, "rk", risk);
                P(c, "a", !b.TryGetProperty("active", out var ac) || ac.ValueKind != JsonValueKind.False); P(c, "by", w.Name); P(c, "i", id ?? 0);
                if (await c.ExecuteScalarAsync(ct) is not int nid) return Results.NotFound();
                await audit.WriteAsync("servicedesk.change_template_saved", name, "", ct: ct);
                return Results.Ok(new { id = nid });
            }
            catch (PostgresException e) when (e.SqlState == "23505") { return Results.Conflict(new { error = "exists", detail = "A standard change with this name exists." }); }
        }
        Any(r.MapPost("/api/servicedesk/change-templates", (JsonElement b, ClaimsPrincipal u, ITenantContext t, IAuditLog audit, CancellationToken ct) => SaveTemplate(null, b, u, t, audit, ct)));
        Any(r.MapPut("/api/servicedesk/change-templates/{id:int}", (int id, JsonElement b, ClaimsPrincipal u, ITenantContext t, IAuditLog audit, CancellationToken ct) => SaveTemplate(id, b, u, t, audit, ct)));
        // raise a standard change from a template: pre-approved, ready to schedule
        Any(r.MapPost("/api/servicedesk/change-templates/{id:int}/use", async (int id, JsonElement b, ClaimsPrincipal u, ITenantContext t, ICompanyDirectory companies, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var w = await ServiceDeskModule.WhoAsync(u, db, ct);
            if (!w.Staff) return Forbidden("Only the support team raises changes.");
            string name, category, description, plan, rollback, test, risk;
            await using (var c = new NpgsqlCommand("select name, category, description, plan, rollback, test_plan, risk from servicedesk.change_templates where id = @i and active", db))
            {
                P(c, "i", id); await using var rd = await c.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) return Results.NotFound();
                name = rd.GetString(0); category = rd.GetString(1); description = rd.GetString(2); plan = rd.GetString(3); rollback = rd.GetString(4); test = rd.GetString(5); risk = rd.GetString(6);
            }
            DateTime? start = DateTime.TryParse(S(b, "startAt"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var sv) ? sv : null;
            DateTime? end = DateTime.TryParse(S(b, "endAt"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var ev) ? ev : null;
            if (start != null && end != null && end <= start) return ServiceDeskModule.Bad("bad_window", "The end must be after the start.");
            int? company = null; var companyName = "";
            if (b.TryGetProperty("companyId", out var cv) && cv.ValueKind == JsonValueKind.Number) { var co = await companies.GetAsync(cv.GetInt32(), ct); if (co == null) return ServiceDeskModule.Bad("bad_company"); company = co.Id; companyName = co.Name; }
            var title = S(b, "title"); if (title == "") title = name + (companyName != "" ? " — " + companyName : "");
            if (title.Length > 200) title = title[..200];
            await using var ins = new NpgsqlCommand(@"insert into servicedesk.changes (title, description, type, status, risk, impact, plan, rollback, test_plan, category, company_id, company_name, owner_id, owner_name, start_at, end_at, created_by, template_id)
                values (@t, @d, 'standard', case when @s is null then 'approved' else 'scheduled' end, @rk, 'low', @p, @r, @tp, @c, @co, @con, @oi, @on, @s, @e, @by, @tid) returning id, number", db);
            P(ins, "t", title); P(ins, "d", description); P(ins, "rk", risk); P(ins, "p", plan); P(ins, "r", rollback); P(ins, "tp", test); P(ins, "c", category); P(ins, "co", company); P(ins, "con", companyName);
            P(ins, "oi", w.Id); P(ins, "on", w.Name); ins.Parameters.Add(new NpgsqlParameter("s", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = start.HasValue ? start.Value : DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("e", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = end.HasValue ? end.Value : DBNull.Value }); P(ins, "by", w.Name); P(ins, "tid", id);
            long cid; string number;
            await using (var rd = await ins.ExecuteReaderAsync(ct)) { await rd.ReadAsync(ct); cid = rd.GetInt64(0); number = rd.GetString(1); }
            await using (var n = new NpgsqlCommand("insert into servicedesk.item_notes (kind, item_id, author_name, body) values ('change', @i, @a, @b)", db))
            { P(n, "i", cid); P(n, "a", w.Name); P(n, "b", $"Raised from the standard change “{name}”: pre-approved."); await n.ExecuteNonQueryAsync(ct); }
            await audit.WriteAsync("servicedesk.change_created", number, title + " (standard)", ct: ct);
            return Results.Ok(new { id = cid, number });
        }));

        // ---- problems: impact and urgency set the priority, as for incidents
        Any(r.MapPut("/api/servicedesk/problems/{id:long}/impact", async (long id, JsonElement b, ClaimsPrincipal u, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            if (!(await ServiceDeskModule.WhoAsync(u, db, ct)).Staff) return Forbidden("Only the support team edits problems.");
            var im = S(b, "impact"); var ur = S(b, "urgency");
            if ((im != "" && !SlaConfig.Levels.Contains(im)) || (ur != "" && !SlaConfig.Levels.Contains(ur))) return ServiceDeskModule.Bad("bad_level", "impact and urgency must be low, medium or high");
            var prio = im != "" && ur != "" ? SlaConfig.PriorityFor(im, ur) : null;
            await using var c = new NpgsqlCommand("update servicedesk.problems set impact = @im, urgency = @ur, priority = coalesce(@p, priority), updated_at = now() where id = @i returning priority", db);
            P(c, "im", im); P(c, "ur", ur); P(c, "p", prio); P(c, "i", id);
            return await c.ExecuteScalarAsync(ct) is string p ? Results.Ok(new { priority = p }) : Results.NotFound();
        }));
        Any(r.MapGet("/api/servicedesk/problems/{id:long}/impact", async (long id, ClaimsPrincipal u, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var w = await ServiceDeskModule.WhoAsync(u, db, ct);
            if (!w.Staff && w.Role != "viewer") return Forbidden("Problems are for the support team.");
            await using var c = new NpgsqlCommand("select impact, urgency from servicedesk.problems where id = @i", db); P(c, "i", id);
            await using var rd = await c.ExecuteReaderAsync(ct);
            return await rd.ReadAsync(ct) ? Results.Ok(new { impact = rd.GetString(0), urgency = rd.GetString(1) }) : Results.NotFound();
        }));

        // a resolved problem closes the incidents it caused: each gets the resolution as a reply and is resolved
        Any(r.MapPost("/api/servicedesk/problems/{id:long}/resolve-incidents", async (long id, JsonElement b, ClaimsPrincipal u, ITenantContext t, IMailQueue mail, IUserDirectory users, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var w = await ServiceDeskModule.WhoAsync(u, db, ct);
            if (!w.Staff) return Forbidden("Only the support team resolves incidents.");
            string number, status, resolution, workaround;
            await using (var c = new NpgsqlCommand("select number, status, resolution, workaround from servicedesk.problems where id = @i", db))
            {
                P(c, "i", id); await using var rd = await c.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) return Results.NotFound();
                number = rd.GetString(0); status = rd.GetString(1); resolution = rd.GetString(2); workaround = rd.GetString(3);
            }
            if (status is not ("resolved" or "closed" or "known_error")) return ServiceDeskModule.Bad("bad_status", "Resolve the problem first, or record it as a known error with a workaround.");
            var text = S(b, "message");
            if (text == "") text = status == "known_error" ? $"This was caused by a known problem ({number}). Workaround: {workaround}" : $"The underlying problem ({number}) has been resolved. {resolution}";
            if (text.Length > 4000) return ServiceDeskModule.Bad("bad_request", "The message is too long.");
            var ids = new List<long>();
            await using (var c = new NpgsqlCommand("select t.id from servicedesk.problem_tickets l join servicedesk.tickets t on t.id = l.ticket_id where l.problem_id = @i and t.status not in ('resolved','closed')", db))
            { P(c, "i", id); await using var rd = await c.ExecuteReaderAsync(ct); while (await rd.ReadAsync(ct)) ids.Add(rd.GetInt64(0)); }
            foreach (var tid in ids)
            {
                await using (var c = new NpgsqlCommand(@"insert into servicedesk.comments (ticket_id, kind, author_id, author_name, body) values (@t, 'public', @a, @n, @b);
                    update servicedesk.tickets set status = 'resolved', resolved_at = now(), first_response_at = coalesce(first_response_at, now()),
                      paused_s = paused_s + coalesce(extract(epoch from now() - paused_at)::bigint, 0), paused_at = null, updated_at = now() where id = @t", db))
                { P(c, "t", tid); P(c, "a", w.Id); P(c, "n", w.Name); P(c, "b", text); await c.ExecuteNonQueryAsync(ct); }
                await ServiceDeskModule.Event(db, tid, "ticket.resolved", new { problem = number }, ct);
                await ServiceDeskModule.Tell(mail, users, db, tid, "resolved", text, w, ct);
            }
            await using (var n = new NpgsqlCommand("insert into servicedesk.item_notes (kind, item_id, author_name, body) values ('problem', @i, @a, @b)", db))
            { P(n, "i", id); P(n, "a", w.Name); P(n, "b", $"Resolved {ids.Count} linked incident(s)."); await n.ExecuteNonQueryAsync(ct); }
            await audit.WriteAsync("servicedesk.problem_incidents_resolved", number, ids.Count + " incidents", ct: ct);
            return Results.Ok(new { resolved = ids.Count });
        }));

        // ---- known error database: problems with a workaround people can use now
        Any(r.MapGet("/api/servicedesk/known-errors", async (string? q, ClaimsPrincipal u, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var w = await ServiceDeskModule.WhoAsync(u, db, ct);
            if (!w.Staff && w.Role != "viewer") return Forbidden("Known errors are for the support team.");
            var words = Words(q ?? "");
            await using var c = new NpgsqlCommand(@"select p.id, p.number, p.title, p.category, p.workaround, p.root_cause, p.status, p.updated_at,
                    (select count(*) from servicedesk.problem_tickets l where l.problem_id = p.id)
                from servicedesk.problems p where p.workaround <> '' and p.status in ('known_error','resolved','investigating','open')
                  and (@tq = '' or to_tsvector('simple', p.title || ' ' || p.description || ' ' || p.workaround || ' ' || p.category) @@ to_tsquery('simple', @tq))
                order by p.status = 'known_error' desc, p.updated_at desc limit 200", db);
            P(c, "tq", words);
            var list = new List<object>();
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) list.Add(new { id = rd.GetInt64(0), number = rd.GetString(1), title = rd.GetString(2), category = rd.GetString(3), workaround = rd.GetString(4), rootCause = rd.GetString(5), status = rd.GetString(6), updatedAt = rd.GetDateTime(7), incidents = rd.GetInt64(8) });
            return Results.Ok(list);
        }));
        // the known errors that may match a ticket (same category, or words of its subject)
        Any(r.MapGet("/api/servicedesk/tickets/{id:long}/known-errors", async (long id, ClaimsPrincipal u, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var w = await ServiceDeskModule.WhoAsync(u, db, ct);
            if (!w.Staff && w.Role != "viewer") return Results.Ok(Array.Empty<object>());
            string subject = "", category = "";
            await using (var c = new NpgsqlCommand("select subject, category from servicedesk.tickets where id = @i", db))
            { P(c, "i", id); await using var rd = await c.ExecuteReaderAsync(ct); if (!await rd.ReadAsync(ct)) return Results.NotFound(); subject = rd.GetString(0); category = rd.GetString(1); }
            var words = Words(subject);
            await using var q = new NpgsqlCommand(@"select p.id, p.number, p.title, p.workaround, p.status,
                    exists (select 1 from servicedesk.problem_tickets l where l.problem_id = p.id and l.ticket_id = @i)
                from servicedesk.problems p where p.workaround <> '' and p.status in ('known_error','investigating','open','resolved')
                  and ((@cat <> '' and lower(p.category) = lower(@cat)) or (@tq <> '' and to_tsvector('simple', p.title || ' ' || p.description || ' ' || p.workaround) @@ to_tsquery('simple', @tq)))
                order by p.status = 'known_error' desc, p.updated_at desc limit 5", db);
            P(q, "i", id); P(q, "cat", category); P(q, "tq", words);
            var list = new List<object>();
            await using var r2 = await q.ExecuteReaderAsync(ct);
            while (await r2.ReadAsync(ct)) list.Add(new { id = r2.GetInt64(0), number = r2.GetString(1), title = r2.GetString(2), workaround = r2.GetString(3), status = r2.GetString(4), linked = r2.GetBoolean(5) });
            return Results.Ok(list);
        }));
        // tie a ticket to a problem from the ticket screen
        Any(r.MapPost("/api/servicedesk/tickets/{id:long}/problem", async (long id, JsonElement b, ClaimsPrincipal u, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            if (!(await ServiceDeskModule.WhoAsync(u, db, ct)).Staff) return Forbidden("Only the support team links problems.");
            if (!b.TryGetProperty("problemId", out var pv) || !pv.TryGetInt64(out var pid)) return ServiceDeskModule.Bad("bad_request", "problemId is needed.");
            await using var c = new NpgsqlCommand(@"insert into servicedesk.problem_tickets (problem_id, ticket_id) select @p, @t where exists (select 1 from servicedesk.problems where id = @p) and exists (select 1 from servicedesk.tickets where id = @t)
                on conflict do nothing", db);
            P(c, "p", pid); P(c, "t", id);
            await c.ExecuteNonQueryAsync(ct);
            await audit.WriteAsync("servicedesk.problem_linked", id.ToString(CultureInfo.InvariantCulture), pid.ToString(CultureInfo.InvariantCulture), ct: ct);
            return Results.Ok(new { ok = true });
        }));
    }

    /// <summary>Words of at least three letters, as a full-text "any of these" query.</summary>
    static string Words(string text)
    {
        var w = Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]{3,}").Select(m => m.Value).Distinct().Take(12).ToList();
        string[] stop = { "the", "and", "for", "not", "with", "from", "this", "that", "can", "cannot", "does", "doesn", "unable", "please", "issue", "problem", "error" };
        w = w.Where(x => !stop.Contains(x)).ToList();
        return string.Join(" | ", w);
    }
}
