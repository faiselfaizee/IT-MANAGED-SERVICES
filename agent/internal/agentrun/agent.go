package agentrun

import (
	"context"
	"errors"
	"fmt"
	"log"
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

const ConfigFile = "agent.json"

// Platform is the name the server uses for this computer's operating system.
func Platform() string {
	if runtime.GOOS == "windows" {
		return "windows"
	}
	return "linux"
}

type enrolResp struct {
	AgentID string `json:"agentId"`
	Key     string `json:"key"`
}

// Enrol registers this computer with an enrolment token and sends its first inventory. It returns the settings to keep.
func Enrol(ctx context.Context, server, token, version string) (config.Agent, error) {
	server = strings.TrimRight(strings.TrimSpace(server), "/")
	if u, err := url.Parse(server); err != nil || (u.Scheme != "https" && u.Scheme != "http") || u.Host == "" {
		return config.Agent{}, fmt.Errorf("the server address must look like https://yourcompany.deskask.in")
	}
	host, _ := os.Hostname()
	body := map[string]any{"token": token, "hostname": host, "platform": Platform(), "version": version}
	if inv, err := inventory.Collect(ctx); err == nil {
		body["inventory"] = inv
	} else {
		log.Printf("inventory could not be read: %v", err)
	}
	var out enrolResp
	c := &client.Client{Server: server}
	if err := c.Do(ctx, "POST", "/agent/v1/enroll", body, &out); err != nil {
		return config.Agent{}, err
	}
	if out.AgentID == "" || out.Key == "" {
		return config.Agent{}, errors.New("the platform did not return an agent id")
	}
	return config.Agent{Server: server, AgentID: out.AgentID, Key: out.Key}, nil
}

// Install enrols, stores the settings and starts the agent as a service.
func Install(ctx context.Context, server, token, version string) error {
	cfg, err := Enrol(ctx, server, token, version)
	if err != nil {
		return err
	}
	if err := config.Save(ConfigFile, cfg); err != nil {
		return fmt.Errorf("could not save the settings (run as administrator or root): %w", err)
	}
	exe, err := svc.Agent.CopySelf()
	if err != nil {
		return err
	}
	return svc.Agent.Install(exe)
}

type message struct {
	Type    string `json:"type"`
	ID      string `json:"id"`
	Shell   string `json:"shell"`
	Body    string `json:"body"`
	Timeout int    `json:"timeout"`
	Version string `json:"version"`
}

// Runner is the agent's long-running part.
type Runner struct {
	Cfg     config.Agent
	Version string
	// Exit ends the program (so the service manager restarts it); replaced in tests.
	Exit func(code int)

	cl         *client.Client
	mu         sync.Mutex
	cancels    map[string]context.CancelFunc
	collectNow chan struct{}
}

func NewRunner(cfg config.Agent, version string) *Runner {
	return &Runner{Cfg: cfg, Version: version, Exit: os.Exit, cancels: map[string]context.CancelFunc{}, collectNow: make(chan struct{}, 1),
		cl: &client.Client{Server: cfg.Server, IDHeader: "X-Agent-Id", ID: cfg.AgentID, Key: cfg.Key}}
}

// InventoryEvery is how often a fresh inventory is sent without being asked.
var InventoryEvery = 6 * time.Hour

func (r *Runner) Run(ctx context.Context) error {
	checkUpdateGuard(r.Version, r.Exit)
	go r.inventoryLoop(ctx)
	failures := 0
	for ctx.Err() == nil {
		var out struct {
			Msgs []message `json:"msgs"`
		}
		pctx, cancel := context.WithTimeout(ctx, 50*time.Second)
		err := r.cl.Do(pctx, "GET", "/agent/v1/poll?wait=25&v="+url.QueryEscape(r.Version), nil, &out)
		cancel()
		if err != nil && ctx.Err() == nil {
			failures++
			wait := min(time.Second<<min(failures, 6), 60*time.Second)
			if errors.Is(err, client.ErrAuth) {
				log.Printf("the platform refused this agent's key; trying again in 5 minutes")
				wait = 5 * time.Minute
			} else {
				log.Printf("poll failed: %v", err)
			}
			sleep(ctx, wait)
			continue
		}
		failures = 0
		for _, m := range out.Msgs {
			r.handle(ctx, m)
		}
		if len(out.Msgs) == 0 && err == nil {
			sleep(ctx, time.Second) // the long poll normally waits on the server; never spin
		}
	}
	return nil
}

func sleep(ctx context.Context, d time.Duration) {
	select {
	case <-ctx.Done():
	case <-time.After(d):
	}
}

func (r *Runner) handle(ctx context.Context, m message) {
	switch m.Type {
	case "job":
		go r.job(ctx, m)
	case "cancel":
		r.mu.Lock()
		if c := r.cancels[m.ID]; c != nil {
			c()
		}
		r.mu.Unlock()
	case "collect":
		select {
		case r.collectNow <- struct{}{}:
		default:
		}
	case "update":
		go r.update(ctx, m.Version)
	case "uninstall":
		log.Printf("the platform asked this agent to uninstall")
		config.Remove(ConfigFile)
		_ = svc.Agent.Uninstall()
		r.Exit(0)
	default:
		log.Printf("ignoring message %q (not supported by this version)", m.Type)
	}
}

func (r *Runner) job(ctx context.Context, m message) {
	jctx, cancel := context.WithCancel(ctx)
	r.mu.Lock()
	r.cancels[m.ID] = cancel
	r.mu.Unlock()
	defer func() {
		cancel()
		r.mu.Lock()
		delete(r.cancels, m.ID)
		r.mu.Unlock()
	}()
	_ = r.cl.Do(ctx, "POST", "/agent/v1/job", map[string]any{"id": m.ID, "status": "running"}, nil)
	res := Exec(jctx, m.Shell, m.Body, time.Duration(m.Timeout)*time.Second)
	for try := 0; try < 5; try++ { // the answer matters more than speed: keep trying through a short network outage
		err := r.cl.Do(ctx, "POST", "/agent/v1/job", map[string]any{"id": m.ID, "status": res.Status, "exit": res.Exit, "output": res.Output}, nil)
		if err == nil || ctx.Err() != nil {
			return
		}
		sleep(ctx, time.Duration(try+1)*5*time.Second)
	}
}

// inventoryLoop sends the first inventory straight away, then again every InventoryEvery or when the platform asks.
func (r *Runner) inventoryLoop(ctx context.Context) {
	failures := 0
	for ctx.Err() == nil {
		wait := InventoryEvery
		if err := r.SendInventory(ctx); err != nil {
			failures++
			wait = min(time.Minute<<min(failures-1, 5), 30*time.Minute)
			log.Printf("inventory not sent: %v", err)
		} else {
			failures = 0
			markUpdateHealthy()
		}
		select {
		case <-ctx.Done():
		case <-r.collectNow:
		case <-time.After(wait):
		}
	}
}

type checkinResp struct {
	OK     bool `json:"ok"`
	Update *struct {
		Version string `json:"version"`
	} `json:"update"`
}

// SendInventory reads this computer's details and posts them.
func (r *Runner) SendInventory(ctx context.Context) error {
	inv, err := inventory.Collect(ctx)
	if err != nil {
		return err
	}
	var out checkinResp
	if err := r.cl.Do(ctx, "POST", "/agent/v1/checkin", map[string]any{"version": r.Version, "inventory": inv}, &out); err != nil {
		return err
	}
	if out.Update != nil && out.Update.Version != "" {
		go r.update(ctx, out.Update.Version)
	}
	return nil
}
