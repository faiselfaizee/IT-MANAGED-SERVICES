using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DeskAsk.Platform.Vault;

/// <summary>
/// Vaults (one per customer, internal ones and a personal one each), their members, the vault keys each member holds, folders and key
/// rotation. Giving someone a vault key is done by a manager's browser (it holds the key); the server only says who is waiting.
/// </summary>
public static class VaultsApi
{
    public static void Map(IEndpointRouteBuilder r, string key)
    {
        VaultModule.Use(r.MapGet("/api/vault/vaults", async (HttpContext http, ITenantContext t, ICompanyDirectory dir, CancellationToken ct) =>
        {
            var me = VaultModule.Me(http.User);
            await using var db = await VaultModule.Open(t, ct);
            var roles = await Access.MyRoles(db, http, ct);
            var names = (await dir.ListAsync(ct)).ToDictionary(c => c.Id, c => c.Name);
            var list = new List<object>();
            if (roles.Count == 0) return Results.Ok(new { items = list });
            await using var c = new NpgsqlCommand(@"select v.id, v.name, v.kind, v.company_id, v.key_version, v.needs_rotation, k.wrapped_key, k.wrapped_dir_key, k.key_version,
                    (select count(*) from vault.items i where i.vault_id = v.id and i.deleted_at is null),
                    (select count(*) from vault.folders f where f.vault_id = v.id)
                from vault.vaults v left join vault.vault_keys k on k.vault_id = v.id and k.user_id = @u where v.id = any(@ids) order by v.kind = 'personal' desc, v.kind, lower(v.name)", db);
            VaultModule.P(c, "u", me); VaultModule.P(c, "ids", roles.Keys.ToArray());
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var id = rd.GetInt32(0); var kv = rd.GetInt32(4);
                list.Add(new
                {
                    id, name = rd.GetString(1), kind = rd.GetString(2), companyId = rd.IsDBNull(3) ? (int?)null : rd.GetInt32(3),
                    company = rd.IsDBNull(3) ? "" : names.GetValueOrDefault(rd.GetInt32(3), ""), keyVersion = kv, needsRotation = rd.GetBoolean(5), role = roles[id],
                    wrappedKey = rd.IsDBNull(6) || rd.IsDBNull(8) || rd.GetInt32(8) != kv ? null : rd.GetString(6),
                    wrappedDirKey = rd.IsDBNull(7) || rd.IsDBNull(8) || rd.GetInt32(8) != kv ? null : rd.GetString(7),
                    items = rd.GetInt64(9), folders = rd.GetInt64(10),
                });
            }
            return Results.Ok(new { items = list });
        }), key);

        // the browser made the vault's keys and wrapped them for its own owner
        VaultModule.Use(r.MapPost("/api/vault/vaults", async (JsonElement b, HttpContext http, ITenantContext t, ISettings s, ICompanyDirectory dir, IAuditLog audit, CancellationToken ct) =>
        {
            var me = VaultModule.Me(http.User); var kind = VaultModule.Str(b, "kind") ?? "";
            var name = (VaultModule.Str(b, "name") ?? "").Trim(); var wk = VaultModule.Str(b, "wrappedKey"); var wd = VaultModule.Str(b, "wrappedDirKey");
            if (kind is not ("customer" or "internal" or "personal")) return VaultModule.Bad("bad_input", "kind must be customer, internal or personal");
            if (!VaultModule.Blob(wk, 4000) || !VaultModule.Blob(wd, 4000)) return VaultModule.Bad("bad_input", "The vault keys wrapped for you are needed.");
            int? company = VaultModule.Int(b, "companyId");
            await using var db = await VaultModule.Open(t, ct);
            await using (var k = new NpgsqlCommand("select 1 from vault.keys where user_id = @u", db)) { VaultModule.P(k, "u", me); if (await k.ExecuteScalarAsync(ct) == null) return VaultModule.Bad("not_set_up", "Set up your vault first."); }
            if (kind == "personal") { name = "My vault"; company = null; }
            else
            {
                var st = await VaultSettings.LoadAsync(s, ct); var role = VaultModule.Role(http.User);
                if (!(role == "admin" || (st.Creators == "engineers" && role == "engineer"))) return VaultModule.Forbid(st.Creators == "engineers" ? "Only administrators and engineers create vaults." : "Only administrators create vaults.");
                if (kind == "customer")
                {
                    if (company == null || await dir.GetAsync(company.Value, ct) is not { } co) return VaultModule.Bad("bad_input", "Choose the customer.");
                    if (name == "") name = co.Name;
                }
                else company = null;
                if (name == "" || name.Length > 120) return VaultModule.Bad("bad_input", "Give the vault a name (up to 120 characters).");
            }
            await using var tx = await db.BeginTransactionAsync(ct);
            int id;
            try
            {
                await using (var c = new NpgsqlCommand("insert into vault.vaults (name, kind, company_id, owner_user, created_by) values (@n, @k, @c, @o, @b) returning id", db, tx))
                { VaultModule.P(c, "n", name); VaultModule.P(c, "k", kind); VaultModule.P(c, "c", company); VaultModule.P(c, "o", kind == "personal" ? me : null); VaultModule.P(c, "b", VaultModule.Actor(http.User)); id = (int)(await c.ExecuteScalarAsync(ct))!; }
            }
            catch (PostgresException e) when (e.SqlState == "23505") { return Results.Conflict(new { error = "exists", detail = "You already have a personal vault." }); }
            if (kind != "personal")
                await using (var c = new NpgsqlCommand("insert into vault.members (vault_id, ptype, pid, role) values (@v, 'user', @u, 'manage')", db, tx)) { VaultModule.P(c, "v", id); VaultModule.P(c, "u", me); await c.ExecuteNonQueryAsync(ct); }
            await using (var c = new NpgsqlCommand("insert into vault.vault_keys (vault_id, user_id, key_version, wrapped_key, wrapped_dir_key, granted_by) values (@v, @u, 1, @k, @d, @b)", db, tx))
            { VaultModule.P(c, "v", id); VaultModule.P(c, "u", me); VaultModule.P(c, "k", wk); VaultModule.P(c, "d", wd); VaultModule.P(c, "b", VaultModule.Actor(http.User)); await c.ExecuteNonQueryAsync(ct); }
            await tx.CommitAsync(ct);
            if (kind != "personal") await audit.WriteAsync("vault.created", id.ToString(), $"{kind} vault \"{name}\"", ct: ct);
            return Results.Ok(new { id });
        }), key);

        VaultModule.Use(r.MapPut("/api/vault/vaults/{id:int}", async (int id, JsonElement b, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            if (await Access.MyRole(db, http, id, ct) != "manage") return VaultModule.Forbid("Only the vault's managers rename it.");
            var name = (VaultModule.Str(b, "name") ?? "").Trim(); if (name == "" || name.Length > 120) return VaultModule.Bad("bad_input", "Give the vault a name (up to 120 characters).");
            await using var c = new NpgsqlCommand("update vault.vaults set name = @n where id = @v and kind <> 'personal'", db); VaultModule.P(c, "n", name); VaultModule.P(c, "v", id);
            if (await c.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            await audit.WriteAsync("vault.renamed", id.ToString(), name, ct: ct);
            return Results.Ok(new { ok = true });
        }), key);

        VaultModule.Use(r.MapDelete("/api/vault/vaults/{id:int}", async (int id, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            var role = await Access.MyRole(db, http, id, ct);
            if (role != "manage" && VaultModule.Role(http.User) != "admin") return VaultModule.Forbid("Only the vault's managers or an administrator delete it.");
            await using var c = new NpgsqlCommand("delete from vault.vaults where id = @v and kind <> 'personal' returning name", db); VaultModule.P(c, "v", id);
            if (await c.ExecuteScalarAsync(ct) is not string name) return Results.NotFound();
            await audit.WriteAsync("vault.deleted", id.ToString(), name, ct: ct);
            return Results.Ok(new { ok = true });
        }), key);

        // members: managers of the vault, and administrators (so a vault whose managers left can get new ones)
        VaultModule.Use(r.MapGet("/api/vault/vaults/{id:int}/members", async (int id, HttpContext http, ITenantContext t, IUserDirectory users, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            var role = await Access.MyRole(db, http, id, ct);
            if (role == null && VaultModule.Role(http.User) != "admin") return Results.NotFound();
            var access = http.RequestServices.GetRequiredService<IAccess>();
            var gnames = (await access.GroupsAsync(ct)).ToDictionary(g => g.Id, g => g.Name);
            var ulist = (await users.ListAsync(ct)).ToDictionary(u => u.Id);
            var grants = new List<object>();
            await using (var c = new NpgsqlCommand("select ptype, pid, role from vault.members where vault_id = @v order by ptype, role desc", db))
            {
                VaultModule.P(c, "v", id); await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                {
                    var type = rd.GetString(0); var pid = rd.GetGuid(1);
                    grants.Add(new { type, id = pid, role = rd.GetString(2), name = type == "group" ? gnames.GetValueOrDefault(pid, "Removed group") : ulist.TryGetValue(pid, out var u) ? u.Name : "Removed user", email = type == "user" && ulist.TryGetValue(pid, out var u2) ? u2.Email : "" });
                }
            }
            var entitled = await Access.Entitled(db, access, id, ct);
            var held = new Dictionary<Guid, (int Version, bool Full)>(); int kv;
            await using (var c = new NpgsqlCommand("select key_version from vault.vaults where id = @v", db)) { VaultModule.P(c, "v", id); kv = (int)(await c.ExecuteScalarAsync(ct))!; }
            await using (var c = new NpgsqlCommand("select user_id, key_version, wrapped_key is not null from vault.vault_keys where vault_id = @v", db))
            { VaultModule.P(c, "v", id); await using var rd = await c.ExecuteReaderAsync(ct); while (await rd.ReadAsync(ct)) held[rd.GetGuid(0)] = (rd.GetInt32(1), rd.GetBoolean(2)); }
            var setUp = new HashSet<Guid>();
            await using (var c = new NpgsqlCommand("select user_id from vault.keys", db)) { await using var rd = await c.ExecuteReaderAsync(ct); while (await rd.ReadAsync(ct)) setUp.Add(rd.GetGuid(0)); }
            var people = entitled.Select(e => new
            {
                id = e.Key, name = ulist.TryGetValue(e.Key, out var u) ? u.Name : "Removed user", role = e.Value,
                state = !setUp.Contains(e.Key) ? "not_set_up" : held.TryGetValue(e.Key, out var h) && h.Version == kv && (h.Full || e.Value == "request") ? "has_key" : "waiting",
            }).OrderBy(p => p.name);
            return Results.Ok(new { grants, people, canManage = role == "manage", keyVersion = kv });
        }), key);

        VaultModule.Use(r.MapPut("/api/vault/vaults/{id:int}/members", async (int id, JsonElement b, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            var role = await Access.MyRole(db, http, id, ct); var admin = VaultModule.Role(http.User) == "admin";
            if (role != "manage" && !admin) return VaultModule.Forbid("Only the vault's managers or an administrator change its members.");
            string kind;
            await using (var c = new NpgsqlCommand("select kind from vault.vaults where id = @v", db)) { VaultModule.P(c, "v", id); if (await c.ExecuteScalarAsync(ct) is not string k) return Results.NotFound(); kind = k; }
            if (kind == "personal") return VaultModule.Bad("personal", "A personal vault is not shared. Use a send link or move the item to a shared vault.");
            if (b.ValueKind != JsonValueKind.Object || !b.TryGetProperty("members", out var ms) || ms.ValueKind != JsonValueKind.Array) return VaultModule.Bad("bad_input", "members is needed");
            var list = new List<(string T, Guid P, string R)>();
            foreach (var m in ms.EnumerateArray())
            {
                var type = VaultModule.Str(m, "type"); var r2 = VaultModule.Str(m, "role");
                if (type is not ("user" or "group") || !Guid.TryParse(VaultModule.Str(m, "id"), out var pid) || VaultModule.Rank(r2) == 0) return VaultModule.Bad("bad_input", "Each member needs type (user or group), id and role (request, view, edit or manage).");
                list.Add((type, pid, r2!));
            }
            if (!list.Any(l => l.R == "manage")) return VaultModule.Bad("no_manager", "Keep at least one manager: without one, nobody can give the vault's key to new members.");
            if (list.Select(l => (l.T, l.P)).Distinct().Count() != list.Count) return VaultModule.Bad("bad_input", "A member is listed twice.");
            var access = http.RequestServices.GetRequiredService<IAccess>();
            var before = await Access.Entitled(db, access, id, ct);
            await using var tx = await db.BeginTransactionAsync(ct);
            await using (var c = new NpgsqlCommand("delete from vault.members where vault_id = @v", db, tx)) { VaultModule.P(c, "v", id); await c.ExecuteNonQueryAsync(ct); }
            foreach (var l in list)
                await using (var c = new NpgsqlCommand("insert into vault.members (vault_id, ptype, pid, role) values (@v, @t, @p, @r)", db, tx)) { VaultModule.P(c, "v", id); VaultModule.P(c, "t", l.T); VaultModule.P(c, "p", l.P); VaultModule.P(c, "r", l.R); await c.ExecuteNonQueryAsync(ct); }
            await tx.CommitAsync(ct);
            var after = await Access.Entitled(db, access, id, ct);
            // people who lost access, or went down to "request", give back their keys; anyone who held the item key could have kept a copy: rotate
            var removed = before.Keys.Where(u => !after.ContainsKey(u)).ToList();
            var downToRequest = after.Where(a => a.Value == "request" && before.TryGetValue(a.Key, out var old) && old != "request").Select(a => a.Key).ToList();
            var rotate = false;
            if (removed.Count > 0)
                await using (var c = new NpgsqlCommand("delete from vault.vault_keys where vault_id = @v and user_id = any(@u) returning wrapped_key is not null", db))
                { VaultModule.P(c, "v", id); VaultModule.P(c, "u", removed.ToArray()); await using var rd = await c.ExecuteReaderAsync(ct); while (await rd.ReadAsync(ct)) rotate |= rd.GetBoolean(0); }
            if (downToRequest.Count > 0)
                await using (var c = new NpgsqlCommand("update vault.vault_keys set wrapped_key = null where vault_id = @v and user_id = any(@u) and wrapped_key is not null", db))
                { VaultModule.P(c, "v", id); VaultModule.P(c, "u", downToRequest.ToArray()); rotate |= await c.ExecuteNonQueryAsync(ct) > 0; }
            if (rotate) await using (var c = new NpgsqlCommand("update vault.vaults set needs_rotation = true where id = @v", db)) { VaultModule.P(c, "v", id); await c.ExecuteNonQueryAsync(ct); }
            await Access.Event(db, id, null, VaultModule.Actor(http.User), "members", $"{list.Count} members; {removed.Count} people removed", ct);
            await audit.WriteAsync("vault.members_changed", id.ToString(), string.Join(", ", list.Select(l => $"{l.T} {l.P}: {l.R}")), ct: ct);
            return Results.Ok(new { ok = true, needsRotation = rotate });
        }), key);

        // people who should hold a vault's keys but do not yet, in vaults where I am a manager and hold the keys
        VaultModule.Use(r.MapGet("/api/vault/pending", async (HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            var me = VaultModule.Me(http.User);
            await using var db = await VaultModule.Open(t, ct);
            var roles = await Access.MyRoles(db, http, ct);
            var access = http.RequestServices.GetRequiredService<IAccess>();
            var keys = new Dictionary<Guid, string>();
            await using (var c = new NpgsqlCommand("select user_id, public_key from vault.keys", db)) { await using var rd = await c.ExecuteReaderAsync(ct); while (await rd.ReadAsync(ct)) keys[rd.GetGuid(0)] = rd.GetString(1); }
            var o = new List<object>();
            foreach (var (vid, role) in roles.Where(x => x.Value == "manage"))
            {
                int kv; bool mine;
                await using (var c = new NpgsqlCommand("select v.key_version, exists(select 1 from vault.vault_keys k where k.vault_id = v.id and k.user_id = @u and k.key_version = v.key_version and k.wrapped_key is not null) from vault.vaults v where v.id = @v and v.kind <> 'personal'", db))
                { VaultModule.P(c, "v", vid); VaultModule.P(c, "u", me); await using var rd = await c.ExecuteReaderAsync(ct); if (!await rd.ReadAsync(ct)) continue; kv = rd.GetInt32(0); mine = rd.GetBoolean(1); }
                if (!mine) continue;
                var held = new Dictionary<Guid, (int V, bool Full)>();
                await using (var c = new NpgsqlCommand("select user_id, key_version, wrapped_key is not null from vault.vault_keys where vault_id = @v", db))
                { VaultModule.P(c, "v", vid); await using var rd = await c.ExecuteReaderAsync(ct); while (await rd.ReadAsync(ct)) held[rd.GetGuid(0)] = (rd.GetInt32(1), rd.GetBoolean(2)); }
                foreach (var (uid, urole) in await Access.Entitled(db, access, vid, ct))
                {
                    if (!keys.TryGetValue(uid, out var pub)) continue; // not set up yet
                    var full = urole != "request";
                    if (held.TryGetValue(uid, out var h) && h.V == kv && (h.Full || !full)) continue;
                    o.Add(new { vaultId = vid, userId = uid, publicKey = pub, full, keyVersion = kv });
                }
            }
            return Results.Ok(new { items = o });
        }), key);

        // a manager's browser hands over wrapped keys; requesters only get the directory key
        VaultModule.Use(r.MapPost("/api/vault/vaults/{id:int}/keys", async (int id, JsonElement b, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            if (await Access.MyRole(db, http, id, ct) != "manage") return VaultModule.Forbid("Only the vault's managers give out its keys.");
            if (b.ValueKind != JsonValueKind.Object || !b.TryGetProperty("keys", out var ks) || ks.ValueKind != JsonValueKind.Array) return VaultModule.Bad("bad_input", "keys is needed");
            var entitled = await Access.Entitled(db, http.RequestServices.GetRequiredService<IAccess>(), id, ct);
            int kv; await using (var c = new NpgsqlCommand("select key_version from vault.vaults where id = @v", db)) { VaultModule.P(c, "v", id); kv = (int)(await c.ExecuteScalarAsync(ct))!; }
            var n = 0;
            foreach (var k in ks.EnumerateArray().Take(500))
            {
                if (!Guid.TryParse(VaultModule.Str(k, "userId"), out var uid) || !entitled.TryGetValue(uid, out var urole)) continue;
                if ((VaultModule.Int(k, "keyVersion") ?? 0) != kv) return Results.Conflict(new { error = "key_version", detail = "The vault's key changed meanwhile. Reload and try again." });
                var wk = VaultModule.Str(k, "wrappedKey"); var wd = VaultModule.Str(k, "wrappedDirKey");
                if (!VaultModule.Blob(wd, 4000) || (urole != "request" && !VaultModule.Blob(wk, 4000))) return VaultModule.Bad("bad_input", "Each key needs wrappedDirKey, and wrappedKey unless the person may only ask for access.");
                await using var c = new NpgsqlCommand(@"insert into vault.vault_keys (vault_id, user_id, key_version, wrapped_key, wrapped_dir_key, granted_by) values (@v, @u, @kv, @k, @d, @b)
                    on conflict (vault_id, user_id) do update set key_version = excluded.key_version, wrapped_key = excluded.wrapped_key, wrapped_dir_key = excluded.wrapped_dir_key, granted_by = excluded.granted_by, at = now()", db);
                VaultModule.P(c, "v", id); VaultModule.P(c, "u", uid); VaultModule.P(c, "kv", kv); VaultModule.P(c, "k", urole == "request" ? null : wk); VaultModule.P(c, "d", wd); VaultModule.P(c, "b", VaultModule.Actor(http.User));
                n += await c.ExecuteNonQueryAsync(ct);
            }
            if (n > 0) await Access.Event(db, id, null, VaultModule.Actor(http.User), "keys_given", $"{n} people received the vault's keys", ct);
            return Results.Ok(new { given = n });
        }), key);

        // new vault keys: every item, folder and member key at once (after someone lost access)
        VaultModule.Use(r.MapPost("/api/vault/vaults/{id:int}/rotate", async (int id, JsonElement b, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            if (await Access.MyRole(db, http, id, ct) != "manage") return VaultModule.Forbid("Only the vault's managers change its keys.");
            var nv = VaultModule.Int(b, "keyVersion") ?? 0;
            int kv; await using (var c = new NpgsqlCommand("select key_version from vault.vaults where id = @v", db)) { VaultModule.P(c, "v", id); kv = (int)(await c.ExecuteScalarAsync(ct))!; }
            if (nv != kv + 1) return Results.Conflict(new { error = "key_version", detail = "The vault's key changed meanwhile. Reload and try again." });
            var items = b.TryGetProperty("items", out var iv) && iv.ValueKind == JsonValueKind.Array ? iv.EnumerateArray().ToList() : new();
            var folders = b.TryGetProperty("folders", out var fv) && fv.ValueKind == JsonValueKind.Array ? fv.EnumerateArray().ToList() : new();
            var keys = b.TryGetProperty("keys", out var kvv) && kvv.ValueKind == JsonValueKind.Array ? kvv.EnumerateArray().ToList() : new();
            var history = b.TryGetProperty("history", out var hv) && hv.ValueKind == JsonValueKind.Array ? hv.EnumerateArray().ToList() : new();
            var have = new HashSet<long>(); var haveF = new HashSet<int>(); var haveH = new HashSet<(long, int)>();
            await using (var c = new NpgsqlCommand("select id from vault.items where vault_id = @v", db)) { VaultModule.P(c, "v", id); await using var rd = await c.ExecuteReaderAsync(ct); while (await rd.ReadAsync(ct)) have.Add(rd.GetInt64(0)); }
            await using (var c = new NpgsqlCommand("select id from vault.folders where vault_id = @v", db)) { VaultModule.P(c, "v", id); await using var rd = await c.ExecuteReaderAsync(ct); while (await rd.ReadAsync(ct)) haveF.Add(rd.GetInt32(0)); }
            await using (var c = new NpgsqlCommand("select h.item_id, h.revision from vault.history h join vault.items i on i.id = h.item_id where i.vault_id = @v", db)) { VaultModule.P(c, "v", id); await using var rd = await c.ExecuteReaderAsync(ct); while (await rd.ReadAsync(ct)) haveH.Add((rd.GetInt64(0), rd.GetInt32(1))); }
            if (!have.SetEquals(items.Select(i => VaultModule.Long(i, "id") ?? 0)) || !haveF.SetEquals(folders.Select(f => VaultModule.Int(f, "id") ?? 0)) || !haveH.SetEquals(history.Select(h => (VaultModule.Long(h, "itemId") ?? 0, VaultModule.Int(h, "revision") ?? 0))))
                return Results.Conflict(new { error = "incomplete", detail = "Every item, folder and old version must be re-encrypted together. Reload and try again." });
            var me = VaultModule.Me(http.User);
            if (!keys.Any(k => VaultModule.Str(k, "userId") == me.ToString())) return VaultModule.Bad("bad_input", "Include the new key wrapped for yourself.");
            var entitled = await Access.Entitled(db, http.RequestServices.GetRequiredService<IAccess>(), id, ct);
            await using var tx = await db.BeginTransactionAsync(ct);
            foreach (var i in items)
            {
                if (!VaultModule.Blob(VaultModule.Str(i, "dataEnc"), 70000) || !VaultModule.Blob(VaultModule.Str(i, "titleEnc"), 3000)) return VaultModule.Bad("bad_input", "An item is not encrypted properly.");
                await using var c = new NpgsqlCommand("update vault.items set data_enc = @d, title_enc = @t, key_version = @kv where id = @i", db, tx);
                VaultModule.P(c, "d", VaultModule.Str(i, "dataEnc")); VaultModule.P(c, "t", VaultModule.Str(i, "titleEnc")); VaultModule.P(c, "kv", nv); VaultModule.P(c, "i", VaultModule.Long(i, "id")); await c.ExecuteNonQueryAsync(ct);
            }
            foreach (var h in history)
            {
                if (!VaultModule.Blob(VaultModule.Str(h, "dataEnc"), 70000)) return VaultModule.Bad("bad_input", "An old version is not encrypted properly.");
                await using var c = new NpgsqlCommand("update vault.history set data_enc = @d, key_version = @kv where item_id = @i and revision = @r", db, tx);
                VaultModule.P(c, "d", VaultModule.Str(h, "dataEnc")); VaultModule.P(c, "kv", nv); VaultModule.P(c, "i", VaultModule.Long(h, "itemId")); VaultModule.P(c, "r", VaultModule.Int(h, "revision")); await c.ExecuteNonQueryAsync(ct);
            }
            foreach (var f in folders)
            {
                if (!VaultModule.Blob(VaultModule.Str(f, "nameEnc"), 2000)) return VaultModule.Bad("bad_input", "A folder name is not encrypted properly.");
                await using var c = new NpgsqlCommand("update vault.folders set name_enc = @n, key_version = @kv where id = @i", db, tx);
                VaultModule.P(c, "n", VaultModule.Str(f, "nameEnc")); VaultModule.P(c, "kv", nv); VaultModule.P(c, "i", VaultModule.Int(f, "id")); await c.ExecuteNonQueryAsync(ct);
            }
            await using (var c = new NpgsqlCommand("delete from vault.vault_keys where vault_id = @v", db, tx)) { VaultModule.P(c, "v", id); await c.ExecuteNonQueryAsync(ct); }
            foreach (var k in keys)
            {
                if (!Guid.TryParse(VaultModule.Str(k, "userId"), out var uid) || !entitled.TryGetValue(uid, out var urole)) continue;
                var wk = VaultModule.Str(k, "wrappedKey"); var wd = VaultModule.Str(k, "wrappedDirKey");
                if (!VaultModule.Blob(wd, 4000) || (urole != "request" && !VaultModule.Blob(wk, 4000))) continue;
                await using var c = new NpgsqlCommand("insert into vault.vault_keys (vault_id, user_id, key_version, wrapped_key, wrapped_dir_key, granted_by) values (@v, @u, @kv, @k, @d, @b)", db, tx);
                VaultModule.P(c, "v", id); VaultModule.P(c, "u", uid); VaultModule.P(c, "kv", nv); VaultModule.P(c, "k", urole == "request" ? null : wk); VaultModule.P(c, "d", wd); VaultModule.P(c, "b", VaultModule.Actor(http.User)); await c.ExecuteNonQueryAsync(ct);
            }
            await using (var c = new NpgsqlCommand("update vault.vaults set key_version = @kv, needs_rotation = false where id = @v", db, tx)) { VaultModule.P(c, "kv", nv); VaultModule.P(c, "v", id); await c.ExecuteNonQueryAsync(ct); }
            await tx.CommitAsync(ct);
            await Access.Event(db, id, null, VaultModule.Actor(http.User), "rotated", $"new keys (version {nv}); {items.Count} items re-encrypted", ct);
            await audit.WriteAsync("vault.key_rotated", id.ToString(), $"version {nv}, {items.Count} items", ct: ct);
            return Results.Ok(new { ok = true });
        }), key);

        // folders (names encrypted with the item key)
        VaultModule.Use(r.MapGet("/api/vault/vaults/{id:int}/folders", async (int id, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            if (VaultModule.Rank(await Access.MyRole(db, http, id, ct)) < 2) return Results.NotFound();
            await using var c = new NpgsqlCommand("select id, name_enc, key_version from vault.folders where vault_id = @v order by id", db); VaultModule.P(c, "v", id);
            var o = new List<object>(); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) o.Add(new { id = rd.GetInt32(0), nameEnc = rd.GetString(1), keyVersion = rd.GetInt32(2) });
            return Results.Ok(new { items = o });
        }), key);

        VaultModule.Use(r.MapPost("/api/vault/vaults/{id:int}/folders", async (int id, JsonElement b, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            if (VaultModule.Rank(await Access.MyRole(db, http, id, ct)) < 3) return VaultModule.Forbid("You may not change this vault.");
            var n = VaultModule.Str(b, "nameEnc"); if (!VaultModule.Blob(n, 2000)) return VaultModule.Bad("bad_input", "nameEnc is needed");
            await using var c = new NpgsqlCommand("insert into vault.folders (vault_id, name_enc, key_version) select @v, @n, key_version from vault.vaults where id = @v returning id", db);
            VaultModule.P(c, "v", id); VaultModule.P(c, "n", n);
            return Results.Ok(new { id = (int)(await c.ExecuteScalarAsync(ct))! });
        }), key);

        VaultModule.Use(r.MapPut("/api/vault/folders/{fid:int}", async (int fid, JsonElement b, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            int vid; await using (var q = new NpgsqlCommand("select vault_id from vault.folders where id = @f", db)) { VaultModule.P(q, "f", fid); if (await q.ExecuteScalarAsync(ct) is not int v) return Results.NotFound(); vid = v; }
            if (VaultModule.Rank(await Access.MyRole(db, http, vid, ct)) < 3) return VaultModule.Forbid("You may not change this vault.");
            var n = VaultModule.Str(b, "nameEnc"); if (!VaultModule.Blob(n, 2000)) return VaultModule.Bad("bad_input", "nameEnc is needed");
            await using var c = new NpgsqlCommand("update vault.folders set name_enc = @n where id = @f", db); VaultModule.P(c, "n", n); VaultModule.P(c, "f", fid); await c.ExecuteNonQueryAsync(ct);
            return Results.Ok(new { ok = true });
        }), key);

        VaultModule.Use(r.MapDelete("/api/vault/folders/{fid:int}", async (int fid, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            int vid; await using (var q = new NpgsqlCommand("select vault_id from vault.folders where id = @f", db)) { VaultModule.P(q, "f", fid); if (await q.ExecuteScalarAsync(ct) is not int v) return Results.NotFound(); vid = v; }
            if (VaultModule.Rank(await Access.MyRole(db, http, vid, ct)) < 3) return VaultModule.Forbid("You may not change this vault.");
            await using var c = new NpgsqlCommand("delete from vault.folders where id = @f", db); VaultModule.P(c, "f", fid); await c.ExecuteNonQueryAsync(ct);
            return Results.Ok(new { ok = true });
        }), key);

        VaultModule.Use(r.MapGet("/api/vault/vaults/{id:int}/events", async (int id, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            if (VaultModule.Rank(await Access.MyRole(db, http, id, ct)) < 3) return Results.NotFound();
            await using var c = new NpgsqlCommand("select id, item_id, user_name, action, detail, at from vault.events where vault_id = @v order by id desc limit 200", db); VaultModule.P(c, "v", id);
            var o = new List<object>(); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) o.Add(new { id = rd.GetInt64(0), itemId = rd.IsDBNull(1) ? (long?)null : rd.GetInt64(1), user = rd.GetString(2), action = rd.GetString(3), detail = rd.GetString(4), at = rd.GetDateTime(5) });
            return Results.Ok(new { items = o });
        }), key);
    }
}
