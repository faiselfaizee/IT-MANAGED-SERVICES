package collector

import (
	"fmt"
	"sort"
	"strings"

	"github.com/deskask/platform/agent/internal/snmp"
)

// The parts of a network device (firewall, switch, access point) that standard and vendor MIBs describe: every port with its state
// and speed, the hardware modules, power supplies and fans, and the firmware. Read over the same SNMP v2c session as the basic inventory.

// vt is a value's text, or "" when the device had no value (a missing table cell).
func vt(v snmp.Value) string {
	if v.Tag == 0 || v.Missing() {
		return ""
	}
	return v.Text()
}

// column walks one table column into index -> value.
func column(c *snmp.Client, oid string, max int) map[string]snmp.Value {
	m := map[string]snmp.Value{}
	rows, _ := c.Walk(oid, max)
	for _, r := range rows {
		m[r.Suffix(oid)] = r.Value
	}
	return m
}

func sortedKeys[V any](m map[string]V) []string {
	k := make([]string, 0, len(m))
	for x := range m {
		k = append(k, x)
	}
	sort.Slice(k, func(i, j int) bool { return natLess(k[i], k[j]) })
	return k
}

// ifType values that are real ports.
var portKinds = map[int64]string{6: "Ethernet", 62: "Ethernet", 69: "Ethernet", 117: "Ethernet", 56: "Fibre Channel", 71: "Wireless", 161: "LAG"}

func speedLabel(mbps int64) string {
	switch {
	case mbps >= 1000 && mbps%1000 == 0:
		return fmt.Sprintf("%d Gbps", mbps/1000)
	case mbps >= 1000:
		return fmt.Sprintf("%.1f Gbps", float64(mbps)/1000)
	case mbps > 0:
		return fmt.Sprintf("%d Mbps", mbps)
	}
	return ""
}

// snmpPorts lists the device's ports and counts how many are in use, free and switched off, and at which speeds.
func snmpPorts(c *snmp.Client) ([]map[string]any, map[string]any) {
	const ift, ifx = "1.3.6.1.2.1.2.2.1", "1.3.6.1.2.1.31.1.1.1"
	descr, typ, admin, oper, mac, low := column(c, ift+".2", 2048), column(c, ift+".3", 2048), column(c, ift+".7", 2048), column(c, ift+".8", 2048), column(c, ift+".6", 2048), column(c, ift+".5", 2048)
	name, high, alias := column(c, ifx+".1", 2048), column(c, ifx+".15", 2048), column(c, ifx+".18", 2048)
	var ports []map[string]any
	total, used, free, off := 0, 0, 0, 0
	speeds := map[string]int{}
	for _, k := range sortedKeys(typ) {
		kind, ok := portKinds[typ[k].Int]
		n := firstNonEmpty(vt(name[k]), vt(descr[k]))
		ln := strings.ToLower(n)
		if !ok || strings.HasPrefix(ln, "vlan") || strings.HasPrefix(ln, "loopback") || strings.HasPrefix(ln, "null") || strings.HasPrefix(ln, "tunnel") {
			continue
		}
		up := oper[k].Int == 1
		disabled := admin[k].Int == 2
		state := "Not connected"
		switch {
		case disabled:
			state = "Disabled"
		case up:
			state = "Connected"
		}
		speed := high[k].Int
		if speed == 0 {
			speed = low[k].Int / 1_000_000
		}
		p := map[string]any{"index": k, "name": n, "description": vt(alias[k]), "kind": kind, "state": state, "admin": map[bool]string{true: "down", false: "up"}[disabled]}
		if up && speed > 0 {
			p["speed_mbps"] = speed
			p["speed"] = speedLabel(speed)
		}
		if b := mac[k].Bytes; len(b) == 6 {
			p["mac"] = vt(mac[k])
		}
		ports = append(ports, p)
		if kind == "LAG" {
			continue // a bundle of ports, not a port of its own
		}
		total++
		switch state {
		case "Connected":
			used++
			if l := speedLabel(speed); l != "" {
				speeds[l]++
			}
		case "Disabled":
			off++
		default:
			free++
		}
	}
	return ports, map[string]any{"total": total, "connected": used, "free": free, "disabled": off, "speeds": speeds}
}

// envState turns the vendors' status numbers into OK / Warning / Critical / Absent.
func envState(vendor string, n int64) string {
	switch vendor {
	case "cisco": // CISCO-ENVMON-MIB: normal(1) warning(2) critical(3) shutdown(4) notPresent(5) notFunctioning(6)
		return map[int64]string{1: "OK", 2: "Warning", 3: "Critical", 4: "Critical", 5: "Absent", 6: "Critical"}[n]
	case "hp": // HP-ICF-CHASSIS sensor status: unknown(1) bad(2) warning(3) good(4) notPresent(5)
		return map[int64]string{1: "Unknown", 2: "Critical", 3: "Warning", 4: "OK", 5: "Absent"}[n]
	}
	return "Unknown"
}

// snmpHardware reads modules, power supplies, fans and sensors from ENTITY-MIB and the vendor MIBs.
func snmpHardware(c *snmp.Client, maker string) (modules, psus, sensors []map[string]any, firmware string) {
	const ent = "1.3.6.1.2.1.47.1.1.1.1"
	class := column(c, ent+".5", 1024)
	if len(class) > 0 {
		descr, nm, hw, fw, sw, serial, model := column(c, ent+".2", 1024), column(c, ent+".7", 1024), column(c, ent+".8", 1024), column(c, ent+".9", 1024), column(c, ent+".10", 1024), column(c, ent+".11", 1024), column(c, ent+".13", 1024)
		for _, k := range sortedKeys(class) {
			label := firstNonEmpty(vt(nm[k]), vt(descr[k]))
			switch class[k].Int {
			case 3, 9, 11: // chassis, module, stack member
				kind := map[int64]string{3: "Chassis", 9: "Module", 11: "Stack"}[class[k].Int]
				if class[k].Int == 3 && firmware == "" {
					firmware = firstNonEmpty(vt(sw[k]), vt(fw[k]))
				}
				if vt(serial[k]) == "" && vt(model[k]) == "" && class[k].Int == 9 {
					continue // empty module slots and sub-parts with nothing to tell
				}
				modules = append(modules, map[string]any{"name": label, "kind": kind, "model": vt(model[k]), "serial": vt(serial[k]), "hardware": vt(hw[k]), "firmware": vt(fw[k]), "software": vt(sw[k]), "description": vt(descr[k])})
			case 6:
				psus = append(psus, map[string]any{"name": label, "model": vt(model[k]), "serial": vt(serial[k]), "state": "Unknown"})
			case 7:
				sensors = append(sensors, map[string]any{"name": label, "type": "fan", "state": "Unknown", "reading": ""})
			}
		}
	}
	setState := func(list []map[string]any, name, state string) bool {
		for _, x := range list {
			if strings.EqualFold(fmt.Sprint(x["name"]), name) || strings.Contains(strings.ToLower(fmt.Sprint(x["name"])), strings.ToLower(name)) {
				x["state"] = state
				return true
			}
		}
		return false
	}
	switch maker {
	case "Cisco":
		const env = "1.3.6.1.4.1.9.9.13.1"
		sd, ss := column(c, env+".5.1.2", 64), column(c, env+".5.1.3", 64)
		for _, k := range sortedKeys(sd) {
			st := envState("cisco", ss[k].Int)
			if !setState(psus, vt(sd[k]), st) && st != "Absent" {
				psus = append(psus, map[string]any{"name": vt(sd[k]), "state": st})
			}
		}
		fd, fs := column(c, env+".4.1.2", 64), column(c, env+".4.1.3", 64)
		for _, k := range sortedKeys(fd) {
			st := envState("cisco", fs[k].Int)
			if !setState(sensors, vt(fd[k]), st) && st != "Absent" {
				sensors = append(sensors, map[string]any{"name": vt(fd[k]), "type": "fan", "state": st, "reading": ""})
			}
		}
		td, tv, ts := column(c, env+".3.1.2", 64), column(c, env+".3.1.3", 64), column(c, env+".3.1.6", 64)
		for _, k := range sortedKeys(td) {
			sensors = append(sensors, map[string]any{"name": vt(td[k]), "type": "temperature", "state": envState("cisco", ts[k].Int), "reading": fmt.Sprintf("%d °C", tv[k].Int)})
		}
	case "HP", "Aruba":
		const s = "1.3.6.1.4.1.11.2.14.11.1.2.6.1"
		st, de := column(c, s+".4", 64), column(c, s+".7", 64)
		for _, k := range sortedKeys(de) {
			state := envState("hp", st[k].Int)
			if state == "Absent" {
				continue
			}
			d := vt(de[k])
			if strings.Contains(strings.ToLower(d), "power") {
				if !setState(psus, d, state) {
					psus = append(psus, map[string]any{"name": d, "state": state})
				}
			} else {
				sensors = append(sensors, map[string]any{"name": d, "type": "sensor", "state": state, "reading": ""})
			}
		}
	case "Fortinet":
		const s = "1.3.6.1.4.1.12356.101.4.3.2.1" // fgHwSensorTable: name, value, alarm
		nm, val, alarm := column(c, s+".2", 128), column(c, s+".3", 128), column(c, s+".4", 128)
		for _, k := range sortedKeys(nm) {
			state := "OK"
			if alarm[k].Int == 1 {
				state = "Critical"
			}
			n := vt(nm[k])
			kind := "sensor"
			switch l := strings.ToLower(n); {
			case strings.Contains(l, "fan"):
				kind = "fan"
			case strings.Contains(l, "temp"):
				kind = "temperature"
			case strings.Contains(l, "ps") || strings.Contains(l, "power"):
				kind = "power"
			}
			row := map[string]any{"name": n, "type": kind, "state": state, "reading": vt(val[k])}
			sensors = append(sensors, row)
			if kind == "power" && psuRx.MatchString(n) {
				psus = append(psus, map[string]any{"name": n, "state": state, "reading": vt(val[k])})
			}
		}
	}
	sort.SliceStable(sensors, func(i, j int) bool { return sevRank(sensors[i]["state"]) > sevRank(sensors[j]["state"]) })
	return modules, psus, sensors, firmware
}

// vendorFacts fills serial, model and firmware from vendor MIBs where ENTITY-MIB is silent.
func vendorFacts(c *snmp.Client, maker string) (serial, model, firmware string) {
	get := func(oid string) string {
		v, err := c.Get(oid)
		if err != nil || v.Missing() {
			return ""
		}
		return v.Text()
	}
	switch maker {
	case "Fortinet":
		serial = get("1.3.6.1.4.1.12356.100.1.1.1.0")           // fnSysSerial
		if v := get("1.3.6.1.4.1.12356.101.4.1.1.0"); v != "" { // fgSysVersion: "v7.2.5,build1517,230606 (GA.F)"
			firmware = "FortiOS " + strings.SplitN(v, ",", 2)[0]
		}
	case "SonicWall":
		model, serial, firmware = get("1.3.6.1.4.1.8741.2.1.1.1.0"), get("1.3.6.1.4.1.8741.2.1.1.2.0"), get("1.3.6.1.4.1.8741.2.1.1.3.0")
	case "Palo Alto Networks":
		if v := get("1.3.6.1.4.1.25461.2.1.2.1.1.0"); v != "" { // panSysSwVersion
			firmware = "PAN-OS " + v
		}
		serial = get("1.3.6.1.4.1.25461.2.1.2.1.3.0") // panSysSerialNumber
	case "Ubiquiti":
		model = get("1.3.6.1.4.1.41112.1.6.3.3.0") // unifiApSystemModel
		firmware = get("1.3.6.1.4.1.41112.1.6.3.6.0")
	}
	return
}

// addNetworkDetails puts the ports and hardware tables of a network device into its inventory document.
func addNetworkDetails(c *snmp.Client, inv map[string]any) {
	maker, _ := inv["make"].(string)
	ports, summary := snmpPorts(c)
	modules, psus, sensors, fw := snmpHardware(c, maker)
	serial, model, vfw := vendorFacts(c, maker)
	if s, _ := inv["serial_number"].(string); s == "" && serial != "" {
		inv["serial_number"] = serial
	}
	if m, _ := inv["model"].(string); m == "" && model != "" {
		inv["model"] = model
	}
	if v := firstNonEmpty(vfw, fw); v != "" {
		inv["os_version"] = v
	}
	if summary["total"].(int) < 2 && len(modules) == 0 && len(psus) == 0 {
		return // a printer or a single-port device: the basic inventory says it all
	}
	inv["hardware_extra"] = map[string]any{"platform": "network", "api": strings.TrimSpace(maker + " (SNMP)"), "ports": ports, "port_summary": summary,
		"modules": modules, "power_supplies": psus, "sensors": sensors}
}
