using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace DeskAsk.Platform.Assets;

/// <summary>
/// Configuration backups of firewalls, switches and access points, sent by the controller with the device's inventory.
/// A new version is kept only when the text changed (the latest 30 are kept). Configurations hold password hashes and keys,
/// so only administrators see them, and every view or download is written to the audit log.
/// </summary>
public static class ConfigsApi
{
    public const int Keep = 30;
    const int MaxText = 5_000_000;

    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>Stores the configuration (or the reason it could not be read) that came with an inventory.</summary>
    public static async Task FromInventory(NpgsqlConnection db, long assetId, JsonElement raw, string actor, CancellationToken ct)
    {
        if (raw.TryGetProperty("config", out var cfg) && cfg.ValueKind == JsonValueKind.Object && cfg.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
        {
            var text = t.GetString() ?? "";
            if (text.Trim() == "") return;
            if (text.Length > MaxText) text = text[..MaxText];
            await Save(db, assetId, AgentsApi.Clip(AgentsApi.Str(cfg, "source"), 20), AgentsApi.Clip(AgentsApi.Str(cfg, "vendor"), 40), text, actor, ct);
        }
        else if (raw.TryGetProperty("config_error", out var e) && e.ValueKind == JsonValueKind.String)
        {
            await using var c = new NpgsqlCommand("update assets.assets set config_checked_at = now(), config_error = @e where id = @i", db);
            AgentsApi.P(c, "e", AgentsApi.Clip(e.GetString(), 600)); AgentsApi.P(c, "i", assetId); await c.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>Keeps a new version when the configuration changed; returns true when it did.</summary>
    public static async Task<bool> Save(NpgsqlConnection db, long assetId, string source, string vendor, string text, string actor, CancellationToken ct)
    {
        text = text.Replace("\r\n", "\n");
        var hash = Hash(text);
        await using (var u = new NpgsqlCommand("update assets.assets set config_checked_at = now(), config_error = '' where id = @i", db)) { AgentsApi.P(u, "i", assetId); await u.ExecuteNonQueryAsync(ct); }
        await using (var q = new NpgsqlCommand("select hash from assets.configs where asset_id = @i order by id desc limit 1", db))
        {
            AgentsApi.P(q, "i", assetId);
            if (await q.ExecuteScalarAsync(ct) is string last && last == hash) return false;
        }
        await using (var c = new NpgsqlCommand("insert into assets.configs (asset_id, source, vendor, text, size, lines, hash) values (@a,@s,@v,@t,@n,@l,@h)", db))
        {
            AgentsApi.P(c, "a", assetId); AgentsApi.P(c, "s", source); AgentsApi.P(c, "v", vendor); AgentsApi.P(c, "t", text);
            AgentsApi.P(c, "n", Encoding.UTF8.GetByteCount(text)); AgentsApi.P(c, "l", text.Count(ch => ch == '\n') + 1); AgentsApi.P(c, "h", hash);
            await c.ExecuteNonQueryAsync(ct);
        }
        await using (var d = new NpgsqlCommand("delete from assets.configs where asset_id = @a and id not in (select id from assets.configs where asset_id = @a order by id desc limit @k)", db))
        { AgentsApi.P(d, "a", assetId); AgentsApi.P(d, "k", Keep); await d.ExecuteNonQueryAsync(ct); }
        await AssetsDevice.History(db, assetId, actor, "config", JsonSerializer.Serialize(new { configuration = "changed", source, vendor }), ct);
        return true;
    }

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        RouteHandlerBuilder Admin(RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole("admin")).RequireModule(key);

        Admin(r.MapGet("/api/assets/{id:long}/configs", async (long id, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            DateTime? checkedAt = null; string error = "";
            await using (var q = new NpgsqlCommand("select config_checked_at, config_error from assets.assets where id = @i", db))
            {
                AgentsApi.P(q, "i", id); await using var rd = await q.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) return Results.NotFound();
                checkedAt = rd.IsDBNull(0) ? null : rd.GetDateTime(0); error = rd.GetString(1);
            }
            var items = new List<object>();
            await using (var c = new NpgsqlCommand("select id, at, source, vendor, size, lines from assets.configs where asset_id = @i order by id desc", db))
            {
                AgentsApi.P(c, "i", id); await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) items.Add(new { id = rd.GetInt64(0), at = rd.GetDateTime(1), source = rd.GetString(2), vendor = rd.GetString(3), size = rd.GetInt32(4), lines = rd.GetInt32(5) });
            }
            return Results.Ok(new { checkedAt, error, keep = Keep, items });
        }));

        async Task<(string Text, DateTime At, string Tag)?> One(NpgsqlConnection db, long id, long cid, CancellationToken ct)
        {
            await using var c = new NpgsqlCommand("select c.text, c.at, a.tag from assets.configs c join assets.assets a on a.id = c.asset_id where c.asset_id = @a and c.id = @c", db);
            AgentsApi.P(c, "a", id); AgentsApi.P(c, "c", cid); await using var rd = await c.ExecuteReaderAsync(ct);
            return await rd.ReadAsync(ct) ? (rd.GetString(0), rd.GetDateTime(1), rd.GetString(2)) : null;
        }

        Admin(r.MapGet("/api/assets/{id:long}/configs/{cid:long}", async (long id, long cid, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            var one = await One(db, id, cid, ct); if (one == null) return Results.NotFound();
            await audit.WriteAsync("assets.config_viewed", id.ToString(), "version " + cid, ct: ct);
            return Results.Ok(new { id = cid, at = one.Value.At, text = one.Value.Text });
        }));

        Admin(r.MapGet("/api/assets/{id:long}/configs/{cid:long}/download", async (long id, long cid, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            var one = await One(db, id, cid, ct); if (one == null) return Results.NotFound();
            await audit.WriteAsync("assets.config_downloaded", id.ToString(), "version " + cid, ct: ct);
            var name = new string((one.Value.Tag + "-config-" + one.Value.At.ToString("yyyyMMdd-HHmm")).Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray()) + ".txt";
            return Results.File(Encoding.UTF8.GetBytes(one.Value.Text), "text/plain; charset=utf-8", name);
        }));
    }
}
