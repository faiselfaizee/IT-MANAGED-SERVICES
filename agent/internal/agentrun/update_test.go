package agentrun

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
)

// "Update agent" on the asset page: every step is reported, a failure is reported as failed, and nothing restarts.
func TestAForcedUpdateReportsEachStepAndAFailure(t *testing.T) {
	dir := t.TempDir()
	t.Setenv("DESKASK_CONFIG_DIR", dir)
	var mu sync.Mutex
	var lines []string
	var failed bool
	var downloads atomic.Int32
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, q *http.Request) {
		switch q.URL.Path {
		case "/agent/v1/binary":
			downloads.Add(1)
			w.Write([]byte("this is not a program")) // the check that it starts must fail
		case "/agent/v1/update-log":
			var b map[string]any
			_ = json.NewDecoder(q.Body).Decode(&b)
			mu.Lock()
			if m, _ := b["msg"].(string); m != "" {
				lines = append(lines, m)
			}
			if b["state"] == "failed" {
				failed = true
			}
			mu.Unlock()
			w.Write([]byte(`{"ok":true}`))
		default:
			w.Write([]byte(`{}`))
		}
	}))
	defer srv.Close()
	r := newTestRunner(srv.URL)
	exited := -1
	r.Exit = func(c int) { exited = c }
	r.RestartSoon = nil

	// the version was put back before: an automatic offer is ignored, a forced one tries again
	if err := os.WriteFile(filepath.Join(dir, doneFile), []byte("9.9.9"), 0o600); err != nil {
		t.Fatal(err)
	}
	r.update(context.Background(), "9.9.9", false)
	if downloads.Load() != 0 {
		t.Fatal("an automatic offer of a version that was put back must be ignored")
	}
	r.update(context.Background(), "9.9.9", true)
	if downloads.Load() != 1 || exited != -1 {
		t.Fatalf("forced update: downloads %d, exit %d", downloads.Load(), exited)
	}
	mu.Lock()
	all := strings.Join(lines, "\n")
	mu.Unlock()
	for _, want := range []string{"put back", "picked up the request: updating to 9.9.9", "Downloading the new program", "Downloaded", "Update to 9.9.9 failed", "does not start"} {
		if !strings.Contains(all, want) {
			t.Fatalf("log misses %q:\n%s", want, all)
		}
	}
	if !failed {
		t.Fatal("the failure was not reported as failed")
	}

	// after a failure, automatic offers wait instead of downloading on every poll
	os.Remove(filepath.Join(dir, doneFile))
	r.update(context.Background(), "9.9.9", false)
	if downloads.Load() != 1 {
		t.Fatal("an automatic retry right after a failure must wait")
	}

	// already on that version
	r.update(context.Background(), r.Version, true)
	mu.Lock()
	defer mu.Unlock()
	if !strings.Contains(lines[len(lines)-1], "already runs") {
		t.Fatalf("expected a note that the agent is up to date, got %q", lines[len(lines)-1])
	}
}
