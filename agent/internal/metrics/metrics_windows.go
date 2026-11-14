//go:build windows

package metrics

import (
	"errors"
	"syscall"
	"unsafe"
)

var (
	kernel32             = syscall.NewLazyDLL("kernel32.dll")
	procGetSystemTimes   = kernel32.NewProc("GetSystemTimes")
	procGlobalMemStatus  = kernel32.NewProc("GlobalMemoryStatusEx")
	procGetLogicalDrives = kernel32.NewProc("GetLogicalDrives")
	procGetDriveType     = kernel32.NewProc("GetDriveTypeW")
	procGetDiskFreeSpace = kernel32.NewProc("GetDiskFreeSpaceExW")
	procGetTickCount64   = kernel32.NewProc("GetTickCount64")
)

func ft(f syscall.Filetime) uint64 { return uint64(f.HighDateTime)<<32 | uint64(f.LowDateTime) }

// cpuTimes: kernel time already includes idle time, so the total is kernel + user.
func cpuTimes() (idle, all uint64, err error) {
	var i, k, u syscall.Filetime
	r, _, e := procGetSystemTimes.Call(uintptr(unsafe.Pointer(&i)), uintptr(unsafe.Pointer(&k)), uintptr(unsafe.Pointer(&u)))
	if r == 0 {
		return 0, 0, e
	}
	return ft(i), ft(k) + ft(u), nil
}

type memStatusEx struct {
	Length, MemoryLoad                     uint32
	TotalPhys, AvailPhys                   uint64
	TotalPageFile, AvailPageFile           uint64
	TotalVirtual, AvailVirtual, AvailExtVi uint64
}

func memory() (used, total uint64, err error) {
	m := memStatusEx{Length: uint32(unsafe.Sizeof(memStatusEx{}))}
	r, _, e := procGlobalMemStatus.Call(uintptr(unsafe.Pointer(&m)))
	if r == 0 {
		return 0, 0, e
	}
	if m.AvailPhys > m.TotalPhys {
		return 0, m.TotalPhys, nil
	}
	return m.TotalPhys - m.AvailPhys, m.TotalPhys, nil
}

const driveFixed = 3

func disks() ([]Disk, error) {
	mask, _, _ := procGetLogicalDrives.Call()
	if mask == 0 {
		return nil, errors.New("no drives found")
	}
	out := []Disk{}
	for i := 0; i < 26; i++ {
		if mask&(1<<uint(i)) == 0 {
			continue
		}
		root := string(rune('A'+i)) + `:\`
		p, err := syscall.UTF16PtrFromString(root)
		if err != nil {
			continue
		}
		if t, _, _ := procGetDriveType.Call(uintptr(unsafe.Pointer(p))); t != driveFixed {
			continue
		}
		var avail, total, free uint64
		if r, _, _ := procGetDiskFreeSpace.Call(uintptr(unsafe.Pointer(p)), uintptr(unsafe.Pointer(&avail)), uintptr(unsafe.Pointer(&total)), uintptr(unsafe.Pointer(&free))); r == 0 || total == 0 {
			continue
		}
		out = append(out, Disk{Mount: root[:2], Total: total, Free: avail})
	}
	return out, nil
}

func uptime() int64 {
	r, _, _ := procGetTickCount64.Call()
	return int64(r / 1000)
}
