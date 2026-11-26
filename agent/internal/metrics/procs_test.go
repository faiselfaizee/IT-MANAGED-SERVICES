package metrics

import "testing"

func TestTopNamesTheBusiestAndAddsProgramsOfTheSameName(t *testing.T) {
	prev := map[int]procRaw{1: {"sqlservr", 0, 1 << 30}, 2: {"chrome", 0, 100 << 20}, 3: {"chrome", 0, 200 << 20}, 4: {"idle", 0, 1 << 20}}
	cur := map[int]procRaw{1: {"sqlservr", 800, 2 << 30}, 2: {"chrome", 100, 100 << 20}, 3: {"chrome", 100, 300 << 20}, 4: {"idle", 0, 1 << 20}, 9: {"new", 5000, 50 << 20}}
	// 10 seconds, 4 cores, 100 units a second: 800 units = 8 s of 40 s of processor = 20%
	top := topFrom(prev, cur, 10, 4, 100)
	if len(top.CPU) != 2 || top.CPU[0].Name != "sqlservr" || top.CPU[0].Pct != 20 || top.CPU[1].Name != "chrome" || top.CPU[1].Pct != 5 {
		t.Fatalf("cpu list wrong: %+v", top.CPU)
	}
	if top.Mem[0].Name != "sqlservr" || top.Mem[0].MemMB != 2048 || top.Mem[1].Name != "chrome" || top.Mem[1].MemMB != 400 {
		t.Fatalf("memory list wrong: %+v", top.Mem)
	}
	for _, p := range top.CPU {
		if p.Name == "new" || p.Name == "idle" {
			t.Fatalf("%s must not be listed for processor: %+v", p.Name, top.CPU)
		}
	}
}

func TestBusiestIsQuietWhenTheComputerIsNotBusy(t *testing.T) {
	p := procTracker{}
	if got := p.busiest(10, 1, 10); got != nil {
		t.Fatalf("a quiet computer names nothing: %+v", got)
	}
}
