package inventory

import (
	"context"
	"encoding/base64"
	"encoding/binary"
	"encoding/json"
	"fmt"
	"os/exec"
	"strings"
	"unicode/utf16"
)

// WindowsScript prints the inventory as JSON, already in the shape the server reads. It runs under Windows PowerShell 5.1 (every supported
// Windows has it) as the local system account, so it sees every installed program, including the ones installed for single users.
const WindowsScript = `
$ErrorActionPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
function Fmt($d) { if ($d) { try { return ([datetime]$d).ToString('yyyy-MM-dd') } catch { return $null } } return $null }
$cs = Get-CimInstance Win32_ComputerSystem
$os = Get-CimInstance Win32_OperatingSystem
$bios = Get-CimInstance Win32_BIOS
$cpus = @(Get-CimInstance Win32_Processor)

$disks = @()
foreach ($d in @(Get-Disk)) {
  $pd = Get-PhysicalDisk | Where-Object { $_.DeviceId -eq "$($d.Number)" } | Select-Object -First 1
  $type = ''
  if ($pd.BusType -eq 'NVMe') { $type = 'NVMe' } elseif ($pd.MediaType -eq 'SSD') { $type = 'SSD' } elseif ($pd.MediaType -eq 'HDD') { $type = 'HDD' }
  $parts = @()
  foreach ($p in @(Get-Partition -DiskNumber $d.Number)) {
    $v = $p | Get-Volume
    $mount = ''; if ($p.DriveLetter) { $mount = "$($p.DriveLetter):" }
    $parts += [pscustomobject]@{ name = "Partition $($p.PartitionNumber)"; size_bytes = [int64]$p.Size; fs = "$($v.FileSystem)"; mount = $mount; label = "$($v.FileSystemLabel)"; free_bytes = [int64]$v.SizeRemaining; type = "$($p.Type)" }
  }
  $disks += [pscustomobject]@{ name = "Disk $($d.Number)"; model = "$($d.FriendlyName)"; type = $type; size_bytes = [int64]$d.Size; partitions = $parts }
}

$nics = @()
foreach ($a in @(Get-NetAdapter | Where-Object { $_.HardwareInterface })) {
  $ips = @(Get-NetIPAddress -InterfaceIndex $a.ifIndex | ForEach-Object { "$($_.IPAddress)" })
  $speed = 0; if ($a.Speed) { $speed = [int64]($a.Speed / 1000000) }
  $nics += [pscustomobject]@{ name = "$($a.Name)"; description = "$($a.InterfaceDescription)"; mac = "$($a.MacAddress)"; connected = ($a.Status -eq 'Up'); speed_mbps = $speed; ips = $ips }
}

$keys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*', 'Registry::HKEY_USERS\S-1-5-21-*\Software\Microsoft\Windows\CurrentVersion\Uninstall\*'
$apps = @()
foreach ($k in @(Get-ItemProperty $keys)) {
  if (-not $k.DisplayName -or $k.SystemComponent -eq 1 -or $k.ParentKeyName) { continue }
  $on = $null; if ($k.InstallDate -match '^\d{8}$') { $on = Fmt ($k.InstallDate.Substring(0,4) + '-' + $k.InstallDate.Substring(4,2) + '-' + $k.InstallDate.Substring(6,2)) }
  $apps += [pscustomobject]@{ name = "$($k.DisplayName)"; version = "$($k.DisplayVersion)"; publisher = "$($k.Publisher)"; installed_on = $on }
}

$av = @()
foreach ($p in @(Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct)) {
  $s = [int]$p.productState
  $av += [pscustomobject]@{ name = "$($p.displayName)"; enabled = (($s -band 0xF000) -eq 0x1000); up_to_date = (($s -band 0xF0) -eq 0); state = "$s" }
}
if ($av.Count -eq 0) {
  $mp = Get-MpComputerStatus
  if ($mp) { $av += [pscustomobject]@{ name = 'Microsoft Defender'; enabled = [bool]$mp.AntivirusEnabled; up_to_date = ($mp.AntivirusSignatureAge -le 7); state = ''; definitions = "$($mp.AntivirusSignatureVersion)" } }
}

# Hyper-V: the virtual machines of this host
$vms = @()
if (Get-Command Get-VM -ErrorAction SilentlyContinue) {
  foreach ($v in @(Get-VM)) {
    $ips = @($v.NetworkAdapters | ForEach-Object { $_.IPAddresses } | Where-Object { $_ -match '^\d+\.\d+\.\d+\.\d+$' })
    $disk = [int64]0
    foreach ($h in @($v.HardDrives)) { if ($h.Path) { $vh = Get-VHD -Path $h.Path; if ($vh) { $disk += [int64]$vh.FileSize } } }
    $mem = [int64]$v.MemoryAssigned; if ($mem -eq 0) { $mem = [int64]$v.MemoryStartup }
    $power = "$($v.State)"; if ($v.State -eq 'Running') { $power = 'On' } elseif ($v.State -eq 'Off') { $power = 'Off' }
    $vms += [pscustomobject]@{ name = "$($v.Name)"; power = $power; ip = ($ips -join ', '); cpus = [int]$v.ProcessorCount; memory_bytes = $mem; disk_bytes = $disk; generation = [int]$v.Generation; uptime_s = [int64]$v.Uptime.TotalSeconds; host = "$env:COMPUTERNAME" }
  }
}
$extra = $null
if ($vms.Count -gt 0 -or (Get-Command Get-VM -ErrorAction SilentlyContinue)) { $extra = [pscustomobject]@{ platform = 'hyperv'; api = "Microsoft Hyper-V on $($os.Caption)"; vms = @($vms) } }

$patch = Get-HotFix | Where-Object { $_.InstalledOn } | Sort-Object InstalledOn -Descending | Select-Object -First 1
$first = $cpus | Select-Object -First 1
$dns = "$($cs.DNSHostName)"; if ($cs.PartOfDomain -and $cs.Domain) { $dns = "$($cs.DNSHostName).$($cs.Domain)" }
$cores = 0; $logical = 0; foreach ($c in $cpus) { $cores += [int]$c.NumberOfCores; $logical += [int]$c.NumberOfLogicalProcessors }
$perCpu = 0; if ($first) { $perCpu = [int]$first.NumberOfCores }
$domainType = 'workgroup'; $domain = "$($cs.Workgroup)"; if ($cs.PartOfDomain) { $domainType = 'domain'; $domain = "$($cs.Domain)" }

[pscustomobject]@{
  hostname = "$env:COMPUTERNAME"; dns_name = $dns; os_family = 'windows'; os_name = "$($os.Caption)"; os_version = "$($os.Version)"
  make = "$($cs.Manufacturer)"; model = "$($cs.Model)"; serial_number = "$($bios.SerialNumber)"; bios_version = "$($bios.SMBIOSBIOSVersion)"
  cpu = [pscustomobject]@{ manufacturer = "$($first.Manufacturer)"; model = "$(($first.Name) -replace '\s+', ' ')".Trim(); socket = "$($first.SocketDesignation)"; count = $cpus.Count; total_cores = $cores; cores_per_cpu = $perCpu; logical = $logical }
  ram_bytes = [int64]$cs.TotalPhysicalMemory; uptime_s = [int64]((Get-Date) - $os.LastBootUpTime).TotalSeconds
  disks = $disks; nics = $nics; installed_apps = $apps; antivirus = $av
  domain_type = $domainType; domain = $domain; last_patch_date = (Fmt $patch.InstalledOn); is_server = ($os.ProductType -ne 1)
  hardware_extra = $extra
} | ConvertTo-Json -Depth 6 -Compress
`

// PowerShellArgs returns the arguments to run a script with Windows PowerShell: the script travels as -EncodedCommand (UTF-16 in base64),
// so quoting can never break it.
func PowerShellArgs(script string) []string {
	u := utf16.Encode([]rune(script))
	b := make([]byte, 0, len(u)*2)
	for _, c := range u {
		b = binary.LittleEndian.AppendUint16(b, c)
	}
	return []string{"-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", base64.StdEncoding.EncodeToString(b)}
}

func collectWindows(ctx context.Context) (*Inventory, error) {
	out, err := exec.CommandContext(ctx, "powershell.exe", PowerShellArgs(WindowsScript)...).Output()
	if err != nil && len(out) == 0 {
		return nil, fmt.Errorf("PowerShell could not read the inventory: %w", err)
	}
	return ParseWindowsJSON(out)
}

// ParseWindowsJSON reads the script's output. PowerShell prints a single-item array as a bare object; both forms are accepted.
func ParseWindowsJSON(b []byte) (*Inventory, error) {
	s := strings.TrimPrefix(strings.TrimSpace(string(b)), "\xEF\xBB\xBF")
	var raw map[string]json.RawMessage
	if err := json.Unmarshal([]byte(s), &raw); err != nil {
		return nil, fmt.Errorf("the inventory script did not return JSON: %w", err)
	}
	for _, k := range []string{"disks", "nics", "installed_apps", "antivirus"} {
		if v, ok := raw[k]; ok && len(v) > 0 && v[0] == '{' {
			raw[k] = json.RawMessage("[" + string(v) + "]")
		}
	}
	for _, k := range []string{"partitions", "ips"} { // nested single items
		_ = k
	}
	fixed, _ := json.Marshal(raw)
	var inv Inventory
	if err := json.Unmarshal(fixed, &inv); err != nil {
		return nil, err
	}
	if inv.Extra != nil { // a host with one virtual machine: PowerShell may print it as a bare object
		if v, ok := inv.Extra["vms"].(map[string]any); ok {
			inv.Extra["vms"] = []any{v}
		}
	}
	return &inv, nil
}
