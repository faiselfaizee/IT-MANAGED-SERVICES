using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
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
/// Site collectors: a small service inside a customer's network that scans for devices, pings and checks the ones that cannot run an agent and reports back
/// (health, inventory, service checks). The server side keeps what it reports, lists what it found and lets an administrator adopt it as assets.
/// </summary>
public static class CollectorsApi
{
    static readonly string[] Monitors = { "ping", "windows", "ssh", "snmp", "vmware", "device" };
    /// <summary>The modes in which the controller reads the device's details, not only whether it answers.</summary>
    static readonly string[] Reading = { "windows", "ssh", "snmp", "vmware" };
    static IResult Bad(string error, string? detail = null) => AgentsApi.Bad(error, detail);

    sealed record Col(string Id, int? Company, string Name, string Platform, string Version, string MonitorDefault, string Cidrs, bool ScanReq);

    static async Task<Col?> Auth(HttpContext http, NpgsqlConnection db, CancellationToken ct)
    {
        var m = Regex.Match(http.Request.Headers.Authorization.ToString(), @"^Bearer\s+(dkco_[A-Za-z0-9_-]{20,})$");
        var id = http.Request.Headers["X-Collector-Id"].ToString();
        if (!m.Success || id == "") return null;
        await using var c = new NpgsqlCommand("select key_hash, company_id, name, platform, version, monitor_default, scan_cidrs, scan_req from assets.collectors where id = @i", db); AgentsApi.P(c, "i", id);
        await using var r = await c.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(r.GetString(0)), Encoding.ASCII.GetBytes(AgentsApi.Hash(m.Groups[1].Value)))) return null;
        return new Col(id, r.IsDBNull(1) ? null : r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetBoolean(7));
    }

    static async Task Log(NpgsqlConnection db, string collector, string level, string text, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("insert into assets.collector_log (collector_id, level, text) values (@c, @l, @t)", db);
        AgentsApi.P(c, "c", collector); AgentsApi.P(c, "l", level is "warn" or "error" ? level : "info"); AgentsApi.P(c, "t", AgentsApi.Clip(text, 500)); await c.ExecuteNonQueryAsync(ct);
    }

    /// <summary>One line of the live install log for a device (what the controller is doing right now).</summary>
    internal static async Task DeployNote(NpgsqlConnection db, long assetId, string level, string text, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("insert into assets.deploy_log (asset_id, level, text) values (@a, @l, @t)", db);
        AgentsApi.P(c, "a", assetId); AgentsApi.P(c, "l", level is "warn" or "error" or "ok" ? level : "info"); AgentsApi.P(c, "t", AgentsApi.Clip(text, 500)); await c.ExecuteNonQueryAsync(ct);
        await using var d = new NpgsqlCommand("delete from assets.deploy_log where asset_id = @a and id < (select max(id) from assets.deploy_log where asset_id = @a) - 200", db);
        AgentsApi.P(d, "a", assetId); await d.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Called when an agent enrols: closes the install log of the device it belongs to.</summary>
    internal static async Task AgentEnrolled(NpgsqlConnection db, long assetId, string host, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("update assets.deploys set status = 'done', message = 'Agent installed and enrolled.', at = now() where asset_id = @a returning 1", db);
        AgentsApi.P(c, "a", assetId);
        if (await c.ExecuteScalarAsync(ct) != null) await DeployNote(db, assetId, "ok", "The agent started on " + host + " and enrolled with the platform. Installation complete.", ct);
    }

    /// <summary>Tells the controller to check in now instead of at the end of its minute (it waits on /collector/v1/wait).</summary>
    static async Task Wake(NpgsqlConnection db, AgentHub hub, ITenantContext t, string collectorId, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("update assets.collectors set wake_req = true where id = @i", db); AgentsApi.P(c, "i", collectorId); await c.ExecuteNonQueryAsync(ct);
        hub.Wake(t.Require().Id + ":c:" + collectorId);
    }

    /// <summary>
    /// With "install the agent automatically" on, every new Windows or Linux computer a scan finds is added as an asset and gets the agent pushed,
    /// as long as a saved login of the right kind exists for the customer. Printers, cameras and other devices are left alone.
    /// </summary>
    static async Task AutoInstall(NpgsqlConnection db, AgentHub hub, ITenantContext t, IEntitlements ent, string module, Col col, List<(string Ip, string Host, string Mac, string Cat, string Sub, List<int> Open)> found, CancellationToken ct)
    {
        bool on;
        await using (var q = new NpgsqlCommand("select auto_install from assets.collectors where id = @i", db)) { AgentsApi.P(q, "i", col.Id); on = await q.ExecuteScalarAsync(ct) is true; }
        if (!on) return;
        bool win = false, ssh = false;
        await using (var q = new NpgsqlCommand("select kind from assets.creds where kind in ('windows','ssh','sshpw') and (company_id is null or company_id = @c)", db))
        {
            AgentsApi.P(q, "c", col.Company); await using var rd = await q.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) { if (rd.GetString(0) == "windows") win = true; else ssh = true; }
        }
        long active;
        await using (var cnt = new NpgsqlCommand("select count(*) from assets.assets where status not in ('retired','lost')", db)) active = (long)(await cnt.ExecuteScalarAsync(ct))!;
        int queued = 0;
        foreach (var f in found.Take(200))
        {
            var isWin = f.Open.Any(p => p is 5985 or 5986) || (f.Open.Contains(135) && f.Open.Contains(445));
            var isLinux = !isWin && f.Open.Contains(22) && !f.Open.Any(p => p is 9100 or 631 or 554);
            if (!isWin && !isLinux) continue;
            await using (var q = new NpgsqlCommand("select 1 from assets.assets where ip_address = @ip", db)) { AgentsApi.P(q, "ip", f.Ip); if (await q.ExecuteScalarAsync(ct) != null) continue; }
            if (isWin && !win) { await Log(db, col.Id, "warn", "Found the Windows computer " + f.Ip + " but no Windows login is saved for this customer. Add one under Assets > Credentials; the next scan installs the agent.", ct); continue; }
            if (isLinux && !ssh) { await Log(db, col.Id, "warn", "Found the Linux computer " + f.Ip + " but no SSH login is saved for this customer. Add one under Assets > Credentials; the next scan installs the agent.", ct); continue; }
            if (!await ent.WithinLimitAsync(module, "max_assets", active + queued, ct)) { await Log(db, col.Id, "warn", "Not adding " + f.Ip + ": the licence limit for assets is reached.", ct); break; }
            long aid;
            await using (var tx = await db.BeginTransactionAsync(ct))
            {
                await using var ins = new NpgsqlCommand("insert into assets.assets (name, status, company_id, category, hostname, ip_address, mac_addresses, os_family, source, collector_id, monitor, created_by, updated_by) values (@n,'in_use',@co,@cat,@h,@ip,@mac::jsonb,@os,'collector',@col,@mon,'auto-install','auto-install') returning id", db, tx);
                AgentsApi.P(ins, "n", f.Host != "" ? f.Host : f.Ip); AgentsApi.P(ins, "co", col.Company); AgentsApi.P(ins, "cat", f.Cat); AgentsApi.P(ins, "h", f.Host); AgentsApi.P(ins, "ip", f.Ip);
                AgentsApi.P(ins, "mac", f.Mac == "" ? "[]" : JsonSerializer.Serialize(new[] { f.Mac.ToUpperInvariant().Replace('-', ':') })); AgentsApi.P(ins, "os", isWin ? "windows" : "linux");
                AgentsApi.P(ins, "col", col.Id); AgentsApi.P(ins, "mon", col.MonitorDefault == "" ? "ping" : col.MonitorDefault);
                aid = (long)(await ins.ExecuteScalarAsync(ct))!;
                await AssetsDevice.History(db, aid, "auto-install", "created", JsonSerializer.Serialize(new { via = "collector", ip = f.Ip, auto = true }), ct, tx);
                await using (var d = new NpgsqlCommand("delete from assets.found where collector_id = @i and ip = @ip", db, tx)) { AgentsApi.P(d, "i", col.Id); AgentsApi.P(d, "ip", f.Ip); await d.ExecuteNonQueryAsync(ct); }
                await using (var up = new NpgsqlCommand("insert into assets.deploys (asset_id, collector_id, status, message) values (@a,@c,'queued','') on conflict (asset_id) do update set collector_id = excluded.collector_id, status = 'queued', message = '', at = now()", db, tx)) { AgentsApi.P(up, "a", aid); AgentsApi.P(up, "c", col.Id); await up.ExecuteNonQueryAsync(ct); }
                await tx.CommitAsync(ct);
            }
            await DeployNote(db, aid, "info", "Found by a scan and added automatically (" + (isWin ? "Windows" : "Linux") + "). Installing the agent is queued; the controller picks it up now.", ct);
            queued++;
        }
        if (queued > 0)
        {
            // no wake-up needed: this runs inside a check-in, whose answer already carries the queued installs
            await Log(db, col.Id, "info", "Automatic install: " + queued + " computer(s) added and queued for the agent.", ct);
        }
    }

    static string Script(string kind, string url, string tok)
    {
        return AgentsApi.Fill(kind switch
        {
            "ps1" => @"# DeskAsk site collector installer for Windows - run in an elevated PowerShell
$ErrorActionPreference='Stop'
@@ADMIN@@
[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
$dir=Join-Path $env:ProgramFiles 'DeskAskCollector'; New-Item -ItemType Directory -Force -Path $dir | Out-Null
$exe=Join-Path $dir 'deskask-collector.exe'
if(Get-ScheduledTask -TaskName DeskAskCollector -ErrorAction SilentlyContinue){Stop-ScheduledTask -TaskName DeskAskCollector -ErrorAction SilentlyContinue; Get-Process deskask-collector -ErrorAction SilentlyContinue | Stop-Process -Force}
Invoke-WebRequest -UseBasicParsing -Uri '@@URL@@/collector/download/windows?t=@@TOK@@' -OutFile $exe
& $exe collector-install --server '@@URL@@' --token '@@TOK@@'
",
            "upgrade-ps1" => @"# DeskAsk site collector UPGRADE for Windows (keeps the same collector, settings and devices) - run in an elevated PowerShell
$ErrorActionPreference='Stop'
@@ADMIN@@
[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
$exe=Join-Path (Join-Path $env:ProgramFiles 'DeskAskCollector') 'deskask-collector.exe'
if(-not (Test-Path $exe)){throw ""No collector found at $exe. Use the install command instead.""}
Invoke-WebRequest -UseBasicParsing -Uri '@@URL@@/collector/download/windows?t=@@TOK@@' -OutFile ""$exe.new""
Stop-ScheduledTask -TaskName DeskAskCollector -ErrorAction SilentlyContinue; Get-Process deskask-collector -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
Move-Item -Force ""$exe.new"" $exe
Start-ScheduledTask -TaskName DeskAskCollector
Write-Host ('Collector upgraded to ' + (& $exe version))
",
            "upgrade-sh" => @"#!/bin/sh
# DeskAsk site collector UPGRADE for Linux - run as root
set -e
[ ""$(id -u)"" = 0 ] || { echo ""Run as root (sudo).""; exit 1; }
arch=$(uname -m); case ""$arch"" in x86_64) p=linux;; aarch64|arm64) p=linux-arm64;; *) echo ""Unsupported CPU $arch""; exit 1;; esac
[ -x /opt/deskask-collector/deskask-collector ] || { echo ""No collector found. Use the install command instead.""; exit 1; }
curl -fsSL -o /opt/deskask-collector/deskask-collector.new ""@@URL@@/collector/download/$p?t=@@TOK@@""
chmod 755 /opt/deskask-collector/deskask-collector.new
systemctl stop deskask-collector 2>/dev/null || true
mv -f /opt/deskask-collector/deskask-collector.new /opt/deskask-collector/deskask-collector
systemctl start deskask-collector
/opt/deskask-collector/deskask-collector version
",
            _ => @"#!/bin/sh
# DeskAsk site collector installer for Linux - run as root:  curl -fsSL '@@URL@@/collector/install.sh?t=@@TOK@@' | sudo sh
set -e
[ ""$(id -u)"" = 0 ] || { echo ""Run as root (sudo).""; exit 1; }
arch=$(uname -m); case ""$arch"" in x86_64) p=linux;; aarch64|arm64) p=linux-arm64;; *) echo ""Unsupported CPU $arch""; exit 1;; esac
mkdir -p /opt/deskask-collector
curl -fsSL -o /opt/deskask-collector/deskask-collector ""@@URL@@/collector/download/$p?t=@@TOK@@""
chmod 755 /opt/deskask-collector/deskask-collector
/opt/deskask-collector/deskask-collector collector-install --server '@@URL@@' --token '@@TOK@@'
"
        }, url, tok);
    }
    static object Commands(string url, string tok) => new
    {
        windows = "powershell -NoProfile -ExecutionPolicy Bypass -Command \"iwr -UseBasicParsing '" + url + "/collector/install.ps1?t=" + tok + "' | iex\"",
        linux = "curl -fsSL '" + url + "/collector/install.sh?t=" + tok + "' | sudo sh",
        upgradeWindows = "powershell -NoProfile -ExecutionPolicy Bypass -Command \"iwr -UseBasicParsing '" + url + "/collector/upgrade.ps1?t=" + tok + "' | iex\"",
        upgradeLinux = "curl -fsSL '" + url + "/collector/upgrade.sh?t=" + tok + "' | sudo sh"
    };

    static int? Int(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? (int)Math.Clamp(l, int.MinValue, int.MaxValue) : null;
    static double Num(JsonElement e, string n, double def = 0) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : def;
    static bool Flag(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
    static IEnumerable<JsonElement> Items(JsonElement e, string n, int max) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Take(max) : Enumerable.Empty<JsonElement>();

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        RouteHandlerBuilder Admin(RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole("admin")).RequireModule(key);
        RouteHandlerBuilder Open(RouteHandlerBuilder b) => b.RequireModule(key);

        // ================================================================ collector-facing
        Open(r.MapPost("/collector/v1/enroll", async (JsonElement body, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var tok = AgentsApi.Str(body, "token") ?? "";
            await using var db = await AgentsApi.Open(t, ct);
            int? company; int tokId;
            await using (var c = new NpgsqlCommand("select id, company_id, revoked, expires_at from assets.enroll_tokens where token_hash = @h and kind = 'collector'", db))
            {
                AgentsApi.P(c, "h", AgentsApi.Hash(tok)); await using var rd = await c.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct) || rd.GetBoolean(2) || (!rd.IsDBNull(3) && rd.GetDateTime(3) < DateTime.UtcNow))
                    return Results.Json(new { error = "token_invalid", detail = "This enrolment token is not valid (revoked or expired)." }, statusCode: 401);
                tokId = rd.GetInt32(0); company = rd.IsDBNull(1) ? null : rd.GetInt32(1);
            }
            var id = "c" + AgentsApi.Rand(9); var k = "dkco_" + AgentsApi.Rand(30);
            var host = AgentsApi.Clip(AgentsApi.Str(body, "hostname"), 120); var name = AgentsApi.Clip(AgentsApi.Str(body, "name"), 80); if (name == "") name = host != "" ? host : id;
            await using (var c = new NpgsqlCommand("insert into assets.collectors (id, company_id, name, key_hash, platform, version, hostname, ip, last_seen) values (@i,@c,@n,@k,@p,@v,@h,@ip,now())", db))
            {
                AgentsApi.P(c, "i", id); AgentsApi.P(c, "c", company); AgentsApi.P(c, "n", name); AgentsApi.P(c, "k", AgentsApi.Hash(k)); AgentsApi.P(c, "p", AgentsApi.Clip(AgentsApi.Str(body, "platform"), 20));
                AgentsApi.P(c, "v", AgentsApi.Clip(AgentsApi.Str(body, "version"), 40)); AgentsApi.P(c, "h", host); AgentsApi.P(c, "ip", AgentsApi.Clip(AgentsApi.Ip(http), 64)); await c.ExecuteNonQueryAsync(ct);
            }
            await using (var c = new NpgsqlCommand("update assets.enroll_tokens set uses = uses + 1 where id = @i", db)) { AgentsApi.P(c, "i", tokId); await c.ExecuteNonQueryAsync(ct); }
            await Log(db, id, "info", "Collector enrolled from " + AgentsApi.Ip(http), ct);
            await audit.WriteAsync("assets.collector_enrolled", id, name, actor: "collector", ct: ct);
            return Results.Ok(new { collectorId = id, key = k, pollSeconds = 60 });
        }));

        // A short-lived agent token the collector uses to install agents on devices it can reach.
        Open(r.MapPost("/collector/v1/agent-token", async (HttpContext http, ITenantContext t, IConfiguration cfg, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            var col = await Auth(http, db, ct); if (col == null) return Results.Json(new { error = "collector_auth" }, statusCode: 401);
            // one install token per collector and day, not one per click: the same token is handed out again while it has more than two hours left
            var label = "Push install via " + col.Name + " (automatic, 24 hours)";
            await using (var f = new NpgsqlCommand("select token_enc from assets.enroll_tokens where created_by = 'collector' and label = @l and kind = 'agent' and not revoked and deleted_at is null and token_enc <> '' and expires_at > now() + interval '2 hours' order by id desc limit 1", db))
            {
                AgentsApi.P(f, "l", label);
                if (await f.ExecuteScalarAsync(ct) is string enc && enc != "") { try { return Results.Ok(new { token = SecretBox.Open(cfg, t.Require().Id, enc) }); } catch (Exception) { /* sealed under another key: make a new one */ } }
            }
            var tok = "dkat_" + AgentsApi.Rand(21);
            await using var c = new NpgsqlCommand("insert into assets.enroll_tokens (company_id, label, token_hash, prefix, token_enc, expires_at, created_by) values (@c,@l,@h,@p,@e, now() + interval '24 hours', 'collector')", db);
            AgentsApi.P(c, "c", col.Company); AgentsApi.P(c, "l", label); AgentsApi.P(c, "h", AgentsApi.Hash(tok)); AgentsApi.P(c, "p", tok[..9]); AgentsApi.P(c, "e", SecretBox.Seal(cfg, t.Require().Id, tok));
            await c.ExecuteNonQueryAsync(ct);
            return Results.Ok(new { token = tok });
        }));

        Open(r.MapGet("/collector/download/{platform}", async (string platform, HttpContext http, ITenantContext t, IConfiguration cfg, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            await using var c = new NpgsqlCommand("select 1 from assets.enroll_tokens where token_hash = @h and kind = 'collector' and not revoked and (expires_at is null or expires_at > now())", db);
            AgentsApi.P(c, "h", AgentsApi.Hash(http.Request.Query["t"].ToString()));
            if (await c.ExecuteScalarAsync(ct) == null) return Results.Json(new { error = "token_invalid" }, statusCode: 401);
            var f = AgentsApi.AgentFile(cfg, platform); if (f == null) return Results.NotFound(new { error = "no_agent_file", detail = "The collector for " + platform + " is not on this server." });
            return Results.File(f, "application/octet-stream", Path.GetFileName(f));
        }));
        // the collector fetches its own new program over its authenticated connection (same file the install page serves)
        Open(r.MapGet("/collector/v1/binary", async (HttpContext http, ITenantContext t, IConfiguration cfg, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            if (await Auth(http, db, ct) == null) return Results.Json(new { error = "collector_auth" }, statusCode: 401);
            var pf = http.Request.Query["platform"] == "windows" ? "windows" : AgentsApi.LinuxFlavour(http);
            var f = AgentsApi.AgentFile(cfg, pf); if (f == null) return Results.NotFound(new { error = "no_agent_file" });
            return Results.File(f, "application/octet-stream", Path.GetFileName(f));
        }));
        foreach (var (kind, file) in new[] { ("ps1", "install.ps1"), ("sh", "install.sh"), ("upgrade-ps1", "upgrade.ps1"), ("upgrade-sh", "upgrade.sh") })
        {
            var k2 = kind;
            Open(r.MapGet("/collector/" + file, async (HttpContext http, ITenantContext t, IConfiguration cfg, CancellationToken ct) =>
            {
                var tok = http.Request.Query["t"].ToString();
                await using var db = await AgentsApi.Open(t, ct);
                await using var c = new NpgsqlCommand("select 1 from assets.enroll_tokens where token_hash = @h and kind = 'collector' and not revoked and (expires_at is null or expires_at > now())", db); AgentsApi.P(c, "h", AgentsApi.Hash(tok));
                if (await c.ExecuteScalarAsync(ct) == null) return Results.Json(new { error = "token_invalid" }, statusCode: 401);
                return Results.Text(Script(k2, AgentsApi.PublicUrl(http, cfg), tok), "text/plain; charset=utf-8");
            }));
        }

        // The controller waits here (up to 25 s); it is answered the moment an install, a scan or a setting needs its attention.
        Open(r.MapGet("/collector/v1/wait", async (HttpContext http, ITenantContext t, AgentHub hub, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            var col = await Auth(http, db, ct); if (col == null) return Results.Json(new { error = "collector_auth" }, statusCode: 401);
            var colId = col.Id;
            async Task<bool> Take()
            {
                await using var c = new NpgsqlCommand("update assets.collectors set wake_req = false where id = @i and wake_req returning true", db); AgentsApi.P(c, "i", colId);
                return await c.ExecuteScalarAsync(ct) is true;
            }
            if (await Take()) return Results.Ok(new { now = true });
            int.TryParse(http.Request.Query["wait"], out var wait);
            await hub.WaitAsync(t.Require().Id + ":c:" + colId, TimeSpan.FromSeconds(Math.Clamp(wait, 1, 30)), ct);
            return Results.Ok(new { now = !ct.IsCancellationRequested && await Take() });
        }));

        // One line of progress from an install in flight; shown live on the device.
        Open(r.MapPost("/collector/v1/progress", async (JsonElement body, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            var col = await Auth(http, db, ct); if (col == null) return Results.Json(new { error = "collector_auth" }, statusCode: 401);
            if (!long.TryParse(AgentsApi.Str(body, "asset"), out var aid)) return Bad("bad_input", "asset is required");
            await using (var q = new NpgsqlCommand("select 1 from assets.deploys where asset_id = @a and collector_id = @c", db)) { AgentsApi.P(q, "a", aid); AgentsApi.P(q, "c", col.Id); if (await q.ExecuteScalarAsync(ct) == null) return Results.NotFound(new { error = "unknown_deploy" }); }
            var msg = AgentsApi.Str(body, "msg"); if (string.IsNullOrWhiteSpace(msg)) return Results.Ok(new { ok = true });
            await DeployNote(db, aid, AgentsApi.Str(body, "level") ?? "info", msg, ct);
            return Results.Ok(new { ok = true });
        }));

        Open(r.MapPost("/collector/v1/checkin", async (JsonElement body, HttpContext http, ITenantContext t, IConfiguration cfg, IAuditLog audit, AgentHub hub, IEntitlements ent, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            var col = await Auth(http, db, ct); if (col == null) return Results.Json(new { error = "collector_auth" }, statusCode: 401);
            await using (var c = new NpgsqlCommand("update assets.collectors set last_seen = now(), wake_req = false, ip = @ip, version = case when @v = '' then version else @v end, hostname = case when @h = '' then hostname else @h end, platform = case when @p = '' then platform else @p end where id = @i", db))
            {
                AgentsApi.P(c, "ip", AgentsApi.Clip(AgentsApi.Ip(http), 64)); AgentsApi.P(c, "v", AgentsApi.Clip(AgentsApi.Str(body, "version"), 40)); AgentsApi.P(c, "h", AgentsApi.Clip(AgentsApi.Str(body, "hostname"), 120));
                AgentsApi.P(c, "p", AgentsApi.Clip(AgentsApi.Str(body, "platform"), 20)); AgentsApi.P(c, "i", col.Id); await c.ExecuteNonQueryAsync(ct);
            }
            var res = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("results", out var rs) && rs.ValueKind == JsonValueKind.Object ? rs : default;

            // assets this collector looks after
            var mine = new Dictionary<long, (string Ip, string Host, string Name, string Monitor, bool? Up, bool ReadReq)>();
            await using (var c = new NpgsqlCommand("select id, ip_address, hostname, name, monitor, check_up, read_req from assets.assets where collector_id = @i", db))
            {
                AgentsApi.P(c, "i", col.Id); await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) mine[rd.GetInt64(0)] = (rd.GetString(1), rd.GetString(2), rd.GetString(3), rd.GetString(4), rd.IsDBNull(5) ? null : rd.GetBoolean(5), rd.GetBoolean(6));
            }
            long AssetOf(JsonElement e) { var s = AgentsApi.Str(e, "asset") ?? ""; return long.TryParse(s, out var l) && mine.ContainsKey(l) ? l : 0; }

            foreach (var h in Items(res, "health", 5000))
            {
                var id = AssetOf(h); if (id == 0) continue; var up = Flag(h, "up");
                await using var c = new NpgsqlCommand(up ? "update assets.assets set check_up = true, check_rtt = @r, checked_at = now(), last_seen = now() where id = @i" : "update assets.assets set check_up = false, check_rtt = 0, checked_at = now() where id = @i", db);
                AgentsApi.P(c, "r", (int)Math.Clamp(Math.Round(Num(h, "rtt_ms")), 0, 60000)); AgentsApi.P(c, "i", id); await c.ExecuteNonQueryAsync(ct);
            }
            foreach (var iv in Items(res, "inventories", 500))
            {
                var id = AssetOf(iv); if (id == 0 || !iv.TryGetProperty("raw", out var raw) || raw.ValueKind != JsonValueKind.Object) continue;
                try
                {
                    await using var tx = await db.BeginTransactionAsync(ct);
                    await AssetsDevice.ApplyInventory(db, id, Inventory.Normalize(raw), "collector", "collector " + col.Name, ct, tx);
                    await tx.CommitAsync(ct);
                }
                catch (Exception e) { await Log(db, col.Id, "warn", "Inventory for asset " + id + " rejected: " + e.Message, ct); }
            }
            foreach (var er in Items(res, "errors", 200))
            {
                var id = AssetOf(er); if (id == 0) continue;
                await using (var rq = new NpgsqlCommand("update assets.assets set read_req = false where id = @i", db)) { AgentsApi.P(rq, "i", id); await rq.ExecuteNonQueryAsync(ct); }
                await AssetsDevice.History(db, id, "collector " + col.Name, "inventory", JsonSerializer.Serialize(new { error = AgentsApi.Clip(AgentsApi.Str(er, "error"), 300) }), ct);
            }
            // service checks: keep the newest result of each and a short history for Monitoring
            var known = new Dictionary<int, long>();
            await using (var c = new NpgsqlCommand("select s.id, s.asset_id, s.type from assets.service_checks s join assets.assets a on a.id = s.asset_id where a.collector_id = @i", db))
            {
                AgentsApi.P(c, "i", col.Id); await using var rd = await c.ExecuteReaderAsync(ct);
                var types = new Dictionary<int, string>();
                while (await rd.ReadAsync(ct)) { known[rd.GetInt32(0)] = rd.GetInt64(1); types[rd.GetInt32(0)] = rd.GetString(2); }
                rd.Close();
                foreach (var ck in Items(res, "checks", 2000))
                {
                    var cid = Int(ck, "id"); if (cid == null || !known.TryGetValue(cid.Value, out var aid) || (AgentsApi.Str(ck, "asset") ?? "") != aid.ToString()) continue;
                    var ok = Flag(ck, "ok"); var ms = Math.Clamp(Num(ck, "ms"), 0, 600000);
                    double? days = types[cid.Value] == "ssl" && (ok || Num(ck, "days", -9999) != 0) && Num(ck, "days", -9999) > -9000 ? Num(ck, "days") : null;
                    await using var u = new NpgsqlCommand("update assets.service_checks set last_at = now(), last_ok = @o, last_ms = @m, last_days = @d, last_detail = @t where id = @i", db);
                    AgentsApi.P(u, "o", ok); AgentsApi.P(u, "m", (float)ms); AgentsApi.P(u, "d", days == null ? null : (float)days.Value); AgentsApi.P(u, "t", AgentsApi.Clip(AgentsApi.Str(ck, "detail"), 300)); AgentsApi.P(u, "i", cid.Value);
                    await u.ExecuteNonQueryAsync(ct);
                    await using var h = new NpgsqlCommand("insert into assets.check_results (check_id, ok, ms, days) values (@i,@o,@m,@d)", db);
                    AgentsApi.P(h, "i", cid.Value); AgentsApi.P(h, "o", ok); AgentsApi.P(h, "m", (float)ms); AgentsApi.P(h, "d", days == null ? null : (float)days.Value); await h.ExecuteNonQueryAsync(ct);
                }
            }
            // found by a scan
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("found", out var fo) && fo.ValueKind == JsonValueKind.Array)
            {
                var newFound = new List<(string Ip, string Host, string Mac, string Cat, string Sub, List<int> Open)>();
                foreach (var f in fo.EnumerateArray().Take(2000))
                {
                    var ip = AgentsApi.Str(f, "ip") ?? ""; if (!System.Net.IPAddress.TryParse(ip, out _)) continue;
                    var open = f.TryGetProperty("open", out var op) && op.ValueKind == JsonValueKind.Array ? op.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32()).Take(40).ToList() : new List<int>();
                    var g = DiscoveryApi.GuessFrom(open);
                    newFound.Add((ip, AgentsApi.Clip(AgentsApi.Str(f, "hostname"), 120), AgentsApi.Clip(AgentsApi.Str(f, "mac"), 40), g.Cat, g.Sub, open));
                    await using var c = new NpgsqlCommand("insert into assets.found (collector_id, ip, hostname, open, mac, cat, sub) values (@c,@ip,@h,@o,@m,@cat,@sub) on conflict (collector_id, ip) do update set hostname = excluded.hostname, open = excluded.open, mac = excluded.mac, cat = excluded.cat, sub = excluded.sub, seen_at = now()", db);
                    AgentsApi.P(c, "c", col.Id); AgentsApi.P(c, "ip", ip); AgentsApi.P(c, "h", AgentsApi.Clip(AgentsApi.Str(f, "hostname"), 120)); c.Parameters.Add(new NpgsqlParameter("o", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(open) });
                    AgentsApi.P(c, "m", AgentsApi.Clip(AgentsApi.Str(f, "mac"), 40)); AgentsApi.P(c, "cat", g.Cat); AgentsApi.P(c, "sub", g.Sub); await c.ExecuteNonQueryAsync(ct);
                }
                await using (var cl = new NpgsqlCommand("update assets.collectors set scan_req = false where id = @i", db)) { AgentsApi.P(cl, "i", col.Id); await cl.ExecuteNonQueryAsync(ct); }
                if (newFound.Count > 0) await AutoInstall(db, hub, t, ent, key, col, newFound, ct);
            }
            // deploy results
            foreach (var d in Items(body, "deploys", 500))
            {
                if (!long.TryParse(AgentsApi.Str(d, "asset"), out var aid) || !mine.ContainsKey(aid)) continue;
                await using var c = new NpgsqlCommand("update assets.deploys set status = @s, message = @m, at = now() where asset_id = @a and collector_id = @c", db);
                AgentsApi.P(c, "s", Flag(d, "ok") ? "done" : "failed"); AgentsApi.P(c, "m", AgentsApi.Clip(AgentsApi.Str(d, "message"), 480)); AgentsApi.P(c, "a", aid); AgentsApi.P(c, "c", col.Id); await c.ExecuteNonQueryAsync(ct);
                await DeployNote(db, aid, Flag(d, "ok") ? "ok" : "error", (Flag(d, "ok") ? "Finished: " : "Failed: ") + AgentsApi.Clip(AgentsApi.Str(d, "message"), 400), ct);
            }
            foreach (var l in Items(body, "log", 300))
            {
                var m = AgentsApi.Str(l, "m"); if (string.IsNullOrEmpty(m)) continue;
                await Log(db, col.Id, AgentsApi.Str(l, "l") ?? "info", m, ct);
            }

            // the logins saved under Assets > Credentials, sealed in the database; sent only to this collector, over its own authenticated connection
            var saved = new List<(int? Company, string Kind, string Category, object Login)>();
            await using (var c = new NpgsqlCommand("select name, kind, username, secret, company_id, category from assets.creds where kind in ('windows','ssh','snmp','sshpw','vmware') order by id", db))
            {
                await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                {
                    string secret; try { secret = rd.GetString(3) == "" ? "" : SecretBox.Open(cfg, t.Require().Id, rd.GetString(3)); } catch (Exception) { continue; }
                    saved.Add((rd.IsDBNull(4) ? null : rd.GetInt32(4), rd.GetString(1), rd.GetString(5), new { name = rd.GetString(0), kind = rd.GetString(1), user = rd.GetString(2), secret }));
                }
            }
            var company = new Dictionary<long, int?>();
            await using (var c = new NpgsqlCommand("select id, company_id from assets.assets where collector_id = @i", db))
            {
                AgentsApi.P(c, "i", col.Id); await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) company[rd.GetInt64(0)] = rd.IsDBNull(1) ? null : rd.GetInt32(1);
            }
            // the customer's own logins before shared ones; for VMware only VMware logins, those filed under VMware and Nutanix first
            object[] LoginsFor(long assetId, string mode = "")
            {
                company.TryGetValue(assetId, out var co);
                return saved.Where(x => (x.Company == null || x.Company == co) && (mode == "vmware" ? x.Kind == "vmware" : x.Kind != "vmware"))
                    .OrderBy(x => x.Company == null ? 1 : 0).ThenBy(x => mode == "vmware" && x.Category != "virtualization" ? 1 : 0).Select(x => x.Login).ToArray();
            }
            // the work list
            var assets = new List<object>(); foreach (var (id, a) in mine) assets.Add(new { id = id.ToString(), ip = a.Ip, host = a.Host, name = a.Name, monitor = a.Monitor == "" ? "ping" : a.Monitor, logins = Reading.Contains(a.Monitor) ? LoginsFor(id, a.Monitor) : Array.Empty<object>(), readNow = a.ReadReq && Reading.Contains(a.Monitor) });
            var checks = new Dictionary<string, List<object>>();
            await using (var c = new NpgsqlCommand("select s.id, s.asset_id, s.name, s.type, s.host, s.port, s.url, s.expect, s.status, s.insecure, s.dns, s.timeout_s, a.ip_address, a.dns_name, a.hostname from assets.service_checks s join assets.assets a on a.id = s.asset_id where a.collector_id = @i and a.monitor <> '' and s.enabled", db))
            {
                AgentsApi.P(c, "i", col.Id); await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                {
                    var type = rd.GetString(3); var host = type == "dns" ? rd.GetString(4) : new[] { rd.GetString(4), rd.GetString(12), rd.GetString(13), rd.GetString(14) }.FirstOrDefault(x => x != "") ?? "";
                    if (host == "" && type != "http") continue;
                    var aid = rd.GetInt64(1).ToString(); if (!checks.TryGetValue(aid, out var l)) checks[aid] = l = new();
                    l.Add(new { id = rd.GetInt32(0), name = rd.GetString(2), type, host, port = rd.GetInt32(5), url = rd.GetString(6), expect = rd.GetString(7), status = rd.GetString(8), insecure = rd.GetBoolean(9), dns = rd.GetString(10), timeoutS = rd.GetInt32(11) });
                }
            }
            // queued installs are handed over now and marked running, so a slow install is not handed out again at the next check-in
            var deploys = new List<object>(); var claimed = new List<long>();
            await using (var c = new NpgsqlCommand("with q as (update assets.deploys set status = 'running', at = now(), attempts = attempts + 1 where collector_id = @i and status = 'queued' returning asset_id) select q.asset_id, a.ip_address, a.hostname, a.os_family from q join assets.assets a on a.id = q.asset_id", db))
            {
                AgentsApi.P(c, "i", col.Id); await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) { claimed.Add(rd.GetInt64(0)); deploys.Add(new { asset = rd.GetInt64(0).ToString(), ip = rd.GetString(1), host = rd.GetString(2), platform = rd.GetString(3), logins = LoginsFor(rd.GetInt64(0)) }); }
            }
            foreach (var cid2 in claimed) await DeployNote(db, cid2, "info", "Handed to the controller \"" + col.Name + "\" (version " + col.Version + ").", ct);
            string cidrs = ""; bool scanReq = false;
            await using (var c = new NpgsqlCommand("select scan_cidrs, scan_req from assets.collectors where id = @i", db)) { AgentsApi.P(c, "i", col.Id); await using var rd = await c.ExecuteReaderAsync(ct); if (await rd.ReadAsync(ct)) { cidrs = rd.GetString(0); scanReq = rd.GetBoolean(1); } }
            return Results.Ok(new { pollSeconds = 60, assets, checks, deploys, scan = new { cidrs = cidrs.Split(new[] { ',', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries), now = scanReq },
                update = AgentsApi.UpdateFor(cfg, AgentsApi.Str(body, "version"), AgentsApi.Str(body, "platform") ?? "linux") });
        }));

        // ================================================================ admin side
        Admin(r.MapGet("/api/assets/collectors", async (ITenantContext t, IConfiguration cfg, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct); var o = new List<object>(); var off = AgentsApi.Offline(cfg);
            await using var c = new NpgsqlCommand(@"select c.id, c.company_id, coalesce(co.name,''), c.name, c.platform, c.version, c.hostname, c.ip, c.last_seen, c.created_at, c.monitor_default, c.scan_cidrs, c.scan_req,
                (select count(*) from assets.assets a where a.collector_id = c.id), (select count(*) from assets.found f where f.collector_id = c.id), c.auto_install
                from assets.collectors c left join assets.companies co on co.id = c.company_id order by lower(c.name)", db);
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                DateTime? seen = rd.IsDBNull(8) ? null : rd.GetDateTime(8);
                o.Add(new { id = rd.GetString(0), companyId = rd.IsDBNull(1) ? (int?)null : rd.GetInt32(1), company = rd.GetString(2), name = rd.GetString(3), platform = rd.GetString(4), version = rd.GetString(5), hostname = rd.GetString(6), ip = rd.GetString(7), lastSeen = seen, online = seen != null && (DateTime.UtcNow - seen.Value).TotalSeconds < off, createdAt = rd.GetDateTime(9), monitorDefault = rd.GetString(10), scanCidrs = rd.GetString(11), scanRequested = rd.GetBoolean(12), devices = rd.GetInt64(13), found = rd.GetInt64(14), autoInstall = rd.GetBoolean(15) });
            }
            return Results.Ok(new { items = o, modes = Monitors });
        }));
        Admin(r.MapPost("/api/assets/collectors/tokens", async (JsonElement body, HttpContext http, ClaimsPrincipal u, ITenantContext t, IConfiguration cfg, IAuditLog audit, CancellationToken ct) =>
        {
            int? company = null;
            if (body.TryGetProperty("companyId", out var co) && co.ValueKind == JsonValueKind.Number) company = co.GetInt32();
            var days = Math.Clamp(Int(body, "days") ?? 14, 0, 3650); var label = AgentsApi.Clip(AgentsApi.Str(body, "label"), 80);
            await using var db = await AgentsApi.Open(t, ct);
            if (company != null) { await using var q = new NpgsqlCommand("select 1 from assets.companies where id = @i", db); AgentsApi.P(q, "i", company); if (await q.ExecuteScalarAsync(ct) == null) return Bad("bad_input", "company not found"); }
            var tok = "dkct_" + AgentsApi.Rand(21);
            await using var c = new NpgsqlCommand("insert into assets.enroll_tokens (company_id, label, token_hash, prefix, token_enc, expires_at, created_by, kind) values (@c,@l,@h,@p,@e,@x,@b,'collector') returning id", db);
            AgentsApi.P(c, "c", company); AgentsApi.P(c, "l", label); AgentsApi.P(c, "h", AgentsApi.Hash(tok)); AgentsApi.P(c, "p", tok[..9]); AgentsApi.P(c, "e", SecretBox.Seal(cfg, t.Require().Id, tok));
            c.Parameters.Add(new NpgsqlParameter("x", NpgsqlDbType.TimestampTz) { Value = days > 0 ? DateTime.UtcNow.AddDays(days) : DBNull.Value }); AgentsApi.P(c, "b", AgentsApi.Actor(u));
            var id = (int)(await c.ExecuteScalarAsync(ct))!;
            await audit.WriteAsync("assets.collector_token_created", id.ToString(), label, ct: ct);
            return Results.Ok(new { id, token = tok, commands = Commands(AgentsApi.PublicUrl(http, cfg), tok) });
        }));
        Admin(r.MapPatch("/api/assets/collectors/{id}", async (string id, JsonElement body, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var name = AgentsApi.Str(body, "name"); var mon = AgentsApi.Str(body, "monitorDefault"); var cidrs = AgentsApi.Str(body, "scanCidrs");
            bool? auto = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("autoInstall", out var ai) && (ai.ValueKind == JsonValueKind.True || ai.ValueKind == JsonValueKind.False) ? (bool?)ai.GetBoolean() : null;
            if (mon != null && !Monitors.Contains(mon)) return Bad("bad_input", "monitorDefault must be one of " + string.Join(", ", Monitors));
            if (cidrs != null)
                foreach (var part in cidrs.Split(new[] { ',', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    if (DiscoveryApi.Expand(part, out var err) == null) return Bad("bad_input", part + ": " + err);
            await using var db = await AgentsApi.Open(t, ct);
            await using var c = new NpgsqlCommand("update assets.collectors set name = coalesce(@n, name), monitor_default = coalesce(@m, monitor_default), scan_cidrs = coalesce(@c, scan_cidrs), auto_install = coalesce(@a::boolean, auto_install) where id = @i", db);
            AgentsApi.P(c, "a", auto);
            AgentsApi.P(c, "n", name == null ? null : AgentsApi.Clip(name, 80).Trim()); AgentsApi.P(c, "m", mon); AgentsApi.P(c, "c", cidrs == null ? null : AgentsApi.Clip(cidrs, 500)); AgentsApi.P(c, "i", id);
            if (await c.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            await audit.WriteAsync("assets.collector_changed", id, auto == null ? "" : "automatic agent install " + (auto.Value ? "on" : "off"), ct: ct);
            return Results.Ok(new { ok = true });
        }));
        Admin(r.MapDelete("/api/assets/collectors/{id}", async (string id, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            await using (var u = new NpgsqlCommand("update assets.assets set collector_id = '', monitor = '' where collector_id = @i", db)) { AgentsApi.P(u, "i", id); await u.ExecuteNonQueryAsync(ct); }
            await using var c = new NpgsqlCommand("delete from assets.collectors where id = @i", db); AgentsApi.P(c, "i", id);
            if (await c.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            await audit.WriteAsync("assets.collector_removed", id, "", ct: ct);
            return Results.NoContent();
        }));
        Admin(r.MapPost("/api/assets/collectors/{id}/scan", async (string id, ITenantContext t, AgentHub hub, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            await using var c = new NpgsqlCommand("update assets.collectors set scan_req = true where id = @i returning scan_cidrs", db); AgentsApi.P(c, "i", id);
            var cidrs = await c.ExecuteScalarAsync(ct) as string; if (cidrs == null) return Results.NotFound();
            if (cidrs.Trim() == "") return Bad("no_ranges", "Enter at least one range to scan (for example 192.168.1.0/24) in the collector settings.");
            await Log(db, id, "info", "Scan requested", ct);
            await Wake(db, hub, t, id, ct);
            await audit.WriteAsync("assets.collector_scan", id, cidrs, ct: ct);
            return Results.Ok(new { ok = true });
        }));
        Admin(r.MapGet("/api/assets/collectors/{id}/log", async (string id, long? after, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct); var o = new List<object>();
            await using var c = new NpgsqlCommand("select id, at, level, text from assets.collector_log where collector_id = @i and id > @a order by id desc limit 300", db); AgentsApi.P(c, "i", id); AgentsApi.P(c, "a", after ?? 0);
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) o.Add(new { id = rd.GetInt64(0), at = rd.GetDateTime(1), level = rd.GetString(2), text = rd.GetString(3) });
            return Results.Ok(new { items = o });
        }));
        Admin(r.MapGet("/api/assets/collectors/{id}/found", async (string id, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct); var rows = new List<(string Ip, object V)>();
            await using var c = new NpgsqlCommand("select f.ip, f.hostname, f.open::text, f.mac, f.cat, f.sub, f.seen_at, exists (select 1 from assets.assets a where a.ip_address = f.ip) from assets.found f where f.collector_id = @i", db); AgentsApi.P(c, "i", id);
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) rows.Add((rd.GetString(0), new { ip = rd.GetString(0), hostname = rd.GetString(1), open = JsonDocument.Parse(rd.GetString(2)).RootElement.Clone(), mac = rd.GetString(3), cat = rd.GetString(4), sub = rd.GetString(5), seenAt = rd.GetDateTime(6), known = rd.GetBoolean(7) }));
            return Results.Ok(new { items = rows.OrderBy(x => System.Net.IPAddress.TryParse(x.Ip, out var a) && a.GetAddressBytes().Length == 4 ? DiscoveryApi.IpKey(x.Ip) : 0).Select(x => x.V).ToList() });
        }));
        Admin(r.MapPost("/api/assets/collectors/{id}/adopt", async (string id, JsonElement body, ClaimsPrincipal u, ITenantContext t, IEntitlements ent, IAuditLog audit, CancellationToken ct) =>
        {
            var ips = body.TryGetProperty("ips", out var ie) && ie.ValueKind == JsonValueKind.Array ? ie.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x != "").Take(500).ToList() : new List<string>();
            if (ips.Count == 0) return Bad("bad_input", "Choose the devices to add.");
            await using var db = await AgentsApi.Open(t, ct);
            int? company = null; string mon = "ping";
            await using (var c = new NpgsqlCommand("select company_id, monitor_default from assets.collectors where id = @i", db)) { AgentsApi.P(c, "i", id); await using var rd = await c.ExecuteReaderAsync(ct); if (!await rd.ReadAsync(ct)) return Results.NotFound(); company = rd.IsDBNull(0) ? null : rd.GetInt32(0); mon = rd.GetString(1); }
            if (body.TryGetProperty("monitor", out var me) && me.ValueKind == JsonValueKind.String && Monitors.Contains(me.GetString()!)) mon = me.GetString()!;
            await using var cnt = new NpgsqlCommand("select count(*) from assets.assets where status not in ('retired','lost')", db);
            var active = (long)(await cnt.ExecuteScalarAsync(ct))!; int added = 0, skipped = 0, limited = 0;
            foreach (var ip in ips)
            {
                (string Host, string Mac, string Cat, string Sub)? f = null;
                await using (var c = new NpgsqlCommand("select hostname, mac, cat, sub from assets.found where collector_id = @i and ip = @ip", db)) { AgentsApi.P(c, "i", id); AgentsApi.P(c, "ip", ip); await using var rd = await c.ExecuteReaderAsync(ct); if (await rd.ReadAsync(ct)) f = (rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetString(3)); }
                if (f == null) { skipped++; continue; }
                await using (var q = new NpgsqlCommand("select 1 from assets.assets where ip_address = @ip", db)) { AgentsApi.P(q, "ip", ip); if (await q.ExecuteScalarAsync(ct) != null) { skipped++; continue; } }
                if (!await ent.WithinLimitAsync(key, "max_assets", active + added, ct)) { limited++; continue; }
                await using var tx = await db.BeginTransactionAsync(ct);
                await using var ins = new NpgsqlCommand("insert into assets.assets (name, status, company_id, category, hostname, ip_address, mac_addresses, source, collector_id, monitor, created_by, updated_by) values (@n,'in_use',@co,@cat,@h,@ip,@mac::jsonb,'collector',@col,@mon,@a,@a) returning id", db, tx);
                AgentsApi.P(ins, "n", f.Value.Host != "" ? f.Value.Host : ip); AgentsApi.P(ins, "co", company); AgentsApi.P(ins, "cat", f.Value.Cat); AgentsApi.P(ins, "h", f.Value.Host); AgentsApi.P(ins, "ip", ip);
                AgentsApi.P(ins, "mac", f.Value.Mac == "" ? "[]" : JsonSerializer.Serialize(new[] { f.Value.Mac.ToUpperInvariant().Replace('-', ':') })); AgentsApi.P(ins, "col", id); AgentsApi.P(ins, "mon", mon); AgentsApi.P(ins, "a", AgentsApi.Actor(u));
                var aid = (long)(await ins.ExecuteScalarAsync(ct))!;
                await AssetsDevice.History(db, aid, AgentsApi.Actor(u), "created", JsonSerializer.Serialize(new { via = "collector", ip }), ct, tx);
                await using (var d = new NpgsqlCommand("delete from assets.found where collector_id = @i and ip = @ip", db, tx)) { AgentsApi.P(d, "i", id); AgentsApi.P(d, "ip", ip); await d.ExecuteNonQueryAsync(ct); }
                await tx.CommitAsync(ct); added++;
            }
            await audit.WriteAsync("assets.collector_adopt", id, added + " added, " + skipped + " skipped", ct: ct);
            return Results.Ok(new { added, skipped, limited });
        }));

        // which collector watches an asset, and how
        Admin(r.MapPost("/api/assets/{id:long}/monitor", async (long id, JsonElement body, ITenantContext t, IAuditLog audit, AgentHub hub, CancellationToken ct) =>
        {
            var mode = AgentsApi.Str(body, "mode") ?? ""; var col = AgentsApi.Str(body, "collectorId") ?? "";
            if (mode != "" && !Monitors.Contains(mode)) return Bad("bad_input", "mode must be empty or one of " + string.Join(", ", Monitors));
            if (mode == "") col = "";
            await using var db = await AgentsApi.Open(t, ct);
            if (col != "")
            {
                await using var q = new NpgsqlCommand("select 1 from assets.collectors where id = @i", db); AgentsApi.P(q, "i", col);
                if (await q.ExecuteScalarAsync(ct) == null) return Bad("bad_input", "collector not found");
            }
            await using var c = new NpgsqlCommand("update assets.assets set monitor = @m, collector_id = @c, read_req = @rr, updated_at = now() where id = @i", db); AgentsApi.P(c, "m", mode); AgentsApi.P(c, "c", col); AgentsApi.P(c, "i", id);
            AgentsApi.P(c, "rr", Reading.Contains(mode));
            if (await c.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            if (col != "") await Wake(db, hub, t, col, ct); // the controller reads it within seconds
            await audit.WriteAsync("assets.monitor_set", id.ToString(), mode == "" ? "off" : mode + " via " + col, ct: ct);
            return Results.Ok(new { ok = true });
        }));

        // read the device's details now instead of at the next six-hourly read
        Admin(r.MapPost("/api/assets/{id:long}/read-now", async (long id, ITenantContext t, AgentHub hub, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            string mode = "", col = "";
            await using (var q = new NpgsqlCommand("select monitor, collector_id from assets.assets where id = @i", db))
            { AgentsApi.P(q, "i", id); await using var rd = await q.ExecuteReaderAsync(ct); if (!await rd.ReadAsync(ct)) return Results.NotFound(); mode = rd.GetString(0); col = rd.GetString(1); }
            if (col == "" || !Reading.Contains(mode)) return Bad("not_read", "Choose a controller and a way to read this device first (Windows, Linux SSH, SNMP or VMware).");
            await using (var c = new NpgsqlCommand("update assets.assets set read_req = true where id = @i", db)) { AgentsApi.P(c, "i", id); await c.ExecuteNonQueryAsync(ct); }
            await Wake(db, hub, t, col, ct);
            return Results.Ok(new { ok = true });
        }));

        // install the agent on a device through the collector that reaches it
        Admin(r.MapPost("/api/assets/{id:long}/agent-install", async (long id, ClaimsPrincipal u, ITenantContext t, AgentHub hub, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            string col = ""; string agent = "";
            await using (var c = new NpgsqlCommand("select collector_id, agent_id from assets.assets where id = @i", db)) { AgentsApi.P(c, "i", id); await using var rd = await c.ExecuteReaderAsync(ct); if (!await rd.ReadAsync(ct)) return Results.NotFound(); col = rd.GetString(0); agent = rd.GetString(1); }
            if (agent != "") return Bad("already_installed", "This device already has an agent.");
            if (col == "") return Bad("no_collector", "Choose the site collector that reaches this device first (Monitoring).");
            await using (var q = new NpgsqlCommand("select 1 from assets.deploys where asset_id = @a and status in ('queued','running') and at > now() - interval '20 minutes'", db)) { AgentsApi.P(q, "a", id); if (await q.ExecuteScalarAsync(ct) != null) return Bad("already_running", "An installation is already in progress for this device. Watch the live log."); }
            string cname = ""; string cver = ""; DateTime? seen = null;
            await using (var q = new NpgsqlCommand("select name, version, last_seen from assets.collectors where id = @i", db)) { AgentsApi.P(q, "i", col); await using var rd = await q.ExecuteReaderAsync(ct); if (await rd.ReadAsync(ct)) { cname = rd.GetString(0); cver = rd.GetString(1); seen = rd.IsDBNull(2) ? null : rd.GetDateTime(2); } }
            await using (var up = new NpgsqlCommand("insert into assets.deploys (asset_id, collector_id, status, message) values (@a,@c,'queued','') on conflict (asset_id) do update set collector_id = excluded.collector_id, status = 'queued', message = '', at = now()", db))
            { AgentsApi.P(up, "a", id); AgentsApi.P(up, "c", col); await up.ExecuteNonQueryAsync(ct); }
            await using (var dl = new NpgsqlCommand("delete from assets.deploy_log where asset_id = @a", db)) { AgentsApi.P(dl, "a", id); await dl.ExecuteNonQueryAsync(ct); }
            await DeployNote(db, id, "info", "Queued by " + AgentsApi.Actor(u) + ".", ct);
            var mins = seen == null ? -1 : (int)(DateTime.UtcNow - seen.Value).TotalMinutes;
            if (seen == null || mins >= 3) await DeployNote(db, id, "warn", "The controller \"" + cname + "\" " + (seen == null ? "has never checked in" : "was last seen " + mins + " minutes ago") + ". It has to be online to install. Check its service on that computer.", ct);
            else await DeployNote(db, id, "info", "The controller \"" + cname + "\" (version " + cver + ") is online. It is told to start now.", ct);
            await Wake(db, hub, t, col, ct);
            await audit.WriteAsync("assets.agent_deploy", id.ToString(), "queued via " + col, ct: ct);
            return Results.Ok(new { status = "queued" });
        }));
        Admin(r.MapGet("/api/assets/{id:long}/agent-install", async (long id, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            await using var c = new NpgsqlCommand("select status, message, at from assets.deploys where asset_id = @i", db); AgentsApi.P(c, "i", id);
            await using var rd = await c.ExecuteReaderAsync(ct);
            return await rd.ReadAsync(ct) ? Results.Ok(new { status = rd.GetString(0), message = rd.GetString(1), at = rd.GetDateTime(2) }) : Results.Ok(new { status = "none", message = "", at = (DateTime?)null });
        }));
        // status and the live log together; the page asks again with ?after=<last id> every second or two
        Admin(r.MapGet("/api/assets/{id:long}/agent-install/log", async (long id, long? after, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            string status = "none", message = ""; DateTime? at = null;
            await using (var c = new NpgsqlCommand("select status, message, at from assets.deploys where asset_id = @i", db))
            { AgentsApi.P(c, "i", id); await using var rd = await c.ExecuteReaderAsync(ct); if (await rd.ReadAsync(ct)) { status = rd.GetString(0); message = rd.GetString(1); at = rd.GetDateTime(2); } }
            var items = new List<object>();
            await using (var c = new NpgsqlCommand("select id, at, level, text from assets.deploy_log where asset_id = @a and id > @q order by id limit 300", db))
            {
                AgentsApi.P(c, "a", id); AgentsApi.P(c, "q", after ?? 0); await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) items.Add(new { seq = rd.GetInt64(0), at = rd.GetDateTime(1), level = rd.GetString(2), text = rd.GetString(3) });
            }
            return Results.Ok(new { status, message, at, items });
        }));
        Admin(r.MapPost("/api/assets/collectors/{id}/agent-install-all", async (string id, ITenantContext t, AgentHub hub, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            int n;
            await using (var c = new NpgsqlCommand("insert into assets.deploys (asset_id, collector_id, status) select id, collector_id, 'queued' from assets.assets where collector_id = @i and agent_id = '' and monitor <> '' on conflict (asset_id) do update set status = 'queued', message = '', at = now() where assets.deploys.status not in ('queued','running') or assets.deploys.at < now() - interval '20 minutes'", db))
            { AgentsApi.P(c, "i", id); n = await c.ExecuteNonQueryAsync(ct); }
            await using (var dl = new NpgsqlCommand("delete from assets.deploy_log where asset_id in (select asset_id from assets.deploys where collector_id = @i and status = 'queued')", db)) { AgentsApi.P(dl, "i", id); await dl.ExecuteNonQueryAsync(ct); }
            await using (var nl = new NpgsqlCommand("insert into assets.deploy_log (asset_id, level, text) select asset_id, 'info', 'Queued with the other devices of this controller.' from assets.deploys where collector_id = @i and status = 'queued'", db)) { AgentsApi.P(nl, "i", id); await nl.ExecuteNonQueryAsync(ct); }
            if (n > 0) await Wake(db, hub, t, id, ct);
            await audit.WriteAsync("assets.agent_deploy", id, n + " devices queued", ct: ct);
            return Results.Ok(new { queued = n });
        }));
    }
}
