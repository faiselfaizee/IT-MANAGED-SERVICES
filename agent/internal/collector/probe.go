package collector

import (
	"context"
	"fmt"
	"net"
	"os"
	"os/exec"
	"regexp"
	"runtime"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"
)

var rttRx = regexp.MustCompile(`(?i)time[=<]\s*([0-9.]+)\s*ms`)
var macRx = regexp.MustCompile(`(?i)\b([0-9a-f]{2}[:-]){5}[0-9a-f]{2}\b`)

// ParsePing reads the output of the system's ping: up only when a real echo reply came back (Windows says "Destination host unreachable" with exit code 0).
func ParsePing(out string) (up bool, rttMs float64) {
	l := strings.ToLower(out)
	if !strings.Contains(l, "ttl=") {
		return false, 0
	}
	if m := rttRx.FindStringSubmatch(out); m != nil {
		rttMs, _ = strconv.ParseFloat(m[1], 64)
	}
	if strings.Contains(l, "time<") && rttMs == 0 {
		rttMs = 1
	}
	return true, rttMs
}

// Ping sends one echo with the system's ping program (no special privileges needed).
func Ping(ctx context.Context, host string) (bool, float64) {
	if net.ParseIP(host) == nil && strings.ContainsAny(host, " ;&|$`\"'<>") {
		return false, 0
	}
	c, cancel := context.WithTimeout(ctx, 6*time.Second)
	defer cancel()
	var cmd *exec.Cmd
	if runtime.GOOS == "windows" {
		cmd = exec.CommandContext(c, "ping", "-n", "1", "-w", "2000", host)
	} else {
		cmd = exec.CommandContext(c, "ping", "-c", "1", "-W", "2", host)
	}
	out, _ := cmd.CombinedOutput()
	return ParsePing(string(out))
}

// HealthPorts are tried when a device does not answer ping (many cameras, biometric terminals and firewalls drop it).
var HealthPorts = []int{443, 80, 22, 445, 3389, 554, 9100, 4370, 8000, 37777}

// TCPOpen reports whether a connection to host:port succeeds within timeout.
func TCPOpen(ctx context.Context, host string, port int, timeout time.Duration) (bool, time.Duration) {
	start := time.Now()
	d := net.Dialer{Timeout: timeout}
	c, err := d.DialContext(ctx, "tcp", net.JoinHostPort(host, strconv.Itoa(port)))
	if err != nil {
		return false, 0
	}
	c.Close()
	return true, time.Since(start)
}

// Health: ping first; if there is no answer, any of the usual ports answering also counts as up.
func Health(ctx context.Context, host string) (bool, float64) {
	if up, ms := Ping(ctx, host); up {
		return true, ms
	}
	for _, p := range HealthPorts {
		if ok, d := TCPOpen(ctx, host, p, 1200*time.Millisecond); ok {
			return true, float64(d.Microseconds()) / 1000
		}
	}
	return false, 0
}

// ScanPorts are the ports a scan looks at; they are enough to tell Windows, Linux, printers, cameras and network gear apart.
var ScanPorts = []int{22, 23, 80, 135, 139, 443, 445, 554, 631, 3389, 4370, 5985, 5986, 8000, 8080, 8443, 9100, 37777}

// Expand lists the addresses of a CIDR range up to /22 (1024 addresses), without network and broadcast addresses.
func Expand(cidr string) ([]string, error) {
	ip, n, err := net.ParseCIDR(strings.TrimSpace(cidr))
	if err != nil || ip.To4() == nil {
		return nil, fmt.Errorf("%q is not an IPv4 range such as 192.168.1.0/24", cidr)
	}
	ones, bits := n.Mask.Size()
	if ones < 22 || bits != 32 {
		return nil, fmt.Errorf("%s is too large; scan at most a /22 (1024 addresses) at a time", cidr)
	}
	base := n.IP.To4()
	if base[0] == 169 && base[1] == 254 || base[0] >= 224 || base[0] == 127 {
		return nil, fmt.Errorf("%s cannot be scanned", cidr)
	}
	size := 1 << (32 - ones)
	var out []string
	for i := 0; i < size; i++ {
		if ones < 31 && (i == 0 || i == size-1) {
			continue
		}
		v := uint32(base[0])<<24 | uint32(base[1])<<16 | uint32(base[2])<<8 | uint32(base[3])
		v += uint32(i)
		out = append(out, net.IPv4(byte(v>>24), byte(v>>16), byte(v>>8), byte(v)).String())
	}
	return out, nil
}

// Found is one device a scan came across.
type Found struct {
	IP       string `json:"ip"`
	Hostname string `json:"hostname,omitempty"`
	Open     []int  `json:"open"`
	MAC      string `json:"mac,omitempty"`
}

// Scan probes every address with `workers` in parallel and returns the ones that answer (ping or any scan port).
func Scan(ctx context.Context, cidrs []string, workers int) ([]Found, []string) {
	var warnings []string
	var addrs []string
	seen := map[string]bool{}
	for _, c := range cidrs {
		l, err := Expand(c)
		if err != nil {
			warnings = append(warnings, err.Error())
			continue
		}
		for _, a := range l {
			if !seen[a] {
				seen[a] = true
				addrs = append(addrs, a)
			}
		}
	}
	return scanAddrs(ctx, addrs, workers), warnings
}

func scanAddrs(ctx context.Context, addrs []string, workers int) []Found {
	var mu sync.Mutex
	var out []Found
	jobs := make(chan string)
	var wg sync.WaitGroup
	if workers <= 0 {
		workers = 64
	}
	for i := 0; i < workers; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			for ip := range jobs {
				if ctx.Err() != nil {
					continue
				}
				open := []int{}
				for _, p := range ScanPorts {
					if ok, _ := TCPOpen(ctx, ip, p, 700*time.Millisecond); ok {
						open = append(open, p)
					}
				}
				if len(open) == 0 {
					if up, _ := Ping(ctx, ip); !up {
						continue
					}
				}
				f := Found{IP: ip, Open: open, MAC: ARP(ctx, ip), Hostname: ReverseDNS(ctx, ip)}
				mu.Lock()
				out = append(out, f)
				mu.Unlock()
			}
		}()
	}
	for _, a := range addrs {
		jobs <- a
	}
	close(jobs)
	wg.Wait()
	sort.Slice(out, func(i, j int) bool { return ipLess(out[i].IP, out[j].IP) })
	return out
}

func ipLess(a, b string) bool {
	x, y := net.ParseIP(a).To4(), net.ParseIP(b).To4()
	if x == nil || y == nil {
		return a < b
	}
	return string(x) < string(y)
}

// ReverseDNS looks up the name of an address; empty when there is none.
func ReverseDNS(ctx context.Context, ip string) string {
	c, cancel := context.WithTimeout(ctx, 1500*time.Millisecond)
	defer cancel()
	names, err := (&net.Resolver{}).LookupAddr(c, ip)
	if err != nil || len(names) == 0 {
		return ""
	}
	return strings.TrimSuffix(names[0], ".")
}

// ParseARP finds the MAC address for ip in the text of an ARP table (/proc/net/arp, `arp -a` or `ip neigh`).
func ParseARP(text, ip string) string {
	for _, l := range strings.Split(text, "\n") {
		f := strings.Fields(l)
		if len(f) == 0 {
			continue
		}
		hit := false
		for _, x := range f {
			if strings.Trim(x, "()") == ip {
				hit = true
				break
			}
		}
		if hit {
			if m := macRx.FindString(l); m != "" && !strings.EqualFold(m, "00:00:00:00:00:00") {
				return strings.ToUpper(strings.ReplaceAll(m, "-", ":"))
			}
		}
	}
	return ""
}

// ARP returns the MAC address of a device on the same network (empty for devices behind a router).
func ARP(ctx context.Context, ip string) string {
	if runtime.GOOS == "linux" {
		if b, err := os.ReadFile("/proc/net/arp"); err == nil {
			if m := ParseARP(string(b), ip); m != "" {
				return m
			}
		}
	}
	c, cancel := context.WithTimeout(ctx, 3*time.Second)
	defer cancel()
	var out []byte
	if runtime.GOOS == "windows" {
		out, _ = exec.CommandContext(c, "arp", "-a", ip).Output()
	} else {
		out, _ = exec.CommandContext(c, "arp", "-n", ip).Output()
	}
	return ParseARP(string(out), ip)
}

// Contains reports whether ip is inside cidr (an empty cidr matches everything).
func Contains(cidr, ip string) bool {
	if strings.TrimSpace(cidr) == "" {
		return true
	}
	_, n, err := net.ParseCIDR(strings.TrimSpace(cidr))
	p := net.ParseIP(ip)
	return err == nil && p != nil && n.Contains(p)
}
