package agentrun

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/deskask/platform/agent/internal/config"
)

func TestExecRunsBashAndReportsExitTimeoutAndCancel(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("bash")
	}
	r := Exec(context.Background(), "bash", "echo hello\necho oops 1>&2\nexit 3", 10*time.Second)
	if r.Status != "failed" || r.Exit != 3 || !strings.Contains(r.Output, "hello") || !strings.Contains(r.Output, "oops") {
		t.Fatalf("%+v", r)
	}
	if r := Exec(context.Background(), "bash", "echo fine", 10*time.Second); r.Status != "done" || r.Exit != 0 {
		t.Fatalf("%+v", r)
	}
	if r := Exec(context.Background(), "bash", "sleep 30", 500*time.Millisecond); r.Status != "timeout" {
		t.Fatalf("%+v", r)
	}
	ctx, cancel := context.WithCancel(context.Background())
	go func() { time.Sleep(300 * time.Millisecond); cancel() }()
	if r := Exec(ctx, "bash", "sleep 30", 10*time.Second); r.Status != "cancelled" {
		t.Fatalf("%+v", r)
	}
	if r := Exec(context.Background(), "powershell", "x", time.Second); r.Status != "failed" {
		t.Fatalf("a shell this computer does not have must fail clearly: %+v", r)
	}
}

type fakePlatform struct {
	mu       sync.Mutex
	enrolled map[string]any
	checkins int
	jobs     []map[string]any
	queue    []map[string]any
	collect  chan struct{}
}

func (f *fakePlatform) handler() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("/agent/v1/enroll", func(w http.ResponseWriter, r *http.Request) {
		var b map[string]any
		_ = json.NewDecoder(r.Body).Decode(&b)
		if b["token"] != "dkat_good" {
			w.WriteHeader(401)
			_, _ = w.Write([]byte(`{"error":"token_invalid"}`))
			return
		}
		f.mu.Lock()
		f.enrolled = b
		f.mu.Unlock()
		_, _ = w.Write([]byte(`{"agentId":"gAAA","key":"dkag_secretsecretsecretsecret","pollSeconds":25}`))
	})
	auth := func(w http.ResponseWriter, r *http.Request) bool {
		if r.Header.Get("Authorization") != "Bearer dkag_secretsecretsecretsecret" || r.Header.Get("X-Agent-Id") != "gAAA" {
			w.WriteHeader(401)
			return false
		}
		return true
	}
	mux.HandleFunc("/agent/v1/checkin", func(w http.ResponseWriter, r *http.Request) {
		if !auth(w, r) {
			return
		}
		f.mu.Lock()
		f.checkins++
		f.mu.Unlock()
		select {
		case f.collect <- struct{}{}:
		default:
		}
		_, _ = w.Write([]byte(`{"ok":true}`))
	})
	mux.HandleFunc("/agent/v1/poll", func(w http.ResponseWriter, r *http.Request) {
		if !auth(w, r) {
			return
		}
		f.mu.Lock()
		q := f.queue
		f.queue = nil
		f.mu.Unlock()
		if len(q) == 0 {
			time.Sleep(200 * time.Millisecond)
		}
		_ = json.NewEncoder(w).Encode(map[string]any{"msgs": q})
	})
	mux.HandleFunc("/agent/v1/job", func(w http.ResponseWriter, r *http.Request) {
		if !auth(w, r) {
			return
		}
		var b map[string]any
		_ = json.NewDecoder(r.Body).Decode(&b)
		f.mu.Lock()
		f.jobs = append(f.jobs, b)
		f.mu.Unlock()
		_, _ = w.Write([]byte(`{"ok":true}`))
	})
	return mux
}

func TestEnrolSendsTheInventoryAndAWrongTokenIsRefused(t *testing.T) {
	if runtime.GOOS != "linux" {
		t.Skip("live inventory runs on Linux here")
	}
	f := &fakePlatform{collect: make(chan struct{}, 10)}
	srv := httptest.NewServer(f.handler())
	defer srv.Close()
	if _, err := Enrol(context.Background(), srv.URL, "dkat_bad", "9.9.9"); err == nil {
		t.Fatal("a wrong token must be refused")
	}
	cfg, err := Enrol(context.Background(), srv.URL, "dkat_good", "9.9.9")
	if err != nil || cfg.AgentID != "gAAA" || !strings.HasPrefix(cfg.Key, "dkag_") {
		t.Fatalf("%v %+v", err, cfg)
	}
	inv, _ := f.enrolled["inventory"].(map[string]any)
	if f.enrolled["platform"] != "linux" || f.enrolled["version"] != "9.9.9" || inv == nil || inv["hostname"] == "" || inv["os_family"] != "linux" {
		t.Fatalf("%v", f.enrolled)
	}
	for _, k := range []string{"cpu", "ram_bytes", "disks", "nics", "installed_apps"} {
		if _, ok := inv[k]; !ok {
			t.Fatalf("the inventory has no %q", k)
		}
	}
	if _, err := Enrol(context.Background(), "ftp://nope", "x", "1"); err == nil {
		t.Fatal("bad address")
	}
}

func TestTheRunnerSendsInventoryRunsAJobAndAnswers(t *testing.T) {
	if runtime.GOOS != "linux" {
		t.Skip("bash")
	}
	t.Setenv("DESKASK_CONFIG_DIR", t.TempDir())
	f := &fakePlatform{collect: make(chan struct{}, 10)}
	srv := httptest.NewServer(f.handler())
	defer srv.Close()
	r := NewRunner(config.Agent{Server: srv.URL, AgentID: "gAAA", Key: "dkag_secretsecretsecretsecret"}, "1.0.0")
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	go func() { _ = r.Run(ctx) }()
	select {
	case <-f.collect:
	case <-time.After(20 * time.Second):
		t.Fatal("no inventory was sent")
	}
	f.mu.Lock()
	f.queue = append(f.queue, map[string]any{"type": "job", "id": "j1", "shell": "bash", "body": "echo from-the-platform", "timeout": 30})
	f.mu.Unlock()
	deadline := time.Now().Add(20 * time.Second)
	for time.Now().Before(deadline) {
		f.mu.Lock()
		n := len(f.jobs)
		f.mu.Unlock()
		if n >= 2 {
			break
		}
		time.Sleep(100 * time.Millisecond)
	}
	f.mu.Lock()
	defer f.mu.Unlock()
	if len(f.jobs) < 2 || f.jobs[0]["status"] != "running" || f.jobs[1]["status"] != "done" || !strings.Contains(f.jobs[1]["output"].(string), "from-the-platform") {
		t.Fatalf("%v", f.jobs)
	}
	// a "collect" message asks for a new inventory
	before := f.checkins
	f.queue = append(f.queue, map[string]any{"type": "collect"})
	f.mu.Unlock()
	time.Sleep(3 * time.Second)
	f.mu.Lock()
	if f.checkins <= before {
		t.Fatalf("collect did not trigger a check-in (%d -> %d)", before, f.checkins)
	}
}

func TestAnUpdateThatNeverReachesThePlatformIsRolledBack(t *testing.T) {
	dir := t.TempDir()
	t.Setenv("DESKASK_CONFIG_DIR", dir)
	if err := os.WriteFile(filepath.Join(dir, pendingFile), []byte("3"), 0o600); err != nil { // already started three times since the update
		t.Fatal(err)
	}
	code := -1
	checkUpdateGuard("2.0.0", func(c int) { code = c })
	if code != 3 {
		t.Fatalf("expected a restart after the rollback, got %d", code)
	}
}
