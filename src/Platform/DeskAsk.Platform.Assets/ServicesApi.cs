using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace DeskAsk.Platform.Assets;

/// <summary>
/// Release 6.3: services and programs on a computer with the agent, from Assets (and the Monitoring device page).
/// <list type="bullet">
/// <item>Services: start, stop, restart, startup type (automatic, automatic delayed, manual, disabled) on Windows; start, stop, restart, enable, disable on Linux (systemd).</item>
/// <item>Log On (Windows): a built-in account (Local System, Network Service, Local Service), or an account with its password. The password goes
/// through the one-time hand-over the Vault uses: sealed until the agent fetches it once, then wiped; it is never in a script, a job or a log.</item>
/// <item>Uninstall: the program's silent uninstall entry (Windows) or the package manager (Linux), with a reason that goes to the history and the audit log.</item>
/// </list>
/// Values typed by a person never become script code: names are checked and then passed as quoted literals.
/// </summary>
public static class ServicesApi
{
    static readonly Regex WinService = new(@"^[A-Za-z0-9][A-Za-z0-9._@$ +-]{0,255}$");
    static readonly Regex LinUnit = new(@"^[A-Za-z0-9][A-Za-z0-9@._:+-]{0,199}$");
    static readonly Regex Logon = new(@"^[A-Za-z0-9.][A-Za-z0-9._$@ -]{0,63}(\\[A-Za-z0-9][A-Za-z0-9._$ -]{0,63})?$");
    static readonly Regex Package = new(@"^[A-Za-z0-9][A-Za-z0-9.+:_~-]{0,199}$");
    static readonly Dictionary<string, string> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LocalSystem"] = "LocalSystem", ["Local System"] = "LocalSystem", ["NT AUTHORITY\\NetworkService"] = "NT AUTHORITY\\NetworkService", ["NetworkService"] = "NT AUTHORITY\\NetworkService",
        ["NT AUTHORITY\\LocalService"] = "NT AUTHORITY\\LocalService", ["LocalService"] = "NT AUTHORITY\\LocalService",
    };
    const string Marker = "DESKASK-SERVICE|";

    /// <summary>A PowerShell string literal: single quotes, with any single quote doubled (curly quotes too, which PowerShell also treats as quotes).</summary>
    public static string Ps(string s) => "'" + s.Replace("'", "''").Replace("‘", "‘‘").Replace("’", "’’") + "'";
    /// <summary>A bash string literal in single quotes.</summary>
    public static string Sh(string s) => "'" + s.Replace("'", "'\"'\"'") + "'";

    const string WinState = @"
$c = Get-CimInstance Win32_Service -Filter (""Name='"" + $Name.Replace(""'"", ""''"") + ""'"")
$st = switch ([string]$c.State) { 'Running' { 'running' } 'Stopped' { 'stopped' } 'Start Pending' { 'starting' } 'Stop Pending' { 'stopping' } 'Paused' { 'paused' } default { 'unknown' } }
$sm = switch ([string]$c.StartMode) { 'Auto' { if ($c.DelayedAutoStart) { 'delayed' } else { 'auto' } } 'Manual' { 'manual' } 'Disabled' { 'disabled' } default { 'other' } }
'" + Marker + @"' + (@{ name = $c.Name; display = $c.DisplayName; state = $st; start = $sm; logon = [string]$c.StartName } | ConvertTo-Json -Compress)";

    static string WinControl(string name, string action) => "$Name = " + Ps(name) + "\n$Action = " + Ps(action) + @"
$ErrorActionPreference = 'Stop'
$svc = Get-Service -Name $Name
switch ($Action) {
  'start'   { if ($svc.Status -ne 'Running') { Start-Service -InputObject $svc; $svc.WaitForStatus('Running', [TimeSpan]::FromSeconds(60)) } }
  'stop'    { if ($svc.Status -ne 'Stopped') { Stop-Service -InputObject $svc -Force; $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60)) } }
  'restart' { Restart-Service -InputObject $svc -Force; $svc.WaitForStatus('Running', [TimeSpan]::FromSeconds(60)) }
}
$svc.Refresh()
'{0} ({1}) is now {2}.' -f $svc.DisplayName, $svc.Name, $svc.Status" + WinState;

    static string WinStartup(string name, string type) => "$Name = " + Ps(name) + "\n$Mode = " + Ps(type) + @"
$ErrorActionPreference = 'Stop'
$null = Get-Service -Name $Name
$out = & sc.exe config $Name start= $Mode 2>&1
if ($LASTEXITCODE -ne 0) { throw ('Windows could not change the startup type: ' + ($out -join ' ')) }
'The startup type of {0} is changed.' -f $Name" + WinState;

    static string WinBuiltInLogon(string name, string account) => "$Name = " + Ps(name) + "\n$Account = " + Ps(account) + @"
$ErrorActionPreference = 'Stop'
$c0 = Get-CimInstance Win32_Service -Filter (""Name='"" + $Name.Replace(""'"", ""''"") + ""'"")
if (-not $c0) { throw ""There is no service named $Name on this computer."" }
$r = $c0 | Invoke-CimMethod -MethodName Change -Arguments @{ StartName = $Account; StartPassword = '' }
if ($r.ReturnValue -ne 0) { throw ('Windows could not change the account (code ' + $r.ReturnValue + ').') }
'{0} now runs as {1}. Restart the service for the change to take effect.' -f $Name, $Account" + WinState;

    static string LinControl(string name, string action) => "NAME=" + Sh(name) + "\nACTION=" + Sh(action) + @"
case ""$ACTION"" in
  start|stop|restart) systemctl ""$ACTION"" -- ""$NAME"" || exit $? ;;
  enable|disable) systemctl ""$ACTION"" -- ""$NAME"" || exit $? ;;
esac
a=$(systemctl is-active -- ""$NAME"" 2>/dev/null); e=$(systemctl is-enabled -- ""$NAME"" 2>/dev/null)
case ""$a"" in active) st=running ;; failed) st=failed ;; activating) st=starting ;; deactivating) st=stopping ;; *) st=inactive ;; esac
case ""$e"" in enabled|enabled-runtime) sm=auto ;; disabled) sm=disabled ;; *) sm=other ;; esac
echo ""$NAME is now $a ($e).""
printf '" + Marker + @"{""name"":""%s"",""state"":""%s"",""start"":""%s""}\n' ""$NAME"" ""$st"" ""$sm""";

    static string WinUninstall(string name, string version) => "$Name = " + Ps(name) + "\n$Version = " + Ps(version) + @"
$ErrorActionPreference = 'Stop'
$keys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
$found = @(Get-ItemProperty -Path $keys -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -and ($_.DisplayName -eq $Name) -and (-not $Version -or $_.DisplayVersion -eq $Version) -and -not $_.SystemComponent })
if ($found.Count -eq 0) { throw ""No installed program named '$Name' was found on this computer. It may already be removed; refresh the inventory."" }
$uniq = @($found | Select-Object -ExpandProperty UninstallString -Unique)
if ($uniq.Count -gt 1) { throw ""More than one program matches '$Name'. Uninstall it from the computer itself."" }
$p = $found[0]; $q = [string]$p.QuietUninstallString; $u = [string]$p.UninstallString
if ($q) { $cmd = $q }
elseif ($u -match '(?i)msiexec(\.exe)?.*?(\{[0-9A-F\-]{36}\})') { $cmd = 'msiexec.exe /x ' + $Matches[2] + ' /qn /norestart' }
else { throw 'This program has no silent uninstall command, so it was not removed. Uninstall it from the computer itself, or with its vendor''s switches in Run command.' }
$proc = Start-Process -FilePath ""$env:SystemRoot\System32\cmd.exe"" -ArgumentList ('/c ""' + $cmd + '""') -PassThru -WindowStyle Hidden
if (-not $proc.WaitForExit(900000)) { try { $proc.Kill() } catch { }; throw 'The uninstaller did not finish within 15 minutes and was stopped.' }
$code = $proc.ExitCode
if (@(0, 1641, 3010) -notcontains $code) { throw ""The uninstaller of $($p.DisplayName) stopped with code $code."" }
if ($code -eq 0) { ""$($p.DisplayName) $($p.DisplayVersion) is uninstalled."" } else { ""$($p.DisplayName) $($p.DisplayVersion) is uninstalled. Restart the computer to finish."" }";

    static string LinUninstall(string pkg) => "PKG=" + Sh(pkg) + @"
export DEBIAN_FRONTEND=noninteractive
if command -v apt-get >/dev/null 2>&1; then apt-get -y remove -- ""$PKG""
elif command -v dnf >/dev/null 2>&1; then dnf -y remove -- ""$PKG""
elif command -v yum >/dev/null 2>&1; then yum -y remove -- ""$PKG""
elif command -v zypper >/dev/null 2>&1; then zypper --non-interactive remove -- ""$PKG""
else echo ""No supported package manager (apt, dnf, yum, zypper) was found.""; exit 1; fi
rc=$?; [ $rc -eq 0 ] && echo ""$PKG is uninstalled.""; exit $rc";

    static async Task<string> Queue(NpgsqlConnection db, AgentHub hub, ITenantContext t, AgentsApi.Target tg, string shell, string title, string body, int timeout, string by, CancellationToken ct)
    {
        var jid = "j" + AgentsApi.Rand(12);
        await using (var ins = new NpgsqlCommand("insert into assets.jobs (id, asset_id, agent_id, shell, title, body, status, by_name, timeout_s) values (@i,@a,@g,@s,@t,@b,'queued',@u,@to)", db))
        {
            AgentsApi.P(ins, "i", jid); AgentsApi.P(ins, "a", tg.AssetId); AgentsApi.P(ins, "g", tg.AgentId); AgentsApi.P(ins, "s", shell); AgentsApi.P(ins, "t", title);
            AgentsApi.P(ins, "b", body); AgentsApi.P(ins, "u", by); AgentsApi.P(ins, "to", timeout); await ins.ExecuteNonQueryAsync(ct);
        }
        await AgentsApi.Enqueue(db, hub, t, tg.AgentId, "job", new { id = jid, shell, body, timeout }, ct);
        return jid;
    }

    /// <summary>The asset if it is in the caller's scope, with the checks remote commands use (agent installed, remote tools allowed, online).</summary>
    static async Task<(IResult? Error, AgentsApi.Target? T)> Target(HttpContext http, NpgsqlConnection db, long id, IConfiguration cfg, CancellationToken ct)
    {
        if (!await AssetsAccess.InScope(db, id, await AssetsAccess.ScopeAsync(http, Acts.Modify), ct)) return (Results.NotFound(), null);
        return await AgentsApi.Resolve(db, id, true, AgentsApi.Offline(cfg), ct);
    }

    static string Reason(JsonElement b) => (AgentsApi.Str(b, "reason") ?? "").Trim().Replace('\r', ' ').Replace('\n', ' ');

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        var staff = (RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole("admin", "engineer")).RequireModule(key).RequireAccess(key, Acts.Modify);

        // the services the agent last reported, with when
        r.MapGet("/api/assets/{id:long}/services", async (long id, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            if (!await AssetsAccess.InScope(db, id, await AssetsAccess.ScopeAsync(http, Acts.Read), ct)) return Results.NotFound();
            await using var c = new NpgsqlCommand(@"select a.m_svc_inv::text, a.m_svc_inv_at, coalesce(g.platform, ''), coalesce(g.version, ''), a.agent_id
                from assets.assets a left join assets.agents g on g.id = a.agent_id where a.id = @i", db);
            AgentsApi.P(c, "i", id);
            await using var rd = await c.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return Results.NotFound();
            var role = http.User.FindFirstValue("role") ?? "";
            return Results.Ok(new
            {
                items = JsonDocument.Parse(rd.GetString(0)).RootElement.Clone(), at = rd.IsDBNull(1) ? (DateTime?)null : rd.GetDateTime(1), platform = rd.GetString(2), agentVersion = rd.GetString(3),
                hasAgent = rd.GetString(4) != "", canControl = role is "admin" or "engineer", canLogon = role == "admin" && rd.GetString(2) == "windows",
            });
        }).RequireAuthorization().RequireModule(key).RequireAccess(key, Acts.Read);

        // start, stop, restart, or change the startup type
        staff(r.MapPost("/api/assets/{id:long}/services/action", async (long id, JsonElement b, HttpContext http, ITenantContext t, IConfiguration cfg, AgentHub hub, IAuditLog audit, CancellationToken ct) =>
        {
            var name = (AgentsApi.Str(b, "name") ?? "").Trim(); var action = AgentsApi.Str(b, "action") ?? ""; var startup = AgentsApi.Str(b, "startup") ?? "";
            if (action is not ("start" or "stop" or "restart" or "startup")) return AgentsApi.Bad("bad_input", "action must be start, stop, restart or startup");
            await using var db = await AgentsApi.Open(t, ct);
            var (err, tg) = await Target(http, db, id, cfg, ct); if (err != null) return err;
            var win = tg!.Platform == "windows";
            if (!(win ? WinService : LinUnit).IsMatch(name)) return AgentsApi.Bad("bad_input", "That is not a valid service name.");
            string body, title, what;
            if (action == "startup")
            {
                if (startup is not ("auto" or "delayed" or "manual" or "disabled")) return AgentsApi.Bad("bad_input", "startup must be auto, delayed, manual or disabled");
                if (!win && startup == "delayed") return AgentsApi.Bad("bad_input", "Linux services have no delayed start; choose automatic.");
                body = win ? WinStartup(name, startup switch { "auto" => "auto", "delayed" => "delayed-auto", "manual" => "demand", _ => "disabled" }) : LinControl(name, startup == "auto" ? "enable" : "disable");
                what = "startup type " + startup switch { "auto" => "Automatic", "delayed" => "Automatic (delayed start)", "manual" => "Manual", _ => "Disabled" };
                title = $"Service {name}: {what}";
            }
            else
            {
                body = win ? WinControl(name, action) : LinControl(name, action);
                what = action; title = $"Service {name}: {action}";
            }
            var by = AgentsApi.Actor(http.User); var reason = AgentsApi.Clip(Reason(b), 300);
            var jid = await Queue(db, hub, t, tg, win ? "powershell" : "bash", title, body, 180, by, ct);
            await audit.WriteAsync("assets.service_" + (action == "startup" ? "startup" : action), id.ToString(), $"{name} on {tg.AssetName}: {what}" + (reason == "" ? "" : $" (reason: {reason})"), ct: ct);
            await AssetsDevice.History(db, id, by, "service", JsonSerializer.Serialize(new { service = name, did = what, reason }), ct);
            return Results.Ok(new { id = jid });
        }));

        // the account a Windows service runs as: administrators only, with a reason
        r.MapPost("/api/assets/{id:long}/services/logon", async (long id, JsonElement b, HttpContext http, ITenantContext t, IConfiguration cfg, AgentHub hub, IAuditLog audit, CancellationToken ct) =>
        {
            var name = (AgentsApi.Str(b, "name") ?? "").Trim(); var account = (AgentsApi.Str(b, "account") ?? "").Trim(); var password = AgentsApi.Str(b, "password") ?? "";
            var reason = AgentsApi.Clip(Reason(b), 300);
            if (reason == "") return AgentsApi.Bad("reason_needed", "Say why the account is changed; it goes to the history and the audit log.");
            await using var db = await AgentsApi.Open(t, ct);
            var (err, tg) = await Target(http, db, id, cfg, ct); if (err != null) return err;
            if (tg!.Platform != "windows") return AgentsApi.Bad("windows_only", "The account a service runs as can only be changed on Windows. On Linux, set User= in the unit file.");
            if (!WinService.IsMatch(name)) return AgentsApi.Bad("bad_input", "That is not a valid service name.");
            var by = AgentsApi.Actor(http.User);
            if (BuiltIn.TryGetValue(account, out var builtIn))
            {
                var jid = await Queue(db, hub, t, tg, "powershell", $"Service {name}: log on as {builtIn}", WinBuiltInLogon(name, builtIn), 120, by, ct);
                await audit.WriteAsync("assets.service_logon", id.ToString(), $"{name} on {tg.AssetName} runs as {builtIn} (reason: {reason})", ct: ct);
                await AssetsDevice.History(db, id, by, "service", JsonSerializer.Serialize(new { service = name, did = "log on as " + builtIn, reason }), ct);
                return Results.Ok(new { id = jid, kind = "job" });
            }
            if (!Logon.IsMatch(account)) return AgentsApi.Bad("bad_input", "Type the account as DOMAIN\\name, .\\name (an account on that computer) or name@domain.");
            if (password == "" || password.Length > 256) return AgentsApi.Bad("bad_input", "Type the account's password (at most 256 characters).");
            var hid = "sv" + AgentsApi.Rand(24);
            await using (var c = new NpgsqlCommand("insert into assets.secret_handoff (id, asset_id, agent_id, account, target, secret_sealed, by_name) values (@i, @a, @g, @u, @t, @s, @b)", db))
            {
                AgentsApi.P(c, "i", hid); AgentsApi.P(c, "a", id); AgentsApi.P(c, "g", tg.AgentId); AgentsApi.P(c, "u", account); AgentsApi.P(c, "t", "service:" + name);
                AgentsApi.P(c, "s", SecretBox.Seal(cfg, t.Require().Id, password)); AgentsApi.P(c, "b", by); await c.ExecuteNonQueryAsync(ct);
            }
            await AgentsApi.Enqueue(db, hub, t, tg.AgentId, "setpw", new { id = hid }, ct);
            await audit.WriteAsync("assets.service_logon", id.ToString(), $"{name} on {tg.AssetName} is set to run as {account} (reason: {reason})", ct: ct);
            await AssetsDevice.History(db, id, by, "service", JsonSerializer.Serialize(new { service = name, did = "log on as " + account, reason }), ct);
            return Results.Ok(new { id = hid, kind = "handoff" });
        }).RequireAuthorization(p => p.RequireRole("admin")).RequireModule(key).RequireAccess(key, Acts.Modify);

        // how a service account change is going (the hand-over is fetched, done or failed)
        r.MapGet("/api/assets/{id:long}/services/logon/{hid}", async (long id, string hid, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            await using var c = new NpgsqlCommand("select state, error from assets.secret_handoff where id = @i and asset_id = @a and target like 'service:%'", db);
            AgentsApi.P(c, "i", hid); AgentsApi.P(c, "a", id);
            await using var rd = await c.ExecuteReaderAsync(ct);
            return await rd.ReadAsync(ct) ? Results.Ok(new { state = rd.GetString(0), error = rd.GetString(1) }) : Results.NotFound();
        }).RequireAuthorization(p => p.RequireRole("admin")).RequireModule(key);

        // uninstall a program, with a reason
        staff(r.MapPost("/api/assets/{id:long}/apps/uninstall", async (long id, JsonElement b, HttpContext http, ITenantContext t, IConfiguration cfg, AgentHub hub, IAuditLog audit, CancellationToken ct) =>
        {
            var name = (AgentsApi.Str(b, "name") ?? "").Trim(); var version = (AgentsApi.Str(b, "version") ?? "").Trim(); var reason = AgentsApi.Clip(Reason(b), 300);
            if (reason == "") return AgentsApi.Bad("reason_needed", "Say why the program is removed; it goes to the history and the audit log.");
            if (name == "" || name.Length > 260 || name.Any(char.IsControl) || version.Length > 100 || version.Any(char.IsControl)) return AgentsApi.Bad("bad_input", "That is not a valid program name.");
            await using var db = await AgentsApi.Open(t, ct);
            var (err, tg) = await Target(http, db, id, cfg, ct); if (err != null) return err;
            var win = tg!.Platform == "windows";
            if (!win && !Package.IsMatch(name)) return AgentsApi.Bad("bad_input", "That is not a valid package name.");
            var by = AgentsApi.Actor(http.User);
            var jid = await Queue(db, hub, t, tg, win ? "powershell" : "bash", $"Uninstall {AgentsApi.Clip(name, 80)}", win ? WinUninstall(name, version) : LinUninstall(name), 1200, by, ct);
            await audit.WriteAsync("assets.app_uninstall", id.ToString(), $"{name} {version} on {tg.AssetName} (reason: {reason})".Replace("  ", " "), ct: ct);
            await AssetsDevice.History(db, id, by, "uninstall", JsonSerializer.Serialize(new { program = name, version, reason }), ct);
            return Results.Ok(new { id = jid });
        }));

        // how an action is going: its state, its output and, for services, the state the computer reported afterwards
        staff(r.MapGet("/api/assets/{id:long}/actions/{jid}", async (long id, string jid, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            await using var c = new NpgsqlCommand("select status, exit_code, output from assets.jobs where id = @i and asset_id = @a", db);
            AgentsApi.P(c, "i", jid); AgentsApi.P(c, "a", id);
            await using var rd = await c.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return Results.NotFound();
            var output = rd.GetString(2); JsonElement? service = null; var lines = new List<string>();
            foreach (var line in output.Split('\n'))
            {
                var l = line.TrimEnd('\r');
                if (l.StartsWith(Marker, StringComparison.Ordinal)) { try { service = JsonDocument.Parse(l[Marker.Length..]).RootElement.Clone(); } catch (JsonException) { } }
                else if (l.Trim() != "") lines.Add(l);
            }
            var text = string.Join("\n", lines); if (text.Length > 4000) text = text[^4000..];
            return Results.Ok(new { status = rd.GetString(0), exit = rd.IsDBNull(1) ? (int?)null : rd.GetInt32(1), output = text, service });
        }));

        // ask the agent for fresh inventory after an uninstall (and fold a reported service state into the list at once)
        staff(r.MapPost("/api/assets/{id:long}/actions/refresh", async (long id, HttpContext http, ITenantContext t, AgentHub hub, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            if (!await AssetsAccess.InScope(db, id, await AssetsAccess.ScopeAsync(http, Acts.Modify), ct)) return Results.NotFound();
            await using var c = new NpgsqlCommand("select agent_id from assets.assets where id = @i", db); AgentsApi.P(c, "i", id);
            if (await c.ExecuteScalarAsync(ct) is not string agent || agent == "") return AgentsApi.Bad("no_agent", "This asset has no agent.");
            await AgentsApi.Enqueue(db, hub, t, agent, "collect", new { }, ct);
            return Results.Ok(new { queued = true });
        }));
    }
}
