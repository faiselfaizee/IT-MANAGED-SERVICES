using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace DeskAsk.Platform.Vault;

/// <summary>
/// Send links: a secret for someone outside DeskAsk (a customer's new starter, a contractor). The browser encrypts it with a key that
/// stays in the link's #fragment, which browsers never send to a server; the link stops working after its time or number of views.
/// </summary>
public static class SendsApi
{
    static readonly Regex Id = new("^[A-Za-z0-9_-]{22}$");

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        VaultModule.Use(r.MapPost("/api/vault/sends", async (JsonElement b, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var data = VaultModule.Str(b, "dataEnc");
            if (!VaultModule.Blob(data, 70000)) return VaultModule.Bad("bad_input", "The text is not encrypted properly (or is larger than 50 KB).");
            var label = (VaultModule.Str(b, "label") ?? "").Trim(); if (label.Length > 80) return VaultModule.Bad("bad_input", "Keep the label under 80 characters.");
            var hours = Math.Clamp(VaultModule.Int(b, "hours") ?? 24, 1, 24 * 30); var views = Math.Clamp(VaultModule.Int(b, "maxViews") ?? 1, 1, 100);
            var id = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            await using var db = await VaultModule.Open(t, ct);
            await using var c = new NpgsqlCommand("insert into vault.sends (id, data_enc, label, created_by, created_by_id, expires_at, max_views, passcode) values (@i, @d, @l, @b, @u, now() + make_interval(hours => @h), @v, @p)", db);
            VaultModule.P(c, "i", id); VaultModule.P(c, "d", data); VaultModule.P(c, "l", label); VaultModule.P(c, "b", VaultModule.Actor(http.User)); VaultModule.P(c, "u", VaultModule.Me(http.User));
            VaultModule.P(c, "h", hours); VaultModule.P(c, "v", views); VaultModule.P(c, "p", VaultModule.Bool(b, "passcode"));
            await c.ExecuteNonQueryAsync(ct);
            await audit.WriteAsync("vault.send_created", id, $"\"{label}\", {hours} hours, {views} views", ct: ct);
            return Results.Ok(new { id });
        }), key);

        VaultModule.Use(r.MapGet("/api/vault/sends", async (HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            var all = VaultModule.Role(http.User) == "admin";
            await using var c = new NpgsqlCommand(@"select id, label, created_by, created_at, expires_at, max_views, views, passcode, deleted_at, data_enc is not null and expires_at > now() and views < max_views and deleted_at is null
                from vault.sends where (@all or created_by_id = @u) order by created_at desc limit 200", db);
            VaultModule.P(c, "all", all); VaultModule.P(c, "u", VaultModule.Me(http.User));
            var o = new List<object>(); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                o.Add(new { id = rd.GetString(0), label = rd.GetString(1), createdBy = rd.GetString(2), createdAt = rd.GetDateTime(3), expiresAt = rd.GetDateTime(4), maxViews = rd.GetInt32(5), views = rd.GetInt32(6), passcode = rd.GetBoolean(7), deletedAt = rd.IsDBNull(8) ? (DateTime?)null : rd.GetDateTime(8), active = rd.GetBoolean(9) });
            return Results.Ok(new { items = o });
        }), key);

        VaultModule.Use(r.MapDelete("/api/vault/sends/{id}", async (string id, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await VaultModule.Open(t, ct);
            await using var c = new NpgsqlCommand("update vault.sends set deleted_at = now(), data_enc = null where id = @i and (created_by_id = @u or @admin) and deleted_at is null", db);
            VaultModule.P(c, "i", id); VaultModule.P(c, "u", VaultModule.Me(http.User)); VaultModule.P(c, "admin", VaultModule.Role(http.User) == "admin");
            return await c.ExecuteNonQueryAsync(ct) > 0 ? Results.Ok(new { ok = true }) : Results.NotFound();
        }), key);

        // the public side: what the link shows before opening (so a link preview in chat does not use up a view), then opening it
        r.MapGet("/api/public/vault/sends/{id}", async (string id, ITenantContext t, CancellationToken ct) =>
        {
            if (!Id.IsMatch(id)) return Results.NotFound();
            await using var db = await VaultModule.Open(t, ct);
            await using var c = new NpgsqlCommand("select passcode, expires_at, max_views - views, created_by from vault.sends where id = @i and data_enc is not null and deleted_at is null and expires_at > now() and views < max_views", db);
            VaultModule.P(c, "i", id); await using var rd = await c.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return Results.NotFound(new { error = "gone", detail = "This link has expired, was used up or was deleted." });
            return Results.Ok(new { passcode = rd.GetBoolean(0), expiresAt = rd.GetDateTime(1), viewsLeft = rd.GetInt32(2), from = rd.GetString(3) });
        }).AllowAnonymous().RequireModule(key);

        r.MapPost("/api/public/vault/sends/{id}/open", async (string id, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            if (!Id.IsMatch(id)) return Results.NotFound();
            await using var db = await VaultModule.Open(t, ct);
            await using var c = new NpgsqlCommand(@"update vault.sends set views = views + 1, data_enc = case when views + 1 >= max_views then null else data_enc end
                from (select id, data_enc as d from vault.sends where id = @i and data_enc is not null and deleted_at is null and expires_at > now() and views < max_views for update) o
                where vault.sends.id = o.id returning o.d, max_views - views", db);
            VaultModule.P(c, "i", id);
            string data; int left;
            await using (var rd = await c.ExecuteReaderAsync(ct)) { if (!await rd.ReadAsync(ct)) return Results.NotFound(new { error = "gone", detail = "This link has expired, was used up or was deleted." }); data = rd.GetString(0); left = rd.GetInt32(1); }
            await audit.WriteAsync("vault.send_opened", id, $"{left} views left; opened from {http.Connection.RemoteIpAddress}", actor: "send link", ct: ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { dataEnc = data, viewsLeft = left });
        }).AllowAnonymous().RequireModule(key);
    }
}

/// <summary>The breach check (k-anonymity: only five characters of a SHA-1 hash leave the server) and the Vault settings.</summary>
public static class ToolsApi
{
    static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) }) { Timeout = TimeSpan.FromSeconds(10) };
    static readonly Regex Prefix = new("^[0-9A-F]{5}$");

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        VaultModule.Use(r.MapGet("/api/vault/breach/{prefix}", async (string prefix, ITenantContext t, ISettings s, IConfiguration cfg, CancellationToken ct) =>
        {
            prefix = prefix.ToUpperInvariant();
            if (!Prefix.IsMatch(prefix)) return VaultModule.Bad("bad_input", "Five hexadecimal characters.");
            if (!(await VaultSettings.LoadAsync(s, ct)).BreachCheck) return Results.Json(new { error = "off", detail = "The breach check is switched off in Vault settings." }, statusCode: 409);
            await using var db = await VaultModule.Open(t, ct);
            await using (var c = new NpgsqlCommand("select body from vault.breach_cache where prefix = @p and at > now() - interval '7 days'", db))
            { VaultModule.P(c, "p", prefix); if (await c.ExecuteScalarAsync(ct) is string cached) return Results.Text(cached, "text/plain"); }
            string body;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, (cfg["Vault:BreachUrl"] ?? "https://api.pwnedpasswords.com/range/") + prefix);
                req.Headers.TryAddWithoutValidation("Add-Padding", "true"); req.Headers.TryAddWithoutValidation("User-Agent", "DeskAsk-Vault");
                using var res = await Http.SendAsync(req, ct);
                if (!res.IsSuccessStatusCode) return Results.Json(new { error = "unavailable", detail = $"The breach service answered {(int)res.StatusCode}." }, statusCode: 502);
                body = await res.Content.ReadAsStringAsync(ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return Results.Json(new { error = "unavailable", detail = "The breach service could not be reached from the server." }, statusCode: 502); }
            // keep only real entries (padding lines have a count of 0)
            body = string.Join("\n", body.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 36 && !l.EndsWith(":0", StringComparison.Ordinal)));
            await using (var c = new NpgsqlCommand("insert into vault.breach_cache (prefix, body) values (@p, @b) on conflict (prefix) do update set body = excluded.body, at = now()", db))
            { VaultModule.P(c, "p", prefix); VaultModule.P(c, "b", body); await c.ExecuteNonQueryAsync(ct); }
            return Results.Text(body, "text/plain");
        }), key);

        r.MapGet("/api/vault/settings", async (ISettings s, CancellationToken ct) => Results.Ok(await VaultSettings.LoadAsync(s, ct)))
            .RequireAuthorization(p => p.RequireRole("admin")).RequireModule(key);

        r.MapPut("/api/vault/settings", async (JsonElement b, ISettings s, IAuditLog audit, CancellationToken ct) =>
        {
            var v = await VaultSettings.LoadAsync(s, ct);
            if (VaultModule.Int(b, "autoLockMinutes") is int al) v.AutoLockMinutes = Math.Clamp(al, 1, 480);
            if (b.TryGetProperty("breachCheck", out _)) v.BreachCheck = VaultModule.Bool(b, "breachCheck");
            if (VaultModule.Int(b, "rotationLength") is int rl) v.RotationLength = Math.Clamp(rl, 14, 128);
            if (VaultModule.Int(b, "defaultRotateDays") is int rd) v.DefaultRotateDays = Math.Clamp(rd, 0, 365);
            if (VaultModule.Int(b, "requestMaxMinutes") is int rm) v.RequestMaxMinutes = Math.Clamp(rm, 15, 7 * 24 * 60);
            if (VaultModule.Str(b, "creators") is { } cr) { if (cr is not ("admins" or "engineers")) return VaultModule.Bad("bad_input", "creators must be admins or engineers"); v.Creators = cr; }
            await v.SaveAsync(s, ct);
            await audit.WriteAsync("vault.settings_changed", "", JsonSerializer.Serialize(v), ct: ct);
            return Results.Ok(v);
        }).RequireAuthorization(p => p.RequireRole("admin")).RequireModule(key);
    }
}
