//go:build !windows

package watch

import (
	"context"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"time"
)

// Services asks systemd for each named unit (Linux). Elsewhere every service shows as unknown.
func Services(ctx context.Context, names []string) []Service {
	out := make([]Service, 0, len(names))
	for _, n := range names {
		st := "unknown"
		if p, err := exec.LookPath("systemctl"); err == nil {
			c, cancel := context.WithTimeout(ctx, 10*time.Second)
			o, _ := exec.CommandContext(c, p, "is-active", n).Output()
			cancel()
			st = systemdState(string(o))
			if st == "stopped" { // is-active says "inactive" for a unit that does not exist too
				c2, cancel2 := context.WithTimeout(ctx, 10*time.Second)
				lo, _ := exec.CommandContext(c2, p, "list-unit-files", n+".service", "--no-legend").Output()
				cancel2()
				if strings.TrimSpace(string(lo)) == "" && !strings.Contains(n, ".") {
					st = "missing"
				}
			}
		}
		out = append(out, Service{Name: n, State: st, Running: st == "running"})
	}
	return out
}

// Running lists the programs running now, from /proc (the command name, and the program file for long names).
func Running() ([]string, error) {
	dirs, err := filepath.Glob("/proc/[0-9]*")
	if err != nil {
		return nil, err
	}
	var names []string
	for _, d := range dirs {
		if exe, err := os.Readlink(d + "/exe"); err == nil {
			names = append(names, filepath.Base(strings.TrimSuffix(exe, " (deleted)")))
			continue
		}
		if b, err := os.ReadFile(d + "/comm"); err == nil {
			names = append(names, strings.TrimSpace(string(b)))
		}
	}
	return names, nil
}

// EventLog exists on Windows only; on other systems logs reach Monitoring through syslog.
type EventLog struct{}

func (l *EventLog) Read(ctx context.Context) []Event { return nil }
