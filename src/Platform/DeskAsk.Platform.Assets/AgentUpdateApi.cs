using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace DeskAsk.Platform.Assets;

/// <summary>
/// "Update agent" on the asset page, with a live log. Agents update themselves anyway when the server holds a newer build; this button
/// asks one agent to do it now, even a version it tried before and put back. Agents from 2.9.3 report each step; older ones only show up
/// again with the new version, which closes the log.
/// </summary>
public static class AgentUpdateApi
{
    internal static async Task Note(NpgsqlConnection db, string agentId, string level, string text, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("insert into assets.agent_upd_log (agent_id, level, text) values (@a, @l, @t)", db);
        AgentsApi.P(c, "a", agentId); AgentsApi.P(c, "l", level is "warn" or "ok" ? level : "info"); AgentsApi.P(c, "t", AgentsApi.Clip(text, 500)); await c.ExecuteNonQueryAsync(ct);
        await using var d = new NpgsqlCommand("delete from assets.agent_upd_log where agent_id = @a and id < (select max(id) from assets.agent_upd_log where agent_id = @a) - 200", db);
        AgentsApi.P(d, "a", agentId); await d.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Called whenever an agent reports its version: closes a running update when it is the version asked for.</summary>
    internal static async Task Reported(NpgsqlConnection db, string agentId, string? version, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(version)) return;
        await using var c = new NpgsqlCommand("update assets.agents set upd_state = 'done', upd_at = now() where id = @i and upd_state = 'running' and upd_to = @v returning id", db);
        AgentsApi.P(c, "i", agentId); AgentsApi.P(c, "v", version);
        if (await c.ExecuteScalarAsync(ct) != null) await Note(db, agentId, "ok", "The agent is back and runs version " + version + ". Update finished.", ct);
    }

    static string State(string state, DateTime? at) => state == "running" && at != null && DateTime.UtcNow - at.Value > TimeSpan.FromMinutes(15) ? "stalled" : state;

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        RouteHandlerBuilder Admin(RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole("admin")).RequireModule(key);

        async Task<(string Id, string Version, string Platform, DateTime? Seen, string State, DateTime? At, string To)?> Agent(NpgsqlConnection db, long assetId, CancellationToken ct)
        {
            await using var c = new NpgsqlCommand(@"select g.id, g.version, g.platform, g.last_seen, g.upd_state, g.upd_at, g.upd_to from assets.assets a
                join assets.agents g on g.id = a.agent_id and g.deleted_at is null where a.id = @i", db);
            AgentsApi.P(c, "i", assetId); await using var rd = await c.ExecuteReaderAsync(ct);
            return await rd.ReadAsync(ct) ? (rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.IsDBNull(3) ? null : rd.GetDateTime(3), rd.GetString(4), rd.IsDBNull(5) ? null : rd.GetDateTime(5), rd.GetString(6)) : null;
        }

        // one update, or several from the asset list; each asset gets the same checks
        async Task<(bool Ok, string Error, string Detail, string From, string To)> Start(NpgsqlConnection db, long id, string actor, IConfiguration cfg, AgentHub hub, ITenantContext t, CancellationToken ct)
        {
            var a = await Agent(db, id, ct);
            if (a == null) return (false, "no_agent", "This asset has no agent. Install the agent first.", "", "");
            var latest = AgentsApi.AgentVersion(cfg);
            if (latest == "" || AgentsApi.AgentFile(cfg, a.Value.Platform == "windows" ? "windows" : "linux") == null)
                return (false, "no_build", "This server holds no agent build. Install the latest DeskAsk update (the .dkb file); it carries the agent.", a.Value.Version, "");
            if (a.Value.Version == latest) return (false, "up_to_date", "The agent already runs " + latest + ", the version this server holds.", a.Value.Version, latest);
            if (a.Value.Seen == null || a.Value.Seen.Value < DateTime.UtcNow.AddSeconds(-AgentsApi.Offline(cfg)))
                return (false, "agent_offline", "The agent is offline, so it cannot be asked to update. Start the computer, or reinstall the agent through the site controller (Install agent on this page); a computer restart also starts it again.", a.Value.Version, latest);
            if (State(a.Value.State, a.Value.At) == "running") return (false, "busy", "An update is already running for this agent. Follow it in the log below.", a.Value.Version, latest);

            await using (var c = new NpgsqlCommand("update assets.agents set upd_state = 'running', upd_to = @v, upd_at = now() where id = @i", db))
            { AgentsApi.P(c, "v", latest); AgentsApi.P(c, "i", a.Value.Id); await c.ExecuteNonQueryAsync(ct); }
            await using (var d = new NpgsqlCommand("delete from assets.agent_upd_log where agent_id = @a", db)) { AgentsApi.P(d, "a", a.Value.Id); await d.ExecuteNonQueryAsync(ct); }
            await Note(db, a.Value.Id, "info", $"Update from {a.Value.Version} to {latest} asked by {actor}. Waiting for the agent to pick it up (a few seconds when it is online).", ct);
            if (!AgentsApi.AtLeast(a.Value.Version, "2.9.3"))
                await Note(db, a.Value.Id, "info", "Agents older than 2.9.3 do not report each step. This log closes when the agent comes back with " + latest + ", normally within a minute.", ct);
            await AgentsApi.Enqueue(db, hub, t, a.Value.Id, "update", new { version = latest, force = true }, ct);
            await AssetsDevice.History(db, id, actor, "agent", JsonSerializer.Serialize(new { update = a.Value.Version + " -> " + latest }), ct);
            return (true, "", "", a.Value.Version, latest);
        }

        Admin(r.MapPost("/api/assets/{id:long}/agent-update", async (long id, ClaimsPrincipal u, ITenantContext t, IConfiguration cfg, AgentHub hub, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            var s = await Start(db, id, AgentsApi.Actor(u), cfg, hub, t, ct);
            if (!s.Ok) return AgentsApi.Bad(s.Error, s.Detail);
            await audit.WriteAsync("assets.agent_update", id.ToString(), s.From + " -> " + s.To, ct: ct);
            return Results.Ok(new { ok = true, from = s.From, to = s.To });
        }));

        // bulk agent actions from the asset list: update, move to the recycle bin, uninstall (at most 1000 assets per request)
        Admin(r.MapPost("/api/assets/agents/bulk", async (JsonElement b, ClaimsPrincipal u, ITenantContext t, IConfiguration cfg, AgentHub hub, IAuditLog audit, CancellationToken ct) =>
        {
            var action = AgentsApi.Str(b, "action") ?? "";
            if (action is not ("update" or "bin" or "uninstall")) return AgentsApi.Bad("bad_input", "action must be update, bin or uninstall");
            var ids = b.TryGetProperty("assetIds", out var arr) && arr.ValueKind == JsonValueKind.Array ? arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt64()).Distinct().ToList() : new List<long>();
            if (ids.Count == 0) return AgentsApi.Bad("bad_input", "Choose at least one asset.");
            if (ids.Count > 1000) return AgentsApi.Bad("bad_input", "At most 1000 assets at a time.");
            await using var db = await AgentsApi.Open(t, ct);
            var actor = AgentsApi.Actor(u);
            int done = 0; var skipped = new Dictionary<string, int>();
            void Skip(string why) => skipped[why] = skipped.TryGetValue(why, out var n) ? n + 1 : 1;
            foreach (var id in ids)
            {
                if (action == "update")
                {
                    var s = await Start(db, id, actor, cfg, hub, t, ct);
                    if (s.Ok) done++; else Skip(s.Error);
                    continue;
                }
                var a = await Agent(db, id, ct);
                if (a == null) { Skip("no_agent"); continue; }
                if (action == "uninstall") await AgentsApi.Uninstall(db, hub, t, a.Value.Id, ct);
                else
                {
                    await using (var c = new NpgsqlCommand("update assets.agents set deleted_at = now() where id = @i and deleted_at is null", db)) { AgentsApi.P(c, "i", a.Value.Id); await c.ExecuteNonQueryAsync(ct); }
                    await using (var c = new NpgsqlCommand("update assets.assets set agent_id = '' where agent_id = @i", db)) { AgentsApi.P(c, "i", a.Value.Id); await c.ExecuteNonQueryAsync(ct); }
                }
                await AssetsDevice.History(db, id, actor, "agent", JsonSerializer.Serialize(new { agent = action == "bin" ? "moved to the recycle bin" : "uninstalled" }), ct);
                done++;
            }
            await audit.WriteAsync("assets.agents_bulk", action, $"{done} of {ids.Count}", ct: ct);
            return Results.Ok(new { done, skipped });
        }));

        Admin(r.MapGet("/api/assets/{id:long}/agent-update/log", async (long id, long? after, ITenantContext t, IConfiguration cfg, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            var a = await Agent(db, id, ct);
            var latest = AgentsApi.AgentVersion(cfg);
            if (a == null) return Results.Ok(new { status = "none", version = "", latest, online = false, items = new List<object>() });
            var items = new List<object>();
            await using (var c = new NpgsqlCommand("select id, at, level, text from assets.agent_upd_log where agent_id = @a and id > @q order by id limit 300", db))
            {
                AgentsApi.P(c, "a", a.Value.Id); AgentsApi.P(c, "q", after ?? 0); await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) items.Add(new { seq = rd.GetInt64(0), at = rd.GetDateTime(1), level = rd.GetString(2), text = rd.GetString(3) });
            }
            var online = a.Value.Seen != null && a.Value.Seen.Value > DateTime.UtcNow.AddSeconds(-AgentsApi.Offline(cfg));
            var st = State(a.Value.State, a.Value.At); if (st == "") st = "none";
            return Results.Ok(new { status = st, version = a.Value.Version, latest, online, items });
        }));

        // the agent reports a step (2.9.3 and later)
        r.MapPost("/agent/v1/update-log", async (JsonElement b, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            var ag = await AgentsApi.AuthAgent(http, db, ct); if (ag == null) return Results.Json(new { error = "agent_auth" }, statusCode: 401);
            var msg = AgentsApi.Str(b, "msg") ?? ""; var level = AgentsApi.Str(b, "level") == "warn" ? "warn" : "info";
            if (msg != "") await Note(db, ag.Value.Id, level, msg, ct);
            if (AgentsApi.Str(b, "state") == "failed")
            {
                await using var c = new NpgsqlCommand("update assets.agents set upd_state = 'failed', upd_at = now() where id = @i and upd_state = 'running'", db);
                AgentsApi.P(c, "i", ag.Value.Id); await c.ExecuteNonQueryAsync(ct);
            }
            return Results.Ok(new { ok = true });
        }).RequireModule(key);
    }
}
