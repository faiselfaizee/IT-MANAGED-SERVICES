# DeskAsk inventory collector for Windows (PowerShell 3.0+). The agent needs Windows 10 / Server 2016+;
# older Windows (7, 2008 R2, 2012 R2) can still be collected agentlessly over WinRM with this script.
# Used by the DeskAsk agent locally and by the portal remotely over WinRM (Invoke-Command).
# Output: one line of JSON on stdout. Read-only - makes no changes to the machine.
$ErrorActionPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'

function Q([string]$Class, [string]$Ns = 'root\cimv2') {
    if (Get-Command Get-CimInstance -ErrorAction SilentlyContinue) { Get-CimInstance -ClassName $Class -Namespace $Ns -ErrorAction SilentlyContinue }
    else { Get-WmiObject -Class $Class -Namespace $Ns -ErrorAction SilentlyContinue }
}
function S($v) { if ($null -eq $v) { return $null }; $t = "$v".Trim(); if ($t -eq '') { return $null }; return $t }
function D($v) {
    if ($null -eq $v) { return $null }
    if ($v -is [datetime]) { return $v.ToString('yyyy-MM-dd') }
    try { return ([datetime]$v).ToString('yyyy-MM-dd') } catch { }
    try { return [Management.ManagementDateTimeConverter]::ToDateTime($v).ToString('yyyy-MM-dd') } catch { return $null }
}

$cs   = Q Win32_ComputerSystem | Select-Object -First 1
$bios = Q Win32_BIOS | Select-Object -First 1
$os   = Q Win32_OperatingSystem | Select-Object -First 1
$csp  = Q Win32_ComputerSystemProduct | Select-Object -First 1

# ---- CPU ----
$cpus = @(Q Win32_Processor)
$cpuFirst = $cpus | Select-Object -First 1
$totalCores = ($cpus | Measure-Object -Property NumberOfCores -Sum).Sum
$logical = ($cpus | Measure-Object -Property NumberOfLogicalProcessors -Sum).Sum
$cpu = [ordered]@{
    model = S $cpuFirst.Name
    manufacturer = S $cpuFirst.Manufacturer
    socket = S $cpuFirst.SocketDesignation
    count = $cpus.Count
    total_cores = [int]$totalCores
    cores_per_cpu = [int]$cpuFirst.NumberOfCores
    logical = [int]$logical
}

# ---- Memory ----
$ram = (@(Q Win32_PhysicalMemory) | Measure-Object -Property Capacity -Sum).Sum
if (-not $ram) { $ram = $cs.TotalPhysicalMemory }

# ---- Disks & partitions ----
$disks = @()
$totalStorage = [int64]0
foreach ($d in @(Get-WmiObject -Class Win32_DiskDrive)) {
    $parts = @()
    $did = $d.DeviceID.Replace('\', '\\')
    foreach ($p in @(Get-WmiObject -Query "ASSOCIATORS OF {Win32_DiskDrive.DeviceID='$did'} WHERE AssocClass=Win32_DiskDriveToDiskPartition")) {
        $ld = @(Get-WmiObject -Query "ASSOCIATORS OF {Win32_DiskPartition.DeviceID='$($p.DeviceID)'} WHERE AssocClass=Win32_LogicalDiskToPartition") | Select-Object -First 1
        $parts += [ordered]@{
            name = S $p.Name
            type = S $p.Type
            size_bytes = [int64]$p.Size
            mount = $(if ($ld) { S $ld.DeviceID } else { $null })
            fs = $(if ($ld) { S $ld.FileSystem } else { $null })
            label = $(if ($ld) { S $ld.VolumeName } else { $null })
            free_bytes = $(if ($ld) { [int64]$ld.FreeSpace } else { $null })
        }
    }
    $media = S $d.MediaType
    try {
        $pd = Get-PhysicalDisk -ErrorAction Stop | Where-Object { $_.DeviceId -eq "$($d.Index)" } | Select-Object -First 1
        if ($pd) { $media = "$($pd.MediaType) $($pd.BusType)".Trim() }
    } catch { }
    $totalStorage += [int64]$d.Size
    $disks += [ordered]@{ name = S $d.DeviceID; model = S $d.Model; type = $media; size_bytes = [int64]$d.Size; partitions = @($parts) }
}

# ---- Network ----
$nics = @()
$configs = @{}
foreach ($c in @(Get-WmiObject -Class Win32_NetworkAdapterConfiguration)) { $configs[[int]$c.Index] = $c }
foreach ($n in @(Get-WmiObject -Class Win32_NetworkAdapter | Where-Object { $_.PhysicalAdapter -eq $true -and $_.MACAddress })) {
    $cfg = $configs[[int]$n.Index]
    $ips = @()
    if ($cfg -and $cfg.IPAddress) { $ips = @($cfg.IPAddress | Where-Object { $_ -match '^\d+\.\d+\.\d+\.\d+$' -or $_ -notmatch '^fe80' }) }
    $nics += [ordered]@{
        name = S $n.NetConnectionID
        description = S $n.Name
        mac = S $n.MACAddress
        connected = ($n.NetConnectionStatus -eq 2)
        speed_mbps = $(if ($n.Speed) { [int64]($n.Speed / 1000000) } else { $null })
        ips = @($ips)
    }
}

# ---- Name / domain ----
$fqdn = $null
try { $fqdn = [System.Net.Dns]::GetHostEntry([string]$env:COMPUTERNAME).HostName } catch { }
if (-not $fqdn -and $cs.PartOfDomain) { $fqdn = "$($cs.DNSHostName).$($cs.Domain)" }
if (-not $fqdn) { $fqdn = $cs.DNSHostName }

# ---- Installed applications ----
$apps = @()
$keys = @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*')
foreach ($k in $keys) {
    foreach ($a in @(Get-ItemProperty -Path $k -ErrorAction SilentlyContinue)) {
        if (-not $a.DisplayName -or $a.SystemComponent -eq 1 -or $a.ParentKeyName -or $a.ReleaseType -match 'Update|Hotfix') { continue }
        $apps += [ordered]@{ name = S $a.DisplayName; version = S $a.DisplayVersion; publisher = S $a.Publisher; installed_on = S $a.InstallDate }
    }
}

# ---- Antivirus ----
$av = @()
foreach ($p in @(Q AntiVirusProduct 'root\SecurityCenter2')) {
    $hex = '{0:X6}' -f [int]$p.productState
    $av += [ordered]@{ name = S $p.displayName; enabled = ($hex.Substring(2, 2) -in @('10', '11')); up_to_date = ($hex.Substring(4, 2) -eq '00'); state = $hex }
}
if ($av.Count -eq 0) {
    # Servers do not have Security Center - check Defender and well-known AV services
    try {
        $mp = Get-MpComputerStatus -ErrorAction Stop
        $av += [ordered]@{ name = 'Microsoft Defender Antivirus'; enabled = [bool]($mp.AntivirusEnabled -and $mp.RealTimeProtectionEnabled)
            up_to_date = ($mp.AntivirusSignatureAge -le 7); definitions = D $mp.AntivirusSignatureLastUpdated; state = "$($mp.AMRunningMode)" }
    } catch { }
    $known = @{ 'CSFalconService' = 'CrowdStrike Falcon'; 'SentinelAgent' = 'SentinelOne'; 'SepMasterService' = 'Symantec Endpoint Protection';
        'ekrn' = 'ESET'; 'AVP' = 'Kaspersky'; 'mfemms' = 'McAfee / Trellix'; 'Sophos MCS Agent' = 'Sophos'; 'SAVService' = 'Sophos'; 'ds_agent' = 'Trend Micro Deep Security';
        'ntrtscan' = 'Trend Micro Apex One'; 'EPSecurityService' = 'Bitdefender'; 'CbDefense' = 'VMware Carbon Black'; 'QHActiveDefense' = 'Quick Heal'; 'SeqrSvc' = 'Seqrite' }
    foreach ($svc in @(Get-Service -ErrorAction SilentlyContinue | Where-Object { $known.ContainsKey($_.Name) })) {
        $av += [ordered]@{ name = $known[$svc.Name]; enabled = ($svc.Status -eq 'Running'); up_to_date = $null; state = "$($svc.Status)" }
    }
}

# ---- Last patch date ----
$lastPatch = $null
try {
    $session = New-Object -ComObject Microsoft.Update.Session
    $searcher = $session.CreateUpdateSearcher()
    $count = $searcher.GetTotalHistoryCount()
    if ($count -gt 0) {
        $h = $searcher.QueryHistory(0, [Math]::Min($count, 200)) | Where-Object { $_.ResultCode -eq 2 -and $_.Operation -eq 1 } | Sort-Object Date -Descending | Select-Object -First 1
        if ($h) { $lastPatch = $h.Date.ToString('yyyy-MM-dd') }
    }
} catch { }
$hf = @(Get-HotFix -ErrorAction SilentlyContinue | Where-Object { $_.InstalledOn } | Sort-Object InstalledOn -Descending) | Select-Object -First 1
if ($hf) { $hfd = D $hf.InstalledOn; if (-not $lastPatch -or $hfd -gt $lastPatch) { $lastPatch = $hfd } }

$isServer = ($os.ProductType -ne 1)
$result = [ordered]@{
    schema = 1
    collector = 'windows.ps1'
    collected_at = (Get-Date).ToUniversalTime().ToString('o')
    os_family = 'windows'
    os_name = S $os.Caption
    os_version = "$($os.Version) build $($os.BuildNumber)"
    is_server = $isServer
    hostname = S $env:COMPUTERNAME
    dns_name = S $fqdn
    domain_type = $(if ($cs.PartOfDomain) { 'domain' } else { 'workgroup' })
    domain = $(if ($cs.PartOfDomain) { S $cs.Domain } else { S $cs.Workgroup })
    make = S $cs.Manufacturer
    model = S $cs.Model
    serial_number = $(if (S $bios.SerialNumber) { S $bios.SerialNumber } else { S $csp.IdentifyingNumber })
    bios_version = $(if (S $bios.SMBIOSBIOSVersion) { S $bios.SMBIOSBIOSVersion } else { S $bios.Version })
    cpu = $cpu
    ram_bytes = [int64]$ram
    disks = @($disks)
    total_storage_bytes = $totalStorage
    nics = @($nics)
    installed_apps = @($apps)
    antivirus = @($av)
    last_patch_date = $lastPatch
}
ConvertTo-Json -InputObject $result -Depth 6 -Compress
