package collector

import (
	"fmt"
	"sort"
	"strconv"
	"strings"
	"time"

	"github.com/deskask/platform/agent/internal/config"
	"github.com/deskask/platform/agent/internal/snmp"
)

// Well-known enterprise numbers (the number after 1.3.6.1.4.1 in sysObjectID) to a maker's name.
var makers = map[string]string{
	"9": "Cisco", "11": "HP", "2636": "Juniper", "14988": "MikroTik", "12356": "Fortinet", "25461": "Palo Alto Networks", "8072": "Net-SNMP",
	"11863": "TP-Link", "4526": "Netgear", "171": "D-Link", "674": "Dell", "1602": "Canon", "1347": "Kyocera", "253": "Xerox", "2435": "Brother",
	"1248": "Epson", "641": "Lexmark", "367": "Ricoh", "310": "Konica Minolta", "119": "NEC", "2011": "Huawei", "25506": "H3C", "41112": "Ubiquiti",
	"6486": "Alcatel-Lucent", "1991": "Brocade", "1916": "Extreme Networks", "6876": "VMware", "2021": "Net-SNMP", "16": "Allied Telesis", "5951": "Citrix",
	"3375": "F5", "6527": "Nokia", "4881": "Ruijie", "27975": "Sophos", "8744": "Sophos", "2604": "Sophos", "10002": "Ubiquiti", "15983": "WatchGuard", "21013": "Hikvision",
	"8741": "SonicWall", "29671": "Cisco Meraki", "14823": "Aruba", "47196": "Aruba",
}

// SNMPInventory reads a device over SNMP v2c and returns it in the inventory shape the platform reads.
func SNMPInventory(addr, community, fallbackName string) (map[string]any, error) {
	return snmpInventory(&snmp.Client{Addr: addr, Community: community, Timeout: 2 * time.Second, Retries: 1}, fallbackName)
}

// SNMPInventoryWith reads with a saved login: a v2c community or an SNMPv3 user.
func SNMPInventoryWith(addr string, cred config.Cred, fallbackName string) (map[string]any, error) {
	return snmpInventory(snmpClient(addr, cred), fallbackName)
}

func snmpClient(addr string, cred config.Cred) *snmp.Client {
	c := &snmp.Client{Addr: addr, Community: cred.Community, Timeout: 2 * time.Second, Retries: 1}
	if cred.V3 {
		c.V3 = &snmp.V3{User: cred.User, AuthProto: cred.AuthProto, AuthPass: cred.Password, PrivProto: cred.PrivProto, PrivPass: cred.PrivPassword}
	}
	return c
}

func snmpInventory(c *snmp.Client, fallbackName string) (map[string]any, error) {
	descr, err := c.Get("1.3.6.1.2.1.1.1.0")
	if err != nil {
		return nil, err
	}
	str := func(oid string) string {
		v, err := c.Get(oid)
		if err != nil || v.Missing() {
			return ""
		}
		return v.Text()
	}
	inv := map[string]any{"os_family": ""}
	name := str("1.3.6.1.2.1.1.5.0")
	if name == "" {
		name = fallbackName
	}
	inv["hostname"] = name
	d := descr.Text()
	if i := strings.IndexAny(d, "\r\n"); i > 0 {
		d = d[:i]
	}
	if len(d) > 200 {
		d = d[:200]
	}
	inv["os_name"] = d
	if v, err := c.Get("1.3.6.1.2.1.1.3.0"); err == nil && !v.Missing() {
		inv["uptime_s"] = v.Int / 100 // TimeTicks are hundredths of a second
	}
	if v, err := c.Get("1.3.6.1.2.1.1.2.0"); err == nil && v.OID != "" {
		if rest, ok := strings.CutPrefix(v.OID, "1.3.6.1.4.1."); ok {
			ent, _, _ := strings.Cut(rest, ".")
			if m := makers[ent]; m != "" {
				inv["make"] = m
			}
		}
	}
	first := func(oid string) string { // the first non-empty value of a table column
		rows, _ := c.Walk(oid, 40)
		for _, r := range rows {
			if t := r.Value.Text(); t != "" && t != "0" {
				return t
			}
		}
		return ""
	}
	if s := first("1.3.6.1.2.1.47.1.1.1.1.11"); s != "" { // entPhysicalSerialNum
		inv["serial_number"] = s
	} else if s := str("1.3.6.1.2.1.43.5.1.1.17.1"); s != "" { // prtGeneralSerialNumber
		inv["serial_number"] = s
	}
	model := first("1.3.6.1.2.1.47.1.1.1.1.13") // entPhysicalModelName
	if model == "" {
		model = str("1.3.6.1.2.1.25.3.2.1.3.1") // hrDeviceDescr: printers put their model here
	}
	if model != "" {
		inv["model"] = model
	}
	if mf := first("1.3.6.1.2.1.47.1.1.1.1.12"); mf != "" && inv["make"] == nil {
		inv["make"] = mf
	}
	inv["nics"] = snmpNICs(c)
	addNetworkDetails(c, inv)
	return inv, nil
}

func snmpNICs(c *snmp.Client) []map[string]any {
	const ifTable = "1.3.6.1.2.1.2.2.1"
	col := func(oid string) map[string]snmp.Value {
		m := map[string]snmp.Value{}
		rows, _ := c.Walk(oid, 256)
		for _, r := range rows {
			m[r.Suffix(oid)] = r.Value
		}
		return m
	}
	names, macs, oper := col(ifTable+".2"), col(ifTable+".6"), col(ifTable+".8")
	high, low := col("1.3.6.1.2.1.31.1.1.1.15"), col(ifTable+".5")
	ips := map[string][]string{}
	addrs, _ := c.Walk("1.3.6.1.2.1.4.20.1.2", 512) // ipAdEntIfIndex: the address is in the OID suffix
	for _, r := range addrs {
		ips[strconv.FormatInt(r.Value.Int, 10)] = append(ips[strconv.FormatInt(r.Value.Int, 10)], r.Suffix("1.3.6.1.2.1.4.20.1.2"))
	}
	keys := make([]string, 0, len(names))
	for k := range names {
		keys = append(keys, k)
	}
	sort.Slice(keys, func(i, j int) bool { a, _ := strconv.Atoi(keys[i]); b, _ := strconv.Atoi(keys[j]); return a < b })
	var out []map[string]any
	for _, k := range keys {
		mac := macs[k].Text()
		if len(macs[k].Bytes) != 6 || mac == "00:00:00:00:00:00" {
			if len(ips[k]) == 0 {
				continue // a virtual or loopback interface with nothing to show
			}
			mac = ""
		}
		speed := high[k].Int
		if speed == 0 {
			speed = low[k].Int / 1_000_000
		}
		n := map[string]any{"name": names[k].Text(), "description": fmt.Sprintf("interface %s", k), "connected": oper[k].Int == 1, "speed_mbps": speed, "ips": ips[k]}
		if mac != "" {
			n["mac"] = mac
		}
		if n["ips"] == nil {
			n["ips"] = []string{}
		}
		out = append(out, n)
	}
	return out
}
