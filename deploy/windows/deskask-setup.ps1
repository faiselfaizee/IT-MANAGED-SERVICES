#Requires -RunAsAdministrator
<#
.SYNOPSIS
  A temporary setup page for DeskAsk Platform on Windows Server. Opens in your browser, runs on this machine only.
.DESCRIPTION
  Tab 1 installs the platform (once): any drive, the database, the service, optionally IIS in front of it.
  Tab 2 creates a customer (every time a new customer comes in): database, schema, first administrator.
  Both show what is happening, live. Close the page with the "Close setup" button when you are done.
  Keep this file next to install-windows.ps1 and the release files (the .dkb bundle, deskask-updater-windows-amd64.exe, release-public.pem).
.EXAMPLE
  .\deskask-setup.ps1
#>
[CmdletBinding()]
param(
    [int]$Port = 8765,
    [switch]$NoBrowser,
    [string]$Token = ''          # for tests; normally a random one is made
)
$ErrorActionPreference = 'Stop'
$ServiceName = 'DeskAskPlatform'
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$Installer = Join-Path $Here 'install-windows.ps1'
if (-not (Test-Path $Installer)) { throw "install-windows.ps1 must be in the same folder as this script ($Here)." }
if (-not $Token) { $b = New-Object byte[] 18; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); $Token = ([Convert]::ToBase64String($b) -replace '[^A-Za-z0-9]', 'x') }
$Work = Join-Path ([IO.Path]::GetTempPath()) ('deskask-setup-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $Work | Out-Null
$script:Jobs = @{}
$script:Stop = $false

# ---------------------------------------------------------------- what runs in the background (one child process per job)
$JobScript = @'
param([string]$SpecFile, [string]$Log)
$ErrorActionPreference = 'Stop'
$spec = Get-Content -Raw -Encoding UTF8 $SpecFile | ConvertFrom-Json
function Log([string]$m) { Add-Content -Path $Log -Value $m -Encoding UTF8 }
function Step([string]$m) { Log "==> $m" }

function Find-Psql {
    $c = Get-Command psql.exe -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    $f = Get-ChildItem "$env:ProgramFiles\PostgreSQL\*\bin\psql.exe" -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
    if ($f) { return $f.FullName }
    return $null
}

function Prepare-Database($s) {
    Step "Preparing PostgreSQL: login '$($s.dbUser)' and database '$($s.dbName)'"
    $psql = Find-Psql
    if (-not $psql) { throw 'psql.exe was not found (PostgreSQL client). Add its bin folder to PATH, or create the login and database yourself and untick this option.' }
    $pw = $env:WIZ_DBPW.Replace("'", "''")
    $sqlFile = Join-Path (Split-Path $SpecFile) 'prepare-db.sql'
    $sql = @"
do `$`$ begin
  if not exists (select from pg_roles where rolname = '$($s.dbUser)') then create role "$($s.dbUser)" login password '$pw' createdb;
  else alter role "$($s.dbUser)" login password '$pw' createdb; end if;
end `$`$;
select 'create database "$($s.dbName)" owner "$($s.dbUser)"' where not exists (select from pg_database where datname = '$($s.dbName)')
\gexec
"@
    Set-Content -Path $sqlFile -Value $sql -Encoding UTF8
    try {
        $env:PGPASSWORD = $env:WIZ_PGPW
        $ErrorActionPreference = 'Continue'
        & $psql -w -v ON_ERROR_STOP=1 -h $s.dbHost -p $s.dbPort -U $s.pgUser -d postgres -f $sqlFile 2>&1 | ForEach-Object { Log "$_" }
        $rc = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'
        if ($rc -ne 0) { throw "psql failed with exit code $rc (check the PostgreSQL administrator name and password)." }
        Log 'PostgreSQL is ready.'
    } finally { Remove-Item $sqlFile -Force -ErrorAction SilentlyContinue; Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue }
}

function Http-Get([string]$url, [string]$hostHeader) {
    $rq = [Net.HttpWebRequest]::Create($url); $rq.Timeout = 15000
    if ($hostHeader) { $rq.Host = $hostHeader }
    try { $resp = $rq.GetResponse() } catch [Net.WebException] { $resp = $_.Exception.Response; if (-not $resp) { return [pscustomobject]@{ code = 0; server = ''; body = $_.Exception.Message } } }
    $text = (New-Object IO.StreamReader($resp.GetResponseStream())).ReadToEnd()
    $srv = $resp.Headers['Server']; $code = [int]$resp.StatusCode; $resp.Close()
    if ($text.Length -gt 30000) { $text = $text.Substring(0, 30000) }
    return [pscustomobject]@{ code = $code; server = $srv; body = $text }
}

# the short version of an IIS error page: its title and the fields that say what is wrong
function Explain-Html([string]$html) {
    $t = [regex]::Replace($html, '<[^>]+>', "`n"); $t = [regex]::Replace($t, '[ \t]+', ' ')
    $lines = @($t -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $out = @()
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^HTTP Error \d' -or $lines[$i] -match '^(Module|Notification|Handler|Error Code|Config Error|Config File|Requested URL|Physical Path)$') {
            $v = if ($lines[$i] -match '^HTTP Error') { if ($i + 1 -lt $lines.Count) { $lines[$i] + ' ' + $lines[$i + 1] } else { $lines[$i] } } elseif ($i + 1 -lt $lines.Count) { $lines[$i] + ': ' + $lines[$i + 1] } else { $lines[$i] }
            $out += $v
        }
        if ($lines[$i] -match '^Most likely causes' -and $i + 1 -lt $lines.Count) { $out += 'Likely cause: ' + $lines[$i + 1] }
    }
    if ($out.Count -eq 0) { return ($t.Trim() -replace '\s+', ' ').Substring(0, [math]::Min(200, $t.Trim().Length)) }
    return ($out -join "`n    ")

}

function Setup-Iis($s) {
    Step 'Setting up IIS as the front door'
    $inet = Join-Path $env:windir 'System32\inetsrv'
    $missing = @()
    if (-not (Get-Service W3SVC -ErrorAction SilentlyContinue)) { $missing += 'IIS itself (run: Install-WindowsFeature Web-Server,Web-Scripting-Tools)' }
    if (-not (Test-Path (Join-Path $inet 'rewrite.dll'))) { $missing += 'the IIS URL Rewrite module' }
    if (-not (Test-Path "$env:ProgramFiles\IIS\Application Request Routing\requestRouter.dll")) { $missing += 'the IIS Application Request Routing (ARR) module' }
    if ($missing.Count -gt 0) {
        Log 'IIS cannot be set up yet. Still missing:'
        $missing | ForEach-Object { Log "  - $_" }
        Log 'Install them (Microsoft downloads: "URL Rewrite" and "Application Request Routing 3.0"), then run this step again. The platform itself is installed.'
        throw 'IIS prerequisites are missing.'
    }
    Import-Module WebAdministration
    $site = $s.iisSite; $dir = Join-Path $s.root 'iis'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $u = [Uri]($s.urls -split ';')[0].Replace('+', 'localhost').Replace('*', 'localhost')
    $target = "http://localhost:$($u.Port)"

    Log 'Allowing IIS to pass requests on to the platform and to keep the customer address (host name) as it came in'
    $apphost = 'MACHINE/WEBROOT/APPHOST'
    Set-WebConfigurationProperty -PSPath $apphost -Filter 'system.webServer/proxy' -Name 'enabled' -Value $true
    Set-WebConfigurationProperty -PSPath $apphost -Filter 'system.webServer/proxy' -Name 'preserveHostHeader' -Value $true
    Set-WebConfigurationProperty -PSPath $apphost -Filter 'system.webServer/proxy' -Name 'timeout' -Value '00:10:00'
    $have = @(Get-WebConfigurationProperty -PSPath $apphost -Filter 'system.webServer/rewrite/allowedServerVariables' -Name '.' -ErrorAction SilentlyContinue | ForEach-Object { $_.Collection } | Where-Object { $_.name -eq 'HTTP_X_FORWARDED_PROTO' })
    if ($have.Count -eq 0) {
        Add-WebConfigurationProperty -PSPath $apphost -Filter 'system.webServer/rewrite/allowedServerVariables' -Name '.' -Value @{ name = 'HTTP_X_FORWARDED_PROTO' }
        Log 'Allowed the server variable HTTP_X_FORWARDED_PROTO'
    } else { Log 'The server variable HTTP_X_FORWARDED_PROTO is already allowed' }

    $cfg = @"
<?xml version="1.0" encoding="UTF-8"?>
<configuration>
  <system.webServer>
    <rewrite>
      <rules>
        <rule name="DeskAsk https" stopProcessing="true">
          <match url="(.*)" />
          <conditions><add input="{HTTPS}" pattern="on" /></conditions>
          <serverVariables><set name="HTTP_X_FORWARDED_PROTO" value="https" /></serverVariables>
          <action type="Rewrite" url="$target/{R:1}" appendQueryString="true" />
        </rule>
        <rule name="DeskAsk http" stopProcessing="true">
          <match url="(.*)" />
          <serverVariables><set name="HTTP_X_FORWARDED_PROTO" value="http" /></serverVariables>
          <action type="Rewrite" url="$target/{R:1}" appendQueryString="true" />
        </rule>
      </rules>
    </rewrite>
    <security><requestFiltering><requestLimits maxAllowedContentLength="1073741824" /></requestFiltering></security>
  </system.webServer>
</configuration>
"@
    Set-Content -Path (Join-Path $dir 'web.config') -Value $cfg -Encoding UTF8

    $hosts = @($s.iisHosts | Where-Object { $_ })
    if ($hosts.Count -eq 0) { $hosts = @('') }
    foreach ($h in $hosts) {
        foreach ($ws in (Get-Website)) {
            if ($ws.Name -ne $site -and ($ws.bindings.Collection | Where-Object { $_.protocol -eq 'http' -and $_.bindingInformation -eq "*:$($s.httpPort):$h" })) {
                $what = if ($h) { "the host name '$h'" } else { 'any host name' }
                throw "The IIS site '$($ws.Name)' already answers $what on port $($s.httpPort). Stop it (Stop-Website '$($ws.Name)') or give this site host names of its own."
            }
        }
    }
    if (Get-Website -Name $site -ErrorAction SilentlyContinue) { Log "Replacing the existing IIS site '$site'"; Remove-Website -Name $site }
    if (-not (Test-Path "IIS:\AppPools\$site")) { New-WebAppPool -Name $site | Out-Null }
    Set-ItemProperty "IIS:\AppPools\$site" -Name managedRuntimeVersion -Value ''
    New-Website -Name $site -PhysicalPath $dir -ApplicationPool $site -Port $s.httpPort -IPAddress '*' -HostHeader $hosts[0] -Force | Out-Null
    foreach ($h in $hosts | Select-Object -Skip 1) { New-WebBinding -Name $site -Protocol http -Port $s.httpPort -IPAddress '*' -HostHeader $h }
    $ports = @($s.httpPort)
    if ($s.certThumbprint) {
        foreach ($h in $hosts) {
            $flags = if ($h) { 1 } else { 0 }     # 1 = SNI, needed when several host names share the port
            New-WebBinding -Name $site -Protocol https -Port $s.httpsPort -IPAddress '*' -HostHeader $h -SslFlags $flags
        }
        foreach ($b in (Get-WebBinding -Name $site -Protocol https)) { $b.AddSslCertificate($s.certThumbprint, 'My') }
        $ports += $s.httpsPort
        Log "HTTPS is on, port $($s.httpsPort)"
    } else { Log 'No certificate chosen: only http. Sign-in needs https, so add a certificate before real use.' }
    foreach ($p in $ports) {
        if (-not (Get-NetFirewallRule -DisplayName "DeskAsk IIS $p" -ErrorAction SilentlyContinue)) {
            New-NetFirewallRule -DisplayName "DeskAsk IIS $p" -Direction Inbound -Protocol TCP -LocalPort $p -Action Allow | Out-Null
        }
    }
    Start-Website -Name $site
    Start-Sleep -Seconds 2
    $hh = $hosts[0]
    $direct = Http-Get "$target/health" ''
    Log "Platform directly ($target): HTTP $($direct.code) $($direct.body.Substring(0, [math]::Min(120, $direct.body.Length)))"
    $via = Http-Get "http://localhost:$($s.httpPort)/health" $hh
    $who = if ($hh) { "host '$hh'" } else { 'any host' }
    $bodyText = if ($via.body -match '<html|<!DOCTYPE') { Explain-Html $via.body } else { $via.body.Substring(0, [math]::Min(200, $via.body.Length)) }
    Log "Through IIS ($who): HTTP $($via.code), answered by '$($via.server)'"
    Log "    $bodyText"
    if ($via.code -ne 200) {
        if ($direct.code -ne 200) { Log 'The platform itself does not answer on its own address. Fix that first (Get-Service DeskAskPlatform, logs folder).' }
        elseif ("$($via.server)" -match 'IIS') { Log 'IIS answered by itself and did not pass the request on. Check: (1) the ARR proxy is switched on (IIS Manager > server > Application Request Routing Cache > Server Proxy Settings > Enable proxy), (2) the URL Rewrite module is installed, (3) no other IIS site matches this address first.' }
        else { Log 'The request reached the platform but it did not answer /health with 200.' }
        throw 'The check through IIS failed.'
    }
    Log 'Each customer now only needs a DNS record that points to this server. No IIS change per customer.'
}

$code = 0
try {
    switch ($spec.kind) {
        'install' {
            if ($spec.prepareDb) { Prepare-Database $spec }
            Step "Installing the platform into $($spec.root)"
            $p = @{ Root = $spec.root; Bundle = $spec.bundle; Updater = $spec.updater; PublicKey = $spec.publicKey; Urls = $spec.urls }
            if ($env:WIZ_CONN) { $p.CatalogConnection = $env:WIZ_CONN } else { Log 'No database given: customers cannot be created until the platform has a database. Run this page again with the database filled in.' }
            if ($env:WIZ_ENC) { $p.EncryptionSecret = $env:WIZ_ENC }
            & $spec.installer @p *>&1 | ForEach-Object { Log "$_" }
            if ($spec.iis) { Setup-Iis $spec }
            Step 'Finished. Next: the "Create customer" tab.'
        }
        'tenant' {
            Step ('Running: tenant ' + ($spec.args -join ' '))
            $env:DESKASK_CONFIG = $spec.config
            $ErrorActionPreference = 'Continue'
            & $spec.hostExe tenant @($spec.args) 2>&1 | ForEach-Object { Log "$_" }
            $code = $LASTEXITCODE
        }
    }
} catch { Log ("ERROR: " + $_.Exception.Message); $code = 1 }
Log "##EXIT $code"
exit $code
'@
Set-Content -Path (Join-Path $Work 'job.ps1') -Value $JobScript -Encoding UTF8

# ---------------------------------------------------------------- helpers
function Get-Prop($o, [string]$name, $default = '') {
    if ($null -ne $o -and $o.PSObject.Properties[$name] -and $null -ne $o.$name -and "$($o.$name)" -ne '') { return $o.$name }
    return $default
}
function Q([string]$v) { if ($v -match '[;="'' ]') { '"' + $v.Replace('"', '""') + '"' } else { $v } }

function Get-Install {
    $r = [ordered]@{ installed = $false; service = ''; root = ''; config = ''; urls = ''; hostExe = ''; health = $null }
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc) { return $r }
    $r.installed = $true; $r.service = "$($svc.Status)"
    $envList = @((Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name Environment -ErrorAction SilentlyContinue).Environment)
    foreach ($e in $envList) {
        if ($e -like 'DESKASK_CONFIG=*') { $r.config = $e.Substring(15) }
        if ($e -like 'ASPNETCORE_URLS=*') { $r.urls = $e.Substring(17) }
    }
    if ($r.config) { $r.root = Split-Path -Parent (Split-Path -Parent $r.config) }
    if ($r.root) {
        $cmd = Join-Path $r.root 'deskask-tenant.cmd'
        if (Test-Path $cmd) { $m = [regex]::Match((Get-Content -Raw $cmd), '"([^"]+)"\s+tenant'); if ($m.Success) { $r.hostExe = $m.Groups[1].Value } }
    }
    if ($r.urls -and $svc.Status -eq 'Running') {
        $u = ($r.urls -split ';')[0] -replace '//(\+|\*|0\.0\.0\.0)', '//localhost'
        if ($u -like 'http://*') {
            try { $r.health = (Invoke-WebRequest "$u/health" -UseBasicParsing -TimeoutSec 3).Content | ConvertFrom-Json } catch { }
        }
    }
    return $r
}

function Get-State {
    $i = Get-Install
    $drives = @([IO.DriveInfo]::GetDrives() | Where-Object { $_.DriveType -eq 'Fixed' -and $_.IsReady } | ForEach-Object {
            [ordered]@{ name = $_.Name; freeGB = [math]::Round($_.AvailableFreeSpace / 1GB, 1); system = ($_.Name -eq ($env:SystemDrive + '\')) } })
    $other = $drives | Where-Object { -not $_.system } | Select-Object -First 1
    $defRoot = if ($i.root) { $i.root } elseif ($other) { $other.name + 'DeskAsk' } else { 'C:\DeskAsk' }
    $bundle = Get-ChildItem $Here -Filter 'deskask-host-win-x64-*.dkb' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $bundle) { $bundle = Get-ChildItem $Here -Filter '*.dkb' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1 }
    $upd = Get-ChildItem $Here -Filter 'deskask-updater*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    $pub = Join-Path $Here 'release-public.pem'
    $certs = @(Get-ChildItem Cert:\LocalMachine\My -ErrorAction SilentlyContinue | Where-Object { $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } | ForEach-Object {
            [ordered]@{ subject = $_.Subject; thumbprint = $_.Thumbprint; until = $_.NotAfter.ToString('yyyy-MM-dd') } })
    $psql = $null
    $c = Get-Command psql.exe -ErrorAction SilentlyContinue
    if ($c) { $psql = $c.Source } else { $f = Get-ChildItem "$env:ProgramFiles\PostgreSQL\*\bin\psql.exe" -ErrorAction SilentlyContinue | Select-Object -First 1; if ($f) { $psql = $f.FullName } }
    $jobs = [ordered]@{ install = $null; tenant = $null }
    foreach ($j in $script:Jobs.Values) { if (-not $jobs[$j.group] -or $j.started -gt $script:Jobs[$jobs[$j.group]].started) { $jobs[$j.group] = $j.id } }
    return [ordered]@{
        computer = $env:COMPUTERNAME; install = $i; drives = $drives; defaultRoot = $defRoot
        files = [ordered]@{ bundle = $(if ($bundle) { $bundle.FullName } else { '' }); updater = $(if ($upd) { $upd.FullName } else { '' }); publicKey = $(if (Test-Path $pub) { $pub } else { '' }) }
        certs = $certs; psql = $psql; iis = [bool](Get-Service W3SVC -ErrorAction SilentlyContinue); jobs = $jobs; work = $Work
    }
}

function Invoke-Capture([string]$exe, [string[]]$argList, [hashtable]$envVars, [int]$timeoutMs = 20000) {
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = ($argList | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }) -join ' '
    $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.CreateNoWindow = $true
    foreach ($k in $envVars.Keys) { $psi.EnvironmentVariables[$k] = [string]$envVars[$k] }
    $p = [Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEndAsync(); $err = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit($timeoutMs)) { try { $p.Kill() } catch { }; return @{ code = -1; text = 'Timed out.' } }
    return @{ code = $p.ExitCode; text = (($out.Result + $err.Result).Trim()) }
}

function Start-SetupJob([string]$group, [hashtable]$spec, [hashtable]$envVars, [string[]]$secrets) {
    $id = [guid]::NewGuid().ToString('N').Substring(0, 8)
    $specFile = Join-Path $Work "$id.json"; $log = Join-Path $Work "$id.log"
    New-Item -ItemType File -Force -Path $log | Out-Null
    ($spec | ConvertTo-Json -Depth 6) | Set-Content -Path $specFile -Encoding UTF8
    foreach ($k in $envVars.Keys) { [Environment]::SetEnvironmentVariable($k, [string]$envVars[$k], 'Process') }
    try {
        $p = Start-Process -FilePath 'powershell.exe' -PassThru -WindowStyle Hidden -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + (Join-Path $Work 'job.ps1') + '"'), '-SpecFile', ('"' + $specFile + '"'), '-Log', ('"' + $log + '"'))
    } finally { foreach ($k in $envVars.Keys) { [Environment]::SetEnvironmentVariable($k, $null, 'Process') } }
    $null = $p.Handle     # without this, ExitCode can come back empty once the process has ended
    $script:Jobs[$id] = [pscustomobject]@{ id = $id; group = $group; log = $log; proc = $p; secrets = @($secrets | Where-Object { $_ }); started = Get-Date; saved = $false; root = $spec.root }
    return $id
}

function Read-JobLog($job, [int]$from) {
    $lines = @()
    try {
        $fs = New-Object IO.FileStream($job.log, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        $sr = New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8)
        $text = $sr.ReadToEnd(); $sr.Close(); $fs.Close()
        $lines = @($text -split "`r?`n" | Where-Object { $_ -ne '' })
    } catch { }
    $all = @($lines | Where-Object { $_ -notlike '##EXIT*' } | ForEach-Object { $l = $_; foreach ($s in $job.secrets) { $l = $l.Replace($s, '********') }; $l })
    return $all
}

# ---------------------------------------------------------------- the page
$Html = @'
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>DeskAsk Setup</title>
<style>
:root{--bg:#f4f6f8;--card:#fff;--ink:#1c2530;--mute:#667585;--line:#d9e0e7;--acc:#1763c6;--ok:#17803d;--bad:#c62828;--warn:#9a6700;--logbg:#0f1720;--logink:#d6e2ee}
@media (prefers-color-scheme:dark){:root{--bg:#10161d;--card:#18212b;--ink:#e6edf4;--mute:#93a3b4;--line:#2a3745;--acc:#5aa2ff;--ok:#4cc27a;--bad:#ff7b7b;--warn:#e3b341;--logbg:#0a0f15}}
*{box-sizing:border-box}body{margin:0;font:15px/1.5 system-ui,Segoe UI,sans-serif;background:var(--bg);color:var(--ink)}
header{display:flex;align-items:center;gap:16px;padding:14px 24px;border-bottom:1px solid var(--line);background:var(--card)}
header h1{font-size:18px;margin:0}header .sub{color:var(--mute);font-size:13px}header .sp{flex:1}
main{max-width:980px;margin:0 auto;padding:20px 16px 60px}
.tabs{display:flex;gap:6px;margin-bottom:14px}
.tab{padding:10px 16px;border:1px solid var(--line);border-radius:8px;background:var(--card);cursor:pointer;color:var(--mute);font:inherit}
.tab.on{color:var(--ink);border-color:var(--acc);box-shadow:inset 0 -3px 0 var(--acc);font-weight:600}
.card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:18px;margin-bottom:16px}
.card h2{margin:0 0 4px;font-size:16px}.hint{color:var(--mute);font-size:13px;margin:0 0 12px}
.grid{display:grid;grid-template-columns:1fr 1fr;gap:12px 16px}@media(max-width:700px){.grid{grid-template-columns:1fr}}
label{display:block;font-size:13px;font-weight:600;margin-bottom:3px}label span{font-weight:400;color:var(--mute)}
input[type=text],input[type=password],input[type=number],select{width:100%;padding:8px 10px;border:1px solid var(--line);border-radius:6px;background:var(--bg);color:var(--ink);font:inherit}
.full{grid-column:1/-1}.chips{display:flex;gap:6px;flex-wrap:wrap;margin-top:6px}
.chip{padding:3px 10px;border:1px solid var(--line);border-radius:999px;background:var(--bg);color:var(--ink);cursor:pointer;font-size:12px}
.chip:hover{border-color:var(--acc)}
button.go{background:var(--acc);color:#fff;border:0;border-radius:7px;padding:10px 20px;font:inherit;font-weight:600;cursor:pointer}
button.go:disabled{opacity:.5;cursor:default}button.sec{background:transparent;border:1px solid var(--line);color:var(--ink);border-radius:7px;padding:8px 14px;font:inherit;cursor:pointer}
.row{display:flex;gap:10px;align-items:center;flex-wrap:wrap}.sp{flex:1}
.pill{padding:2px 10px;border-radius:999px;font-size:12px;font-weight:600;border:1px solid var(--line);color:var(--mute)}
.pill.run{color:var(--acc);border-color:var(--acc)}.pill.ok{color:var(--ok);border-color:var(--ok)}.pill.bad{color:var(--bad);border-color:var(--bad)}
.log{background:var(--logbg);color:var(--logink);font:12.5px/1.55 Consolas,Menlo,monospace;border-radius:8px;padding:12px;height:300px;overflow:auto;white-space:pre-wrap;word-break:break-all}
.log .h{color:#6cc4ff;font-weight:700}.log .e{color:#ff8a8a}.log .w{color:#f0c36a}.log .o{color:#7fdc9a}
.note{padding:10px 12px;border-radius:7px;border:1px solid var(--line);font-size:13px;margin:10px 0}.note.warn{border-color:var(--warn);color:var(--warn)}.note.bad{border-color:var(--bad);color:var(--bad)}.note.ok{border-color:var(--ok);color:var(--ok)}
.pw{font:600 18px Consolas,monospace;letter-spacing:.5px;padding:8px 12px;background:var(--bg);border:1px dashed var(--ok);border-radius:6px;display:inline-block;user-select:all}
.opt{display:flex;gap:8px;align-items:flex-start;margin:8px 0}.opt input{margin-top:4px}.sect{display:none;margin:8px 0 0 24px;padding-left:12px;border-left:3px solid var(--line)}.sect.on{display:block}
.hid{display:none}
</style></head><body>
<header><h1>DeskAsk Setup</h1><span class="sub" id="where"></span><span class="sp"></span><span class="sub">Temporary page. It runs on this server only.</span><button class="sec" id="close">Close setup</button></header>
<main>
<div class="tabs"><button class="tab on" data-t="inst">1 · Install the platform <small>(once)</small></button><button class="tab" data-t="ten">2 · Create a customer <small>(each new customer)</small></button></div>

<section id="inst">
 <div class="card" id="instStatus"></div>
 <div class="card">
  <h2>Where and what</h2><p class="hint">Everything the platform keeps (program, settings, keys, data, logs) goes into one folder. Pick any drive.</p>
  <div class="grid">
   <div class="full"><label>Install folder</label><input type="text" id="root"><div class="chips" id="drives"></div></div>
   <div class="full"><label>Bundle <span>(deskask-host-win-x64-….dkb)</span></label><input type="text" id="bundle"></div>
   <div><label>Updater <span>(deskask-updater-windows-amd64.exe)</span></label><input type="text" id="updater"></div>
   <div><label>Public key <span>(release-public.pem)</span></label><input type="text" id="pub"></div>
   <div class="full"><label>The platform listens on <span>(keep this when IIS is the front door)</span></label><input type="text" id="urls" value="http://localhost:5000"></div>
  </div>
 </div>
 <div class="card">
  <h2>Database (PostgreSQL)</h2><p class="hint">The platform's own catalog of customers. Each customer later gets a database of its own.</p>
  <div class="grid">
   <div><label>Server</label><input type="text" id="dbHost" value="localhost"></div><div><label>Port</label><input type="number" id="dbPort" value="5432"></div>
   <div><label>Catalog database</label><input type="text" id="dbName" value="deskask_catalog"></div><div><label>Login</label><input type="text" id="dbUser" value="deskask"></div>
   <div class="full"><label>Password</label><input type="password" id="dbPw" autocomplete="new-password"></div>
  </div>
  <div class="opt"><input type="checkbox" id="prep"><div><label for="prep" style="margin:0">Create this login and database for me</label><span class="hint">Needs the PostgreSQL administrator. If the login exists, its password is set to the one above.</span></div></div>
  <div class="sect" id="prepBox"><div class="grid"><div><label>PostgreSQL administrator</label><input type="text" id="pgUser" value="postgres"></div><div><label>Administrator password</label><input type="password" id="pgPw" autocomplete="new-password"></div></div></div>
  <div class="row" style="margin-top:10px"><button class="sec" id="testDb">Test connection</button><span id="dbRes" class="hint" style="margin:0"></span></div>
  <details style="margin-top:10px"><summary class="hint" style="cursor:pointer">Advanced</summary>
   <div class="grid" style="margin-top:8px"><div class="full"><label>Or paste a full connection string <span>(overrides the fields above)</span></label><input type="text" id="raw"></div>
   <div class="full"><label>Encryption secret <span>(leave empty: one is made and kept in the settings file)</span></label><input type="password" id="enc" autocomplete="new-password"></div></div></details>
 </div>
 <div class="card">
  <h2>IIS as the front door <span class="hint" style="margin:0">(optional)</span></h2>
  <div class="opt"><input type="checkbox" id="iis"><div><label for="iis" style="margin:0">Publish through IIS</label><span class="hint">One IIS site that passes every address on to the platform. A new customer then only needs a DNS record. Needs the IIS URL Rewrite and Application Request Routing modules.</span></div></div>
  <div class="sect" id="iisBox"><div class="grid">
   <div><label>Site name</label><input type="text" id="iisSite" value="DeskAsk"></div>
   <div><label>Host names <span>(empty = any name; or list names separated by spaces)</span></label><input type="text" id="iisHosts" placeholder="empty = any name"></div>
   <div><label>HTTP port</label><input type="number" id="httpPort" value="80"></div><div><label>HTTPS port</label><input type="number" id="httpsPort" value="443"></div>
   <div class="full"><label>Certificate <span>(from this server's certificate store; sign-in needs https)</span></label><select id="cert"><option value="">None, http only</option></select></div>
  </div></div>
 </div>
 <div class="row" style="margin-bottom:12px"><button class="go" id="goInst">Install / update</button><span class="pill" id="pInst">Not started</span><span class="hint" id="tInst" style="margin:0"></span><span class="sp"></span><button class="sec" data-copy="inst">Copy log</button></div>
 <div class="log" id="logInst"></div>
</section>

<section id="ten" class="hid">
 <div class="card" id="tenStatus"></div>
 <div class="card">
  <h2>New customer</h2><p class="hint">Creates the customer's database, prepares every module in it and makes the first administrator.</p>
  <div class="grid">
   <div><label>Customer name</label><input type="text" id="tName" placeholder="Damaze Tech"></div>
   <div><label>Short id <span>(letters, digits, - ; also names the database)</span></label><input type="text" id="tId" placeholder="damazetech"></div>
   <div><label>Base domain <span>(remembered)</span></label><input type="text" id="tBase" placeholder="deskask.in"></div>
   <div><label>Address</label><input type="text" id="tHost" placeholder="damazetech.deskask.in"></div>
   <div><label>Administrator's email</label><input type="text" id="tMail" placeholder="admin@customer.com"></div>
   <div><label>Administrator's password <span>(empty: one is made and shown once)</span></label><input type="password" id="tPw" autocomplete="new-password"></div>
  </div>
  <div class="row" style="margin-top:14px"><button class="go" id="goTen">Create customer</button><span class="pill" id="pTen">Not started</span><span class="hint" id="tTen" style="margin:0"></span><span class="sp"></span><button class="sec" data-copy="ten">Copy log</button></div>
 </div>
 <div id="result"></div>
 <div class="log" id="logTen"></div>
 <div class="card" style="margin-top:16px">
  <h2>Customers already here</h2><p class="hint">List them, or give an administrator a new password (when the first one was lost).</p>
  <div class="row"><button class="sec" id="listTen">List customers</button><span class="sp"></span>
   <input type="text" id="rId" placeholder="customer id" style="width:160px"><input type="text" id="rMail" placeholder="admin email (optional)" style="width:200px"><button class="sec" id="resetTen">New admin password</button></div>
 </div>
</section>
</main>
<script>
const T=new URLSearchParams(location.search).get('t')||'';
const $=id=>document.getElementById(id);
let ST=null;
async function api(path,body){
  const r=await fetch('/api/'+path,{method:body?'POST':'GET',headers:{'X-Token':T,'Content-Type':'application/json'},body:body?JSON.stringify(body):undefined});
  const j=await r.json().catch(()=>({error:'Bad answer from the setup page.'}));
  if(!r.ok) throw new Error(j.error||('Error '+r.status));
  return j;
}
document.querySelectorAll('.tab').forEach(b=>b.onclick=()=>{
  document.querySelectorAll('.tab').forEach(x=>x.classList.toggle('on',x===b));
  $('inst').classList.toggle('hid',b.dataset.t!=='inst');$('ten').classList.toggle('hid',b.dataset.t!=='ten');
});
$('prep').onchange=()=>$('prepBox').classList.toggle('on',$('prep').checked);
$('iis').onchange=()=>$('iisBox').classList.toggle('on',$('iis').checked);
function note(el,cls,html){el.innerHTML='<div class="note '+cls+'">'+html+'</div>';}
function esc(s){return String(s).replace(/[&<>]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;'}[c]));}

// ------------------------------------------------------------ log view
const views={};
function view(key,logEl,pill,timeEl){return views[key]={logEl,pill,timeEl,job:null,from:0,timer:null,lines:[],start:0};}
function paint(v,lines){
  for(const l of lines){
    const d=document.createElement('div');d.textContent=l;
    if(/^==>/.test(l))d.className='h';else if(/^ERROR|failed|Failed|Error:|cannot|FAILED/.test(l))d.className='e';else if(/warn|No database|only http/i.test(l))d.className='w';else if(/ready|created|OK\b|Finished|answers/i.test(l))d.className='o';
    v.logEl.appendChild(d);v.lines.push(l);
  }
  if(lines.length)v.logEl.scrollTop=v.logEl.scrollHeight;
}
function follow(key,id,onDone,resume){
  const v=views[key];clearInterval(v.timer);v.job=id;v.from=0;v.lines=[];v.logEl.textContent='';v.start=Date.now();
  v.pill.className='pill run';v.pill.textContent='Running';sessionStorage.setItem('job-'+key,id);
  const tick=async()=>{
    try{
      const j=await api('job?id='+id+'&from='+v.from);
      const ls=Array.isArray(j.lines)?j.lines:(j.lines?[String(j.lines)]:[]);paint(v,ls);v.from=j.next;
      const s=Math.round((Date.now()-v.start)/1000);v.timeEl.textContent=(j.done?'took ':'')+Math.floor(s/60)+':'+String(s%60).padStart(2,'0');
      if(j.done){clearInterval(v.timer);v.pill.className='pill '+(j.exit===0?'ok':'bad');v.pill.textContent=j.exit===0?'Done':'Failed';onDone&&onDone(j.exit,v.lines);refresh();}
    }catch(e){clearInterval(v.timer);v.pill.className='pill bad';v.pill.textContent='Lost';paint(v,['The setup page stopped answering: '+e.message]);}
  };
  v.timer=setInterval(tick,700);tick();
}
document.querySelectorAll('[data-copy]').forEach(b=>b.onclick=()=>{
  const v=views[b.dataset.copy];navigator.clipboard.writeText(v.lines.join('\n')).then(()=>{b.textContent='Copied';setTimeout(()=>b.textContent='Copy log',1500);});
});
view('inst',$('logInst'),$('pInst'),$('tInst'));view('ten',$('logTen'),$('pTen'),$('tTen'));

// ------------------------------------------------------------ state
async function refresh(){
  ST=await api('state');
  $('where').textContent='on '+ST.computer;
  const i=ST.install;
  if(i.installed){
    const h=i.health?('answering, version '+esc(i.health.version||'?')):(i.service==='Running'?'running, not answering yet':'service '+esc(i.service));
    note($('instStatus'),i.health?'ok':'warn','<b>Installed</b> in <code>'+esc(i.root)+'</code> · '+h+'. Running this page again with a newer bundle updates it and keeps the settings.');
    note($('tenStatus'),i.health?'ok':'warn',i.health?'Platform is installed and answering. Customers can be created.':'The platform is installed but is not answering. Check the service first.');
  }else{
    note($('instStatus'),'warn','<b>Not installed yet.</b> Fill in the three blocks below and press Install.');
    note($('tenStatus'),'bad','The platform is not installed. Do tab 1 first.');
  }
  $('goTen').disabled=!i.installed;$('listTen').disabled=!i.installed;$('resetTen').disabled=!i.installed;
  if(!$('root').dataset.set){$('root').value=ST.defaultRoot;$('root').dataset.set=1;
    $('bundle').value=ST.files.bundle;$('updater').value=ST.files.updater;$('pub').value=ST.files.publicKey;
    if(i.installed){$('root').readOnly=true;if(i.urls)$('urls').value=i.urls;}
    $('drives').innerHTML='';ST.drives.forEach(d=>{const c=document.createElement('button');c.type='button';c.className='chip';
      c.textContent=d.name+'DeskAsk · '+d.freeGB+' GB free'+(d.system?' · system drive':'');c.onclick=()=>{if(!$('root').readOnly)$('root').value=d.name+'DeskAsk';};$('drives').appendChild(c);});
    ST.certs.forEach(c=>{const o=document.createElement('option');o.value=c.thumbprint;o.textContent=c.subject+' · until '+c.until;$('cert').appendChild(o);});
  }
  if(!ST.psql)$('prep').title='psql.exe was not found on this server';
  if(ST.jobs.install&&!views.inst.job)follow('inst',ST.jobs.install);
  if(ST.jobs.tenant&&!views.ten.job)follow('ten',ST.jobs.tenant);
}

// ------------------------------------------------------------ install
function dbBody(){return{host:$('dbHost').value.trim(),port:$('dbPort').value.trim(),db:$('dbName').value.trim(),user:$('dbUser').value.trim(),password:$('dbPw').value,raw:$('raw').value.trim()};}
$('testDb').onclick=async()=>{
  $('dbRes').textContent='Testing…';
  try{const j=await api('test-db',dbBody());$('dbRes').style.color=j.ok?'var(--ok)':'var(--bad)';$('dbRes').textContent=j.message;}
  catch(e){$('dbRes').style.color='var(--bad)';$('dbRes').textContent=e.message;}
};
$('goInst').onclick=async()=>{
  const d=dbBody();
  if(!d.raw&&(!d.host||!d.db||!d.user||!d.password)&&!confirm('No database is filled in. The platform can be installed, but no customer can be created until a database is set. Continue?'))return;
  const hosts=$('iisHosts').value.split(/[\s,;]+/).filter(Boolean);
  const body={root:$('root').value.trim(),bundle:$('bundle').value.trim(),updater:$('updater').value.trim(),publicKey:$('pub').value.trim(),urls:$('urls').value.trim(),
    db:d,prepareDb:$('prep').checked,pgUser:$('pgUser').value.trim(),pgPassword:$('pgPw').value,encryptionSecret:$('enc').value,
    iis:$('iis').checked,iisSite:$('iisSite').value.trim(),iisHosts:hosts,httpPort:+$('httpPort').value,httpsPort:+$('httpsPort').value,certThumbprint:$('cert').value};
  $('goInst').disabled=true;
  try{const j=await api('install',body);follow('inst',j.job,()=>{$('goInst').disabled=false;});}
  catch(e){$('goInst').disabled=false;views.inst.logEl.textContent='';paint(views.inst,['ERROR: '+e.message]);views.inst.pill.className='pill bad';views.inst.pill.textContent='Not started';}
};

// ------------------------------------------------------------ customers
$('tBase').value=localStorage.getItem('base')||'';
const slug=s=>s.toLowerCase().replace(/[^a-z0-9]+/g,'-').replace(/^-+|-+$/g,'').replace(/^[^a-z]+/,'').slice(0,30);
let idTouched=false,hostTouched=false;
$('tId').oninput=()=>{idTouched=true;fill();};$('tHost').oninput=()=>{hostTouched=true;};
$('tName').oninput=()=>{if(!idTouched)$('tId').value=slug($('tName').value);fill();};
$('tBase').oninput=()=>{localStorage.setItem('base',$('tBase').value.trim());fill();};
function fill(){if(!hostTouched)$('tHost').value=$('tId').value&&$('tBase').value?$('tId').value+'.'+$('tBase').value.trim():'';}
function result(lines,code){
  const out=$('result');out.innerHTML='';
  const pw=lines.map(l=>/Password: (\S+)/.exec(l)).filter(Boolean).pop();
  const done=lines.find(l=>/^Customer '.+' created|^New password set/.test(l));
  if(code===0&&done){
    const d=document.createElement('div');d.className='card';
    d.innerHTML='<h2>'+esc(done)+'</h2>'+(pw?'<p class="hint">Write this password down now. It is shown once.</p><div class="pw">'+esc(pw[1])+'</div> <button class="sec" id="cp">Copy</button>':'<p class="hint">The password is the one you typed.</p>');
    out.appendChild(d);if(pw)$('cp').onclick=()=>navigator.clipboard.writeText(pw[1]);
  }
}
async function tenant(action,extra){
  views.ten.logEl.textContent='';$('result').innerHTML='';
  try{const j=await api('tenant',Object.assign({action},extra));follow('ten',j.job,(code,lines)=>{$('goTen').disabled=!ST.install.installed;result(lines,code);});}
  catch(e){paint(views.ten,['ERROR: '+e.message]);views.ten.pill.className='pill bad';views.ten.pill.textContent='Not started';}
}
$('goTen').onclick=()=>tenant('create',{id:$('tId').value.trim(),name:$('tName').value.trim(),host:$('tHost').value.trim(),email:$('tMail').value.trim(),password:$('tPw').value});
$('listTen').onclick=()=>tenant('list',{});
$('resetTen').onclick=()=>tenant('reset-admin',{id:$('rId').value.trim(),email:$('rMail').value.trim()});
$('close').onclick=async()=>{if(!confirm('Close the setup page? The platform keeps running.'))return;try{await api('shutdown',{});}catch(e){}document.body.innerHTML='<main><div class="card"><h2>Setup closed</h2><p class="hint">You can close this tab. The platform keeps running as a service.</p></div></main>';};
refresh().catch(e=>note($('instStatus'),'bad',esc(e.message)));
</script></body></html>
'@

# ---------------------------------------------------------------- the web server (this machine only)
function Send($ctx, [int]$status, [string]$type, [string]$text) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($text)
    $ctx.Response.StatusCode = $status; $ctx.Response.ContentType = $type
    $ctx.Response.Headers.Add('Cache-Control', 'no-store')
    $ctx.Response.ContentLength64 = $bytes.Length
    $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length); $ctx.Response.OutputStream.Close()
}
function Send-Json($ctx, [int]$status, $obj) { Send $ctx $status 'application/json; charset=utf-8' (ConvertTo-Json -InputObject $obj -Depth 8 -Compress) }
function Bad([string]$m) { throw [System.ArgumentException]::new($m) }

function Handle-Install($b) {
    foreach ($j in $script:Jobs.Values) { if ($j.group -eq 'install' -and -not $j.proc.HasExited) { Bad 'An installation is already running.' } }
    $root = [string](Get-Prop $b 'root')
    if ($root -notmatch '^[A-Za-z]:\\[^"<>|*?]*$') { Bad 'Install folder: give a full path such as D:\DeskAsk.' }
    foreach ($f in @(@('bundle', 'Bundle'), @('updater', 'Updater'), @('publicKey', 'Public key'))) {
        $v = [string](Get-Prop $b $f[0]); if (-not $v -or -not (Test-Path -LiteralPath $v -PathType Leaf)) { Bad "$($f[1]): file not found." }
    }
    $urls = [string](Get-Prop $b 'urls' 'http://localhost:5000')
    if ($urls -notmatch '^https?://[^\s;]+(;https?://[^\s;]+)*$') { Bad 'The platform address must look like http://localhost:5000.' }
    $db = Get-Prop $b 'db' $null
    $conn = ''
    if ($db) { $conn = Conn-String $db }
    $prep = [bool](Get-Prop $b 'prepareDb' $false)
    $spec = @{ kind = 'install'; installer = $Installer; root = $root; bundle = [string]$b.bundle; updater = [string]$b.updater; publicKey = [string]$b.publicKey; urls = $urls; prepareDb = $prep }
    if ($prep) {
        if (-not $conn -or $db.raw) { Bad 'To create the login and database, fill in the server, database, login and password fields (not a pasted string).' }
        $spec.dbHost = [string]$db.host; $spec.dbPort = [string]$db.port; $spec.dbName = [string]$db.db; $spec.dbUser = [string]$db.user; $spec.pgUser = [string](Get-Prop $b 'pgUser' 'postgres')
        if ($spec.pgUser -notmatch '^[A-Za-z0-9_.-]+$') { Bad 'PostgreSQL administrator: letters, digits, . _ - only.' }
        if (-not (Get-Prop $b 'pgPassword')) { Bad 'PostgreSQL administrator password is needed.' }
    }
    if ([bool](Get-Prop $b 'iis' $false)) {
        $spec.iis = $true; $spec.iisSite = [string](Get-Prop $b 'iisSite' 'DeskAsk')
        if ($spec.iisSite -notmatch '^[A-Za-z0-9 _.-]+$') { Bad 'IIS site name: letters, digits, space . _ - only.' }
        $spec.iisHosts = @((Get-Prop $b 'iisHosts' @()) | ForEach-Object { [string]$_ })
        foreach ($h in $spec.iisHosts) { if ($h -notmatch '^[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?$') { Bad "IIS host name '$h' is not valid." } }
        $spec.httpPort = [int](Get-Prop $b 'httpPort' 80); $spec.httpsPort = [int](Get-Prop $b 'httpsPort' 443)
        $spec.certThumbprint = [string](Get-Prop $b 'certThumbprint' '')
        if ($spec.certThumbprint -and $spec.certThumbprint -notmatch '^[A-Fa-f0-9]{40}$') { Bad 'Certificate thumbprint is not valid.' }
    }
    $pw = if ($db) { [string](Get-Prop $db 'password') } else { '' }
    $envs = @{ WIZ_CONN = $conn; WIZ_ENC = [string](Get-Prop $b 'encryptionSecret'); WIZ_PGPW = [string](Get-Prop $b 'pgPassword'); WIZ_DBPW = $pw }
    return Start-SetupJob 'install' $spec $envs @($pw, [string](Get-Prop $b 'pgPassword'), [string](Get-Prop $b 'encryptionSecret'), $(if ($conn) { $conn }))
}

function Conn-String($db) {
    $raw = [string](Get-Prop $db 'raw')
    if ($raw) { return $raw.Trim() }
    $h = [string](Get-Prop $db 'host'); $p = [string](Get-Prop $db 'port' '5432'); $n = [string](Get-Prop $db 'db'); $u = [string](Get-Prop $db 'user'); $w = [string](Get-Prop $db 'password')
    if (-not $h -and -not $n -and -not $u -and -not $w) { return '' }
    if ($h -notmatch '^[A-Za-z0-9._-]+$') { Bad 'Database server: letters, digits, . _ - only.' }
    if ($p -notmatch '^\d{1,5}$') { Bad 'Database port must be a number.' }
    if ($n -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') { Bad 'Database name: letters, digits and _ only.' }
    if ($u -notmatch '^[A-Za-z_][A-Za-z0-9_.-]*$') { Bad 'Database login: letters, digits, . _ - only.' }
    if (-not $w) { Bad 'Database password is needed.' }
    return "Host=$h;Port=$p;Database=$n;Username=$u;Password=$(Q $w)"
}

function Handle-TestDb($b) {
    if (Get-Prop $b 'raw') { return @{ ok = $true; message = 'A pasted connection string is checked when the first customer is created.' } }
    $conn = Conn-String $b
    if (-not $conn) { Bad 'Fill in the database fields first.' }
    $psql = (Get-State).psql
    if ($psql) {
        $r = Invoke-Capture $psql @('-w', '-h', [string]$b.host, '-p', [string]$b.port, '-U', [string]$b.user, '-d', [string]$b.db, '-tAc', 'select 1') @{ PGPASSWORD = [string]$b.password; PGCONNECT_TIMEOUT = '8' }
        if ($r.code -eq 0) { return @{ ok = $true; message = 'Connected.' } }
        $msg = ($r.text -split "`r?`n")[0]
        if ($msg -match 'does not exist' -or $msg -match 'authentication failed') { $msg += '  (Tick "Create this login and database for me" if they are not made yet.)' }
        return @{ ok = $false; message = $msg }
    }
    $tcp = New-Object Net.Sockets.TcpClient
    try { $iar = $tcp.BeginConnect([string]$b.host, [int]$b.port, $null, $null); if ($iar.AsyncWaitHandle.WaitOne(4000) -and $tcp.Connected) { return @{ ok = $true; message = 'The server answers on that port. (psql.exe was not found, so the login was not tested.)' } } }
    catch { } finally { $tcp.Close() }
    return @{ ok = $false; message = 'Nothing answers on that server and port.' }
}

function Handle-Tenant($b) {
    $i = Get-Install
    if (-not $i.installed -or -not $i.hostExe -or -not (Test-Path -LiteralPath $i.hostExe)) { Bad 'The platform is not installed yet (tab 1).' }
    foreach ($j in $script:Jobs.Values) { if ($j.group -eq 'tenant' -and -not $j.proc.HasExited) { Bad 'Another customer command is still running.' } }
    $action = [string](Get-Prop $b 'action'); $envs = @{}; $secrets = @(); $tArgs = @()
    switch ($action) {
        'list' { $tArgs = @('list') }
        'create' {
            $id = ([string](Get-Prop $b 'id')).ToLowerInvariant(); $hostName = ([string](Get-Prop $b 'host')).ToLowerInvariant(); $mail = [string](Get-Prop $b 'email'); $name = ([string](Get-Prop $b 'name')) -replace '["%^&|<>]', ''
            if ($id -notmatch '^[a-z][a-z0-9-]{1,30}$') { Bad 'Short id: 2 to 31 characters, lower case letters, digits and -, starting with a letter.' }
            if ($hostName -notmatch '^[a-z0-9]([a-z0-9.-]*[a-z0-9])?$') { Bad 'Address is not valid (for example acme.deskask.in).' }
            if ($mail -notmatch '^[^@\s]+@[^@\s]+\.[^@\s]+$') { Bad "Administrator's email is not valid." }
            $tArgs = @('create', '--id', $id, '--host', $hostName, '--admin-email', $mail)
            if ($name.Trim()) { $tArgs += @('--name', $name.Trim()) }
            $pw = [string](Get-Prop $b 'password')
            if ($pw) {
                if ($pw.Length -lt 10) { Bad 'Administrator password: at least 10 characters (or leave it empty to have one made).' }
                $envs.WIZ_ADMINPW = $pw; $secrets += $pw; $tArgs += @('--admin-password-env', 'WIZ_ADMINPW')
            }
        }
        'reset-admin' {
            $id = ([string](Get-Prop $b 'id')).ToLowerInvariant(); $mail = [string](Get-Prop $b 'email')
            if ($id -notmatch '^[a-z][a-z0-9-]{1,30}$') { Bad 'Give the customer id.' }
            $tArgs = @('reset-admin', '--id', $id)
            if ($mail) { if ($mail -notmatch '^[^@\s]+@[^@\s]+\.[^@\s]+$') { Bad 'Email is not valid.' }; $tArgs += @('--email', $mail) }
        }
        default { Bad 'Unknown action.' }
    }
    $spec = @{ kind = 'tenant'; hostExe = $i.hostExe; config = $i.config; args = $tArgs; root = $i.root }
    return Start-SetupJob 'tenant' $spec $envs $secrets
}

function Route($ctx) {
    $req = $ctx.Request; $path = $req.Url.AbsolutePath
    if ($req.Url.Host -notin @('127.0.0.1', 'localhost')) { Send $ctx 403 'text/plain' 'Forbidden'; return }
    if ($path -eq '/') {
        if ($req.QueryString['t'] -ne $Token) { Send $ctx 403 'text/plain' 'Open the address printed in the PowerShell window (it ends with ?t=...).'; return }
        Send $ctx 200 'text/html; charset=utf-8' $Html; return
    }
    if (-not $path.StartsWith('/api/') -or $req.Headers['X-Token'] -ne $Token) { Send $ctx 403 'text/plain' 'Forbidden'; return }
    $body = $null
    if ($req.HttpMethod -eq 'POST') {
        $raw = (New-Object IO.StreamReader($req.InputStream, [Text.Encoding]::UTF8)).ReadToEnd()
        if ($raw) { $body = $raw | ConvertFrom-Json }
    }
    try {
        switch ($path) {
            '/api/state' { Send-Json $ctx 200 (Get-State) }
            '/api/test-db' { Send-Json $ctx 200 (Handle-TestDb $body) }
            '/api/install' { Send-Json $ctx 200 @{ job = (Handle-Install $body) } }
            '/api/tenant' { Send-Json $ctx 200 @{ job = (Handle-Tenant $body) } }
            '/api/job' {
                $job = $script:Jobs[$req.QueryString['id']]
                if (-not $job) { Send-Json $ctx 404 @{ error = 'Unknown job.' }; return }
                $from = [int]$req.QueryString['from']
                $all = @(Read-JobLog $job 0)
                $done = $job.proc.HasExited
                if ($done) { Start-Sleep -Milliseconds 150; $all = @(Read-JobLog $job 0) }     # the last lines are written just before the process ends
                $new = @(if ($from -lt $all.Count) { $all[$from..($all.Count - 1)] })
                if ($done -and $job.group -eq 'install' -and -not $job.saved -and $job.root -and (Test-Path (Join-Path $job.root 'logs'))) {
                    $job.saved = $true
                    Set-Content -Path (Join-Path $job.root ('logs\setup-install-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')) -Value $all -Encoding UTF8
                }
                Send-Json $ctx 200 @{ lines = [string[]]$new; next = $all.Count; done = $done; exit = $(if ($done) { $job.proc.ExitCode } else { $null }) }
            }
            '/api/shutdown' { $script:Stop = $true; Send-Json $ctx 200 @{ ok = $true } }
            default { Send-Json $ctx 404 @{ error = 'Not found.' } }
        }
    } catch [System.ArgumentException] { Send-Json $ctx 400 @{ error = $_.Exception.Message } }
}

$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
try { $listener.Start() } catch { throw "Port $Port is in use. Start again with -Port <another number>. ($($_.Exception.Message))" }
$url = "http://127.0.0.1:$Port/?t=$Token"
Write-Host ''
Write-Host 'DeskAsk Setup is open.' -ForegroundColor Cyan
Write-Host "  $url"
Write-Host '  It listens on this server only. Press Ctrl+C here, or use "Close setup" in the page, to stop it.'
if (-not $NoBrowser) { try { Start-Process $url } catch { Write-Host '  (The browser did not open by itself; open the address above.)' } }
try {
    while (-not $script:Stop) {
        $iar = $listener.BeginGetContext($null, $null)
        while (-not $iar.AsyncWaitHandle.WaitOne(300)) { if ($script:Stop) { break } }
        if (-not $iar.IsCompleted) { break }
        $ctx = $listener.EndGetContext($iar)
        try { Route $ctx } catch {
            try { Send-Json $ctx 500 @{ error = $_.Exception.Message } } catch { }
        }
    }
} finally {
    $listener.Stop(); $listener.Close()
    foreach ($j in $script:Jobs.Values) { if (-not $j.proc.HasExited) { Write-Host "A job is still running in the background (log: $($j.log))." } }
    if (-not ($script:Jobs.Values | Where-Object { -not $_.proc.HasExited })) { Remove-Item -Recurse -Force $Work -ErrorAction SilentlyContinue }
    Write-Host 'Setup closed.'
}
