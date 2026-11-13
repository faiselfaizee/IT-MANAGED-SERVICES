package collector

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
)

const soapHead = `<?xml version="1.0" encoding="UTF-8"?><soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/" xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><soapenv:Body>`
const soapTail = `</soapenv:Body></soapenv:Envelope>`

// fakeVsphere answers like ESXi 8 (apiType HostAgent) or vCenter (VirtualCenter), with the property shapes the real API uses.
func fakeVsphere(t *testing.T, apiType string, seen *[]string) *httptest.Server {
	var mu sync.Mutex
	session := "s-123"
	return httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, q *http.Request) {
		b, _ := io.ReadAll(q.Body)
		body := string(b)
		op := body[strings.Index(body, "<soapenv:Body>")+len("<soapenv:Body><"):]
		op = op[:strings.IndexAny(op, "> ")]
		mu.Lock()
		*seen = append(*seen, op)
		mu.Unlock()
		if op != "RetrieveServiceContent" && op != "Login" {
			if c, err := q.Cookie("vmware_soap_session"); err != nil || c.Value != session {
				w.WriteHeader(500)
				fmt.Fprint(w, soapHead+`<soapenv:Fault><faultcode>ServerFaultCode</faultcode><faultstring>The session is not authenticated.</faultstring><detail><NotAuthenticatedFault xmlns="urn:vim25" xsi:type="NotAuthenticated"></NotAuthenticatedFault></detail></soapenv:Fault>`+soapTail)
				return
			}
		}
		out := ""
		switch op {
		case "RetrieveServiceContent":
			name, full := "VMware ESXi", "VMware ESXi 8.0.2 build-22380479"
			if apiType == "VirtualCenter" {
				name, full = "VMware vCenter Server", "VMware vCenter Server 8.0.2 build-22385739"
			}
			out = `<RetrieveServiceContentResponse xmlns="urn:vim25"><returnval><rootFolder type="Folder">ha-folder-root</rootFolder><propertyCollector type="PropertyCollector">ha-property-collector</propertyCollector><viewManager type="ViewManager">ViewManager</viewManager>` +
				`<about><name>` + name + `</name><fullName>` + full + `</fullName><vendor>VMware, Inc.</vendor><version>8.0.2</version><build>22380479</build><apiType>` + apiType + `</apiType><apiVersion>8.0.2.0</apiVersion></about>` +
				`<sessionManager type="SessionManager">ha-sessionmgr</sessionManager></returnval></RetrieveServiceContentResponse>`
		case "Login":
			if !strings.Contains(body, "<userName>audit</userName>") || !strings.Contains(body, "<password>p&amp;ss</password>") {
				w.WriteHeader(500)
				fmt.Fprint(w, soapHead+`<soapenv:Fault><faultcode>ServerFaultCode</faultcode><faultstring>Cannot complete login due to an incorrect user name or password.</faultstring><detail><InvalidLoginFault xmlns="urn:vim25" xsi:type="InvalidLogin"></InvalidLoginFault></detail></soapenv:Fault>`+soapTail)
				return
			}
			http.SetCookie(w, &http.Cookie{Name: "vmware_soap_session", Value: session, Path: "/"})
			out = `<LoginResponse xmlns="urn:vim25"><returnval><key>52a</key><userName>audit</userName></returnval></LoginResponse>`
		case "CreateContainerView":
			typ := body[strings.Index(body, "<type>")+6 : strings.Index(body, "</type>")]
			out = `<CreateContainerViewResponse xmlns="urn:vim25"><returnval type="ContainerView">session[52a]` + typ + `</returnval></CreateContainerViewResponse>`
		case "DestroyView", "Logout":
			out = "<" + op + `Response xmlns="urn:vim25"></` + op + `Response>`
		case "RetrievePropertiesEx":
			switch {
			case strings.Contains(body, "<type>HostSystem</type>"):
				if !strings.Contains(body, `<selectSet xsi:type="TraversalSpec"><type>ContainerView</type><path>view</path>`) {
					t.Errorf("property request without the container view traversal: %s", body)
				}
				out = `<RetrievePropertiesExResponse xmlns="urn:vim25"><returnval><objects><obj type="HostSystem">ha-host</obj>` + hostProps + `</objects></returnval></RetrievePropertiesExResponse>`
			case strings.Contains(body, "<type>Datastore</type>"):
				out = `<RetrievePropertiesExResponse xmlns="urn:vim25"><returnval><objects><obj type="Datastore">ds-1</obj><propSet><name>summary</name><val xsi:type="DatastoreSummary"><datastore type="Datastore">ds-1</datastore><name>datastore1</name><url>ds:///vmfs/volumes/1/</url><capacity>1999307276288</capacity><freeSpace>823822794752</freeSpace><accessible>true</accessible><multipleHostAccess>false</multipleHostAccess><type>VMFS</type></val></propSet></objects></returnval></RetrievePropertiesExResponse>`
			case strings.Contains(body, "<type>VirtualMachine</type>"):
				// two pages: the first carries a token
				out = `<RetrievePropertiesExResponse xmlns="urn:vim25"><returnval><token>tok1</token>` + vm("vm-1", "SQL01", "poweredOn", "false", "8", "32768", "10.0.0.21") + `</returnval></RetrievePropertiesExResponse>`
			}
		case "ContinueRetrievePropertiesEx":
			out = `<ContinueRetrievePropertiesExResponse xmlns="urn:vim25"><returnval>` + vm("vm-2", "app02", "poweredOff", "false", "4", "8192", "") + vm("vm-3", "Win2022-template", "poweredOff", "true", "2", "4096", "") + `</returnval></ContinueRetrievePropertiesExResponse>`
		}
		if out == "" {
			t.Errorf("unexpected call %s", op)
		}
		w.Header().Set("Content-Type", "text/xml; charset=utf-8")
		fmt.Fprint(w, soapHead+out+soapTail)
	}))
}

func vm(id, name, power, template, cpu, mem, ip string) string {
	return `<objects><obj type="VirtualMachine">` + id + `</obj><propSet><name>name</name><val xsi:type="xsd:string">` + name + `</val></propSet>` +
		`<propSet><name>runtime.powerState</name><val xsi:type="VirtualMachinePowerState">` + power + `</val></propSet>` +
		`<propSet><name>runtime.host</name><val type="HostSystem" xsi:type="ManagedObjectReference">ha-host</val></propSet>` +
		`<propSet><name>config.template</name><val xsi:type="xsd:boolean">` + template + `</val></propSet>` +
		`<propSet><name>config.hardware.numCPU</name><val xsi:type="xsd:int">` + cpu + `</val></propSet>` +
		`<propSet><name>config.hardware.memoryMB</name><val xsi:type="xsd:int">` + mem + `</val></propSet>` +
		`<propSet><name>config.guestFullName</name><val xsi:type="xsd:string">Microsoft Windows Server 2022 (64-bit)</val></propSet>` +
		`<propSet><name>guest.ipAddress</name><val xsi:type="xsd:string">` + ip + `</val></propSet>` +
		`<propSet><name>summary.storage.committed</name><val xsi:type="xsd:long">107374182400</val></propSet></objects>`
}

const hostProps = `<propSet><name>name</name><val xsi:type="xsd:string">esx01.acme.local</val></propSet>` +
	`<propSet><name>summary.hardware</name><val xsi:type="HostHardwareSummary"><vendor>Dell Inc.</vendor><model>PowerEdge R740</model><uuid>4c4c4544</uuid><memorySize>274877906944</memorySize><cpuModel>Intel(R) Xeon(R) Gold 6230 CPU @ 2.10GHz</cpuModel><cpuMhz>2095</cpuMhz><numCpuPkgs>2</numCpuPkgs><numCpuCores>40</numCpuCores><numCpuThreads>80</numCpuThreads><numNics>4</numNics><numHBAs>3</numHBAs></val></propSet>` +
	`<propSet><name>hardware.systemInfo</name><val xsi:type="HostSystemInfo"><vendor>Dell Inc.</vendor><model>PowerEdge R740</model><uuid>4c4c4544</uuid>` +
	`<otherIdentifyingInfo><identifierValue>1234</identifierValue><identifierType><label>Asset Tag</label><summary>Asset tag</summary><key>AssetTag</key></identifierType></otherIdentifyingInfo>` +
	`<otherIdentifyingInfo><identifierValue>7XK2LM3</identifierValue><identifierType><label>Service tag</label><summary>Service tag</summary><key>ServiceTag</key></identifierType></otherIdentifyingInfo></val></propSet>` +
	`<propSet><name>hardware.biosInfo</name><val xsi:type="HostBIOSInfo"><biosVersion>2.19.1</biosVersion><releaseDate>2023-03-14T00:00:00Z</releaseDate><vendor>Dell Inc.</vendor></val></propSet>` +
	`<propSet><name>hardware.cpuPkg</name><val xsi:type="ArrayOfHostCpuPackage"><HostCpuPackage><index>0</index><vendor>intel</vendor><hz>2095000000</hz><busHz>100000000</busHz><description>Intel(R) Xeon(R)  Gold 6230 CPU @ 2.10GHz</description></HostCpuPackage><HostCpuPackage><index>1</index><description>Intel(R) Xeon(R) Gold 6230 CPU @ 2.10GHz</description></HostCpuPackage></val></propSet>` +
	`<propSet><name>hardware.pciDevice</name><val xsi:type="ArrayOfHostPciDevice"><HostPciDevice><id>0000:03:00.0</id><classId>768</classId><vendorId>4318</vendorId><vendorName>NVIDIA Corporation</vendorName><deviceName>Tesla T4</deviceName></HostPciDevice><HostPciDevice><id>0000:18:00.0</id><classId>512</classId><vendorName>Broadcom</vendorName><deviceName>NetXtreme BCM5720</deviceName></HostPciDevice></val></propSet>` +
	`<propSet><name>config.product</name><val xsi:type="AboutInfo"><name>VMware ESXi</name><fullName>VMware ESXi 8.0.2 build-22380479</fullName><version>8.0.2</version><build>22380479</build></val></propSet>` +
	`<propSet><name>config.network.pnic</name><val xsi:type="ArrayOfPhysicalNic"><PhysicalNic><key>key-vim.host.PhysicalNic-vmnic0</key><device>vmnic0</device><pci>0000:18:00.0</pci><driver>ntg3</driver><linkSpeed><speedMb>1000</speedMb><duplex>true</duplex></linkSpeed><mac>24:6e:96:aa:bb:01</mac></PhysicalNic><PhysicalNic><device>vmnic1</device><driver>ntg3</driver><mac>24:6e:96:aa:bb:02</mac></PhysicalNic></val></propSet>` +
	`<propSet><name>config.storageDevice.scsiLun</name><val xsi:type="ArrayOfScsiLun"><ScsiLun xsi:type="HostScsiDisk"><canonicalName>naa.6d0946</canonicalName><displayName>Local DELL Disk (naa.6d0946)</displayName><lunType>disk</lunType><vendor>DELL    </vendor><model>PERC H730P Mini</model><serialNumber>00a1</serialNumber><capacity><block>3904897024</block><blockSize>512</blockSize></capacity><ssd>true</ssd></ScsiLun><ScsiLun xsi:type="HostScsiTopologyLun"><canonicalName>mpx.vmhba32</canonicalName><displayName>Local USB CD-ROM</displayName><lunType>cdrom</lunType></ScsiLun></val></propSet>` +
	`<propSet><name>runtime.bootTime</name><val xsi:type="xsd:dateTime">2026-11-01T06:00:00.123Z</val></propSet>` +
	`<propSet><name>runtime.connectionState</name><val xsi:type="HostSystemConnectionState">connected</val></propSet>` +
	`<propSet><name>runtime.powerState</name><val xsi:type="HostSystemPowerState">poweredOn</val></propSet>` +
	`<propSet><name>overallStatus</name><val xsi:type="ManagedEntityStatus">yellow</val></propSet>` +
	`<propSet><name>runtime.healthSystemRuntime.systemHealthInfo.numericSensorInfo</name><val xsi:type="ArrayOfHostNumericSensorInfo">` +
	`<HostNumericSensorInfo><name>Power Supply 1 PS1 Status: Presence detected</name><healthState><label>Green</label><summary>Sensor is operating under normal conditions</summary><key>green</key></healthState><currentReading>0</currentReading><unitModifier>0</unitModifier><baseUnits></baseUnits><sensorType>power</sensorType></HostNumericSensorInfo>` +
	`<HostNumericSensorInfo><name>Power Supply 2 PS2 Status: Power Supply AC lost</name><healthState><key>red</key></healthState><currentReading>0</currentReading><unitModifier>0</unitModifier><sensorType>power</sensorType></HostNumericSensorInfo>` +
	`<HostNumericSensorInfo><name>System Board 1 Fan1A</name><healthState><key>green</key></healthState><currentReading>756000</currentReading><unitModifier>-2</unitModifier><baseUnits>RPM</baseUnits><sensorType>fan</sensorType></HostNumericSensorInfo>` +
	`<HostNumericSensorInfo><name>System Board 1 Pwr Consumption</name><healthState><key>green</key></healthState><currentReading>29400</currentReading><unitModifier>-2</unitModifier><baseUnits>Watts</baseUnits><sensorType>power</sensorType></HostNumericSensorInfo>` +
	`</val></propSet>`

func TestVMwareInventoryOfAnESXiHost(t *testing.T) {
	var seen []string
	srv := fakeVsphere(t, "HostAgent", &seen)
	defer srv.Close()
	inv, err := VMwareInventory(context.Background(), srv.URL, "audit", "p&ss")
	if err != nil {
		t.Fatal(err)
	}
	b, _ := json.Marshal(inv)
	var doc map[string]any
	_ = json.Unmarshal(b, &doc)
	want := map[string]any{"hostname": "esx01.acme.local", "os_family": "vmware", "os_name": "VMware ESXi", "os_version": "8.0.2 build 22380479", "make": "Dell Inc.",
		"model": "PowerEdge R740", "serial_number": "7XK2LM3", "bios_version": "2.19.1 2023-03-14", "ram_bytes": 274877906944.0, "is_server": true}
	for k, v := range want {
		if doc[k] != v {
			t.Errorf("%s = %v, want %v", k, doc[k], v)
		}
	}
	cpu := doc["cpu"].(map[string]any)
	if cpu["model"] != "Intel(R) Xeon(R) Gold 6230 CPU @ 2.10GHz" || cpu["count"] != 2.0 || cpu["total_cores"] != 40.0 || cpu["logical"] != 80.0 || cpu["manufacturer"] != "Intel" {
		t.Errorf("cpu = %v", cpu)
	}
	disks := doc["disks"].([]any)
	if len(disks) != 1 || disks[0].(map[string]any)["type"] != "SSD" || disks[0].(map[string]any)["size_bytes"] != 3904897024.0*512 {
		t.Errorf("disks = %v (the CD-ROM must be left out)", disks)
	}
	nics := doc["nics"].([]any)
	if len(nics) != 2 || nics[0].(map[string]any)["speed_mbps"] != 1000.0 || nics[0].(map[string]any)["connected"] != true || nics[1].(map[string]any)["connected"] != false {
		t.Errorf("nics = %v", nics)
	}
	x := doc["hardware_extra"].(map[string]any)
	psus := x["power_supplies"].([]any)
	if len(psus) != 2 || psus[1].(map[string]any)["state"] != "Critical" {
		t.Errorf("power supplies = %v", psus)
	}
	sensors := x["sensors"].([]any)
	if len(sensors) != 4 || sensors[0].(map[string]any)["state"] != "Critical" {
		t.Errorf("sensors must list the critical one first: %v", sensors)
	}
	for _, s := range sensors {
		m := s.(map[string]any)
		if m["name"] == "System Board 1 Fan1A" && m["reading"] != "7560 RPM" || m["name"] == "System Board 1 Pwr Consumption" && m["reading"] != "294 Watts" {
			t.Errorf("reading wrong: %v", m)
		}
	}
	gpus := x["gpus"].([]any)
	if len(gpus) != 1 || gpus[0].(map[string]any)["name"] != "Tesla T4" {
		t.Errorf("gpus = %v", gpus)
	}
	ds := x["datastores"].([]any)
	if len(ds) != 1 || ds[0].(map[string]any)["free_bytes"] != 823822794752.0 {
		t.Errorf("datastores = %v", ds)
	}
	vms := x["vms"].([]any)
	if len(vms) != 2 { // the template is left out; the second page (token) is read
		t.Fatalf("vms = %v", vms)
	}
	if m := vms[1].(map[string]any); m["name"] != "SQL01" || m["power"] != "On" || m["memory_bytes"] != 32768.0*1048576 || m["host"] != "esx01.acme.local" || m["ip"] != "10.0.0.21" {
		t.Errorf("vm = %v", m)
	}
	if seen[len(seen)-1] != "Logout" {
		t.Errorf("the session must be closed, calls: %v", seen)
	}
}

func TestVMwareInventoryOfVCenterListsItsHosts(t *testing.T) {
	var seen []string
	srv := fakeVsphere(t, "VirtualCenter", &seen)
	defer srv.Close()
	inv, err := VMwareInventory(context.Background(), srv.URL, "audit", "p&ss")
	if err != nil {
		t.Fatal(err)
	}
	if inv["model"] != "vCenter Server" || inv["os_name"] != "VMware vCenter Server" {
		t.Errorf("vCenter identity: %v %v", inv["model"], inv["os_name"])
	}
	hosts := inv["hardware_extra"].(map[string]any)["hosts"].([]map[string]any)
	if len(hosts) != 1 || hosts[0]["serial"] != "7XK2LM3" || hosts[0]["sockets"] != int64(2) {
		t.Errorf("hosts = %v", hosts)
	}
}

func TestVMwareWrongPasswordIsExplained(t *testing.T) {
	var seen []string
	srv := fakeVsphere(t, "HostAgent", &seen)
	defer srv.Close()
	_, err := VMwareInventory(context.Background(), srv.URL, "audit", "wrong")
	if err != errVsLogin {
		t.Fatalf("got %v", err)
	}
}

func TestVMwareNotAVsphereAddress(t *testing.T) {
	srv := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, q *http.Request) {
		w.WriteHeader(404)
		fmt.Fprint(w, "<html>not here</html>")
	}))
	defer srv.Close()
	_, err := VMwareInventory(context.Background(), srv.URL, "a", "b")
	if err == nil || !strings.Contains(err.Error(), "VMware") {
		t.Fatalf("got %v", err)
	}
}

// The controller uses the VMware login the platform sends with the asset; with none it says where to add one.
func TestReadDeviceUsesTheSavedVMwareLogin(t *testing.T) {
	var seen []string
	srv := fakeVsphere(t, "HostAgent", &seen)
	defer srv.Close()
	target := strings.TrimPrefix(srv.URL, "https://")
	r := &Runner{}
	a := asset{ID: "7", Monitor: "vmware", Logins: []login{{Name: "wrong one", Kind: "vmware", User: "audit", Secret: "nope"}, {Name: "ESXi audit", Kind: "vmware", User: "audit", Secret: "p&ss"}, {Name: "printer", Kind: "snmp", Secret: "public"}}}
	raw, err := r.readDevice(context.Background(), a, target)
	if err != nil {
		t.Fatal(err)
	}
	if raw.(map[string]any)["serial_number"] != "7XK2LM3" {
		t.Fatalf("got %v", raw)
	}
	if _, err := r.readDevice(context.Background(), asset{ID: "8", Monitor: "vmware"}, target); err == nil || !strings.Contains(err.Error(), "Assets > Credentials") {
		t.Fatalf("got %v", err)
	}
}
