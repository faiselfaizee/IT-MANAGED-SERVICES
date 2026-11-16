// Package backup reads the results of the backup software installed on this computer, for the platform's Backup module:
// Windows Server Backup (Get-WBJob), the Azure Recovery Services agent (Get-OBJob), Azure Backup Server / DPM (Get-DPMJob) and Veeam
// (Backup & Replication and Veeam Agent, from their event logs). It only reads; it never starts, stops or changes a backup.
package backup

import (
	"bytes"
	"encoding/base64"
	"encoding/json"
	"errors"
	"strings"
	"unicode/utf16"
)

// Report is one finished run of a backup job.
type Report struct {
	Tool    string `json:"tool"`
	Job     string `json:"job"`
	Started string `json:"started,omitempty"`
	Ended   string `json:"ended,omitempty"`
	Result  string `json:"result"`
	Message string `json:"message,omitempty"`
	Bytes   int64  `json:"bytes"`
	Key     string `json:"key"`
}

// Found is what the agent sends: the backup tools installed and their recent runs.
type Found struct {
	Tools   []string `json:"tools"`
	Reports []Report `json:"reports"`
}

// Script is run by Windows PowerShell 5.1 or later. It writes one JSON object: {"tools":[...],"reports":[...]} with the runs of the last three days.
const Script = `$ErrorActionPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
$since = (Get-Date).AddDays(-3)
$tools = New-Object System.Collections.ArrayList
$out = New-Object System.Collections.ArrayList
function T($d) { if ($d) { try { return ([datetime]$d).ToUniversalTime().ToString('o') } catch { } }; return '' }
function R($s) { $s = "$s"; if ($s -match 'warn|partial') { 'warning' } elseif ($s -match 'fail|error|abort|cancel') { 'failed' } elseif ($s -match 'succe|complet|^ok$') { 'success' } else { '' } }
function Add($tool, $job, $st, $en, $res, $msg, $key) {
  if ($res -and "$job") { [void]$out.Add([pscustomobject]@{ tool = $tool; job = "$job"; started = (T $st); ended = (T $en); result = $res; message = ("$msg" -replace '\s+', ' ').Trim(); bytes = 0; key = "$key" }) }
}
function Ticks($d) { try { return ([datetime]$d).ToUniversalTime().Ticks } catch { return 0 } }

if (Get-Command Get-WBJob -ErrorAction SilentlyContinue) {
  [void]$tools.Add('wsb')
  foreach ($j in @(Get-WBJob -Previous 20)) {
    if (-not $j -or "$($j.JobType)" -ne 'Backup' -or -not $j.EndTime) { continue }
    if (([datetime]$j.EndTime) -lt $since) { continue }
    $res = if ($j.HResult -eq 0) { 'success' } else { 'failed' }
    Add 'wsb' 'Windows Server Backup' $j.StartTime $j.EndTime $res $j.ErrorDescription ('wsb:' + (Ticks $j.EndTime))
  }
}

if (-not (Get-Command Get-OBJob -ErrorAction SilentlyContinue)) { Import-Module MSOnlineBackup -ErrorAction SilentlyContinue }
if (Get-Command Get-OBJob -ErrorAction SilentlyContinue) {
  [void]$tools.Add('mars')
  foreach ($j in @(Get-OBJob -Previous 20)) {
    if (-not $j -or "$($j.JobType)" -match 'Recover|Restore') { continue }
    $s = $j.JobStatus
    $st = if ($s -and $s.StartTime) { $s.StartTime } else { $j.StartTime }
    $en = if ($s -and $s.EndTime) { $s.EndTime } else { $j.EndTime }
    if (-not $en -or ([datetime]$en) -lt $since) { continue }
    $state = if ($s -and $s.JobState) { "$($s.JobState)" } else { "$($j.JobState)$($j.Status)" }
    Add 'mars' 'Azure Backup (MARS)' $st $en (R $state) $state ('mars:' + (Ticks $en))
  }
}

if (-not (Get-Command Get-DPMJob -ErrorAction SilentlyContinue)) { Import-Module DataProtectionManager -ErrorAction SilentlyContinue }
if (Get-Command Get-DPMJob -ErrorAction SilentlyContinue) {
  [void]$tools.Add('mabs')
  foreach ($j in @(Get-DPMJob -From $since -To (Get-Date))) {
    if (-not $j) { continue }
    $type = "$($j.JobCategory) $($j.Type)".Trim()
    if ($type -match 'Restore|Recover|Inventory|Library|Catalog') { continue }
    $res = R "$($j.Status)"; if (-not $res -or -not $j.EndTime) { continue }
    $ds = "$($j.DataSources)"; if (-not $ds) { $ds = "$($j.Datasource)" }
    $pg = "$($j.ProtectionGroupName)"
    $name = if ($pg -and $ds) { "$pg / $ds" } elseif ($pg) { $pg } elseif ($ds) { $ds } else { $type }
    $id = "$($j.JobId)$($j.Id)"; if (-not $id) { $id = Ticks $j.EndTime }
    Add 'mabs' $name $j.StartTime $j.EndTime $res ("$type $($j.Status) $($j.ErrorInfo)") ('mabs:' + $id)
  }
}

foreach ($log in @('Veeam Backup', 'Veeam Agent')) {
  if (-not (Get-WinEvent -ListLog $log -ErrorAction SilentlyContinue)) { continue }
  if (-not $tools.Contains('veeam')) { [void]$tools.Add('veeam') }
  $ids = if ($log -eq 'Veeam Agent') { @(190) } else { @(190, 194, 390, 490, 590, 790) }
  foreach ($e in @(Get-WinEvent -FilterHashtable @{ LogName = $log; Id = $ids; StartTime = $since } -MaxEvents 300 -ErrorAction SilentlyContinue)) {
    if (-not $e) { continue }
    $m = "$($e.Message)"; $job = ''
    if ($m -match "'([^']+)'") { $job = $Matches[1] } elseif ($m -match '"([^"]+)"') { $job = $Matches[1] } elseif ($m -match 'job (.+?) (has )?finished') { $job = $Matches[1] }
    if (-not $job) { continue }
    $res = ''
    if ($m -match 'finished with (\w+)') { $res = R $Matches[1] }
    if (-not $res) { $res = switch ($e.Level) { 1 { 'failed' } 2 { 'failed' } 3 { 'warning' } default { 'success' } } }
    Add 'veeam' $job $null $e.TimeCreated $res $m ('veeam:' + $log + ':' + $e.RecordId)
  }
}

@{ tools = @($tools); reports = @($out) } | ConvertTo-Json -Depth 4 -Compress`

// Encoded is the script for "powershell -EncodedCommand" (base64 of UTF-16LE), so no quoting reaches the command line.
func Encoded() string {
	u := utf16.Encode([]rune(Script))
	b := make([]byte, len(u)*2)
	for i, c := range u {
		b[2*i], b[2*i+1] = byte(c), byte(c>>8)
	}
	return base64.StdEncoding.EncodeToString(b)
}

// Parse reads the script's output. Text around the JSON object (warnings from a module) is skipped; a lone report or tool instead of
// a list is accepted, as Windows PowerShell 5.1 writes one-item arrays that way in some places.
func Parse(out []byte) (Found, error) {
	var f Found
	i, j := bytes.IndexByte(out, '{'), bytes.LastIndexByte(out, '}')
	if i < 0 || j < i {
		return f, errors.New("no result from the backup script")
	}
	var raw struct {
		Tools   json.RawMessage `json:"tools"`
		Reports json.RawMessage `json:"reports"`
	}
	if err := json.Unmarshal(out[i:j+1], &raw); err != nil {
		return f, err
	}
	f.Tools = strs(raw.Tools)
	if r := bytes.TrimSpace(raw.Reports); len(r) > 0 && r[0] == '{' {
		var one Report
		if json.Unmarshal(r, &one) == nil {
			f.Reports = []Report{one}
		}
	} else if len(r) > 0 {
		_ = json.Unmarshal(r, &f.Reports)
	}
	keep := f.Reports[:0]
	for _, x := range f.Reports {
		x.Job = strings.TrimSpace(x.Job)
		if x.Job == "" || (x.Result != "success" && x.Result != "warning" && x.Result != "failed") {
			continue
		}
		if len(x.Message) > 2000 {
			x.Message = x.Message[:2000]
		}
		keep = append(keep, x)
	}
	f.Reports = keep
	return f, nil
}

func strs(raw json.RawMessage) []string {
	raw = bytes.TrimSpace(raw)
	if len(raw) == 0 || string(raw) == "null" {
		return nil
	}
	var list []string
	if json.Unmarshal(raw, &list) == nil {
		return list
	}
	var one string
	if json.Unmarshal(raw, &one) == nil && one != "" {
		return []string{one}
	}
	return nil
}
