package inventory

import (
	"strconv"
	"strings"
)

// HypervisorScript lists the virtual machines of a Linux host running KVM/libvirt (virsh) or Proxmox VE (qm and pct).
// It prints nothing for a host that is neither. The agent runs it locally; the collector appends it to the SSH script.
const HypervisorScript = `
echo '##VIRSH'; if command -v virsh >/dev/null 2>&1; then virsh list --all --name 2>/dev/null | while IFS= read -r d; do [ -n "$d" ] || continue; echo "@@$d"; virsh dominfo "$d" 2>/dev/null; virsh domifaddr "$d" 2>/dev/null | awk 'NR>2 && $4 != "" {print "IP: "$4}'; done; fi
echo '##QM'; if command -v qm >/dev/null 2>&1; then qm list 2>/dev/null; fi
echo '##PCT'; if command -v pct >/dev/null 2>&1; then pct list 2>/dev/null; fi
echo '##PVE'; if command -v pveversion >/dev/null 2>&1; then pveversion 2>/dev/null; fi
echo '##VEND'
`

// HypervisorExtra reads the script's sections into the hardware tables; nil when the host runs no virtual machines.
func HypervisorExtra(s map[string]string, host string) map[string]any {
	var vms []map[string]any
	platform, api := "", ""
	if strings.TrimSpace(s["VIRSH"]) != "" {
		platform, api = "kvm", "KVM / libvirt"
		var cur map[string]any
		for _, l := range strings.Split(s["VIRSH"], "\n") {
			l = strings.TrimSpace(l)
			if strings.HasPrefix(l, "@@") {
				cur = map[string]any{"name": strings.TrimPrefix(l, "@@"), "host": host}
				vms = append(vms, cur)
				continue
			}
			if cur == nil {
				continue
			}
			k, v, ok := strings.Cut(l, ":")
			if !ok {
				continue
			}
			v = strings.TrimSpace(v)
			switch strings.TrimSpace(k) {
			case "State":
				cur["power"] = map[string]string{"running": "On", "shut off": "Off", "paused": "Suspended"}[v]
				if cur["power"] == "" {
					cur["power"] = v
				}
			case "CPU(s)":
				cur["cpus"], _ = strconv.Atoi(v)
			case "Max memory":
				if n, err := strconv.ParseInt(strings.Fields(v + " 0")[0], 10, 64); err == nil {
					cur["memory_bytes"] = n * 1024
				}
			case "IP":
				ip, _, _ := strings.Cut(v, "/")
				if old, _ := cur["ip"].(string); old != "" {
					ip = old + ", " + ip
				}
				cur["ip"] = ip
			}
		}
	}
	if strings.TrimSpace(s["QM"]) != "" || strings.TrimSpace(s["PCT"]) != "" {
		platform, api = "proxmox", firstNonEmptyStr(firstLine(s["PVE"]), "Proxmox VE")
		for i, l := range strings.Split(s["QM"], "\n") {
			f := strings.Fields(l)
			if i == 0 || len(f) < 5 { // header: VMID NAME STATUS MEM(MB) BOOTDISK(GB) PID
				continue
			}
			mem, _ := strconv.ParseInt(f[3], 10, 64)
			disk, _ := strconv.ParseFloat(f[4], 64)
			vms = append(vms, map[string]any{"name": f[1], "id": f[0], "kind": "VM", "power": pvePower(f[2]), "memory_bytes": mem << 20, "disk_bytes": int64(disk * 1073741824), "host": host})
		}
		for i, l := range strings.Split(s["PCT"], "\n") {
			f := strings.Fields(l)
			if i == 0 || len(f) < 3 { // header: VMID Status Lock Name (Lock is often empty)
				continue
			}
			vms = append(vms, map[string]any{"name": f[len(f)-1], "id": f[0], "kind": "Container", "power": pvePower(f[1]), "host": host})
		}
	}
	if platform == "" {
		return nil
	}
	return map[string]any{"platform": platform, "api": api, "vms": vms}
}

func pvePower(s string) string {
	switch s {
	case "running":
		return "On"
	case "stopped":
		return "Off"
	}
	return s
}

func firstNonEmptyStr(s ...string) string {
	for _, x := range s {
		if x != "" {
			return x
		}
	}
	return ""
}
