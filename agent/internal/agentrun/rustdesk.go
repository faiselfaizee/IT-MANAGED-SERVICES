package agentrun

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"strings"
	"time"
)

// Remote control with a self-hosted RustDesk (Windows). The platform sends "rdsetup" with the servers, the installer address and its SHA-256;
// the agent downloads and checks the installer, installs it silently, points it at the servers, sets the password (unattended mode) and reads
// the RustDesk ID back. Every step is sent to the platform as it happens so the person who pressed the button sees where it is.

type rdConfig struct {
	IDServer  string `json:"idServer"`
	Relay     string `json:"relay"`
	Key       string `json:"key"`
	Installer string `json:"installer"`
	SHA256    string `json:"sha256"`
	Password  string `json:"password"`
	Mode      string `json:"mode"`
}

// rdEnv is everything the set-up touches outside this program, so a test can replace it.
type rdEnv struct {
	Exe        string // the installed RustDesk program
	Temp       string // where the installer is saved
	HTTP       *http.Client
	Run        func(ctx context.Context, limit time.Duration, name string, args ...string) (string, error)
	ConfigDirs []string      // folders that hold RustDesk.toml and RustDesk2.toml (the service's first)
	Poll       time.Duration // how often to look again while waiting
	Wait       time.Duration // how long to wait for the service and the ID
}

func defaultRdEnv() rdEnv {
	pf := os.Getenv("ProgramFiles")
	if pf == "" {
		pf = `C:\Program Files`
	}
	root := os.Getenv("SystemRoot")
	if root == "" {
		root = `C:\Windows`
	}
	return rdEnv{
		Exe:  filepath.Join(pf, "RustDesk", "rustdesk.exe"),
		Temp: os.TempDir(),
		HTTP: &http.Client{Timeout: 20 * time.Minute},
		Run:  rdRun,
		ConfigDirs: []string{
			filepath.Join(root, "ServiceProfiles", "LocalService", "AppData", "Roaming", "RustDesk", "config"),
			filepath.Join(root, "System32", "config", "systemprofile", "AppData", "Roaming", "RustDesk", "config"),
		},
		Poll: 3 * time.Second, Wait: 90 * time.Second,
	}
}

// rdRun runs a program and returns what it printed. The output goes to a file, not a pipe: RustDesk's installer starts
// rustdesk.exe, which inherits the output handles and keeps running, so waiting for a pipe to close would never end
// ("WaitDelay expired before I/O complete") even though the installer itself finished well.
func rdRun(ctx context.Context, limit time.Duration, name string, args ...string) (string, error) {
	ctx, cancel := context.WithTimeout(ctx, limit)
	defer cancel()
	f, err := os.CreateTemp("", "deskask-rd-*.log")
	if err != nil {
		return "", err
	}
	defer os.Remove(f.Name())
	defer f.Close()
	cmd := exec.CommandContext(ctx, name, args...)
	cmd.Stdout, cmd.Stderr = f, f
	killTree(cmd)
	err = cmd.Run()
	if errors.Is(err, exec.ErrWaitDelay) {
		err = nil // the program itself ended with success; only a child it left running still holds the output
	}
	if ctx.Err() != nil {
		err = fmt.Errorf("stopped after %s", limit)
	}
	b, _ := os.ReadFile(f.Name())
	return strings.TrimSpace(string(b)), err
}

type rdLogger func(level, format string, a ...any)

var rdIDPattern = regexp.MustCompile(`^\d{6,12}$`)

// rdInstall does the whole set-up and returns the RustDesk ID.
func rdInstall(ctx context.Context, c rdConfig, env rdEnv, logf rdLogger) (string, error) {
	logf("info", "Settings from the platform: server %s, relay %s, mode %s, installer %s", c.IDServer, firstNonEmpty(c.Relay, "(none)"), c.Mode, c.Installer)

	// 1. the program
	if _, err := os.Stat(env.Exe); err == nil {
		logf("info", "RustDesk is already installed (%s): skipping the download", env.Exe)
	} else {
		if !strings.HasPrefix(strings.ToLower(c.Installer), "https://") {
			return "", errors.New("the installer address must start with https://")
		}
		dest := filepath.Join(env.Temp, "deskask-rustdesk-setup.exe")
		defer os.Remove(dest)
		if err := rdDownload(ctx, env.HTTP, c.Installer, dest, strings.ToLower(strings.TrimSpace(c.SHA256)), logf); err != nil {
			return "", err
		}
		logf("info", "Installing RustDesk silently (this takes up to a minute)")
		out, err := env.Run(ctx, 6*time.Minute, dest, "--silent-install")
		if out != "" {
			logf("info", "Installer said: %s", clip(out, 300))
		}
		if err != nil {
			return "", fmt.Errorf("the installer failed: %v", err)
		}
		if !rdWait(ctx, env, func() bool { _, e := os.Stat(env.Exe); return e == nil }) {
			return "", fmt.Errorf("the installer finished but %s does not exist", env.Exe)
		}
		logf("info", "RustDesk installed at %s", env.Exe)
	}

	// 2. the service
	if err := rdService(ctx, env, logf); err != nil {
		return "", err
	}

	// 3. servers
	opts := [][2]string{{"custom-rendezvous-server", c.IDServer}}
	if c.Relay != "" {
		opts = append(opts, [2]string{"relay-server", c.Relay})
	}
	if c.Key != "" {
		opts = append(opts, [2]string{"key", c.Key})
	}
	for _, o := range opts {
		if out, err := env.Run(ctx, time.Minute, env.Exe, "--option", o[0], o[1]); err != nil {
			logf("warn", "Could not set %s with RustDesk's own command: %v %s", o[0], err, clip(out, 200))
		} else {
			logf("info", "Set %s", o[0])
		}
	}
	if !rdHasServer(env, c.IDServer) {
		logf("warn", "RustDesk's settings file does not show the server yet: writing it directly")
		if err := rdWriteConfig(ctx, env, c, logf); err != nil {
			logf("warn", "Could not write the settings file: %v", err)
		}
	} else {
		logf("info", "Checked: RustDesk's settings file holds the server %s", c.IDServer)
	}

	// 4. password
	if c.Mode == "attended" || c.Password == "" {
		logf("info", "Attended mode: no permanent password is set; the person at the computer approves each connection")
	} else if out, err := env.Run(ctx, time.Minute, env.Exe, "--password", c.Password); err != nil {
		return "", fmt.Errorf("could not set the unattended password: %v %s", err, clip(out, 200))
	} else {
		logf("info", "Permanent password set for unattended access")
	}

	// 5. the ID
	var id string
	deadline := time.Now().Add(env.Wait)
	for n := 1; ; n++ {
		id = rdReadID(ctx, env)
		if id != "" {
			break
		}
		if time.Now().After(deadline) || ctx.Err() != nil {
			return "", errors.New("RustDesk is installed but did not report an ID within " + env.Wait.String() + ". Check that this computer can reach " + c.IDServer + " (ports 21115 to 21117) and that the public key is right")
		}
		logf("info", "Waiting for RustDesk to register with %s (try %d)", c.IDServer, n)
		sleepFor(ctx, env.Poll)
	}
	logf("info", "RustDesk ID is %s", id)
	return id, nil
}

func clip(s string, n int) string {
	s = strings.Join(strings.Fields(s), " ")
	if len(s) > n {
		return s[:n] + "…"
	}
	return s
}

func firstNonEmpty(s ...string) string {
	for _, x := range s {
		if x != "" {
			return x
		}
	}
	return ""
}

func sleepFor(ctx context.Context, d time.Duration) {
	select {
	case <-ctx.Done():
	case <-time.After(d):
	}
}

func rdWait(ctx context.Context, env rdEnv, ok func() bool) bool {
	deadline := time.Now().Add(env.Wait)
	for {
		if ok() {
			return true
		}
		if time.Now().After(deadline) || ctx.Err() != nil {
			return false
		}
		sleepFor(ctx, env.Poll)
	}
}

// rdDownload saves the installer, checks its SHA-256 and says how far it got.
func rdDownload(ctx context.Context, hc *http.Client, url, dest, want string, logf rdLogger) error {
	logf("info", "Downloading %s", url)
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		return err
	}
	resp, err := hc.Do(req)
	if err != nil {
		return fmt.Errorf("the download failed: %v (can this computer reach the internet and %s?)", err, req.URL.Host)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("the download failed: the server answered %d for %s", resp.StatusCode, url)
	}
	f, err := os.Create(dest)
	if err != nil {
		return err
	}
	h := sha256.New()
	total := resp.ContentLength
	var got, next int64
	buf := make([]byte, 256<<10)
	for {
		n, rerr := resp.Body.Read(buf)
		if n > 0 {
			if _, werr := f.Write(buf[:n]); werr != nil {
				f.Close()
				return werr
			}
			h.Write(buf[:n])
			got += int64(n)
			if got >= next {
				if total > 0 {
					logf("info", "Downloaded %d of %d MB (%d%%)", got>>20, total>>20, got*100/total)
					next = got + total/5 + 1
				} else {
					logf("info", "Downloaded %d MB", got>>20)
					next = got + 5<<20
				}
			}
		}
		if rerr == io.EOF {
			break
		}
		if rerr != nil {
			f.Close()
			return fmt.Errorf("the download stopped: %v", rerr)
		}
	}
	if err := f.Close(); err != nil {
		return err
	}
	have := hex.EncodeToString(h.Sum(nil))
	switch {
	case want == "":
		logf("warn", "No SHA-256 is set under Settings, so the download is not verified (its SHA-256 is %s)", have)
	case have != want:
		os.Remove(dest)
		return fmt.Errorf("the downloaded file does not match the SHA-256 set under Settings (expected %s, got %s). Nothing was installed", want, have)
	default:
		logf("info", "SHA-256 matches the value set under Settings")
	}
	return nil
}

// rdService makes sure the RustDesk service is running (it is what lets the computer be reached without anyone logged in).
func rdService(ctx context.Context, env rdEnv, logf rdLogger) error {
	running := func() bool {
		out, _ := env.Run(ctx, 20*time.Second, "sc", "query", "RustDesk")
		return strings.Contains(out, "RUNNING")
	}
	if running() {
		logf("info", "The RustDesk service is running")
		return nil
	}
	logf("info", "Starting the RustDesk service")
	_, _ = env.Run(ctx, 30*time.Second, "sc", "start", "RustDesk")
	if !rdWait(ctx, env, running) {
		return errors.New("the RustDesk service did not start (look at Services on the computer)")
	}
	logf("info", "The RustDesk service is running")
	return nil
}

func rdTomlFiles(env rdEnv, name string) []string {
	var out []string
	for _, d := range env.ConfigDirs {
		out = append(out, filepath.Join(d, name))
	}
	return out
}

// rdHasServer tells whether RustDesk's own settings file already holds the server (so the --option command worked).
func rdHasServer(env rdEnv, server string) bool {
	host := strings.Split(server, ":")[0]
	for _, f := range rdTomlFiles(env, "RustDesk2.toml") {
		if b, err := os.ReadFile(f); err == nil && strings.Contains(string(b), host) {
			return true
		}
	}
	return false
}

// rdToml is the settings file RustDesk reads for a self-hosted server.
func rdToml(c rdConfig) string {
	host := strings.Split(c.IDServer, ":")[0]
	rv := c.IDServer
	if !strings.Contains(rv, ":") {
		rv += ":21116"
	}
	var b strings.Builder
	fmt.Fprintf(&b, "rendezvous_server = '%s'\nnat_type = 1\nserial = 0\n\n[options]\ncustom-rendezvous-server = '%s'\n", rv, host)
	if c.Relay != "" {
		fmt.Fprintf(&b, "relay-server = '%s'\n", strings.Split(c.Relay, ":")[0])
	}
	if c.Key != "" {
		fmt.Fprintf(&b, "key = '%s'\n", c.Key)
	}
	return b.String()
}

// rdWriteConfig writes the settings file for the service with the service stopped, then starts it again.
func rdWriteConfig(ctx context.Context, env rdEnv, c rdConfig, logf rdLogger) error {
	_, _ = env.Run(ctx, 30*time.Second, "sc", "stop", "RustDesk")
	sleepFor(ctx, env.Poll)
	var last error
	wrote := 0
	for _, f := range rdTomlFiles(env, "RustDesk2.toml") {
		if err := os.MkdirAll(filepath.Dir(f), 0o755); err != nil {
			last = err
			continue
		}
		if err := os.WriteFile(f, []byte(rdToml(c)), 0o644); err != nil {
			last = err
			continue
		}
		wrote++
		logf("info", "Wrote %s", f)
	}
	_, _ = env.Run(ctx, 30*time.Second, "sc", "start", "RustDesk")
	if wrote == 0 {
		return last
	}
	return nil
}

// rdReadID asks RustDesk for its ID, and falls back to its settings file.
func rdReadID(ctx context.Context, env rdEnv) string {
	if out, err := env.Run(ctx, 30*time.Second, env.Exe, "--get-id"); err == nil {
		lines := strings.Split(strings.TrimSpace(out), "\n")
		if id := strings.TrimSpace(lines[len(lines)-1]); rdIDPattern.MatchString(id) {
			return id
		}
	}
	re := regexp.MustCompile(`(?m)^id\s*=\s*'(\d{6,12})'`)
	for _, f := range rdTomlFiles(env, "RustDesk.toml") {
		if b, err := os.ReadFile(f); err == nil {
			if m := re.FindSubmatch(b); m != nil {
				return string(m[1])
			}
		}
	}
	return ""
}

// rdUninstall removes RustDesk.
func rdUninstall(ctx context.Context, env rdEnv, logf rdLogger) error {
	if _, err := os.Stat(env.Exe); err != nil {
		logf("info", "RustDesk is not installed on this computer")
		return nil
	}
	logf("info", "Stopping the RustDesk service")
	_, _ = env.Run(ctx, 30*time.Second, "sc", "stop", "RustDesk")
	logf("info", "Uninstalling RustDesk")
	if out, err := env.Run(ctx, 3*time.Minute, env.Exe, "--uninstall"); err != nil {
		logf("warn", "RustDesk's own uninstall reported: %v %s", err, clip(out, 200))
	}
	if rdWait(ctx, env, func() bool { _, e := os.Stat(env.Exe); return e != nil }) {
		logf("info", "RustDesk is removed")
		return nil
	}
	logf("warn", "The uninstaller left files behind: removing the program and its service directly")
	_, _ = env.Run(ctx, 30*time.Second, "taskkill", "/F", "/IM", "rustdesk.exe")
	_, _ = env.Run(ctx, 30*time.Second, "sc", "delete", "RustDesk")
	if err := os.RemoveAll(filepath.Dir(env.Exe)); err != nil {
		return fmt.Errorf("could not remove %s: %v", filepath.Dir(env.Exe), err)
	}
	logf("info", "RustDesk is removed")
	return nil
}

// ---- the agent's side: messages from the platform and reports back

func (r *Runner) rdPost(ctx context.Context, body map[string]any) {
	pctx, cancel := context.WithTimeout(ctx, 15*time.Second)
	defer cancel()
	_ = r.cl.Do(pctx, "POST", "/agent/v1/remote", body, nil)
}

func (r *Runner) rdLogf(ctx context.Context, level, format string, a ...any) {
	if level != "warn" {
		level = "info"
	}
	r.rdPost(ctx, map[string]any{"state": "log", "level": level, "msg": clip(fmt.Sprintf(format, a...), 380)})
}

// rdBegin makes sure only one remote-control job runs at a time.
func (r *Runner) rdBegin() bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.rdBusy {
		return false
	}
	r.rdBusy = true
	return true
}

func (r *Runner) rdEnd() {
	r.mu.Lock()
	r.rdBusy = false
	r.mu.Unlock()
}

func (r *Runner) rdSetup(ctx context.Context, c *rdConfig, env rdEnv) {
	if c == nil {
		return
	}
	if !r.rdBegin() {
		r.rdLogf(ctx, "warn", "A remote-control job is already running on this computer; ignoring the new request")
		return
	}
	defer r.rdEnd()
	logf := func(level, format string, a ...any) { r.rdLogf(ctx, level, format, a...) }
	logf("info", "The agent (version %s) picked up the request", r.Version)
	if Platform() != "windows" {
		r.rdPost(ctx, map[string]any{"state": "failed", "msg": "Remote control is available for Windows computers only for now."})
		return
	}
	if c.IDServer == "" {
		r.rdPost(ctx, map[string]any{"state": "failed", "msg": "The platform sent no RustDesk server address (Settings > Remote control)."})
		return
	}
	id, err := rdInstall(ctx, *c, env, logf)
	if err != nil {
		logf("warn", "Failed: %v", err)
		r.rdPost(ctx, map[string]any{"state": "failed", "msg": err.Error()})
		return
	}
	logf("info", "Ready. Press Connect on the device page.")
	r.rdPost(ctx, map[string]any{"state": "ready", "id": id})
}

func (r *Runner) rdRemove(ctx context.Context, env rdEnv) {
	if !r.rdBegin() {
		return
	}
	defer r.rdEnd()
	logf := func(level, format string, a ...any) { r.rdLogf(ctx, level, format, a...) }
	if Platform() != "windows" {
		r.rdPost(ctx, map[string]any{"state": "removed"})
		return
	}
	if err := rdUninstall(ctx, env, logf); err != nil {
		logf("warn", "Failed: %v", err)
		r.rdPost(ctx, map[string]any{"state": "failed", "msg": err.Error()})
		return
	}
	r.rdPost(ctx, map[string]any{"state": "removed"})
}
