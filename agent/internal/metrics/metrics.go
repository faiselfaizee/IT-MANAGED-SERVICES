// Package metrics reads how busy a computer is right now: processor use, memory in use, free space on each fixed disk and how long it
// has been running. The agent sends one sample a minute; the Monitoring module turns them into charts and alerts. Only the standard
// library is used (kernel32 on Windows, /proc and statfs on Linux).
package metrics

import (
	"bufio"
	"errors"
	"strconv"
	"strings"
	"sync"
)

// Disk is one fixed volume: C: on Windows, a mount point such as / or /var on Linux.
type Disk struct {
	Mount string `json:"mount"`
	Total uint64 `json:"total"`
	Free  uint64 `json:"free"`
}

// Sample is one reading. CPU is the percentage of processor time in use since the previous reading (-1 on the first one).
type Sample struct {
	CPU      float64 `json:"cpu"`
	MemUsed  uint64  `json:"mem_used"`
	MemTotal uint64  `json:"mem_total"`
	Disks    []Disk  `json:"disks"`
	Uptime   int64   `json:"uptime"`
}

// ErrUnsupported is returned on systems the agent cannot read (macOS for now).
var ErrUnsupported = errors.New("performance data is not read on this system yet")

// Sampler keeps the processor counters between readings, because processor use is a difference over time.
type Sampler struct {
	mu        sync.Mutex
	idle, all uint64
	primed    bool
}

// Take reads one sample.
func (s *Sampler) Take() (Sample, error) {
	idle, all, err := cpuTimes()
	if err != nil {
		return Sample{}, err
	}
	s.mu.Lock()
	pct := -1.0
	if s.primed {
		pct = cpuPct(s.idle, s.all, idle, all)
	}
	s.idle, s.all, s.primed = idle, all, true
	s.mu.Unlock()
	used, total, err := memory()
	if err != nil {
		return Sample{}, err
	}
	disks, err := disks()
	if err != nil {
		return Sample{}, err
	}
	return Sample{CPU: pct, MemUsed: used, MemTotal: total, Disks: disks, Uptime: uptime()}, nil
}

// cpuPct is the share of time not idle between two readings of (idle, total) counters, rounded to one decimal.
func cpuPct(idle0, all0, idle1, all1 uint64) float64 {
	if all1 <= all0 || idle1 < idle0 {
		return 0
	}
	busy := float64((all1-all0)-(idle1-idle0)) / float64(all1-all0) * 100
	if busy < 0 {
		busy = 0
	}
	if busy > 100 {
		busy = 100
	}
	return float64(int64(busy*10+0.5)) / 10
}

// parseStat reads the first "cpu" line of /proc/stat: idle (idle + iowait) and the total of all fields except guest time,
// which the kernel already counts in user time.
func parseStat(text string) (idle, all uint64, err error) {
	sc := bufio.NewScanner(strings.NewReader(text))
	for sc.Scan() {
		f := strings.Fields(sc.Text())
		if len(f) < 5 || f[0] != "cpu" {
			continue
		}
		var v []uint64
		for _, x := range f[1:] {
			n, e := strconv.ParseUint(x, 10, 64)
			if e != nil {
				return 0, 0, e
			}
			v = append(v, n)
		}
		for i, n := range v {
			if i >= 8 { // guest, guest_nice
				break
			}
			all += n
		}
		idle = v[3]
		if len(v) > 4 {
			idle += v[4]
		}
		return idle, all, nil
	}
	return 0, 0, errors.New("no cpu line in /proc/stat")
}

// parseMeminfo returns memory in use (total minus available) and the total, in bytes.
func parseMeminfo(text string) (used, total uint64, err error) {
	var avail, free, buffers, cached uint64
	hasAvail := false
	sc := bufio.NewScanner(strings.NewReader(text))
	for sc.Scan() {
		f := strings.Fields(sc.Text())
		if len(f) < 2 {
			continue
		}
		n, e := strconv.ParseUint(f[1], 10, 64)
		if e != nil {
			continue
		}
		n *= 1024
		switch f[0] {
		case "MemTotal:":
			total = n
		case "MemAvailable:":
			avail, hasAvail = n, true
		case "MemFree:":
			free = n
		case "Buffers:":
			buffers = n
		case "Cached:":
			cached = n
		}
	}
	if total == 0 {
		return 0, 0, errors.New("no MemTotal in /proc/meminfo")
	}
	if !hasAvail { // kernels before 3.14
		avail = free + buffers + cached
	}
	if avail > total {
		avail = total
	}
	return total - avail, total, nil
}

// realFS are the file systems that hold data; tmpfs, overlay, squashfs (snaps) and the like are left out.
var realFS = map[string]bool{"ext2": true, "ext3": true, "ext4": true, "xfs": true, "btrfs": true, "zfs": true, "f2fs": true, "jfs": true, "reiserfs": true, "ntfs": true, "ntfs3": true, "exfat": true}

// parseMounts lists the mount points of real file systems from /proc/mounts, one per device (the first mount of a device wins).
func parseMounts(text string) []string {
	var out []string
	seen := map[string]bool{}
	sc := bufio.NewScanner(strings.NewReader(text))
	for sc.Scan() {
		f := strings.Fields(sc.Text())
		if len(f) < 3 || !realFS[f[2]] || seen[f[0]] {
			continue
		}
		seen[f[0]] = true
		out = append(out, strings.ReplaceAll(f[1], `\040`, " "))
	}
	return out
}
