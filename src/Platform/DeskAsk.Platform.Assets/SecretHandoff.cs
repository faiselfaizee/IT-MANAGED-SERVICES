using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace DeskAsk.Platform.Assets;

/// <summary>
/// Password changes for the Vault. The new password waits here sealed; the device's agent fetches it exactly once (it is wiped as it is
/// handed over), sets it, and reports the result.
/// </summary>
public static class SecretHandoff
{
    static readonly Regex Account = new(@"^[A-Za-z0-9][A-Za-z0-9._$ -]{0,63}$");
    public static bool ValidAccount(string? a) => a != null && Account.IsMatch(a);

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        r.MapGet("/agent/v1/secret/{id}", async (string id, HttpContext http, ITenantContext t, IConfiguration cfg, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            var ag = await AgentsApi.AuthAgent(http, db, ct); if (ag == null) return Results.Json(new { error = "agent_auth" }, statusCode: 401);
            // only the agent it was meant for, only once
            await using var c = new NpgsqlCommand(@"update assets.secret_handoff h set state = 'fetched', fetched_at = now(), secret_sealed = null
                from (select id, secret_sealed from assets.secret_handoff where id = @i and agent_id = @g and state = 'queued' for update) o
                where h.id = o.id returning h.account, h.target, o.secret_sealed", db);
            AgentsApi.P(c, "i", id); AgentsApi.P(c, "g", ag.Value.Id);
            await using var rd = await c.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct) || rd.IsDBNull(2)) return Results.NotFound(new { error = "gone", detail = "This password change was already fetched, withdrawn or never existed." });
            var res = new { account = rd.GetString(0), target = rd.GetString(1), password = SecretBox.Open(cfg, t.Require().Id, rd.GetString(2)) };
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(res);
        }).RequireModule(key);

        r.MapPost("/agent/v1/secret/{id}/result", async (string id, JsonElement b, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            var ag = await AgentsApi.AuthAgent(http, db, ct); if (ag == null) return Results.Json(new { error = "agent_auth" }, statusCode: 401);
            var ok = b.ValueKind == JsonValueKind.Object && b.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
            var err = b.ValueKind == JsonValueKind.Object && b.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
            if (err.Length > 400) err = err[..400];
            await using var c = new NpgsqlCommand("update assets.secret_handoff set state = @s, error = @e, done_at = now() where id = @i and agent_id = @g and state = 'fetched' returning asset_id, account", db);
            AgentsApi.P(c, "s", ok ? "done" : "failed"); AgentsApi.P(c, "e", ok ? "" : (err == "" ? "The agent could not change the password." : err)); AgentsApi.P(c, "i", id); AgentsApi.P(c, "g", ag.Value.Id);
            long asset; string account;
            await using (var rd = await c.ExecuteReaderAsync(ct)) { if (!await rd.ReadAsync(ct)) return Results.NotFound(); asset = rd.GetInt64(0); account = rd.GetString(1); }
            await audit.WriteAsync(ok ? "assets.password_changed" : "assets.password_change_failed", asset.ToString(), ok ? $"account {account}" : $"account {account}: {err}", ct: ct);
            return Results.Ok(new { ok = true });
        }).RequireModule(key);
    }
}
