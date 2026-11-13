package collector

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func jsonServer(t *testing.T, auth func(*http.Request) bool, routes map[string]any) *httptest.Server {
	return httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, q *http.Request) {
		if auth != nil && !auth(q) {
			w.WriteHeader(401)
			return
		}
		v, ok := routes[q.URL.Path+"?"+q.URL.RawQuery]
		if !ok {
			v, ok = routes[q.URL.Path]
		}
		if !ok {
			w.WriteHeader(404)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		_ = json.NewEncoder(w).Encode(v)
	}))
}

func TestNutanixClusterInventory(t *testing.T) {
	const b = "/PrismGateway/services/rest/v2.0"
	srv := jsonServer(t, func(q *http.Request) bool { u, p, ok := q.BasicAuth(); return ok && u == "admin" && p == "nx" }, map[string]any{
		b + "/cluster": map[string]any{"name": "NTNX-CL1", "version": "6.5.5.7", "num_nodes": 3},
		b + "/hosts": map[string]any{"entities": []any{
			map[string]any{"uuid": "h1", "name": "NTNX-A", "serial": "ZM19A1", "block_serial": "19SM6B", "block_model_name": "NX-3060-G7", "cpu_model": "Intel(R) Xeon(R) Silver 4210", "num_cpu_sockets": 2, "num_cpu_cores": 20, "num_cpu_threads": 40,
				"memory_capacity_in_bytes": 412316860416, "hypervisor_full_name": "Nutanix 20220304.342", "hypervisor_address": "10.0.0.11", "ipmi_address": "10.0.1.11", "state": "NORMAL", "bios_version": "PB40.003"},
			map[string]any{"uuid": "h2", "name": "NTNX-B", "serial": "ZM19A2", "block_model_name": "NX-3060-G7", "state": "NORMAL"},
			map[string]any{"uuid": "h3", "name": "NTNX-C", "serial": "ZM19A3", "block_model_name": "NX-3060-G7", "state": "MAINTENANCE"}}},
		b + "/storage_containers": map[string]any{"entities": []any{map[string]any{"name": "default-container", "max_capacity": 30000000000000, "usage_stats": map[string]any{"storage.user_usage_bytes": "12000000000000"}}}},
		b + "/vms?include_vm_disk_config=true&include_vm_nic_config=true": map[string]any{"entities": []any{
			map[string]any{"name": "SQL01", "power_state": "on", "num_vcpus": 2, "num_cores_per_vcpu": 4, "memory_mb": 32768, "host_uuid": "h1",
				"vm_nics": []any{map[string]any{"ip_address": "10.0.2.21"}}, "vm_disk_info": []any{map[string]any{"is_cdrom": false, "size": 107374182400}, map[string]any{"is_cdrom": true, "size": 5}}},
			map[string]any{"name": "app02", "power_state": "off", "num_vcpus": 2, "memory_mb": 4096}}},
	})
	defer srv.Close()
	inv, err := NutanixInventory(context.Background(), strings.TrimPrefix(srv.URL, "https://"), "admin", "nx")
	if err != nil {
		t.Fatal(err)
	}
	x := roundTrip(t, inv)
	if x["make"] != "Nutanix" || x["model"] != "NX-3060-G7" || x["os_version"] != "6.5.5.7" || x["hostname"] != "NTNX-CL1" {
		t.Errorf("identity = %v %v %v %v", x["make"], x["model"], x["os_version"], x["hostname"])
	}
	e := x["hardware_extra"].(map[string]any)
	hosts := e["hosts"].([]any)
	if len(hosts) != 3 || hosts[0].(map[string]any)["ipmi"] != "10.0.1.11" || hosts[2].(map[string]any)["health"] != "yellow" {
		t.Errorf("hosts = %v", hosts)
	}
	vms := e["vms"].([]any)
	if len(vms) != 2 || vms[1].(map[string]any)["name"] != "SQL01" || vms[1].(map[string]any)["cpus"] != 8.0 || vms[1].(map[string]any)["disk_bytes"] != 107374182400.0 || vms[1].(map[string]any)["host"] != "NTNX-A" {
		t.Errorf("vms = %v", vms)
	}
	if ds := e["datastores"].([]any); ds[0].(map[string]any)["free_bytes"] != 18000000000000.0 {
		t.Errorf("containers = %v", ds)
	}
	if _, err := NutanixInventory(context.Background(), strings.TrimPrefix(srv.URL, "https://"), "admin", "bad"); err != errHvLogin {
		t.Errorf("wrong password: %v", err)
	}
}

func TestProxmoxSingleNodeWithTicketAndToken(t *testing.T) {
	const b = "/api2/json"
	routes := map[string]any{
		b + "/version":                        map[string]any{"data": map[string]any{"version": "8.2.4", "release": "8.2"}},
		b + "/nodes":                          map[string]any{"data": []any{map[string]any{"node": "pve01", "status": "online"}}},
		b + "/nodes/pve01/status":             map[string]any{"data": map[string]any{"cpuinfo": map[string]any{"model": "AMD EPYC 7302P 16-Core Processor", "sockets": 1, "cores": 16, "cpus": 32}, "memory": map[string]any{"total": 137438953472}, "pveversion": "pve-manager/8.2.4", "uptime": 86400}},
		b + "/nodes/pve01/disks/list":         map[string]any{"data": []any{map[string]any{"devpath": "/dev/nvme0n1", "model": "Samsung SSD 980 PRO 2TB", "serial": "S6B0", "size": 2000398934016, "type": "nvme", "health": "PASSED"}, map[string]any{"devpath": "/dev/sda", "model": "ST4000", "size": 4000787030016, "type": "hdd", "health": "FAILED"}}},
		b + "/nodes/pve01/network":            map[string]any{"data": []any{map[string]any{"iface": "eno1", "type": "eth", "active": 1}, map[string]any{"iface": "vmbr0", "type": "bridge", "active": 1, "address": "10.0.0.5"}}},
		b + "/cluster/resources?type=vm":      map[string]any{"data": []any{map[string]any{"vmid": 100, "name": "win2022", "node": "pve01", "status": "running", "maxcpu": 4, "maxmem": 8589934592, "maxdisk": 107374182400, "type": "qemu"}, map[string]any{"vmid": 200, "name": "pihole", "node": "pve01", "status": "stopped", "maxcpu": 1, "type": "lxc"}, map[string]any{"vmid": 9000, "name": "tmpl", "template": 1, "type": "qemu"}}},
		b + "/cluster/resources?type=storage": map[string]any{"data": []any{map[string]any{"storage": "local-lvm", "node": "pve01", "plugintype": "lvmthin", "maxdisk": 1000000000000, "disk": 250000000000, "status": "available"}}},
	}
	var sawTicket, sawToken bool
	srv := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, q *http.Request) {
		if q.URL.Path == b+"/access/ticket" {
			body, _ := io.ReadAll(q.Body)
			if !strings.Contains(string(body), "username=root%40pam") || !strings.Contains(string(body), "password=pw") {
				w.WriteHeader(401)
				return
			}
			fmt.Fprint(w, `{"data":{"ticket":"PVE:root@pam:ABC","CSRFPreventionToken":"x"}}`)
			return
		}
		if c, err := q.Cookie("PVEAuthCookie"); err == nil && c.Value == "PVE:root@pam:ABC" {
			sawTicket = true
		} else if q.Header.Get("Authorization") == "PVEAPIToken=audit@pve!deskask=1111-2222" {
			sawToken = true
		} else {
			w.WriteHeader(401)
			return
		}
		v, ok := routes[q.URL.Path+"?"+q.URL.RawQuery]
		if !ok {
			v, ok = routes[q.URL.Path]
		}
		if !ok {
			w.WriteHeader(404)
			return
		}
		_ = json.NewEncoder(w).Encode(v)
	}))
	defer srv.Close()
	host := strings.TrimPrefix(srv.URL, "https://")
	inv, err := ProxmoxInventory(context.Background(), host, "root", "pw")
	if err != nil || !sawTicket {
		t.Fatalf("ticket login: %v", err)
	}
	x := roundTrip(t, inv)
	cpu := x["cpu"].(map[string]any)
	if x["hostname"] != "pve01" || x["os_version"] != "8.2.4" || cpu["count"] != 1.0 || cpu["total_cores"] != 16.0 || cpu["logical"] != 32.0 || x["ram_bytes"] != 137438953472.0 {
		t.Errorf("identity = %v", x)
	}
	e := x["hardware_extra"].(map[string]any)
	vms := e["vms"].([]any)
	if len(vms) != 2 || vms[0].(map[string]any)["kind"] != "Container" || vms[1].(map[string]any)["power"] != "On" {
		t.Errorf("vms (template left out) = %v", vms)
	}
	drives := e["drives"].([]any)
	if len(drives) != 2 || drives[0].(map[string]any)["media"] != "NVME" || drives[1].(map[string]any)["state"] != "Warning" {
		t.Errorf("drives = %v", drives)
	}
	if n := x["nics"].([]any); len(n) != 1 || n[0].(map[string]any)["name"] != "eno1" {
		t.Errorf("nics = %v", n)
	}
	if _, err := ProxmoxInventory(context.Background(), host, "audit@pve!deskask", "1111-2222"); err != nil || !sawToken {
		t.Fatalf("token login: %v", err)
	}
	if _, err := ProxmoxInventory(context.Background(), host, "root", "nope"); err != errHvLogin {
		t.Fatalf("wrong password: %v", err)
	}
}

func xrResp(v string) string {
	return `<?xml version="1.0"?><methodResponse><params><param><value><struct><member><name>Status</name><value>Success</value></member><member><name>Value</name>` + v + `</member></struct></value></param></params></methodResponse>`
}
func xrStruct(kv ...string) string {
	var b strings.Builder
	b.WriteString("<value><struct>")
	for i := 0; i+1 < len(kv); i += 2 {
		b.WriteString("<member><name>" + kv[i] + "</name>" + kv[i+1] + "</member>")
	}
	b.WriteString("</struct></value>")
	return b.String()
}
func xv(s string) string { return "<value><string>" + s + "</string></value>" }

func TestXCPngPoolOfOneHost(t *testing.T) {
	srv := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, q *http.Request) {
		body, _ := io.ReadAll(q.Body)
		m := string(body)
		method := m[strings.Index(m, "<methodName>")+12 : strings.Index(m, "</methodName>")]
		out := ""
		switch method {
		case "session.login_with_password":
			if !strings.Contains(m, "<string>root</string>") || !strings.Contains(m, "<string>xs</string>") {
				fmt.Fprint(w, `<?xml version="1.0"?><methodResponse><params><param><value><struct><member><name>Status</name><value>Failure</value></member><member><name>ErrorDescription</name><value><array><data><value>SESSION_AUTHENTICATION_FAILED</value></data></array></value></member></struct></value></param></params></methodResponse>`)
				return
			}
			out = xrResp(xv("OpaqueRef:sess"))
		case "session.logout":
			out = xrResp(xv(""))
		case "host.get_all_records":
			out = xrResp(xrStruct("OpaqueRef:h1", xrStruct("name_label", xv("xcp01"), "hostname", xv("xcp01"), "address", xv("10.0.0.7"), "enabled", "<value><boolean>1</boolean></value>", "metrics", xv("OpaqueRef:m1"),
				"software_version", xrStruct("product_brand", xv("XCP-ng"), "product_version", xv("8.3.0")),
				"cpu_info", xrStruct("modelname", xv("Intel(R) Xeon(R) E-2236 CPU @ 3.40GHz"), "cpu_count", xv("12"), "socket_count", xv("1")),
				"bios_strings", xrStruct("system-manufacturer", xv("Supermicro"), "system-product-name", xv("X11SCL-F"), "system-serial-number", xv("SM123"), "bios-version", xv("2.1")))))
		case "host_metrics.get_all_records":
			out = xrResp(xrStruct("OpaqueRef:m1", xrStruct("memory_total", xv("68719476736"))))
		case "VM_guest_metrics.get_all_records":
			out = xrResp(xrStruct("OpaqueRef:g1", xrStruct("networks", xrStruct("0/ip", xv("10.0.0.40"), "0/ipv6/0", xv("fe80::1")), "os_version", xrStruct("name", xv("Debian 12")))))
		case "VM.get_all_records":
			out = xrResp(xrStruct(
				"OpaqueRef:v1", xrStruct("name_label", xv("web01"), "power_state", xv("Running"), "is_a_template", "<value><boolean>0</boolean></value>", "is_control_domain", "<value><boolean>0</boolean></value>", "is_a_snapshot", "<value><boolean>0</boolean></value>",
					"resident_on", xv("OpaqueRef:h1"), "guest_metrics", xv("OpaqueRef:g1"), "VCPUs_max", xv("2"), "memory_static_max", xv("4294967296")),
				"OpaqueRef:d0", xrStruct("name_label", xv("Control domain on host: xcp01"), "is_control_domain", "<value><boolean>1</boolean></value>"),
				"OpaqueRef:t1", xrStruct("name_label", xv("Debian Bookworm 12"), "is_a_template", "<value><boolean>1</boolean></value>")))
		case "SR.get_all_records":
			out = xrResp(xrStruct("OpaqueRef:s1", xrStruct("name_label", xv("Local storage"), "type", xv("ext"), "physical_size", xv("1000000000000"), "physical_utilisation", xv("400000000000")),
				"OpaqueRef:s2", xrStruct("name_label", xv("DVD drives"), "type", xv("udev"), "physical_size", xv("0"))))
		default:
			t.Errorf("unexpected %s", method)
		}
		fmt.Fprint(w, out)
	}))
	defer srv.Close()
	host := strings.TrimPrefix(srv.URL, "https://")
	inv, err := XenInventory(context.Background(), host, "root", "xs")
	if err != nil {
		t.Fatal(err)
	}
	x := roundTrip(t, inv)
	if x["make"] != "Supermicro" || x["serial_number"] != "SM123" || x["os_name"] != "XCP-ng 8.3.0" || x["ram_bytes"] != 68719476736.0 || x["hostname"] != "xcp01" {
		t.Errorf("identity = %v", x)
	}
	e := x["hardware_extra"].(map[string]any)
	vms := e["vms"].([]any)
	if len(vms) != 1 || vms[0].(map[string]any)["ip"] != "10.0.0.40" || vms[0].(map[string]any)["guest_os"] != "Debian 12" || vms[0].(map[string]any)["host"] != "xcp01" {
		t.Errorf("vms (templates and dom0 left out) = %v", vms)
	}
	if ds := e["datastores"].([]any); len(ds) != 1 || ds[0].(map[string]any)["free_bytes"] != 600000000000.0 {
		t.Errorf("SRs = %v", ds)
	}
	if _, err := XenInventory(context.Background(), host, "root", "bad"); err != errHvLogin {
		t.Errorf("wrong password: %v", err)
	}
}
