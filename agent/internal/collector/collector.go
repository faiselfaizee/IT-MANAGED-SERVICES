// Package collector is the controller that sits inside a customer's network. It finds devices, checks that they are up, reads printers,
// switches and firewalls over SNMP, reads computers over SSH or PowerShell, and installs the agent on computers on the platform's request.
// It only ever calls out to the platform; nothing calls in.
package collector

import (
	"context"
	"errors"
	"fmt"
	"log"
	"net"
	"net/url"
	"os"
	"runtime"
	"strings"
	"sync"
	"time"

	"github.com/deskask/platform/agent/internal/client"
	"github.com/deskask/platform/agent/internal/config"
	"github.com/deskask/platform/agent/internal/inventory"
	"github.com/deskask/platform/agent/internal/svc"
)

const ConfigFile = "collector.json"

func Platform() string {
	if runtime.GOOS == "windows" {
		return "windows"
	}
	return "linux"
}

// Enrol registers this computer as a collector with a collector enrolment token.
func Enrol(ctx context.Context, server, token, name, version string) (config.Collector, error) {
	server = strings.TrimRight(strings.TrimSpace(server), "/")
	if u, err := url.Parse(server); err != nil || (u.Scheme != "https" && u.Scheme != "http") || u.Host == "" {
		return config.Collector{}, errors.New("the server address must look like https://yourcompany.deskask.in")
	}
	host, _ := os.Hostname()
	var out struct {
		CollectorID string `json:"collectorId"`
		Key         string `json:"key"`
	}
	c := &client.Client{Server: server}
	if err := c.Do(ctx, "POST", "/collector/v1/enroll", map[string]any{"token": token, "hostname": host, "name": name, "platform": Platform(), "version": version}, &out); err != nil {
		return config.Collector{}, err
	}
	if out.CollectorID == "" || out.Key == "" {
		return config.Collector{}, errors.New("the platform did not return a collector id")
	}
	return config.Collector{Server: server, CollectorID: out.CollectorID, Key: out.Key}, nil
}

// Install enrols and starts the collector as a service. Saved logins from an earlier installation are kept.
func Install(ctx context.Context, server, token, name, version string) error {
	cfg, err := Enrol(ctx, server, token, name, version)
	if err != nil {
		return err
	}
	var old config.Collector
	if config.Load(ConfigFile, &old) == nil {
		cfg.Creds = old.Creds
	}
	if err := config.Save(ConfigFile, cfg); err != nil {
		return fmt.Errorf("could not save the settings (run as administrator or root): %w", err)
	}
	exe, err := svc.Collector.CopySelf()
	if err != nil {
		return err
	}
	return svc.Collector.Install(exe)
}

// ---- what the platform sends and what we send back

type asset struct {
	ID      string  `json:"id"`
	IP      string  `json:"ip"`
	Host    string  `json:"host"`
	Name    string  `json:"name"`
	Monitor string  `json:"monitor"`
	Logins  []login `json:"logins"`
	ReadNow bool    `json:"readNow"` // someone pressed "Read now" on the device page
	// Bmc is the address of the server's management controller (iLO, iDRAC, XClarity), read in addition to the main read.
	Bmc       string  `json:"bmc"`
	BmcLogins []login `json:"bmcLogins"`
}

type deploy struct {
	Asset    string  `json:"asset"`
	IP       string  `json:"ip"`
	Host     string  `json:"host"`
	Platform string  `json:"platform"`
	Logins   []login `json:"logins"`
}

type work struct {
	PollSeconds int                   `json:"pollSeconds"`
	Assets      []asset               `json:"assets"`
	Checks      map[string][]CheckDef `json:"checks"`
	Deploys     []deploy              `json:"deploys"`
	Scan        struct {
		Cidrs []string `json:"cidrs"`
		Now   bool     `json:"now"`
	} `json:"scan"`
	Update *updateInfo `json:"update"`
}

type healthRes struct {
	Asset string  `json:"asset"`
	Up    bool    `json:"up"`
	RTT   float64 `json:"rtt_ms"`
}
type invRes struct {
	Asset string `json:"asset"`
	Raw   any    `json:"raw"`
}
type errRes struct {
	Asset string `json:"asset"`
	Error string `json:"error"`
}
type deployRes struct {
	Asset   string `json:"asset"`
	OK      bool   `json:"ok"`
	Message string `json:"message"`
}
type logLine struct {
	L string `json:"l"`
	M string `json:"m"`
}

type results struct {
	Health      []healthRes   `json:"health"`
	Inventories []invRes      `json:"inventories"`
	Bmc         []invRes      `json:"bmc"` // what a server's management controller reported, merged into the asset
	Errors      []errRes      `json:"errors"`
	Checks      []CheckResult `json:"checks"`
}

// Report is the body of one check-in.
type report struct {
	Version  string      `json:"version"`
	Hostname string      `json:"hostname"`
	Platform string      `json:"platform"`
	Results  results     `json:"results"`
	Found    []Found     `json:"found"`
	Deploys  []deployRes `json:"deploys,omitempty"`
	Log      []logLine   `json:"log,omitempty"`
}

// Runner is the collector's long-running part.
type Runner struct {
	Cfg     config.Collector
	Version string

	// InventoryEvery is how often devices are read in full; ScanEvery how often a configured range is scanned again.
	InventoryEvery time.Duration
	ScanEvery      time.Duration

	// Exit ends the program so the service manager starts it again (replaced in tests).
	Exit func(code int)
	// RestartSoon asks the system to start the program again after it ends (Windows); nil in tests.
	RestartSoon func() error

	cl       *client.Client
	mu       sync.Mutex
	pending  report
	hasScan  bool
	lastInv  map[string]time.Time
	lastScan time.Time
	scanning bool

	wake      chan struct{}   // a result is ready or the platform has new work: check in now
	deploying map[string]bool // installs in flight, by asset id (the platform keeps handing out nothing twice, this guards a retry)
}

func NewRunner(cfg config.Collector, version string) *Runner {
	return &Runner{Cfg: cfg, Version: version, Exit: os.Exit, RestartSoon: svc.Collector.RestartSoon, InventoryEvery: 6 * time.Hour, ScanEvery: 24 * time.Hour, lastInv: map[string]time.Time{},
		wake: make(chan struct{}, 1), deploying: map[string]bool{},
		cl: &client.Client{Server: cfg.Server, IDHeader: "X-Collector-Id", ID: cfg.CollectorID, Key: cfg.Key}}
}

func (r *Runner) logf(level, format string, a ...any) {
	m := fmt.Sprintf(format, a...)
	log.Print(m)
	r.mu.Lock()
	r.pending.Log = append(r.pending.Log, logLine{level, trim(m)})
	r.mu.Unlock()
}

// Run checks in with the platform again and again until ctx ends.
func (r *Runner) Run(ctx context.Context) error {
	checkUpdateGuard(r.Version, r.restart)
	go func() { // tasks made by controllers before 2.9.3 have no watchdog: add it once
		if err := svc.Collector.EnsureWatchdog(); err != nil {
			log.Printf("watchdog: %v", err)
		}
	}()
	go r.wakeLoop(ctx)
	failures := 0
	for ctx.Err() == nil {
		w, err := r.CheckIn(ctx)
		wait := 60 * time.Second
		switch {
		case err != nil && ctx.Err() == nil:
			failures++
			wait = min(time.Second<<min(failures, 6), 60*time.Second)
			if errors.Is(err, client.ErrAuth) {
				wait = 5 * time.Minute
				log.Printf("the platform refused this collector's key; trying again in 5 minutes")
			} else {
				log.Printf("check-in failed: %v", err)
			}
		case err == nil:
			failures = 0
			markUpdateHealthy()
			if w.Update != nil {
				go r.selfUpdate(ctx, w.Update.Version)
			}
			if w.PollSeconds >= 15 {
				wait = time.Duration(w.PollSeconds) * time.Second
			}
			start := time.Now()
			r.DoWork(ctx, w)
			wait = max(wait-time.Since(start), 5*time.Second)
		}
		select {
		case <-ctx.Done():
		case <-r.wake:
		case <-time.After(wait):
		}
	}
	return nil
}

// CheckIn sends what has been collected since the last time and receives the next list of work.
func (r *Runner) CheckIn(ctx context.Context) (*work, error) {
	r.mu.Lock()
	rep := r.pending
	r.pending = report{}
	rep.Version, rep.Platform = r.Version, Platform()
	rep.Hostname, _ = os.Hostname()
	hadScan := r.hasScan
	if !hadScan {
		rep.Found = nil
	}
	r.hasScan = false
	r.mu.Unlock()
	var w work
	if err := r.cl.Do(ctx, "POST", "/collector/v1/checkin", rep, &w); err != nil {
		r.mu.Lock() // keep what we have for the next try (bounded, so a long outage cannot grow without limit)
		r.pending = merge(rep, r.pending)
		r.hasScan = r.hasScan || hadScan
		r.mu.Unlock()
		return nil, err
	}
	return &w, nil
}

func merge(old, cur report) report {
	cap1 := func(n int) int { return min(n, 5000) }
	old.Results.Health = append(old.Results.Health, cur.Results.Health...)
	if len(old.Results.Health) > 5000 {
		old.Results.Health = old.Results.Health[len(old.Results.Health)-5000:]
	}
	old.Results.Inventories = append(old.Results.Inventories, cur.Results.Inventories...)
	old.Results.Errors = append(old.Results.Errors, cur.Results.Errors...)
	old.Results.Checks = append(old.Results.Checks, cur.Results.Checks...)
	if len(old.Results.Checks) > cap1(len(old.Results.Checks)) {
		old.Results.Checks = old.Results.Checks[len(old.Results.Checks)-5000:]
	}
	if cur.Found != nil {
		old.Found = cur.Found
	}
	old.Deploys = append(old.Deploys, cur.Deploys...)
	old.Log = append(old.Log, cur.Log...)
	return old
}

// DoWork carries out one list of work and queues the results for the next check-in.
func (r *Runner) DoWork(ctx context.Context, w *work) {
	sem := make(chan struct{}, 12)
	var wg sync.WaitGroup
	for _, a := range w.Assets {
		a := a
		wg.Add(1)
		sem <- struct{}{}
		go func() {
			defer func() { <-sem; wg.Done() }()
			r.watch(ctx, a, w.Checks[a.ID])
		}()
	}
	wg.Wait()
	if len(w.Deploys) > 0 {
		r.deploy(ctx, w.Deploys)
	}
	r.maybeScan(ctx, w)
}

func (r *Runner) watch(ctx context.Context, a asset, checks []CheckDef) {
	target := a.IP
	if target == "" {
		target = a.Host
	}
	if target == "" {
		return
	}
	up, rtt := Health(ctx, target)
	var res []CheckResult
	for _, d := range checks {
		cr := RunCheck(ctx, d)
		cr.Asset = a.ID
		res = append(res, cr)
	}
	r.mu.Lock()
	r.pending.Results.Health = append(r.pending.Results.Health, healthRes{a.ID, up, rtt})
	r.pending.Results.Checks = append(r.pending.Results.Checks, res...)
	key := a.ID + "|" + a.Monitor // a device switched to another way of reading is read at once
	since := time.Since(r.lastInv[key])
	due := up && (since >= r.InventoryEvery || a.ReadNow && since >= readNowGap)
	if due {
		r.lastInv[key] = time.Now() // claimed now so a slow read is not started twice
	}
	bkey := a.ID + "|bmc|" + a.Bmc
	bsince := time.Since(r.lastInv[bkey])
	bdue := a.Bmc != "" && (bsince >= r.InventoryEvery || a.ReadNow && bsince >= readNowGap)
	if bdue {
		r.lastInv[bkey] = time.Now()
	}
	r.mu.Unlock()
	if bdue {
		r.readBmc(ctx, a, bkey)
	}
	if !due || a.Monitor == "ping" || a.Monitor == "device" || a.Monitor == "" {
		return
	}
	raw, err := r.readDevice(ctx, a, target)
	r.mu.Lock()
	defer r.mu.Unlock()
	if err != nil {
		delete(r.lastInv, a.ID+"|"+a.Monitor) // try again at the next check-in rather than in six hours
		r.pending.Results.Errors = append(r.pending.Results.Errors, errRes{a.ID, err.Error()})
		return
	}
	r.pending.Results.Inventories = append(r.pending.Results.Inventories, invRes{a.ID, raw})
}

// readBmc reads the management controller of a server that is also read another way (VMware, Windows, an agent).
func (r *Runner) readBmc(ctx context.Context, a asset, key string) {
	raw, err := redfishWith(ctx, r.credsFor(a.BmcLogins), a.Bmc)
	r.mu.Lock()
	defer r.mu.Unlock()
	if err != nil {
		delete(r.lastInv, key)
		r.pending.Results.Errors = append(r.pending.Results.Errors, errRes{a.ID, "Management controller " + a.Bmc + ": " + err.Error()})
		return
	}
	r.pending.Results.Bmc = append(r.pending.Results.Bmc, invRes{a.ID, raw})
}

func redfishWith(ctx context.Context, creds []config.Cred, target string) (map[string]any, error) {
	var last error = errors.New("no server management login is saved for this customer: add one under Assets > Credentials (hardware category Physical servers, used for Server management)")
	for _, c := range pick(creds, "bmc", hostOnly(target)) {
		inv, err := RedfishInventory(ctx, target, c.User, c.Password)
		if err == nil {
			return inv, nil
		}
		last = fmt.Errorf("login %q: %v", c.Name, err)
		if !errors.Is(err, errRfLogin) {
			break // the controller itself is the problem, another password will not help
		}
	}
	return nil, last
}

func hostOnly(t string) string {
	if h, _, err := net.SplitHostPort(t); err == nil {
		return h
	}
	return t
}

// apiReaders are the hypervisors read through their own management API, by read mode (the login kind has the same name).
var apiReaders = map[string]struct {
	name string
	read func(ctx context.Context, host, user, pass string) (map[string]any, error)
}{
	"vmware":  {"VMware", VMwareInventory},
	"nutanix": {"Nutanix", NutanixInventory},
	"proxmox": {"Proxmox VE", ProxmoxInventory},
	"xen":     {"XCP-ng / Citrix Hypervisor", XenInventory},
}

// readNowGap keeps "Read now" from starting a second read while the first is still running.
const readNowGap = 2 * time.Minute

func (r *Runner) readDevice(ctx context.Context, a asset, target string) (any, error) {
	switch a.Monitor {
	case "snmp":
		var last error = errors.New("no SNMP login is saved on the collector for this address (collector-cred add --kind snmp --community ...)")
		for _, c := range pick(r.credsFor(a.Logins), "snmp", target) {
			inv, err := SNMPInventory(target, c.Community, firstNonEmpty(a.Host, a.Name))
			if err == nil {
				return inv, nil
			}
			last = err
		}
		return nil, last
	case "ssh":
		return sshInventory(ctx, r.credsFor(a.Logins), target)
	case "windows":
		return winInventory(ctx, r.credsFor(a.Logins), hostsOf(a.Host, target)...)
	case "bmc":
		return redfishWith(ctx, r.credsFor(a.Logins), target)
	case "vmware", "nutanix", "proxmox", "xen":
		h := apiReaders[a.Monitor]
		var last error = fmt.Errorf("no %s login is saved for this customer: add one under Assets > Credentials (hardware category Hypervisors, used for %s)", h.name, h.name)
		for _, c := range pick(r.credsFor(a.Logins), a.Monitor, hostOnly(target)) {
			inv, err := h.read(ctx, target, c.User, c.Password)
			if err == nil {
				return inv, nil
			}
			last = fmt.Errorf("%s login %q: %v", h.name, c.Name, err)
			if !errors.Is(err, errVsLogin) && !errors.Is(err, errHvLogin) {
				break // the system itself is the problem, another password will not help
			}
		}
		return nil, last
	}
	return nil, fmt.Errorf("unknown monitor kind %q", a.Monitor)
}

func firstNonEmpty(s ...string) string {
	for _, x := range s {
		if x != "" {
			return x
		}
	}
	return ""
}

// ---- push the agent

// deploy starts the installs in the background, a few at a time, and returns at once: the check-in loop keeps running, every step is reported
// as it happens (see progress.go) and the result goes back the moment the install ends.
func (r *Runner) deploy(ctx context.Context, list []deploy) {
	var fresh []deploy
	r.mu.Lock()
	for _, d := range list {
		if r.deploying[d.Asset] {
			continue
		}
		r.deploying[d.Asset] = true
		fresh = append(fresh, d)
	}
	r.mu.Unlock()
	if len(fresh) == 0 {
		return
	}
	go func() {
		sem := make(chan struct{}, 4)
		var wg sync.WaitGroup
		for _, d := range fresh {
			d := d
			wg.Add(1)
			sem <- struct{}{}
			go func() {
				defer func() { <-sem; wg.Done() }()
				r.deployOne(ctx, d)
			}()
		}
		wg.Wait()
	}()
}

func (r *Runner) deployOne(ctx context.Context, d deploy) {
	defer func() {
		r.mu.Lock()
		delete(r.deploying, d.Asset)
		r.mu.Unlock()
	}()
	name := firstNonEmpty(d.Host, d.IP)
	sctx := withSteps(ctx, func(level, msg string) { r.progress(ctx, d.Asset, level, msg) })
	host, _ := os.Hostname()
	step(sctx, "info", "Controller %s (version %s, %s) started the install on %s", host, r.Version, Platform(), name)
	var tok struct {
		Token string `json:"token"`
	}
	var msg string
	var err error
	if e := r.cl.Do(ctx, "POST", "/collector/v1/agent-token", nil, &tok); e != nil || tok.Token == "" {
		err = errors.New("could not get an install token from the platform")
	} else {
		step(sctx, "info", "Install token ready (valid 24 hours)")
		msg, err = r.install(sctx, d, tok.Token)
	}
	r.mu.Lock()
	if err != nil {
		r.pending.Deploys = append(r.pending.Deploys, deployRes{d.Asset, false, trim(err.Error())})
	} else {
		r.pending.Deploys = append(r.pending.Deploys, deployRes{d.Asset, true, trim(msg)})
	}
	r.mu.Unlock()
	outcome := "done"
	if err != nil {
		outcome = "failed - " + trim(err.Error())
	}
	r.logf("info", "Install on %s: %s", name, outcome)
	r.kick() // report the result now, not at the end of the minute
}

func (r *Runner) install(ctx context.Context, d deploy, token string) (string, error) {
	target := firstNonEmpty(d.IP, d.Host)
	kind := d.Platform
	if kind == "" || (kind != "windows" && kind != "linux") {
		switch {
		case func() bool { ok, _ := TCPOpen(ctx, target, 5985, 1500*time.Millisecond); return ok }() || func() bool { ok, _ := TCPOpen(ctx, target, 445, 1500*time.Millisecond); return ok }():
			kind = "windows"
		case func() bool { ok, _ := TCPOpen(ctx, target, 22, 1500*time.Millisecond); return ok }():
			kind = "linux"
		default:
			return "", errors.New("could not tell whether this is a Windows or Linux computer (no remote management port answered)")
		}
		step(ctx, "info", "Looks like a %s computer (decided from the ports that answer on %s)", kind, target)
	} else {
		step(ctx, "info", "The platform says this is a %s computer (%s)", kind, target)
	}
	base := strings.TrimRight(r.Cfg.Server, "/")
	step(ctx, "info", "Logins available: %d (saved on the platform for this customer, plus any saved on the controller)", len(r.credsFor(d.Logins)))
	if kind == "windows" {
		return winInstall(ctx, r.credsFor(d.Logins), hostsOf(d.Host, target), base+"/agent/install.ps1?t="+url.QueryEscape(token))
	}
	return sshInstall(ctx, r.credsFor(d.Logins), target, base+"/agent/install.sh?t="+url.QueryEscape(token))
}

// ---- scanning

func (r *Runner) maybeScan(ctx context.Context, w *work) {
	if len(w.Scan.Cidrs) == 0 && !w.Scan.Now {
		return
	}
	r.mu.Lock()
	due := w.Scan.Now || time.Since(r.lastScan) >= r.ScanEvery
	if !due || r.scanning || len(w.Scan.Cidrs) == 0 {
		r.mu.Unlock()
		return
	}
	r.scanning = true
	r.lastScan = time.Now()
	r.mu.Unlock()
	go r.scanNow(ctx, w.Scan.Cidrs) // in the background, so an install pressed meanwhile is not held up by the scan
}

func (r *Runner) scanNow(ctx context.Context, cidrs []string) {
	r.logf("info", "Scanning %s", strings.Join(cidrs, ", "))
	found, warn := Scan(ctx, cidrs, 64)
	for _, m := range warn {
		r.logf("warn", "%s", m)
	}
	r.mu.Lock()
	r.pending.Found = append([]Found{}, found...) // an empty list still tells the platform the scan is done
	r.hasScan = true
	r.scanning = false
	r.mu.Unlock()
	r.logf("info", "Scan finished: %d device(s) found", len(found))
	r.kick() // send what was found now
}

// Inventory is exported for the "inventory" command (print this computer's inventory as JSON).
func Inventory(ctx context.Context) ([]byte, error) {
	inv, err := inventory.Collect(ctx)
	if err != nil {
		return nil, err
	}
	return inv.JSON(), nil
}
