// Package watch reports what Monitoring asked the agent to keep an eye on: whether named services are running, whether named
// processes are running (and how many), and new errors in the Windows event logs. Only the standard library is used.
package watch

import (
	"bufio"
	"encoding/json"
	"strconv"
	"strings"
)

// Service is the state of one service: running, stopped, starting, stopping, paused or missing (not installed).
type Service struct {
	Name    string `json:"name"`
	State   string `json:"state"`
	Running bool   `json:"running"`
}

// Process tells whether a program is running and in how many copies.
type Process struct {
	Name    string `json:"name"`
	Running bool   `json:"running"`
	Count   int    `json:"count"`
}

// Event is one entry from a Windows event log. Severity uses the syslog scale (2 critical, 3 error, 4 warning).
type Event struct {
	Log      string `json:"log"`
	Source   string `json:"source"`
	ID       int    `json:"id"`
	Severity int    `json:"severity"`
	Text     string `json:"text"`
	At       string `json:"at"`
	Record   uint64 `json:"record"`
}

// Norm makes process names comparable: lower case, without a path or ".exe".
func Norm(name string) string {
	n := strings.ToLower(strings.TrimSpace(name))
	if i := strings.LastIndexAny(n, `/\`); i >= 0 {
		n = n[i+1:]
	}
	return strings.TrimSuffix(n, ".exe")
}

// Processes reports each named program, counting the running copies among the given names of running programs.
func Processes(want []string, running []string) []Process {
	count := map[string]int{}
	for _, r := range running {
		count[Norm(r)]++
	}
	out := make([]Process, 0, len(want))
	for _, w := range want {
		c := count[Norm(w)]
		out = append(out, Process{Name: w, Running: c > 0, Count: c})
	}
	return out
}

// scState reads the STATE line of "sc query": "        STATE              : 4  RUNNING".
func scState(out string) string {
	sc := bufio.NewScanner(strings.NewReader(out))
	for sc.Scan() {
		f := strings.Fields(sc.Text())
		if len(f) >= 3 && f[0] == "STATE" && f[1] == ":" {
			switch n, _ := strconv.Atoi(f[2]); n {
			case 1:
				return "stopped"
			case 2:
				return "starting"
			case 3:
				return "stopping"
			case 4:
				return "running"
			case 5, 6:
				return "pausing"
			case 7:
				return "paused"
			}
		}
	}
	if strings.Contains(out, "1060") { // ERROR_SERVICE_DOES_NOT_EXIST
		return "missing"
	}
	return "unknown"
}

// systemdState turns "systemctl is-active" output into the same words.
func systemdState(out string) string {
	switch s := strings.TrimSpace(out); s {
	case "active":
		return "running"
	case "inactive", "failed":
		return "stopped"
	case "activating", "reloading":
		return "starting"
	case "deactivating":
		return "stopping"
	case "":
		return "unknown"
	default:
		return s
	}
}

func clip(s string, n int) string {
	s = strings.TrimSpace(s)
	if len(s) > n {
		return s[:n]
	}
	return s
}

// ServiceInfo is one service of the computer as the service manager lists it. Start is auto, manual, disabled or other.
// State is running, stopped, starting, stopping, paused, failed or inactive (Linux units that are not running and not failed).
type ServiceInfo struct {
	Name    string `json:"name"`
	Display string `json:"display,omitempty"`
	State   string `json:"state"`
	Start   string `json:"start"`
}

// maxInventory keeps one report to a size the server accepts.
const maxInventory = 1500

// parseCimServices reads the JSON that "Get-CimInstance Win32_Service | Select Name,DisplayName,State,StartMode" prints: one object or a list.
func parseCimServices(out string) []ServiceInfo {
	out = strings.TrimSpace(out)
	if out == "" {
		return nil
	}
	type row struct {
		Name        string `json:"Name"`
		DisplayName string `json:"DisplayName"`
		State       string `json:"State"`
		StartMode   string `json:"StartMode"`
	}
	var rows []row
	if strings.HasPrefix(out, "[") {
		if json.Unmarshal([]byte(out), &rows) != nil {
			return nil
		}
	} else {
		var one row
		if json.Unmarshal([]byte(out), &one) != nil {
			return nil
		}
		rows = []row{one}
	}
	res := make([]ServiceInfo, 0, len(rows))
	for _, r := range rows {
		if r.Name == "" {
			continue
		}
		st := "unknown"
		switch strings.ToLower(strings.ReplaceAll(r.State, " ", "")) {
		case "running":
			st = "running"
		case "stopped":
			st = "stopped"
		case "startpending", "continuepending":
			st = "starting"
		case "stoppending", "pausepending":
			st = "stopping"
		case "paused":
			st = "paused"
		}
		start := "other"
		switch strings.ToLower(r.StartMode) {
		case "auto":
			start = "auto"
		case "manual":
			start = "manual"
		case "disabled":
			start = "disabled"
		}
		res = append(res, ServiceInfo{Name: clip(r.Name, 120), Display: clip(r.DisplayName, 160), State: st, Start: start})
		if len(res) >= maxInventory {
			break
		}
	}
	return res
}

// parseSystemd joins "systemctl list-units --type=service --all --plain --no-legend" (UNIT LOAD ACTIVE SUB DESCRIPTION) with
// "systemctl list-unit-files --type=service --no-legend" (UNIT STATE). Only enabled units count as automatic.
func parseSystemd(units, files string) []ServiceInfo {
	enabled := map[string]string{}
	sc := bufio.NewScanner(strings.NewReader(files))
	for sc.Scan() {
		f := strings.Fields(sc.Text())
		if len(f) >= 2 && strings.HasSuffix(f[0], ".service") {
			enabled[strings.TrimSuffix(f[0], ".service")] = f[1]
		}
	}
	var res []ServiceInfo
	sc = bufio.NewScanner(strings.NewReader(units))
	for sc.Scan() {
		line := strings.TrimLeft(sc.Text(), "●* \t")
		f := strings.Fields(line)
		if len(f) < 4 || !strings.HasSuffix(f[0], ".service") || strings.Contains(f[0], "@") {
			continue
		}
		name := strings.TrimSuffix(f[0], ".service")
		if f[1] == "not-found" {
			continue
		}
		st := "inactive"
		switch f[2] {
		case "active":
			st = "running"
		case "failed":
			st = "failed"
		case "activating", "reloading":
			st = "starting"
		case "deactivating":
			st = "stopping"
		}
		start := "manual"
		switch enabled[name] {
		case "enabled", "enabled-runtime":
			start = "auto"
		case "disabled", "masked", "masked-runtime":
			start = "disabled"
		}
		d := ""
		if len(f) > 4 {
			d = strings.Join(f[4:], " ")
		}
		res = append(res, ServiceInfo{Name: clip(name, 120), Display: clip(d, 160), State: st, Start: start})
		if len(res) >= maxInventory {
			break
		}
	}
	return res
}
