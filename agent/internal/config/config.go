// Package config keeps the agent's and the collector's settings in small JSON files that only administrators (root, SYSTEM) can read.
package config

import (
	"encoding/json"
	"os"
	"path/filepath"
	"runtime"
)

// Dir is where settings live: C:\ProgramData\DeskAsk on Windows, /etc/deskask elsewhere. DESKASK_CONFIG_DIR overrides it (used by tests).
func Dir() string {
	if d := os.Getenv("DESKASK_CONFIG_DIR"); d != "" {
		return d
	}
	if runtime.GOOS == "windows" {
		pd := os.Getenv("ProgramData")
		if pd == "" {
			pd = `C:\ProgramData`
		}
		return filepath.Join(pd, "DeskAsk")
	}
	return "/etc/deskask"
}

type Agent struct {
	Server  string `json:"server"`
	AgentID string `json:"agentId"`
	Key     string `json:"key"`
}

// Cred is one login the collector may use on devices in its network. It never leaves the collector's computer.
type Cred struct {
	Name      string `json:"name"`
	Kind      string `json:"kind"` // windows, ssh, snmp
	User      string `json:"user,omitempty"`
	Password  string `json:"password,omitempty"`
	KeyFile   string `json:"keyFile,omitempty"`   // ssh private key path
	Community string `json:"community,omitempty"` // snmp v2c
	CIDR      string `json:"cidr,omitempty"`      // limit to a network; empty means any device
	Vendor    string `json:"vendor,omitempty"`    // for network device logins: Fortinet, Cisco Meraki, Palo Alto...
}

type Collector struct {
	Server      string `json:"server"`
	CollectorID string `json:"collectorId"`
	Key         string `json:"key"`
	Creds       []Cred `json:"creds,omitempty"`
}

func Load(file string, v any) error {
	b, err := os.ReadFile(filepath.Join(Dir(), file))
	if err != nil {
		return err
	}
	return json.Unmarshal(b, v)
}

// Save writes the file readable by its owner only.
func Save(file string, v any) error {
	if err := os.MkdirAll(Dir(), 0o755); err != nil {
		return err
	}
	b, _ := json.MarshalIndent(v, "", "  ")
	p := filepath.Join(Dir(), file)
	tmp := p + ".tmp"
	if err := os.WriteFile(tmp, b, 0o600); err != nil {
		return err
	}
	if err := os.Rename(tmp, p); err != nil {
		return err
	}
	return restrict(p)
}

func Remove(file string) { _ = os.Remove(filepath.Join(Dir(), file)) }
