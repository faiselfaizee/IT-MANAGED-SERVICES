//go:build windows

package backup

import (
	"context"
	"os/exec"
	"syscall"
	"time"
)

// Collect runs the backup script (at most three minutes; Get-DPMJob can be slow on a busy server).
func Collect(ctx context.Context) (Found, error) {
	ctx, cancel := context.WithTimeout(ctx, 3*time.Minute)
	defer cancel()
	cmd := exec.CommandContext(ctx, "powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Encoded())
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
	out, err := cmd.Output() // standard output only: module warnings go to standard error
	if err != nil && len(out) == 0 {
		return Found{}, err
	}
	return Parse(out)
}
