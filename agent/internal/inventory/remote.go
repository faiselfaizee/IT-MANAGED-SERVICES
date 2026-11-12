package inventory

import (
	"encoding/json"
	"strconv"
	"strings"
)

// LinuxRemoteScript is run over SSH by the collector: it prints each piece of the inventory under a marker line. ParseLinuxRemote reads it back.
const LinuxRemoteScript = `
echo '##HOST'; hostname
echo '##FQDN'; hostname -f 2>/dev/null
echo '##OS'; cat /etc/os-release 2>/dev/null
echo '##CPU'; cat /proc/cpuinfo 2>/dev/null
echo '##MEM'; cat /proc/meminfo 2>/dev/null
echo '##UP'; cat /proc/uptime 2>/dev/null
echo '##DMI'; for f in sys_vendor product_name product_serial bios_version; do echo "$f=$(cat /sys/class/dmi/id/$f 2>/dev/null)"; done
echo '##LSBLK'; lsblk -b -J -o NAME,SIZE,TYPE,FSTYPE,MOUNTPOINT,LABEL,MODEL,ROTA,TRAN 2>/dev/null
echo '##DF'; df -B1 --output=target,avail 2>/dev/null
echo '##IP'; ip -j addr 2>/dev/null
echo '##SPEED'; for n in /sys/class/net/*; do echo "$(basename $n)=$(cat $n/speed 2>/dev/null)"; done
echo '##DPKG'; dpkg-query -W -f='${binary:Package}\t${Version}\t${Maintainer}\t${db:Status-Abbrev}\n' 2>/dev/null
echo '##RPM'; rpm -qa --qf '%{NAME}\t%{VERSION}-%{RELEASE}\t%{VENDOR}\t%{INSTALLTIME}\n' 2>/dev/null
echo '##TARGET'; systemctl get-default 2>/dev/null
echo '##END'
`

// Sections splits the script's output at its "##NAME" marker lines.
func Sections(out string) map[string]string {
	m := map[string]string{}
	cur := ""
	var sb strings.Builder
	flush := func() {
		if cur != "" {
			m[cur] = strings.TrimSpace(sb.String())
		}
		sb.Reset()
	}
	for _, l := range strings.Split(strings.ReplaceAll(out, "\r\n", "\n"), "\n") {
		if strings.HasPrefix(l, "##") && !strings.Contains(l, " ") {
			flush()
			cur = strings.TrimPrefix(l, "##")
			continue
		}
		sb.WriteString(l)
		sb.WriteByte('\n')
	}
	flush()
	return m
}

type ipLink struct {
	Name      string   `json:"ifname"`
	MAC       string   `json:"address"`
	State     string   `json:"operstate"`
	Flags     []string `json:"flags"`
	LinkType  string   `json:"link_type"`
	AddrInfos []struct {
		Local string `json:"local"`
	} `json:"addr_info"`
}

// ParseIPJSON reads `ip -j addr`: physical-looking adapters with MAC address, state and addresses.
func ParseIPJSON(js string, speeds map[string]int64) []NIC {
	var links []ipLink
	if json.Unmarshal([]byte(js), &links) != nil {
		return nil
	}
	var out []NIC
	for _, l := range links {
		n := strings.ToLower(l.Name)
		if l.LinkType == "loopback" || l.MAC == "" || l.MAC == "00:00:00:00:00:00" || strings.HasPrefix(n, "veth") || strings.HasPrefix(n, "docker") || strings.HasPrefix(n, "br-") || strings.HasPrefix(n, "virbr") || strings.HasPrefix(n, "cali") || strings.HasPrefix(n, "flannel") || strings.HasPrefix(n, "cni") {
			continue
		}
		nic := NIC{Name: l.Name, MAC: l.MAC, Connected: l.State == "UP" || (l.State == "UNKNOWN" && len(l.AddrInfos) > 0), IPs: []string{}, SpeedMbps: speeds[l.Name]}
		for _, a := range l.AddrInfos {
			nic.IPs = append(nic.IPs, a.Local)
		}
		out = append(out, nic)
	}
	return out
}

// ParseLinuxRemote builds an inventory from the output of LinuxRemoteScript.
func ParseLinuxRemote(out string) *Inventory {
	s := Sections(out)
	inv := &Inventory{OSFamily: "linux", DomainType: "workgroup"}
	inv.Hostname = firstLine(s["HOST"])
	inv.DNSName = firstLine(s["FQDN"])
	inv.OSName, inv.OSVersion = ParseOSRelease(s["OS"])
	for _, l := range strings.Split(s["DMI"], "\n") {
		k, v, _ := strings.Cut(l, "=")
		v = cleanDMI(strings.TrimSpace(v))
		switch k {
		case "sys_vendor":
			inv.Make = v
		case "product_name":
			inv.Model = v
		case "product_serial":
			inv.Serial = v
		case "bios_version":
			inv.BIOS = v
		}
	}
	inv.CPU = ParseCPUInfo(s["CPU"])
	inv.RAMBytes = ParseMemTotal(s["MEM"])
	inv.UptimeS = ParseUptime(s["UP"])
	free := map[string]int64{}
	for i, l := range strings.Split(s["DF"], "\n") {
		f := strings.Fields(l)
		if i == 0 || len(f) < 2 {
			continue
		}
		if n, err := strconv.ParseInt(f[len(f)-1], 10, 64); err == nil {
			free[strings.Join(f[:len(f)-1], " ")] = n
		}
	}
	inv.Disks = ParseLsblk(s["LSBLK"], func(m string) int64 { return free[m] })
	speeds := map[string]int64{}
	for _, l := range strings.Split(s["SPEED"], "\n") {
		k, v, _ := strings.Cut(l, "=")
		if n, err := strconv.ParseInt(strings.TrimSpace(v), 10, 64); err == nil && n > 0 {
			speeds[k] = n
		}
	}
	inv.NICs = ParseIPJSON(s["IP"], speeds)
	if s["DPKG"] != "" {
		inv.Apps = append(inv.Apps, ParseTabular(s["DPKG"], true)...)
	}
	if s["RPM"] != "" {
		inv.Apps = append(inv.Apps, ParseTabular(s["RPM"], false)...)
	}
	inv.IsServer = firstLine(s["TARGET"]) != "graphical.target"
	for _, d := range inv.Disks {
		inv.TotalDisk += d.SizeBytes
	}
	return inv
}

func firstLine(s string) string {
	l, _, _ := strings.Cut(strings.TrimSpace(s), "\n")
	return strings.TrimSpace(l)
}
