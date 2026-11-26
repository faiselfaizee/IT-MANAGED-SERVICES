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
/// Release 6.25: patching and print servers, run on a computer with the agent as ordinary agent jobs (so nothing changes in the agent).
/// <list type="bullet">
/// <item>Patching: look for updates (Windows Update through its own API as the agent's SYSTEM account; apt, dnf or yum on Linux), install all or the ones
/// ticked, and optionally restart. The last check is kept with the asset so the fleet page can show what is waiting.</item>
/// <item>Print servers (Windows): list queues, ports and drivers; clear one queue; clear every stuck job; restart the print spooler.</item>
/// </list>
/// Values typed by a person never become script code: a KB number or package name is checked against a pattern and a printer name is passed as a quoted literal.
/// Anything that changes a computer needs a reason, which goes to the asset history and the audit log.
/// </summary>
public static class RemoteActionsApi
{
    const string Data = "DESKASK-DATA|";
    static readonly Regex Kb = new(@"^KB[0-9]{5,8}$", RegexOptions.IgnoreCase);
    static readonly Regex Pkg = new(@"^[A-Za-z0-9][A-Za-z0-9.+:_~-]{0,199}$");

    static string Ps(string s) => ServicesApi.Ps(s);
    static string Reason(JsonElement b) => AgentsApi.Clip((AgentsApi.Str(b, "reason") ?? "").Trim().Replace('\r', ' ').Replace('\n', ' '), 300);

    // ---------------------------------------------------------------- Windows: Windows Update through its own API
    const string WinSearch = @"
$ErrorActionPreference = 'Stop'
$session = New-Object -ComObject Microsoft.Update.Session
$session.ClientApplicationID = 'DeskAsk'
$found = $session.CreateUpdateSearcher().Search(""IsInstalled=0 and IsHidden=0 and Type='Software'"")
";

    const string WinCheck = WinSearch + @"
$items = @()
foreach ($u in $found.Updates) {
  $kb = ''; if ($u.KBArticleIDs.Count -gt 0) { $kb = 'KB' + $u.KBArticleIDs.Item(0) }
  $cat = ''; foreach ($c in $u.Categories) { $cat = [string]$c.Name; break }
  $items += [ordered]@{ title = [string]$u.Title; kb = $kb; severity = [string]$u.MsrcSeverity; category = $cat; sizeMb = [math]::Round($u.MaxDownloadSize / 1MB, 1); reboot = ($u.InstallationBehavior.RebootBehavior -ne 0) }
}
$reboot = $false
try { $reboot = [bool](New-Object -ComObject Microsoft.Update.SystemInfo).RebootRequired } catch { }
$n = @($items).Count
if ($n -eq 0) { 'This computer is up to date.' } else { ""$n update(s) are waiting."" }
'" + Data + @"' + (@{ pending = $n; rebootRequired = $reboot; items = @($items) } | ConvertTo-Json -Compress -Depth 4)";

    static string WinInstall(IReadOnlyList<string> kbs, bool restart) => "$Kbs = @(" + string.Join(", ", kbs.Select(k => Ps(k.ToUpperInvariant()))) + ")\n$Restart = " + (restart ? "$true" : "$false") + WinSearch + @"
$col = New-Object -ComObject Microsoft.Update.UpdateColl
foreach ($u in $found.Updates) {
  $kb = ''; if ($u.KBArticleIDs.Count -gt 0) { $kb = 'KB' + $u.KBArticleIDs.Item(0) }
  if ($Kbs.Count -eq 0 -or $Kbs -contains $kb) { if (-not $u.EulaAccepted) { $u.AcceptEula() }; [void]$col.Add($u) }
}
if ($col.Count -eq 0) { 'No matching updates are waiting.'; exit 0 }
""Downloading $($col.Count) update(s) ...""
$dl = $session.CreateUpdateDownloader(); $dl.Updates = $col; $dr = $dl.Download()
if ($dr.ResultCode -ne 2 -and $dr.ResultCode -ne 3) { throw (""Windows could not download the updates (result "" + $dr.ResultCode + '). Check the internet access and the free disk space.') }
""Installing ...""
$inst = $session.CreateUpdateInstaller(); $inst.Updates = $col; $ir = $inst.Install()
$ok = 0; $bad = 0
for ($i = 0; $i -lt $col.Count; $i++) {
  $res = $ir.GetUpdateResult($i); $t = [string]$col.Item($i).Title
  if ($res.ResultCode -eq 2) { $ok++; ""Installed: $t"" } else { $bad++; (""Failed (result {0}): {1}"" -f $res.ResultCode, $t) }
}
$reboot = [bool]$ir.RebootRequired
if ($reboot) { 'Restart the computer to finish the updates.' }
'" + Data + @"' + (@{ installed = $ok; failed = $bad; rebootRequired = $reboot } | ConvertTo-Json -Compress)
if ($reboot -and $Restart -and $bad -eq 0) { 'Restarting in two minutes.'; shutdown.exe /r /t 120 /c 'DeskAsk: restarting to finish the Windows updates.' | Out-Null }
if ($bad -gt 0) { exit 1 }";

    // ---------------------------------------------------------------- Linux: apt, dnf or yum
    const string LinCheck = @"
esc() { printf '%s' ""$1"" | sed -e 's/\\/\\\\/g' -e 's/""/\\""/g'; }
items=''; n=0
add() { [ $n -gt 0 ] && items=""$items,""; n=$((n+1)); items=""$items{\""title\"":\""$(esc ""$1"")\"",\""kb\"":\""\"",\""version\"":\""$(esc ""$2"")\"",\""severity\"":\""$3\"",\""category\"":\""\"",\""sizeMb\"":0,\""reboot\"":false}""; }
reboot=false
if command -v apt-get >/dev/null 2>&1; then
  apt-get update -qq >/dev/null 2>&1 || true
  while IFS= read -r line; do
    case ""$line"" in Listing*|'') continue ;; esac
    name=${line%%/*}; ver=$(printf '%s' ""$line"" | awk '{print $2}'); sec=''
    case ""$line"" in *-security*) sec='Important' ;; esac
    add ""$name"" ""$ver"" ""$sec""
  done < <(apt list --upgradable 2>/dev/null)
  [ -f /var/run/reboot-required ] && reboot=true
elif command -v dnf >/dev/null 2>&1 || command -v yum >/dev/null 2>&1; then
  pm=dnf; command -v dnf >/dev/null 2>&1 || pm=yum
  while read -r name ver rest; do add ""$name"" ""$ver"" """"; done < <($pm -q check-update 2>/dev/null | awk 'NF==3 && $1 ~ /\./ {print $1, $2}')
  if command -v needs-restarting >/dev/null 2>&1; then needs-restarting -r >/dev/null 2>&1 || reboot=true; fi
else
  echo 'No supported package manager (apt, dnf or yum) was found.'; exit 1
fi
if [ $n -eq 0 ]; then echo 'This computer is up to date.'; else echo ""$n update(s) are waiting.""; fi
printf '" + Data + @"{""pending"":%d,""rebootRequired"":%s,""items"":[%s]}\n' ""$n"" ""$reboot"" ""$items""";

    static string LinInstall(IReadOnlyList<string> pkgs, bool restart) => "PKGS=(" + string.Join(" ", pkgs.Select(ServicesApi.Sh)) + ")\nRESTART=" + (restart ? "1" : "0") + @"
export DEBIAN_FRONTEND=noninteractive
if command -v apt-get >/dev/null 2>&1; then
  apt-get update -qq || true
  if [ ${#PKGS[@]} -eq 0 ]; then apt-get -y -o Dpkg::Options::=--force-confold upgrade; else apt-get -y -o Dpkg::Options::=--force-confold install --only-upgrade -- ""${PKGS[@]}""; fi
elif command -v dnf >/dev/null 2>&1; then
  if [ ${#PKGS[@]} -eq 0 ]; then dnf -y upgrade; else dnf -y upgrade -- ""${PKGS[@]}""; fi
elif command -v yum >/dev/null 2>&1; then
  if [ ${#PKGS[@]} -eq 0 ]; then yum -y update; else yum -y update -- ""${PKGS[@]}""; fi
else echo 'No supported package manager (apt, dnf or yum) was found.'; exit 1; fi
rc=$?
if [ $rc -ne 0 ]; then echo ""The package manager stopped with code $rc.""; exit $rc; fi
reboot=false
[ -f /var/run/reboot-required ] && reboot=true
if command -v needs-restarting >/dev/null 2>&1; then needs-restarting -r >/dev/null 2>&1 || reboot=true; fi
echo 'The updates are installed.'
[ ""$reboot"" = true ] && echo 'Restart the computer to finish the updates.'
printf '" + Data + @"{""installed"":1,""failed"":0,""rebootRequired"":%s}\n' ""$reboot""
if [ ""$reboot"" = true ] && [ ""$RESTART"" = 1 ]; then echo 'Restarting in two minutes.'; shutdown -r +2 'DeskAsk: restarting to finish the updates.' >/dev/null 2>&1; fi
exit 0";

    // ---------------------------------------------------------------- Windows print server
    const string PrintGuard = @"
$ErrorActionPreference = 'Stop'
if (-not (Get-Command Get-Printer -ErrorAction SilentlyContinue)) { throw 'The print management commands are not on this computer (they are part of Windows 8 / Server 2012 and later).' }
";

    const string PrintList = PrintGuard + @"
$queues = @(Get-Printer | ForEach-Object {
  $pj = 0; try { $pj = @(Get-PrintJob -PrinterName $_.Name -ErrorAction Stop).Count } catch { }
  [ordered]@{ name = [string]$_.Name; driver = [string]$_.DriverName; port = [string]$_.PortName; shared = [bool]$_.Shared; shareName = [string]$_.ShareName; status = [string]$_.PrinterStatus; jobs = $pj }
})
$ports = @(Get-PrinterPort | ForEach-Object { [ordered]@{ name = [string]$_.Name; host = [string]$_.PrinterHostAddress; port = [int]$_.PortNumber } })
$drivers = @(Get-PrinterDriver | ForEach-Object { [ordered]@{ name = [string]$_.Name; manufacturer = [string]$_.Manufacturer } })
$sp = Get-Service -Name Spooler
""$($queues.Count) queue(s), $($ports.Count) port(s), $($drivers.Count) driver(s). The print spooler is $($sp.Status).""
'" + Data + @"' + (@{ spooler = [string]$sp.Status; queues = $queues; ports = $ports; drivers = $drivers } | ConvertTo-Json -Compress -Depth 5)";

    static string PrintClear(string printer) => "$Printer = " + Ps(printer) + PrintGuard + @"
$jobs = @(Get-PrintJob -PrinterName $Printer)
if ($jobs.Count -eq 0) { 'The queue is already empty.'; exit 0 }
foreach ($j in $jobs) { Remove-PrintJob -InputObject $j -ErrorAction SilentlyContinue }
$left = @(Get-PrintJob -PrinterName $Printer -ErrorAction SilentlyContinue).Count
if ($left -gt 0) { ""$($jobs.Count - $left) job(s) removed, $left could not be removed. Use Clear all stuck jobs."" } else { ""$($jobs.Count) job(s) removed from $Printer."" }";

    const string PrintClearAll = @"
$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:SystemRoot 'System32\spool\PRINTERS'
Stop-Service -Name Spooler -Force
try {
  $files = @(Get-ChildItem -Path $dir -File -ErrorAction SilentlyContinue)
  $files | Remove-Item -Force -ErrorAction SilentlyContinue
  ""$($files.Count) spool file(s) removed.""
} finally { Start-Service -Name Spooler }
""The print spooler is $((Get-Service -Name Spooler).Status).""";

    const string PrintRestart = @"
$ErrorActionPreference = 'Stop'
Restart-Service -Name Spooler -Force
$s = Get-Service -Name Spooler; $s.WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
""The print spooler is $($s.Status).""";

    // ---------------------------------------------------------------- the busiest processes (what to look at when an alert says the processor or memory is full)
    const string WinProcesses = @"
$ErrorActionPreference = 'Stop'
$cores = [Environment]::ProcessorCount
$first = @{}; foreach ($p in Get-Process) { if ($null -ne $p.CPU) { $first[$p.Id] = [double]$p.CPU } }
Start-Sleep -Seconds 3
$rows = @(foreach ($p in Get-Process) {
  $d = 0.0; if ($first.ContainsKey($p.Id) -and $null -ne $p.CPU) { $d = ([double]$p.CPU - $first[$p.Id]) / 3 / $cores * 100 }
  [pscustomobject]@{ name = [string]$p.ProcessName; id = [int]$p.Id; cpu = [math]::Round($d, 1); memMb = [int][math]::Round($p.WorkingSet64 / 1MB, 0) }
})
$cpu = @($rows | Sort-Object cpu -Descending | Select-Object -First 8)
$mem = @($rows | Sort-Object memMb -Descending | Select-Object -First 8)
'Busiest by processor: ' + (($cpu | Select-Object -First 3 | ForEach-Object { '{0} {1}%' -f $_.name, $_.cpu }) -join ', ')
'Busiest by memory: ' + (($mem | Select-Object -First 3 | ForEach-Object { '{0} {1} MB' -f $_.name, $_.memMb }) -join ', ')
'" + Data + @"' + (@{ cores = $cores; cpu = $cpu; mem = $mem } | ConvertTo-Json -Compress -Depth 4)";

    const string LinProcesses = @"
cores=$(nproc 2>/dev/null || echo 1)
cpu=$(top -b -n 2 -d 2 -w 200 2>/dev/null | awk -v c=""$cores"" '/^ *PID /{n++; next} n==2 && NF>=12 && cnt<8 {gsub(/""/, """", $12); printf ""%s{\""name\"":\""%s\"",\""id\"":%d,\""cpu\"":%.1f,\""memMb\"":0}"", (cnt++ ? "","" : """"), $12, $1, $9 / c}')
mem=$(ps -eo pid=,comm=,rss= --sort=-rss 2>/dev/null | head -8 | awk '{gsub(/""/, """", $2); printf ""%s{\""name\"":\""%s\"",\""id\"":%d,\""cpu\"":0,\""memMb\"":%.0f}"", (NR > 1 ? "","" : """"), $2, $1, $3 / 1024}')
echo 'Processes read.'
printf '" + Data + @"{""cores"":%d,""cpu"":[%s],""mem"":[%s]}\n' ""$cores"" ""$cpu"" ""$mem""";

    // ---------------------------------------------------------------- endpoints
    public static void Map(IEndpointRouteBuilder r, string key)
    {
        var staff = (RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole("admin", "engineer")).RequireModule(key).RequireAccess(key, Acts.Modify);

        // ---- patching: what each computer with an agent last reported as waiting
        r.MapGet("/api/assets/patching", async (HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            var scope = await AssetsAccess.ScopeAsync(http, Acts.Read);
            await using var c = new NpgsqlCommand(@"select a.id, a.tag, a.name, a.os_family, a.os_name, coalesce(co.name, ''), a.last_patch_date, a.patch_check::text, a.patch_check_at
                from assets.assets a left join assets.companies co on co.id = a.company_id
                where a.agent_id <> '' and a.os_family in ('windows', 'linux') and a.status not in ('retired', 'lost') and (not @sc or a.company_id = any(@scope))
                order by coalesce((a.patch_check->>'pending')::int, -1) desc, lower(a.name) limit 3000", db).Scoped(scope);
            var rows = new List<object>(); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                JsonElement? chk = rd.IsDBNull(7) ? null : JsonDocument.Parse(rd.GetString(7)).RootElement.Clone();
                int? pending = chk is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("pending", out var pn) && pn.TryGetInt32(out var pv) ? pv : null;
                bool? reboot = chk is { ValueKind: JsonValueKind.Object } o2 && o2.TryGetProperty("rebootRequired", out var rb) && rb.ValueKind is JsonValueKind.True or JsonValueKind.False ? rb.GetBoolean() : null;
                rows.Add(new
                {
                    id = rd.GetInt64(0), tag = rd.GetString(1), name = rd.GetString(2), os = rd.GetString(3), osName = rd.GetString(4), company = rd.GetString(5),
                    lastPatch = rd.IsDBNull(6) ? null : rd.GetFieldValue<DateOnly>(6).ToString("yyyy-MM-dd"), pending, rebootRequired = reboot, checkedAt = rd.IsDBNull(8) ? (DateTime?)null : rd.GetDateTime(8),
                });
            }
            return Results.Ok(new { items = rows });
        }).RequireAuthorization(p => p.RequireRole(AssetsAccess.Readers)).RequireModule(key).RequireAccess(key, Acts.Read);

        // ---- one computer: the last check that was kept
        r.MapGet("/api/assets/{id:long}/patches", async (long id, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            if (!await AssetsAccess.InScope(db, id, await AssetsAccess.ScopeAsync(http, Acts.Read), ct)) return Results.NotFound();
            await using var c = new NpgsqlCommand("select a.patch_check::text, a.patch_check_at, a.last_patch_date, a.agent_id, a.os_family from assets.assets a where a.id = @i", db); AgentsApi.P(c, "i", id);
            await using var rd = await c.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return Results.NotFound();
            var role = http.User.FindFirstValue("role") ?? "";
            return Results.Ok(new
            {
                check = rd.IsDBNull(0) ? (JsonElement?)null : JsonDocument.Parse(rd.GetString(0)).RootElement.Clone(), checkedAt = rd.IsDBNull(1) ? (DateTime?)null : rd.GetDateTime(1),
                lastPatch = rd.IsDBNull(2) ? null : rd.GetFieldValue<DateOnly>(2).ToString("yyyy-MM-dd"), hasAgent = rd.GetString(3) != "", os = rd.GetString(4), canControl = role is "admin" or "engineer",
            });
        }).RequireAuthorization(p => p.RequireRole(AssetsAccess.Readers)).RequireModule(key).RequireAccess(key, Acts.Read);

        staff(r.MapPost("/api/assets/{id:long}/patches/check", async (long id, HttpContext http, ITenantContext t, IConfiguration cfg, AgentHub hub, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            var (err, tg) = await ServicesApi.Target(http, db, id, cfg, ct); if (err != null) return err;
            var win = tg!.Platform == "windows"; var by = AgentsApi.Actor(http.User);
            var jid = await ServicesApi.Queue(db, hub, t, tg, win ? "powershell" : "bash", "Check for updates", win ? WinCheck : LinCheck, 900, by, ct);
            await audit.WriteAsync("assets.patch_check", id.ToString(), tg.AssetName, ct: ct);
            return Results.Ok(new { id = jid });
        }));

        staff(r.MapPost("/api/assets/{id:long}/patches/install", async (long id, JsonElement b, HttpContext http, ITenantContext t, IConfiguration cfg, AgentHub hub, IAuditLog audit, CancellationToken ct) =>
        {
            var reason = Reason(b);
            if (reason == "") return AgentsApi.Bad("reason_needed", "Say why the updates are installed; it goes to the history and the audit log.");
            var items = new List<string>();
            if (b.ValueKind == JsonValueKind.Object && b.TryGetProperty("items", out var arr))
            {
                if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() > 200) return AgentsApi.Bad("bad_input", "Choose at most 200 updates, or none to install them all.");
                foreach (var e in arr.EnumerateArray()) if (e.ValueKind == JsonValueKind.String) items.Add(e.GetString()!.Trim());
            }
            var restart = b.ValueKind == JsonValueKind.Object && b.TryGetProperty("restart", out var rs) && rs.ValueKind == JsonValueKind.True;
            await using var db = await AgentsApi.Open(t, ct);
            var (err, tg) = await Target(http, db, id, cfg, ct); if (err != null) return err;
            var win = tg!.Platform == "windows";
            if (items.Any(x => !(win ? Kb : Pkg).IsMatch(x))) return AgentsApi.Bad("bad_input", win ? "Updates are chosen by KB number, for example KB5034441." : "Updates are chosen by package name.");
            var by = AgentsApi.Actor(http.User);
            var what = items.Count == 0 ? "all waiting updates" : string.Join(", ", items.Take(10)) + (items.Count > 10 ? $" and {items.Count - 10} more" : "");
            var jid = await ServicesApi.Queue(db, hub, t, tg, win ? "powershell" : "bash", "Install updates", win ? WinInstall(items, restart) : LinInstall(items, restart), 10800, by, ct);
            await audit.WriteAsync("assets.patch_install", id.ToString(), $"{what} on {tg.AssetName}{(restart ? ", restart when needed" : "")} (reason: {reason})", ct: ct);
            await AssetsDevice.History(db, id, by, "patch", JsonSerializer.Serialize(new { did = "install " + what, restart, reason }), ct);
            return Results.Ok(new { id = jid });
        }));

        // ---- the busiest processes right now (read only)
        staff(r.MapPost("/api/assets/{id:long}/processes", async (long id, HttpContext http, ITenantContext t, IConfiguration cfg, AgentHub hub, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            var (err, tg) = await Target(http, db, id, cfg, ct); if (err != null) return err;
            var win = tg!.Platform == "windows";
            var jid = await ServicesApi.Queue(db, hub, t, tg, win ? "powershell" : "bash", "Busiest processes", win ? WinProcesses : LinProcesses, 120, AgentsApi.Actor(http.User), ct);
            return Results.Ok(new { id = jid });
        }));

        // ---- print servers (Windows)
        staff(r.MapPost("/api/assets/{id:long}/printserver", async (long id, JsonElement b, HttpContext http, ITenantContext t, IConfiguration cfg, AgentHub hub, IAuditLog audit, CancellationToken ct) =>
        {
            var action = AgentsApi.Str(b, "action") ?? ""; var printer = (AgentsApi.Str(b, "printer") ?? "").Trim(); var reason = Reason(b);
            if (action is not ("list" or "clear" or "clear-all" or "restart-spooler")) return AgentsApi.Bad("bad_input", "action must be list, clear, clear-all or restart-spooler");
            if (action != "list" && reason == "") return AgentsApi.Bad("reason_needed", "Say why; it goes to the history and the audit log.");
            if (action == "clear" && (printer == "" || printer.Length > 200 || printer.Any(char.IsControl))) return AgentsApi.Bad("bad_input", "Choose a print queue.");
            await using var db = await AgentsApi.Open(t, ct);
            var (err, tg) = await Target(http, db, id, cfg, ct); if (err != null) return err;
            if (tg!.Platform != "windows") return AgentsApi.Bad("windows_only", "Print server tools work on Windows computers.");
            var by = AgentsApi.Actor(http.User);
            var (title, body) = action switch
            {
                "list" => ("Read print queues", PrintList),
                "clear" => ("Clear print queue " + AgentsApi.Clip(printer, 60), PrintClear(printer)),
                "clear-all" => ("Clear all stuck print jobs", PrintClearAll),
                _ => ("Restart print spooler", PrintRestart),
            };
            var jid = await ServicesApi.Queue(db, hub, t, tg, "powershell", title, body, 300, by, ct);
            if (action != "list")
            {
                await audit.WriteAsync("assets.printserver_" + action.Replace('-', '_'), id.ToString(), $"{(action == "clear" ? printer + " on " : "")}{tg.AssetName} (reason: {reason})", ct: ct);
                await AssetsDevice.History(db, id, by, "printserver", JsonSerializer.Serialize(new { did = title, reason }), ct);
            }
            return Results.Ok(new { id = jid });
        }));

        // ---- how an action is going, with the structured result the computer printed (and the check kept on the asset)
        staff(r.MapGet("/api/assets/{id:long}/remote/{jid}", async (long id, string jid, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            string status, output, title; int? exit;
            await using (var c = new NpgsqlCommand("select status, exit_code, output, title from assets.jobs where id = @i and asset_id = @a", db))
            {
                AgentsApi.P(c, "i", jid); AgentsApi.P(c, "a", id);
                await using var rd = await c.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) return Results.NotFound();
                status = rd.GetString(0); exit = rd.IsDBNull(1) ? (int?)null : rd.GetInt32(1); output = rd.GetString(2); title = rd.GetString(3);
            }
            JsonElement? data = null; var lines = new List<string>();
            foreach (var line in output.Split('\n'))
            {
                var l = line.TrimEnd('\r');
                if (l.StartsWith(Data, StringComparison.Ordinal)) { try { data = JsonDocument.Parse(l[Data.Length..]).RootElement.Clone(); } catch (JsonException) { } }
                else if (l.Trim() != "") lines.Add(l);
            }
            if (status == "done" && data != null && title == "Check for updates")
            {
                await using var up = new NpgsqlCommand("update assets.assets set patch_check = @d::jsonb, patch_check_at = now() where id = @i", db);
                AgentsApi.P(up, "d", data.Value.GetRawText()); AgentsApi.P(up, "i", id); await up.ExecuteNonQueryAsync(ct);
            }
            var text = string.Join("\n", lines); if (text.Length > 6000) text = text[^6000..];
            return Results.Ok(new { status, exit, output = text, data });
        }));
    }

    static Task<(IResult? Error, AgentsApi.Target? T)> Target(HttpContext http, NpgsqlConnection db, long id, IConfiguration cfg, CancellationToken ct) => ServicesApi.Target(http, db, id, cfg, ct);
}
