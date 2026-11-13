// Package inventory collects what the platform wants to know about a computer: make, model, serial number, CPU and sockets, memory,
// disks and partitions, network adapters with their addresses, installed applications and versions. The result is the JSON the
// Assets module reads (see Inventory.Normalize on the server). Only the standard library is used.
package inventory

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"runtime"
	"sort"
	"strings"
	"time"
)

type Partition struct {
	Name      string `json:"name"`
	SizeBytes int64  `json:"size_bytes"`
	FS        string `json:"fs,omitempty"`
	Mount     string `json:"mount,omitempty"`
	Label     string `json:"label,omitempty"`
	FreeBytes int64  `json:"free_bytes"`
	Type      string `json:"type,omitempty"`
}

type Disk struct {
	Name       string      `json:"name"`
	Model      string      `json:"model,omitempty"`
	Type       string      `json:"type,omitempty"` // SSD, HDD, NVMe
	SizeBytes  int64       `json:"size_bytes"`
	Partitions []Partition `json:"partitions"`
}

type NIC struct {
	Name        string   `json:"name"`
	Description string   `json:"description,omitempty"`
	MAC         string   `json:"mac,omitempty"`
	Connected   bool     `json:"connected"`
	SpeedMbps   int64    `json:"speed_mbps,omitempty"`
	IPs         []string `json:"ips"`
}

type App struct {
	Name        string `json:"name"`
	Version     string `json:"version,omitempty"`
	Publisher   string `json:"publisher,omitempty"`
	InstalledOn string `json:"installed_on,omitempty"`
}

type CPU struct {
	Manufacturer string `json:"manufacturer,omitempty"`
	Model        string `json:"model,omitempty"`
	Socket       string `json:"socket,omitempty"` // socket type, for example "LGA1700"
	Count        int    `json:"count"`            // physical CPUs (sockets in use)
	TotalCores   int    `json:"total_cores"`
	CoresPerCPU  int    `json:"cores_per_cpu"`
	Logical      int    `json:"logical"`
}

type Antivirus struct {
	Name       string `json:"name"`
	Enabled    bool   `json:"enabled"`
	UpToDate   *bool  `json:"up_to_date,omitempty"`
	State      string `json:"state,omitempty"`
	Definition string `json:"definitions,omitempty"`
}

// Inventory uses the JSON names the server expects.
type Inventory struct {
	Hostname   string      `json:"hostname"`
	DNSName    string      `json:"dns_name,omitempty"`
	OSFamily   string      `json:"os_family"`
	OSName     string      `json:"os_name,omitempty"`
	OSVersion  string      `json:"os_version,omitempty"`
	Make       string      `json:"make,omitempty"`
	Model      string      `json:"model,omitempty"`
	Serial     string      `json:"serial_number,omitempty"`
	BIOS       string      `json:"bios_version,omitempty"`
	CPU        CPU         `json:"cpu"`
	RAMBytes   int64       `json:"ram_bytes"`
	TotalDisk  int64       `json:"total_storage_bytes"`
	UptimeS    int64       `json:"uptime_s"`
	Disks      []Disk      `json:"disks"`
	NICs       []NIC       `json:"nics"`
	Apps       []App       `json:"installed_apps"`
	Antivirus  []Antivirus `json:"antivirus"`
	DomainType string      `json:"domain_type,omitempty"`
	Domain     string      `json:"domain,omitempty"`
	LastPatch  string      `json:"last_patch_date,omitempty"` // yyyy-mm-dd
	IsServer   bool        `json:"is_server"`
	// Extra holds tables beyond the computer itself, such as the virtual machines of a Hyper-V, KVM or Proxmox host.
	Extra map[string]any `json:"hardware_extra,omitempty"`
}

// Collect gathers everything it can. A part that cannot be read is left empty; one failing command never stops the rest.
func Collect(ctx context.Context) (*Inventory, error) {
	ctx, cancel := context.WithTimeout(ctx, 3*time.Minute)
	defer cancel()
	var inv *Inventory
	var err error
	switch runtime.GOOS {
	case "windows":
		inv, err = collectWindows(ctx)
	case "linux":
		inv, err = collectLinux(ctx)
	default:
		return nil, fmt.Errorf("inventory is not supported on %s", runtime.GOOS)
	}
	if err != nil {
		return nil, err
	}
	if inv.Hostname == "" {
		inv.Hostname, _ = os.Hostname()
	}
	if inv.TotalDisk == 0 {
		for _, d := range inv.Disks {
			inv.TotalDisk += d.SizeBytes
		}
	}
	sort.Slice(inv.Apps, func(i, j int) bool { return strings.ToLower(inv.Apps[i].Name) < strings.ToLower(inv.Apps[j].Name) })
	return inv, nil
}

// JSON returns the inventory in the shape the server reads.
func (i *Inventory) JSON() []byte { b, _ := json.Marshal(i); return b }
