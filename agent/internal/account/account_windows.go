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

	advapi32                 = syscall.NewLazyDLL("advapi32.dll")
	procOpenSCManagerW       = advapi32.NewProc("OpenSCManagerW")
	procOpenServiceW         = advapi32.NewProc("OpenServiceW")
	procChangeServiceConfigW = advapi32.NewProc("ChangeServiceConfigW")
	procCloseServiceHandle   = advapi32.NewProc("CloseServiceHandle")
	procLsaOpenPolicy        = advapi32.NewProc("LsaOpenPolicy")
	procLsaAddAccountRights  = advapi32.NewProc("LsaAddAccountRights")
	procLsaClose             = advapi32.NewProc("LsaClose")
	procLsaNtStatusToWinErr  = advapi32.NewProc("LsaNtStatusToWinError")
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

const (
	scManagerConnect     = 0x0001
	serviceQueryConfig   = 0x0001
	serviceChangeConfig  = 0x0002
	serviceNoChange      = 0xffffffff
	policyCreateAccount  = 0x00000010
	policyLookupNames    = 0x00000800
)

type lsaUnicodeString struct {
	Length        uint16
	MaximumLength uint16
	Buffer        *uint16
}

type lsaObjectAttributes struct {
	Length                   uint32
	RootDirectory            uintptr
	ObjectName               uintptr
	Attributes               uint32
	SecurityDescriptor       uintptr
	SecurityQualityOfService uintptr
}

func lsaString(s string) (lsaUnicodeString, error) {
	p, err := syscall.UTF16FromString(s)
	if err != nil {
		return lsaUnicodeString{}, err
	}
	n := uint16((len(p) - 1) * 2)
	return lsaUnicodeString{Length: n, MaximumLength: n + 2, Buffer: &p[0]}, nil
}

// grantServiceLogon gives the account SeServiceLogonRight, as the Services console does when an account is chosen.
func grantServiceLogon(acct string) error {
	lookup := acct
	if len(lookup) > 2 && lookup[:2] == ".\\" {
		lookup = lookup[2:]
	}
	sid, _, _, err := syscall.LookupSID("", lookup)
	if err != nil {
		return fmt.Errorf("Windows does not know the account %s", acct)
	}
	attrs := lsaObjectAttributes{Length: uint32(unsafe.Sizeof(lsaObjectAttributes{}))}
	var policy uintptr
	if st, _, _ := procLsaOpenPolicy.Call(0, uintptr(unsafe.Pointer(&attrs)), policyCreateAccount|policyLookupNames, uintptr(unsafe.Pointer(&policy))); st != 0 {
		code, _, _ := procLsaNtStatusToWinErr.Call(st)
		return fmt.Errorf("could not open the security policy to grant \"Log on as a service\" (error %d)", code)
	}
	defer procLsaClose.Call(policy)
	right, err := lsaString("SeServiceLogonRight")
	if err != nil {
		return err
	}
	if st, _, _ := procLsaAddAccountRights.Call(policy, uintptr(unsafe.Pointer(sid)), uintptr(unsafe.Pointer(&right)), 1); st != 0 {
		code, _, _ := procLsaNtStatusToWinErr.Call(st)
		return fmt.Errorf("could not grant \"Log on as a service\" to %s (error %d)", acct, code)
	}
	return nil
}

func setServiceLogon(service, acct, password string) error {
	if err := grantServiceLogon(acct); err != nil {
		return err
	}
	scm, _, e := procOpenSCManagerW.Call(0, 0, scManagerConnect)
	if scm == 0 {
		return fmt.Errorf("could not open the service manager: %v", e)
	}
	defer procCloseServiceHandle.Call(scm)
	name, err := syscall.UTF16PtrFromString(service)
	if err != nil {
		return err
	}
	h, _, e := procOpenServiceW.Call(scm, uintptr(unsafe.Pointer(name)), serviceQueryConfig|serviceChangeConfig)
	if h == 0 {
		if errno, ok := e.(syscall.Errno); ok && errno == 1060 {
			return fmt.Errorf("there is no service named %s on this computer", service)
		}
		return fmt.Errorf("could not open the service %s: %v", service, e)
	}
	defer procCloseServiceHandle.Call(h)
	a, err := syscall.UTF16PtrFromString(acct)
	if err != nil {
		return err
	}
	p, err := syscall.UTF16PtrFromString(password)
	if err != nil {
		return err
	}
	ok, _, e := procChangeServiceConfigW.Call(h, serviceNoChange, serviceNoChange, serviceNoChange, 0, 0, 0, 0, uintptr(unsafe.Pointer(a)), uintptr(unsafe.Pointer(p)), 0)
	b := unsafe.Slice(p, len(password)+1)
	for i := range b {
		b[i] = 0
	}
	if ok == 0 {
		switch errno, _ := e.(syscall.Errno); errno {
		case 1057:
			return errors.New("the account name is not valid or does not exist")
		case 5:
			return errors.New("access denied: the agent's service account may not change this service")
		default:
			return fmt.Errorf("Windows could not change the account of %s: %v", service, e)
		}
	}
	return nil
}
