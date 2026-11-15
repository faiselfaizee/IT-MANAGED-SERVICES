using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DeskAsk.Platform.Vault;

/// <summary>
/// Access requests ("check-out"): someone who may not read an item asks, with a reason, for a limited time. An editor or manager of the
/// vault approves in their browser, which encrypts the item once more for the requester's public key; the copy is deleted when the
/// time is up (and a managed account marked "change after each use" gets a new password).
/// </summary>
public static class RequestsApi
{
    static async Task Notify(HttpContext http, string template, string to, Dictionary<string, string> values, string reference, CancellationToken ct)
    {
        var mail = http.RequestServices.GetService<IMailQueue>();
        if (mail != null && to.Contains('@')) await mail.EnqueueTemplateAsync(template, to, values, reference, ct);
    }

    static async Task<string> VaultName(NpgsqlConnection db, int id, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("select name from vault.vaults where id = @v", db); VaultModule.P(c, "v", id);
        return await c.ExecuteScalarAsync(ct) as string ?? "";
    }

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        VaultModule.Use(r.MapPost("/api/vault/items/{id:long}/requests", async (long id, JsonElement b, HttpContext http, ITenantContext t, ISettings s, IUserDirectory users, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            if (await ItemsApi.FindAsync(db, id, ct) is not { } it) return Results.NotFound();
            var role = await Access.MyRole(db, http, it.VaultId, ct);
            if (role == null) return Results.NotFound();
            if (ItemsApi.CanRead(role, it.RequiresApproval)) return VaultModule.Bad("not_needed", "You can open this item already.");
            var me = VaultModule.Me(http.User);
            await using (var k = new NpgsqlCommand("select 1 from vault.keys where user_id = @u", db)) { VaultModule.P(k, "u", me); if (await k.ExecuteScalarAsync(ct) == null) return VaultModule.Bad("not_set_up", "Set up your vault first."); }
            var reason = (VaultModule.Str(b, "reason") ?? "").Trim(); var ticket = (VaultModule.Str(b, "ticket") ?? "").Trim();
            if (reason.Length < 3 || reason.Length > 500) return VaultModule.Bad("bad_input", "Say why you need it (3 to 500 characters).");
            if (ticket.Length > 40) return VaultModule.Bad("bad_input", "The ticket number is too long.");
            var settings = await VaultSettings.LoadAsync(s, ct);
            var minutes = Math.Clamp(VaultModule.Int(b, "minutes") ?? 60, 15, Math.Max(15, settings.RequestMaxMinutes));
            await using (var q = new NpgsqlCommand("select 1 from vault.requests where item_id = @i and user_id = @u and status = 'pending'", db))
            { VaultModule.P(q, "i", id); VaultModule.P(q, "u", me); if (await q.ExecuteScalarAsync(ct) != null) return Results.Conflict(new { error = "pending", detail = "You have asked for this item already; wait for an answer." }); }
            if (await ManagedApi.HasGrant(db, id, me, ct)) return VaultModule.Bad("granted", "You have access to this item right now.");
            var name = (await users.GetAsync(me, ct))?.Name ?? VaultModule.Actor(http.User);
            long rid;
            await using (var c = new NpgsqlCommand("insert into vault.requests (item_id, vault_id, user_id, user_name, reason, ticket, minutes) values (@i, @v, @u, @n, @r, @t, @m) returning id", db))
            { VaultModule.P(c, "i", id); VaultModule.P(c, "v", it.VaultId); VaultModule.P(c, "u", me); VaultModule.P(c, "n", name); VaultModule.P(c, "r", reason); VaultModule.P(c, "t", ticket); VaultModule.P(c, "m", minutes); rid = (long)(await c.ExecuteScalarAsync(ct))!; }
            await Access.Event(db, it.VaultId, id, VaultModule.Actor(http.User), "requested", $"{minutes} minutes: {reason}", ct);
            var vname = await VaultName(db, it.VaultId, ct);
            foreach (var (uid, urole) in await Access.Entitled(db, http.RequestServices.GetRequiredService<IAccess>(), it.VaultId, ct))
                if (uid != me && VaultModule.Rank(urole) >= 3 && await users.GetAsync(uid, ct) is { Active: true } u)
                    await Notify(http, "vault_request", u.Email, new() { ["requester"] = name, ["vault"] = vname, ["reason"] = reason, ["ticket"] = ticket == "" ? "-" : ticket, ["minutes"] = minutes.ToString(), ["path"] = "#/vault/requests" }, "vault-request:" + rid, ct);
            return Results.Ok(new { id = rid });
        }), key);

        // mine: what I asked for; approve: what is waiting for me (with the requester's public key, so my browser can encrypt for them)
        VaultModule.Use(r.MapGet("/api/vault/requests", async (string? scope, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            var me = VaultModule.Me(http.User);
            var o = new List<object>();
            if (scope == "approve")
            {
                var vaults = (await Access.MyRoles(db, http, ct)).Where(x => VaultModule.Rank(x.Value) >= 3).Select(x => x.Key).ToArray();
                if (vaults.Length == 0) return Results.Ok(new { items = o });
                await using var c = new NpgsqlCommand(@"select r.id, r.item_id, r.vault_id, v.name, r.user_id, r.user_name, r.reason, r.ticket, r.minutes, r.created_at, i.title_enc, i.type, i.managed, k.public_key
                    from vault.requests r join vault.items i on i.id = r.item_id join vault.vaults v on v.id = r.vault_id left join vault.keys k on k.user_id = r.user_id
                    where r.status = 'pending' and r.vault_id = any(@v) and r.user_id <> @u order by r.id", db);
                VaultModule.P(c, "v", vaults); VaultModule.P(c, "u", me);
                await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                    o.Add(new { id = rd.GetInt64(0), itemId = rd.GetInt64(1), vaultId = rd.GetInt32(2), vault = rd.GetString(3), userId = rd.GetGuid(4), user = rd.GetString(5), reason = rd.GetString(6), ticket = rd.GetString(7), minutes = rd.GetInt32(8), at = rd.GetDateTime(9), titleEnc = rd.GetString(10), type = rd.GetString(11), managed = rd.GetBoolean(12), publicKey = rd.IsDBNull(13) ? null : rd.GetString(13) });
            }
            else
            {
                await using var c = new NpgsqlCommand(@"select r.id, r.item_id, r.vault_id, v.name, r.reason, r.ticket, r.minutes, r.status, r.decided_by, r.decided_at, r.note, r.expires_at, r.created_at, i.title_enc, i.type
                    from vault.requests r join vault.items i on i.id = r.item_id join vault.vaults v on v.id = r.vault_id where r.user_id = @u order by r.id desc limit 100", db);
                VaultModule.P(c, "u", me);
                await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                    o.Add(new { id = rd.GetInt64(0), itemId = rd.GetInt64(1), vaultId = rd.GetInt32(2), vault = rd.GetString(3), reason = rd.GetString(4), ticket = rd.GetString(5), minutes = rd.GetInt32(6), status = rd.GetString(7), decidedBy = rd.GetString(8), decidedAt = rd.IsDBNull(9) ? (DateTime?)null : rd.GetDateTime(9), note = rd.GetString(10), expiresAt = rd.IsDBNull(11) ? (DateTime?)null : rd.GetDateTime(11), at = rd.GetDateTime(12), titleEnc = rd.GetString(13), type = rd.GetString(14) });
            }
            return Results.Ok(new { items = o });
        }), key);

        VaultModule.Use(r.MapPost("/api/vault/requests/{id:long}/{action:regex(^(approve|deny|cancel)$)}", async (long id, string action, JsonElement b, HttpContext http, ITenantContext t, IUserDirectory users, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            long item; int vault; Guid requester; string requesterName; int minutes; string status;
            await using (var q = new NpgsqlCommand("select item_id, vault_id, user_id, user_name, minutes, status from vault.requests where id = @r", db))
            { VaultModule.P(q, "r", id); await using var rd = await q.ExecuteReaderAsync(ct); if (!await rd.ReadAsync(ct)) return Results.NotFound(); item = rd.GetInt64(0); vault = rd.GetInt32(1); requester = rd.GetGuid(2); requesterName = rd.GetString(3); minutes = rd.GetInt32(4); status = rd.GetString(5); }
            if (status != "pending") return Results.Conflict(new { error = "decided", detail = $"This request is {status} already." });
            var me = VaultModule.Me(http.User); var by = VaultModule.Actor(http.User);
            var note = (VaultModule.Str(b, "note") ?? "").Trim(); if (note.Length > 300) note = note[..300];
            if (action == "cancel")
            {
                if (requester != me) return VaultModule.Forbid("Only the person who asked cancels a request.");
                await using var c = new NpgsqlCommand("update vault.requests set status = 'cancelled', decided_at = now() where id = @r", db); VaultModule.P(c, "r", id); await c.ExecuteNonQueryAsync(ct);
                return Results.Ok(new { ok = true });
            }
            if (requester == me) return VaultModule.Forbid("Someone else has to answer your own request.");
            if (VaultModule.Rank(await Access.MyRole(db, http, vault, ct)) < 3) return VaultModule.Forbid("Only the vault's editors and managers answer requests.");
            if (action == "approve")
            {
                var data = VaultModule.Str(b, "dataEnc");
                if (!VaultModule.Blob(data, 80000)) return VaultModule.Bad("bad_input", "The item encrypted for the requester is needed.");
                minutes = Math.Clamp(VaultModule.Int(b, "minutes") ?? minutes, 15, minutes);
                await using var tx = await db.BeginTransactionAsync(ct);
                await using (var c = new NpgsqlCommand("update vault.requests set status = 'approved', decided_by = @b, decided_at = now(), note = @n, expires_at = now() + make_interval(mins => @m), minutes = @m where id = @r and status = 'pending'", db, tx))
                { VaultModule.P(c, "b", by); VaultModule.P(c, "n", note); VaultModule.P(c, "m", minutes); VaultModule.P(c, "r", id); if (await c.ExecuteNonQueryAsync(ct) == 0) return Results.Conflict(new { error = "decided", detail = "Someone answered meanwhile." }); }
                await using (var c = new NpgsqlCommand("insert into vault.grants (request_id, item_id, user_id, data_enc, expires_at, granted_by) values (@r, @i, @u, @d, now() + make_interval(mins => @m), @b)", db, tx))
                { VaultModule.P(c, "r", id); VaultModule.P(c, "i", item); VaultModule.P(c, "u", requester); VaultModule.P(c, "d", data); VaultModule.P(c, "m", minutes); VaultModule.P(c, "b", by); await c.ExecuteNonQueryAsync(ct); }
                await tx.CommitAsync(ct);
            }
            else
                await using (var c = new NpgsqlCommand("update vault.requests set status = 'denied', decided_by = @b, decided_at = now(), note = @n where id = @r", db))
                { VaultModule.P(c, "b", by); VaultModule.P(c, "n", note); VaultModule.P(c, "r", id); await c.ExecuteNonQueryAsync(ct); }
            var word = action == "approve" ? "approved" : "denied";
            await Access.Event(db, vault, item, by, word, $"{requesterName}{(action == "approve" ? $", {minutes} minutes" : "")}{(note == "" ? "" : ": " + note)}", ct);
            await audit.WriteAsync("vault.request_" + word, id.ToString(), $"{requesterName}, item {item}", ct: ct);
            if (await users.GetAsync(requester, ct) is { Active: true } u)
                await Notify(http, "vault_decision", u.Email, new() { ["requester"] = u.Name, ["vault"] = await VaultName(db, vault, ct), ["decision"] = word, ["by"] = by, ["note"] = note == "" ? (action == "approve" ? $"You can open it for {minutes} minutes." : "") : note, ["path"] = "#/vault/requests" }, "vault-decision:" + id, ct);
            return Results.Ok(new { ok = true });
        }), key);

        // what I may open right now through an approved request
        VaultModule.Use(r.MapGet("/api/vault/grants", async (HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            await using var c = new NpgsqlCommand(@"select g.id, g.item_id, i.vault_id, i.title_enc, i.type, i.managed, g.data_enc, g.expires_at, g.granted_by from vault.grants g join vault.items i on i.id = g.item_id
                where g.user_id = @u and g.expires_at > now() and i.deleted_at is null order by g.expires_at", db);
            VaultModule.P(c, "u", VaultModule.Me(http.User));
            var o = new List<object>(); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) o.Add(new { id = rd.GetInt64(0), itemId = rd.GetInt64(1), vaultId = rd.GetInt32(2), titleEnc = rd.GetString(3), type = rd.GetString(4), managed = rd.GetBoolean(5), dataEnc = rd.GetString(6), expiresAt = rd.GetDateTime(7), grantedBy = rd.GetString(8) });
            return Results.Ok(new { items = o });
        }), key);

        // give it back early
        VaultModule.Use(r.MapDelete("/api/vault/grants/{id:long}", async (long id, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            await using var c = new NpgsqlCommand("update vault.grants set expires_at = now() where id = @g and user_id = @u and expires_at > now()", db);
            VaultModule.P(c, "g", id); VaultModule.P(c, "u", VaultModule.Me(http.User));
            return await c.ExecuteNonQueryAsync(ct) > 0 ? Results.Ok(new { ok = true }) : Results.NotFound();
        }), key);
    }
}
