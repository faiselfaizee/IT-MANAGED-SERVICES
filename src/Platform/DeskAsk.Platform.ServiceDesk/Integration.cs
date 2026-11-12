using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DeskAsk.Platform.ServiceDesk;

/// <summary>
/// Connecting other systems to the Service Desk. Administrators create API keys (read or write) and webhooks here.
/// Programs call /api/v1 with a key; webhooks tell a program's address about ticket events, signed so the receiver can check them.
/// </summary>
internal static class IntegrationApi
{
    static readonly string[] Scopes = { "read", "write" };
    static readonly string[] Prios = { "critical", "high", "medium", "low" };
    static readonly string[] Statuses = { "open", "progress", "hold", "resolved", "closed" };

    internal static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    static string Token(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    static string? Str(JsonElement b, string n, int max)
    {
        if (b.ValueKind != JsonValueKind.Object || !b.TryGetProperty(n, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var s = (v.GetString() ?? "").Trim();
        return s.Length > max ? s[..max] : s;
    }
    static string Actor(HttpContext h) => h.User.FindFirst("name")?.Value ?? h.User.FindFirst("email")?.Value ?? "admin";

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        RouteHandlerBuilder Admin(RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole("admin")).RequireModule(key);
        RouteHandlerBuilder Keyed(RouteHandlerBuilder b, bool write) => b.AllowAnonymous().RequireModule(key).AddEndpointFilter(async (ctx, next) =>
        {
            var h = ctx.HttpContext; var raw = h.Request.Headers.Authorization.ToString();
            var token = raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? raw[7..].Trim() : h.Request.Headers["X-Api-Key"].ToString().Trim();
            if (!token.StartsWith("dk_")) return Results.Json(new { error = "unauthorized", detail = "Send your API key in the X-Api-Key header." }, statusCode: 401);
            var tenant = h.RequestServices.GetRequiredService<ITenantContext>();
            await using var db = await ServiceDeskModule.Open(tenant, h.RequestAborted);
            string scope, name; int id;
            await using (var c = new NpgsqlCommand("select id, scope, name from servicedesk.api_keys where hash = @h and revoked_at is null", db))
            {
                ServiceDeskModule.P(c, "h", Sha(token));
                await using var rd = await c.ExecuteReaderAsync(h.RequestAborted);
                if (!await rd.ReadAsync(h.RequestAborted)) return Results.Json(new { error = "unauthorized", detail = "That API key is not valid or was revoked." }, statusCode: 401);
                id = rd.GetInt32(0); scope = rd.GetString(1); name = rd.GetString(2);
            }
            if (write && scope != "write") return Results.Json(new { error = "forbidden", detail = "This API key can only read." }, statusCode: 403);
            await using (var c = new NpgsqlCommand("update servicedesk.api_keys set last_used_at = now() where id = @i", db)) { ServiceDeskModule.P(c, "i", id); await c.ExecuteNonQueryAsync(h.RequestAborted); }
            h.Items["apikey"] = name;
            return await next(ctx);
        });

        // ================================================================ keys
        Admin(r.MapGet("/api/servicedesk/integration/keys", async (ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct); var o = new List<object>();
            await using var c = new NpgsqlCommand("select id, name, prefix, scope, created_at, created_by, last_used_at, revoked_at from servicedesk.api_keys order by id desc", db);
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) o.Add(new { id = rd.GetInt32(0), name = rd.GetString(1), prefix = rd.GetString(2), scope = rd.GetString(3), createdAt = rd.GetDateTime(4), createdBy = rd.GetString(5), lastUsedAt = rd.IsDBNull(6) ? (DateTime?)null : rd.GetDateTime(6), revokedAt = rd.IsDBNull(7) ? (DateTime?)null : rd.GetDateTime(7) });
            return Results.Ok(new { items = o });
        }));
        Admin(r.MapPost("/api/servicedesk/integration/keys", async (JsonElement b, HttpContext h, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var name = Str(b, "name", 80) ?? ""; if (name == "") return ServiceDeskModule.Bad("bad_input", "Give the key a name, for example the program that will use it.");
            var scope = Str(b, "scope", 10) ?? "read"; if (!Scopes.Contains(scope)) return ServiceDeskModule.Bad("bad_input", "scope must be read or write");
            var token = "dk_" + Token(30); var prefix = token[..9];
            await using var db = await ServiceDeskModule.Open(t, ct);
            await using var c = new NpgsqlCommand("insert into servicedesk.api_keys (name, prefix, hash, scope, created_by) values (@n,@p,@h,@s,@b) returning id", db);
            ServiceDeskModule.P(c, "n", name); ServiceDeskModule.P(c, "p", prefix); ServiceDeskModule.P(c, "h", Sha(token)); ServiceDeskModule.P(c, "s", scope); ServiceDeskModule.P(c, "b", Actor(h));
            var id = (int)(await c.ExecuteScalarAsync(ct))!;
            await audit.WriteAsync("servicedesk.apikey_created", name, scope, ct: ct);
            return Results.Ok(new { id, name, scope, key = token, note = "Copy the key now. It cannot be shown again." });
        }));
        Admin(r.MapDelete("/api/servicedesk/integration/keys/{id:int}", async (int id, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            await using var c = new NpgsqlCommand("update servicedesk.api_keys set revoked_at = now() where id = @i and revoked_at is null", db); ServiceDeskModule.P(c, "i", id);
            if (await c.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            await audit.WriteAsync("servicedesk.apikey_revoked", id.ToString(), "", ct: ct);
            return Results.NoContent();
        }));

        // ================================================================ webhooks
        Admin(r.MapGet("/api/servicedesk/integration/webhooks", async (ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct); var o = new List<object>();
            await using var c = new NpgsqlCommand(@"select h.id, h.url, h.events, h.active, h.created_at,
                (select count(*) from servicedesk.webhook_deliveries d where d.hook_id = h.id and d.status = 'failed'),
                (select max(sent_at) from servicedesk.webhook_deliveries d where d.hook_id = h.id and d.status = 'sent') from servicedesk.webhooks h order by h.id", db);
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) o.Add(new { id = rd.GetInt32(0), url = rd.GetString(1), events = rd.GetString(2), active = rd.GetBoolean(3), createdAt = rd.GetDateTime(4), failed = rd.GetInt64(5), lastSentAt = rd.IsDBNull(6) ? (DateTime?)null : rd.GetDateTime(6) });
            return Results.Ok(new { items = o, events = WebhookJob.KnownEvents });
        }));
        Admin(r.MapPost("/api/servicedesk/integration/webhooks", async (JsonElement b, HttpContext h, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var (url, events, err) = ReadHook(b); if (err != null) return ServiceDeskModule.Bad("bad_input", err);
            var secret = "whsec_" + Token(24);
            await using var db = await ServiceDeskModule.Open(t, ct);
            await using var c = new NpgsqlCommand("insert into servicedesk.webhooks (url, secret, events, created_by) values (@u,@s,@e,@b) returning id", db);
            ServiceDeskModule.P(c, "u", url); ServiceDeskModule.P(c, "s", secret); ServiceDeskModule.P(c, "e", events); ServiceDeskModule.P(c, "b", Actor(h));
            var id = (int)(await c.ExecuteScalarAsync(ct))!;
            await audit.WriteAsync("servicedesk.webhook_created", url, events, ct: ct);
            return Results.Ok(new { id, secret, note = "Copy the secret now. The receiver uses it to check each message's signature." });
        }));
        Admin(r.MapPut("/api/servicedesk/integration/webhooks/{id:int}", async (int id, JsonElement b, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var (url, events, err) = ReadHook(b); if (err != null) return ServiceDeskModule.Bad("bad_input", err);
            var active = !(b.TryGetProperty("active", out var av) && av.ValueKind == JsonValueKind.False);
            await using var db = await ServiceDeskModule.Open(t, ct);
            await using var c = new NpgsqlCommand("update servicedesk.webhooks set url = @u, events = @e, active = @a where id = @i", db);
            ServiceDeskModule.P(c, "u", url); ServiceDeskModule.P(c, "e", events); ServiceDeskModule.P(c, "a", active); ServiceDeskModule.P(c, "i", id);
            if (await c.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            await audit.WriteAsync("servicedesk.webhook_changed", id.ToString(), url, ct: ct);
            return Results.Ok(new { ok = true });
        }));
        Admin(r.MapDelete("/api/servicedesk/integration/webhooks/{id:int}", async (int id, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            await using var c = new NpgsqlCommand("delete from servicedesk.webhooks where id = @i", db); ServiceDeskModule.P(c, "i", id);
            if (await c.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            await audit.WriteAsync("servicedesk.webhook_removed", id.ToString(), "", ct: ct);
            return Results.NoContent();
        }));
        Admin(r.MapPost("/api/servicedesk/integration/webhooks/{id:int}/test", async (int id, ITenantContext t, IJobQueue jobs, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var payload = JsonSerializer.Serialize(new { id = "evt_test", @event = "ping", at = DateTime.UtcNow, message = "This is a test from DeskAsk." });
            await using var c = new NpgsqlCommand("insert into servicedesk.webhook_deliveries (hook_id, event_id, kind, payload) select id, 0, 'ping', @p from servicedesk.webhooks where id = @i", db);
            ServiceDeskModule.P(c, "p", payload); ServiceDeskModule.P(c, "i", id);
            if (await c.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            await jobs.EnqueueAsync(WebhookJob.JobType, "{}", null, "servicedesk.webhooks", ct);
            return Results.Ok(new { ok = true });
        }));
        Admin(r.MapGet("/api/servicedesk/integration/webhooks/{id:int}/deliveries", async (int id, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct); var o = new List<object>();
            await using var c = new NpgsqlCommand("select id, kind, status, attempts, http_code, error, created_at, sent_at, next_at from servicedesk.webhook_deliveries where hook_id = @i order by id desc limit 50", db); ServiceDeskModule.P(c, "i", id);
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) o.Add(new { id = rd.GetInt64(0), kind = rd.GetString(1), status = rd.GetString(2), attempts = rd.GetInt32(3), httpCode = rd.IsDBNull(4) ? (int?)null : rd.GetInt32(4), error = rd.GetString(5), createdAt = rd.GetDateTime(6), sentAt = rd.IsDBNull(7) ? (DateTime?)null : rd.GetDateTime(7), nextAt = rd.GetDateTime(8) });
            return Results.Ok(new { items = o });
        }));
        Admin(r.MapPost("/api/servicedesk/integration/deliveries/{id:long}/retry", async (long id, ITenantContext t, IJobQueue jobs, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            await using var c = new NpgsqlCommand("update servicedesk.webhook_deliveries set status = 'pending', attempts = 0, next_at = now(), error = '' where id = @i and status = 'failed'", db); ServiceDeskModule.P(c, "i", id);
            if (await c.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            await jobs.EnqueueAsync(WebhookJob.JobType, "{}", null, "servicedesk.webhooks", ct);
            return Results.Ok(new { ok = true });
        }));

        // ================================================================ /api/v1 (API key)
        const string cols = "id, number, subject, description, status, priority, category, company_id, company_name, requester_name, requester_email, assignee_name, asset_id, asset_name, source, created_at, updated_at, resolved_at, closed_at, custom::text";
        static object Row(NpgsqlDataReader r, bool full) => new
        {
            id = r.GetInt64(0), number = r.GetString(1), subject = r.GetString(2), description = full ? r.GetString(3) : null, status = r.GetString(4), priority = r.GetString(5), category = r.GetString(6),
            companyId = r.IsDBNull(7) ? (int?)null : r.GetInt32(7), company = r.GetString(8), requester = r.GetString(9), requesterEmail = r.GetString(10), assignee = r.GetString(11),
            assetId = r.IsDBNull(12) ? (long?)null : r.GetInt64(12), asset = r.GetString(13), source = r.GetString(14),
            createdAt = r.GetDateTime(15), updatedAt = r.GetDateTime(16), resolvedAt = r.IsDBNull(17) ? (DateTime?)null : r.GetDateTime(17), closedAt = r.IsDBNull(18) ? (DateTime?)null : r.GetDateTime(18),
            custom = System.Text.Json.JsonDocument.Parse(r.GetString(19)).RootElement.Clone(),
        };
        static void Match(NpgsqlCommand c, string idOrNumber)
        {
            if (long.TryParse(idOrNumber, out var n)) { ServiceDeskModule.P(c, "i", n); c.CommandText += " where id = @i"; }
            else { ServiceDeskModule.P(c, "n", idOrNumber.ToUpperInvariant()); c.CommandText += " where number = @n"; }
        }

        Keyed(r.MapGet("/api/v1/tickets", async (HttpContext h, ITenantContext t, CancellationToken ct) =>
        {
            var q = h.Request.Query; await using var db = await ServiceDeskModule.Open(t, ct);
            await using var c = new NpgsqlCommand { Connection = db }; var where = new List<string> { "true" };
            if (q["status"].ToString() is { Length: > 0 } st) { if (st == "active") where.Add("status not in ('resolved','closed')"); else { var l = st.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(Statuses.Contains).ToArray(); if (l.Length == 0) return ServiceDeskModule.Bad("bad_status", "status must be active or a list of " + string.Join(", ", Statuses)); ServiceDeskModule.P(c, "st", l); where.Add("status = any(@st)"); } }
            if (int.TryParse(q["companyId"], out var co)) { ServiceDeskModule.P(c, "co", co); where.Add("company_id = @co"); }
            if (long.TryParse(q["assetId"], out var asset)) { ServiceDeskModule.P(c, "as", asset); where.Add("asset_id = @as"); }
            if (DateTime.TryParse(q["updatedSince"], null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var since)) { ServiceDeskModule.P(c, "since", since); where.Add("updated_at >= @since"); }
            var take = Math.Clamp(int.TryParse(q["take"], out var tk) ? tk : 50, 1, 200); var skip = Math.Max(0, int.TryParse(q["skip"], out var sk) ? sk : 0);
            c.CommandText = $"select count(*) from servicedesk.tickets where {string.Join(" and ", where)}"; var total = (long)(await c.ExecuteScalarAsync(ct))!;
            c.CommandText = $"select {cols} from servicedesk.tickets where {string.Join(" and ", where)} order by id desc limit {take} offset {skip}";
            var items = new List<object>(); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) items.Add(Row(rd, false));
            return Results.Ok(new { total, items });
        }), false);
        Keyed(r.MapGet("/api/v1/tickets/{idOrNumber}", async (string idOrNumber, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            await using var c = new NpgsqlCommand($"select {cols} from servicedesk.tickets", db); Match(c, idOrNumber);
            object? ticket; long id;
            await using (var rd = await c.ExecuteReaderAsync(ct)) { if (!await rd.ReadAsync(ct)) return Results.NotFound(); id = rd.GetInt64(0); ticket = Row(rd, true); }
            var comments = new List<object>();
            await using var cc = new NpgsqlCommand("select at, author_name, kind, body from servicedesk.comments where ticket_id = @i and kind <> 'system' order by id", db); ServiceDeskModule.P(cc, "i", id);
            await using var cr = await cc.ExecuteReaderAsync(ct);
            while (await cr.ReadAsync(ct)) comments.Add(new { at = cr.GetDateTime(0), author = cr.GetString(1), internalNote = cr.GetString(2) == "internal", body = cr.GetString(3) });
            return Results.Ok(new { ticket, comments });
        }), false);
        Keyed(r.MapPost("/api/v1/tickets", async (JsonElement b, HttpContext h, ITenantContext t, ISettings s, ICompanyDirectory companies, IAssetDirectory assets, CancellationToken ct) =>
        {
            var subject = Str(b, "subject", 200) ?? ""; if (subject == "") return ServiceDeskModule.Bad("bad_input", "subject is needed");
            var priority = Str(b, "priority", 20) ?? "medium"; if (priority == "") priority = "medium"; if (!Prios.Contains(priority)) return ServiceDeskModule.Bad("bad_input", "priority must be one of " + string.Join(", ", Prios));
            int? company = null; var companyName = "";
            if (b.TryGetProperty("companyId", out var cv) && cv.ValueKind != JsonValueKind.Null)
            {
                if (cv.ValueKind != JsonValueKind.Number || !cv.TryGetInt32(out var cid)) return ServiceDeskModule.Bad("bad_input", "companyId must be a number");
                var co = await companies.GetAsync(cid, ct); if (co == null) return ServiceDeskModule.Bad("bad_input", "companyId " + cid + " does not exist"); company = cid; companyName = co.Name;
            }
            long? assetId = null; var assetName = "";
            if (b.TryGetProperty("assetId", out var av) && av.ValueKind != JsonValueKind.Null)
            {
                if (av.ValueKind != JsonValueKind.Number || !av.TryGetInt64(out var aid)) return ServiceDeskModule.Bad("bad_input", "assetId must be a number");
                var a = await assets.GetAsync(aid, ct); if (a == null) return ServiceDeskModule.Bad("bad_input", "assetId " + aid + " does not exist"); assetId = aid; assetName = a.Name; if (company == null && a.CompanyId is int ac) { company = ac; companyName = (await companies.GetAsync(ac, ct))?.Name ?? ""; }
            }
            var sla = await SlaConfig.LoadAsync(s, ct); var by = "API: " + (h.Items["apikey"] as string ?? "key");
            await using var db = await ServiceDeskModule.Open(t, ct);
            Dictionary<string, object?> custom;
            try
            {
                var defs = await CustomFields.LoadAsync(db, true, ct);
                custom = b.TryGetProperty("custom", out var cuv) ? CustomFields.Check(cuv, defs, true) : new();
                CustomFields.RequireAll(custom, defs, true);
            }
            catch (FormatException e) { return ServiceDeskModule.Bad("bad_custom", e.Message); }
            await using var c = new NpgsqlCommand(@"insert into servicedesk.tickets
                (subject, description, priority, category, company_id, company_name, requester_name, requester_email, asset_id, asset_name, source, created_by, custom, respond_due, resolve_due)
                values (@s,@d,@p,@cat,@co,@con,@rn,@re,@ai,@an,'api',@by,@cu::jsonb, now() + make_interval(secs => @rp * 3600), now() + make_interval(secs => @rs * 3600)) returning id, number", db);
            ServiceDeskModule.P(c, "s", subject); ServiceDeskModule.P(c, "d", Str(b, "description", 20000) ?? ""); ServiceDeskModule.P(c, "p", priority); ServiceDeskModule.P(c, "cat", Str(b, "category", 60) ?? "");
            ServiceDeskModule.P(c, "co", company); ServiceDeskModule.P(c, "con", companyName); ServiceDeskModule.P(c, "rn", Str(b, "requesterName", 200) ?? ""); ServiceDeskModule.P(c, "re", Str(b, "requesterEmail", 320) ?? "");
            ServiceDeskModule.P(c, "ai", assetId); ServiceDeskModule.P(c, "an", assetName); ServiceDeskModule.P(c, "by", by); ServiceDeskModule.P(c, "rp", sla.Respond[priority]); ServiceDeskModule.P(c, "rs", sla.Resolve[priority]); ServiceDeskModule.P(c, "cu", CustomFields.ToJson(custom));
            long id; string number;
            await using (var rd = await c.ExecuteReaderAsync(ct)) { await rd.ReadAsync(ct); id = rd.GetInt64(0); number = rd.GetString(1); }
            await ServiceDeskModule.Event(db, id, "ticket.created", new { number, priority, company, api = true }, ct);
            return Results.Ok(new { id, number });
        }), true);
        Keyed(r.MapPost("/api/v1/tickets/{idOrNumber}/comments", async (string idOrNumber, JsonElement b, HttpContext h, ITenantContext t, CancellationToken ct) =>
        {
            var body = Str(b, "body", 20000) ?? ""; if (body == "") return ServiceDeskModule.Bad("bad_input", "body is needed");
            var note = b.TryGetProperty("internal", out var iv) && iv.ValueKind == JsonValueKind.True;
            await using var db = await ServiceDeskModule.Open(t, ct);
            await using var f = new NpgsqlCommand("select id from servicedesk.tickets", db); Match(f, idOrNumber);
            if (await f.ExecuteScalarAsync(ct) is not long id) return Results.NotFound();
            var by = "API: " + (h.Items["apikey"] as string ?? "key");
            await using (var c = new NpgsqlCommand("insert into servicedesk.comments (ticket_id, author_name, kind, body) values (@t,@a,@k,@b)", db))
            { ServiceDeskModule.P(c, "t", id); ServiceDeskModule.P(c, "a", by); ServiceDeskModule.P(c, "k", note ? "internal" : "public"); ServiceDeskModule.P(c, "b", body); await c.ExecuteNonQueryAsync(ct); }
            await using (var c = new NpgsqlCommand("update servicedesk.tickets set updated_at = now() where id = @t", db)) { ServiceDeskModule.P(c, "t", id); await c.ExecuteNonQueryAsync(ct); }
            await ServiceDeskModule.Event(db, id, note ? "ticket.note" : "ticket.comment", new { by, customer = false }, ct);
            return Results.Ok(new { ok = true });
        }), true);

        // ---- changes and problems (the support team's records; a key with write access may also raise a draft change or a problem)
        static void MatchBy(NpgsqlCommand c, string idOrNumber, string prefix)
        {
            if (long.TryParse(idOrNumber, out var n)) { ServiceDeskModule.P(c, "i", n); c.CommandText += " where id = @i"; }
            else { ServiceDeskModule.P(c, "n", idOrNumber.ToUpperInvariant().StartsWith(prefix) ? idOrNumber.ToUpperInvariant() : prefix + idOrNumber); c.CommandText += " where number = @n"; }
        }
        static string? Pick(JsonElement b, string n, string[] allowed, string fallback, out string? error)
        {
            error = null; var v = Str(b, n, 30); if (string.IsNullOrEmpty(v)) return fallback;
            if (!allowed.Contains(v)) { error = n + " must be one of " + string.Join(", ", allowed); return null; }
            return v;
        }
        const string chgCols = "id, number, title, description, type, status, risk, impact, reason, plan, rollback, impact_analysis, outcome, review_notes, closure_code, company_id, company_name, owner_name, start_at, end_at, created_at, updated_at, closed_at";
        static object ChangeRow(NpgsqlDataReader r, bool full) => new
        {
            id = r.GetInt64(0), number = r.GetString(1), title = r.GetString(2), description = full ? r.GetString(3) : null, type = r.GetString(4), status = r.GetString(5), stage = ChangesApi.StageOf(r.GetString(5)),
            risk = r.GetString(6), impact = r.GetString(7), reason = full ? r.GetString(8) : null, plan = full ? r.GetString(9) : null, rollback = full ? r.GetString(10) : null, impactAnalysis = full ? r.GetString(11) : null,
            outcome = full ? r.GetString(12) : null, reviewNotes = full ? r.GetString(13) : null, closureCode = r.GetString(14), companyId = r.IsDBNull(15) ? (int?)null : r.GetInt32(15), company = r.GetString(16), owner = r.GetString(17),
            startAt = r.IsDBNull(18) ? (DateTime?)null : r.GetDateTime(18), endAt = r.IsDBNull(19) ? (DateTime?)null : r.GetDateTime(19), createdAt = r.GetDateTime(20), updatedAt = r.GetDateTime(21), closedAt = r.IsDBNull(22) ? (DateTime?)null : r.GetDateTime(22),
        };
        Keyed(r.MapGet("/api/v1/changes", async (HttpContext h, ITenantContext t, CancellationToken ct) =>
        {
            var q = h.Request.Query; await using var db = await ServiceDeskModule.Open(t, ct);
            await using var c = new NpgsqlCommand { Connection = db }; var where = new List<string> { "true" };
            if (q["status"].ToString() is { Length: > 0 } st) { if (st == "active") where.Add("status not in ('closed','cancelled','rejected')"); else { ServiceDeskModule.P(c, "st", st.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); where.Add("status = any(@st)"); } }
            if (q["type"].ToString() is { Length: > 0 } ty) { ServiceDeskModule.P(c, "ty", ty); where.Add("type = @ty"); }
            if (int.TryParse(q["companyId"], out var co)) { ServiceDeskModule.P(c, "co", co); where.Add("company_id = @co"); }
            var take = Math.Clamp(int.TryParse(q["take"], out var tk) ? tk : 50, 1, 200); var skip = Math.Max(0, int.TryParse(q["skip"], out var sk) ? sk : 0);
            c.CommandText = $"select count(*) from servicedesk.changes where {string.Join(" and ", where)}"; var total = (long)(await c.ExecuteScalarAsync(ct))!;
            c.CommandText = $"select {chgCols} from servicedesk.changes where {string.Join(" and ", where)} order by id desc limit {take} offset {skip}";
            var items = new List<object>(); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) items.Add(ChangeRow(rd, false));
            return Results.Ok(new { total, items });
        }), false);
        Keyed(r.MapGet("/api/v1/changes/{idOrNumber}", async (string idOrNumber, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            await using var c = new NpgsqlCommand($"select {chgCols} from servicedesk.changes", db); MatchBy(c, idOrNumber, "CHG-");
            object change; long id;
            await using (var rd = await c.ExecuteReaderAsync(ct)) { if (!await rd.ReadAsync(ct)) return Results.NotFound(); id = rd.GetInt64(0); change = ChangeRow(rd, true); }
            var tasks = new List<object>();
            await using var tc = new NpgsqlCommand("select stage, title, assignee_name, done from servicedesk.change_tasks where change_id = @i order by stage, position, id", db); ServiceDeskModule.P(tc, "i", id);
            await using var tr = await tc.ExecuteReaderAsync(ct);
            while (await tr.ReadAsync(ct)) tasks.Add(new { stage = tr.GetString(0), title = tr.GetString(1), assignee = tr.GetString(2), done = tr.GetBoolean(3) });
            return Results.Ok(new { change, tasks });
        }), false);
        Keyed(r.MapPost("/api/v1/changes", async (JsonElement b, HttpContext h, ITenantContext t, ISettings s, ICompanyDirectory companies, CancellationToken ct) =>
        {
            var title = Str(b, "title", 200) ?? ""; if (title == "") return ServiceDeskModule.Bad("bad_input", "title is needed");
            var type = Pick(b, "type", new[] { "normal", "standard", "emergency" }, "normal", out var e1); if (e1 != null) return ServiceDeskModule.Bad("bad_input", e1);
            var risk = Pick(b, "risk", new[] { "low", "medium", "high" }, "low", out var e2); if (e2 != null) return ServiceDeskModule.Bad("bad_input", e2);
            var impact = Pick(b, "impact", new[] { "low", "medium", "high" }, "low", out var e3); if (e3 != null) return ServiceDeskModule.Bad("bad_input", e3);
            int? company = null; var companyName = "";
            if (b.TryGetProperty("companyId", out var cv) && cv.ValueKind != JsonValueKind.Null)
            {
                if (cv.ValueKind != JsonValueKind.Number || !cv.TryGetInt32(out var cid)) return ServiceDeskModule.Bad("bad_input", "companyId must be a number");
                var co = await companies.GetAsync(cid, ct); if (co == null) return ServiceDeskModule.Bad("bad_input", "companyId " + cid + " does not exist"); company = cid; companyName = co.Name;
            }
            var by = "API: " + (h.Items["apikey"] as string ?? "key");
            await using var db = await ServiceDeskModule.Open(t, ct);
            await using var c = new NpgsqlCommand(@"insert into servicedesk.changes (title, description, type, risk, impact, reason, plan, rollback, impact_analysis, company_id, company_name, owner_name, quorum, created_by)
                values (@t,@d,@ty,@rk,@im,@re,@pl,@rb,@ia,@ci,@cn,@on,@q,@cb) returning id, number", db);
            ServiceDeskModule.P(c, "t", title); ServiceDeskModule.P(c, "d", Str(b, "description", 8000) ?? ""); ServiceDeskModule.P(c, "ty", type); ServiceDeskModule.P(c, "rk", risk); ServiceDeskModule.P(c, "im", impact);
            ServiceDeskModule.P(c, "re", Str(b, "reason", 4000) ?? ""); ServiceDeskModule.P(c, "pl", Str(b, "plan", 8000) ?? ""); ServiceDeskModule.P(c, "rb", Str(b, "rollback", 8000) ?? ""); ServiceDeskModule.P(c, "ia", Str(b, "impactAnalysis", 8000) ?? "");
            ServiceDeskModule.P(c, "ci", company); ServiceDeskModule.P(c, "cn", companyName); ServiceDeskModule.P(c, "on", by); ServiceDeskModule.P(c, "q", await ChangesApi.Quorum(s, type!, ct)); ServiceDeskModule.P(c, "cb", by);
            long id; string number;
            await using (var rd = await c.ExecuteReaderAsync(ct)) { await rd.ReadAsync(ct); id = rd.GetInt64(0); number = rd.GetString(1); }
            return Results.Ok(new { id, number, status = "draft" });
        }), true);

        const string prbCols = "id, number, title, description, status, priority, category, root_cause, workaround, resolution, owner_name, created_at, updated_at";
        static object ProblemRow(NpgsqlDataReader r, bool full) => new
        {
            id = r.GetInt64(0), number = r.GetString(1), title = r.GetString(2), description = full ? r.GetString(3) : null, status = r.GetString(4), priority = r.GetString(5), category = r.GetString(6),
            rootCause = full ? r.GetString(7) : null, workaround = full ? r.GetString(8) : null, resolution = full ? r.GetString(9) : null, owner = r.GetString(10), createdAt = r.GetDateTime(11), updatedAt = r.GetDateTime(12),
        };
        Keyed(r.MapGet("/api/v1/problems", async (HttpContext h, ITenantContext t, CancellationToken ct) =>
        {
            var q = h.Request.Query; await using var db = await ServiceDeskModule.Open(t, ct);
            await using var c = new NpgsqlCommand { Connection = db }; var where = new List<string> { "true" };
            if (q["status"].ToString() is { Length: > 0 } st) { if (st == "active") where.Add("status not in ('resolved','closed')"); else { ServiceDeskModule.P(c, "st", st.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); where.Add("status = any(@st)"); } }
            var take = Math.Clamp(int.TryParse(q["take"], out var tk) ? tk : 50, 1, 200); var skip = Math.Max(0, int.TryParse(q["skip"], out var sk) ? sk : 0);
            c.CommandText = $"select count(*) from servicedesk.problems where {string.Join(" and ", where)}"; var total = (long)(await c.ExecuteScalarAsync(ct))!;
            c.CommandText = $"select {prbCols} from servicedesk.problems where {string.Join(" and ", where)} order by id desc limit {take} offset {skip}";
            var items = new List<object>(); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) items.Add(ProblemRow(rd, false));
            return Results.Ok(new { total, items });
        }), false);
        Keyed(r.MapGet("/api/v1/problems/{idOrNumber}", async (string idOrNumber, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            await using var c = new NpgsqlCommand($"select {prbCols} from servicedesk.problems", db); MatchBy(c, idOrNumber, "PRB-");
            await using var rd = await c.ExecuteReaderAsync(ct); if (!await rd.ReadAsync(ct)) return Results.NotFound();
            return Results.Ok(new { problem = ProblemRow(rd, true) });
        }), false);
        Keyed(r.MapPost("/api/v1/problems", async (JsonElement b, HttpContext h, ITenantContext t, CancellationToken ct) =>
        {
            var title = Str(b, "title", 200) ?? ""; if (title == "") return ServiceDeskModule.Bad("bad_input", "title is needed");
            var prio = Pick(b, "priority", Prios, "medium", out var e1); if (e1 != null) return ServiceDeskModule.Bad("bad_input", e1);
            var by = "API: " + (h.Items["apikey"] as string ?? "key");
            await using var db = await ServiceDeskModule.Open(t, ct);
            await using var c = new NpgsqlCommand("insert into servicedesk.problems (title, description, priority, category, owner_name, created_by) values (@t,@d,@p,@c,@on,@cb) returning id, number", db);
            ServiceDeskModule.P(c, "t", title); ServiceDeskModule.P(c, "d", Str(b, "description", 8000) ?? ""); ServiceDeskModule.P(c, "p", prio); ServiceDeskModule.P(c, "c", Str(b, "category", 100) ?? ""); ServiceDeskModule.P(c, "on", by); ServiceDeskModule.P(c, "cb", by);
            await using var rd = await c.ExecuteReaderAsync(ct); await rd.ReadAsync(ct);
            return Results.Ok(new { id = rd.GetInt64(0), number = rd.GetString(1), status = "open" });
        }), true);
    }

    static (string url, string events, string? err) ReadHook(JsonElement b)
    {
        var url = Str(b, "url", 500) ?? "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)) return ("", "", "Give the full web address the messages go to, starting with https://");
        var raw = Str(b, "events", 400) ?? "*"; if (raw == "") raw = "*";
        var list = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var e in list) if (e != "*" && !WebhookJob.KnownEvents.Contains(e)) return ("", "", "Unknown event " + e + ". Use * or a list of " + string.Join(", ", WebhookJob.KnownEvents));
        return (url, string.Join(",", list), null);
    }
}

/// <summary>Turns new ticket events into webhook messages and sends the ones that are due. A message that fails is tried again a few times, a little later each time.</summary>
public sealed class WebhookJob : IJobHandler
{
    public const string JobType = "servicedesk.webhooks";
    public string Type => JobType;
    public static readonly string[] KnownEvents = { "ticket.created", "ticket.updated", "ticket.status", "ticket.comment", "ticket.note" };
    const int MaxAttempts = 6;
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public async Task RunAsync(JobContext job, CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(job.Tenant.ConnectionString); await db.OpenAsync(ct);
        await Collect(db, ct);
        await Send(db, ct);
    }

    static bool Wants(string events, string kind) => events == "*" || events.Split(',').Contains(kind);

    static async Task Collect(NpgsqlConnection db, CancellationToken ct)
    {
        await using (var c = new NpgsqlCommand("insert into servicedesk.webhook_state (id, last_event) select 1, coalesce(max(id), 0) from servicedesk.events on conflict (id) do nothing", db)) await c.ExecuteNonQueryAsync(ct);
        long last;
        await using (var c = new NpgsqlCommand("select last_event from servicedesk.webhook_state where id = 1", db)) last = (long)(await c.ExecuteScalarAsync(ct))!;
        var hooks = new List<(int id, string events)>();
        await using (var c = new NpgsqlCommand("select id, events from servicedesk.webhooks where active", db))
        { await using var rd = await c.ExecuteReaderAsync(ct); while (await rd.ReadAsync(ct)) hooks.Add((rd.GetInt32(0), rd.GetString(1))); }
        var events = new List<(long id, string kind, string payload)>();
        await using (var c = new NpgsqlCommand(@"select e.id, e.kind, e.at, e.data, t.id, t.number, t.subject, t.status, t.priority, t.category, t.company_name, t.requester_name, t.requester_email, t.assignee_name
            from servicedesk.events e left join servicedesk.tickets t on t.id = e.ticket_id where e.id > @l order by e.id limit 500", db))
        {
            ServiceDeskModule.P(c, "l", last); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var data = JsonDocument.Parse(rd.GetString(3)).RootElement.Clone();
                var ticket = rd.IsDBNull(4) ? null : new { id = rd.GetInt64(4), number = rd.GetString(5), subject = rd.GetString(6), status = rd.GetString(7), priority = rd.GetString(8), category = rd.GetString(9), company = rd.GetString(10), requester = rd.GetString(11), requesterEmail = rd.GetString(12), assignee = rd.GetString(13) };
                events.Add((rd.GetInt64(0), rd.GetString(1), JsonSerializer.Serialize(new { id = "evt_" + rd.GetInt64(0), @event = rd.GetString(1), at = rd.GetDateTime(2), ticket, data })));
            }
        }
        if (events.Count == 0) return;
        foreach (var e in events) foreach (var h in hooks)
            if (Wants(h.events, e.kind))
            {
                await using var c = new NpgsqlCommand("insert into servicedesk.webhook_deliveries (hook_id, event_id, kind, payload) values (@h,@e,@k,@p)", db);
                ServiceDeskModule.P(c, "h", h.id); ServiceDeskModule.P(c, "e", e.id); ServiceDeskModule.P(c, "k", e.kind); ServiceDeskModule.P(c, "p", e.payload); await c.ExecuteNonQueryAsync(ct);
            }
        await using (var c = new NpgsqlCommand("update servicedesk.webhook_state set last_event = @l where id = 1", db)) { ServiceDeskModule.P(c, "l", events[^1].id); await c.ExecuteNonQueryAsync(ct); }
    }

    static async Task Send(NpgsqlConnection db, CancellationToken ct)
    {
        var due = new List<(long id, string url, string secret, string kind, string payload, int attempts)>();
        await using (var c = new NpgsqlCommand(@"select d.id, h.url, h.secret, d.kind, d.payload, d.attempts from servicedesk.webhook_deliveries d join servicedesk.webhooks h on h.id = d.hook_id
            where d.status = 'pending' and d.next_at <= now() order by d.id limit 50", db))
        { await using var rd = await c.ExecuteReaderAsync(ct); while (await rd.ReadAsync(ct)) due.Add((rd.GetInt64(0), rd.GetString(1), rd.GetString(2), rd.GetString(3), rd.GetString(4), rd.GetInt32(5))); }
        foreach (var d in due)
        {
            int? code = null; string error = "";
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, d.url) { Content = new StringContent(d.payload, Encoding.UTF8, "application/json") };
                var sig = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(d.secret), Encoding.UTF8.GetBytes(d.payload))).ToLowerInvariant();
                req.Headers.Add("X-DeskAsk-Event", d.kind); req.Headers.Add("X-DeskAsk-Delivery", d.id.ToString()); req.Headers.Add("X-DeskAsk-Signature", "sha256=" + sig);
                using var res = await Http.SendAsync(req, ct); code = (int)res.StatusCode;
                if (!res.IsSuccessStatusCode) error = "The receiver answered " + code + ".";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException) { error = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message; }
            var attempts = d.attempts + 1; var ok = error == "";
            var status = ok ? "sent" : attempts >= MaxAttempts ? "failed" : "pending";
            await using var u = new NpgsqlCommand("update servicedesk.webhook_deliveries set status = @s, attempts = @a, http_code = @h, error = @e, sent_at = case when @ok then now() else sent_at end, next_at = now() + make_interval(mins => @m) where id = @i", db);
            ServiceDeskModule.P(u, "s", status); ServiceDeskModule.P(u, "a", attempts); ServiceDeskModule.P(u, "h", code); ServiceDeskModule.P(u, "e", error); ServiceDeskModule.P(u, "ok", ok); ServiceDeskModule.P(u, "m", attempts * attempts); ServiceDeskModule.P(u, "i", d.id);
            await u.ExecuteNonQueryAsync(ct);
        }
    }
}
