using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace DeskAsk.Platform.Assets;

/// <summary>
/// The elevated live shell (cmd, PowerShell or bash, run as SYSTEM or root by the agent) and remote control with a self-hosted RustDesk.
/// Both pass through the same checks as remote commands. Every line typed in a shell and every remote session is written to the audit log.
/// </summary>
public static class ShellApi
{
    static readonly Regex RcHost = new(@"^[A-Za-z0-9.-]{1,200}(:\d{1,5})?\z", RegexOptions.Compiled);
    const string RcKey = "assets.rustdesk";
    static IResult Bad(string e, string? d = null) => AgentsApi.Bad(e, d);
    static void P(NpgsqlCommand c, string n, object? v) => AgentsApi.P(c, n, v);

    sealed class Rd { public bool Enabled { get; set; } public string IdServer { get; set; } = ""; public string Relay { get; set; } = ""; public string Key { get; set; } = ""; public string Installer { get; set; } = ""; public string Sha256 { get; set; } = ""; }
    static async Task<Rd> LoadRd(ISettings s, CancellationToken ct)
    {
        var raw = await s.GetAsync(RcKey, ct);
        try { return raw == null ? new Rd() : JsonSerializer.Deserialize<Rd>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new Rd(); } catch (JsonException) { return new Rd(); }
    }
    static object RdView(Rd r) => new { enabled = r.Enabled, idServer = r.IdServer, relay = r.Relay, key = r.Key, installer = r.Installer, sha256 = r.Sha256 };

    static async Task Note(NpgsqlConnection db, string agentId, string level, string text, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("insert into assets.rc_log (agent_id, level, text) values (@a,@l,@t)", db); P(c, "a", agentId); P(c, "l", level); P(c, "t", AgentsApi.Clip(text, 400)); await c.ExecuteNonQueryAsync(ct);
        await using var d = new NpgsqlCommand("delete from assets.rc_log where agent_id = @a and id < (select max(id) from assets.rc_log where agent_id = @a) - 300", db); P(d, "a", agentId); await d.ExecuteNonQueryAsync(ct);
    }

    static async Task<(string Id, long AssetId, string By, bool Closed, string AgentId, string Shell)?> MyShell(NpgsqlConnection db, string sid, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("select id, asset_id, by_name, closed, agent_id, shell from assets.shells where id = @i", db); P(c, "i", sid);
        await using var r = await c.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetBoolean(3), r.GetString(4), r.GetString(5)) : null;
    }
    static async Task<int> NextSeq(NpgsqlConnection db, string sid, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("update assets.shells set seq = seq + 1 where id = @i returning seq", db); P(c, "i", sid);
        return await c.ExecuteScalarAsync(ct) is int n ? n : 1;
    }
    static string RcState(string state, DateTime? at) => state == "pending" && at != null && DateTime.UtcNow - at.Value > TimeSpan.FromMinutes(15) ? "stalled" : state;

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        var admin = (RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole("admin")).RequireModule(key);
        var open = (RouteHandlerBuilder b) => b.RequireModule(key);

        // ================================================================ live shell
        admin(r.MapPost("/api/assets/{id:long}/shell", async (long id, JsonElement body, ClaimsPrincipal u, ITenantContext t, IConfiguration cfg, AgentHub hub, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            var (err, tg) = await AgentsApi.Resolve(db, id, true, AgentsApi.Offline(cfg), ct); if (err != null) return err;
            string shell;
            try { var asked = AgentsApi.Str(body, "shell"); shell = AgentsApi.ShellFor(tg!.Platform, string.IsNullOrEmpty(asked) ? (tg.Platform == "windows" ? "cmd" : "bash") : asked); }
            catch (FormatException e) { return Bad("bad_input", e.Message); }
            await using (var cnt = new NpgsqlCommand("select count(*) from assets.shells where asset_id = @a and not closed", db)) { P(cnt, "a", id); if ((long)(await cnt.ExecuteScalarAsync(ct))! >= 3) return Bad("too_many_shells", "Three shells are already open on this computer. Close one first."); }
            var sid = "s" + AgentsApi.Rand(12);
            await using (var ins = new NpgsqlCommand("insert into assets.shells (id, asset_id, agent_id, shell, by_name) values (@i,@a,@g,@s,@b)", db)) { P(ins, "i", sid); P(ins, "a", id); P(ins, "g", tg!.AgentId); P(ins, "s", shell); P(ins, "b", AgentsApi.Actor(u)); await ins.ExecuteNonQueryAsync(ct); }
            await AgentsApi.Enqueue(db, hub, t, tg.AgentId, "open", new { sid, shell }, ct);
            await audit.WriteAsync("assets.shell_opened", id.ToString(), "elevated " + shell + " shell on " + tg.AssetName, ct: ct);
            await AssetsDevice.History(db, id, AgentsApi.Actor(u), "command", JsonSerializer.Serialize(new { opened = "elevated " + shell + " shell" }), ct);
            return Results.Ok(new { sid, shell, platform = tg.Platform });
        }));

        admin(r.MapPost("/api/assets/shell/{sid}/input", async (string sid, JsonElement body, ClaimsPrincipal u, ITenantContext t, AgentHub hub, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            var s = await MyShell(db, sid, ct); if (s == null) return Results.NotFound();
            if (s.Value.Closed) return Bad("shell_closed", "This shell is closed.");
            var data = AgentsApi.Str(body, "data") ?? "";
            if (data == "") return Results.Ok(new { ok = true });
            if (data.Length > 20000) return Bad("too_much", "Too much text at once.");
            await using (var c = new NpgsqlCommand("update assets.shells set last_in = now(), input_log = right(input_log || @d, 20000) where id = @i", db)) { P(c, "d", data); P(c, "i", sid); await c.ExecuteNonQueryAsync(ct); }
            foreach (var line in Regex.Split(data, @"\r?\n").Select(l => l.Trim()).Where(l => l != "").Take(50))
                await audit.WriteAsync("assets.shell_input", s.Value.AssetId.ToString(), line.Length > 500 ? line[..500] : line, ct: ct);
            await AgentsApi.Enqueue(db, hub, t, s.Value.AgentId, "input", new { sid, data }, ct);
            return Results.Ok(new { ok = true });
        }));

        admin(r.MapGet("/api/assets/shell/{sid}/output", async (string sid, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            if (await MyShell(db, sid, ct) == null) return Results.NotFound();
            long.TryParse(http.Request.Query["since"], out var since); double.TryParse(http.Request.Query["wait"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var wait);
            async Task<List<(int Seq, string Data)>> Get()
            {
                var rows = new List<(int, string)>();
                await using var c = new NpgsqlCommand("select seq, data from assets.shell_out where sid = @s and seq > @q order by seq limit 200", db); P(c, "s", sid); P(c, "q", (int)since);
                await using var rd = await c.ExecuteReaderAsync(ct); while (await rd.ReadAsync(ct)) rows.Add((rd.GetInt32(0), rd.GetString(1)));
                return rows;
            }
            var got = await Get();
            for (int i = 0, loops = (int)Math.Min(Math.Max(wait, 0), 25) * 4; got.Count == 0 && i < loops && !ct.IsCancellationRequested; i++)
            {
                try { await Task.Delay(250, ct); } catch (OperationCanceledException) { break; }
                got = await Get();
                var cur0 = await MyShell(db, sid, ct); if (cur0 != null && cur0.Value.Closed && got.Count == 0) break;
            }
            var cur = await MyShell(db, sid, ct);
            var closed = got.Any(x => x.Data == "\0closed") || (cur != null && cur.Value.Closed && got.Count == 0);
            var chunks = got.Where(x => x.Data != "\0closed").Select(x => new { seq = x.Seq, data = x.Data }).ToList();
            return Results.Ok(new { chunks, seq = got.Count > 0 ? got[^1].Seq : (int)since, closed });
        }));

        admin(r.MapPost("/api/assets/shell/{sid}/close", async (string sid, ITenantContext t, AgentHub hub, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            var s = await MyShell(db, sid, ct); if (s == null) return Results.NotFound();
            if (!s.Value.Closed)
            {
                await AgentsApi.Enqueue(db, hub, t, s.Value.AgentId, "close", new { sid }, ct);
                await using var c = new NpgsqlCommand("update assets.shells set closed = true where id = @i", db); P(c, "i", sid); await c.ExecuteNonQueryAsync(ct);
                await audit.WriteAsync("assets.shell_closed", s.Value.AssetId.ToString(), ct: ct);
            }
            return Results.Ok(new { ok = true });
        }));

        open(r.MapPost("/agent/v1/shell", async (JsonElement body, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            var ag = await AgentsApi.AuthAgent(http, db, ct); if (ag == null) return Results.Json(new { error = "agent_auth" }, statusCode: 401);
            var s = await MyShell(db, AgentsApi.Str(body, "sid") ?? "", ct);
            if (s == null || s.Value.AgentId != ag.Value.Id) return Results.NotFound(new { error = "unknown_session" });
            var data = AgentsApi.Str(body, "data") ?? ""; if (data.Length > 64 * 1024) data = data[..(64 * 1024)];
            async Task Put(string d) { var seq = await NextSeq(db, s.Value.Id, ct); await using var c = new NpgsqlCommand("insert into assets.shell_out (sid, seq, data) values (@s,@q,@d)", db); P(c, "s", s.Value.Id); P(c, "q", seq); P(c, "d", d); await c.ExecuteNonQueryAsync(ct); }
            if (data != "") await Put(data);
            if (body.TryGetProperty("closed", out var cl) && cl.ValueKind == JsonValueKind.True)
            {
                await using var c = new NpgsqlCommand("update assets.shells set closed = true where id = @i", db); P(c, "i", s.Value.Id); await c.ExecuteNonQueryAsync(ct);
                await Put("\0closed");
            }
            return Results.Ok(new { ok = true });
        }));

        // ================================================================ remote control (RustDesk)
        admin(r.MapGet("/api/assets/settings/remote-control", async (ISettings s, CancellationToken ct) => Results.Ok(RdView(await LoadRd(s, ct)))));
        admin(r.MapPut("/api/assets/settings/remote-control", async (JsonElement b, ISettings s, IAuditLog audit, CancellationToken ct) =>
        {
            string G(string n, int max) => AgentsApi.Clip(AgentsApi.Str(b, n), max).Trim();
            var rd = new Rd { IdServer = G("idServer", 200), Relay = G("relay", 200), Key = G("key", 120), Installer = G("installer", 500), Sha256 = G("sha256", 64).ToLowerInvariant(), Enabled = b.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True };
            if (rd.IdServer != "" && !RcHost.IsMatch(rd.IdServer)) return Bad("bad_input", "The RustDesk server address must be a name or IP address, optionally with :port (for example rd.example.com).");
            if (rd.Relay != "" && !RcHost.IsMatch(rd.Relay)) return Bad("bad_input", "The relay address must be a name or IP address, optionally with :port.");
            if (rd.Key != "" && !Regex.IsMatch(rd.Key, @"^[A-Za-z0-9+/=_-]{20,120}\z")) return Bad("bad_input", "The public key looks wrong. Copy the content of id_ed25519.pub from your RustDesk server.");
            if (rd.Installer != "" && !Regex.IsMatch(rd.Installer, @"^https://\S+\z")) return Bad("bad_input", "The installer address must start with https://.");
            if (rd.Sha256 != "" && !Regex.IsMatch(rd.Sha256, @"^[0-9a-f]{64}\z")) return Bad("bad_input", "The SHA-256 must be 64 hexadecimal characters (or empty).");
            if (rd.Enabled && (rd.IdServer == "" || rd.Installer == "")) return Bad("bad_input", "To switch it on, fill in the RustDesk server address and the installer address.");
            await s.SetAsync(RcKey, JsonSerializer.Serialize(rd), ct);
            await audit.WriteAsync("assets.remote_control_settings", "", (rd.Enabled ? "enabled" : "disabled") + (rd.IdServer != "" ? " for " + rd.IdServer : ""), ct: ct);
            return Results.Ok(RdView(rd));
        }));

        async Task<(string AgentId, string Platform, string State, string Id, string Mode, string Msg, DateTime? At, string Pw, string Version, string AssetName)?> RcAgent(NpgsqlConnection db, long assetId, CancellationToken ct)
        {
            await using var c = new NpgsqlCommand("select g.id, g.platform, g.rd_state, g.rd_id, g.rd_mode, g.rd_msg, g.rd_at, g.rd_pw, g.version, a.name from assets.assets a join assets.agents g on g.id = a.agent_id where a.id = @i", db); P(c, "i", assetId);
            await using var rd = await c.ExecuteReaderAsync(ct);
            return await rd.ReadAsync(ct) ? (rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetString(3), rd.GetString(4), rd.GetString(5), rd.IsDBNull(6) ? null : rd.GetDateTime(6), rd.GetString(7), rd.GetString(8), rd.GetString(9)) : null;
        }

        admin(r.MapGet("/api/assets/{id:long}/remote-control", async (long id, ISettings s, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct); var rd = await LoadRd(s, ct); var a = await RcAgent(db, id, ct);
            return Results.Ok(new { enabled = rd.Enabled, hasAgent = a != null, windows = a?.Platform.StartsWith("windows", StringComparison.OrdinalIgnoreCase) == true, state = a == null ? "" : RcState(a.Value.State, a.Value.At), agentVersion = a?.Version ?? "", id = a?.Id ?? "", mode = a?.Mode ?? "", msg = a?.Msg ?? "" });
        }));

        admin(r.MapGet("/api/assets/{id:long}/remote-control/log", async (long id, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct); var a = await RcAgent(db, id, ct);
            if (a == null) return Results.Ok(new { items = new List<object>(), state = "" });
            long.TryParse(http.Request.Query["after"], out var after);
            await using var c = new NpgsqlCommand(after > 0 ? "select id, at, level, text from assets.rc_log where agent_id = @a and id > @q order by id limit 300" : "select id, at, level, text from (select id, at, level, text from assets.rc_log where agent_id = @a order by id desc limit 300) x order by id", db);
            P(c, "a", a.Value.AgentId); P(c, "q", after);
            var rows = new List<object>(); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) rows.Add(new { seq = rd.GetInt64(0), at = rd.GetDateTime(1), level = rd.GetString(2), text = rd.GetString(3) });
            return Results.Ok(new { items = rows, state = RcState(a.Value.State, a.Value.At) });
        }));

        admin(r.MapPost("/api/assets/{id:long}/remote-control/setup", async (long id, JsonElement b, ClaimsPrincipal u, ISettings s, ITenantContext t, IConfiguration cfg, AgentHub hub, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            var (err, tg) = await AgentsApi.Resolve(db, id, true, AgentsApi.Offline(cfg), ct); if (err != null) return err;
            var rd = await LoadRd(s, ct); if (!rd.Enabled) return Bad("not_enabled", "Remote control is not switched on. Set it up under Settings, Remote control.");
            if (!tg!.Platform.StartsWith("windows", StringComparison.OrdinalIgnoreCase)) return Bad("windows_only", "Remote control is available for Windows computers only for now.");
            var mode = AgentsApi.Str(b, "mode") == "attended" ? "attended" : "unattended";
            var pw = mode == "unattended" ? AgentsApi.Rand(12) : "";
            await using (var c = new NpgsqlCommand("update assets.agents set rd_state = 'pending', rd_mode = @m, rd_pw = @p, rd_msg = '', rd_at = now() where id = @i", db))
            { P(c, "m", mode); P(c, "p", pw != "" ? SecretBox.Seal(cfg, t.Require().Id, pw) : ""); P(c, "i", tg.AgentId); await c.ExecuteNonQueryAsync(ct); }
            await using (var d = new NpgsqlCommand("delete from assets.rc_log where agent_id = @a", db)) { P(d, "a", tg.AgentId); await d.ExecuteNonQueryAsync(ct); }
            await Note(db, tg.AgentId, "info", "Request sent (" + mode + "). Waiting for the agent on the computer to pick it up; it does so within about a minute.", ct);
            await AgentsApi.Enqueue(db, hub, t, tg.AgentId, "rdsetup", new { rd = new { idServer = rd.IdServer, relay = rd.Relay, key = rd.Key, installer = rd.Installer, sha256 = rd.Sha256, password = pw, mode } }, ct);
            await audit.WriteAsync("assets.remote_control_setup", id.ToString(), mode + " on " + tg.AssetName, ct: ct);
            await AssetsDevice.History(db, id, AgentsApi.Actor(u), "remote", JsonSerializer.Serialize(new { setup = mode }), ct);
            return Results.Ok(new { ok = true });
        }));

        admin(r.MapPost("/api/assets/{id:long}/remote-control/remove", async (long id, ClaimsPrincipal u, ITenantContext t, IConfiguration cfg, AgentHub hub, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            var (err, tg) = await AgentsApi.Resolve(db, id, false, AgentsApi.Offline(cfg), ct); if (err != null) return err;
            await AgentsApi.Enqueue(db, hub, t, tg!.AgentId, "rdremove", new { }, ct);
            await using (var c = new NpgsqlCommand("update assets.agents set rd_state = '', rd_id = '', rd_pw = '', rd_msg = '', rd_at = now() where id = @i", db)) { P(c, "i", tg.AgentId); await c.ExecuteNonQueryAsync(ct); }
            await audit.WriteAsync("assets.remote_control_removed", id.ToString(), tg.AssetName, ct: ct);
            await AssetsDevice.History(db, id, AgentsApi.Actor(u), "remote", JsonSerializer.Serialize(new { removed = true }), ct);
            return Results.Ok(new { ok = true });
        }));

        admin(r.MapPost("/api/assets/{id:long}/remote-control/connect", async (long id, JsonElement b, ClaimsPrincipal u, ITenantContext t, IConfiguration cfg, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            var (err, tg) = await AgentsApi.Resolve(db, id, true, AgentsApi.Offline(cfg), ct); if (err != null) return err;
            var a = await RcAgent(db, id, ct);
            if (a == null || a.Value.State != "ready" || a.Value.Id == "") return Bad("not_ready", "Remote control is not ready on this computer yet. Set it up first and wait until it shows Ready.");
            var pw = a.Value.Pw != "" ? SecretBox.Open(cfg, t.Require().Id, a.Value.Pw) : "";
            var via = AgentsApi.Clip(AgentsApi.Str(b, "via"), 40);
            await audit.WriteAsync("assets.remote_session", id.ToString(), "RustDesk " + a.Value.Id + ", " + a.Value.Mode + (via != "" ? " from " + via : "") + " on " + tg!.AssetName, ct: ct);
            await AssetsDevice.History(db, id, AgentsApi.Actor(u), "remote", JsonSerializer.Serialize(new { session = "remote control session started" }), ct);
            var link = "rustdesk://connection/new/" + Uri.EscapeDataString(a.Value.Id) + (pw != "" ? "?password=" + Uri.EscapeDataString(pw) : "");
            return Results.Ok(new { id = a.Value.Id, mode = a.Value.Mode, link, password = pw });
        }));

        open(r.MapPost("/agent/v1/remote", async (JsonElement b, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            var ag = await AgentsApi.AuthAgent(http, db, ct); if (ag == null) return Results.Json(new { error = "agent_auth" }, statusCode: 401);
            var st = AgentsApi.Str(b, "state") ?? "";
            if (st == "log") { await Note(db, ag.Value.Id, AgentsApi.Str(b, "level") == "warn" ? "warn" : "info", AgentsApi.Str(b, "msg") ?? "", ct); return Results.Ok(new { ok = true }); }
            if (st == "failed") await Note(db, ag.Value.Id, "warn", "Failed: " + AgentsApi.Str(b, "msg"), ct);
            var rid = Regex.Replace(AgentsApi.Str(b, "id") ?? "", @"[^0-9A-Za-z_-]", ""); if (rid.Length > 40) rid = rid[..40];
            string? sql = st == "ready" && rid != "" ? "update assets.agents set rd_state = 'ready', rd_id = @id, rd_msg = '', rd_at = now() where id = @a"
                : st == "failed" ? "update assets.agents set rd_state = 'failed', rd_msg = @m, rd_at = now() where id = @a"
                : st == "removed" ? "update assets.agents set rd_state = '', rd_id = '', rd_pw = '', rd_msg = '', rd_at = now() where id = @a" : null;
            if (sql != null) { await using var c = new NpgsqlCommand(sql, db); P(c, "id", rid); P(c, "m", AgentsApi.Clip(AgentsApi.Str(b, "msg"), 400)); P(c, "a", ag.Value.Id); await c.ExecuteNonQueryAsync(ct); }
            return Results.Ok(new { ok = true });
        }));
    }
}
