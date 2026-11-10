#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Installs or updates DeskAsk Platform on Windows Server: the platform as a Windows service, and the updater as a startup task.
.DESCRIPTION
  Needs: a signed bundle (deskask-host-win-x64-X.dkb), deskask-updater.exe and release-public.pem, all from the same GitHub release.
  Safe to run again: it keeps your configuration and secrets, and applies the bundle if it is newer than what is installed.
  See docs/windows-install.md.
.EXAMPLE
  .\install-windows.ps1 -Bundle .\deskask-host-win-x64-0.1.0.dkb -Updater .\deskask-updater-windows-amd64.exe -PublicKey .\release-public.pem `
      -CatalogConnection "Host=localhost;Database=deskask_catalog;Username=deskask;Password=..."
#>
[CmdletBinding()]
param(
    [string]$Root = 'C:\DeskAsk',
    [string]$Bundle,
    [string]$Updater,
    [string]$PublicKey,
    [string]$Urls = 'http://localhost:5000',
    [string]$CatalogConnection = '',
    [string]$EncryptionSecret = '',
    [string]$PlatformService = 'DeskAskPlatform',
    [string]$UpdaterTask = 'DeskAskUpdater',
    [switch]$Uninstall,
    [switch]$RemoveFiles
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

# well-known accounts by SID, so this works on any language of Windows
$System = '*S-1-5-18'; $Admins = '*S-1-5-32-544'; $LocalService = '*S-1-5-19'
function Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Run-Native([string]$exe, [string[]]$argList) {
    & $exe @argList
    if ($LASTEXITCODE -ne 0) { throw "$exe $($argList -join ' ') failed with exit code $LASTEXITCODE" }
}
function Stop-UpdaterTask {
    if (Get-ScheduledTask -TaskName $UpdaterTask -ErrorAction SilentlyContinue) { Stop-ScheduledTask -TaskName $UpdaterTask -ErrorAction SilentlyContinue }
    Get-Process -Name 'deskask-updater' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

if ($Uninstall) {
    Step 'Removing the services (data and configuration are kept unless -RemoveFiles is given)'
    Stop-UpdaterTask
    if (Get-ScheduledTask -TaskName $UpdaterTask -ErrorAction SilentlyContinue) { Unregister-ScheduledTask -TaskName $UpdaterTask -Confirm:$false }
    if (Get-Service -Name $PlatformService -ErrorAction SilentlyContinue) {
        Stop-Service -Name $PlatformService -Force -ErrorAction SilentlyContinue
        Run-Native 'sc.exe' @('delete', $PlatformService)
    }
    if ($RemoveFiles -and (Test-Path $Root)) { Remove-Item -Recurse -Force $Root }
    Write-Host 'Removed.'
    return
}

foreach ($p in @('Bundle', 'Updater', 'PublicKey')) {
    $v = Get-Variable -Name $p -ValueOnly
    if (-not $v -or -not (Test-Path $v)) { throw "-$p is needed and must be an existing file." }
}
$Bundle = (Resolve-Path $Bundle).Path; $Updater = (Resolve-Path $Updater).Path; $PublicKey = (Resolve-Path $PublicKey).Path

# --- folders
Step "Preparing $Root"
Stop-UpdaterTask
foreach ($d in @('', 'inbox', 'state', 'logs', 'config', 'data', 'data\keys')) { New-Item -ItemType Directory -Force -Path (Join-Path $Root $d) | Out-Null }
Copy-Item -Force $Updater (Join-Path $Root 'deskask-updater.exe')
Copy-Item -Force $PublicKey (Join-Path $Root 'release-public.pem')

# --- who may do what: administrators and the updater (SYSTEM) change everything; the platform (LocalService) only reads,
#     and writes just to inbox (uploads), data (keys). It cannot change live, previous or the release public key.
Step 'Setting folder permissions'
Run-Native 'icacls.exe' @($Root, '/inheritance:r', '/grant:r', "${System}:(OI)(CI)F", "${Admins}:(OI)(CI)F", "${LocalService}:(OI)(CI)RX")
foreach ($w in @('inbox', 'data')) { Run-Native 'icacls.exe' @((Join-Path $Root $w), '/grant', "${LocalService}:(OI)(CI)M") }

# --- configuration (kept on re-runs, so secrets stay)
$cfgPath = Join-Path $Root 'config\deskask.json'
if (-not (Test-Path $cfgPath)) {
    Step 'Writing configuration with a new encryption secret'
    if (-not $EncryptionSecret) {
        $b = New-Object byte[] 48; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); $EncryptionSecret = [Convert]::ToBase64String($b)
    }
    $cfg = [ordered]@{
        Identity = [ordered]@{ EncryptionSecret = $EncryptionSecret }
        Platform = [ordered]@{ DataProtectionPath = (Join-Path $Root 'data\keys') }
        Updates  = [ordered]@{ Dir = $Root }
    }
    if ($CatalogConnection) { $cfg.Platform.CatalogConnection = $CatalogConnection }
    [IO.File]::WriteAllText($cfgPath, ($cfg | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding $false))
    Write-Host "  BACK UP $cfgPath : it holds the encryption secret. If it is lost, two-step sign-in secrets and signing keys cannot be read again."
}
elseif ($CatalogConnection) {
    Step 'Updating the catalog connection in the existing configuration'
    $c = Get-Content $cfgPath -Raw | ConvertFrom-Json
    if (-not ($c.PSObject.Properties.Name -contains 'Platform')) { $c | Add-Member -NotePropertyName Platform -NotePropertyValue ([pscustomobject]@{}) }
    $c.Platform | Add-Member -NotePropertyName CatalogConnection -NotePropertyValue $CatalogConnection -Force
    [IO.File]::WriteAllText($cfgPath, ($c | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding $false))
}

# --- the platform service
$exe = Join-Path $Root 'live\DeskAsk.Platform.Host.exe'
Step "Creating the Windows service $PlatformService"
if (-not (Get-Service -Name $PlatformService -ErrorAction SilentlyContinue)) {
    New-Service -Name $PlatformService -BinaryPathName "`"$exe`"" -DisplayName 'DeskAsk Platform' -StartupType Automatic `
        -Description 'DeskAsk Platform: help desk, service desk, assets, monitoring and more.' | Out-Null
}
Run-Native 'sc.exe' @('config', $PlatformService, 'obj=', 'NT AUTHORITY\LocalService')
Run-Native 'sc.exe' @('failure', $PlatformService, 'reset=', '86400', 'actions=', 'restart/5000/restart/5000/restart/30000')
# settings of this service only (not the whole machine). The secrets are in the configuration file, which only administrators and the service can read.
$envValues = @("DESKASK_CONFIG=$cfgPath", "ASPNETCORE_URLS=$Urls", 'ASPNETCORE_ENVIRONMENT=Production', 'DOTNET_ENVIRONMENT=Production')
New-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$PlatformService" -Name Environment -PropertyType MultiString -Value $envValues -Force | Out-Null

$listen = [uri](($Urls -split ';')[0] -replace '//[+*]', '//localhost')
if ($listen.Host -notin @('localhost', '127.0.0.1', '::1', '[::1]')) {
    Step "Opening port $($listen.Port) in the firewall"
    if (-not (Get-NetFirewallRule -DisplayName "DeskAsk Platform $($listen.Port)" -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName "DeskAsk Platform $($listen.Port)" -Direction Inbound -Protocol TCP -LocalPort $listen.Port -Action Allow | Out-Null
    }
}
$health = "http://localhost:$($listen.Port)/health"

# --- the updater: starts with the machine, watches the inbox, swaps versions, rolls back on a failed start
$stopCmd = "powershell -NoProfile -Command Stop-Service -Name $PlatformService -ErrorAction SilentlyContinue"
$startCmd = "powershell -NoProfile -Command Start-Service -Name $PlatformService -ErrorAction Stop"
$updaterArgs = "-root `"$Root`" -pub `"$(Join-Path $Root 'release-public.pem')`" -stop-cmd `"$stopCmd`" -start-cmd `"$startCmd`" -health `"$health`""
$updaterExe = Join-Path $Root 'deskask-updater.exe'
Set-Content -Encoding ASCII -Path (Join-Path $Root 'run-updater.cmd') -Value "@echo off`r`n`"$updaterExe`" run $updaterArgs >> `"$(Join-Path $Root 'logs\updater.log')`" 2>&1"
Set-Content -Encoding ASCII -Path (Join-Path $Root 'deskask-tenant.cmd') -Value "@echo off`r`nset DESKASK_CONFIG=$cfgPath`r`n`"$exe`" tenant %*"

# --- install or update the platform itself
Step 'Applying the bundle (checks the signature, swaps the version, starts the service, waits for /health)'
$applyArgs = @('apply', '-root', $Root, '-pub', (Join-Path $Root 'release-public.pem'), '-bundle', $Bundle, '-stop-cmd', $stopCmd, '-start-cmd', $startCmd, '-health', $health, '-health-timeout', '120s')
# The updater logs progress on stderr; Windows PowerShell turns that into an error under 'Stop'. Only its exit code decides.
$prevEap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
& $updaterExe @applyArgs 2>&1 | ForEach-Object { Write-Host "$_" }
$applyCode = $LASTEXITCODE
$ErrorActionPreference = $prevEap
if ($applyCode -ne 0) {
    $installed = Join-Path $Root 'live\.deskask-version'
    if (Test-Path $installed) { Write-Warning "The updater did not apply this bundle (see above). Version installed stays: $((Get-Content $installed -Raw).Trim())" }
    else { throw "The first install failed (see above). Look at $Root\logs and the Windows Application event log." }
}

Step "Starting the updater task $UpdaterTask"
$action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument "/c `"$(Join-Path $Root 'run-updater.cmd')`""
$trigger = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) `
    -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $UpdaterTask -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName $UpdaterTask

$svc = Get-Service -Name $PlatformService
Write-Host ''
Write-Host "Platform service : $($svc.Status)  ($health)"
$ver = Get-Content (Join-Path $Root 'live\.deskask-version') -Raw -ErrorAction SilentlyContinue
if ($ver) { $ver = $ver.Trim() }
Write-Host "Installed version: $ver"
Write-Host "Configuration    : $cfgPath"
Write-Host "Next             : $Root\deskask-tenant.cmd create --id acme --host acme.example.com --admin-email you@example.com   (see docs/provisioning.md)"
