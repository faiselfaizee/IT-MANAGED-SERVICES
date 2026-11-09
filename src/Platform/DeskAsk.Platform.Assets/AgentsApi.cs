using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace DeskAsk.Platform.Assets;

/// <summary>Wakes a waiting agent (long poll) as soon as something is queued for it.</summary>
public sealed class AgentHub
{
    readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> waiters = new();
    public void Wake(string key) { if (waiters.TryRemove(key, out var w)) w.TrySetResult(true); }
    public async Task WaitAsync(string key, TimeSpan max, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (waiters.TryGetValue(key, out var old)) old.TrySetResult(true);
        waiters[key] = tcs;
        try { await Task.WhenAny(tcs.Task, Task.Delay(max, ct)); }
        catch (OperationCanceledException) { }
        finally { if (waiters.TryGetValue(key, out var cur) && cur == tcs) waiters.TryRemove(key, out _); }
    }
}

/// <summary>Every minute: jobs nobody picked up time out, old queue entries and finished jobs are removed.</summary>
public sealed class AssetsJanitorJob : IJobHandler
{
    public const string JobType = "assets.janitor";
    public string Type => JobType;
    public async Task RunAsync(JobContext job, CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(job.Tenant.ConnectionString); await db.OpenAsync(ct);
        async Task Sql(string sql) { await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(ct); }
        await Sql("update assets.jobs set status = 'timeout', finished_at = now(), output = output || E'\\n[The agent did not pick this up in time.]' where status in ('queued','sent') and created_at < now() - interval '15 minutes'");
        await Sql("update assets.jobs set status = 'timeout', finished_at = now() where status = 'running' and started_at < now() - interval '3 hours'");
        await Sql("update assets.shells set closed = true where not closed and last_in < now() - interval '30 minutes'");
        await Sql("delete from assets.shell_out where at < now() - interval '2 days'");
        await Sql("update assets.agents set rd_state = 'stalled' where rd_state = 'pending' and rd_at < now() - interval '15 minutes'");
        await Sql("delete from assets.agent_q where created_at < now() - interval '3 days'");
        await Sql("delete from assets.jobs where created_at < now() - interval '180 days'");
    }
}

/// <summary>
/// Agents: enrolment tokens, the agent's own API (the wire format of the existing DeskAsk agent: enrol, check-in, long-poll, job results, downloads, install scripts),
/// the troubleshooting command library and remote commands. Remote work needs an administrator, an agent that is not locked, the asset switch and the company switch.
/// </summary>
public static class AgentsApi
{
    internal static IResult Bad(string error, string? detail = null) => Results.BadRequest(new { error, detail });
    internal static string Actor(ClaimsPrincipal u) => u.FindFirstValue("email") ?? u.FindFirstValue("sub") ?? "unknown";
    internal static async Task<NpgsqlConnection> Open(ITenantContext t, CancellationToken ct) { var db = new NpgsqlConnection(t.Require().ConnectionString); await db.OpenAsync(ct); return db; }
    internal static void P(NpgsqlCommand c, string n, object? v) => c.Parameters.AddWithValue(n, v ?? DBNull.Value);
    internal static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    internal static string Rand(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static JsonElement Clone(string json) => JsonDocument.Parse(json).RootElement.Clone();
    internal static string? Str(JsonElement b, string n) => b.ValueKind == JsonValueKind.Object && b.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    internal static string Clip(string? s, int max) { s ??= ""; return s.Length <= max ? s : s[..max]; }
    internal static string Ip(HttpContext h) => h.Connection.RemoteIpAddress?.ToString() ?? "";

    public static string PublicUrl(HttpContext h, IConfiguration cfg) => (cfg["Assets:PublicUrl"] ?? (h.Request.Scheme + "://" + h.Request.Host)).TrimEnd('/');
    static string AgentDir(IConfiguration cfg) => cfg["Assets:AgentDir"] ?? Path.Combine(AppContext.BaseDirectory, "agent-dist");
    static string AgentVersion(IConfiguration cfg) { try { return File.ReadAllText(Path.Combine(AgentDir(cfg), "version.txt")).Trim(); } catch { return ""; } }
    static readonly Dictionary<string, string> Files = new() { ["windows"] = "deskask-agent-windows-amd64.exe", ["linux"] = "deskask-agent-linux-amd64", ["linux-arm64"] = "deskask-agent-linux-arm64" };
    internal static string? AgentFile(IConfiguration cfg, string platform) => Files.TryGetValue(platform, out var f) && File.Exists(Path.Combine(AgentDir(cfg), f)) ? Path.Combine(AgentDir(cfg), f) : null;
    internal static string LinuxFlavour(HttpContext h) => h.Request.Query["arch"] == "arm64" ? "linux-arm64" : "linux";

    static bool BadSerial(string s) { var v = s.Trim().ToLowerInvariant(); return v.Length < 4 || v.Contains("o.e.m") || v.Contains("default string") || v.Contains("not specified") || v.Contains("to be filled") || v.Contains("system serial") || v.Trim('0', '-', ' ', 'x', 'f') == ""; }

    // ---------------------------------------------------------------- install scripts
    internal const string PsAdmin = "if(-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Run this in PowerShell as administrator.'}";
    internal static string Fill(string t, string url, string tok) => t.Replace("@@ADMIN@@", PsAdmin).Replace("@@URL@@", url).Replace("@@TOK@@", tok).Replace("\r\n", "\n");
    public static string InstallPs1(string url, string tok) => Fill(@"# DeskAsk agent installer for Windows - run in an elevated PowerShell
$ErrorActionPreference='Stop'
@@ADMIN@@
[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
$dir=Join-Path $env:ProgramFiles 'DeskAskAgent'; New-Item -ItemType Directory -Force -Path $dir | Out-Null
$exe=Join-Path $dir 'deskask-agent.exe'
if(Get-ScheduledTask -TaskName DeskAskAgent -ErrorAction SilentlyContinue){Stop-ScheduledTask -TaskName DeskAskAgent -ErrorAction SilentlyContinue; Get-Process deskask-agent -ErrorAction SilentlyContinue | Stop-Process -Force}
Invoke-WebRequest -UseBasicParsing -Uri '@@URL@@/agent/download/windows?t=@@TOK@@' -OutFile $exe
& $exe install --server '@@URL@@' --token '@@TOK@@'
", url, tok);
    public static string InstallSh(string url, string tok) => Fill(@"#!/bin/sh
# DeskAsk agent installer for Linux - run as root:  curl -fsSL '@@URL@@/agent/install.sh?t=@@TOK@@' | sudo sh
set -e
[ ""$(id -u)"" = 0 ] || { echo ""Run as root (sudo).""; exit 1; }
arch=$(uname -m); case ""$arch"" in x86_64) p=linux;; aarch64|arm64) p=linux-arm64;; *) echo ""Unsupported CPU $arch""; exit 1;; esac
mkdir -p /opt/deskask-agent
curl -fsSL -o /opt/deskask-agent/deskask-agent ""@@URL@@/agent/download/$p?t=@@TOK@@""
chmod 755 /opt/deskask-agent/deskask-agent
/opt/deskask-agent/deskask-agent install --server '@@URL@@' --token '@@TOK@@'
", url, tok);
    static object Commands(string url, string tok) => new
    {
        windows = "powershell -NoProfile -ExecutionPolicy Bypass -Command \"iwr -UseBasicParsing '" + url + "/agent/install.ps1?t=" + tok + "' | iex\"",
        linux = "curl -fsSL '" + url + "/agent/install.sh?t=" + tok + "' | sudo sh"
    };

    // ---------------------------------------------------------------- agent authentication and queue
    internal static async Task<(string Id, long AssetId, int? Company, string Platform, string Version, bool Locked, bool ShellOk)?> AuthAgent(HttpContext http, NpgsqlConnection db, CancellationToken ct)
    {
        var m = Regex.Match(http.Request.Headers.Authorization.ToString(), @"^Bearer\s+(dkag_[A-Za-z0-9_-]{20,})$");
        var id = http.Request.Headers["X-Agent-Id"].ToString();
        if (!m.Success || id == "") return null;
        await using var c = new NpgsqlCommand("select key_hash, asset_id, company_id, platform, version, locked, shell_ok from assets.agents where id = @i", db); P(c, "i", id);
        await using var r = await c.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(r.GetString(0)), Encoding.ASCII.GetBytes(Hash(m.Groups[1].Value)))) return null;
        return (id, r.IsDBNull(1) ? 0 : r.GetInt64(1), r.IsDBNull(2) ? null : r.GetInt32(2), r.GetString(3), r.GetString(4), r.GetBoolean(5), r.GetBoolean(6));
    }

    public static async Task Enqueue(NpgsqlConnection db, AgentHub hub, ITenantContext t, string agentId, string kind, object payload, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("insert into assets.agent_q (agent_id, kind, payload) values (@a, @k, @p)", db);
        P(c, "a", agentId); P(c, "k", kind); P(c, "p", JsonSerializer.Serialize(payload));
        await c.ExecuteNonQueryAsync(ct);
        hub.Wake(t.Require().Id + ":" + agentId);
    }

    static async Task<List<Dictionary<string, object?>>> Pending(NpgsqlConnection db, string agentId, CancellationToken ct)
    {
        var msgs = new List<Dictionary<string, object?>>(); var ids = new List<long>(); var jobIds = new List<string>();
        await using (var c = new NpgsqlCommand("select id, kind, payload from assets.agent_q where agent_id = @a and not delivered order by id limit 50", db))
        {
            P(c, "a", agentId); await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                ids.Add(r.GetInt64(0)); var m = new Dictionary<string, object?> { ["type"] = r.GetString(1) };
                foreach (var p in JsonDocument.Parse(r.GetString(2)).RootElement.EnumerateObject()) m[p.Name] = p.Value.Clone();
                if (r.GetString(1) == "job" && m.TryGetValue("id", out var jid) && jid is JsonElement je && je.ValueKind == JsonValueKind.String) jobIds.Add(je.GetString()!);
                msgs.Add(m);
            }
        }
        if (ids.Count > 0)
        {
            await using var u = new NpgsqlCommand("update assets.agent_q set delivered = true where id = any(@i)", db); P(u, "i", ids.ToArray()); await u.ExecuteNonQueryAsync(ct);
            if (jobIds.Count > 0) { await using var j = new NpgsqlCommand("update assets.jobs set status = 'sent' where id = any(@i) and status = 'queued'", db); P(j, "i", jobIds.ToArray()); await j.ExecuteNonQueryAsync(ct); }
        }
        return msgs;
    }

    static async Task Touch(NpgsqlConnection db, string agentId, long assetId, string ip, string? version, string? host, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("update assets.agents set last_seen = now(), ip = @ip, version = coalesce(nullif(@v, ''), version), hostname = coalesce(nullif(@h, ''), hostname) where id = @i", db);
        P(c, "ip", Clip(ip, 64)); P(c, "v", Clip(version, 40)); P(c, "h", Clip(host, 120)); P(c, "i", agentId); await c.ExecuteNonQueryAsync(ct);
        await using var a = new NpgsqlCommand("update assets.assets set last_seen = now() where id = @i", db); P(a, "i", assetId); await a.ExecuteNonQueryAsync(ct);
    }

    sealed record UpdateInfo(string version, string platform);
    static UpdateInfo? UpdateFor(IConfiguration cfg, string? have, string platform)
    {
        var v = AgentVersion(cfg); if (v == "" || string.IsNullOrEmpty(have) || v == have) return null;
        var pf = platform == "windows" ? "windows" : "linux";
        return AgentFile(cfg, pf) == null ? null : new UpdateInfo(v, pf);
    }

    // ---------------------------------------------------------------- remote command helpers
    static readonly Regex ParamOk = new(@"^[A-Za-z0-9 ._:@%\\/\-]{0,200}\z", RegexOptions.Compiled);
    static string FillParams(string body, JsonElement defs, JsonElement vals)
    {
        return Regex.Replace(body, @"\{\{(\w+)\}\}", m =>
        {
            var k = m.Groups[1].Value; string label = k, def = "";
            if (defs.ValueKind == JsonValueKind.Array)
                foreach (var d in defs.EnumerateArray()) if (Str(d, "name") == k) { def = Str(d, "default") ?? ""; label = string.IsNullOrEmpty(Str(d, "label")) ? k : Str(d, "label")!; }
            var v = Str(vals, k); if (string.IsNullOrEmpty(v)) v = def;
            if (!ParamOk.IsMatch(v)) throw new FormatException("The value for \"" + label + "\" has characters that are not allowed (letters, numbers, space and . _ : @ % \\ / - only).");
            if (v == "") throw new FormatException("Enter a value for \"" + label + "\".");
            return v;
        });
    }

    internal sealed record Target(long AssetId, string AssetName, string AgentId, string Platform);
    /// <summary>All the checks that stand between an engineer and a computer. Returns an error result or the target.</summary>
    internal static async Task<(IResult? Error, Target? T)> Resolve(NpgsqlConnection db, long assetId, bool needOnline, int offlineSec, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand(@"select a.name, a.agent_id, a.remote_ok, a.last_seen, g.platform, g.shell_ok, g.locked, a.company_id, coalesce(co.remote_allowed, true), coalesce(co.name, '')
            from assets.assets a left join assets.agents g on g.id = a.agent_id left join assets.companies co on co.id = a.company_id where a.id = @i", db); P(c, "i", assetId);
        await using var r = await c.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return (Results.NotFound(), null);
        var name = r.GetString(0); var agent = r.GetString(1);
        if (agent == "" || r.IsDBNull(4)) return (Bad("no_agent", "This asset has no agent installed. Install the agent first."), null);
        if (!r.GetBoolean(2) || !r.GetBoolean(5)) return (Results.Json(new { error = "remote_blocked", detail = "Remote tools are blocked for this asset." }, statusCode: 403), null);
        if (r.GetBoolean(6)) return (Results.Json(new { error = "agent_locked", detail = "This computer was installed as monitoring-only. Remote commands are switched off on the computer itself." }, statusCode: 403), null);
        if (!r.GetBoolean(8)) return (Results.Json(new { error = "company_blocked", detail = "Remote access is not allowed for " + r.GetString(9) + ". Allow it in the company settings." }, statusCode: 403), null);
        if (needOnline && (r.IsDBNull(3) || r.GetDateTime(3) < DateTime.UtcNow.AddSeconds(-offlineSec)))
            return (Bad("agent_offline", "The agent on " + name + " is offline."), null);
        return (null, new Target(assetId, name, agent, r.GetString(4)));
    }
    internal static int Offline(IConfiguration cfg) => int.TryParse(cfg["Assets:AgentOfflineSeconds"], out var s) && s > 0 ? s : 900;
    internal static string ShellFor(string platform, string? shell)
    {
        var w = platform == "windows";
        if (w && shell is not ("cmd" or "powershell")) throw new FormatException("Choose cmd or PowerShell for a Windows computer.");
        if (!w && shell != "bash") throw new FormatException("Linux computers use bash.");
        return shell!;
    }

    static object JobOut(NpgsqlDataReader r, bool full)
    {
        string? output = full ? r.GetString(11) : null;
        return new { id = r.GetString(0), assetId = r.GetInt64(1), shell = r.GetString(2), title = r.GetString(3), status = r.GetString(4), by = r.GetString(5), created = r.GetDateTime(6), started = r.IsDBNull(7) ? (DateTime?)null : r.GetDateTime(7), finished = r.IsDBNull(8) ? (DateTime?)null : r.GetDateTime(8), exit = r.IsDBNull(9) ? (int?)null : r.GetInt32(9), timeout = r.GetInt32(10), body = full ? r.GetString(12) : null, output };
    }
    const string JobCols = "j.id, j.asset_id, j.shell, j.title, j.status, j.by_name, j.created_at, j.started_at, j.finished_at, j.exit_code, j.timeout_s, j.output, j.body";

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        var admin = (RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole("admin")).RequireModule(key);
        var open = (RouteHandlerBuilder b) => b.RequireModule(key);

        // ================================================================ agent-facing
        open(r.MapPost("/agent/v1/enroll", async (JsonElement body, HttpContext http, ITenantContext t, IConfiguration cfg, IEntitlements ent, AgentHub hub, IAuditLog audit, CancellationToken ct) =>
        {
            var tok = Str(body, "token") ?? "";
            await using var db = await Open(t, ct);
            int? company = null; long? tokAsset = null; bool monitorOnly = false; int tokId;
            await using (var c = new NpgsqlCommand("select id, company_id, asset_id, monitor_only, revoked, expires_at from assets.enroll_tokens where token_hash = @h and kind = 'agent'", db))
            {
                P(c, "h", Hash(tok)); await using var rd = await c.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct) || rd.GetBoolean(4) || (!rd.IsDBNull(5) && rd.GetDateTime(5) < DateTime.UtcNow))
                    return Results.Json(new { error = "token_invalid", detail = "This enrolment token is not valid (revoked or expired)." }, statusCode: 401);
                tokId = rd.GetInt32(0); company = rd.IsDBNull(1) ? null : rd.GetInt32(1); tokAsset = rd.IsDBNull(2) ? null : rd.GetInt64(2); monitorOnly = rd.GetBoolean(3);
            }
            var inv = body.TryGetProperty("inventory", out var iv) && iv.ValueKind == JsonValueKind.Object ? iv : default;
            var norm = Inventory.Normalize(inv);
            var host = Clip(norm.Hostname ?? Str(body, "hostname"), 120); var serial = Clip(norm.Serial, 120);
            if (host == "" && serial == "") return Bad("no_host", "The agent did not send a host name.");

            await using var tx = await db.BeginTransactionAsync(ct);
            long? assetId = null; string oldAgent = "";
            async Task<long?> One(string sql, params (string, object?)[] ps)
            {
                await using var q = new NpgsqlCommand(sql, db, tx); foreach (var (n, v) in ps) P(q, n, v);
                return await q.ExecuteScalarAsync(ct) is long l ? l : null;
            }
            if (tokAsset != null) assetId = await One("select id from assets.assets where id = @i and agent_id = ''", ("i", tokAsset));
            if (assetId == null && serial != "") assetId = await One("select id from assets.assets where lower(serial) = lower(@s) and company_id is not distinct from @c and agent_id = '' limit 1", ("s", serial), ("c", company));
            if (assetId == null && host != "") assetId = await One("select id from assets.assets where (lower(hostname) = lower(@h) or lower(dns_name) = lower(@h)) and company_id is not distinct from @c and agent_id = '' limit 1", ("h", host), ("c", company));
            if (assetId == null && serial != "" && !BadSerial(serial))
            {
                var macs = JsonSerializer.Deserialize<List<string>>(norm.Macs) ?? new();
                if (macs.Count > 0)
                {
                    await using var q = new NpgsqlCommand("select id, agent_id from assets.assets where lower(serial) = lower(@s) and company_id is not distinct from @c and mac_addresses ?| @m limit 1", db, tx);
                    P(q, "s", serial); P(q, "c", company); P(q, "m", macs.ToArray());
                    await using var rd = await q.ExecuteReaderAsync(ct);
                    if (await rd.ReadAsync(ct)) { assetId = rd.GetInt64(0); oldAgent = rd.GetString(1); }
                }
            }
            var agentId = "g" + Rand(10); var agentKey = "dkag_" + Rand(30);
            if (assetId == null)
            {
                await using var cnt = new NpgsqlCommand("select count(*) from assets.assets where status not in ('retired','lost')", db, tx);
                var active = (long)(await cnt.ExecuteScalarAsync(ct))!;
                if (!await ent.WithinLimitAsync(key, "max_assets", active, ct)) return Results.Json(new { error = "limit_reached", limit = "max_assets" }, statusCode: 402);
                await using var ins = new NpgsqlCommand("insert into assets.assets (name, status, company_id, hostname, source, created_by, category) values (@n, 'in_use', @c, @h, 'agent', 'agent', 'IT hardware') returning id", db, tx);
                P(ins, "n", host != "" ? host : serial); P(ins, "c", company); P(ins, "h", host);
                assetId = (long)(await ins.ExecuteScalarAsync(ct))!;
                await AssetsDevice.History(db, assetId.Value, "agent", "created", JsonSerializer.Serialize(new { by = "agent", host }), ct, tx);
            }
            else if (oldAgent != "")
            {
                await using var d = new NpgsqlCommand("delete from assets.agents where id = @a", db, tx); P(d, "a", oldAgent); await d.ExecuteNonQueryAsync(ct);
                await AssetsDevice.History(db, assetId.Value, "agent", "agent", JsonSerializer.Serialize(new { note = "re-installed (same serial number and MAC address); the new agent replaced the old one" }), ct, tx);
            }
            else await AssetsDevice.History(db, assetId.Value, "agent", "agent", JsonSerializer.Serialize(new { note = "agent installed and linked" }), ct, tx);
            await using (var ia = new NpgsqlCommand("insert into assets.agents (id, company_id, asset_id, hostname, platform, version, key_hash, last_seen, ip, locked) values (@i,@c,@a,@h,@p,@v,@k,now(),@ip,@l)", db, tx))
            {
                P(ia, "i", agentId); P(ia, "c", company); P(ia, "a", assetId); P(ia, "h", host); P(ia, "p", Clip(Str(body, "platform"), 20)); P(ia, "v", Clip(Str(body, "version"), 40)); P(ia, "k", Hash(agentKey)); P(ia, "ip", Clip(Ip(http), 64)); P(ia, "l", monitorOnly);
                await ia.ExecuteNonQueryAsync(ct);
            }
            await using (var la = new NpgsqlCommand("update assets.assets set agent_id = @g, last_seen = now() where id = @i", db, tx)) { P(la, "g", agentId); P(la, "i", assetId); await la.ExecuteNonQueryAsync(ct); }
            await using (var ut = new NpgsqlCommand("update assets.enroll_tokens set uses = uses + 1 where id = @i", db, tx)) { P(ut, "i", tokId); await ut.ExecuteNonQueryAsync(ct); }
            if (inv.ValueKind == JsonValueKind.Object) await AssetsDevice.ApplyInventory(db, assetId.Value, norm, "agent", "agent", ct, tx);
            await tx.CommitAsync(ct);
            await audit.WriteAsync("assets.agent_enrolled", agentId, host, actor: "agent", ct: ct);
            return Results.Ok(new { agentId, key = agentKey, assetId = assetId.Value.ToString(), pollSeconds = 25, lockShell = monitorOnly });
        }));

        open(r.MapPost("/agent/v1/checkin", async (JsonElement body, HttpContext http, ITenantContext t, IConfiguration cfg, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            var ag = await AuthAgent(http, db, ct); if (ag == null) return Results.Json(new { error = "agent_auth" }, statusCode: 401);
            string? host = null;
            if (body.TryGetProperty("inventory", out var inv) && inv.ValueKind == JsonValueKind.Object)
            {
                var norm = Inventory.Normalize(inv); host = norm.Hostname;
                await using var tx = await db.BeginTransactionAsync(ct);
                await AssetsDevice.ApplyInventory(db, ag.Value.AssetId, norm, "agent", "agent", ct, tx);
                await tx.CommitAsync(ct);
            }
            await Touch(db, ag.Value.Id, ag.Value.AssetId, Ip(http), Str(body, "version"), host, ct);
            return Results.Ok(new { ok = true, update = UpdateFor(cfg, Str(body, "version"), ag.Value.Platform), metrics = new { on = true, interval = 300 } });
        }));

        open(r.MapGet("/agent/v1/poll", async (HttpContext http, ITenantContext t, IConfiguration cfg, AgentHub hub, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            var ag = await AuthAgent(http, db, ct); if (ag == null) return Results.Json(new { error = "agent_auth" }, statusCode: 401);
            await Touch(db, ag.Value.Id, ag.Value.AssetId, Ip(http), null, null, ct);
            var msgs = await Pending(db, ag.Value.Id, ct);
            int.TryParse(http.Request.Query["wait"], out var wait);
            if (msgs.Count == 0 && wait > 0)
            {
                await hub.WaitAsync(t.Require().Id + ":" + ag.Value.Id, TimeSpan.FromSeconds(Math.Min(wait, 30)), ct);
                if (!ct.IsCancellationRequested) msgs = await Pending(db, ag.Value.Id, ct);
            }
            var up = UpdateFor(cfg, http.Request.Query["v"], ag.Value.Platform);
            if (up != null) msgs.Add(new Dictionary<string, object?> { ["type"] = "update", ["version"] = up.version, ["platform"] = up.platform });
            return Results.Ok(new { msgs });
        }));

        open(r.MapPost("/agent/v1/job", async (JsonElement body, HttpContext http, ITenantContext t, IConfiguration cfg, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            var ag = await AuthAgent(http, db, ct); if (ag == null) return Results.Json(new { error = "agent_auth" }, statusCode: 401);
            var jid = Str(body, "id") ?? "";
            var st = Str(body, "status") is "done" or "failed" or "timeout" or "cancelled" or "running" ? Str(body, "status")! : "done";
            var max = int.TryParse(cfg["Assets:MaxJobOutput"], out var m) && m > 0 ? m : 256 * 1024;
            var o = Str(body, "output") ?? ""; if (o.Length > max) o = o[..max] + "\n[output cut at " + max / 1024 + " KB]";
            int? exit = body.TryGetProperty("exit", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var d) ? (int)Math.Truncate(d) : null;
            string sql = st == "running"
                ? "update assets.jobs set status = 'running', started_at = now() where id = @i and agent_id = @a"
                : "update assets.jobs set status = @s, exit_code = @x, output = @o, finished_at = now(), started_at = coalesce(started_at, now()) where id = @i and agent_id = @a";
            await using var c = new NpgsqlCommand(sql, db); P(c, "i", jid); P(c, "a", ag.Value.Id); P(c, "s", st); P(c, "x", exit); P(c, "o", o);
            if (await c.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound(new { error = "unknown_job" });
            return Results.Ok(new { ok = true });
        }));

        async Task<IResult> Download(string platform, HttpContext http, ITenantContext t, IConfiguration cfg, bool sig, CancellationToken ct)
        {
            var f = AgentFile(cfg, platform); if (f == null) return Results.NotFound(new { error = "no_agent_file", detail = "The agent for " + platform + " is not on this server." });
            if (sig) { if (!File.Exists(f + ".sig")) return Results.NotFound(); return Results.File(f + ".sig", "application/octet-stream"); }
            return Results.File(f, "application/octet-stream", Path.GetFileName(f));
        }
        async Task<bool> TokenOk(string tok, ITenantContext t, CancellationToken ct)
        {
            await using var db = await Open(t, ct);
            await using var c = new NpgsqlCommand("select 1 from assets.enroll_tokens where token_hash = @h and kind = 'agent' and not revoked and (expires_at is null or expires_at > now())", db); P(c, "h", Hash(tok));
            return await c.ExecuteScalarAsync(ct) != null;
        }
        open(r.MapGet("/agent/download/{platform}", async (string platform, HttpContext http, ITenantContext t, IConfiguration cfg, CancellationToken ct) =>
            !await TokenOk(http.Request.Query["t"].ToString(), t, ct) ? Results.Json(new { error = "token_invalid" }, statusCode: 401) : await Download(platform, http, t, cfg, false, ct)));
        open(r.MapGet("/agent/v1/binary", async (HttpContext http, ITenantContext t, IConfiguration cfg, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct); var ag = await AuthAgent(http, db, ct);
            return ag == null ? Results.Json(new { error = "agent_auth" }, statusCode: 401) : await Download(ag.Value.Platform == "windows" ? "windows" : LinuxFlavour(http), http, t, cfg, false, ct);
        }));
        open(r.MapGet("/agent/v1/binary.sig", async (HttpContext http, ITenantContext t, IConfiguration cfg, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct); var ag = await AuthAgent(http, db, ct);
            return ag == null ? Results.Json(new { error = "agent_auth" }, statusCode: 401) : await Download(ag.Value.Platform == "windows" ? "windows" : LinuxFlavour(http), http, t, cfg, true, ct);
        }));
        open(r.MapGet("/agent/install.ps1", async (HttpContext http, ITenantContext t, IConfiguration cfg, CancellationToken ct) =>
        {
            var tok = http.Request.Query["t"].ToString();
            return !await TokenOk(tok, t, ct) ? Results.Json(new { error = "token_invalid" }, statusCode: 401) : Results.Text(InstallPs1(PublicUrl(http, cfg), tok), "text/plain; charset=utf-8");
        }));
        open(r.MapGet("/agent/install.sh", async (HttpContext http, ITenantContext t, IConfiguration cfg, CancellationToken ct) =>
        {
            var tok = http.Request.Query["t"].ToString();
            return !await TokenOk(tok, t, ct) ? Results.Json(new { error = "token_invalid" }, statusCode: 401) : Results.Text(InstallSh(PublicUrl(http, cfg), tok), "text/plain; charset=utf-8");
        }));

        // ================================================================ enrolment tokens
        admin(r.MapGet("/api/assets/enrolments", async (ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            await using var c = new NpgsqlCommand("select e.id, e.company_id, coalesce(c.name,''), e.label, e.prefix, e.created_at, e.expires_at, e.uses, e.revoked, e.monitor_only from assets.enroll_tokens e left join assets.companies c on c.id = e.company_id where e.asset_id is null and e.kind = 'agent' order by e.id desc", db);
            var rows = new List<object>(); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) rows.Add(new { id = rd.GetInt32(0), companyId = rd.IsDBNull(1) ? (int?)null : rd.GetInt32(1), company = rd.GetString(2), label = rd.GetString(3), prefix = rd.GetString(4), created = rd.GetDateTime(5), expires = rd.IsDBNull(6) ? (DateTime?)null : rd.GetDateTime(6), uses = rd.GetInt32(7), revoked = rd.GetBoolean(8), monitorOnly = rd.GetBoolean(9) });
            return Results.Ok(rows);
        }));

        admin(r.MapPost("/api/assets/enrolments", async (JsonElement body, HttpContext http, ClaimsPrincipal u, ITenantContext t, IConfiguration cfg, IAuditLog audit, CancellationToken ct) =>
        {
            int? company = null;
            if (body.TryGetProperty("companyId", out var co) && co.ValueKind != JsonValueKind.Null) { if (!co.TryGetInt32(out var cid)) return Bad("bad_input", "companyId must be a number"); company = cid; }
            var days = body.TryGetProperty("days", out var dv) && dv.TryGetInt32(out var dd) ? Math.Clamp(dd, 0, 3650) : 0;
            var label = Clip(Str(body, "label"), 80); var mon = body.TryGetProperty("monitorOnly", out var mo) && mo.ValueKind == JsonValueKind.True;
            await using var db = await Open(t, ct);
            if (company != null) { await using var q = new NpgsqlCommand("select 1 from assets.companies where id = @i", db); P(q, "i", company); if (await q.ExecuteScalarAsync(ct) == null) return Bad("bad_input", "company not found"); }
            var tok = "dkat_" + Rand(21);
            await using var c = new NpgsqlCommand("insert into assets.enroll_tokens (company_id, label, token_hash, prefix, token_enc, expires_at, created_by, monitor_only) values (@c,@l,@h,@p,@e,@x,@b,@m) returning id", db);
            P(c, "c", company); P(c, "l", label); P(c, "h", Hash(tok)); P(c, "p", tok[..9]); P(c, "e", SecretBox.Seal(cfg, t.Require().Id, tok));
            c.Parameters.Add(new NpgsqlParameter("x", NpgsqlDbType.TimestampTz) { Value = days > 0 ? DateTime.UtcNow.AddDays(days) : DBNull.Value }); P(c, "b", Actor(u)); P(c, "m", mon);
            var id = (int)(await c.ExecuteScalarAsync(ct))!;
            await audit.WriteAsync("assets.enrolment_created", id.ToString(), label, ct: ct);
            return Results.Ok(new { id, token = tok, commands = Commands(PublicUrl(http, cfg), tok) });
        }));

        admin(r.MapGet("/api/assets/enrolments/{id:int}/scripts", async (int id, HttpContext http, ITenantContext t, IConfiguration cfg, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            await using var c = new NpgsqlCommand("select token_enc, revoked from assets.enroll_tokens where id = @i", db); P(c, "i", id);
            await using var rd = await c.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return Results.NotFound();
            if (rd.GetBoolean(1) || rd.GetString(0) == "") return Bad("token_unavailable", "This token was revoked or cannot be shown again.");
            var tok = SecretBox.Open(cfg, t.Require().Id, rd.GetString(0)); var url = PublicUrl(http, cfg);
            return Results.Ok(new { token = tok, commands = Commands(url, tok), windowsScript = InstallPs1(url, tok), linuxScript = InstallSh(url, tok) });
        }));

        admin(r.MapDelete("/api/assets/enrolments/{id:int}", async (int id, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            await using var c = new NpgsqlCommand("update assets.enroll_tokens set revoked = true, token_enc = '' where id = @i", db); P(c, "i", id);
            if (await c.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            await audit.WriteAsync("assets.enrolment_revoked", id.ToString(), ct: ct);
            return Results.Ok(new { ok = true });
        }));

        // ================================================================ agents
        admin(r.MapGet("/api/assets/agents", async (ITenantContext t, IConfiguration cfg, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            await using var c = new NpgsqlCommand("select g.id, g.asset_id, a.name, g.company_id, coalesce(co.name,''), g.hostname, g.platform, g.version, g.ip, g.last_seen, g.created_at, g.shell_ok, g.locked from assets.agents g left join assets.assets a on a.id = g.asset_id left join assets.companies co on co.id = g.company_id order by lower(g.hostname)", db);
            var latest = AgentVersion(cfg); var off = DateTime.UtcNow.AddSeconds(-Offline(cfg));
            var rows = new List<object>(); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) rows.Add(new { id = rd.GetString(0), assetId = rd.IsDBNull(1) ? (long?)null : rd.GetInt64(1), assetName = rd.IsDBNull(2) ? "" : rd.GetString(2), companyId = rd.IsDBNull(3) ? (int?)null : rd.GetInt32(3), company = rd.GetString(4), hostname = rd.GetString(5), platform = rd.GetString(6), version = rd.GetString(7), ip = rd.GetString(8), lastSeen = rd.IsDBNull(9) ? (DateTime?)null : rd.GetDateTime(9), online = !rd.IsDBNull(9) && rd.GetDateTime(9) > off, created = rd.GetDateTime(10), shellOk = rd.GetBoolean(11), locked = rd.GetBoolean(12), latest });
            return Results.Ok(rows);
        }));

        admin(r.MapDelete("/api/assets/agents/{id}", async (string id, ITenantContext t, AgentHub hub, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            await using var f = new NpgsqlCommand("select asset_id from assets.agents where id = @i", db); P(f, "i", id);
            var asset = await f.ExecuteScalarAsync(ct); if (asset == null) return Results.NotFound();
            await Enqueue(db, hub, t, id, "uninstall", new { }, ct);
            await using (var a = new NpgsqlCommand("update assets.assets set agent_id = '' where agent_id = @i", db)) { P(a, "i", id); await a.ExecuteNonQueryAsync(ct); }
            // the uninstall message is delivered with the next poll; the agent record goes right away
            await using (var d = new NpgsqlCommand("delete from assets.agents where id = @i", db)) { P(d, "i", id); await d.ExecuteNonQueryAsync(ct); }
            await audit.WriteAsync("assets.agent_removed", id, ct: ct);
            return Results.Ok(new { ok = true });
        }));

        // ================================================================ command library
        admin(r.MapGet("/api/assets/commands", async (HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            var q = http.Request.Query["q"].ToString().Trim().ToLowerInvariant();
            await using var c = new NpgsqlCommand(@"select id, title, description, category, os, shell, risk, params, timeout_s, builtin, enabled, body from assets.commands
                where (@q = '' or lower(concat_ws(' ', title, description, category)) like @l escape '\') order by os, category, pos, lower(title)", db);
            P(c, "q", q); P(c, "l", "%" + q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
            var rows = new List<object>(); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) rows.Add(new { id = rd.GetInt32(0), title = rd.GetString(1), description = rd.GetString(2), category = rd.GetString(3), os = rd.GetString(4), shell = rd.GetString(5), risk = rd.GetString(6), @params = Clone(rd.GetString(7)), timeout = rd.GetInt32(8), builtin = rd.GetBoolean(9), enabled = rd.GetBoolean(10), body = rd.GetString(11) });
            return Results.Ok(new { count = rows.Count, items = rows });
        }));

        async Task<IResult> SaveCommand(int? id, JsonElement b, ITenantContext t, IAuditLog audit, CancellationToken ct)
        {
            var title = Clip(Str(b, "title"), 120).Trim(); var body = Str(b, "body") ?? "";
            if (id == null && title == "") return Bad("bad_input", "title is required");
            if (id == null && body.Trim() == "") return Bad("bad_input", "the command text is empty");
            if (body.Length > 20000) return Bad("bad_input", "the command text is too long");
            var os = Str(b, "os"); var shell = Str(b, "shell");
            if (os != null && os is not ("windows" or "linux" or "any")) return Bad("bad_input", "os must be windows, linux or any");
            if (shell != null && shell is not ("cmd" or "powershell" or "bash")) return Bad("bad_input", "shell must be cmd, powershell or bash");
            var risk = Str(b, "risk"); if (risk != null && risk is not ("safe" or "caution" or "disruptive")) return Bad("bad_input", "risk must be safe, caution or disruptive");
            if (os == "windows" && shell == "bash") return Bad("bad_input", "Windows commands run in cmd or PowerShell");
            if (os == "linux" && shell != null && shell != "bash") return Bad("bad_input", "Linux commands run in bash");
            string? paramsJson = null; var pnames = new HashSet<string>();
            if (b.TryGetProperty("params", out var pr))
            {
                if (pr.ValueKind != JsonValueKind.Array || pr.GetArrayLength() > 6) return Bad("bad_input", "at most 6 parameters");
                var list = new List<object>();
                foreach (var p in pr.EnumerateArray()) { var n = Regex.Replace(Clip(Str(p, "name"), 30), "[^A-Za-z0-9_]", ""); if (n != "") { pnames.Add(n); list.Add(new { name = n, label = Clip(Str(p, "label"), 60), @default = Clip(Str(p, "default"), 100) }); } }
                paramsJson = JsonSerializer.Serialize(list);
            }
            if (id == null) foreach (Match m in Regex.Matches(body, @"\{\{(\w+)\}\}")) if (!pnames.Contains(m.Groups[1].Value)) return Bad("bad_input", "the text uses {{" + m.Groups[1].Value + "}} but there is no parameter with that name");
            int? timeout = b.TryGetProperty("timeout", out var to) && to.TryGetInt32(out var ti) ? Math.Clamp(ti, 10, 7200) : null;
            await using var db = await Open(t, ct);
            try
            {
                if (id == null)
                {
                    var sh = shell ?? (os == "linux" ? "bash" : "powershell");
                    await using var c = new NpgsqlCommand("insert into assets.commands (title, description, category, os, shell, body, risk, params, timeout_s, pos) values (@t,@d,@c,@o,@s,@b,@r,@p,@to,1000) returning id", db);
                    P(c, "t", title); P(c, "d", Clip(Str(b, "description"), 400)); P(c, "c", string.IsNullOrWhiteSpace(Str(b, "category")) ? "General" : Clip(Str(b, "category"), 40)); P(c, "o", os ?? "windows"); P(c, "s", sh); P(c, "b", body); P(c, "r", risk ?? "safe"); P(c, "p", paramsJson ?? "[]"); P(c, "to", timeout ?? 120);
                    var nid = (int)(await c.ExecuteScalarAsync(ct))!; await audit.WriteAsync("assets.command_added", nid.ToString(), title, ct: ct); return Results.Ok(new { id = nid });
                }
                bool? enabled = b.TryGetProperty("enabled", out var en) && en.ValueKind is JsonValueKind.True or JsonValueKind.False ? en.GetBoolean() : null;
                await using var u = new NpgsqlCommand(@"update assets.commands set title = coalesce(nullif(@t,''), title), description = coalesce(@d, description), category = coalesce(nullif(@c,''), category), os = coalesce(@o, os), shell = coalesce(@s, shell),
                    body = coalesce(nullif(@b,''), body), risk = coalesce(@r, risk), params = coalesce(@p, params), timeout_s = coalesce(@to, timeout_s), enabled = coalesce(@en, enabled) where id = @i", db);
                P(u, "t", title); P(u, "d", b.TryGetProperty("description", out _) ? Clip(Str(b, "description"), 400) : null); P(u, "c", Str(b, "category")); P(u, "o", os); P(u, "s", shell); P(u, "b", body); P(u, "r", risk); P(u, "p", paramsJson); P(u, "to", timeout); P(u, "en", enabled); P(u, "i", id);
                if (await u.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
                await audit.WriteAsync("assets.command_changed", id.ToString(), title, ct: ct); return Results.Ok(new { id });
            }
            catch (PostgresException e) when (e.SqlState is "23514" or "23505") { return Bad("bad_input", "os and shell do not fit together, or the command already exists"); }
        }
        admin(r.MapPost("/api/assets/commands", (JsonElement b, ITenantContext t, IAuditLog audit, CancellationToken ct) => SaveCommand(null, b, t, audit, ct)));
        admin(r.MapPut("/api/assets/commands/{id:int}", (int id, JsonElement b, ITenantContext t, IAuditLog audit, CancellationToken ct) => SaveCommand(id, b, t, audit, ct)));
        admin(r.MapDelete("/api/assets/commands/{id:int}", async (int id, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            await using var c = new NpgsqlCommand("with b as (select builtin from assets.commands where id = @i), d as (delete from assets.commands where id = @i and not builtin returning 1), u as (update assets.commands set enabled = false where id = @i and builtin returning 1) select (select count(*) from b), (select count(*) from u)", db); P(c, "i", id);
            await using var rd = await c.ExecuteReaderAsync(ct); await rd.ReadAsync(ct);
            if (rd.GetInt64(0) == 0) return Results.NotFound();
            await audit.WriteAsync("assets.command_removed", id.ToString(), ct: ct);
            return Results.Ok(new { ok = true, disabled = rd.GetInt64(1) > 0 });
        }));

        // ================================================================ running commands
        admin(r.MapPost("/api/assets/{id:long}/run", async (long id, JsonElement b, ClaimsPrincipal u, ITenantContext t, IConfiguration cfg, AgentHub hub, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            var (err, tg) = await Resolve(db, id, true, Offline(cfg), ct); if (err != null) return err;
            string shell, body, title; int timeout = 120; int? cmdId = null;
            try
            {
                if (b.TryGetProperty("commandId", out var ci) && ci.ValueKind == JsonValueKind.Number && ci.TryGetInt32(out var cid))
                {
                    await using var q = new NpgsqlCommand("select title, os, shell, body, risk, params, timeout_s from assets.commands where id = @i and enabled", db); P(q, "i", cid);
                    await using var rd = await q.ExecuteReaderAsync(ct);
                    if (!await rd.ReadAsync(ct)) return Results.NotFound(new { error = "command_not_found" });
                    var os = rd.GetString(1);
                    if (os != "any" && os != tg!.Platform) return Bad("wrong_os", "This command is for " + os + " computers.");
                    if (rd.GetString(4) == "disruptive" && !(b.TryGetProperty("confirm", out var cf) && cf.ValueKind == JsonValueKind.True)) return Bad("confirm_needed", "This command can disrupt the user. Confirm to run it.");
                    title = rd.GetString(0); shell = rd.GetString(2); timeout = rd.GetInt32(6); cmdId = cid;
                    body = FillParams(rd.GetString(3), Clone(rd.GetString(5)), b.TryGetProperty("params", out var pv) ? pv : default);
                }
                else
                {
                    body = Str(b, "body") ?? ""; if (body.Trim() == "") return Bad("bad_input", "Type the command or script to run.");
                    if (body.Length > 100000) return Bad("bad_input", "The script is too long (100 000 characters at most).");
                    shell = ShellFor(tg!.Platform, Str(b, "shell"));
                    var first = Regex.Split(body, @"\r?\n")[0];
                    title = Clip(Str(b, "title"), 120); if (title == "") title = b.TryGetProperty("script", out var sc) && sc.ValueKind == JsonValueKind.True ? "Script (PowerShell editor)" : Clip(first, 80);
                    timeout = b.TryGetProperty("timeout", out var tv) && tv.TryGetInt32(out var tt) && tt > 0 ? Math.Clamp(tt, 5, 7200) : 300;
                }
                ShellFor(tg!.Platform, shell);
            }
            catch (FormatException e) { return Bad("bad_input", e.Message); }
            var jid = "j" + Rand(12);
            await using (var ins = new NpgsqlCommand("insert into assets.jobs (id, asset_id, agent_id, shell, title, body, status, by_name, command_id, timeout_s) values (@i,@a,@g,@s,@t,@b,'queued',@u,@c,@to)", db))
            { P(ins, "i", jid); P(ins, "a", id); P(ins, "g", tg!.AgentId); P(ins, "s", shell); P(ins, "t", title); P(ins, "b", body); P(ins, "u", Actor(u)); P(ins, "c", cmdId); P(ins, "to", timeout); await ins.ExecuteNonQueryAsync(ct); }
            await Enqueue(db, hub, t, tg.AgentId, "job", new { id = jid, shell, body, timeout }, ct);
            await audit.WriteAsync("assets.command_run", id.ToString(), "\"" + title + "\" (" + shell + ") on " + tg.AssetName + (cmdId != null ? "" : ": " + Clip(body, 400)), ct: ct);
            await AssetsDevice.History(db, id, Actor(u), "command", JsonSerializer.Serialize(new { ran = title }), ct);
            return Results.Ok(new { id = jid });
        }));

        admin(r.MapGet("/api/assets/{id:long}/jobs", async (long id, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            await using var c = new NpgsqlCommand($"select {JobCols} from assets.jobs j where j.asset_id = @i order by j.created_at desc limit 60", db); P(c, "i", id);
            var rows = new List<object>(); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) rows.Add(JobOut(rd, false));
            return Results.Ok(rows);
        }));

        admin(r.MapGet("/api/assets/jobs/{jid}", async (string jid, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            await using var c = new NpgsqlCommand($"select {JobCols} from assets.jobs j where j.id = @i", db); P(c, "i", jid);
            await using var rd = await c.ExecuteReaderAsync(ct);
            return await rd.ReadAsync(ct) ? Results.Ok(JobOut(rd, true)) : Results.NotFound();
        }));

        admin(r.MapPost("/api/assets/jobs/{jid}/cancel", async (string jid, ITenantContext t, AgentHub hub, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            await using var c = new NpgsqlCommand("update assets.jobs set status = 'cancelled', finished_at = now() where id = @i and status in ('queued','sent','running') returning agent_id", db); P(c, "i", jid);
            var agent = await c.ExecuteScalarAsync(ct) as string;
            if (agent != null) await Enqueue(db, hub, t, agent, "cancel", new { id = jid }, ct);
            return Results.Ok(new { ok = true });
        }));

        admin(r.MapPost("/api/assets/{id:long}/refresh", async (long id, ClaimsPrincipal u, ITenantContext t, AgentHub hub, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            await using var c = new NpgsqlCommand("select agent_id from assets.assets where id = @i", db); P(c, "i", id);
            var agent = await c.ExecuteScalarAsync(ct) as string; if (agent == null) return Results.NotFound();
            if (agent == "") return Bad("no_agent", "This asset has no agent. Install the agent first.");
            await Enqueue(db, hub, t, agent, "collect", new { }, ct);
            await AssetsDevice.History(db, id, Actor(u), "inventory", JsonSerializer.Serialize(new { requested = true }), ct);
            return Results.Ok(new { queued = true, message = "The agent will send fresh inventory within a minute if it is online." });
        }));

        admin(r.MapGet("/api/assets/remote-log", async (HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            var days = int.TryParse(http.Request.Query["days"], out var d) ? Math.Clamp(d, 1, 365) : 30;
            await using var db = await Open(t, ct);
            await using var c = new NpgsqlCommand(@"select j.id, j.asset_id, a.name, coalesce(co.name,''), j.shell, j.title, j.body, j.status, j.by_name, j.created_at, j.finished_at, j.exit_code
                from assets.jobs j join assets.assets a on a.id = j.asset_id left join assets.companies co on co.id = a.company_id
                where j.created_at >= now() - make_interval(days => @d) order by j.created_at desc limit 300", db); P(c, "d", days);
            var rows = new List<object>(); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) rows.Add(new { kind = "command", id = rd.GetString(0), assetId = rd.GetInt64(1), asset = rd.GetString(2), company = rd.GetString(3), shell = rd.GetString(4), title = rd.GetString(5), what = Clip(rd.GetString(6), 4000), status = rd.GetString(7), who = rd.GetString(8), at = rd.GetDateTime(9), finished = rd.IsDBNull(10) ? (DateTime?)null : rd.GetDateTime(10), exit = rd.IsDBNull(11) ? (int?)null : rd.GetInt32(11) });
            return Results.Ok(new { days, items = rows });
        }));
    }
}
