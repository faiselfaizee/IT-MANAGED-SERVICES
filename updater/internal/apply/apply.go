// Package apply puts a verified bundle in place of the running program and puts the old one back when the new one is not healthy.
package apply

import (
	"context"
	"crypto/ecdsa"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/deskask/platform/updater/internal/bundle"
)

const VersionFile = ".deskask-version"

var (
	ErrRolledBack     = errors.New("the new version did not start properly; the previous version is running again")
	ErrRollbackFailed = errors.New("the new version failed and the previous version could not be restarted: needs attention")
	ErrNotNewer       = errors.New("the bundle is not newer than the installed version")
)

// Runner controls the service. Production uses shell commands and an HTTP health address; tests use a fake.
type Runner interface {
	Stop(ctx context.Context) error
	Start(ctx context.Context) error
	Healthy(ctx context.Context) error
}

type Env struct {
	Root          string   // holds live/, previous/, staging/, state/
	Keep          []string // paths inside live/ carried over to the new version (settings, data), forward slashes
	Runner        Runner
	HealthTimeout time.Duration
	HealthEvery   time.Duration
	Log           func(string)
}

type Options struct {
	Force          bool // install even when not newer (re-apply)
	AllowDowngrade bool // with Force: go to an older version
}

type Result struct {
	From, To   string
	RolledBack bool
}

func (e *Env) log(f string, a ...any) {
	if e.Log != nil {
		e.Log(fmt.Sprintf(f, a...))
	}
}
func (e *Env) live() string { return filepath.Join(e.Root, "live") }
func (e *Env) prev() string { return filepath.Join(e.Root, "previous") }

func Installed(root string) string {
	b, err := os.ReadFile(filepath.Join(root, "live", VersionFile))
	if err != nil {
		return ""
	}
	return strings.TrimSpace(string(b))
}

func copyTree(src, dst string) error {
	info, err := os.Lstat(src)
	if err != nil {
		return err
	}
	if info.Mode()&os.ModeSymlink != 0 {
		return fmt.Errorf("%s is a link; links are not carried over", src)
	}
	if info.IsDir() {
		if err := os.MkdirAll(dst, info.Mode().Perm()|0o700); err != nil {
			return err
		}
		ents, err := os.ReadDir(src)
		if err != nil {
			return err
		}
		for _, d := range ents {
			if err := copyTree(filepath.Join(src, d.Name()), filepath.Join(dst, d.Name())); err != nil {
				return err
			}
		}
		return nil
	}
	data, err := os.ReadFile(src)
	if err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(dst), 0o755); err != nil {
		return err
	}
	return os.WriteFile(dst, data, info.Mode().Perm())
}

// Apply installs the bundle. The signature and every hash are checked before the service is touched.
func Apply(ctx context.Context, env *Env, bundlePath string, pubs []*ecdsa.PublicKey, opt Options) (Result, error) {
	b, err := bundle.Open(bundlePath, pubs)
	if err != nil {
		return Result{}, err
	}
	defer b.Close()
	res := Result{From: Installed(env.Root), To: b.Manifest.Version}
	if res.From != "" {
		c, err := CompareVersions(b.Manifest.Version, res.From)
		if err != nil {
			return res, err
		}
		if c <= 0 && !opt.Force {
			return res, fmt.Errorf("%w (%s installed, %s offered)", ErrNotNewer, res.From, b.Manifest.Version)
		}
		if c < 0 && !opt.AllowDowngrade {
			return res, fmt.Errorf("%w: going back to an older version needs the downgrade option", ErrNotNewer)
		}
	}
	env.log("bundle %s %s (%s) verified, key %s, %d files", b.Manifest.Component, b.Manifest.Version, bundlePath, b.Manifest.KeyID, len(b.Manifest.Files))

	staging := filepath.Join(env.Root, "staging", fmt.Sprintf("%s-%d", b.Manifest.Version, time.Now().UnixNano()))
	if err := os.MkdirAll(staging, 0o755); err != nil {
		return res, err
	}
	cleanup := func() { _ = os.RemoveAll(filepath.Dir(staging)) }
	if err := b.Extract(staging); err != nil {
		cleanup()
		return res, err
	}
	for _, k := range env.Keep {
		if !bundle.SafePath(k) {
			cleanup()
			return res, fmt.Errorf("keep path %q is not allowed", k)
		}
		from := filepath.Join(env.live(), filepath.FromSlash(k))
		if _, err := os.Lstat(from); err != nil {
			continue
		}
		_ = os.RemoveAll(filepath.Join(staging, filepath.FromSlash(k)))
		if err := copyTree(from, filepath.Join(staging, filepath.FromSlash(k))); err != nil {
			cleanup()
			return res, fmt.Errorf("could not carry over %s: %w", k, err)
		}
		env.log("kept %s", k)
	}
	if err := os.WriteFile(filepath.Join(staging, VersionFile), []byte(b.Manifest.Version+"\n"), 0o644); err != nil {
		cleanup()
		return res, err
	}

	// ---- the swap
	env.log("stopping the service")
	if err := env.Runner.Stop(ctx); err != nil {
		cleanup()
		return res, fmt.Errorf("could not stop the service: %w", err)
	}
	hadLive := false
	if _, err := os.Stat(env.live()); err == nil {
		hadLive = true
		_ = os.RemoveAll(env.prev())
		if err := os.Rename(env.live(), env.prev()); err != nil {
			_ = env.Runner.Start(ctx)
			cleanup()
			return res, fmt.Errorf("could not move the current version aside: %w", err)
		}
	}
	if err := os.Rename(staging, env.live()); err != nil {
		if hadLive {
			_ = os.Rename(env.prev(), env.live())
		}
		_ = env.Runner.Start(ctx)
		cleanup()
		return res, fmt.Errorf("could not put the new version in place: %w", err)
	}
	cleanup()
	env.log("starting %s", b.Manifest.Version)
	problem := env.Runner.Start(ctx)
	if problem == nil {
		problem = env.waitHealthy(ctx)
	}
	if problem != nil {
		env.log("the new version is not healthy (%v): going back", problem)
		res.RolledBack = true
		_ = env.Runner.Stop(ctx)
		failed := filepath.Join(env.Root, "failed")
		_ = os.RemoveAll(failed)
		_ = os.Rename(env.live(), failed)
		if hadLive {
			if err := os.Rename(env.prev(), env.live()); err != nil {
				return res, fmt.Errorf("%w: %v", ErrRollbackFailed, err)
			}
			if err := env.Runner.Start(ctx); err != nil {
				return res, fmt.Errorf("%w: %v", ErrRollbackFailed, err)
			}
			if err := env.waitHealthy(ctx); err != nil {   // the old version must answer again before we claim it does
				return res, fmt.Errorf("%w: %v", ErrRollbackFailed, err)
			}
			env.log("the previous version %s is running again", res.From)
		}
		return res, fmt.Errorf("%w: %v", ErrRolledBack, problem)
	}
	env.log("%s is running", b.Manifest.Version)
	return res, nil
}

func (e *Env) waitHealthy(ctx context.Context) error {
	to, every := e.HealthTimeout, e.HealthEvery
	if to <= 0 {
		to = 90 * time.Second
	}
	if every <= 0 {
		every = time.Second
	}
	deadline := time.Now().Add(to)
	var last error
	for {
		if last = e.Runner.Healthy(ctx); last == nil {
			return nil
		}
		if time.Now().After(deadline) {
			return fmt.Errorf("no healthy answer within %s: %w", to, last)
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(every):
		}
	}
}

// Rollback swaps live and previous by hand (the person at the server decides the new version is wrong after all).
func Rollback(ctx context.Context, env *Env) (string, error) {
	if _, err := os.Stat(env.prev()); err != nil {
		return "", errors.New("there is no previous version to go back to")
	}
	if err := env.Runner.Stop(ctx); err != nil {
		return "", err
	}
	tmp := filepath.Join(env.Root, "swap")
	_ = os.RemoveAll(tmp)
	if err := os.Rename(env.live(), tmp); err != nil {
		_ = env.Runner.Start(ctx)
		return "", err
	}
	if err := os.Rename(env.prev(), env.live()); err != nil {
		_ = os.Rename(tmp, env.live())
		_ = env.Runner.Start(ctx)
		return "", err
	}
	_ = os.Rename(tmp, env.prev())
	if err := env.Runner.Start(ctx); err != nil {
		return "", fmt.Errorf("%w: %v", ErrRollbackFailed, err)
	}
	if err := env.waitHealthy(ctx); err != nil {
		return "", fmt.Errorf("%w: %v", ErrRollbackFailed, err)
	}
	return Installed(env.Root), nil
}
