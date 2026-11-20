using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace DeskAsk.Platform.Vault;

/// <summary>
/// Items (logins, notes, cards, server accounts and so on), all encrypted in the browser. Changes keep the old version; deleting moves
/// to the trash for 30 days. People who may only ask for access see titles, not contents. Items marked "needs approval" are read only by
/// the vault's editors and managers; viewers ask first.
/// </summary>
public static class ItemsApi
{
    public static readonly string[] Types = { "login", "note", "card", "identity", "ssh", "server", "network", "wifi", "database", "apikey" };

    /// <summary>May someone with this role read the contents of an item?</summary>
    public static bool CanRead(string? role, bool requiresApproval) => VaultModule.Rank(role) >= (requiresApproval ? 3 : 2);

    sealed record Row(long Id, int VaultId, int? FolderId, string Type, string DataEnc, string TitleEnc, int KeyVersion, int Revision, long? AssetId,
        bool Managed, bool RequiresApproval, DateTime? PwChangedAt, string CreatedBy, string UpdatedBy, DateTime CreatedAt, DateTime UpdatedAt, DateTime? DeletedAt);

    const string Cols = "i.id, i.vault_id, i.folder_id, i.type, i.data_enc, i.title_enc, i.key_version, i.revision, i.asset_id, i.managed, i.requires_approval, i.pw_changed_at, i.created_by, i.updated_by, i.created_at, i.updated_at, i.deleted_at";
    static Row Read(NpgsqlDataReader r) => new(r.GetInt64(0), r.GetInt32(1), r.IsDBNull(2) ? null : r.GetInt32(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt32(6), r.GetInt32(7),
        r.IsDBNull(8) ? null : r.GetInt64(8), r.GetBoolean(9), r.GetBoolean(10), r.IsDBNull(11) ? null : r.GetDateTime(11), r.GetString(12), r.GetString(13), r.GetDateTime(14), r.GetDateTime(15), r.IsDBNull(16) ? null : r.GetDateTime(16));

    static object Out(Row i, string role) => new
    {
        id = i.Id, vaultId = i.VaultId, folderId = i.FolderId, type = i.Type, titleEnc = i.TitleEnc, keyVersion = i.KeyVersion, revision = i.Revision,
        dataEnc = CanRead(role, i.RequiresApproval) ? i.DataEnc : null, canRead = CanRead(role, i.RequiresApproval), canEdit = VaultModule.Rank(role) >= 3,
        assetId = i.AssetId, managed = i.Managed, requiresApproval = i.RequiresApproval, pwChangedAt = i.PwChangedAt,
        createdBy = i.CreatedBy, updatedBy = i.UpdatedBy, createdAt = i.CreatedAt, updatedAt = i.UpdatedAt, deletedAt = i.DeletedAt,
    };

    internal static async Task<(long Id, int VaultId, bool Managed, bool RequiresApproval, int Revision, string TitleEnc, long? AssetId)?> FindAsync(NpgsqlConnection db, long id, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("select id, vault_id, managed, requires_approval, revision, title_enc, asset_id from vault.items where id = @i and deleted_at is null", db); VaultModule.P(c, "i", id);
        await using var rd = await c.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return (rd.GetInt64(0), rd.GetInt32(1), rd.GetBoolean(2), rd.GetBoolean(3), rd.GetInt32(4), rd.GetString(5), rd.IsDBNull(6) ? null : rd.GetInt64(6));
    }

    /// <summary>Checks the asset exists and, for a customer's vault, belongs to that customer.</summary>
    static async Task<string?> AssetProblem(NpgsqlConnection db, IAssetDirectory assets, int vaultId, long? assetId, CancellationToken ct)
    {
        if (assetId == null) return null;
        if (await assets.GetAsync(assetId.Value, ct) is not { } a) return "That asset does not exist.";
        await using var c = new NpgsqlCommand("select kind, company_id from vault.vaults where id = @v", db); VaultModule.P(c, "v", vaultId);
        await using var rd = await c.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return "The vault does not exist.";
        if (rd.GetString(0) == "customer" && !rd.IsDBNull(1) && a.CompanyId != rd.GetInt32(1)) return "That asset belongs to another customer.";
        return null;
    }

    static async Task<int?> KeyVersion(NpgsqlConnection db, int vaultId, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("select key_version from vault.vaults where id = @v", db); VaultModule.P(c, "v", vaultId);
        return await c.ExecuteScalarAsync(ct) as int?;
    }

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        // every item I can see, in every vault (the browser decrypts them for search, the health report and the export)
        VaultModule.Use(r.MapGet("/api/vault/items", async (int? vault, bool? trash, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            var roles = await Access.MyRoles(db, http, ct);
            var ids = roles.Keys.Where(v => vault == null || v == vault).ToArray();
            var list = new List<object>();
            if (ids.Length == 0) return Results.Ok(new { items = list });
            await using var c = new NpgsqlCommand($"select {Cols} from vault.items i where i.vault_id = any(@v) and (i.deleted_at is not null) = @t order by i.id", db);
            VaultModule.P(c, "v", ids); VaultModule.P(c, "t", trash == true);
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var i = Read(rd); var role = roles[i.VaultId];
                if (trash == true && VaultModule.Rank(role) < 3) continue;
                list.Add(Out(i, role));
            }
            return Results.Ok(new { items = list });
        }), key);

        VaultModule.Use(r.MapGet("/api/vault/items/{id:long}", async (long id, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            await using var c = new NpgsqlCommand($"select {Cols} from vault.items i where i.id = @i", db); VaultModule.P(c, "i", id);
            Row? row = null;
            await using (var rd = await c.ExecuteReaderAsync(ct)) if (await rd.ReadAsync(ct)) row = Read(rd);
            if (row == null) return Results.NotFound();
            var role = await Access.MyRole(db, http, row.VaultId, ct);
            return role == null ? Results.NotFound() : Results.Ok(Out(row, role));
        }), key);

        VaultModule.Use(r.MapPost("/api/vault/vaults/{vid:int}/items", async (int vid, JsonElement b, HttpContext http, ITenantContext t, IAssetDirectory assets, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            if (VaultModule.Rank(await Access.MyRole(db, http, vid, ct)) < 3) return VaultModule.Forbid("You may not add items to this vault.");
            var type = VaultModule.Str(b, "type"); var data = VaultModule.Str(b, "dataEnc"); var title = VaultModule.Str(b, "titleEnc");
            if (!Types.Contains(type)) return VaultModule.Bad("bad_input", "type must be one of " + string.Join(", ", Types));
            if (!VaultModule.Blob(data, 70000) || !VaultModule.Blob(title, 3000)) return VaultModule.Bad("bad_input", "The item is not encrypted properly (or is larger than 50 KB).");
            var kv = VaultModule.Int(b, "keyVersion");
            if (kv != await KeyVersion(db, vid, ct)) return Results.Conflict(new { error = "key_version", detail = "The vault's key changed meanwhile. Reload and try again." });
            long? asset = VaultModule.Long(b, "assetId");
            if (await AssetProblem(db, assets, vid, asset, ct) is { } ap) return VaultModule.Bad("bad_asset", ap);
            var folder = VaultModule.Int(b, "folderId");
            if (folder != null) await using (var f = new NpgsqlCommand("select 1 from vault.folders where id = @f and vault_id = @v", db)) { VaultModule.P(f, "f", folder); VaultModule.P(f, "v", vid); if (await f.ExecuteScalarAsync(ct) == null) return VaultModule.Bad("bad_input", "That folder is in another vault."); }
            var by = VaultModule.Actor(http.User);
            await using var c = new NpgsqlCommand(@"insert into vault.items (vault_id, folder_id, type, data_enc, title_enc, key_version, asset_id, requires_approval, pw_changed_at, created_by, updated_by)
                values (@v, @f, @t, @d, @n, @kv, @a, @r, case when @pw then now() end, @b, @b) returning id", db);
            VaultModule.P(c, "v", vid); VaultModule.P(c, "f", folder); VaultModule.P(c, "t", type); VaultModule.P(c, "d", data); VaultModule.P(c, "n", title); VaultModule.P(c, "kv", kv);
            VaultModule.P(c, "a", asset); VaultModule.P(c, "r", VaultModule.Bool(b, "requiresApproval")); VaultModule.P(c, "pw", VaultModule.Bool(b, "hasPassword")); VaultModule.P(c, "b", by);
            var id = (long)(await c.ExecuteScalarAsync(ct))!;
            await Access.Event(db, vid, id, by, "created", type!, ct);
            return Results.Ok(new { id, revision = 1 });
        }), key);

        // a change; revision is the one the browser started from, so two people editing at once do not overwrite each other
        VaultModule.Use(r.MapPut("/api/vault/items/{id:long}", async (long id, JsonElement b, HttpContext http, ITenantContext t, IAssetDirectory assets, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            if (await FindAsync(db, id, ct) is not { } it) return Results.NotFound();
            if (VaultModule.Rank(await Access.MyRole(db, http, it.VaultId, ct)) < 3) return VaultModule.Forbid("You may not change items in this vault.");
            var data = VaultModule.Str(b, "dataEnc"); var title = VaultModule.Str(b, "titleEnc");
            if (!VaultModule.Blob(data, 70000) || !VaultModule.Blob(title, 3000)) return VaultModule.Bad("bad_input", "The item is not encrypted properly (or is larger than 50 KB).");
            if (VaultModule.Int(b, "revision") != it.Revision) return Results.Conflict(new { error = "changed", detail = "Someone changed this item meanwhile. Reload it and make your change again.", revision = it.Revision });
            var kv = VaultModule.Int(b, "keyVersion");
            if (kv != await KeyVersion(db, it.VaultId, ct)) return Results.Conflict(new { error = "key_version", detail = "The vault's key changed meanwhile. Reload and try again." });
            var hasAsset = b.TryGetProperty("assetId", out _); long? asset = hasAsset ? VaultModule.Long(b, "assetId") : it.AssetId;
            if (hasAsset && await AssetProblem(db, assets, it.VaultId, asset, ct) is { } ap) return VaultModule.Bad("bad_asset", ap);
            if (it.Managed && hasAsset && asset != it.AssetId) return VaultModule.Bad("managed", "Stop managing the account before linking the item to another asset.");
            var hasFolder = b.TryGetProperty("folderId", out _); var folder = VaultModule.Int(b, "folderId");
            if (hasFolder && folder != null) await using (var f = new NpgsqlCommand("select 1 from vault.folders where id = @f and vault_id = @v", db)) { VaultModule.P(f, "f", folder); VaultModule.P(f, "v", it.VaultId); if (await f.ExecuteScalarAsync(ct) == null) return VaultModule.Bad("bad_input", "That folder is in another vault."); }
            var by = VaultModule.Actor(http.User);
            await using var tx = await db.BeginTransactionAsync(ct);
            await using (var h = new NpgsqlCommand("insert into vault.history (item_id, revision, data_enc, key_version, changed_by) select id, revision, data_enc, key_version, updated_by from vault.items where id = @i", db, tx)) { VaultModule.P(h, "i", id); await h.ExecuteNonQueryAsync(ct); }
            await using (var h = new NpgsqlCommand("delete from vault.history where item_id = @i and revision <= (select revision from vault.items where id = @i) - 20", db, tx)) { VaultModule.P(h, "i", id); await h.ExecuteNonQueryAsync(ct); }
            await using (var c = new NpgsqlCommand(@"update vault.items set data_enc = @d, title_enc = @n, key_version = @kv, revision = revision + 1, updated_by = @b, updated_at = now(),
                    asset_id = @a, folder_id = case when @hf then @f else folder_id end, requires_approval = coalesce(@r, requires_approval),
                    pw_changed_at = case when @pw then now() else pw_changed_at end where id = @i", db, tx))
            {
                VaultModule.P(c, "d", data); VaultModule.P(c, "n", title); VaultModule.P(c, "kv", kv); VaultModule.P(c, "b", by); VaultModule.P(c, "a", asset);
                VaultModule.P(c, "hf", hasFolder); VaultModule.P(c, "f", folder); VaultModule.P(c, "r", b.TryGetProperty("requiresApproval", out _) ? VaultModule.Bool(b, "requiresApproval") : null);
                VaultModule.P(c, "pw", VaultModule.Bool(b, "passwordChanged")); VaultModule.P(c, "i", id); await c.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            await Access.Event(db, it.VaultId, id, by, "changed", VaultModule.Bool(b, "passwordChanged") ? "password changed" : "", ct);
            return Results.Ok(new { ok = true, revision = it.Revision + 1 });
        }), key);

        // moving to another vault: the browser re-encrypted the item with that vault's key; old versions cannot follow
        VaultModule.Use(r.MapPost("/api/vault/items/{id:long}/move", async (long id, JsonElement b, HttpContext http, ITenantContext t, IAssetDirectory assets, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            if (await FindAsync(db, id, ct) is not { } it) return Results.NotFound();
            var to = VaultModule.Int(b, "vaultId") ?? 0;
            if (VaultModule.Rank(await Access.MyRole(db, http, it.VaultId, ct)) < 3 || VaultModule.Rank(await Access.MyRole(db, http, to, ct)) < 3) return VaultModule.Forbid("You need to be able to change both vaults.");
            if (it.Managed) return VaultModule.Bad("managed", "Stop managing the account before moving it.");
            var data = VaultModule.Str(b, "dataEnc"); var title = VaultModule.Str(b, "titleEnc");
            if (!VaultModule.Blob(data, 70000) || !VaultModule.Blob(title, 3000)) return VaultModule.Bad("bad_input", "The item is not encrypted properly.");
            var kv = VaultModule.Int(b, "keyVersion");
            if (kv != await KeyVersion(db, to, ct)) return Results.Conflict(new { error = "key_version", detail = "The vault's key changed meanwhile. Reload and try again." });
            var assetOk = await AssetProblem(db, assets, to, it.AssetId, ct) == null;
            var by = VaultModule.Actor(http.User);
            await using var tx = await db.BeginTransactionAsync(ct);
            await using (var c = new NpgsqlCommand(@"delete from vault.history where item_id = @i; delete from vault.grants where item_id = @i; update vault.requests set status = 'cancelled' where item_id = @i and status = 'pending';
                update vault.items set vault_id = @v, folder_id = null, data_enc = @d, title_enc = @n, key_version = @kv, revision = revision + 1, updated_by = @b, updated_at = now(),
                  asset_id = case when @ao then asset_id end where id = @i", db, tx))
            { VaultModule.P(c, "i", id); VaultModule.P(c, "v", to); VaultModule.P(c, "d", data); VaultModule.P(c, "n", title); VaultModule.P(c, "kv", kv); VaultModule.P(c, "b", by); VaultModule.P(c, "ao", assetOk); await c.ExecuteNonQueryAsync(ct); }
            await tx.CommitAsync(ct);
            await Access.Event(db, it.VaultId, id, by, "moved_out", $"to vault {to}", ct);
            await Access.Event(db, to, id, by, "moved_in", $"from vault {it.VaultId}", ct);
            return Results.Ok(new { ok = true });
        }), key);

        VaultModule.Use(r.MapDelete("/api/vault/items/{id:long}", async (long id, bool? purge, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            int vid; bool deleted;
            await using (var q = new NpgsqlCommand("select vault_id, deleted_at is not null from vault.items where id = @i", db))
            { VaultModule.P(q, "i", id); await using var rd = await q.ExecuteReaderAsync(ct); if (!await rd.ReadAsync(ct)) return Results.NotFound(); vid = rd.GetInt32(0); deleted = rd.GetBoolean(1); }
            var role = await Access.MyRole(db, http, vid, ct);
            if (VaultModule.Rank(role) < 3) return VaultModule.Forbid("You may not delete items in this vault.");
            var by = VaultModule.Actor(http.User);
            if (purge == true)
            {
                if (!deleted) return VaultModule.Bad("not_in_trash", "Move the item to the trash first.");
                await using var c = new NpgsqlCommand("delete from vault.items where id = @i", db); VaultModule.P(c, "i", id); await c.ExecuteNonQueryAsync(ct);
                await Access.Event(db, vid, null, by, "purged", $"item {id}", ct);
                await audit.WriteAsync("vault.item_purged", id.ToString(), $"vault {vid}", ct: ct);
                return Results.Ok(new { ok = true });
            }
            await using (var c = new NpgsqlCommand("update vault.items set deleted_at = now(), updated_by = @b where id = @i and deleted_at is null; update vault.requests set status = 'cancelled' where item_id = @i and status = 'pending'; delete from vault.grants where item_id = @i", db))
            { VaultModule.P(c, "i", id); VaultModule.P(c, "b", by); await c.ExecuteNonQueryAsync(ct); }
            await Access.Event(db, vid, id, by, "deleted", "moved to the trash", ct);
            return Results.Ok(new { ok = true });
        }), key);

        VaultModule.Use(r.MapPost("/api/vault/items/{id:long}/restore", async (long id, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            int vid; await using (var q = new NpgsqlCommand("select vault_id from vault.items where id = @i and deleted_at is not null", db)) { VaultModule.P(q, "i", id); if (await q.ExecuteScalarAsync(ct) is not int v) return Results.NotFound(); vid = v; }
            if (VaultModule.Rank(await Access.MyRole(db, http, vid, ct)) < 3) return VaultModule.Forbid("You may not restore items in this vault.");
            await using (var c = new NpgsqlCommand("update vault.items set deleted_at = null where id = @i", db)) { VaultModule.P(c, "i", id); await c.ExecuteNonQueryAsync(ct); }
            await Access.Event(db, vid, id, VaultModule.Actor(http.User), "restored", "", ct);
            return Results.Ok(new { ok = true });
        }), key);

        VaultModule.Use(r.MapGet("/api/vault/items/{id:long}/history", async (long id, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            if (await FindAsync(db, id, ct) is not { } it) return Results.NotFound();
            if (!CanRead(await Access.MyRole(db, http, it.VaultId, ct), it.RequiresApproval)) return Results.NotFound();
            await using var c = new NpgsqlCommand("select revision, data_enc, key_version, changed_by, at from vault.history where item_id = @i order by revision desc", db); VaultModule.P(c, "i", id);
            var o = new List<object>(); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) o.Add(new { revision = rd.GetInt32(0), dataEnc = rd.GetString(1), keyVersion = rd.GetInt32(2), changedBy = rd.GetString(3), at = rd.GetDateTime(4) });
            return Results.Ok(new { items = o });
        }), key);

        // the browser reports what the person did with a secret it decrypted (shown, copied, opened); the item's log is for the vault's editors
        VaultModule.Use(r.MapPost("/api/vault/items/{id:long}/events", async (long id, JsonElement b, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            if (await FindAsync(db, id, ct) is not { } it) return Results.NotFound();
            if (await Access.MyRole(db, http, it.VaultId, ct) == null) return Results.NotFound();
            var action = VaultModule.Str(b, "action"); var field = (VaultModule.Str(b, "field") ?? "").Trim();
            if (action is not ("viewed" or "revealed" or "copied" or "opened" or "exported")) return VaultModule.Bad("bad_input", "action must be viewed, revealed, copied, opened or exported");
            if (field.Length > 60) field = field[..60];
            await Access.Event(db, it.VaultId, id, VaultModule.Actor(http.User), action, field, ct);
            return Results.Ok(new { ok = true });
        }), key);

        VaultModule.Use(r.MapGet("/api/vault/items/{id:long}/events", async (long id, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            int vid; await using (var q = new NpgsqlCommand("select vault_id from vault.items where id = @i", db)) { VaultModule.P(q, "i", id); if (await q.ExecuteScalarAsync(ct) is not int v) return Results.NotFound(); vid = v; }
            if (VaultModule.Rank(await Access.MyRole(db, http, vid, ct)) < 3) return Results.NotFound();
            await using var c = new NpgsqlCommand("select id, user_name, action, detail, at from vault.events where item_id = @i order by id desc limit 200", db); VaultModule.P(c, "i", id);
            var o = new List<object>(); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) o.Add(new { id = rd.GetInt64(0), user = rd.GetString(1), action = rd.GetString(2), detail = rd.GetString(3), at = rd.GetDateTime(4) });
            return Results.Ok(new { items = o });
        }), key);

        // Vault › Reports › Access report: who opened, revealed, copied or exported which item, in the vaults I manage or edit.
        // Item titles stay encrypted; the browser decrypts them with keys it already holds.
        VaultModule.Use(r.MapGet("/api/vault/reports/access", async (HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            var q = http.Request.Query;
            var days = int.TryParse(q["days"], out var d) && d is >= 1 and <= 366 ? d : 30;
            var who = q["user"].ToString().Trim(); var act = q["action"].ToString();
            if (act is not ("" or "viewed" or "revealed" or "copied" or "opened" or "exported")) return VaultModule.Bad("bad_input", "action must be viewed, revealed, copied, opened or exported");
            await using var db = await VaultModule.Open(t, ct);
            var mine = (await Access.MyRoles(db, http, ct)).Where(x => VaultModule.Rank(x.Value) >= 3).Select(x => x.Key).ToArray();
            await using var c = new NpgsqlCommand(@"select e.id, e.at, e.user_name, e.action, e.detail, e.vault_id, v.name, e.item_id, coalesce(i.title_enc, ''), i.deleted_at is not null
                from vault.events e join vault.vaults v on v.id = e.vault_id left join vault.items i on i.id = e.item_id
                where e.vault_id = any(@v) and e.item_id is not null and e.at > now() - make_interval(days => @d)
                  and e.action in ('viewed','revealed','copied','opened','exported') and (@a = '' or e.action = @a) and (@u = '' or e.user_name ilike @ul)
                order by e.id desc limit 2000", db);
            VaultModule.P(c, "v", mine); VaultModule.P(c, "d", days); VaultModule.P(c, "a", act); VaultModule.P(c, "u", who); VaultModule.P(c, "ul", "%" + who.Replace("%", "") + "%");
            var o = new List<object>(); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                o.Add(new { id = rd.GetInt64(0), at = rd.GetDateTime(1), user = rd.GetString(2), action = rd.GetString(3), field = rd.GetString(4), vaultId = rd.GetInt32(5), vault = rd.GetString(6), itemId = rd.GetInt64(7), titleEnc = rd.GetString(8), deleted = rd.GetBoolean(9) });
            return Results.Ok(new { days, vaults = mine.Length, items = o });
        }), key);

        // items linked to an asset, for the asset's page (titles only; the browser decrypts them with keys it already has)
        VaultModule.Use(r.MapGet("/api/vault/assets/{assetId:long}/items", async (long assetId, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            var roles = await Access.MyRoles(db, http, ct);
            var o = new List<object>();
            if (roles.Count == 0) return Results.Ok(new { items = o });
            await using var c = new NpgsqlCommand($"select {Cols} from vault.items i where i.asset_id = @a and i.deleted_at is null and i.vault_id = any(@v) order by i.id", db);
            VaultModule.P(c, "a", assetId); VaultModule.P(c, "v", roles.Keys.ToArray());
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) { var i = Read(rd); o.Add(Out(i, roles[i.VaultId])); }
            return Results.Ok(new { items = o });
        }), key);
    }
}
