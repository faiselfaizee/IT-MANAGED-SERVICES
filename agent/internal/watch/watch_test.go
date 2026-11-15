package watch

import (
	"context"
	"os"
	"path/filepath"
	"runtime"
	"testing"
)

func TestScQueryStates(t *testing.T) {
	out := "SERVICE_NAME: Spooler\n        TYPE               : 110  WIN32_OWN_PROCESS\n        STATE              : 4  RUNNING\n                                (STOPPABLE)\n"
	if scState(out) != "running" {
		t.Fatal(scState(out))
	}
	if scState("        STATE              : 1  STOPPED\n") != "stopped" {
		t.Fatal("stopped")
	}
	if scState("[SC] EnumQueryServicesStatus:OpenService FAILED 1060:\n\nThe specified service does not exist as an installed service.") != "missing" {
		t.Fatal("missing")
	}
	if systemdState("active\n") != "running" || systemdState("failed") != "stopped" || systemdState("") != "unknown" {
		t.Fatal("systemd")
	}
}

func TestProcessesAreMatchedWithoutPathCaseOrExe(t *testing.T) {
	got := Processes([]string{"sqlservr.exe", "nginx", "backup"}, []string{`C:\Program Files\SQL\sqlservr.exe`, "nginx", "NGINX", "explorer.exe"})
	if !got[0].Running || got[0].Count != 1 || got[1].Count != 2 || got[2].Running {
		t.Fatalf("%+v", got)
	}
}

func TestRenderedEventLogXmlIsRead(t *testing.T) {
	out := `<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Service Control Manager'/><EventID Qualifiers='49152'>7031</EventID><Level>2</Level>` +
		`<TimeCreated SystemTime='2026-11-15T03:04:05.000Z'/><EventRecordID>1234</EventRecordID><Channel>System</Channel></System>` +
		`<RenderingInfo Culture='en-US'><Message>The Print Spooler service terminated
 unexpectedly.</Message></RenderingInfo></Event>` +
		`<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='disk'/><EventID>7</EventID><Level>1</Level><TimeCreated SystemTime='2026-11-15T03:04:06.000Z'/><EventRecordID>1235</EventRecordID><Channel>System</Channel></System></Event>`
	evs, err := parseEvents(out)
	if err != nil || len(evs) != 2 {
		t.Fatalf("%v %v", evs, err)
	}
	e := evs[0]
	if e.ID != 7031 || e.Severity != 3 || e.Source != "Service Control Manager" || e.Record != 1234 || e.Text != "The Print Spooler service terminated unexpectedly." || e.Log != "System" {
		t.Fatalf("%+v", e)
	}
	if evs[1].Severity != 2 {
		t.Fatalf("critical %+v", evs[1])
	}
}

func TestThisProgramIsSeenRunning(t *testing.T) {
	if runtime.GOOS != "linux" {
		t.Skip("reads /proc")
	}
	names, err := Running()
	if err != nil {
		t.Fatal(err)
	}
	self, _ := os.Executable()
	p := Processes([]string{filepath.Base(self)}, names)
	if !p[0].Running {
		t.Fatalf("%s not among %d programs", filepath.Base(self), len(names))
	}
	if s := Services(context.Background(), []string{"no-such-unit-dk"}); s[0].Running {
		t.Fatalf("%+v", s)
	}
}
