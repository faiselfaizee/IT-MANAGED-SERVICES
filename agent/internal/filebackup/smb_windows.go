//go:build windows

package filebackup

import (
	"fmt"
	"strings"
	"syscall"
	"unsafe"
)

var (
	mpr                    = syscall.NewLazyDLL("mpr.dll")
	procWNetAddConnection2 = mpr.NewProc("WNetAddConnection2W")
)

type netResource struct {
	Scope, Type, DisplayType, Usage          uint32
	LocalName, RemoteName, Comment, Provider *uint16
}

// connectShare signs in to \\server\share with the account (without a drive letter and without putting the password on a command line).
// With no account, the agent's own (the computer account for the service) is used as it is.
func connectShare(path, user, password string) error {
	if user == "" {
		return nil
	}
	p := strings.ReplaceAll(path, "/", `\`)
	parts := strings.SplitN(strings.TrimPrefix(p, `\\`), `\`, 3)
	if len(parts) < 2 || parts[0] == "" || parts[1] == "" {
		return fmt.Errorf("%q is not a share address", path)
	}
	share := `\\` + parts[0] + `\` + parts[1]
	remote, _ := syscall.UTF16PtrFromString(share)
	u, _ := syscall.UTF16PtrFromString(user)
	pw, _ := syscall.UTF16PtrFromString(password)
	nr := netResource{Type: 1 /* RESOURCETYPE_DISK */, RemoteName: remote}
	r, _, _ := procWNetAddConnection2.Call(uintptr(unsafe.Pointer(&nr)), uintptr(unsafe.Pointer(pw)), uintptr(unsafe.Pointer(u)), 0)
	switch r {
	case 0, 1219: // done; or already connected with the same or other credentials (ERROR_SESSION_CREDENTIAL_CONFLICT): use that connection
		return nil
	case 5, 86, 1326:
		return fmt.Errorf("the share %s refused the account %s (wrong user name or password, or no permission)", share, user)
	case 53, 67:
		return fmt.Errorf("the share %s was not found (check the server name and share name, and that the server is reachable on port 445)", share)
	}
	return fmt.Errorf("could not connect to %s: Windows error %d", share, r)
}
