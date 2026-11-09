// Package jobs is the folder the platform and the updater share: the platform drops a bundle in inbox/<id>/ (job.json last),
// the updater applies it and writes state/jobs/<id>.json and state/status.json. The updater never trusts the platform's
// word about a bundle: it verifies the signature itself.
package jobs

import (
	"context"
	"crypto/ecdsa"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/deskask/platform/updater/internal/apply"
)

var idRx = regexp.MustCompile(`^[a-f0-9]{16,40}$`)

type Job struct {
	ID        string   `json:"id"`
	File      string   `json:"file"`
	By        string   `json:"by"`
	Requested int64    `json:"requested"`
	Force     bool     `json:"force,omitempty"`
	Started   int64    `json:"started,omitempty"`
	Finished  int64    `json:"finished,omitempty"`
	Status    string   `json:"status"` // running | done | rolled_back | failed
	From      string   `json:"from,omitempty"`
	To        string   `json:"to,omitempty"`
	Error     string   `json:"error,omitempty"`
	Log       []string `json:"log,omitempty"`
}

type Status struct {
	Updated int64  `json:"updated"`
	Current string `json:"current"`
	Product string `json:"product"`
	Running string `json:"running,omitempty"`
}

func writeJSON(path string, v any) error {
	b, err := json.MarshalIndent(v, "", "  ")
	if err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return err
	}
	tmp := path + ".part"
	if err := os.WriteFile(tmp, b, 0o644); err != nil {
		return err
	}
	return os.Rename(tmp, path)
}

func WriteStatus(root, running string) error {
	return writeJSON(filepath.Join(root, "state", "status.json"), Status{Updated: time.Now().UnixMilli(), Current: apply.Installed(root), Product: "deskask-platform", Running: running})
}

type Runner struct {
	Env   *apply.Env
	Pubs  []*ecdsa.PublicKey
	Allow bool // allow downgrades when a job asks for force
	mu    sync.Mutex
}

// ProcessOne takes the oldest waiting job. It returns false when the inbox is empty.
func (r *Runner) ProcessOne(ctx context.Context) (bool, error) {
	r.mu.Lock()
	defer r.mu.Unlock()
	root := r.Env.Root
	inbox := filepath.Join(root, "inbox")
	ents, err := os.ReadDir(inbox)
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return false, nil
		}
		return false, err
	}
	type cand struct {
		dir string
		job Job
	}
	var list []cand
	for _, d := range ents {
		if !d.IsDir() || !idRx.MatchString(d.Name()) {
			continue
		}
		b, err := os.ReadFile(filepath.Join(inbox, d.Name(), "job.json"))
		if err != nil {
			continue // not complete yet: job.json is written last
		}
		var j Job
		if json.Unmarshal(b, &j) != nil {
			continue
		}
		j.ID = d.Name() // the folder name is the id, whatever the file says
		list = append(list, cand{filepath.Join(inbox, d.Name()), j})
	}
	if len(list) == 0 {
		return false, nil
	}
	sort.Slice(list, func(i, k int) bool { return list[i].job.Requested < list[k].job.Requested })
	c := list[0]
	j := c.job
	j.Status, j.Started = "running", time.Now().UnixMilli()
	jobFile := filepath.Join(root, "state", "jobs", j.ID+".json")
	_ = writeJSON(jobFile, j)
	_ = WriteStatus(root, j.ID)

	var lines []string
	env := *r.Env
	env.Log = func(s string) {
		lines = append(lines, time.Now().UTC().Format("15:04:05 ")+s)
		j.Log = lines
		_ = writeJSON(jobFile, j)
	}
	res, err := apply.Apply(ctx, &env, filepath.Join(c.dir, "package.dkb"), r.Pubs, apply.Options{Force: j.Force, AllowDowngrade: j.Force && r.Allow})
	j.From, j.To, j.Finished, j.Log = res.From, res.To, time.Now().UnixMilli(), lines
	switch {
	case err == nil:
		j.Status = "done"
	case errors.Is(err, apply.ErrRolledBack):
		j.Status, j.Error = "rolled_back", err.Error()
	default:
		j.Status, j.Error = "failed", err.Error()
	}
	_ = writeJSON(jobFile, j)
	_ = os.RemoveAll(c.dir)
	_ = WriteStatus(root, "")
	return true, nil
}

// Run keeps taking jobs until ctx ends, and refreshes status.json so the platform can see the updater is alive.
func (r *Runner) Run(ctx context.Context, every time.Duration) {
	for {
		_ = WriteStatus(r.Env.Root, "")
		for {
			did, err := r.ProcessOne(ctx)
			if err != nil || !did {
				break
			}
		}
		select {
		case <-ctx.Done():
			return
		case <-time.After(every):
		}
	}
}

func Slug(s string) string { return strings.ToLower(strings.TrimSpace(s)) }
