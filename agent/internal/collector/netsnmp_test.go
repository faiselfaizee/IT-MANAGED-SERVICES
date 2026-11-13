package collector

import (
	"strings"
	"testing"

	"github.com/deskask/platform/agent/internal/snmp"
)

func TestSNMPReadsASwitchsPortsHardwareAndPowerSupplies(t *testing.T) {
	const ift, ifx, ent = "1.3.6.1.2.1.2.2.1", "1.3.6.1.2.1.31.1.1.1", "1.3.6.1.2.1.47.1.1.1.1"
	v := map[string]snmp.Value{
		"1.3.6.1.2.1.1.1.0": snmp.Str("Cisco IOS Software, C9200L Software (C9200L-UNIVERSALK9-M), Version 17.9.4"),
		"1.3.6.1.2.1.1.2.0": snmp.ObjectID("1.3.6.1.4.1.9.1.2697"),
		"1.3.6.1.2.1.1.3.0": snmp.Ticks(360000),
		"1.3.6.1.2.1.1.5.0": snmp.Str("CORE-SW1"),
	}
	port := func(i, name string, typ, admin, oper, mbps int64, alias string) {
		v[ift+".2."+i], v[ift+".3."+i], v[ift+".7."+i], v[ift+".8."+i] = snmp.Str(name), snmp.Int(typ), snmp.Int(admin), snmp.Int(oper)
		v[ift+".5."+i] = snmp.Gauge(mbps * 1000000 % 4294967296)
		v[ift+".6."+i] = snmp.Bin(0x70, 0x1b, 0x7b, 0, 0, byte(len(i)))
		v[ifx+".1."+i], v[ifx+".15."+i], v[ifx+".18."+i] = snmp.Str(name), snmp.Gauge(mbps), snmp.Str(alias)
	}
	port("1", "Gi1/0/1", 6, 1, 1, 1000, "Uplink to FW")
	port("2", "Gi1/0/2", 6, 1, 2, 1000, "")
	port("3", "Gi1/0/3", 6, 2, 2, 1000, "unused")
	port("4", "Te1/1/1", 6, 1, 1, 10000, "To server")
	port("10", "Vlan1", 53, 1, 1, 1000, "")
	port("11", "Po1", 161, 1, 1, 20000, "")
	// ENTITY-MIB: chassis, a power supply, a fan, an empty module slot
	for k, x := range map[string][2]any{"1": {3, "c9200L-48P-4X"}, "2": {6, "PWR-C5-125WAC"}, "3": {7, ""}, "4": {9, ""}} {
		v[ent+".5."+k] = snmp.Int(int64(x[0].(int)))
		v[ent+".13."+k] = snmp.Str(x[1].(string))
	}
	v[ent+".7.1"], v[ent+".11.1"], v[ent+".10.1"] = snmp.Str("Switch 1"), snmp.Str("JAE25110ABC"), snmp.Str("17.9.4")
	v[ent+".7.2"], v[ent+".11.2"] = snmp.Str("Switch 1 - Power Supply A"), snmp.Str("DCB2502X1")
	v[ent+".7.3"] = snmp.Str("Switch 1 - FAN 1")
	v[ent+".7.4"] = snmp.Str("Switch 1 - Slot 2")
	// CISCO-ENVMON-MIB: the power supply is critical, the fan normal, one temperature
	v["1.3.6.1.4.1.9.9.13.1.5.1.2.1"], v["1.3.6.1.4.1.9.9.13.1.5.1.3.1"] = snmp.Str("Switch 1 - Power Supply A"), snmp.Int(3)
	v["1.3.6.1.4.1.9.9.13.1.4.1.2.1"], v["1.3.6.1.4.1.9.9.13.1.4.1.3.1"] = snmp.Str("Switch 1 - FAN 1"), snmp.Int(1)
	v["1.3.6.1.4.1.9.9.13.1.3.1.2.1"], v["1.3.6.1.4.1.9.9.13.1.3.1.3.1"], v["1.3.6.1.4.1.9.9.13.1.3.1.6.1"] = snmp.Str("Inlet"), snmp.Gauge(31), snmp.Int(1)
	addr, stop := snmp.FakeDevice(v)
	defer stop()

	inv, err := SNMPInventory(addr, "public", "")
	if err != nil {
		t.Fatal(err)
	}
	if inv["make"] != "Cisco" || inv["serial_number"] != "JAE25110ABC" || inv["model"] != "c9200L-48P-4X" || inv["os_version"] != "17.9.4" || inv["hostname"] != "CORE-SW1" {
		t.Errorf("identity = %v %v %v %v", inv["make"], inv["serial_number"], inv["model"], inv["os_version"])
	}
	x := inv["hardware_extra"].(map[string]any)
	ports := x["ports"].([]map[string]any)
	if len(ports) != 5 { // the VLAN interface is left out, the LAG kept
		t.Fatalf("ports = %v", ports)
	}
	if ports[0]["state"] != "Connected" || ports[0]["speed"] != "1 Gbps" || ports[0]["description"] != "Uplink to FW" || ports[1]["state"] != "Not connected" || ports[1]["speed"] != nil || ports[2]["state"] != "Disabled" {
		t.Errorf("port states = %v", ports[:3])
	}
	sum := x["port_summary"].(map[string]any)
	sp := sum["speeds"].(map[string]int)
	if sum["total"] != 4 || sum["connected"] != 2 || sum["free"] != 1 || sum["disabled"] != 1 || sp["1 Gbps"] != 1 || sp["10 Gbps"] != 1 {
		t.Errorf("summary = %v", sum)
	}
	mods := x["modules"].([]map[string]any)
	if len(mods) != 1 || mods[0]["kind"] != "Chassis" || mods[0]["serial"] != "JAE25110ABC" {
		t.Errorf("modules (empty slot left out) = %v", mods)
	}
	psus := x["power_supplies"].([]map[string]any)
	if len(psus) != 1 || psus[0]["state"] != "Critical" || psus[0]["model"] != "PWR-C5-125WAC" {
		t.Errorf("psus = %v", psus)
	}
	sensors := x["sensors"].([]map[string]any)
	if len(sensors) != 2 || !strings.Contains(sensors[1]["reading"].(string), "31") {
		t.Errorf("sensors = %v", sensors)
	}
}

func TestAPrinterGetsNoNetworkTables(t *testing.T) {
	addr, stop := snmp.FakeDevice(map[string]snmp.Value{
		"1.3.6.1.2.1.1.1.0": snmp.Str("HP LaserJet M404"), "1.3.6.1.2.1.1.5.0": snmp.Str("PRN-1"),
		"1.3.6.1.2.1.2.2.1.2.1": snmp.Str("eth0"), "1.3.6.1.2.1.2.2.1.3.1": snmp.Int(6), "1.3.6.1.2.1.2.2.1.8.1": snmp.Int(1),
	})
	defer stop()
	inv, err := SNMPInventory(addr, "public", "")
	if err != nil {
		t.Fatal(err)
	}
	if _, ok := inv["hardware_extra"]; ok {
		t.Fatalf("a printer must not get a ports table: %v", inv["hardware_extra"])
	}
}

func TestFortinetSerialAndFirmwareComeFromItsOwnMIB(t *testing.T) {
	v := map[string]snmp.Value{
		"1.3.6.1.2.1.1.1.0": snmp.Str(""), "1.3.6.1.2.1.1.2.0": snmp.ObjectID("1.3.6.1.4.1.12356.101.1.60"), "1.3.6.1.2.1.1.5.0": snmp.Str("FGT-HQ"),
		"1.3.6.1.4.1.12356.100.1.1.1.0":     snmp.Str("FGT60FTK2209ABCD"),
		"1.3.6.1.4.1.12356.101.4.1.1.0":     snmp.Str("v7.2.5,build1517,230606 (GA.F)"),
		"1.3.6.1.4.1.12356.101.4.3.2.1.2.1": snmp.Str("PS1 Status"), "1.3.6.1.4.1.12356.101.4.3.2.1.3.1": snmp.Str("1"), "1.3.6.1.4.1.12356.101.4.3.2.1.4.1": snmp.Int(1),
	}
	for i, n := range []string{"wan1", "wan2", "internal1", "internal2"} {
		k := string(rune('1' + i))
		v["1.3.6.1.2.1.2.2.1.2."+k], v["1.3.6.1.2.1.2.2.1.3."+k], v["1.3.6.1.2.1.2.2.1.8."+k], v["1.3.6.1.2.1.2.2.1.7."+k] = snmp.Str(n), snmp.Int(6), snmp.Int(int64(1+i%2)), snmp.Int(1)
	}
	addr, stop := snmp.FakeDevice(v)
	defer stop()
	inv, err := SNMPInventory(addr, "public", "")
	if err != nil {
		t.Fatal(err)
	}
	if inv["make"] != "Fortinet" || inv["serial_number"] != "FGT60FTK2209ABCD" || inv["os_version"] != "FortiOS v7.2.5" {
		t.Errorf("identity = %v %v %v", inv["make"], inv["serial_number"], inv["os_version"])
	}
	x := inv["hardware_extra"].(map[string]any)
	if p := x["power_supplies"].([]map[string]any); len(p) != 1 || p[0]["state"] != "Critical" {
		t.Errorf("psu = %v", p)
	}
	if s := x["port_summary"].(map[string]any); s["total"] != 4 || s["connected"] != 2 {
		t.Errorf("summary = %v", s)
	}
}
