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

func TestParseCimServices(t *testing.T) {
	got := parseCimServices(`[{"Name":"Spooler","DisplayName":"Print Spooler","State":"Running","StartMode":"Auto"},{"Name":"BITS","DisplayName":"BITS","State":"Stop Pending","StartMode":"Manual"},{"Name":"X","DisplayName":"","State":"Stopped","StartMode":"Disabled"}]`)
	if len(got) != 3 || got[0].State != "running" || got[0].Start != "auto" || got[1].State != "stopping" || got[2].Start != "disabled" {
		t.Fatalf("got %+v", got)
	}
	one := parseCimServices(`{"Name":"W32Time","DisplayName":"Windows Time","State":"Stopped","StartMode":"Auto"}`)
	if len(one) != 1 || one[0].State != "stopped" || one[0].Start != "auto" {
		t.Fatalf("single object: %+v", one)
	}
	if parseCimServices("") != nil || parseCimServices("not json") != nil {
		t.Fatal("bad input must give nothing")
	}
}

func TestParseSystemd(t *testing.T) {
	units := "  ssh.service     loaded active   running OpenBSD Secure Shell server\n● nginx.service   loaded failed   failed  A high performance web server\n  cron.service    loaded inactive dead    Cron\n  getty@tty1.service loaded active running Getty\n  ghost.service   not-found inactive dead ghost.service\n"
	files := "ssh.service enabled enabled\nnginx.service enabled enabled\ncron.service static -\nghost.service disabled enabled\n"
	got := parseSystemd(units, files)
	if len(got) != 3 {
		t.Fatalf("got %+v", got)
	}
	if got[0].Name != "ssh" || got[0].State != "running" || got[0].Start != "auto" || got[1].State != "failed" || got[2].State != "inactive" || got[2].Start != "manual" {
		t.Fatalf("got %+v", got)
	}
}
