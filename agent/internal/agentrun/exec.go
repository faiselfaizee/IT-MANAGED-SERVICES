// Package agentrun is the general agent: it enrols with the platform, sends the computer's inventory, takes commands and updates itself.
package agentrun

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strconv"
	"sync"
	"time"

	"github.com/deskask/platform/agent/internal/inventory"
)

// limitedBuffer keeps the first max bytes and says so when it had to cut.
type limitedBuffer struct {
	mu  sync.Mutex
	b   bytes.Buffer
	max int
	cut bool
}

func (l *limitedBuffer) Write(p []byte) (int, error) {
	l.mu.Lock()
	defer l.mu.Unlock()
	room := l.max - l.b.Len()
	if room > 0 {
		if len(p) <= room {
			l.b.Write(p)
		} else {
			l.b.Write(p[:room])
			l.cut = true
		}
	} else {
		l.cut = true
	}
	return len(p), nil
}

func (l *limitedBuffer) String() string {
	l.mu.Lock()
	defer l.mu.Unlock()
	if l.cut {
		return l.b.String() + "\n[output cut]"
	}
	return l.b.String()
}

// Result of a command: status is done, failed, timeout or cancelled, matching what the platform stores for a job.
type Result struct {
	Status string
	Exit   int
	Output string
}

// Exec runs body in the given shell (cmd, powershell or bash) and stops it when ctx ends or the timeout passes.
func Exec(ctx context.Context, shell, body string, timeout time.Duration) Result {
	if timeout <= 0 {
		timeout = 5 * time.Minute
	}
	cctx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	var cmd *exec.Cmd
	var cleanup func()
	switch {
	case runtime.GOOS == "windows" && shell == "powershell":
		pre := "$ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; "
		cmd = exec.CommandContext(cctx, "powershell.exe", inventory.PowerShellArgs(pre+body)...)
	case runtime.GOOS == "windows" && shell == "cmd":
		f, err := script(".cmd", "@echo off\r\n"+body)
		if err != nil {
			return Result{"failed", -1, err.Error()}
		}
		cleanup = func() { os.Remove(f) }
		cmd = exec.CommandContext(cctx, "cmd.exe", "/c", f)
	case runtime.GOOS != "windows" && shell == "bash":
		f, err := script(".sh", body+"\n")
		if err != nil {
			return Result{"failed", -1, err.Error()}
		}
		cleanup = func() { os.Remove(f) }
		cmd = exec.CommandContext(cctx, "bash", f)
	default:
		return Result{"failed", -1, fmt.Sprintf("this computer cannot run %q commands", shell)}
	}
	if cleanup != nil {
		defer cleanup()
	}
	out := &limitedBuffer{max: 1 << 20}
	cmd.Stdout, cmd.Stderr = out, out
	killTree(cmd)
	err := cmd.Run()
	res := Result{Output: out.String()}
	switch {
	case errors.Is(cctx.Err(), context.DeadlineExceeded):
		res.Status, res.Exit = "timeout", -1
		res.Output += fmt.Sprintf("\n[stopped after %s]", timeout)
	case ctx.Err() != nil:
		res.Status, res.Exit = "cancelled", -1
	case err == nil:
		res.Status = "done"
	default:
		var ee *exec.ExitError
		res.Status, res.Exit = "failed", -1
		if errors.As(err, &ee) {
			res.Exit = ee.ExitCode()
		} else {
			res.Output += "\n" + err.Error()
		}
	}
	return res
}

func script(ext, body string) (string, error) {
	f, err := os.CreateTemp("", "deskask-job-*"+ext)
	if err != nil {
		return "", err
	}
	defer f.Close()
	if _, err := f.WriteString(body); err != nil {
		return "", err
	}
	return filepath.Clean(f.Name()), nil
}

// killTree makes cancelling stop the whole process tree, not only the shell.
func killTree(cmd *exec.Cmd) {
	cmd.WaitDelay = 5 * time.Second
	cmd.Cancel = func() error {
		if cmd.Process == nil {
			return nil
		}
		if runtime.GOOS == "windows" {
			return exec.Command("taskkill", "/T", "/F", "/PID", strconv.Itoa(cmd.Process.Pid)).Run()
		}
		return cmd.Process.Kill()
	}
}
