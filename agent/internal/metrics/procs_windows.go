//go:build windows

package metrics

import (
	"strings"
	"syscall"
	"unsafe"
)

// procUnitsPerSec: process times come in 100 nanosecond units.
const procUnitsPerSec = 1e7

var procGetProcessMemoryInfo = syscall.NewLazyDLL("kernel32.dll").NewProc("K32GetProcessMemoryInfo")

type processMemoryCounters struct {
	Cb, PageFaultCount                                                                                                                                                     uint32
	PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage uintptr
}

const processQueryLimitedInformation = 0x1000

func readProcs() (map[int]procRaw, error) {
	snap, err := syscall.CreateToolhelp32Snapshot(syscall.TH32CS_SNAPPROCESS, 0)
	if err != nil {
		return nil, err
	}
	defer syscall.CloseHandle(snap)
	var pe syscall.ProcessEntry32
	pe.Size = uint32(unsafe.Sizeof(pe))
	out := map[int]procRaw{}
	if err := syscall.Process32First(snap, &pe); err != nil {
		return nil, err
	}
	for {
		pid := int(pe.ProcessID)
		if pid != 0 {
			name := strings.TrimSuffix(syscall.UTF16ToString(pe.ExeFile[:]), ".exe")
			r := procRaw{Name: name}
			if h, err := syscall.OpenProcess(processQueryLimitedInformation, false, pe.ProcessID); err == nil {
				var c, e, k, u syscall.Filetime
				if syscall.GetProcessTimes(h, &c, &e, &k, &u) == nil {
					r.CPU = ft(k) + ft(u)
				}
				m := processMemoryCounters{Cb: uint32(unsafe.Sizeof(processMemoryCounters{}))}
				if x, _, _ := procGetProcessMemoryInfo.Call(uintptr(h), uintptr(unsafe.Pointer(&m)), uintptr(m.Cb)); x != 0 {
					r.Mem = uint64(m.WorkingSetSize)
				}
				syscall.CloseHandle(h)
			}
			out[pid] = r
		}
		if err := syscall.Process32Next(snap, &pe); err != nil {
			break
		}
	}
	return out, nil
}
