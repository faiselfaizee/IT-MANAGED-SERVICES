//go:build !linux && !windows

package metrics

func cpuTimes() (idle, all uint64, err error) { return 0, 0, ErrUnsupported }
func memory() (used, total uint64, err error) { return 0, 0, ErrUnsupported }
func disks() ([]Disk, error)                  { return nil, ErrUnsupported }
func uptime() int64                           { return 0 }
