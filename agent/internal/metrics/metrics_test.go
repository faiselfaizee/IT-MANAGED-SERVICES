package metrics

import (
	"runtime"
	"testing"
)

func TestProcStatGivesIdleAndTotalWithoutGuestTime(t *testing.T) {
	idle, all, err := parseStat("cpu  100 0 50 800 50 0 0 0 30 0\ncpu0 1 2 3 4 5\n")
	if err != nil || idle != 850 || all != 1000 {
		t.Fatalf("idle %d all %d err %v", idle, all, err)
	}
	if _, _, err := parseStat("intr 1 2 3"); err == nil {
		t.Fatal("expected an error without a cpu line")
	}
}

func TestProcessorUseIsTheBusyShareBetweenTwoReadings(t *testing.T) {
	if got := cpuPct(850, 1000, 900, 1200); got != 75 { // 200 ticks, 50 idle
		t.Fatalf("got %v", got)
	}
	if got := cpuPct(850, 1000, 850, 1000); got != 0 {
		t.Fatalf("no time passed: %v", got)
	}
	if got := cpuPct(100, 1000, 50, 900); got != 0 { // counters went back (restart): never negative
		t.Fatalf("got %v", got)
	}
}

func TestMemoryInUseIsTotalMinusAvailable(t *testing.T) {
	used, total, err := parseMeminfo("MemTotal:       8000000 kB\nMemFree:  1000000 kB\nMemAvailable:   3000000 kB\nBuffers: 1 kB\n")
	if err != nil || total != 8000000*1024 || used != 5000000*1024 {
		t.Fatalf("used %d total %d err %v", used, total, err)
	}
	// older kernels have no MemAvailable
	used, _, _ = parseMeminfo("MemTotal: 1000 kB\nMemFree: 100 kB\nBuffers: 100 kB\nCached: 300 kB\n")
	if used != 500*1024 {
		t.Fatalf("old kernel used %d", used)
	}
}

func TestOnlyRealFileSystemsAreListedOncePerDevice(t *testing.T) {
	got := parseMounts(`/dev/sda1 / ext4 rw 0 0
proc /proc proc rw 0 0
tmpfs /run tmpfs rw 0 0
/dev/sda1 /var/lib/docker ext4 rw 0 0
/dev/loop0 /snap/core/1 squashfs ro 0 0
/dev/sdb1 /srv/my\040data xfs rw 0 0
/dev/sda15 /boot/efi vfat rw 0 0
`)
	want := []string{"/", "/srv/my data"}
	if len(got) != len(want) || got[0] != want[0] || got[1] != want[1] {
		t.Fatalf("got %v", got)
	}
}

func TestASampleCanBeTakenOnThisComputer(t *testing.T) {
	if runtime.GOOS != "linux" && runtime.GOOS != "windows" {
		t.Skip("not read on this system")
	}
	var s Sampler
	a, err := s.Take()
	if err != nil {
		t.Fatal(err)
	}
	if a.CPU != -1 || a.MemTotal == 0 || a.MemUsed > a.MemTotal {
		t.Fatalf("first sample %+v", a)
	}
	b, _ := s.Take()
	if b.CPU < 0 || b.CPU > 100 {
		t.Fatalf("second sample cpu %v", b.CPU)
	}
}
