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
	"sync"
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

// after a failed automatic attempt the agent waits before trying the same version again, instead of downloading it on every poll
var (
	failMu   sync.Mutex
	failedAt = map[string]time.Time{}
)

const retryAfter = 10 * time.Minute

// update installs version. force comes from "Update agent" on the asset page: it also retries a version that was put back
// before, and every step is reported so the person who pressed the button sees where it is.
func (r *Runner) update(ctx context.Context, version string, force bool) {
	say := func(level, format string, a ...any) {
		msg := fmt.Sprintf(format, a...)
		log.Print(msg)
		if force {
			r.updPost(ctx, map[string]any{"level": level, "msg": msg})
		}
	}
	if version == "" || version == r.Version {
		if force && version != "" {
			say("info", "This agent already runs %s", r.Version)
		}
		return
	}
	var done string
	if b, err := os.ReadFile(filepath.Join(config.Dir(), doneFile)); err == nil {
		done = strings.TrimSpace(string(b))
	}
	if done == version && !force {
		return // tried before and put back: only a person can ask for it again
	}
	failMu.Lock()
	last := failedAt[version]
	failMu.Unlock()
	if !force && time.Since(last) < retryAfter {
		return
	}
	if !updating.CompareAndSwap(false, true) {
		if force {
			say("info", "An update is already running on this computer")
		}
		return
	}
	defer updating.Store(false)
	if done == version {
		say("info", "Version %s was installed here before and put back because it did not reach the platform; trying again as asked", version)
	}
	say("info", "The agent (version %s) picked up the request: updating to %s", r.Version, version)
	if err := r.applyUpdate(ctx, version, say); err != nil {
		failMu.Lock()
		failedAt[version] = time.Now()
		failMu.Unlock()
		say("warn", "Update to %s failed: %v", version, err)
		if force {
			r.updPost(ctx, map[string]any{"state": "failed"})
		}
		return
	}
	say("info", "New program in place. Restarting the agent; it is back within about 15 seconds")
	r.restart(3)
}

// restart ends the program and makes sure it starts again: Windows' scheduled task does not restart a program that ended.
func (r *Runner) restart(code int) {
	if r.RestartSoon != nil {
		if err := r.RestartSoon(); err != nil {
			log.Printf("could not schedule the restart (the five-minute watchdog will start the agent): %v", err)
		}
	}
	r.Exit(code)
}

func (r *Runner) updPost(ctx context.Context, body map[string]any) {
	pctx, cancel := context.WithTimeout(ctx, 15*time.Second)
	defer cancel()
	_ = r.cl.Do(pctx, "POST", "/agent/v1/update-log", body, nil)
}

func (r *Runner) applyUpdate(ctx context.Context, version string, say func(level, format string, a ...any)) error {
	exe, err := os.Executable()
	if err != nil {
		return err
	}
	tmp := exe + ".new"
	f, err := os.OpenFile(tmp, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0o755)
	if err != nil {
		return err
	}
	say("info", "Downloading the new program from the platform")
	err = r.cl.Download(ctx, "/agent/v1/binary", f)
	size := int64(0)
	if st, e := f.Stat(); e == nil {
		size = st.Size()
	}
	f.Close()
	if err != nil {
		os.Remove(tmp)
		return fmt.Errorf("download: %v", err)
	}
	say("info", "Downloaded %.1f MB; checking that the new program starts", float64(size)/1048576)
	vctx, cancel := context.WithTimeout(ctx, 20*time.Second)
	defer cancel()
	out, err := exec.CommandContext(vctx, tmp, "version").Output() // the new program must at least start
	if err != nil || strings.TrimSpace(string(out)) == "" {
		os.Remove(tmp)
		return fmt.Errorf("the downloaded program does not start (%v); an antivirus may be blocking it", err)
	}
	say("info", "The new program starts and reports %s", strings.TrimSpace(string(out)))
	old := exe + ".old"
	os.Remove(old)
	if err := os.Rename(exe, old); err != nil {
		os.Remove(tmp)
		return fmt.Errorf("could not move the running program aside: %v", err)
	}
	if err := os.Rename(tmp, exe); err != nil {
		_ = os.Rename(old, exe)
		return fmt.Errorf("could not put the new program in place: %v", err)
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
