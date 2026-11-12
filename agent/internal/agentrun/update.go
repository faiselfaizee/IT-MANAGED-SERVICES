package agentrun

import (
	"context"
	"fmt"
	"log"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"sync/atomic"
	"time"

	"github.com/deskask/platform/agent/internal/config"
)

// The agent updates itself from the platform it belongs to, over its own authenticated connection: it downloads the new program, checks that
// it starts, keeps the old one beside it, and swaps. If the new one cannot reach the platform after a few starts, the old one is put back.

const (
	pendingFile = "update-pending.txt" // "<attempts>" while a new program has not yet contacted the platform
	doneFile    = "update-done.txt"    // the version last installed, so a mismatching version.txt on the server cannot cause an update loop
	maxStarts   = 3
)

var updating atomic.Bool

func (r *Runner) update(ctx context.Context, version string) {
	if version == "" || version == r.Version || !updating.CompareAndSwap(false, true) {
		return
	}
	defer updating.Store(false)
	var done struct{ V string }
	if b, err := os.ReadFile(filepath.Join(config.Dir(), doneFile)); err == nil {
		done.V = strings.TrimSpace(string(b))
	}
	if done.V == version {
		return
	}
	if err := r.applyUpdate(ctx, version); err != nil {
		log.Printf("update to %s failed: %v", version, err)
		return
	}
	log.Printf("updated to %s; restarting", version)
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
	err = r.cl.Download(ctx, "/agent/v1/binary", f)
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

// checkUpdateGuard runs at start: it counts starts since an update and rolls back when the new program never reached the platform.
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
