//go:build linux

package metrics

import (
	"os"
	"strconv"
	"strings"
	"syscall"
)

func cpuTimes() (idle, all uint64, err error) {
	b, err := os.ReadFile("/proc/stat")
	if err != nil {
		return 0, 0, err
	}
	return parseStat(string(b))
}

func memory() (used, total uint64, err error) {
	b, err := os.ReadFile("/proc/meminfo")
	if err != nil {
		return 0, 0, err
	}
	return parseMeminfo(string(b))
}

func disks() ([]Disk, error) {
	b, err := os.ReadFile("/proc/mounts")
	if err != nil {
		return nil, err
	}
	out := []Disk{}
	for _, m := range parseMounts(string(b)) {
		var st syscall.Statfs_t
		if syscall.Statfs(m, &st) != nil || st.Blocks == 0 {
			continue
		}
		bs := uint64(st.Bsize)
		out = append(out, Disk{Mount: m, Total: st.Blocks * bs, Free: st.Bavail * bs})
	}
	return out, nil
}

func uptime() int64 {
	b, err := os.ReadFile("/proc/uptime")
	if err != nil {
		return 0
	}
	f := strings.Fields(string(b))
	if len(f) == 0 {
		return 0
	}
	v, _ := strconv.ParseFloat(f[0], 64)
	return int64(v)
}
