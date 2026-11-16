//go:build windows

package filebackup

import (
	"context"
	"fmt"
	"os/exec"
	"path/filepath"
	"strings"
	"syscall"
	"time"
)

func ps(ctx context.Context, script string) (string, error) {
	ctx, cancel := context.WithTimeout(ctx, 3*time.Minute)
	defer cancel()
	cmd := exec.CommandContext(ctx, "powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script)
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
	out, err := cmd.Output()
	return strings.TrimSpace(string(out)), err
}

// Shadow makes a shadow copy of each local drive the paths are on, so files that are open (Outlook data files, databases) can be read
// as they were at one moment. It returns the path mapping, a cleanup that deletes the shadow copies, and warnings for drives that could
// not be copied (their files are then read as they are).
func Shadow(ctx context.Context, paths []string) (func(string) string, func(), []string) {
	devices := map[string]string{}
	var ids, warns []string
	for _, p := range paths {
		v := strings.ToUpper(filepath.VolumeName(p))
		if len(v) != 2 || v[1] != ':' {
			continue // shares and other paths are read directly
		}
		if _, done := devices[v]; done {
			continue
		}
		devices[v] = ""
		out, err := ps(ctx, fmt.Sprintf(`$ErrorActionPreference='Stop'; $s = (Get-WmiObject -List Win32_ShadowCopy).Create('%s\', 'ClientAccessible'); if ($s.ReturnValue -ne 0) { throw "code $($s.ReturnValue)" }; $c = Get-WmiObject Win32_ShadowCopy | Where-Object { $_.ID -eq $s.ShadowID }; "$($c.ID)|$($c.DeviceObject)"`, v))
		parts := strings.SplitN(out, "|", 2)
		if err != nil || len(parts) != 2 || !strings.HasPrefix(parts[1], `\\?\GLOBALROOT`) {
			warns = append(warns, fmt.Sprintf("no shadow copy of %s (open files may be skipped): %v %s", v, err, out))
			delete(devices, v)
			continue
		}
		ids = append(ids, parts[0])
		devices[v] = parts[1]
	}
	cleanup := func() {
		for _, id := range ids {
			ps(context.Background(), fmt.Sprintf(`Get-WmiObject Win32_ShadowCopy | Where-Object { $_.ID -eq '%s' } | ForEach-Object { $_.Delete() }`, strings.ReplaceAll(id, "'", "")))
		}
	}
	read := func(p string) string {
		v := strings.ToUpper(filepath.VolumeName(p))
		if d := devices[v]; d != "" {
			return d + p[len(v):]
		}
		return p
	}
	return read, cleanup, warns
}
