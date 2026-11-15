using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DeskAsk.Platform.Vault;

/// <summary>
/// Managed accounts: the one place the server holds a password it can read, so the agent on the asset can change it on a schedule
/// (Windows local or domain accounts, Linux accounts). Every read goes through the server and is logged.
/// </summary>
public static class ManagedApi
{
    public const string Purpose = "dk.vault.managed";
    internal static IDataProtector Protector(IServiceProvider sp, ITenantContext t) => sp.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose + ":" + t.Require().Id);

    internal static async Task<bool> HasGrant(NpgsqlConnection db, long itemId, Guid user, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("select 1 from vault.grants where item_id = @i and user_id = @u and expires_at > now() limit 1", db);
        VaultModule.P(c, "i", itemId); VaultModule.P(c, "u", user);
        return await c.ExecuteScalarAsync(ct) != null;
    }

    static object Row(NpgsqlDataReader rd, Dictionary<long, string> assetNames) => new
    {
        itemId = rd.GetInt64(0), account = rd.GetString(1), target = rd.GetString(2), assetId = rd.GetInt64(3), asset = assetNames.GetValueOrDefault(rd.GetInt64(3), ""),
        rotateDays = rd.GetInt32(4), resetAfterUse = rd.GetBoolean(5), nextAt = rd.IsDBNull(6) ? (DateTime?)null : rd.GetDateTime(6),
        lastRotatedAt = rd.IsDBNull(7) ? (DateTime?)null : rd.GetDateTime(7), lastError = rd.GetString(8), state = rd.GetString(9),
        vaultId = rd.GetInt32(10), titleEnc = rd.GetString(11), startedAt = rd.IsDBNull(12) ? (DateTime?)null : rd.GetDateTime(12),
    };
    const string Sel = "select m.item_id, m.account, m.target, m.asset_id, m.rotate_days, m.reset_after_use, m.next_at, m.last_rotated_at, m.last_error, m.state, i.vault_id, i.title_enc, m.started_at from vault.managed m join vault.items i on i.id = m.item_id";

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        // the password of a managed account, for members who may read the item or hold an approved request
        VaultModule.Use(r.MapGet("/api/vault/items/{id:long}/secret", async (long id, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            if (await ItemsApi.FindAsync(db, id, ct) is not { Managed: true } it) return Results.NotFound();
            var me = VaultModule.Me(http.User); var role = await Access.MyRole(db, http, it.VaultId, ct);
            var viaGrant = !ItemsApi.CanRead(role, it.RequiresApproval);
            if (role == null || (viaGrant && !await HasGrant(db, id, me, ct))) return VaultModule.Forbid(role == null ? "This item is not in your vaults." : "Ask for access first.");
            string account, sealedPw, state; bool reset;
            await using (var c = new NpgsqlCommand("select account, secret_sealed, state, reset_after_use from vault.managed where item_id = @i", db))
            { VaultModule.P(c, "i", id); await using var rd = await c.ExecuteReaderAsync(ct); if (!await rd.ReadAsync(ct)) return Results.NotFound(); account = rd.GetString(0); sealedPw = rd.GetString(1); state = rd.GetString(2); reset = rd.GetBoolean(3); }
            var p = Protector(http.RequestServices, t);
            string? maybe = null;
            if (state == "unknown")
                await using (var c = new NpgsqlCommand("select secret_sealed from vault.managed_history where item_id = @i and note = 'maybe' order by id desc limit 1", db))
                { VaultModule.P(c, "i", id); if (await c.ExecuteScalarAsync(ct) is string s) maybe = p.Unprotect(s); }
            // "change after each use": a member's read is followed by a change an hour later (a grant's read, when the grant ends)
            if (reset && !viaGrant)
                await using (var c = new NpgsqlCommand("update vault.managed set next_at = least(coalesce(next_at, 'infinity'), now() + interval '1 hour') where item_id = @i", db)) { VaultModule.P(c, "i", id); await c.ExecuteNonQueryAsync(ct); }
            var by = VaultModule.Actor(http.User);
            await Access.Event(db, it.VaultId, id, by, "revealed", viaGrant ? "managed password, through an approved request" : "managed password", ct);
            await audit.WriteAsync("vault.managed_password_read", id.ToString(), $"{account}{(viaGrant ? " (approved request)" : "")}", ct: ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { account, password = p.Unprotect(sealedPw), state, possiblePassword = maybe });
        }), key);

        // make an item a managed account (the browser then removes the password from the encrypted item)
        VaultModule.Use(r.MapPost("/api/vault/items/{id:long}/managed", async (long id, JsonElement b, HttpContext http, ITenantContext t, ISettings s, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            if (await ItemsApi.FindAsync(db, id, ct) is not { } it) return Results.NotFound();
            if (VaultModule.Rank(await Access.MyRole(db, http, it.VaultId, ct)) < 3) return VaultModule.Forbid("Only the vault's editors and managers manage accounts.");
            if (it.Managed) return Results.Conflict(new { error = "managed", detail = "This account is managed already." });
            if (it.AssetId == null) return VaultModule.Bad("no_asset", "Link the item to the asset whose agent will change the password.");
            await using (var k = new NpgsqlCommand("select kind from vault.vaults where id = @v", db)) { VaultModule.P(k, "v", it.VaultId); if (await k.ExecuteScalarAsync(ct) as string == "personal") return VaultModule.Bad("personal", "Managed accounts live in shared vaults."); }
            var account = (VaultModule.Str(b, "account") ?? "").Trim(); var target = VaultModule.Str(b, "target"); var pw = VaultModule.Str(b, "password") ?? "";
            if (!System.Text.RegularExpressions.Regex.IsMatch(account, @"^[A-Za-z0-9][A-Za-z0-9._$ -]{0,63}$")) return VaultModule.Bad("bad_input", "Give the account name as Windows or Linux knows it (letters, digits, . _ - $ and spaces; no domain prefix).");
            if (target is not ("local" or "domain" or "linux")) return VaultModule.Bad("bad_input", "target must be local, domain or linux");
            if (pw == "" || pw.Length > 256) return VaultModule.Bad("bad_input", "Give the account's current password.");
            var settings = await VaultSettings.LoadAsync(s, ct);
            var days = Math.Clamp(VaultModule.Int(b, "rotateDays") ?? settings.DefaultRotateDays, 0, 365);
            var by = VaultModule.Actor(http.User);
            await using var tx = await db.BeginTransactionAsync(ct);
            await using (var c = new NpgsqlCommand(@"insert into vault.managed (item_id, account, target, asset_id, secret_sealed, rotate_days, reset_after_use, next_at)
                values (@i, @a, @t, @as, @s, @d, @r, case when @now then now() when @d > 0 then now() + make_interval(days => @d) end)", db, tx))
            {
                VaultModule.P(c, "i", id); VaultModule.P(c, "a", account); VaultModule.P(c, "t", target); VaultModule.P(c, "as", it.AssetId); VaultModule.P(c, "s", Protector(http.RequestServices, t).Protect(pw));
                VaultModule.P(c, "d", days); VaultModule.P(c, "r", VaultModule.Bool(b, "resetAfterUse")); VaultModule.P(c, "now", VaultModule.Bool(b, "rotateNow")); await c.ExecuteNonQueryAsync(ct);
            }
            await using (var c = new NpgsqlCommand("update vault.items set managed = true where id = @i", db, tx)) { VaultModule.P(c, "i", id); await c.ExecuteNonQueryAsync(ct); }
            await tx.CommitAsync(ct);
            await Access.Event(db, it.VaultId, id, by, "managed", $"{target} account {account}; changed every {days} days", ct);
            await audit.WriteAsync("vault.account_managed", id.ToString(), $"{target} account {account} on asset {it.AssetId}, every {days} days", ct: ct);
            return Results.Ok(new { ok = true });
        }), key);

        VaultModule.Use(r.MapPut("/api/vault/items/{id:long}/managed", async (long id, JsonElement b, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            if (await ItemsApi.FindAsync(db, id, ct) is not { Managed: true } it) return Results.NotFound();
            if (VaultModule.Rank(await Access.MyRole(db, http, it.VaultId, ct)) < 3) return VaultModule.Forbid("Only the vault's editors and managers change this.");
            var days = Math.Clamp(VaultModule.Int(b, "rotateDays") ?? 0, 0, 365);
            await using var c = new NpgsqlCommand(@"update vault.managed set rotate_days = @d, reset_after_use = @r,
                next_at = case when state = 'rotating' then next_at when @d > 0 then coalesce(last_rotated_at, now()) + make_interval(days => @d) else null end where item_id = @i", db);
            VaultModule.P(c, "d", days); VaultModule.P(c, "r", VaultModule.Bool(b, "resetAfterUse")); VaultModule.P(c, "i", id); await c.ExecuteNonQueryAsync(ct);
            await Access.Event(db, it.VaultId, id, VaultModule.Actor(http.User), "managed_changed", days > 0 ? $"changed every {days} days" : "no schedule", ct);
            return Results.Ok(new { ok = true });
        }), key);

        // stop managing: the server forgets the password and hands it back once, for the browser to put into the encrypted item
        VaultModule.Use(r.MapDelete("/api/vault/items/{id:long}/managed", async (long id, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            if (await ItemsApi.FindAsync(db, id, ct) is not { Managed: true } it) return Results.NotFound();
            if (VaultModule.Rank(await Access.MyRole(db, http, it.VaultId, ct)) < 3) return VaultModule.Forbid("Only the vault's editors and managers change this.");
            await using var tx = await db.BeginTransactionAsync(ct);
            string? sealedPw; string account;
            await using (var c = new NpgsqlCommand("delete from vault.managed where item_id = @i and state <> 'rotating' returning secret_sealed, account", db, tx))
            { VaultModule.P(c, "i", id); await using var rd = await c.ExecuteReaderAsync(ct); if (!await rd.ReadAsync(ct)) return Results.Conflict(new { error = "rotating", detail = "The password is being changed right now. Try again in a few minutes." }); sealedPw = rd.GetString(0); account = rd.GetString(1); }
            await using (var c = new NpgsqlCommand("delete from vault.managed_history where item_id = @i; update vault.items set managed = false where id = @i", db, tx)) { VaultModule.P(c, "i", id); await c.ExecuteNonQueryAsync(ct); }
            await tx.CommitAsync(ct);
            var by = VaultModule.Actor(http.User);
            await Access.Event(db, it.VaultId, id, by, "unmanaged", account, ct);
            await audit.WriteAsync("vault.account_unmanaged", id.ToString(), account, ct: ct);
            return Results.Ok(new { password = Protector(http.RequestServices, t).Unprotect(sealedPw) });
        }), key);

        VaultModule.Use(r.MapPost("/api/vault/items/{id:long}/rotate", async (long id, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            if (await ItemsApi.FindAsync(db, id, ct) is not { Managed: true } it) return Results.NotFound();
            if (VaultModule.Rank(await Access.MyRole(db, http, it.VaultId, ct)) < 3) return VaultModule.Forbid("Only the vault's editors and managers change the password.");
            var state = await Rotation.StartAsync(db, http.RequestServices, t, id, VaultModule.Actor(http.User), ct);
            return Results.Ok(new { state });
        }), key);

        VaultModule.Use(r.MapGet("/api/vault/items/{id:long}/managed", async (long id, HttpContext http, ITenantContext t, IAssetDirectory assets, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            if (await ItemsApi.FindAsync(db, id, ct) is not { Managed: true } it) return Results.NotFound();
            if (await Access.MyRole(db, http, it.VaultId, ct) == null) return Results.NotFound();
            var names = new Dictionary<long, string>();
            if (it.AssetId is long a && await assets.GetAsync(a, ct) is { } ar) names[a] = ar.Name;
            object? row = null;
            await using (var c = new NpgsqlCommand(Sel + " where m.item_id = @i", db)) { VaultModule.P(c, "i", id); await using var rd = await c.ExecuteReaderAsync(ct); if (await rd.ReadAsync(ct)) row = Row(rd, names); }
            var hist = new List<object>();
            await using (var c = new NpgsqlCommand("select id, at, by_name, note from vault.managed_history where item_id = @i order by id desc limit 50", db))
            { VaultModule.P(c, "i", id); await using var rd = await c.ExecuteReaderAsync(ct); while (await rd.ReadAsync(ct)) hist.Add(new { id = rd.GetInt64(0), at = rd.GetDateTime(1), by = rd.GetString(2), note = rd.GetString(3) }); }
            return row == null ? Results.NotFound() : Results.Ok(new { account = row, history = hist });
        }), key);

        // every managed account in my vaults, for the Managed accounts page
        VaultModule.Use(r.MapGet("/api/vault/managed", async (HttpContext http, ITenantContext t, IAssetDirectory assets, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            var roles = await Access.MyRoles(db, http, ct);
            var o = new List<object>();
            if (roles.Count == 0) return Results.Ok(new { items = o });
            var ids = new List<long>();
            await using (var c = new NpgsqlCommand("select distinct m.asset_id from vault.managed m join vault.items i on i.id = m.item_id where i.vault_id = any(@v) and i.deleted_at is null", db))
            { VaultModule.P(c, "v", roles.Keys.ToArray()); await using var rd = await c.ExecuteReaderAsync(ct); while (await rd.ReadAsync(ct)) ids.Add(rd.GetInt64(0)); }
            var names = new Dictionary<long, string>();
            foreach (var a in ids) if (await assets.GetAsync(a, ct) is { } ar) names[a] = ar.Name;
            await using (var c = new NpgsqlCommand(Sel + " where i.vault_id = any(@v) and i.deleted_at is null order by m.state <> 'failed', m.next_at nulls last", db))
            { VaultModule.P(c, "v", roles.Keys.ToArray()); await using var rd = await c.ExecuteReaderAsync(ct); while (await rd.ReadAsync(ct)) o.Add(Row(rd, names)); }
            return Results.Ok(new { items = o });
        }), key);
    }
}

/// <summary>
/// Changing a managed password, in two steps so nothing is lost: the new password is kept as "pending" and handed to the agent; only when
/// the agent reports success does it become the password. If the agent took it but never answered, it is kept as a possible password and
/// the change is tried again soon (setting a password does not need the old one, so the next success settles it).
/// </summary>
public static class Rotation
{
    const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", Lower = "abcdefghijkmnopqrstuvwxyz", Digits = "23456789", Symbols = "!#%+-=?@^_*";

    /// <summary>A random password with upper and lower case letters, digits and symbols (Windows complexity rules are met).</summary>
    public static string Generate(int length)
    {
        length = Math.Clamp(length, 14, 128);
        var all = Upper + Lower + Digits + Symbols;
        var chars = new char[length];
        string[] sets = { Upper, Lower, Digits, Symbols };
        for (var i = 0; i < length; i++) { var set = i < sets.Length ? sets[i] : all; chars[i] = set[RandomNumberGenerator.GetInt32(set.Length)]; }
        RandomNumberGenerator.Shuffle(chars.AsSpan());
        return new string(chars);
    }

    /// <summary>Starts a change now. Returns the account's state afterwards (rotating, or failed when there is no agent to do it).</summary>
    public static async Task<string> StartAsync(NpgsqlConnection db, IServiceProvider sp, ITenantContext t, long itemId, string by, CancellationToken ct)
    {
        string account, target, state; long asset;
        await using (var c = new NpgsqlCommand("select account, target, asset_id, state from vault.managed where item_id = @i", db))
        { VaultModule.P(c, "i", itemId); await using var rd = await c.ExecuteReaderAsync(ct); if (!await rd.ReadAsync(ct)) return "gone"; account = rd.GetString(0); target = rd.GetString(1); asset = rd.GetInt64(2); state = rd.GetString(3); }
        if (state == "rotating") return state;
        var settings = await VaultSettings.LoadAsync(sp.GetRequiredService<ISettings>(), ct);
        var pw = Generate(settings.RotationLength);
        var actions = sp.GetService<IDeviceActions>();
        var handle = actions == null ? null : await actions.SetPasswordAsync(asset, account, target, pw, "Vault (" + by + ")", ct);
        if (handle == null)
        {
            await Fail(db, sp, t, itemId, by, "The asset has no working agent, or it runs a system that cannot change this kind of account (Windows for local and domain accounts, Linux for Linux accounts).", ct);
            return "failed";
        }
        await using (var c = new NpgsqlCommand("update vault.managed set state = 'rotating', pending_sealed = @p, handoff = @h, started_at = now(), last_error = '' where item_id = @i", db))
        { VaultModule.P(c, "p", ManagedApi.Protector(sp, t).Protect(pw)); VaultModule.P(c, "h", handle); VaultModule.P(c, "i", itemId); await c.ExecuteNonQueryAsync(ct); }
        await Event(db, itemId, by, "rotation_started", $"new password sent to the agent for {account}", ct);
        return "rotating";
    }

    /// <summary>Follows up a change in progress.</summary>
    public static async Task CheckAsync(NpgsqlConnection db, IServiceProvider sp, ITenantContext t, long itemId, string handle, CancellationToken ct)
    {
        var actions = sp.GetService<IDeviceActions>(); if (actions == null) return;
        var st = await actions.PasswordChangeAsync(handle, ct);
        if (st == null || st.State is "failed" or "cancelled") { await Fail(db, sp, t, itemId, "Vault", st?.Error is { Length: > 0 } e ? e : "The change was withdrawn.", ct); return; }
        if (st.State == "done")
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            await using (var c = new NpgsqlCommand(@"insert into vault.managed_history (item_id, secret_sealed, by_name, note) select item_id, secret_sealed, 'Vault', 'replaced' from vault.managed where item_id = @i;
                update vault.managed set secret_sealed = pending_sealed, pending_sealed = null, handoff = null, state = 'ok', last_error = '', last_rotated_at = now(),
                  next_at = case when rotate_days > 0 then now() + make_interval(days => rotate_days) end where item_id = @i and pending_sealed is not null;
                update vault.items set pw_changed_at = now() where id = @i;
                delete from vault.managed_history where item_id = @i and id not in (select id from vault.managed_history where item_id = @i order by id desc limit 50)", db, tx))
            { VaultModule.P(c, "i", itemId); await c.ExecuteNonQueryAsync(ct); }
            await tx.CommitAsync(ct);
            await Event(db, itemId, "Vault", "rotated", "the agent changed the password", ct);
            return;
        }
        // the agent has not fetched it for a day (the Assets janitor then fails it), or fetched it and went quiet
        if (st.State == "fetched" && st.FetchedAt is { } f && DateTime.SpecifyKind(f, DateTimeKind.Utc) < DateTime.UtcNow.AddMinutes(-15))
        {
            await using var c = new NpgsqlCommand(@"insert into vault.managed_history (item_id, secret_sealed, by_name, note) select item_id, pending_sealed, 'Vault', 'maybe' from vault.managed where item_id = @i and pending_sealed is not null;
                update vault.managed set pending_sealed = null, handoff = null, state = 'unknown', next_at = now() + interval '5 minutes',
                  last_error = 'The agent took the new password but did not say whether it worked. The vault shows both the current and the possible password, and tries again in 5 minutes.' where item_id = @i", db);
            VaultModule.P(c, "i", itemId); await c.ExecuteNonQueryAsync(ct);
            await Event(db, itemId, "Vault", "rotation_unknown", "the agent did not report back", ct);
        }
    }

    static async Task Fail(NpgsqlConnection db, IServiceProvider sp, ITenantContext t, long itemId, string by, string error, CancellationToken ct)
    {
        string account = ""; long asset = 0; int vault = 0; string vaultName = "";
        await using (var c = new NpgsqlCommand(@"update vault.managed m set pending_sealed = null, handoff = null, state = 'failed', last_error = @e,
                next_at = case when m.rotate_days > 0 or m.reset_after_use then now() + interval '1 hour' end
              from vault.items i join vault.vaults v on v.id = i.vault_id where i.id = m.item_id and m.item_id = @i returning m.account, m.asset_id, v.id, v.name", db))
        { VaultModule.P(c, "e", error); VaultModule.P(c, "i", itemId); await using var rd = await c.ExecuteReaderAsync(ct); if (await rd.ReadAsync(ct)) { account = rd.GetString(0); asset = rd.GetInt64(1); vault = rd.GetInt32(2); vaultName = rd.GetString(3); } }
        await Event(db, itemId, by, "rotation_failed", error, ct);
        // tell the vault's managers (once per failure)
        var mail = sp.GetService<IMailQueue>(); var users = sp.GetService<IUserDirectory>(); var access = sp.GetService<IAccess>();
        if (mail == null || users == null || access == null || vault == 0) return;
        var device = sp.GetService<IAssetDirectory>() is { } ad && await ad.GetAsync(asset, ct) is { } a ? a.Name : $"asset {asset}";
        foreach (var (uid, role) in await Access.Entitled(db, access, vault, ct))
            if (role == "manage" && await users.GetAsync(uid, ct) is { Active: true } u && u.Email.Contains('@'))
                await mail.EnqueueTemplateAsync("vault_rotation_failed", u.Email, new Dictionary<string, string> { ["account"] = account, ["device"] = device, ["vault"] = vaultName, ["error"] = error, ["path"] = "#/vault/managed" }, "vault-rotation:" + itemId, ct);
    }

    static async Task Event(NpgsqlConnection db, long itemId, string by, string action, string detail, CancellationToken ct)
    {
        int? vault = null;
        await using (var c = new NpgsqlCommand("select vault_id from vault.items where id = @i", db)) { VaultModule.P(c, "i", itemId); vault = await c.ExecuteScalarAsync(ct) as int?; }
        await Access.Event(db, vault, itemId, by, action, detail, ct);
    }
}

/// <summary>Every minute: password changes due or in progress, ended access grants, and clean-up.</summary>
public sealed class VaultJob : IJobHandler
{
    public const string JobType = "vault.tick";
    public string Type => JobType;

    public async Task RunAsync(JobContext job, CancellationToken ct)
    {
        var sp = job.Services; var t = sp.GetRequiredService<ITenantContext>();
        var lic = await sp.GetRequiredService<IEntitlements>().CurrentAsync(ct);
        if (!lic.Has(Modules.Vault)) return;
        await using var db = new NpgsqlConnection(job.Tenant.ConnectionString); await db.OpenAsync(ct);
        async Task<List<(long, string)>> Rows(string sql)
        {
            var o = new List<(long, string)>();
            await using var c = new NpgsqlCommand(sql, db); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) o.Add((rd.GetInt64(0), rd.IsDBNull(1) ? "" : rd.GetString(1)));
            return o;
        }
        async Task Sql(string sql) { await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(ct); }

        foreach (var (id, handle) in await Rows("select item_id, handoff from vault.managed where state = 'rotating' and handoff is not null"))
            await Rotation.CheckAsync(db, sp, t, id, handle, ct);

        // ended grants; a managed account marked "change after each use" gets a new password
        var ended = await Rows("delete from vault.grants g where expires_at <= now() returning item_id, (select case when m.reset_after_use then 'reset' else '' end from vault.managed m where m.item_id = g.item_id)");
        await Sql("update vault.requests set status = 'expired' where status = 'approved' and expires_at <= now()");
        await Sql("update vault.requests set status = 'expired', note = 'Nobody answered within a day.' where status = 'pending' and created_at < now() - interval '1 day'");
        foreach (var id in ended.Where(e => e.Item2 == "reset").Select(e => e.Item1).Distinct())
            await Sql($"update vault.managed set next_at = now() where item_id = {id} and state <> 'rotating'");

        foreach (var (id, _) in await Rows("select item_id, '' from vault.managed m join vault.items i on i.id = m.item_id where i.deleted_at is null and m.state <> 'rotating' and m.next_at <= now() order by m.next_at limit 20"))
            await Rotation.StartAsync(db, sp, t, id, "schedule", ct);

        await Sql("delete from vault.items where deleted_at < now() - interval '30 days'");
        await Sql("update vault.sends set data_enc = null where data_enc is not null and (expires_at <= now() or views >= max_views or deleted_at is not null)");
        await Sql("delete from vault.sends where expires_at < now() - interval '30 days'");
        await Sql("delete from vault.breach_cache where at < now() - interval '7 days'");
        await Sql("delete from vault.events where at < now() - interval '400 days'");
    }
}
