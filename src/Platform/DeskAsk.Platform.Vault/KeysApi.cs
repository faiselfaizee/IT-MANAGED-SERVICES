using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DeskAsk.Platform.Vault;

/// <summary>
/// A person's key record: public key, private key encrypted with the master password, and the same private key encrypted with the
/// recovery code. None of these let the server read anything.
/// </summary>
public static class KeysApi
{
    static string? Kdf(JsonElement b)
    {
        if (b.ValueKind != JsonValueKind.Object || !b.TryGetProperty("kdf", out var k) || k.ValueKind != JsonValueKind.Object) return null;
        var alg = VaultModule.Str(k, "alg"); var iter = VaultModule.Int(k, "iterations") ?? 0; var salt = VaultModule.Str(k, "salt") ?? "";
        if (alg != "pbkdf2-sha256" || iter < 600_000 || iter > 10_000_000 || salt.Length < 16 || salt.Length > 64) return null;
        return JsonSerializer.Serialize(new { alg, iterations = iter, salt });
    }

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        VaultModule.Use(r.MapGet("/api/vault/me", async (HttpContext http, ITenantContext t, ISettings s, CancellationToken ct) =>
        {
            var me = VaultModule.Me(http.User);
            await using var db = await VaultModule.Open(t, ct);
            var settings = await VaultSettings.LoadAsync(s, ct);
            await using var c = new NpgsqlCommand("select public_key, wrapped_private, kdf from vault.keys where user_id = @u", db); VaultModule.P(c, "u", me);
            await using var rd = await c.ExecuteReaderAsync(ct);
            var role = VaultModule.Role(http.User);
            var canCreate = role == "admin" || (settings.Creators == "engineers" && role == "engineer");
            var common = new { userId = me, name = VaultModule.Actor(http.User), role, canCreate, autoLockMinutes = settings.AutoLockMinutes, breachCheck = settings.BreachCheck, requestMaxMinutes = settings.RequestMaxMinutes };
            if (!await rd.ReadAsync(ct)) return Results.Ok(new { setUp = false, settings = common });
            return Results.Ok(new { setUp = true, publicKey = rd.GetString(0), wrappedPrivate = rd.GetString(1), kdf = JsonDocument.Parse(rd.GetString(2)).RootElement, settings = common });
        }), key);

        // first set-up, or a new start after losing both the master password and the recovery code (reset: true)
        VaultModule.Use(r.MapPost("/api/vault/keys", async (JsonElement b, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var me = VaultModule.Me(http.User); if (me == Guid.Empty) return VaultModule.Forbid("Sign in again.");
            var pub = VaultModule.Str(b, "publicKey") ?? ""; var priv = VaultModule.Str(b, "wrappedPrivate"); var rec = VaultModule.Str(b, "wrappedRecovery"); var kdf = Kdf(b);
            if (pub.Length < 300 || pub.Length > 2000 || !VaultModule.Blob(priv, 10000) || !VaultModule.Blob(rec, 10000) || kdf == null)
                return VaultModule.Bad("bad_input", "The key set-up is incomplete (public key, both wrapped private keys and the key derivation settings are needed; at least 600 000 rounds).");
            var reset = VaultModule.Bool(b, "reset");
            await using var db = await VaultModule.Open(t, ct);
            await using var tx = await db.BeginTransactionAsync(ct);
            bool exists;
            await using (var c = new NpgsqlCommand("select 1 from vault.keys where user_id = @u", db, tx)) { VaultModule.P(c, "u", me); exists = await c.ExecuteScalarAsync(ct) != null; }
            if (exists && !reset) return Results.Conflict(new { error = "already_set_up", detail = "Your vault is already set up. Unlock it with your master password or recovery code." });
            if (exists)
            {
                // the old keys are gone with the password: shared vaults are given again by their managers, the personal vault cannot be read any more
                await using var d = new NpgsqlCommand(@"delete from vault.vault_keys where user_id = @u;
                    update vault.items set deleted_at = now() where deleted_at is null and vault_id in (select id from vault.vaults where kind = 'personal' and owner_user = @u);
                    delete from vault.vaults where kind = 'personal' and owner_user = @u;
                    delete from vault.grants where user_id = @u", db, tx);
                VaultModule.P(d, "u", me); await d.ExecuteNonQueryAsync(ct);
            }
            await using (var c = new NpgsqlCommand(@"insert into vault.keys (user_id, public_key, wrapped_private, wrapped_recovery, kdf) values (@u, @p, @w, @r, @k)
                on conflict (user_id) do update set public_key = excluded.public_key, wrapped_private = excluded.wrapped_private, wrapped_recovery = excluded.wrapped_recovery, kdf = excluded.kdf, updated_at = now()", db, tx))
            { VaultModule.P(c, "u", me); VaultModule.P(c, "p", pub); VaultModule.P(c, "w", priv); VaultModule.P(c, "r", rec); VaultModule.P(c, "k", kdf); await c.ExecuteNonQueryAsync(ct); }
            await tx.CommitAsync(ct);
            await audit.WriteAsync(exists ? "vault.keys_reset" : "vault.keys_created", me.ToString(), exists ? "new keys after losing the master password and recovery code; personal vault emptied" : "", ct: ct);
            return Results.Ok(new { ok = true });
        }), key);

        // a new master password (the browser re-encrypts the private key); also used after unlocking with the recovery code
        VaultModule.Use(r.MapPut("/api/vault/keys/password", async (JsonElement b, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var me = VaultModule.Me(http.User); var priv = VaultModule.Str(b, "wrappedPrivate"); var kdf = Kdf(b);
            if (!VaultModule.Blob(priv, 10000) || kdf == null) return VaultModule.Bad("bad_input", "wrappedPrivate and kdf (at least 600 000 rounds) are needed.");
            await using var db = await VaultModule.Open(t, ct);
            await using var c = new NpgsqlCommand("update vault.keys set wrapped_private = @w, kdf = @k, updated_at = now() where user_id = @u", db);
            VaultModule.P(c, "u", me); VaultModule.P(c, "w", priv); VaultModule.P(c, "k", kdf);
            if (await c.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            await audit.WriteAsync(VaultModule.Bool(b, "viaRecovery") ? "vault.recovery_used" : "vault.master_password_changed", me.ToString(), ct: ct);
            return Results.Ok(new { ok = true });
        }), key);

        VaultModule.Use(r.MapPut("/api/vault/keys/recovery", async (JsonElement b, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var me = VaultModule.Me(http.User); var rec = VaultModule.Str(b, "wrappedRecovery");
            if (!VaultModule.Blob(rec, 10000)) return VaultModule.Bad("bad_input", "wrappedRecovery is needed.");
            await using var db = await VaultModule.Open(t, ct);
            await using var c = new NpgsqlCommand("update vault.keys set wrapped_recovery = @r, updated_at = now() where user_id = @u", db);
            VaultModule.P(c, "u", me); VaultModule.P(c, "r", rec);
            if (await c.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            await audit.WriteAsync("vault.recovery_code_changed", me.ToString(), ct: ct);
            return Results.Ok(new { ok = true });
        }), key);

        VaultModule.Use(r.MapGet("/api/vault/keys/recovery", async (HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            var me = VaultModule.Me(http.User);
            await using var db = await VaultModule.Open(t, ct);
            await using var c = new NpgsqlCommand("select wrapped_recovery from vault.keys where user_id = @u", db); VaultModule.P(c, "u", me);
            return await c.ExecuteScalarAsync(ct) is string w ? Results.Ok(new { wrappedRecovery = w }) : Results.NotFound();
        }), key);

        // public keys, so a manager can give someone a vault key
        VaultModule.Use(r.MapGet("/api/vault/users", async (HttpContext http, ITenantContext t, IUserDirectory users, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            var keys = new Dictionary<Guid, string>();
            await using (var c = new NpgsqlCommand("select user_id, public_key from vault.keys", db)) { await using var rd = await c.ExecuteReaderAsync(ct); while (await rd.ReadAsync(ct)) keys[rd.GetGuid(0)] = rd.GetString(1); }
            var groups = await http.RequestServices.GetRequiredService<IAccess>().GroupsAsync(ct);
            return Results.Ok(new
            {
                users = (await users.ListAsync(ct)).Where(u => u.Active && u.Role != "customer").Select(u => new { id = u.Id, name = u.Name, email = u.Email, role = u.Role, setUp = keys.ContainsKey(u.Id), publicKey = keys.GetValueOrDefault(u.Id) }),
                groups = groups.Select(g => new { id = g.Id, name = g.Name }),
            });
        }), key);
    }
}
