package jobs

import (
	"context"
	"crypto/ecdsa"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/deskask/platform/updater/internal/apply"
	"github.com/deskask/platform/updater/internal/bundle"
)

type ok struct{}

func (ok) Stop(context.Context) error    { return nil }
func (ok) Start(context.Context) error   { return nil }
func (ok) Healthy(context.Context) error { return nil }

func drop(t *testing.T, root, id string, k *ecdsa.PrivateKey, version string, requested int64) {
	t.Helper()
	src := t.TempDir()
	os.WriteFile(filepath.Join(src, "app.dll"), []byte(version), 0o644)
	dir := filepath.Join(root, "inbox", id)
	os.MkdirAll(dir, 0o755)
	if err := bundle.Pack(src, filepath.Join(dir, "package.dkb"), bundle.Manifest{Component: "host-linux-x64", Version: version}, k); err != nil {
		t.Fatal(err)
	}
	b, _ := json.Marshal(Job{ID: id, File: "x.dkb", By: "admin@acme.test", Requested: requested})
	os.WriteFile(filepath.Join(dir, "job.json"), b, 0o644)
}

func job(t *testing.T, root, id string) Job {
	t.Helper()
	b, err := os.ReadFile(filepath.Join(root, "state", "jobs", id+".json"))
	if err != nil {
		t.Fatal(err)
	}
	var j Job
	json.Unmarshal(b, &j)
	return j
}

func TestInboxIsProcessedOldestFirstAndRecorded(t *testing.T) {
	root := t.TempDir()
	k, _ := bundle.GenerateKey()
	r := &Runner{Env: &apply.Env{Root: root, Runner: ok{}, HealthTimeout: time.Second}, Pubs: []*ecdsa.PublicKey{&k.PublicKey}}
	drop(t, root, "bbbbbbbbbbbbbbbb", k, "2.1.0", 200)
	drop(t, root, "aaaaaaaaaaaaaaaa", k, "2.0.0", 100)
	for i := 0; i < 2; i++ {
		did, err := r.ProcessOne(context.Background())
		if !did || err != nil {
			t.Fatalf("%v %v", did, err)
		}
	}
	if did, _ := r.ProcessOne(context.Background()); did {
		t.Fatal("inbox should be empty")
	}
	a, b := job(t, root, "aaaaaaaaaaaaaaaa"), job(t, root, "bbbbbbbbbbbbbbbb")
	if a.Status != "done" || b.Status != "done" || a.To != "2.0.0" || b.From != "2.0.0" || b.To != "2.1.0" || len(a.Log) == 0 {
		t.Fatalf("%+v %+v", a, b)
	}
	if apply.Installed(root) != "2.1.0" {
		t.Fatal(apply.Installed(root))
	}
	if _, err := os.Stat(filepath.Join(root, "inbox", "aaaaaaaaaaaaaaaa")); err == nil {
		t.Fatal("handled packages must leave the inbox")
	}
	st, _ := os.ReadFile(filepath.Join(root, "state", "status.json"))
	var s Status
	json.Unmarshal(st, &s)
	if s.Current != "2.1.0" || s.Updated == 0 {
		t.Fatalf("%+v", s)
	}
}

func TestUntrustedBundleIsRecordedAsFailed(t *testing.T) {
	root := t.TempDir()
	trusted, _ := bundle.GenerateKey()
	other, _ := bundle.GenerateKey()
	r := &Runner{Env: &apply.Env{Root: root, Runner: ok{}}, Pubs: []*ecdsa.PublicKey{&trusted.PublicKey}}
	drop(t, root, "cccccccccccccccc", other, "9.9.9", 1)
	r.ProcessOne(context.Background())
	j := job(t, root, "cccccccccccccccc")
	if j.Status != "failed" || j.Error == "" || apply.Installed(root) != "" {
		t.Fatalf("%+v", j)
	}
}

func TestJobWithoutJobFileIsNotTakenYetAndOddFoldersAreIgnored(t *testing.T) {
	root := t.TempDir()
	k, _ := bundle.GenerateKey()
	r := &Runner{Env: &apply.Env{Root: root, Runner: ok{}}, Pubs: []*ecdsa.PublicKey{&k.PublicKey}}
	os.MkdirAll(filepath.Join(root, "inbox", "dddddddddddddddd"), 0o755) // upload still in progress
	os.MkdirAll(filepath.Join(root, "inbox", "..evil"), 0o755)
	os.MkdirAll(filepath.Join(root, "inbox", "not-an-id"), 0o755)
	if did, _ := r.ProcessOne(context.Background()); did {
		t.Fatal("nothing should be taken")
	}
}
