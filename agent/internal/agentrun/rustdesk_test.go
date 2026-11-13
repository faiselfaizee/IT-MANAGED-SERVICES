package agentrun

import (
	"encoding/json"

	"context"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"github.com/deskask/platform/agent/internal/config"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"
)

// a pretend Windows computer: the "installer" creates the program, "sc" reports the service, RustDesk's own commands act on files in a temp folder
type fakePC struct {
	t         *testing.T
	env       rdEnv
	mu        sync.Mutex
	calls     []string
	optionsOK bool   // --option writes the settings file (as the real service does)
	id        string // what --get-id prints
}

func newFakePC(t *testing.T, srv *httptest.Server) *fakePC {
	dir := t.TempDir()
	pc := &fakePC{t: t, optionsOK: true, id: "123456789"}
	pc.env = rdEnv{
		Exe: filepath.Join(dir, "Program Files", "RustDesk", "rustdesk.exe"), Temp: dir, HTTP: srv.Client(),
		ConfigDirs: []string{filepath.Join(dir, "svc", "config"), filepath.Join(dir, "sys", "config")},
		Poll:       time.Millisecond, Wait: 50 * time.Millisecond,
	}
	pc.env.Run = pc.run
	return pc
}

func (p *fakePC) run(_ context.Context, _ time.Duration, name string, args ...string) (string, error) {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.calls = append(p.calls, name+" "+strings.Join(args, " "))
	switch {
	case len(args) == 1 && args[0] == "--silent-install":
		_ = os.MkdirAll(filepath.Dir(p.env.Exe), 0o755)
		_ = os.WriteFile(p.env.Exe, []byte("exe"), 0o755)
		return "", nil
	case name == "sc" && len(args) > 0 && args[0] == "query":
		return "SERVICE_NAME: RustDesk\n STATE : 4  RUNNING", nil
	case len(args) == 3 && args[0] == "--option":
		if p.optionsOK && args[1] == "custom-rendezvous-server" {
			_ = os.MkdirAll(p.env.ConfigDirs[0], 0o755)
			_ = os.WriteFile(filepath.Join(p.env.ConfigDirs[0], "RustDesk2.toml"), []byte("custom-rendezvous-server = '"+args[2]+"'"), 0o644)
		}
		return "", nil
	case len(args) == 1 && args[0] == "--get-id":
		return p.id, nil
	}
	return "", nil
}

func (p *fakePC) called(sub string) bool {
	p.mu.Lock()
	defer p.mu.Unlock()
	for _, c := range p.calls {
		if strings.Contains(c, sub) {
			return true
		}
	}
	return false
}

func installer() (body []byte, sum string, srv func() *httptest.Server) {
	body = []byte(strings.Repeat("rustdesk-installer-bytes", 4000))
	h := sha256.Sum256(body)
	sum = hex.EncodeToString(h[:])
	srv = func() *httptest.Server {
		return httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			if r.URL.Path != "/rustdesk.exe" {
				http.NotFound(w, r)
				return
			}
			_, _ = w.Write(body)
		}))
	}
	return
}

type logs struct {
	mu    sync.Mutex
	lines []string
}

func (l *logs) add(level, format string, a ...any) {
	l.mu.Lock()
	defer l.mu.Unlock()
	l.lines = append(l.lines, level+": "+sprintf(format, a...))
}
func (l *logs) text() string { l.mu.Lock(); defer l.mu.Unlock(); return strings.Join(l.lines, "\n") }

func sprintf(f string, a ...any) string {
	if len(a) == 0 {
		return f
	}
	return strings.TrimSpace(fmt.Sprintf(f, a...))
}

func TestRustDeskSetupInstallsConfiguresAndReportsEveryStep(t *testing.T) {
	_, sum, mk := installer()
	srv := mk()
	defer srv.Close()
	pc := newFakePC(t, srv)
	var l logs
	id, err := rdInstall(context.Background(), rdConfig{IDServer: "rd.example.com", Key: "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789=", Installer: srv.URL + "/rustdesk.exe", SHA256: strings.ToUpper(sum), Password: "S3cretPassw0rd", Mode: "unattended"}, pc.env, l.add)
	if err != nil || id != "123456789" {
		t.Fatalf("%v %q\n%s", err, id, l.text())
	}
	for _, want := range []string{"Downloading", "SHA-256 matches", "Installing RustDesk silently", "The RustDesk service is running", "Set custom-rendezvous-server", "Set key", "Checked: RustDesk's settings file holds the server rd.example.com", "Permanent password set", "RustDesk ID is 123456789"} {
		if !strings.Contains(l.text(), want) {
			t.Errorf("log lacks %q:\n%s", want, l.text())
		}
	}
	if strings.Contains(l.text(), "S3cretPassw0rd") {
		t.Fatal("the password must never be written to the log")
	}
	if !pc.called("--password S3cretPassw0rd") {
		t.Fatal("the password was not set")
	}
}

func TestRustDeskAWrongHashStopsBeforeInstalling(t *testing.T) {
	_, _, mk := installer()
	srv := mk()
	defer srv.Close()
	pc := newFakePC(t, srv)
	var l logs
	_, err := rdInstall(context.Background(), rdConfig{IDServer: "rd.example.com", Installer: srv.URL + "/rustdesk.exe", SHA256: strings.Repeat("a", 64), Mode: "attended"}, pc.env, l.add)
	if err == nil || !strings.Contains(err.Error(), "does not match the SHA-256") || !strings.Contains(err.Error(), strings.Repeat("a", 64)) {
		t.Fatalf("%v", err)
	}
	if pc.called("--silent-install") {
		t.Fatal("an installer that failed the check must not run")
	}
	if _, e := os.Stat(filepath.Join(pc.env.Temp, "deskask-rustdesk-setup.exe")); e == nil {
		t.Fatal("the bad download must be deleted")
	}
}

func TestRustDeskWithoutAHashWarnsAndAttendedSetsNoPassword(t *testing.T) {
	_, _, mk := installer()
	srv := mk()
	defer srv.Close()
	pc := newFakePC(t, srv)
	var l logs
	if _, err := rdInstall(context.Background(), rdConfig{IDServer: "rd.example.com:21116", Installer: srv.URL + "/rustdesk.exe", Password: "x", Mode: "attended"}, pc.env, l.add); err != nil {
		t.Fatalf("%v\n%s", err, l.text())
	}
	if !strings.Contains(l.text(), "warn: No SHA-256 is set") || !strings.Contains(l.text(), "Attended mode") || pc.called("--password") {
		t.Fatalf("%s", l.text())
	}
}

func TestRustDeskWritesTheSettingsFileWhenTheOptionCommandDoesNothing(t *testing.T) {
	_, sum, mk := installer()
	srv := mk()
	defer srv.Close()
	pc := newFakePC(t, srv)
	pc.optionsOK = false
	var l logs
	if _, err := rdInstall(context.Background(), rdConfig{IDServer: "rd.example.com", Relay: "relay.example.com", Key: "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789=", Installer: srv.URL + "/rustdesk.exe", SHA256: sum, Mode: "attended"}, pc.env, l.add); err != nil {
		t.Fatalf("%v\n%s", err, l.text())
	}
	b, err := os.ReadFile(filepath.Join(pc.env.ConfigDirs[0], "RustDesk2.toml"))
	if err != nil {
		t.Fatalf("%v\n%s", err, l.text())
	}
	for _, want := range []string{"rendezvous_server = 'rd.example.com:21116'", "custom-rendezvous-server = 'rd.example.com'", "relay-server = 'relay.example.com'", "key = 'AbCdEf"} {
		if !strings.Contains(string(b), want) {
			t.Errorf("settings file lacks %q:\n%s", want, b)
		}
	}
}

func TestRustDeskReadsTheIDFromTheSettingsFileAndGivesUpWithAReason(t *testing.T) {
	_, sum, mk := installer()
	srv := mk()
	defer srv.Close()
	pc := newFakePC(t, srv)
	pc.id = "" // --get-id prints nothing (a GUI program may not write to a pipe)
	_ = os.MkdirAll(pc.env.ConfigDirs[0], 0o755)
	_ = os.WriteFile(filepath.Join(pc.env.ConfigDirs[0], "RustDesk.toml"), []byte("enc_id = 'x'\nid = '987654321'\n"), 0o644)
	var l logs
	id, err := rdInstall(context.Background(), rdConfig{IDServer: "rd.example.com", Installer: srv.URL + "/rustdesk.exe", SHA256: sum, Mode: "attended"}, pc.env, l.add)
	if err != nil || id != "987654321" {
		t.Fatalf("%v %q", err, id)
	}
	// and with no file at all
	pc2 := newFakePC(t, srv)
	pc2.id = ""
	_, err = rdInstall(context.Background(), rdConfig{IDServer: "rd.example.com", Installer: srv.URL + "/rustdesk.exe", SHA256: sum, Mode: "attended"}, pc2.env, l.add)
	if err == nil || !strings.Contains(err.Error(), "did not report an ID") || !strings.Contains(err.Error(), "21115") {
		t.Fatalf("%v", err)
	}
}

func TestRustDeskRefusesANonHTTPSInstaller(t *testing.T) {
	pc := newFakePC(t, httptest.NewTLSServer(http.NotFoundHandler()))
	var l logs
	_, err := rdInstall(context.Background(), rdConfig{IDServer: "rd.example.com", Installer: "http://example.com/x.exe", Mode: "attended"}, pc.env, l.add)
	if err == nil || !strings.Contains(err.Error(), "https://") {
		t.Fatalf("%v", err)
	}
}

func TestTheRdSetupMessageDecodesAndANonWindowsAgentSaysSo(t *testing.T) {
	var m message
	raw := `{"type":"rdsetup","rd":{"idServer":"rd.example.com","relay":"","key":"k","installer":"https://x/y.exe","sha256":"ab","password":"pw","mode":"unattended"}}`
	if err := jsonUnmarshal(raw, &m); err != nil || m.Type != "rdsetup" || m.RD == nil || m.RD.IDServer != "rd.example.com" || m.RD.Mode != "unattended" || m.RD.Password != "pw" {
		t.Fatalf("%v %+v", err, m)
	}
	var mu sync.Mutex
	var bodies []string
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		b := new(strings.Builder)
		buf := make([]byte, 4096)
		for {
			n, err := r.Body.Read(buf)
			b.Write(buf[:n])
			if err != nil {
				break
			}
		}
		mu.Lock()
		bodies = append(bodies, b.String())
		mu.Unlock()
		_, _ = w.Write([]byte(`{"ok":true}`))
	}))
	defer srv.Close()
	r := newTestRunner(srv.URL)
	r.rdSetup(context.Background(), m.RD, defaultRdEnv())
	mu.Lock()
	defer mu.Unlock()
	all := strings.Join(bodies, "\n")
	if Platform() == "windows" {
		t.Skip("this check is for other systems")
	}
	if !strings.Contains(all, `"state":"log"`) || !strings.Contains(all, `"state":"failed"`) || !strings.Contains(all, "Windows computers only") {
		t.Fatalf("%s", all)
	}
	if strings.Contains(all, `"pw"`) {
		t.Fatal("the password must not be sent back")
	}
}

func newTestRunner(server string) *Runner {
	return NewRunner(config.Agent{Server: server, AgentID: "gAAA", Key: "dkag_secretsecretsecretsecret"}, "1.0.0")
}

func jsonUnmarshal(s string, v any) error { return json.Unmarshal([]byte(s), v) }
