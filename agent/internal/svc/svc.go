// Package svc installs the agent or the collector so it starts with the computer and restarts if it stops:
// a scheduled task running as SYSTEM on Windows, a systemd service on Linux.
package svc

import (
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"

	"github.com/deskask/platform/agent/internal/inventory"
)

type Spec struct {
	Task string // Windows scheduled task name, e.g. DeskAskAgent
	Unit string // systemd unit name, e.g. deskask-agent
	Dir  string // install folder name inside Program Files or /opt
	File string // the program's file name without .exe
	Args string // what the service runs: "run" or "collector-run"
	Desc string
}

var Agent = Spec{Task: "DeskAskAgent", Unit: "deskask-agent", Dir: "DeskAskAgent", File: "deskask-agent", Args: "run", Desc: "DeskAsk agent"}
var Collector = Spec{Task: "DeskAskCollector", Unit: "deskask-collector", Dir: "DeskAskCollector", File: "deskask-collector", Args: "collector-run", Desc: "DeskAsk site collector"}

// ExePath is where the program lives once installed.
func (s Spec) ExePath() string {
	if runtime.GOOS == "windows" {
		pf := os.Getenv("ProgramFiles")
		if pf == "" {
			pf = `C:\Program Files`
		}
		return filepath.Join(pf, s.Dir, s.File+".exe")
	}
	return filepath.Join("/opt", s.Unit, s.File)
}

// CopySelf puts the running program at its install path (when it is not already there) and returns that path.
func (s Spec) CopySelf() (string, error) {
	self, err := os.Executable()
	if err != nil {
		return "", err
	}
	target := s.ExePath()
	if a, _ := filepath.Abs(self); strings.EqualFold(a, target) {
		return target, nil
	}
	if err := os.MkdirAll(filepath.Dir(target), 0o755); err != nil {
		return "", err
	}
	in, err := os.Open(self)
	if err != nil {
		return "", err
	}
	defer in.Close()
	tmp := target + ".new"
	out, err := os.OpenFile(tmp, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0o755)
	if err != nil {
		return "", err
	}
	if _, err := io.Copy(out, in); err != nil {
		out.Close()
		return "", err
	}
	out.Close()
	_ = os.Remove(target + ".old")
	if _, err := os.Stat(target); err == nil {
		_ = os.Rename(target, target+".old") // a running program cannot be overwritten on Windows, but it can be renamed
	}
	return target, os.Rename(tmp, target)
}

func (s Spec) windowsInstallScript(exe string) string {
	return fmt.Sprintf(`$ErrorActionPreference = 'Stop'
$a = New-ScheduledTaskAction -Execute '%s' -Argument '%s'
$t = New-ScheduledTaskTrigger -AtStartup
$p = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -RunLevel Highest -LogonType ServiceAccount
$s = New-ScheduledTaskSettingsSet -StartWhenAvailable -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName '%s' -Description '%s' -Action $a -Trigger $t -Principal $p -Settings $s -Force | Out-Null
Start-ScheduledTask -TaskName '%s'
`, strings.ReplaceAll(exe, "'", "''"), s.Args, s.Task, s.Desc, s.Task)
}

func (s Spec) unitFile(exe string) string {
	return fmt.Sprintf(`[Unit]
Description=%s
After=network-online.target
Wants=network-online.target

[Service]
ExecStart=%s %s
Restart=always
RestartSec=10

[Install]
WantedBy=multi-user.target
`, s.Desc, exe, s.Args)
}

// Install registers and starts the service for the program at exe.
func (s Spec) Install(exe string) error {
	if runtime.GOOS == "windows" {
		if out, err := exec.Command("powershell.exe", inventory.PowerShellArgs(s.windowsInstallScript(exe))...).CombinedOutput(); err != nil {
			return fmt.Errorf("could not create the scheduled task: %v: %s", err, strings.TrimSpace(string(out)))
		}
		return nil
	}
	if _, err := exec.LookPath("systemctl"); err != nil {
		return fmt.Errorf("systemd was not found; start `%s %s` yourself from your service manager", exe, s.Args)
	}
	if err := os.WriteFile("/etc/systemd/system/"+s.Unit+".service", []byte(s.unitFile(exe)), 0o644); err != nil {
		return err
	}
	for _, args := range [][]string{{"daemon-reload"}, {"enable", s.Unit}, {"restart", s.Unit}} {
		if out, err := exec.Command("systemctl", args...).CombinedOutput(); err != nil {
			return fmt.Errorf("systemctl %s: %v: %s", strings.Join(args, " "), err, strings.TrimSpace(string(out)))
		}
	}
	return nil
}

// Uninstall stops and removes the service. The program file is removed after this process ends.
func (s Spec) Uninstall() error {
	if runtime.GOOS == "windows" {
		script := fmt.Sprintf("Stop-ScheduledTask -TaskName '%s' -ErrorAction SilentlyContinue; Unregister-ScheduledTask -TaskName '%s' -Confirm:$false -ErrorAction SilentlyContinue", s.Task, s.Task)
		_ = exec.Command("powershell.exe", inventory.PowerShellArgs(script)...).Run()
		exe := s.ExePath()
		// delete the program a few seconds later, when this process has gone
		return exec.Command("cmd.exe", "/c", fmt.Sprintf(`start "" /b cmd /c "ping -n 5 127.0.0.1 >nul & del /f /q "%s" "%s.old" & rmdir "%s""`, exe, exe, filepath.Dir(exe))).Start()
	}
	_ = exec.Command("systemctl", "disable", "--now", s.Unit).Run()
	_ = os.Remove("/etc/systemd/system/" + s.Unit + ".service")
	_ = exec.Command("systemctl", "daemon-reload").Run()
	_ = os.RemoveAll(filepath.Dir(s.ExePath()))
	return nil
}
