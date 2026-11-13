package svc

import (
	"strings"
	"testing"
)

func TestTaskScriptHasTheWatchdog(t *testing.T) {
	s := Agent.windowsInstallScript(`C:\Program Files\DeskAskAgent\deskask-agent.exe`)
	for _, want := range []string{"-AtStartup", "-RepetitionInterval (New-TimeSpan -Minutes 5)", "-Trigger @($t, $w)", "-MultipleInstances IgnoreNew", "Start-ScheduledTask -TaskName 'DeskAskAgent'"} {
		if !strings.Contains(s, want) {
			t.Fatalf("install script misses %q:\n%s", want, s)
		}
	}
	if strings.Contains(Agent.windowsTaskScript(`C:\x.exe`, false), "Start-ScheduledTask") {
		t.Fatal("the repair must not start a second copy")
	}
}

func TestHasWatchdog(t *testing.T) {
	old := `<Task><Triggers><BootTrigger><Enabled>true</Enabled></BootTrigger></Triggers></Task>`
	cur := `<Task><Triggers><BootTrigger/><TimeTrigger><Repetition><Interval>PT5M</Interval></Repetition></TimeTrigger></Triggers></Task>`
	if hasWatchdog(old) || !hasWatchdog(cur) {
		t.Fatal("watchdog detection is wrong")
	}
}
