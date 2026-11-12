//go:build windows

package config

import "os/exec"

// restrict leaves the file to SYSTEM and administrators only.
func restrict(path string) error {
	return exec.Command("icacls", path, "/inheritance:r", "/grant:r", "SYSTEM:F", "Administrators:F").Run()
}
