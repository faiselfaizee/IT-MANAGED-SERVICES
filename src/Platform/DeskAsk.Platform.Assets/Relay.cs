using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace DeskAsk.Platform.Assets;

/// <summary>
/// Hands work to a controller and waits for the answer. The AD Manager uses it to reach a customer's domain controllers from inside
/// their network, so the platform needs no inbound path to them. The job (which holds the service account password) is sealed while
/// it waits and wiped the moment the controller has taken it.
/// </summary>
public sealed class ControllerRelay(ITenantContext tenant, IConfiguration cfg, AgentHub hub) : IControllerRelay
{
    public async Task<IReadOnlyList<ControllerInfo>> ControllersAsync(CancellationToken ct = default)
    {
        await using var db = await AgentsApi.Open(tenant, ct);
        await using var c = new NpgsqlCommand("select id, name, company_id, version, last_seen from assets.collectors where deleted_at is null order by name", db);
        await using var r = await c.ExecuteReaderAsync(ct);
        var list = new List<ControllerInfo>();
        while (await r.ReadAsync(ct))
            list.Add(new ControllerInfo(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt32(2), !r.IsDBNull(4) && DateTime.UtcNow - r.GetDateTime(4).ToUniversalTime() < TimeSpan.FromMinutes(3), r.GetString(3)));
        return list;
    }

    public async Task<RelayReply> CallAsync(string controllerId, string kind, object payload, TimeSpan timeout, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var tid = tenant.Require().Id;
        await using var db = await AgentsApi.Open(tenant, ct);
        await using (var c = new NpgsqlCommand("insert into assets.relay_jobs (id, collector_id, kind, payload) values (@i, @c, @k, @p)", db))
        {
            AgentsApi.P(c, "i", id); AgentsApi.P(c, "c", controllerId); AgentsApi.P(c, "k", kind);
            AgentsApi.P(c, "p", SecretBox.Seal(cfg, tid, JsonSerializer.Serialize(payload)));
            await c.ExecuteNonQueryAsync(ct);
        }
        hub.Wake(tid + ":relay:" + controllerId);
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            await using (var q = new NpgsqlCommand("select status, result, error from assets.relay_jobs where id = @i", db))
            {
                AgentsApi.P(q, "i", id);
                await using var r = await q.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return new RelayReply(false, null, Err("The job was removed."), false);
                var st = r.GetString(0);
                if (st is "done" or "failed")
                {
                    var res = r.IsDBNull(1) ? (JsonElement?)null : JsonDocument.Parse(r.GetString(1)).RootElement.Clone();
                    var er = r.IsDBNull(2) ? (JsonElement?)null : JsonDocument.Parse(r.GetString(2)).RootElement.Clone();
                    await r.CloseAsync();
                    await using var d = new NpgsqlCommand("delete from assets.relay_jobs where id = @i", db); AgentsApi.P(d, "i", id); await d.ExecuteNonQueryAsync(CancellationToken.None);
                    return new RelayReply(st == "done", res, er, false);
                }
            }
            if (DateTime.UtcNow >= until || ct.IsCancellationRequested)
            {
                // still waiting for a controller that has not taken it: take it back so it cannot run later, unseen
                await using var x = new NpgsqlCommand("update assets.relay_jobs set status = 'failed', payload = '', finished_at = now(), error = @e where id = @i and status = 'queued'", db);
                AgentsApi.P(x, "i", id); AgentsApi.P(x, "e", "{\"message\":\"The controller did not pick this up. Is it online (Assets > Agents & collectors)?\"}");
                await x.ExecuteNonQueryAsync(CancellationToken.None);
                return new RelayReply(false, null, Err("The controller did not answer in time."), true);
            }
            await Task.Delay(200, CancellationToken.None);
        }
    }

    static JsonElement Err(string m) => JsonDocument.Parse(JsonSerializer.Serialize(new { message = m })).RootElement.Clone();
}

public static class RelayApi
{
    /// <summary>Controller side: wait for jobs, then return the result. Authenticated like every other controller call.</summary>
    internal static void Map(IEndpointRouteBuilder r, string key, Func<HttpContext, NpgsqlConnection, CancellationToken, Task<string?>> auth)
    {
        r.MapGet("/collector/v1/relay/wait", async (HttpContext http, ITenantContext t, IConfiguration cfg, AgentHub hub, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            var col = await auth(http, db, ct); if (col == null) return Results.Json(new { error = "collector_auth" }, statusCode: 401);
            async Task<List<object>> Take()
            {
                var jobs = new List<object>();
                await using var c = new NpgsqlCommand(@"update assets.relay_jobs set status = 'running', taken_at = now()
                    where id in (select id from assets.relay_jobs where collector_id = @c and status = 'queued' order by created_at limit 8 for update skip locked)
                    returning id, kind, payload", db);
                AgentsApi.P(c, "c", col);
                var rows = new List<(string Id, string Kind, string Payload)>();
                await using (var rd = await c.ExecuteReaderAsync(ct)) while (await rd.ReadAsync(ct)) rows.Add((rd.GetString(0), rd.GetString(1), rd.GetString(2)));
                foreach (var (id, kind, payload) in rows)
                {
                    string plain;
                    try { plain = SecretBox.Open(cfg, t.Require().Id, payload); }
                    catch (Exception)
                    {
                        await using var f = new NpgsqlCommand("update assets.relay_jobs set status = 'failed', payload = '', finished_at = now(), error = @e where id = @i", db);
                        AgentsApi.P(f, "i", id); AgentsApi.P(f, "e", "{\"message\":\"The job could not be read.\"}"); await f.ExecuteNonQueryAsync(ct); continue;
                    }
                    jobs.Add(new { id, kind, payload = JsonDocument.Parse(plain).RootElement.Clone() });
                    await using var w = new NpgsqlCommand("update assets.relay_jobs set payload = '' where id = @i", db); AgentsApi.P(w, "i", id); await w.ExecuteNonQueryAsync(ct);
                }
                return jobs;
            }
            var got = await Take();
            if (got.Count == 0)
            {
                int.TryParse(http.Request.Query["wait"], out var wait);
                await hub.WaitAsync(t.Require().Id + ":relay:" + col, TimeSpan.FromSeconds(Math.Clamp(wait, 1, 30)), ct);
                if (!ct.IsCancellationRequested) got = await Take();
            }
            return Results.Ok(new { jobs = got });
        }).RequireFoundation();

        r.MapPost("/collector/v1/relay/result", async (JsonElement body, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            var col = await auth(http, db, ct); if (col == null) return Results.Json(new { error = "collector_auth" }, statusCode: 401);
            var id = AgentsApi.Str(body, "id") ?? "";
            var ok = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
            JsonElement rs = default, er = default;
            var hasRes = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("result", out rs);
            var hasErr = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("error", out er);
            string? result = hasRes && rs.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? rs.GetRawText() : null;
            string? error = hasErr && er.ValueKind == JsonValueKind.Object ? er.GetRawText() : null;
            if (result != null && result.Length > 16_000_000) { result = null; ok = false; error = "{\"message\":\"The answer was too large.\"}"; }
            if (!ok && error == null) error = "{\"message\":\"The controller reported a failure.\"}";
            await using var c = new NpgsqlCommand("update assets.relay_jobs set status = @s, result = @r, error = @e, payload = '', finished_at = now() where id = @i and collector_id = @c and status = 'running'", db);
            AgentsApi.P(c, "s", ok ? "done" : "failed"); AgentsApi.P(c, "r", result); AgentsApi.P(c, "e", error); AgentsApi.P(c, "i", id); AgentsApi.P(c, "c", col);
            return Results.Ok(new { ok = await c.ExecuteNonQueryAsync(ct) == 1 });
        }).RequireFoundation();
    }
}
