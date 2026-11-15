using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace DeskAsk.Platform.AdManager;

/// <summary>
/// New-account requests (internal, external, service) and the approvals that risky changes wait for. Someone with the approve right agrees
/// or declines; the person who asked may not agree to their own request (administrators excepted). Agreeing runs the change at once.
/// </summary>
internal static class RequestsApi
{
    static JsonElement Json(object o) => JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement.Clone();

    /// <summary>The domains where the person holds the approve right somewhere (all of them for an administrator).</summary>
    static async Task<HashSet<int>> ApproverOf(NpgsqlConnection db, IAccess access, ClaimsPrincipal u, CancellationToken ct)
    {
        var set = new HashSet<int>();
        var ids = new List<int>();
        await using (var c = new NpgsqlCommand("select id from adsuite.domains", db)) await using (var rd = await c.ExecuteReaderAsync(ct)) while (await rd.ReadAsync(ct)) ids.Add(rd.GetInt32(0));
        foreach (var id in ids) { var s = await Perms.ScopeAsync(db, access, u, id, "approve", ct); if (s == null || s.Count > 0) set.Add(id); }
        return set;
    }

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        // ---------------------------------------------------------------- account requests
        AdModule.Use(r.MapPost("/api/adsuite/requests", async (JsonElement body, ClaimsPrincipal u, ITenantContext t, IDataProtectionProvider dp, IAccess access, IAuditLog audit, CancellationToken ct) =>
        {
            var domainId = AdModule.Int(body, "domainId") ?? 0;
            string S(string n) => (AdModule.Str(body, n) ?? "").Trim();
            var kind = S("kind"); var ou = S("ou"); var sam = S("sam");
            if (kind is not ("internal" or "external" or "service")) return AdModule.Bad("bad_input", "Choose internal, external or service.");
            await using var db = await AdModule.Open(t, ct);
            var d = await Dir.LoadAsync(db, dp, t, domainId, ct); if (d == null) return AdModule.Bad("bad_input", "Choose a domain.");
            if (!Dir.ValidDn(ou) || !Dir.IsUnder(ou, d.BaseDn)) return AdModule.Bad("bad_input", "Choose the OU the account goes in.");
            if (!await Perms.CanAsync(db, access, u, domainId, ou, "read", ct)) return AdModule.Forbid("You may not ask for accounts in this OU.");
            if (!Dir.Sam.IsMatch(sam)) return AdModule.Bad("bad_input", "The sign-in name has letters, digits and . _ - only, up to 20 characters.");
            if (kind != "service" && S("first") == "" && S("last") == "") return AdModule.Bad("bad_input", "Give the person's name.");
            if (kind == "service" && S("notes").Length < 5) return AdModule.Bad("bad_input", "Say what the service account is for.");
            DateOnly? exp = null;
            if (S("expires") != "")
            {
                if (!DateOnly.TryParseExact(S("expires"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) return AdModule.Bad("bad_input", "The date is year-month-day.");
                exp = day;
            }
            if (kind == "external" && exp == null) return AdModule.Bad("bad_input", "An external account needs an expiry date.");
            var groups = new List<string>();
            if (body.TryGetProperty("groups", out var gs) && gs.ValueKind == JsonValueKind.Array) foreach (var g in gs.EnumerateArray()) if (g.ValueKind == JsonValueKind.String && Dir.ValidDn(g.GetString()) && Dir.IsUnder(g.GetString()!, d.BaseDn)) groups.Add(g.GetString()!);
            var mgr = S("manager"); if (mgr != "" && (!Dir.ValidDn(mgr) || !Dir.IsUnder(mgr, d.BaseDn))) return AdModule.Bad("bad_input", "The manager is a person in this domain.");
            await using var c = new NpgsqlCommand(@"insert into adsuite.requests (domain_id, kind, first_name, last_name, display_name, sam, upn, email, ou_dn, title, department, manager_dn, expires_on, groups, notes, requested_by, requested_by_id)
                values (@d,@k,@f,@l,@dn,@s,@up,@em,@ou,@ti,@de,@m,@ex,@g::jsonb,@no,@by,@id) returning id", db);
            AdModule.P(c, "d", domainId); AdModule.P(c, "k", kind); AdModule.P(c, "f", AdModule.Clip(S("first"), 100)); AdModule.P(c, "l", AdModule.Clip(S("last"), 100)); AdModule.P(c, "dn", AdModule.Clip(S("display"), 200));
            AdModule.P(c, "s", sam); AdModule.P(c, "up", AdModule.Clip(S("upn"), 256)); AdModule.P(c, "em", AdModule.Clip(S("email"), 256)); AdModule.P(c, "ou", ou); AdModule.P(c, "ti", AdModule.Clip(S("title"), 200));
            AdModule.P(c, "de", AdModule.Clip(S("department"), 200)); AdModule.P(c, "m", mgr); AdModule.P(c, "ex", exp); AdModule.P(c, "g", JsonSerializer.Serialize(groups));
            AdModule.P(c, "no", AdModule.Clip(S("notes"), 1000)); AdModule.P(c, "by", AdModule.Actor(u)); AdModule.P(c, "id", AdModule.Me(u));
            var id = (int)(await c.ExecuteScalarAsync(ct))!;
            await audit.WriteAsync("adsuite.request.add", $"{d.Name}: {sam}", $"{kind} account asked for", null, ct);
            return Results.Ok(new { id });
        }), key);

        AdModule.Use(r.MapGet("/api/adsuite/requests", async (string? status, ClaimsPrincipal u, ITenantContext t, IAccess access, CancellationToken ct) =>
        {
            await using var db = await AdModule.Open(t, ct);
            var approver = await ApproverOf(db, access, u, ct); var me = AdModule.Me(u); var admin = AdModule.IsAdmin(u);
            await using var c = new NpgsqlCommand(@"select r.id, r.domain_id, d.name, r.kind, r.status, r.first_name, r.last_name, r.display_name, r.sam, r.upn, r.email, r.ou_dn, r.title, r.department, r.manager_dn, r.expires_on, r.groups::text, r.notes,
                r.requested_by, r.requested_by_id, r.decided_by, r.decided_at, r.decision_note, r.result_dn, r.error, r.created_at
                from adsuite.requests r join adsuite.domains d on d.id = r.domain_id where (@s = '' or r.status = @s) order by r.created_at desc limit 500", db);
            AdModule.P(c, "s", status ?? "");
            await using var rd = await c.ExecuteReaderAsync(ct);
            var list = new List<object>();
            while (await rd.ReadAsync(ct))
            {
                var mine = !rd.IsDBNull(19) && rd.GetGuid(19) == me; var canApprove = approver.Contains(rd.GetInt32(1));
                if (!admin && !mine && !canApprove) continue;
                list.Add(new
                {
                    id = rd.GetInt32(0), domainId = rd.GetInt32(1), domain = rd.GetString(2), kind = rd.GetString(3), status = rd.GetString(4), first = rd.GetString(5), last = rd.GetString(6), display = rd.GetString(7),
                    sam = rd.GetString(8), upn = rd.GetString(9), email = rd.GetString(10), ou = rd.GetString(11), title = rd.GetString(12), department = rd.GetString(13), manager = rd.GetString(14),
                    expires = rd.IsDBNull(15) ? null : rd.GetDateTime(15).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), groups = JsonDocument.Parse(rd.GetString(16)).RootElement.Clone(), notes = rd.GetString(17),
                    requestedBy = rd.GetString(18), decidedBy = rd.GetString(20), decidedAt = rd.IsDBNull(21) ? (DateTime?)null : rd.GetDateTime(21), decisionNote = rd.GetString(22), resultDn = rd.GetString(23), error = rd.GetString(24),
                    createdAt = rd.GetDateTime(25), mine, canApprove = canApprove && (admin || !mine)
                });
            }
            return Results.Ok(list);
        }), key);

        AdModule.Use(r.MapPost("/api/adsuite/requests/{id:int}/approve", async (int id, ClaimsPrincipal u, ITenantContext t, IDataProtectionProvider dp, IAccess access, IControllerRelay relay, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await AdModule.Open(t, ct);
            Dictionary<string, object?> q = new(); Guid by; int domainId; string status; string name;
            await using (var c = new NpgsqlCommand(@"select domain_id, kind, status, first_name, last_name, display_name, sam, upn, email, ou_dn, title, department, manager_dn, expires_on, groups::text, requested_by_id from adsuite.requests where id = @i", db))
            {
                AdModule.P(c, "i", id); await using var rd = await c.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) return Results.NotFound();
                domainId = rd.GetInt32(0); status = rd.GetString(2); by = rd.IsDBNull(15) ? Guid.Empty : rd.GetGuid(15); name = rd.GetString(6);
                q["action"] = "create-user"; q["kind"] = rd.GetString(1); q["first"] = rd.GetString(3); q["last"] = rd.GetString(4); q["display"] = rd.GetString(5); q["sam"] = name; q["upn"] = rd.GetString(7); q["email"] = rd.GetString(8);
                q["ou"] = rd.GetString(9); q["title"] = rd.GetString(10); q["department"] = rd.GetString(11); q["manager"] = rd.GetString(12);
                q["expires"] = rd.IsDBNull(13) ? "" : rd.GetDateTime(13).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); q["groups"] = JsonDocument.Parse(rd.GetString(14)).RootElement.Clone();
            }
            if (status != "pending") return AdModule.Bad("not_pending", "This request has already been dealt with.");
            if (by == AdModule.Me(u) && !AdModule.IsAdmin(u)) return AdModule.Forbid("Someone else has to approve your own request.");
            var d = await Dir.LoadAsync(db, dp, t, domainId, ct); if (d == null) return Results.NotFound();
            var o = await Actions.RunAsync(Json(q), d, u, db, access, relay, audit, approved: true, ct);
            await using var up = new NpgsqlCommand(o.Ok
                ? "update adsuite.requests set status = 'created', decided_by = @by, decided_at = now(), result_dn = @dn, error = '' where id = @i"
                : "update adsuite.requests set error = @e where id = @i", db);
            AdModule.P(up, "i", id);
            if (o.Ok) { AdModule.P(up, "by", AdModule.Actor(u)); AdModule.P(up, "dn", Json(o.Body).TryGetProperty("dn", out var dn) && dn.ValueKind == JsonValueKind.String ? dn.GetString() : ""); }
            else AdModule.P(up, "e", AdModule.Clip(o.Error, 500));
            await up.ExecuteNonQueryAsync(ct);
            if (o.Ok) await audit.WriteAsync("adsuite.request.approve", $"{d.Name}: {name}", "account created", null, ct);
            return Results.Json(o.Body, statusCode: o.Status);
        }), key);

        AdModule.Use(r.MapPost("/api/adsuite/requests/{id:int}/reject", async (int id, JsonElement body, ClaimsPrincipal u, ITenantContext t, IAccess access, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await AdModule.Open(t, ct);
            int domainId; string ou, sam;
            await using (var c = new NpgsqlCommand("select domain_id, ou_dn, sam from adsuite.requests where id = @i and status = 'pending'", db))
            {
                AdModule.P(c, "i", id); await using var rd = await c.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) return Results.NotFound();
                domainId = rd.GetInt32(0); ou = rd.GetString(1); sam = rd.GetString(2);
            }
            if (!await Perms.CanAsync(db, access, u, domainId, ou, "approve", ct)) return AdModule.Forbid("You may not decide on requests here.");
            await using var up = new NpgsqlCommand("update adsuite.requests set status = 'rejected', decided_by = @by, decided_at = now(), decision_note = @n where id = @i", db);
            AdModule.P(up, "i", id); AdModule.P(up, "by", AdModule.Actor(u)); AdModule.P(up, "n", AdModule.Clip((AdModule.Str(body, "note") ?? "").Trim(), 500));
            await up.ExecuteNonQueryAsync(ct);
            await audit.WriteAsync("adsuite.request.reject", sam, AdModule.Clip(AdModule.Str(body, "note"), 200), null, ct);
            return Results.Ok(new { ok = true });
        }), key);

        // ---------------------------------------------------------------- approvals of risky changes
        AdModule.Use(r.MapGet("/api/adsuite/approvals", async (string? status, ClaimsPrincipal u, ITenantContext t, IAccess access, CancellationToken ct) =>
        {
            await using var db = await AdModule.Open(t, ct);
            var approver = await ApproverOf(db, access, u, ct); var me = AdModule.Me(u); var admin = AdModule.IsAdmin(u);
            await using var c = new NpgsqlCommand(@"select a.id, a.domain_id, d.name, a.action, a.target_dn, a.target_name, a.reason, a.status, a.requested_by, a.requested_by_id, a.decided_by, a.decided_at, a.decision_note, a.error, a.created_at
                from adsuite.approvals a join adsuite.domains d on d.id = a.domain_id where (@s = '' or a.status = @s) order by a.created_at desc limit 500", db);
            AdModule.P(c, "s", status ?? "");
            await using var rd = await c.ExecuteReaderAsync(ct);
            var list = new List<object>();
            while (await rd.ReadAsync(ct))
            {
                var mine = !rd.IsDBNull(9) && rd.GetGuid(9) == me; var canApprove = approver.Contains(rd.GetInt32(1));
                if (!admin && !mine && !canApprove) continue;
                list.Add(new
                {
                    id = rd.GetInt32(0), domainId = rd.GetInt32(1), domain = rd.GetString(2), action = rd.GetString(3), targetDn = rd.GetString(4), target = rd.GetString(5), reason = rd.GetString(6), status = rd.GetString(7),
                    requestedBy = rd.GetString(8), decidedBy = rd.GetString(10), decidedAt = rd.IsDBNull(11) ? (DateTime?)null : rd.GetDateTime(11), decisionNote = rd.GetString(12), error = rd.GetString(13), createdAt = rd.GetDateTime(14),
                    mine, canApprove = canApprove && (admin || !mine)
                });
            }
            return Results.Ok(list);
        }), key);

        AdModule.Use(r.MapPost("/api/adsuite/approvals/{id:int}/approve", async (int id, ClaimsPrincipal u, ITenantContext t, IDataProtectionProvider dp, IAccess access, IControllerRelay relay, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await AdModule.Open(t, ct);
            int domainId; string status, bodyText, target; Guid by;
            await using (var c = new NpgsqlCommand("select domain_id, status, body::text, requested_by_id, target_dn from adsuite.approvals where id = @i", db))
            {
                AdModule.P(c, "i", id); await using var rd = await c.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) return Results.NotFound();
                domainId = rd.GetInt32(0); status = rd.GetString(1); bodyText = rd.GetString(2); by = rd.IsDBNull(3) ? Guid.Empty : rd.GetGuid(3); target = rd.GetString(4);
            }
            if (status != "pending") return AdModule.Bad("not_pending", "This has already been dealt with.");
            if (by == AdModule.Me(u) && !AdModule.IsAdmin(u)) return AdModule.Forbid("Someone else has to approve your own request.");
            var d = await Dir.LoadAsync(db, dp, t, domainId, ct); if (d == null) return Results.NotFound();
            var body = JsonDocument.Parse(bodyText).RootElement.Clone();
            var o = await Actions.RunAsync(body, d, u, db, access, relay, audit, approved: true, ct);
            if (!o.Ok && o.Status == 403) return Results.Json(o.Body, statusCode: 403);
            var keep = !o.Ok && o.Status == 503; // the controller was unreachable: leave it waiting
            await using var up = new NpgsqlCommand(keep ? "update adsuite.approvals set error = @e where id = @i"
                : "update adsuite.approvals set status = @s, decided_by = @by, decided_at = now(), error = @e where id = @i", db);
            AdModule.P(up, "i", id); AdModule.P(up, "e", AdModule.Clip(o.Error, 500));
            if (!keep) { AdModule.P(up, "s", o.Ok ? "done" : "failed"); AdModule.P(up, "by", AdModule.Actor(u)); }
            await up.ExecuteNonQueryAsync(ct);
            await audit.WriteAsync("adsuite.approval.approve", $"{d.Name}: {target}", o.Ok ? "done" : "failed: " + o.Error, null, ct);
            return Results.Json(o.Body, statusCode: o.Status);
        }), key);

        AdModule.Use(r.MapPost("/api/adsuite/approvals/{id:int}/reject", async (int id, JsonElement body, ClaimsPrincipal u, ITenantContext t, IAccess access, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await AdModule.Open(t, ct);
            int domainId; string target;
            await using (var c = new NpgsqlCommand("select domain_id, target_dn from adsuite.approvals where id = @i and status = 'pending'", db))
            {
                AdModule.P(c, "i", id); await using var rd = await c.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) return Results.NotFound();
                domainId = rd.GetInt32(0); target = rd.GetString(1);
            }
            if (!await Perms.CanAsync(db, access, u, domainId, target, "approve", ct)) return AdModule.Forbid("You may not decide on this.");
            await using var up = new NpgsqlCommand("update adsuite.approvals set status = 'rejected', decided_by = @by, decided_at = now(), decision_note = @n where id = @i", db);
            AdModule.P(up, "i", id); AdModule.P(up, "by", AdModule.Actor(u)); AdModule.P(up, "n", AdModule.Clip((AdModule.Str(body, "note") ?? "").Trim(), 500));
            await up.ExecuteNonQueryAsync(ct);
            await audit.WriteAsync("adsuite.approval.reject", target, AdModule.Clip(AdModule.Str(body, "note"), 200), null, ct);
            return Results.Ok(new { ok = true });
        }), key);
    }
}
