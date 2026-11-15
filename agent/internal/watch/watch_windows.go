//go:build windows

package watch

import (
	"context"
	"fmt"
	"os/exec"
	"strconv"
	"strings"
	"syscall"
	"time"
	"unsafe"
)

func run(ctx context.Context, name string, args ...string) (string, error) {
	ctx, cancel := context.WithTimeout(ctx, 20*time.Second)
	defer cancel()
	cmd := exec.CommandContext(ctx, name, args...)
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
	b, err := cmd.CombinedOutput()
	return string(b), err
}

// Services asks the service manager for each named service.
func Services(ctx context.Context, names []string) []Service {
	out := make([]Service, 0, len(names))
	for _, n := range names {
		o, _ := run(ctx, "sc.exe", "query", n)
		st := scState(o)
		out = append(out, Service{Name: n, State: st, Running: st == "running"})
	}
	return out
}

// Running lists the programs running now.
func Running() ([]string, error) {
	h, err := syscall.CreateToolhelp32Snapshot(syscall.TH32CS_SNAPPROCESS, 0)
	if err != nil {
		return nil, err
	}
	defer syscall.CloseHandle(h)
	var pe syscall.ProcessEntry32
	pe.Size = uint32(unsafe.Sizeof(pe))
	var names []string
	for err = syscall.Process32First(h, &pe); err == nil; err = syscall.Process32Next(h, &pe) {
		names = append(names, syscall.UTF16ToString(pe.ExeFile[:]))
	}
	return names, nil
}

// EventLog reads errors and critical events newer than the given record numbers from the System and Application logs. On the first
// call (no record number yet) it only notes where the logs are, so old events are not sent.
type EventLog struct{ last map[string]uint64 }

func (l *EventLog) Read(ctx context.Context) []Event {
	if l.last == nil {
		l.last = map[string]uint64{}
	}
	var all []Event
	for _, name := range []string{"System", "Application"} {
		last, known := l.last[name]
		if !known {
			o, err := run(ctx, "wevtutil.exe", "qe", name, "/rd:true", "/c:1", "/f:RenderedXml")
			if err != nil {
				continue
			}
			l.last[name] = 0
			if evs, err := parseEvents(o); err == nil && len(evs) > 0 {
				l.last[name] = evs[0].Record
			}
			continue
		}
		q := fmt.Sprintf("*[System[(Level=1 or Level=2) and (EventRecordID > %s)]]", strconv.FormatUint(last, 10))
		o, err := run(ctx, "wevtutil.exe", "qe", name, "/q:"+q, "/c:50", "/f:RenderedXml")
		if err != nil || strings.TrimSpace(o) == "" {
			continue
		}
		evs, err := parseEvents(o)
		if err != nil {
			continue
		}
		for _, e := range evs {
			if e.Record > l.last[name] {
				l.last[name] = e.Record
			}
		}
		all = append(all, evs...)
	}
	return all
}

// Inventory lists every service with its state and start type (one PowerShell call, so it is meant for every few minutes, not every minute).
func Inventory(ctx context.Context) []ServiceInfo {
	o, err := run(ctx, "powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
		"Get-CimInstance Win32_Service | Select-Object Name,DisplayName,State,StartMode | ConvertTo-Json -Compress")
	if err != nil {
		return nil
	}
	return parseCimServices(o)
}
