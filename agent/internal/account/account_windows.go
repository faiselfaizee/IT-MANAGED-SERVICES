//go:build windows

package account

import (
	"errors"
	"fmt"
	"syscall"
	"unsafe"
)

var (
	netapi32           = syscall.NewLazyDLL("netapi32.dll")
	procNetUserSetInfo = netapi32.NewProc("NetUserSetInfo")
)

// userInfo1003 is USER_INFO_1003: only the password.
type userInfo1003 struct{ password *uint16 }

func isDomainController() bool {
	var k syscall.Handle
	path, _ := syscall.UTF16PtrFromString(`SYSTEM\CurrentControlSet\Control\ProductOptions`)
	if syscall.RegOpenKeyEx(syscall.HKEY_LOCAL_MACHINE, path, 0, syscall.KEY_READ, &k) != nil {
		return false
	}
	defer syscall.RegCloseKey(k)
	name, _ := syscall.UTF16PtrFromString("ProductType")
	buf := make([]uint16, 64)
	n := uint32(len(buf) * 2)
	var typ uint32
	if syscall.RegQueryValueEx(k, name, nil, &typ, (*byte)(unsafe.Pointer(&buf[0])), &n) != nil {
		return false
	}
	return syscall.UTF16ToString(buf) == "LanmanNt"
}

func setPassword(acct, target, password string) error {
	switch target {
	case "local":
		if isDomainController() {
			return errors.New("a domain controller has no local accounts; choose a domain account")
		}
	case "domain":
		if !isDomainController() {
			return ErrNotDC
		}
	default:
		return fmt.Errorf("a Windows computer cannot change a %q account", target)
	}
	u, err := syscall.UTF16PtrFromString(acct)
	if err != nil {
		return err
	}
	p, err := syscall.UTF16PtrFromString(password)
	if err != nil {
		return err
	}
	info := userInfo1003{password: p}
	var parmErr uint32
	rc, _, _ := procNetUserSetInfo.Call(0, uintptr(unsafe.Pointer(u)), 1003, uintptr(unsafe.Pointer(&info)), uintptr(unsafe.Pointer(&parmErr)))
	// wipe the copy we made
	b := unsafe.Slice(p, len(password)+1)
	for i := range b {
		b[i] = 0
	}
	switch rc {
	case 0:
		return nil
	case 2221:
		return errors.New("there is no such account")
	case 2245:
		return errors.New("the new password does not meet the password policy (length, complexity or history)")
	case 5:
		return errors.New("access denied: the agent's service account may not change this password")
	default:
		return fmt.Errorf("Windows could not change the password (NetUserSetInfo error %d)", rc)
	}
}
