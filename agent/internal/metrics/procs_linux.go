//go:build linux

package metrics

import (
	"os"
	"strconv"
	"strings"
)

// procUnitsPerSec: the kernel counts processor time in clock ticks, 100 a second on every platform the agent runs on.
const procUnitsPerSec = 100.0

func readProcs() (map[int]procRaw, error) {
	ents, err := os.ReadDir("/proc")
	if err != nil {
		return nil, err
	}
	page := uint64(os.Getpagesize())
	out := make(map[int]procRaw, len(ents))
	for _, e := range ents {
		pid, err := strconv.Atoi(e.Name())
		if err != nil {
			continue
		}
		raw, err := os.ReadFile("/proc/" + e.Name() + "/stat")
		if err != nil {
			continue
		}
		name, cpu, ok := parseProcStat(string(raw))
		if !ok {
			continue
		}
		var rss uint64
		if m, err := os.ReadFile("/proc/" + e.Name() + "/statm"); err == nil {
			if f := strings.Fields(string(m)); len(f) > 1 {
				rss, _ = strconv.ParseUint(f[1], 10, 64)
			}
		}
		out[pid] = procRaw{Name: name, CPU: cpu, Mem: rss * page}
	}
	return out, nil
}

// parseProcStat takes the program name (in brackets, and it may contain spaces and brackets itself) and utime + stime from a /proc/<pid>/stat line.
func parseProcStat(s string) (name string, cpu uint64, ok bool) {
	l, r := strings.IndexByte(s, '('), strings.LastIndexByte(s, ')')
	if l < 0 || r < l {
		return "", 0, false
	}
	f := strings.Fields(s[r+1:]) // f[0] is the state, utime and stime are fields 14 and 15 of the whole line = f[11] and f[12]
	if len(f) < 13 {
		return "", 0, false
	}
	u, e1 := strconv.ParseUint(f[11], 10, 64)
	k, e2 := strconv.ParseUint(f[12], 10, 64)
	if e1 != nil || e2 != nil {
		return "", 0, false
	}
	return s[l+1 : r], u + k, true
}
