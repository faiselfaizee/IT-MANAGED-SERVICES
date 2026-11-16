package agentrun

import (
	"context"
	"errors"
	"fmt"
	"log"
	"os"
	"path"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/deskask/platform/agent/internal/client"
	"github.com/deskask/platform/agent/internal/config"
	"github.com/deskask/platform/agent/internal/filebackup"
)

// backupPlan is a file backup plan as the platform sends it (with the storage secrets and the repository password).
type backupPlan struct {
	ID         int64              `json:"id"`
	Name       string             `json:"name"`
	Paths      []string           `json:"paths"`
	Excludes   []string           `json:"excludes"`
	EveryHours int                `json:"everyHours"`
	AtHour     int                `json:"atHour"` // local hour for daily plans, -1 for none
	KeepLast   int                `json:"keepLast"`
	KeepDays   int                `json:"keepDays"`
	UseVss     bool               `json:"useVss"`
	Enabled    bool               `json:"enabled"`
	Storage    filebackup.Storage `json:"storage"`
	Password   string             `json:"password"`
}

const plansFile = "backup-plans.json" // kept beside agent.json, readable by administrators and the agent only
const backupStateFile = "backup-state.json"

type fileBackups struct {
	mu      sync.Mutex
	plans   []backupPlan
	last    map[string]int64 // plan id -> Unix time of the last run (any result)
	busy    sync.Mutex       // one plan runs at a time
	sync    chan struct{}
	runNow  chan int64
	restore chan string
}

func newFileBackups() *fileBackups {
	f := &fileBackups{last: map[string]int64{}, sync: make(chan struct{}, 1), runNow: make(chan int64, 8), restore: make(chan string, 8)}
	_ = config.Load(plansFile, &f.plans)
	_ = config.Load(backupStateFile, &f.last)
	return f
}

// due says whether a plan should run now: daily plans with an hour run once a day from that hour; others every N hours.
func (p backupPlan) due(last time.Time, now time.Time) bool {
	if !p.Enabled || p.EveryHours <= 0 {
		return false
	}
	if p.EveryHours == 24 && p.AtHour >= 0 && p.AtHour <= 23 {
		today := time.Date(now.Year(), now.Month(), now.Day(), p.AtHour, 0, 0, 0, now.Location())
		if now.Before(today) {
			today = today.AddDate(0, 0, -1)
		}
		return last.Before(today)
	}
	return now.Sub(last) >= time.Duration(p.EveryHours)*time.Hour
}

// fileBackupLoop keeps the plans up to date and runs them when they are due, when asked to run now, and restores when asked.
func (r *Runner) fileBackupLoop(ctx context.Context) {
	fb := r.fb
	fetch := time.After(30 * time.Second)
	tick := time.NewTicker(time.Minute)
	defer tick.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-fetch:
			r.fetchPlans(ctx)
			fetch = time.After(15 * time.Minute)
		case <-fb.sync:
			r.fetchPlans(ctx)
		case id := <-fb.runNow:
			r.fetchPlans(ctx)
			fb.mu.Lock()
			var p *backupPlan
			for i := range fb.plans {
				if fb.plans[i].ID == id {
					p = &fb.plans[i]
				}
			}
			fb.mu.Unlock()
			if p != nil {
				go r.runPlan(ctx, *p)
			}
		case id := <-fb.restore:
			go r.runRestore(ctx, id)
		case <-tick.C:
			now := time.Now()
			fb.mu.Lock()
			var due []backupPlan
			for _, p := range fb.plans {
				if p.due(time.Unix(fb.last[strconv.FormatInt(p.ID, 10)], 0), now) {
					due = append(due, p)
				}
			}
			fb.mu.Unlock()
			for _, p := range due {
				go r.runPlan(ctx, p)
			}
		}
	}
}

func (r *Runner) fetchPlans(ctx context.Context) {
	var plans []backupPlan
	err := r.cl.Do(ctx, "GET", "/agent/v1/backup/plans", nil, &plans)
	var he *client.Error
	if errors.As(err, &he) && he.Status == 404 {
		plans = nil // a platform without file backups
	} else if err != nil {
		log.Printf("backup plans not fetched (the saved ones are used): %v", err)
		return
	}
	r.fb.mu.Lock()
	r.fb.plans = plans
	r.fb.mu.Unlock()
	if len(plans) == 0 {
		config.Remove(plansFile)
	} else if err := config.Save(plansFile, plans); err != nil {
		log.Printf("backup plans not saved: %v", err)
	}
}

// planStorage puts each plan's backups in its own folder (or prefix), so two plans never share pieces or a password.
func planStorage(p backupPlan) filebackup.Storage {
	s := p.Storage
	sub := "deskask-plan-" + strconv.FormatInt(p.ID, 10)
	switch s.Kind {
	case "local", "smb":
		sep := `\`
		if !strings.Contains(s.Path, `\`) {
			sep = "/"
		}
		s.Path = strings.TrimRight(s.Path, `\/`) + sep + sub
	default:
		s.Prefix = path.Join(strings.Trim(strings.ReplaceAll(s.Prefix, `\`, "/"), "/"), sub)
	}
	return s
}

func (r *Runner) openPlanRepo(ctx context.Context, p backupPlan) (*filebackup.Repo, error) {
	st, err := filebackup.OpenStore(ctx, planStorage(p))
	if err != nil {
		return nil, err
	}
	return filebackup.OpenRepo(ctx, st, p.Password)
}

func (r *Runner) runPlan(ctx context.Context, p backupPlan) {
	fb := r.fb
	if !fb.busy.TryLock() {
		fb.busy.Lock() // wait for the plan that is running
	}
	defer fb.busy.Unlock()
	id := strconv.FormatInt(p.ID, 10)
	start := time.Now()
	fb.mu.Lock()
	fb.last[id] = start.Unix()
	_ = config.Save(backupStateFile, fb.last)
	fb.mu.Unlock()
	host, _ := os.Hostname()
	res := filebackup.Summary{}
	var removed []string
	err := func() error {
		repo, err := r.openPlanRepo(ctx, p)
		if err != nil {
			return err
		}
		o := filebackup.Options{Plan: id, Host: host, Paths: p.Paths, Excludes: p.Excludes}
		var warns []string
		if p.UseVss {
			read, cleanup, w := filebackup.Shadow(ctx, p.Paths)
			defer cleanup()
			o.Read, warns = read, w
		}
		res, err = filebackup.Backup(ctx, repo, o)
		res.Warnings = append(warns, res.Warnings...)
		if err != nil {
			return err
		}
		removed, err = filebackup.Forget(ctx, repo, id, p.KeepLast, p.KeepDays, time.Now())
		if err != nil {
			res.Warnings = append(res.Warnings, "old backups not removed: "+err.Error())
		}
		return nil
	}()
	result, msg := "success", fmt.Sprintf("%d files, %s, %s new", res.Files, size(res.Bytes), size(res.NewBytes))
	key := "deskask:" + res.Snapshot
	switch {
	case err != nil:
		result, msg, key = "failed", err.Error(), "deskask:failed:"+strconv.FormatInt(start.Unix(), 10)
	case len(res.Warnings) > 0:
		result = "warning"
		msg += fmt.Sprintf("; %d not backed up, for example %s", len(res.Warnings), res.Warnings[0])
	}
	log.Printf("backup %q: %s: %s", p.Name, result, msg)
	report := backupReport{Tools: []string{"deskask"}, Reports: []backupRun{{Tool: "deskask", Job: p.Name, Started: start.UTC().Format(time.RFC3339), Ended: time.Now().UTC().Format(time.RFC3339),
		Result: result, Message: msg, Bytes: res.Bytes, Key: key}}}
	if e := r.cl.Do(ctx, "POST", "/agent/v1/backup", report, nil); e != nil {
		log.Printf("backup result not sent: %v", e)
	}
	if err == nil {
		snap := map[string]any{"plan": p.ID, "id": res.Snapshot, "at": start.UTC().Format(time.RFC3339), "files": res.Files, "bytes": res.Bytes, "newBytes": res.NewBytes,
			"warnings": len(res.Warnings), "removed": removed}
		if e := r.cl.Do(ctx, "POST", "/agent/v1/backup/snapshot", snap, nil); e != nil {
			log.Printf("snapshot not sent: %v", e)
		}
	}
}

type backupRun struct {
	Tool    string `json:"tool"`
	Job     string `json:"job"`
	Started string `json:"started"`
	Ended   string `json:"ended"`
	Result  string `json:"result"`
	Message string `json:"message"`
	Bytes   int64  `json:"bytes"`
	Key     string `json:"key"`
}
type backupReport struct {
	Tools   []string    `json:"tools"`
	Reports []backupRun `json:"reports"`
}

func size(b int64) string {
	switch {
	case b >= 1<<30:
		return fmt.Sprintf("%.1f GB", float64(b)/(1<<30))
	case b >= 1<<20:
		return fmt.Sprintf("%.1f MB", float64(b)/(1<<20))
	case b >= 1<<10:
		return fmt.Sprintf("%.0f KB", float64(b)/(1<<10))
	}
	return fmt.Sprintf("%d bytes", b)
}

// runRestore fetches what to restore, restores it and reports the result.
func (r *Runner) runRestore(ctx context.Context, id string) {
	var job struct {
		Plan     backupPlan `json:"plan"`
		Snapshot string     `json:"snapshot"`
		Include  []string   `json:"include"`
		Target   string     `json:"target"`
	}
	if err := r.cl.Do(ctx, "GET", "/agent/v1/backup/restore?id="+id, nil, &job); err != nil {
		log.Printf("restore %s not fetched: %v", id, err)
		return
	}
	r.fb.busy.Lock()
	defer r.fb.busy.Unlock()
	out := map[string]any{"ok": false}
	repo, err := r.openPlanRepo(ctx, job.Plan)
	if err == nil {
		var s filebackup.RestoreSummary
		s, err = filebackup.Restore(ctx, repo, job.Snapshot, filebackup.RestoreOptions{Include: job.Include, Target: job.Target})
		out = map[string]any{"ok": err == nil && len(s.Errors) == 0, "files": s.Files, "bytes": s.Bytes, "errors": s.Errors}
		if err == nil {
			out["message"] = fmt.Sprintf("%d files restored (%s)", s.Files, size(s.Bytes))
			if len(s.Errors) > 0 {
				out["message"] = fmt.Sprintf("%d files restored, %d failed: %s", s.Files, len(s.Errors), s.Errors[0])
			}
		}
	}
	if err != nil {
		out["message"] = err.Error()
	}
	if e := r.cl.Do(ctx, "POST", "/agent/v1/backup/restore?id="+id, out, nil); e != nil {
		log.Printf("restore result not sent: %v", e)
	}
}
