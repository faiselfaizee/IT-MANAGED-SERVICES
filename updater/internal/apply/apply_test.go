package apply

import (
	"archive/zip"
	"context"
	"crypto/ecdsa"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/deskask/platform/updater/internal/bundle"
)

type fake struct {
	calls                 []string
	failStart, failHealth func(version string) bool
	root                  string
}

func (f *fake) ver() string                { return Installed(f.root) }
func (f *fake) Stop(context.Context) error { f.calls = append(f.calls, "stop"); return nil }
func (f *fake) Start(context.Context) error {
	f.calls = append(f.calls, "start "+f.ver())
	if f.failStart != nil && f.failStart(f.ver()) {
		return errors.New("service would not start")
	}
	return nil
}
func (f *fake) Healthy(context.Context) error {
	if f.failHealth != nil && f.failHealth(f.ver()) {
		return errors.New("health 503")
	}
	return nil
}

func mk(t *testing.T) (*Env, *fake, *ecdsa.PrivateKey) {
	t.Helper()
	root := t.TempDir()
	k, _ := bundle.GenerateKey()
	f := &fake{root: root}
	return &Env{Root: root, Runner: f, HealthTimeout: 50 * time.Millisecond, HealthEvery: 10 * time.Millisecond, Keep: []string{"appsettings.Production.json", "data"}}, f, k
}

func pack(t *testing.T, k *ecdsa.PrivateKey, version string, files map[string]string) string {
	t.Helper()
	dir := t.TempDir()
	for p, c := range files {
		full := filepath.Join(dir, filepath.FromSlash(p))
		os.MkdirAll(filepath.Dir(full), 0o755)
		if err := os.WriteFile(full, []byte(c), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	out := filepath.Join(t.TempDir(), "b.dkb")
	if err := bundle.Pack(dir, out, bundle.Manifest{Component: "host-linux-x64", Version: version}, k); err != nil {
		t.Fatal(err)
	}
	return out
}

func read(t *testing.T, p string) string {
	t.Helper()
	b, err := os.ReadFile(p)
	if err != nil {
		t.Fatal(err)
	}
	return string(b)
}

func pubs(k *ecdsa.PrivateKey) []*ecdsa.PublicKey { return []*ecdsa.PublicKey{&k.PublicKey} }

func TestFreshInstall(t *testing.T) {
	env, f, k := mk(t)
	res, err := Apply(context.Background(), env, pack(t, k, "2.0.0", map[string]string{"app.dll": "v2", "web/index.html": "<html>"}), pubs(k), Options{})
	if err != nil || res.To != "2.0.0" || res.From != "" {
		t.Fatalf("%+v %v", res, err)
	}
	if read(t, env.Root+"/live/app.dll") != "v2" || Installed(env.Root) != "2.0.0" || read(t, env.Root+"/live/web/index.html") != "<html>" {
		t.Fatal("files not in place")
	}
	if strings.Join(f.calls, ",") != "stop,start 2.0.0" {
		t.Fatalf("calls %v", f.calls)
	}
}

func TestUpgradeKeepsSettingsAndDataAndKeepsPrevious(t *testing.T) {
	env, _, k := mk(t)
	Apply(context.Background(), env, pack(t, k, "2.0.0", map[string]string{"app.dll": "v2"}), pubs(k), Options{})
	os.WriteFile(env.Root+"/live/appsettings.Production.json", []byte(`{"customer":"acme"}`), 0o644)
	os.MkdirAll(env.Root+"/live/data/files", 0o755)
	os.WriteFile(env.Root+"/live/data/files/a.txt", []byte("precious"), 0o644)
	res, err := Apply(context.Background(), env, pack(t, k, "2.1.0", map[string]string{"app.dll": "v21", "appsettings.Production.json": "{}"}), pubs(k), Options{})
	if err != nil || res.From != "2.0.0" || res.To != "2.1.0" {
		t.Fatalf("%+v %v", res, err)
	}
	if read(t, env.Root+"/live/app.dll") != "v21" {
		t.Fatal("new code missing")
	}
	if read(t, env.Root+"/live/appsettings.Production.json") != `{"customer":"acme"}` {
		t.Fatal("the customer's settings must win over the bundle's file")
	}
	if read(t, env.Root+"/live/data/files/a.txt") != "precious" {
		t.Fatal("data lost")
	}
	if read(t, env.Root+"/previous/app.dll") != "v2" {
		t.Fatal("previous version not kept")
	}
}

func TestUnhealthyNewVersionIsRolledBack(t *testing.T) {
	env, f, k := mk(t)
	Apply(context.Background(), env, pack(t, k, "2.0.0", map[string]string{"app.dll": "good"}), pubs(k), Options{})
	f.calls = nil
	f.failHealth = func(v string) bool { return v == "2.1.0" }
	res, err := Apply(context.Background(), env, pack(t, k, "2.1.0", map[string]string{"app.dll": "bad"}), pubs(k), Options{})
	if !errors.Is(err, ErrRolledBack) || !res.RolledBack {
		t.Fatalf("expected a rollback, got %v", err)
	}
	if Installed(env.Root) != "2.0.0" || read(t, env.Root+"/live/app.dll") != "good" {
		t.Fatal("the old version must be back")
	}
	if strings.Join(f.calls, ",") != "stop,start 2.1.0,stop,start 2.0.0" {
		t.Fatalf("calls %v", f.calls)
	}
}

func TestServiceThatWillNotStartIsRolledBack(t *testing.T) {
	env, f, k := mk(t)
	Apply(context.Background(), env, pack(t, k, "2.0.0", map[string]string{"app.dll": "good"}), pubs(k), Options{})
	f.failStart = func(v string) bool { return v == "2.1.0" }
	_, err := Apply(context.Background(), env, pack(t, k, "2.1.0", map[string]string{"app.dll": "bad"}), pubs(k), Options{})
	if !errors.Is(err, ErrRolledBack) || Installed(env.Root) != "2.0.0" {
		t.Fatalf("%v installed %s", err, Installed(env.Root))
	}
}

func TestRollbackThatDoesNotBecomeHealthyIsReportedAsFailed(t *testing.T) {
	env, f, k := mk(t)
	Apply(context.Background(), env, pack(t, k, "2.0.0", map[string]string{"app.dll": "good"}), pubs(k), Options{})
	f.failHealth = func(string) bool { return true } // nothing is healthy any more, not even the old version
	_, err := Apply(context.Background(), env, pack(t, k, "2.1.0", map[string]string{"app.dll": "bad"}), pubs(k), Options{})
	if !errors.Is(err, ErrRollbackFailed) {
		t.Fatalf("expected ErrRollbackFailed, got %v", err)
	}
}

func TestFailedFirstInstallLeavesNothingBehind(t *testing.T) {
	env, f, k := mk(t)
	f.failHealth = func(string) bool { return true }
	_, err := Apply(context.Background(), env, pack(t, k, "2.0.0", map[string]string{"app.dll": "x"}), pubs(k), Options{})
	if !errors.Is(err, ErrRolledBack) {
		t.Fatal(err)
	}
	if _, e := os.Stat(env.Root + "/live"); e == nil {
		t.Fatal("a failed first install must not stay in live/")
	}
}

func TestOnlyNewerVersionsUnlessForced(t *testing.T) {
	env, _, k := mk(t)
	Apply(context.Background(), env, pack(t, k, "2.1.0", map[string]string{"a": "1"}), pubs(k), Options{})
	for _, v := range []string{"2.1.0", "2.0.9", "2.1.0-alpha.1"} {
		if _, err := Apply(context.Background(), env, pack(t, k, v, map[string]string{"a": "2"}), pubs(k), Options{}); !errors.Is(err, ErrNotNewer) {
			t.Fatalf("%s should be refused, got %v", v, err)
		}
	}
	if _, err := Apply(context.Background(), env, pack(t, k, "2.1.0", map[string]string{"a": "re"}), pubs(k), Options{Force: true}); err != nil {
		t.Fatalf("re-apply with force: %v", err)
	}
	if _, err := Apply(context.Background(), env, pack(t, k, "2.0.0", map[string]string{"a": "old"}), pubs(k), Options{Force: true}); !errors.Is(err, ErrNotNewer) {
		t.Fatalf("a downgrade needs its own switch, got %v", err)
	}
	if _, err := Apply(context.Background(), env, pack(t, k, "2.0.0", map[string]string{"a": "old"}), pubs(k), Options{Force: true, AllowDowngrade: true}); err != nil {
		t.Fatal(err)
	}
}

func TestBundleSignedByAnotherKeyIsRefusedBeforeTheServiceIsTouched(t *testing.T) {
	env, f, k := mk(t)
	other, _ := bundle.GenerateKey()
	_, err := Apply(context.Background(), env, pack(t, other, "2.0.0", map[string]string{"a": "evil"}), pubs(k), Options{})
	if !errors.Is(err, bundle.ErrBadSignature) {
		t.Fatalf("got %v", err)
	}
	if len(f.calls) != 0 {
		t.Fatalf("the service must not be stopped for an untrusted bundle: %v", f.calls)
	}
}

func TestKeyRotationAcceptsEitherKey(t *testing.T) {
	env, _, oldK := mk(t)
	newK, _ := bundle.GenerateKey()
	both := []*ecdsa.PublicKey{&oldK.PublicKey, &newK.PublicKey}
	if _, err := Apply(context.Background(), env, pack(t, newK, "2.0.0", map[string]string{"a": "1"}), both, Options{}); err != nil {
		t.Fatal(err)
	}
	if _, err := Apply(context.Background(), env, pack(t, oldK, "2.0.1", map[string]string{"a": "2"}), both, Options{}); err != nil {
		t.Fatal(err)
	}
}

// builds a zip by hand: lets a test break one thing in an otherwise valid bundle
func rebuild(t *testing.T, src string, edit func(name string, data []byte) (string, []byte, bool), extra map[string]string) string {
	t.Helper()
	zr, err := zip.OpenReader(src)
	if err != nil {
		t.Fatal(err)
	}
	defer zr.Close()
	out := filepath.Join(t.TempDir(), "x.dkb")
	of, _ := os.Create(out)
	zw := zip.NewWriter(of)
	for _, f := range zr.File {
		r, _ := f.Open()
		data := make([]byte, f.UncompressedSize64)
		n, _ := r.Read(data)
		for n < len(data) {
			m, e := r.Read(data[n:])
			n += m
			if e != nil {
				break
			}
		}
		r.Close()
		name, d, keep := f.Name, data, true
		if edit != nil {
			name, d, keep = edit(f.Name, data)
		}
		if keep {
			w, _ := zw.Create(name)
			w.Write(d)
		}
	}
	for n, c := range extra {
		w, _ := zw.Create(n)
		w.Write([]byte(c))
	}
	zw.Close()
	of.Close()
	return out
}

func TestChangedPayloadIsCaughtByTheHashBeforeTheSwap(t *testing.T) {
	env, f, k := mk(t)
	Apply(context.Background(), env, pack(t, k, "2.0.0", map[string]string{"app.dll": "good"}), pubs(k), Options{})
	f.calls = nil
	good := pack(t, k, "2.1.0", map[string]string{"app.dll": "genuine"})
	evil := rebuild(t, good, func(n string, d []byte) (string, []byte, bool) {
		if n == "payload/app.dll" {
			return n, []byte("malware"), true
		}
		return n, d, true
	}, nil)
	_, err := Apply(context.Background(), env, evil, pubs(k), Options{})
	if !errors.Is(err, bundle.ErrBadBundle) {
		t.Fatalf("got %v", err)
	}
	if read(t, env.Root+"/live/app.dll") != "good" || Installed(env.Root) != "2.0.0" {
		t.Fatal("the running version must be untouched")
	}
	if len(f.calls) != 0 {
		t.Fatalf("service touched: %v", f.calls)
	}
}

func TestExtraFilesAndUnsafePathsAreRefused(t *testing.T) {
	env, _, k := mk(t)
	good := pack(t, k, "2.0.0", map[string]string{"app.dll": "x"})
	for name, extra := range map[string]map[string]string{
		"extra file": {"payload/backdoor.dll": "x"},
		"zip slip":   {"payload/../../evil": "x"},
		"outside":    {"evil.txt": "x"},
		"absolute":   {"payload//etc/passwd": "x"},
		"backslash":  {"payload/a\\b": "x"},
	} {
		bad := rebuild(t, good, nil, extra)
		if _, err := Apply(context.Background(), env, bad, pubs(k), Options{}); !errors.Is(err, bundle.ErrBadBundle) {
			t.Errorf("%s: expected ErrBadBundle, got %v", name, err)
		}
	}
	if Installed(env.Root) != "" {
		t.Fatal("nothing may be installed")
	}
}

func TestManifestWithoutPayloadFileIsRefused(t *testing.T) {
	env, _, k := mk(t)
	good := pack(t, k, "2.0.0", map[string]string{"a": "1", "b": "2"})
	bad := rebuild(t, good, func(n string, d []byte) (string, []byte, bool) { return n, d, n != "payload/b" }, nil)
	if _, err := Apply(context.Background(), env, bad, pubs(k), Options{}); !errors.Is(err, bundle.ErrBadBundle) {
		t.Fatalf("got %v", err)
	}
}

func TestGarbageFileIsNotABundle(t *testing.T) {
	env, _, k := mk(t)
	p := filepath.Join(t.TempDir(), "x.dkb")
	os.WriteFile(p, []byte("this is not a zip"), 0o644)
	if _, err := Apply(context.Background(), env, p, pubs(k), Options{}); !errors.Is(err, bundle.ErrBadBundle) {
		t.Fatalf("got %v", err)
	}
}

func TestManualRollbackSwapsTheVersions(t *testing.T) {
	env, _, k := mk(t)
	Apply(context.Background(), env, pack(t, k, "2.0.0", map[string]string{"app.dll": "old"}), pubs(k), Options{})
	Apply(context.Background(), env, pack(t, k, "2.1.0", map[string]string{"app.dll": "new"}), pubs(k), Options{})
	v, err := Rollback(context.Background(), env)
	if err != nil || v != "2.0.0" || read(t, env.Root+"/live/app.dll") != "old" {
		t.Fatalf("%s %v", v, err)
	}
	v, err = Rollback(context.Background(), env) // and forward again
	if err != nil || v != "2.1.0" {
		t.Fatalf("%s %v", v, err)
	}
}

func TestKeepPathMayNotEscape(t *testing.T) {
	env, _, k := mk(t)
	env.Keep = []string{"../secrets"}
	if _, err := Apply(context.Background(), env, pack(t, k, "2.0.0", map[string]string{"a": "1"}), pubs(k), Options{}); err == nil {
		t.Fatal("a keep path with .. must be refused")
	}
}

func TestCompareVersions(t *testing.T) {
	cases := []struct {
		a, b string
		want int
	}{
		{"2.0.0", "2.0.0", 0}, {"2.0.1", "2.0.0", 1}, {"2.1.0", "2.0.9", 1}, {"3.0.0", "2.99.99", 1}, {"v2.0.0", "2.0.0", 0},
		{"2.0.0-alpha.1", "2.0.0-alpha.2", -1}, {"2.0.0-alpha.2", "2.0.0-alpha.10", -1}, {"2.0.0-alpha", "2.0.0-alpha.1", -1},
		{"2.0.0-alpha.1", "2.0.0-beta", -1}, {"2.0.0-rc.1", "2.0.0", -1}, {"2.0.0", "2.0.0-rc.1", 1}, {"2.0.0+build5", "2.0.0", 0},
		{"2.0.0-1", "2.0.0-alpha", -1},
	}
	for _, c := range cases {
		got, err := CompareVersions(c.a, c.b)
		if err != nil || got != c.want {
			t.Errorf("Compare(%s,%s)=%d,%v want %d", c.a, c.b, got, err, c.want)
		}
		back, _ := CompareVersions(c.b, c.a)
		if back != -c.want {
			t.Errorf("Compare is not antisymmetric for %s,%s", c.a, c.b)
		}
	}
	for _, bad := range []string{"", "2.0", "a.b.c", "2.0.x"} {
		if _, err := CompareVersions(bad, "2.0.0"); err == nil {
			t.Errorf("%q should be an error", bad)
		}
	}
	_ = fmt.Sprint
}
