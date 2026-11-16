package agentrun

import (
	"testing"
	"time"

	"github.com/deskask/platform/agent/internal/filebackup"
)

func TestPlanIsDue(t *testing.T) {
	loc := time.FixedZone("IST", 5*3600+1800)
	now := time.Date(2026, 11, 16, 23, 30, 0, 0, loc)
	daily := backupPlan{Enabled: true, EveryHours: 24, AtHour: 22}
	if !daily.due(time.Date(2026, 11, 15, 22, 5, 0, 0, loc), now) {
		t.Error("yesterday's run: due again after 22:00 today")
	}
	if daily.due(time.Date(2026, 11, 16, 22, 1, 0, 0, loc), now) {
		t.Error("already ran after 22:00 today")
	}
	early := time.Date(2026, 11, 16, 9, 0, 0, 0, loc)
	if daily.due(time.Date(2026, 11, 15, 22, 5, 0, 0, loc), early) {
		t.Error("before 22:00, yesterday's 22:05 run still counts")
	}
	if !daily.due(time.Time{}, early) {
		t.Error("never ran: due at once")
	}
	hourly := backupPlan{Enabled: true, EveryHours: 4, AtHour: -1}
	if hourly.due(now.Add(-3*time.Hour), now) || !hourly.due(now.Add(-4*time.Hour), now) {
		t.Error("every 4 hours")
	}
	off := backupPlan{Enabled: false, EveryHours: 4}
	if off.due(time.Time{}, now) {
		t.Error("a switched-off plan never runs")
	}
}

func TestEachPlanHasItsOwnFolder(t *testing.T) {
	p := backupPlan{ID: 7, Storage: filebackup.Storage{Kind: "smb", Path: `\\nas\backup\`}}
	if got := planStorage(p).Path; got != `\\nas\backup\deskask-plan-7` {
		t.Error(got)
	}
	p.Storage = filebackup.Storage{Kind: "s3", Prefix: "/office/"}
	if got := planStorage(p).Prefix; got != "office/deskask-plan-7" {
		t.Error(got)
	}
	p.Storage = filebackup.Storage{Kind: "local", Path: "/mnt/usb"}
	if got := planStorage(p).Path; got != "/mnt/usb/deskask-plan-7" {
		t.Error(got)
	}
}
