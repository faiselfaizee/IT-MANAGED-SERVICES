// Package watch reports what Monitoring asked the agent to keep an eye on: whether named services are running, whether named
// processes are running (and how many), and new errors in the Windows event logs. Only the standard library is used.
package watch

import (
	"bufio"
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
