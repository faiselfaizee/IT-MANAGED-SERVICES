package collector

import (
	"context"
	"fmt"
	"log"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"sync/atomic"
	"time"

	"github.com/deskask/platform/agent/internal/config"
)

// The collector updates itself like the general agent does: it downloads the new program from the platform over its own authenticated
// connection, checks that it starts, keeps the old one beside it and swaps. If the new one never reaches the platform it is put back.
const (
	pendingFile = "collector-update-pending.txt" // number of starts since the update while the new program has not yet checked in
	doneFile    = "collector-update-done.txt"    // the version last installed, so a wrong version.txt on the server cannot cause a loop
	maxStarts   = 3
)

var updating atomic.Bool

type updateInfo struct {
	Version  string `json:"version"`
	Platform string `json:"platform"`
}

func (r *Runner) selfUpdate(ctx context.Context, version string) {
	if version == "" || version == r.Version || !updating.CompareAndSwap(false, true) {
		return
	}
	defer updating.Store(false)
	if b, err := os.ReadFile(filepath.Join(config.Dir(), doneFile)); err == nil && strings.TrimSpace(string(b)) == version {
		return
	}
	if err := r.applyUpdate(ctx, version); err != nil {
		r.logf("warn", "update to %s failed: %v", version, err)
		return
	}
	r.logf("info", "updated to %s; restarting", version)
	r.flushNow(ctx)
	r.Exit(3) // a non-zero exit makes the service manager start the new program
}

func (r *Runner) applyUpdate(ctx context.Context, version string) error {
	exe, err := os.Executable()
	if err != nil {
		return err
	}
	tmp := exe + ".new"
	f, err := os.OpenFile(tmp, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0o755)
	if err != nil {
		return err
	}
	path := "/collector/v1/binary?platform=" + Platform()
	if runtime.GOARCH == "arm64" {
		path += "&arch=arm64"
	}
	err = r.cl.Download(ctx, path, f)
	f.Close()
	if err != nil {
		os.Remove(tmp)
		return err
	}
	vctx, cancel := context.WithTimeout(ctx, 20*time.Second)
	defer cancel()
	out, err := exec.CommandContext(vctx, tmp, "version").Output() // the new program must at least start
	if err != nil || strings.TrimSpace(string(out)) == "" {
		os.Remove(tmp)
		return fmt.Errorf("the downloaded program does not start")
	}
	old := exe + ".old"
	os.Remove(old)
	if err := os.Rename(exe, old); err != nil {
		os.Remove(tmp)
		return err
	}
	if err := os.Rename(tmp, exe); err != nil {
		_ = os.Rename(old, exe)
		return err
	}
	_ = os.WriteFile(filepath.Join(config.Dir(), pendingFile), []byte("0"), 0o600)
	_ = os.WriteFile(filepath.Join(config.Dir(), doneFile), []byte(version), 0o600)
	return nil
}

// flushNow sends the log lines that are waiting, so "updated to ..." is not lost by the restart (best effort).
func (r *Runner) flushNow(ctx context.Context) {
	cctx, cancel := context.WithTimeout(ctx, 10*time.Second)
	defer cancel()
	_, _ = r.CheckIn(cctx)
}

// checkUpdateGuard runs at start: it counts starts since an update and goes back to the old program when the new one never reached the platform.
func checkUpdateGuard(version string, exit func(int)) {
	p := filepath.Join(config.Dir(), pendingFile)
	b, err := os.ReadFile(p)
	if err != nil {
		return
	}
	n, _ := strconv.Atoi(strings.TrimSpace(string(b)))
	n++
	if n <= maxStarts {
		_ = os.WriteFile(p, []byte(strconv.Itoa(n)), 0o600)
		return
	}
	exe, err := os.Executable()
	if err != nil {
		return
	}
	log.Printf("the updated program (%s) never reached the platform; going back to the previous one", version)
	if _, err := os.Stat(exe + ".old"); err == nil {
		os.Remove(exe + ".bad")
		_ = os.Rename(exe, exe+".bad")
		_ = os.Rename(exe+".old", exe)
	}
	os.Remove(p)
	_ = os.WriteFile(filepath.Join(config.Dir(), doneFile), []byte(version), 0o600) // do not try this version again
	exit(3)
}

// markUpdateHealthy is called after the first successful check-in: the update worked, so the old program is no longer needed.
func markUpdateHealthy() {
	p := filepath.Join(config.Dir(), pendingFile)
	if _, err := os.Stat(p); err != nil {
		return
	}
	os.Remove(p)
	if exe, err := os.Executable(); err == nil {
		os.Remove(exe + ".old")
		os.Remove(exe + ".bad")
	}
}
