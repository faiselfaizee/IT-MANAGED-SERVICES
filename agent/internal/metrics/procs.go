package metrics

import (
	"runtime"
	"sort"
	"time"
)

// procRaw is what one reading of a running program gives: its name, the processor time it has used so far (in the system's own unit, see
// procUnitsPerSec) and the memory it holds.
type procRaw struct {
	Name string
	CPU  uint64
	Mem  uint64
}

// Proc is one busy program in a sample.
type Proc struct {
	Name  string  `json:"name"`
	Pct   float64 `json:"pct,omitempty"`    // share of the whole computer's processor, in percent
	MemMB int64   `json:"mem_mb,omitempty"` // memory held, in megabytes
}

// Top names the programs that use the most processor and memory. It is attached to a sample only when the computer is busy, so
// that an alert can say what is using it.
type Top struct {
	CPU []Proc `json:"cpu"`
	Mem []Proc `json:"mem"`
}

const (
	busyCPU = 75.0 // percent of processor use from which the busiest programs are named
	busyMem = 85.0 // percent of memory in use from which they are named
	topN    = 5
)

// topFrom works out the busiest programs from two readings that are elapsed seconds apart. Programs that started after the first
// reading are left out of the processor list (their use cannot be told), but not out of the memory list. Several processes of one
// program (browsers) are added together.
func topFrom(prev, cur map[int]procRaw, elapsed float64, ncpu int, unitsPerSec float64) Top {
	cpu := map[string]float64{}
	mem := map[string]uint64{}
	for pid, c := range cur {
		mem[c.Name] += c.Mem
		if p, ok := prev[pid]; ok && p.Name == c.Name && c.CPU >= p.CPU && elapsed > 0 && ncpu > 0 {
			cpu[c.Name] += float64(c.CPU-p.CPU) / unitsPerSec / elapsed / float64(ncpu) * 100
		}
	}
	var t Top
	for n, v := range cpu {
		if v >= 0.5 {
			t.CPU = append(t.CPU, Proc{Name: n, Pct: float64(int64(v*10+0.5)) / 10})
		}
	}
	for n, v := range mem {
		t.Mem = append(t.Mem, Proc{Name: n, MemMB: int64(v >> 20)})
	}
	sort.Slice(t.CPU, func(i, j int) bool { return t.CPU[i].Pct > t.CPU[j].Pct })
	sort.Slice(t.Mem, func(i, j int) bool { return t.Mem[i].MemMB > t.Mem[j].MemMB })
	if len(t.CPU) > topN {
		t.CPU = t.CPU[:topN]
	}
	if len(t.Mem) > topN {
		t.Mem = t.Mem[:topN]
	}
	return t
}

// procTracker keeps the previous reading of the programs between samples.
type procTracker struct {
	prev map[int]procRaw
	at   time.Time
}

// busiest reads the programs and, when the computer is busy, names the busiest. It returns nil when it is not busy or cannot read.
func (p *procTracker) busiest(cpuPct float64, memUsed, memTotal uint64) *Top {
	cur, err := readProcs()
	if err != nil {
		return nil
	}
	now := time.Now()
	prev, at := p.prev, p.at
	p.prev, p.at = cur, now
	memPct := 0.0
	if memTotal > 0 {
		memPct = float64(memUsed) * 100 / float64(memTotal)
	}
	if cpuPct < busyCPU && memPct < busyMem {
		return nil
	}
	t := topFrom(prev, cur, now.Sub(at).Seconds(), runtime.NumCPU(), procUnitsPerSec)
	if len(t.CPU) == 0 && len(t.Mem) == 0 {
		return nil
	}
	return &t
}
