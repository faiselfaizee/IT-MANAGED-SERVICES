package collector

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

// fakeRedfish answers like a Dell iDRAC 9: service root open, everything else behind Basic auth.
func fakeRedfish(t *testing.T, legacyStorage bool) *httptest.Server {
	res := map[string]any{
		"/redfish/v1/": map[string]any{"RedfishVersion": "1.17.0", "Vendor": "Dell", "Product": "Integrated Dell Remote Access Controller",
			"Systems": ref("/redfish/v1/Systems"), "Chassis": ref("/redfish/v1/Chassis"), "Managers": ref("/redfish/v1/Managers")},
		"/redfish/v1/Systems":                   coll("/redfish/v1/Systems/System.Embedded.1"),
		"/redfish/v1/Managers":                  coll("/redfish/v1/Managers/iDRAC.Embedded.1"),
		"/redfish/v1/Managers/iDRAC.Embedded.1": map[string]any{"Model": "15G Monolithic", "Name": "Manager", "FirmwareVersion": "7.00.00.171", "Status": map[string]any{"Health": "OK", "State": "Enabled"}},
		"/redfish/v1/Chassis":                   coll("/redfish/v1/Chassis/System.Embedded.1"),
		"/redfish/v1/Systems/System.Embedded.1": map[string]any{"Manufacturer": "Dell Inc.", "Model": "PowerEdge R750", "SerialNumber": "CN7792", "SKU": "8XK2LM3", "BiosVersion": "1.11.2",
			"HostName": "esx02.acme.local", "PowerState": "On", "Status": map[string]any{"Health": "Warning", "HealthRollup": "Warning", "State": "Enabled"},
			"ProcessorSummary": map[string]any{"Count": 2, "Model": "Intel(R) Xeon(R) Gold 6338 CPU @ 2.00GHz"}, "MemorySummary": map[string]any{"TotalSystemMemoryGiB": 256},
			"Processors": ref("/redfish/v1/Systems/System.Embedded.1/Processors"), "Memory": ref("/redfish/v1/Systems/System.Embedded.1/Memory"),
			"Storage": ref("/redfish/v1/Systems/System.Embedded.1/Storage"), "SimpleStorage": ref("/redfish/v1/Systems/System.Embedded.1/SimpleStorage"),
			"EthernetInterfaces": ref("/redfish/v1/Systems/System.Embedded.1/EthernetInterfaces")},
		"/redfish/v1/Systems/System.Embedded.1/Processors": coll("/p/CPU.Socket.1", "/p/CPU.Socket.2", "/p/Video.Slot.3-1"),
		"/p/CPU.Socket.1":   map[string]any{"Id": "CPU.Socket.1", "Socket": "CPU.Socket.1", "ProcessorType": "CPU", "Manufacturer": "Intel", "Model": "Intel(R) Xeon(R) Gold 6338 CPU @ 2.00GHz", "TotalCores": 32, "TotalThreads": 64, "MaxSpeedMHz": 4000, "Status": map[string]any{"Health": "OK", "State": "Enabled"}},
		"/p/CPU.Socket.2":   map[string]any{"Id": "CPU.Socket.2", "Socket": "CPU.Socket.2", "ProcessorType": "CPU", "Manufacturer": "Intel", "Model": "Intel(R) Xeon(R) Gold 6338 CPU @ 2.00GHz", "TotalCores": 32, "TotalThreads": 64, "Status": map[string]any{"Health": "OK", "State": "Enabled"}},
		"/p/Video.Slot.3-1": map[string]any{"Id": "Video.Slot.3-1", "ProcessorType": "GPU", "Manufacturer": "NVIDIA Corporation", "Model": "NVIDIA A40", "Status": map[string]any{"Health": "OK", "State": "Enabled"}},
		"/redfish/v1/Systems/System.Embedded.1/Memory":             coll("/m/DIMM.Socket.A10", "/m/DIMM.Socket.A2", "/m/DIMM.Socket.A3"),
		"/m/DIMM.Socket.A2":                                        map[string]any{"DeviceLocator": "DIMM.Socket.A2", "CapacityMiB": 131072, "MemoryDeviceType": "DDR4", "OperatingSpeedMhz": 3200, "Manufacturer": "Hynix", "PartNumber": "HMAA8GR7", "SerialNumber": "S1", "Status": map[string]any{"Health": "OK", "State": "Enabled"}},
		"/m/DIMM.Socket.A10":                                       map[string]any{"DeviceLocator": "DIMM.Socket.A10", "CapacityMiB": 131072, "MemoryDeviceType": "DDR4", "OperatingSpeedMhz": 3200, "Manufacturer": "Hynix", "Status": map[string]any{"Health": "Critical", "State": "Enabled"}},
		"/m/DIMM.Socket.A3":                                        map[string]any{"DeviceLocator": "DIMM.Socket.A3", "CapacityMiB": 0, "Status": map[string]any{"State": "Absent"}},
		"/redfish/v1/Systems/System.Embedded.1/EthernetInterfaces": coll("/e/NIC.Integrated.1-1-1", "/e/NIC.Integrated.1-2-1"),
		"/e/NIC.Integrated.1-1-1":                                  map[string]any{"Id": "NIC.Integrated.1-1-1", "Name": "System Ethernet Interface", "MACAddress": "B0:7B:25:00:00:01", "SpeedMbps": 25000, "LinkStatus": "LinkUp", "Status": map[string]any{"Health": "OK"}},
		"/e/NIC.Integrated.1-2-1":                                  map[string]any{"Id": "NIC.Integrated.1-2-1", "MACAddress": "B0:7B:25:00:00:02", "SpeedMbps": 0, "LinkStatus": "LinkDown"},
		"/redfish/v1/Chassis/System.Embedded.1":                    map[string]any{"SerialNumber": "CN7792", "Model": "PowerEdge R750", "Power": ref("/c/Power"), "Thermal": ref("/c/Thermal"), "PCIeDevices": ref("/c/PCIe")},
		"/c/Power": map[string]any{"PowerSupplies": []any{
			map[string]any{"MemberId": "0", "Name": "PS1 Status", "Model": "PWR SPLY,1400W,RDNT,LTON", "SerialNumber": "PS-1", "PowerCapacityWatts": 1400, "LastPowerOutputWatts": 312, "LineInputVoltage": 230, "FirmwareVersion": "00.1B.53", "Status": map[string]any{"Health": "OK", "State": "Enabled"}},
			map[string]any{"MemberId": "1", "Name": "PS2 Status", "Model": "PWR SPLY,1400W,RDNT,LTON", "SerialNumber": "PS-2", "PowerCapacityWatts": 1400, "LineInputVoltage": 0, "Status": map[string]any{"Health": "Critical", "State": "Enabled"}}},
			"PowerControl": []any{map[string]any{"Name": "System Power Control", "PowerConsumedWatts": 336, "Status": map[string]any{"Health": "OK"}}}},
		"/c/Thermal": map[string]any{"Fans": []any{map[string]any{"Name": "System Board Fan1A", "Reading": 7800, "ReadingUnits": "RPM", "Status": map[string]any{"Health": "OK", "State": "Enabled"}},
			map[string]any{"Name": "System Board Fan6A", "Status": map[string]any{"State": "Absent"}}},
			"Temperatures": []any{map[string]any{"Name": "CPU1 Temp", "ReadingCelsius": 54, "Status": map[string]any{"Health": "OK", "State": "Enabled"}}}},
		"/c/PCIe":     coll("/c/PCIe/3-0"),
		"/c/PCIe/3-0": map[string]any{"Id": "3-0", "Name": "GA102GL [A40]", "Manufacturer": "NVIDIA Corporation", "Model": "NVIDIA A40"},
	}
	if legacyStorage {
		res["/redfish/v1/Systems/System.Embedded.1/SimpleStorage"] = coll("/s/AHCI")
		res["/s/AHCI"] = map[string]any{"Name": "Embedded AHCI", "Devices": []any{map[string]any{"Name": "Disk 1", "Model": "MZ7LH480", "Manufacturer": "Samsung", "CapacityBytes": 480103981056, "Status": map[string]any{"Health": "OK", "State": "Enabled"}}}}
	} else {
		res["/redfish/v1/Systems/System.Embedded.1/Storage"] = coll("/st/RAID.Integrated.1-1")
		res["/st/RAID.Integrated.1-1"] = map[string]any{"Name": "PERC H755 Front", "StorageControllers": []any{map[string]any{"Name": "PERC H755 Front", "Model": "PERC H755 Front", "FirmwareVersion": "52.16.1-4405", "Status": map[string]any{"Health": "OK"}}},
			"Drives": []any{ref("/d/0"), ref("/d/1")}}
		res["/d/0"] = map[string]any{"Name": "SSD 0", "Model": "MZ7LH960HBJR0D3", "Manufacturer": "SAMSUNG", "SerialNumber": "S1", "CapacityBytes": 960197124096, "MediaType": "SSD", "Protocol": "SATA", "Status": map[string]any{"Health": "OK", "State": "Enabled"}}
		res["/d/1"] = map[string]any{"Name": "Disk 1", "Model": "ST2000NM", "Manufacturer": "SEAGATE", "CapacityBytes": 2000398934016, "MediaType": "HDD", "Protocol": "SAS", "Status": map[string]any{"Health": "Warning", "State": "Enabled"}}
	}
	return httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, q *http.Request) {
		if q.URL.Path != "/redfish/v1/" {
			if u, p, ok := q.BasicAuth(); !ok || u != "root" || p != "calvin" {
				w.WriteHeader(401)
				return
			}
		}
		v, ok := res[q.URL.Path]
		if !ok {
			w.WriteHeader(404)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		_ = json.NewEncoder(w).Encode(v)
	}))
}

func ref(p string) map[string]any { return map[string]any{"@odata.id": p} }
func coll(p ...string) map[string]any {
	var m []any
	for _, x := range p {
		m = append(m, ref(x))
	}
	return map[string]any{"Members": m, "Members@odata.count": len(m)}
}

func roundTrip(t *testing.T, v any) map[string]any {
	b, _ := json.Marshal(v)
	var m map[string]any
	_ = json.Unmarshal(b, &m)
	return m
}

func TestRedfishInventoryOfADellServer(t *testing.T) {
	srv := fakeRedfish(t, false)
	defer srv.Close()
	raw, err := RedfishInventory(context.Background(), srv.URL, "root", "calvin")
	if err != nil {
		t.Fatal(err)
	}
	d := roundTrip(t, raw)
	for k, v := range map[string]any{"make": "Dell Inc.", "model": "PowerEdge R750", "serial_number": "CN7792", "bios_version": "1.11.2", "hostname": "esx02.acme.local", "ram_bytes": 2.0 * 131072 * 1048576} {
		if d[k] != v {
			t.Errorf("%s = %v, want %v", k, d[k], v)
		}
	}
	cpu := d["cpu"].(map[string]any)
	if cpu["count"] != 2.0 || cpu["total_cores"] != 64.0 || cpu["logical"] != 128.0 || cpu["manufacturer"] != "Intel" || cpu["socket"] != "CPU.Socket.1" {
		t.Errorf("cpu = %v (the GPU must not count as a socket)", cpu)
	}
	x := d["hardware_extra"].(map[string]any)
	if x["api"] != "15G Monolithic 7.00.00.171 (Redfish 1.17.0)" {
		t.Errorf("api = %v", x["api"])
	}
	dimms := x["memory_modules"].([]any)
	if len(dimms) != 2 || dimms[0].(map[string]any)["slot"] != "DIMM.Socket.A2" || dimms[1].(map[string]any)["state"] != "Critical" {
		t.Errorf("dimms (empty slot left out, natural order) = %v", dimms)
	}
	drives := x["drives"].([]any)
	if len(drives) != 2 || drives[0].(map[string]any)["media"] != "SSD" || drives[1].(map[string]any)["state"] != "Warning" || drives[0].(map[string]any)["controller"] != "PERC H755 Front" {
		t.Errorf("drives = %v", drives)
	}
	if c := x["storage_controllers"].([]any); len(c) != 1 || c[0].(map[string]any)["firmware"] != "52.16.1-4405" {
		t.Errorf("controllers = %v", c)
	}
	psus := x["power_supplies"].([]any)
	if len(psus) != 2 || psus[0].(map[string]any)["reading"] != "312 W out, 1400 W rated, 230 V in" || psus[1].(map[string]any)["state"] != "Critical" {
		t.Errorf("psus = %v", psus)
	}
	gpus := x["gpus"].([]any)
	if len(gpus) != 1 { // listed both under Processors and PCIe devices: shown once
		t.Errorf("gpus = %v", gpus)
	}
	foundA40 := false
	for _, g := range gpus {
		if strings.Contains(g.(map[string]any)["name"].(string), "A40") {
			foundA40 = true
		}
	}
	if !foundA40 {
		t.Errorf("gpu missing: %v", gpus)
	}
	sensors := x["sensors"].([]any)
	if len(sensors) != 3 { // power use, one fan (absent one left out), one temperature
		t.Errorf("sensors = %v", sensors)
	}
	nics := d["nics"].([]any)
	if len(nics) != 2 || nics[0].(map[string]any)["speed_mbps"] != 25000.0 || nics[1].(map[string]any)["connected"] != false {
		t.Errorf("nics = %v", nics)
	}
	disks := d["disks"].([]any)
	if len(disks) != 2 || disks[1].(map[string]any)["type"] != "HDD" {
		t.Errorf("disks = %v", disks)
	}
}

func TestRedfishFallsBackToSimpleStorage(t *testing.T) {
	srv := fakeRedfish(t, true)
	defer srv.Close()
	raw, err := RedfishInventory(context.Background(), srv.URL, "root", "calvin")
	if err != nil {
		t.Fatal(err)
	}
	drives := roundTrip(t, raw)["hardware_extra"].(map[string]any)["drives"].([]any)
	if len(drives) != 1 || drives[0].(map[string]any)["controller"] != "Embedded AHCI" {
		t.Errorf("drives = %v", drives)
	}
}

func TestRedfishWrongPasswordAndWrongAddress(t *testing.T) {
	srv := fakeRedfish(t, false)
	defer srv.Close()
	if _, err := RedfishInventory(context.Background(), srv.URL, "root", "nope"); err != errRfLogin {
		t.Fatalf("wrong password: %v", err)
	}
	plain := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, q *http.Request) { w.WriteHeader(404) }))
	defer plain.Close()
	if _, err := RedfishInventory(context.Background(), plain.URL, "a", "b"); err == nil || !strings.Contains(err.Error(), "no Redfish service") {
		t.Fatalf("not a controller: %v", err)
	}
}

// A server read as VMware with its iDRAC address set: both are read, the iDRAC result goes in the "bmc" list.
func TestTheManagementAddressIsReadBesideTheMainRead(t *testing.T) {
	srv := fakeRedfish(t, false)
	defer srv.Close()
	r := &Runner{lastInv: map[string]time.Time{}, InventoryEvery: time.Hour}
	a := asset{ID: "9", Monitor: "ping", Bmc: strings.TrimPrefix(srv.URL, "https://"), BmcLogins: []login{{Name: "wrong", Kind: "bmc", User: "root", Secret: "x"}, {Name: "iDRAC", Kind: "bmc", User: "root", Secret: "calvin"}}}
	r.readBmc(context.Background(), a, "k")
	if len(r.pending.Results.Bmc) != 1 || len(r.pending.Results.Errors) != 0 {
		t.Fatalf("bmc %d, errors %v", len(r.pending.Results.Bmc), r.pending.Results.Errors)
	}
	r.readBmc(context.Background(), asset{ID: "10", Bmc: a.Bmc}, "k2")
	if len(r.pending.Results.Errors) != 1 || !strings.Contains(r.pending.Results.Errors[0].Error, "Management controller") || !strings.Contains(r.pending.Results.Errors[0].Error, "Assets > Credentials") {
		t.Fatalf("errors = %v", r.pending.Results.Errors)
	}
}
