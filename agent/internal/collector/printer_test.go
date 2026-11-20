package collector

import (
	"strings"
	"testing"
	"time"

	"github.com/deskask/platform/agent/internal/snmp"
)

// colourLaser answers like a network colour laser printer (HP, Canon, Ricoh and others use these same standard objects).
func colourLaser() map[string]snmp.Value {
	const sup, inp = "1.3.6.1.2.1.43.11.1.1", "1.3.6.1.2.1.43.8.2.1"
	v := map[string]snmp.Value{
		"1.3.6.1.2.1.1.1.0":           snmp.Str("HP Color LaserJet Pro M454dn"),
		"1.3.6.1.2.1.25.3.5.1.1.1":    snmp.Int(4),
		"1.3.6.1.2.1.25.3.5.1.2.1":    snmp.Bin(0x20, 0x80), // low toner; input tray missing... see test
		"1.3.6.1.2.1.25.3.2.1.3.1":    snmp.Str("HP Color LaserJet Pro M454dn"),
		"1.3.6.1.2.1.43.5.1.1.16.1":   snmp.Str("Accounts printer"),
		"1.3.6.1.2.1.43.5.1.1.17.1":   snmp.Str("PHBGK12345"),
		"1.3.6.1.2.1.43.10.2.1.3.1.1": snmp.Int(8),
		"1.3.6.1.2.1.43.10.2.1.4.1.1": snmp.Counter(48213),
		"1.3.6.1.2.1.43.16.5.1.2.1.1": snmp.Str("Ready"),
		"1.3.6.1.2.1.43.18.1.1.2.1.1": snmp.Int(4),
		"1.3.6.1.2.1.43.18.1.1.8.1.1": snmp.Str("Cyan cartridge is low"),
		"1.3.6.1.2.1.43.12.1.1.4.1.1": snmp.Str("black"),
		"1.3.6.1.2.1.43.12.1.1.4.1.2": snmp.Str("cyan"),
		"1.3.6.1.2.1.43.12.1.1.4.1.3": snmp.Str("magenta"),
		"1.3.6.1.2.1.43.12.1.1.4.1.4": snmp.Str("yellow"),
		inp + ".13.1.1":               snmp.Str("Tray 1"),
		inp + ".9.1.1":                snmp.Int(100),
		inp + ".10.1.1":               snmp.Int(-3),
		inp + ".12.1.1":               snmp.Str("A4"),
		inp + ".13.1.2":               snmp.Str("Tray 2"),
		inp + ".9.1.2":                snmp.Int(550),
		inp + ".10.1.2":               snmp.Int(0),
		inp + ".12.1.2":               snmp.Str("A4"),
	}
	add := func(i int, descr string, typ, colorant, class, level, max int64) {
		k := ".1." + string(rune('0'+i))
		v[sup+".3"+k], v[sup+".4"+k], v[sup+".5"+k] = snmp.Int(colorant), snmp.Int(class), snmp.Int(typ)
		v[sup+".6"+k], v[sup+".7"+k], v[sup+".8"+k], v[sup+".9"+k] = snmp.Str(descr), snmp.Int(19), snmp.Int(max), snmp.Int(level)
	}
	add(1, "Black Cartridge HP 414A", 3, 1, 3, 80, 100)
	add(2, "Cyan Cartridge HP 414A", 3, 2, 3, 8, 100)
	add(3, "Magenta Cartridge HP 414A", 3, 3, 3, 50, 100)
	add(4, "Yellow Cartridge HP 414A", 3, 4, 3, 0, 100)
	add(5, "Waste Toner Box", 4, 0, 4, 95, 100)
	return v
}

func TestReadPrinterGivesStatusSuppliesPagesTraysAndAlerts(t *testing.T) {
	addr, stop := snmp.FakeDevice(colourLaser())
	defer stop()
	p, err := ReadPrinter(&snmp.Client{Addr: addr, Community: "public", Timeout: time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if p.Status != "printing" || p.Pages != 48213 || p.Serial != "PHBGK12345" || p.Name != "Accounts printer" || p.Message != "Ready" {
		t.Fatalf("basics: %+v", p)
	}
	if len(p.Supply) != 5 {
		t.Fatalf("supplies: %+v", p.Supply)
	}
	want := []struct {
		color, state string
		pct          float64
	}{{"black", "ok", 80}, {"cyan", "low", 8}, {"magenta", "ok", 50}, {"yellow", "empty", 0}}
	for i, w := range want {
		s := p.Supply[i]
		if s.Color != w.color || s.State != w.state || s.Pct != w.pct || s.Kind != "toner" {
			t.Errorf("supply %d: %+v, want %+v", i, s, w)
		}
	}
	if w := p.Supply[4]; !w.Waste || w.State != "full" || w.Kind != "waste" {
		t.Errorf("a waste box 95%% full is full: %+v", w)
	}
	if len(p.Trays) != 2 || p.Trays[0].State != "ok" || p.Trays[0].Pct != -1 || p.Trays[1].State != "empty" || p.Trays[1].Media != "A4" {
		t.Errorf("trays: %+v", p.Trays)
	}
	if len(p.Alerts) != 1 || p.Alerts[0].Severity != "warning" || !strings.Contains(p.Alerts[0].Text, "Cyan") {
		t.Errorf("alerts: %+v", p.Alerts)
	}
	if len(p.Flags) != 2 || p.Flags[0] != "low_toner" || p.Flags[1] != "input_tray_missing" {
		t.Errorf("flags: %v", p.Flags)
	}
}

func TestReadPrinterSaysWhenTheDeviceIsNotAPrinter(t *testing.T) {
	addr, stop := snmp.FakeDevice(map[string]snmp.Value{"1.3.6.1.2.1.1.1.0": snmp.Str("Cisco IOS")})
	defer stop()
	if _, err := ReadPrinter(&snmp.Client{Addr: addr, Community: "public", Timeout: time.Second}); err == nil || !strings.Contains(err.Error(), "printer") {
		t.Fatalf("got %v", err)
	}
}

func TestSupplyLevelRules(t *testing.T) {
	for _, c := range []struct {
		level, max, unit int64
		pct              float64
		state            string
	}{
		{-3, 0, 0, -1, "ok"}, {-2, 0, 0, -1, "unknown"}, {0, 100, 0, 0, "empty"}, {1500, 3000, 8, 50, "ok"}, {300, 3000, 8, 10, "low"},
		{42, 0, 19, 42, "ok"}, {42, 0, 8, -1, "unknown"}, {5000, 3000, 8, 100, "ok"},
	} {
		pct, st := supplyLevel(c.level, c.max, c.unit)
		if pct != c.pct || st != c.state {
			t.Errorf("%+v: got %v %s", c, pct, st)
		}
	}
}

func TestErrorFlagsAreDecodedFromBothOctets(t *testing.T) {
	got := decodeFlags([]byte{0x04 | 0x02, 0x08 | 0x04})
	want := []string{"jammed", "offline", "output_full", "input_tray_empty"}
	if strings.Join(got, ",") != strings.Join(want, ",") {
		t.Fatalf("got %v", got)
	}
	if len(decodeFlags(nil)) != 0 {
		t.Fatal("no bits, no flags")
	}
}

func TestAPrinterWithTwoCountersReportsTheLargerAsTheTotal(t *testing.T) {
	v := map[string]snmp.Value{
		"1.3.6.1.2.1.1.1.0": snmp.Str("Ricoh"), "1.3.6.1.2.1.25.3.5.1.1.1": snmp.Int(3),
		"1.3.6.1.2.1.43.10.2.1.3.1.1": snmp.Int(8), "1.3.6.1.2.1.43.10.2.1.4.1.1": snmp.Counter(900),
		"1.3.6.1.2.1.43.10.2.1.3.1.2": snmp.Int(8), "1.3.6.1.2.1.43.10.2.1.4.1.2": snmp.Counter(12000),
		"1.3.6.1.2.1.43.10.2.1.3.1.3": snmp.Int(11), "1.3.6.1.2.1.43.10.2.1.4.1.3": snmp.Counter(99999), // hours: not pages
	}
	addr, stop := snmp.FakeDevice(v)
	defer stop()
	p, err := ReadPrinter(&snmp.Client{Addr: addr, Community: "public", Timeout: time.Second})
	if err != nil || p.Pages != 12000 || p.Status != "idle" {
		t.Fatalf("%+v %v", p, err)
	}
}

func TestAPrinterWithTwoNetworkPortsIsNotTreatedAsASwitch(t *testing.T) {
	const ift = "1.3.6.1.2.1.2.2.1"
	v := colourLaser()
	for i, name := range []string{"eth0", "wlan0"} {
		k := string(rune('1' + i))
		v[ift+".2."+k], v[ift+".3."+k], v[ift+".7."+k], v[ift+".8."+k], v[ift+".5."+k] = snmp.Str(name), snmp.Int(6), snmp.Int(1), snmp.Int(1), snmp.Gauge(100000000)
		v[ift+".6."+k] = snmp.Bin(0x70, 0x1b, 0x7b, 0, 0, byte(i+1))
	}
	v["1.3.6.1.2.1.1.2.0"] = snmp.ObjectID("1.3.6.1.4.1.11.2.3.9.1")
	addr, stop := snmp.FakeDevice(v)
	defer stop()
	inv, err := SNMPInventory(addr, "public", "x")
	if err != nil {
		t.Fatal(err)
	}
	if inv["is_printer"] != true || inv["hardware_extra"] != nil {
		t.Fatalf("a printer must not get switch tables: %v", inv["hardware_extra"])
	}
	if inv["make"] != "HP" {
		t.Errorf("make: %v", inv["make"])
	}
}
