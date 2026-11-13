package inventory

import (
	"bufio"
	"context"
	"encoding/json"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"time"
)

func run(ctx context.Context, name string, args ...string) string {
	c, cancel := context.WithTimeout(ctx, 60*time.Second)
	defer cancel()
	out, err := exec.CommandContext(c, name, args...).Output()
	if err != nil && len(out) == 0 {
		return ""
	}
	return string(out)
}

func readTrim(path string) string { b, _ := os.ReadFile(path); return strings.TrimSpace(string(b)) }

func collectLinux(ctx context.Context) (*Inventory, error) {
	inv := &Inventory{OSFamily: "linux", DomainType: "workgroup"}
	inv.Hostname, _ = os.Hostname()
	inv.DNSName = strings.TrimSpace(run(ctx, "hostname", "-f"))
	inv.OSName, inv.OSVersion = ParseOSRelease(readTrim("/etc/os-release"))
	inv.Make = cleanDMI(readTrim("/sys/class/dmi/id/sys_vendor"))
	inv.Model = cleanDMI(readTrim("/sys/class/dmi/id/product_name"))
	inv.Serial = cleanDMI(readTrim("/sys/class/dmi/id/product_serial"))
	inv.BIOS = cleanDMI(readTrim("/sys/class/dmi/id/bios_version"))
	inv.CPU = ParseCPUInfo(readTrim("/proc/cpuinfo"))
	if s := socketType(ctx); s != "" {
		inv.CPU.Socket = s
	}
	inv.RAMBytes = ParseMemTotal(readTrim("/proc/meminfo"))
	inv.UptimeS = ParseUptime(readTrim("/proc/uptime"))
	inv.Disks = ParseLsblk(run(ctx, "lsblk", "-b", "-J", "-o", "NAME,SIZE,TYPE,FSTYPE,MOUNTPOINT,LABEL,MODEL,ROTA,TRAN"), freeBytes)
	inv.NICs = netInterfaces()
	inv.Apps = collectLinuxApps(ctx)
	inv.LastPatch = lastPatchLinux()
	inv.IsServer = strings.TrimSpace(run(ctx, "systemctl", "get-default")) != "graphical.target"
	inv.Extra = HypervisorExtra(Sections(run(ctx, "sh", "-c", HypervisorScript)), inv.Hostname)
	return inv, nil
}

func cleanDMI(s string) string {
	l := strings.ToLower(s)
	for _, bad := range []string{"to be filled", "default string", "not specified", "system serial", "o.e.m", "none"} {
		if strings.Contains(l, bad) {
			return ""
		}
	}
	return s
}

// socketType reads the socket from the firmware tables when dmidecode is there ("Upgrade: Socket LGA1700").
func socketType(ctx context.Context) string {
	for _, l := range strings.Split(run(ctx, "dmidecode", "-t", "processor"), "\n") {
		l = strings.TrimSpace(l)
		if v, ok := strings.CutPrefix(l, "Upgrade:"); ok {
			v = strings.TrimSpace(strings.TrimPrefix(strings.TrimSpace(v), "Socket "))
			if v != "" && v != "Unknown" && v != "Other" {
				return v
			}
		}
	}
	return ""
}

func ParseOSRelease(s string) (name, version string) {
	kv := map[string]string{}
	for _, l := range strings.Split(s, "\n") {
		if k, v, ok := strings.Cut(l, "="); ok {
			kv[strings.TrimSpace(k)] = strings.Trim(strings.TrimSpace(v), `"'`)
		}
	}
	name = kv["PRETTY_NAME"]
	if name == "" {
		name = kv["NAME"]
	}
	return name, kv["VERSION_ID"]
}

// ParseCPUInfo reads /proc/cpuinfo: sockets are the distinct "physical id" values, cores per socket the "cpu cores" line.
func ParseCPUInfo(s string) CPU {
	var c CPU
	phys := map[string]bool{}
	cores := map[string]int{}
	cur := ""
	for _, l := range strings.Split(s, "\n") {
		k, v, ok := strings.Cut(l, ":")
		if !ok {
			continue
		}
		k, v = strings.TrimSpace(k), strings.TrimSpace(v)
		switch k {
		case "processor":
			c.Logical++
		case "vendor_id":
			if c.Manufacturer == "" {
				c.Manufacturer = v
			}
		case "model name", "Model", "Hardware":
			if c.Model == "" || k == "model name" {
				c.Model = v
			}
		case "physical id":
			cur = v
			phys[v] = true
		case "cpu cores":
			n, _ := strconv.Atoi(v)
			if cur == "" {
				cur = "0"
			}
			cores[cur] = n
		}
	}
	c.Count = len(phys)
	if c.Count == 0 && c.Logical > 0 {
		c.Count = 1
	}
	for _, n := range cores {
		c.TotalCores += n
	}
	if c.TotalCores == 0 {
		c.TotalCores = c.Logical // virtual machines and ARM boards do not list cores
	}
	if c.Count > 0 {
		c.CoresPerCPU = c.TotalCores / c.Count
	}
	return c
}

func ParseMemTotal(s string) int64 {
	for _, l := range strings.Split(s, "\n") {
		if v, ok := strings.CutPrefix(l, "MemTotal:"); ok {
			f := strings.Fields(v)
			if len(f) > 0 {
				n, _ := strconv.ParseInt(f[0], 10, 64)
				return n * 1024
			}
		}
	}
	return 0
}

func ParseUptime(s string) int64 {
	f := strings.Fields(s)
	if len(f) == 0 {
		return 0
	}
	v, _ := strconv.ParseFloat(f[0], 64)
	return int64(v)
}

type lsblkNode struct {
	Name     string      `json:"name"`
	Size     json.Number `json:"size"`
	Type     string      `json:"type"`
	FSType   string      `json:"fstype"`
	Mount    string      `json:"mountpoint"`
	Label    string      `json:"label"`
	Model    string      `json:"model"`
	Rota     any         `json:"rota"`
	Tran     string      `json:"tran"`
	Children []lsblkNode `json:"children"`
}

// ParseLsblk turns `lsblk -J` output into disks with their partitions. free reports the free bytes of a mounted partition (may be nil).
func ParseLsblk(js string, free func(mount string) int64) []Disk {
	var doc struct {
		Devices []lsblkNode `json:"blockdevices"`
	}
	if err := json.Unmarshal([]byte(js), &doc); err != nil {
		return nil
	}
	var out []Disk
	for _, n := range doc.Devices {
		if n.Type != "disk" {
			continue
		}
		sz, _ := n.Size.Int64()
		if sz == 0 || strings.HasPrefix(n.Name, "zram") || strings.HasPrefix(n.Name, "ram") {
			continue // memory-backed or empty devices are not disks
		}
		d := Disk{Name: n.Name, Model: strings.TrimSpace(n.Model), SizeBytes: sz, Type: diskType(n), Partitions: []Partition{}}
		var walk func(c lsblkNode, depth int)
		walk = func(c lsblkNode, depth int) {
			psz, _ := c.Size.Int64()
			if c.Type == "part" || c.Type == "lvm" || c.Type == "crypt" || c.Type == "raid1" || c.Type == "md" || depth == 0 && c.FSType != "" {
				p := Partition{Name: c.Name, SizeBytes: psz, FS: c.FSType, Mount: c.Mount, Label: c.Label, Type: c.Type}
				if c.Mount != "" && free != nil {
					p.FreeBytes = free(c.Mount)
				}
				d.Partitions = append(d.Partitions, p)
			}
			for _, k := range c.Children {
				walk(k, depth+1)
			}
		}
		if n.FSType != "" && len(n.Children) == 0 { // a whole disk formatted without a partition table
			walk(n, 0)
		}
		for _, c := range n.Children {
			walk(c, 1)
		}
		out = append(out, d)
	}
	return out
}

func diskType(n lsblkNode) string {
	if strings.HasPrefix(n.Name, "nvme") || n.Tran == "nvme" {
		return "NVMe"
	}
	rota := false
	switch v := n.Rota.(type) {
	case bool:
		rota = v
	case string:
		rota = v == "1" || v == "true"
	case float64:
		rota = v == 1
	}
	if rota {
		return "HDD"
	}
	if n.Name != "" {
		return "SSD"
	}
	return ""
}

func netInterfaces() []NIC {
	ifs, _ := net.Interfaces()
	var out []NIC
	for _, i := range ifs {
		if i.Flags&net.FlagLoopback != 0 || len(i.HardwareAddr) == 0 {
			continue
		}
		n := strings.ToLower(i.Name)
		if strings.HasPrefix(n, "veth") || strings.HasPrefix(n, "docker") || strings.HasPrefix(n, "br-") || strings.HasPrefix(n, "virbr") || strings.HasPrefix(n, "cali") || strings.HasPrefix(n, "flannel") || strings.HasPrefix(n, "cni") {
			continue
		}
		nic := NIC{Name: i.Name, MAC: i.HardwareAddr.String(), IPs: []string{}}
		nic.Connected = i.Flags&net.FlagUp != 0 && readTrim(filepath.Join("/sys/class/net", i.Name, "operstate")) != "down"
		if v, err := strconv.ParseInt(readTrim(filepath.Join("/sys/class/net", i.Name, "speed")), 10, 64); err == nil && v > 0 {
			nic.SpeedMbps = v
		}
		nic.Description = nicDescription(i.Name)
		addrs, _ := i.Addrs()
		for _, a := range addrs {
			if ip, _, err := net.ParseCIDR(a.String()); err == nil {
				nic.IPs = append(nic.IPs, ip.String())
			}
		}
		out = append(out, nic)
	}
	return out
}

func nicDescription(name string) string {
	dev := filepath.Join("/sys/class/net", name, "device")
	drv, _ := os.Readlink(filepath.Join(dev, "driver"))
	if drv != "" {
		return filepath.Base(drv)
	}
	return ""
}

func collectLinuxApps(ctx context.Context) []App {
	var apps []App
	if out := run(ctx, "dpkg-query", "-W", "-f", "${binary:Package}\t${Version}\t${Maintainer}\t${db:Status-Abbrev}\n"); out != "" {
		apps = append(apps, ParseTabular(out, true)...)
	}
	if out := run(ctx, "rpm", "-qa", "--qf", "%{NAME}\t%{VERSION}-%{RELEASE}\t%{VENDOR}\t%{INSTALLTIME}\n"); out != "" {
		apps = append(apps, ParseTabular(out, false)...)
	}
	if out := run(ctx, "snap", "list"); out != "" {
		sc := bufio.NewScanner(strings.NewReader(out))
		sc.Scan() // header
		for sc.Scan() {
			f := strings.Fields(sc.Text())
			if len(f) >= 2 {
				apps = append(apps, App{Name: f[0] + " (snap)", Version: f[1]})
			}
		}
	}
	return apps
}

// ParseTabular reads "name<TAB>version<TAB>publisher[<TAB>status or install time]" lines (dpkg-query and rpm).
func ParseTabular(s string, dpkg bool) []App {
	var apps []App
	for _, l := range strings.Split(s, "\n") {
		f := strings.Split(l, "\t")
		if len(f) < 2 || strings.TrimSpace(f[0]) == "" {
			continue
		}
		if dpkg && len(f) >= 4 && !strings.HasPrefix(strings.TrimSpace(f[3]), "ii") {
			continue // removed or half-installed
		}
		a := App{Name: strings.TrimSpace(f[0]), Version: strings.TrimSpace(f[1])}
		if len(f) >= 3 {
			a.Publisher = strings.TrimSpace(f[2])
			if a.Publisher == "(none)" {
				a.Publisher = ""
			}
		}
		if !dpkg && len(f) >= 4 {
			if sec, err := strconv.ParseInt(strings.TrimSpace(f[3]), 10, 64); err == nil && sec > 0 {
				a.InstalledOn = time.Unix(sec, 0).UTC().Format("2006-01-02")
			}
		}
		apps = append(apps, a)
	}
	return apps
}

func lastPatchLinux() string {
	var newest time.Time
	for _, p := range []string{"/var/log/apt/history.log", "/var/log/dnf.log", "/var/log/yum.log", "/var/log/pacman.log"} {
		if st, err := os.Stat(p); err == nil && st.ModTime().After(newest) {
			newest = st.ModTime()
		}
	}
	if newest.IsZero() {
		return ""
	}
	return newest.UTC().Format("2006-01-02")
}
