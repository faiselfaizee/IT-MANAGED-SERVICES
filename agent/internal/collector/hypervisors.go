package collector

import (
	"bytes"
	"context"
	"crypto/tls"
	"encoding/json"
	"encoding/xml"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"sort"
	"strconv"
	"strings"
	"time"
)

// Readers for hypervisors that have their own management API: Nutanix (Prism Element REST v2), Proxmox VE (REST) and
// XCP-ng / Citrix Hypervisor (XAPI over XML-RPC). Each returns the common inventory document with a hardware_extra block
// carrying hosts, storage and virtual machines, shaped like the VMware reader's.

var errHvLogin = errors.New("the login was refused: check the user name and password saved under Assets > Credentials")

func lanClient(timeout time.Duration) *http.Client {
	return &http.Client{Timeout: timeout, Transport: &http.Transport{
		// these systems come with self-signed certificates; the controller talks to them on the customer's own network
		TLSClientConfig:     &tls.Config{InsecureSkipVerify: true}, //nolint:gosec
		DialContext:         (&net.Dialer{Timeout: 10 * time.Second}).DialContext,
		TLSHandshakeTimeout: 15 * time.Second,
	}}
}

// withPort adds the product's default port when the address has none.
func withPort(host string, port int) string {
	host = strings.TrimPrefix(strings.TrimPrefix(host, "https://"), "http://")
	host = strings.TrimRight(host, "/")
	if _, _, err := net.SplitHostPort(host); err == nil {
		return host
	}
	return net.JoinHostPort(host, strconv.Itoa(port))
}

func sortVMs(list []map[string]any) {
	sort.Slice(list, func(i, j int) bool {
		return strings.ToLower(fmt.Sprint(list[i]["name"])) < strings.ToLower(fmt.Sprint(list[j]["name"]))
	})
}

func atoi64(s string) int64 { n, _ := strconv.ParseInt(strings.TrimSpace(s), 10, 64); return n }

// ================================================================ Nutanix

func nutanixGet(ctx context.Context, hc *http.Client, base, user, pass, path string) (jobj, error) {
	req, err := http.NewRequestWithContext(ctx, "GET", base+path, nil)
	if err != nil {
		return nil, err
	}
	req.SetBasicAuth(user, pass)
	req.Header.Set("Accept", "application/json")
	resp, err := hc.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	b, _ := io.ReadAll(io.LimitReader(resp.Body, 32<<20))
	if resp.StatusCode == 401 || resp.StatusCode == 403 {
		return nil, errHvLogin
	}
	if resp.StatusCode != 200 {
		return nil, fmt.Errorf("HTTP %d from %s (is this a Nutanix Prism Element address?)", resp.StatusCode, path)
	}
	var m jobj
	if err := json.Unmarshal(b, &m); err != nil {
		return nil, fmt.Errorf("%s did not answer with JSON: is this a Nutanix Prism Element address?", path)
	}
	return m, nil
}

// NutanixInventory reads a Nutanix cluster through Prism Element (port 9440, REST API v2).
func NutanixInventory(ctx context.Context, host, user, pass string) (map[string]any, error) {
	ctx, cancel := context.WithTimeout(ctx, 3*time.Minute)
	defer cancel()
	hc := lanClient(60 * time.Second)
	base := "https://" + withPort(host, 9440) + "/PrismGateway/services/rest/v2.0"
	cl, err := nutanixGet(ctx, hc, base, user, pass, "/cluster")
	if err != nil {
		return nil, err
	}
	hs, err := nutanixGet(ctx, hc, base, user, pass, "/hosts")
	if err != nil {
		return nil, err
	}
	names := map[string]string{}
	var hosts []map[string]any
	var first jobj
	for _, h := range jarr(hs, "entities") {
		if first == nil {
			first = h
		}
		names[jstr(h, "uuid")] = jstr(h, "name")
		hosts = append(hosts, map[string]any{"name": jstr(h, "name"), "vendor": "Nutanix", "model": jstr(h, "block_model_name"), "serial": firstNonEmpty(jstr(h, "serial"), jstr(h, "block_serial")),
			"block": jstr(h, "block_serial"), "esxi": jstr(h, "hypervisor_full_name"), "cpu": jstr(h, "cpu_model"), "sockets": int64(jnum(h, "num_cpu_sockets")), "cores": int64(jnum(h, "num_cpu_cores")),
			"ram_bytes": int64(jnum(h, "memory_capacity_in_bytes")), "address": jstr(h, "hypervisor_address"), "ipmi": jstr(h, "ipmi_address"), "bios": jstr(h, "bios_version"), "bmc": jstr(h, "bmc_version"),
			"state": strings.ToLower(jstr(h, "state")), "health": nutanixHealth(jstr(h, "state"))})
	}
	extra := map[string]any{"platform": "nutanix", "api": strings.TrimSpace("Nutanix AOS " + jstr(cl, "version") + " (cluster " + jstr(cl, "name") + ")"), "hosts": hosts}
	if sc, err := nutanixGet(ctx, hc, base, user, pass, "/storage_containers"); err == nil {
		var list []map[string]any
		for _, c := range jarr(sc, "entities") {
			capacity := int64(jnum(c, "max_capacity"))
			used := atoi64(firstNonEmpty(jstr(c, "usage_stats", "storage.user_usage_bytes"), jstr(c, "usage_stats", "storage.usage_bytes")))
			list = append(list, map[string]any{"name": jstr(c, "name"), "type": "Storage container", "capacity_bytes": capacity, "free_bytes": capacity - used, "accessible": true})
		}
		extra["datastores"] = list
	}
	if vs, err := nutanixGet(ctx, hc, base, user, pass, "/vms?include_vm_disk_config=true&include_vm_nic_config=true"); err == nil {
		var list []map[string]any
		for _, v := range jarr(vs, "entities") {
			var ips []string
			for _, n := range jarr(v, "vm_nics") {
				if ip := jstr(n, "ip_address"); ip != "" {
					ips = append(ips, ip)
				}
			}
			var disk int64
			for _, d := range jarr(v, "vm_disk_info") {
				if jstr(d, "is_cdrom") != "true" {
					disk += int64(jnum(d, "size"))
				}
			}
			power := map[string]string{"on": "On", "off": "Off", "suspended": "Suspended", "paused": "Suspended"}[strings.ToLower(jstr(v, "power_state"))]
			list = append(list, map[string]any{"name": jstr(v, "name"), "power": firstNonEmpty(power, jstr(v, "power_state")), "host": names[jstr(v, "host_uuid")], "ip": strings.Join(ips, ", "),
				"cpus": int64(jnum(v, "num_vcpus") * maxf(jnum(v, "num_cores_per_vcpu"), 1)), "memory_bytes": int64(jnum(v, "memory_mb")) << 20, "disk_bytes": disk, "guest_os": jstr(v, "description")})
		}
		sortVMs(list)
		extra["vms"] = list
	}
	inv := map[string]any{"schema": 1, "hostname": firstNonEmpty(jstr(cl, "name"), host), "os_family": "nutanix", "os_name": "Nutanix AOS", "os_version": jstr(cl, "version"),
		"make": "Nutanix", "model": firstNonEmpty(jstr(first, "block_model_name"), "Nutanix cluster"), "is_server": true, "hardware_extra": extra}
	if len(hosts) == 1 { // a single node: the asset is that node
		h := first
		inv["serial_number"] = firstNonEmpty(jstr(h, "serial"), jstr(h, "block_serial"))
		inv["bios_version"] = jstr(h, "bios_version")
		inv["cpu"] = map[string]any{"model": jstr(h, "cpu_model"), "count": int64(jnum(h, "num_cpu_sockets")), "total_cores": int64(jnum(h, "num_cpu_cores")), "logical": int64(jnum(h, "num_cpu_threads"))}
		inv["ram_bytes"] = int64(jnum(h, "memory_capacity_in_bytes"))
	}
	return inv, nil
}

func nutanixHealth(state string) string {
	if strings.EqualFold(state, "NORMAL") {
		return "green"
	}
	if state == "" {
		return ""
	}
	return "yellow"
}

func maxf(a, b float64) float64 {
	if a > b {
		return a
	}
	return b
}

// ================================================================ Proxmox VE

type pve struct {
	hc         *http.Client
	base, auth string
	cookie     string
}

func (p *pve) get(ctx context.Context, path string) (any, error) {
	req, err := http.NewRequestWithContext(ctx, "GET", p.base+path, nil)
	if err != nil {
		return nil, err
	}
	if p.auth != "" {
		req.Header.Set("Authorization", p.auth)
	}
	if p.cookie != "" {
		req.AddCookie(&http.Cookie{Name: "PVEAuthCookie", Value: p.cookie})
	}
	resp, err := p.hc.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	b, _ := io.ReadAll(io.LimitReader(resp.Body, 32<<20))
	if resp.StatusCode == 401 {
		return nil, errHvLogin
	}
	if resp.StatusCode != 200 {
		return nil, fmt.Errorf("HTTP %d from %s", resp.StatusCode, path)
	}
	var w struct {
		Data any `json:"data"`
	}
	if err := json.Unmarshal(b, &w); err != nil {
		return nil, fmt.Errorf("%s did not answer like Proxmox VE", path)
	}
	return w.Data, nil
}

func asObj(v any) jobj { m, _ := v.(map[string]any); return m }
func asList(v any) []jobj {
	a, _ := v.([]any)
	var o []jobj
	for _, x := range a {
		if m, ok := x.(map[string]any); ok {
			o = append(o, m)
		}
	}
	return o
}

// ProxmoxInventory reads a Proxmox VE node or cluster (port 8006). The login is an API token (user "name@realm!tokenid",
// password the token secret) or a user and password ("root" means root@pam).
func ProxmoxInventory(ctx context.Context, host, user, pass string) (map[string]any, error) {
	ctx, cancel := context.WithTimeout(ctx, 3*time.Minute)
	defer cancel()
	p := &pve{hc: lanClient(60 * time.Second), base: "https://" + withPort(host, 8006) + "/api2/json"}
	if strings.Contains(user, "!") {
		p.auth = "PVEAPIToken=" + user + "=" + pass
	} else {
		if !strings.Contains(user, "@") {
			user += "@pam"
		}
		form := url.Values{"username": {user}, "password": {pass}}
		req, err := http.NewRequestWithContext(ctx, "POST", p.base+"/access/ticket", strings.NewReader(form.Encode()))
		if err != nil {
			return nil, err
		}
		req.Header.Set("Content-Type", "application/x-www-form-urlencoded")
		resp, err := p.hc.Do(req)
		if err != nil {
			return nil, fmt.Errorf("cannot reach Proxmox VE on %s: %v", withPort(host, 8006), err)
		}
		b, _ := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
		resp.Body.Close()
		var t struct {
			Data struct {
				Ticket string `json:"ticket"`
			} `json:"data"`
		}
		if resp.StatusCode == 401 || json.Unmarshal(b, &t) != nil || t.Data.Ticket == "" {
			if resp.StatusCode == 404 {
				return nil, fmt.Errorf("%s does not answer like Proxmox VE", withPort(host, 8006))
			}
			return nil, errHvLogin
		}
		p.cookie = t.Data.Ticket
	}
	ver, err := p.get(ctx, "/version")
	if err != nil {
		return nil, err
	}
	nodesV, err := p.get(ctx, "/nodes")
	if err != nil {
		return nil, err
	}
	nodes := asList(nodesV)
	sort.Slice(nodes, func(i, j int) bool { return jstr(nodes[i], "node") < jstr(nodes[j], "node") })
	var hosts []map[string]any
	var disks []map[string]any
	var drives []jobj
	var firstStatus jobj
	for _, n := range nodes {
		name := jstr(n, "node")
		st := asObj(must(p.get(ctx, "/nodes/"+url.PathEscape(name)+"/status")))
		if firstStatus == nil {
			firstStatus = st
		}
		ci := asObj(st["cpuinfo"])
		sockets, perSocket := jnum(ci, "sockets"), jnum(ci, "cores")
		hosts = append(hosts, map[string]any{"name": name, "esxi": jstr(st, "pveversion"), "cpu": jstr(ci, "model"), "sockets": int64(sockets), "cores": int64(sockets * perSocket),
			"ram_bytes": int64(jnum(asObj(st["memory"]), "total")), "state": jstr(n, "status"), "health": map[string]string{"online": "green"}[jstr(n, "status")]})
		for _, d := range asList(must(p.get(ctx, "/nodes/"+url.PathEscape(name)+"/disks/list"))) {
			kind := strings.ToUpper(jstr(d, "type"))
			state := "OK"
			if h := jstr(d, "health"); h != "" && h != "PASSED" && h != "OK" {
				state = "Warning"
			}
			drives = append(drives, jobj{"name": jstr(d, "devpath"), "model": jstr(d, "model"), "serial": jstr(d, "serial"), "size_bytes": int64(jnum(d, "size")), "media": kind, "controller": name, "state": state})
			disks = append(disks, map[string]any{"name": jstr(d, "devpath"), "model": jstr(d, "model"), "type": kind, "size_bytes": int64(jnum(d, "size")), "serial": jstr(d, "serial"), "partitions": []any{}})
		}
	}
	extra := map[string]any{"platform": "proxmox", "api": strings.TrimSpace("Proxmox VE " + jstr(asObj(ver), "version")), "drives": drives}
	if len(nodes) > 1 {
		extra["hosts"] = hosts
	}
	var vms []map[string]any
	for _, v := range asList(must(p.get(ctx, "/cluster/resources?type=vm"))) {
		if jstr(v, "template") == "1" {
			continue
		}
		kind := "VM"
		if jstr(v, "type") == "lxc" {
			kind = "Container"
		}
		vms = append(vms, map[string]any{"name": jstr(v, "name"), "id": jstr(v, "vmid"), "kind": kind, "power": pvePowerAPI(jstr(v, "status")), "host": jstr(v, "node"),
			"cpus": int64(jnum(v, "maxcpu")), "memory_bytes": int64(jnum(v, "maxmem")), "disk_bytes": int64(jnum(v, "maxdisk"))})
	}
	sortVMs(vms)
	extra["vms"] = vms
	var stores []map[string]any
	for _, s := range asList(must(p.get(ctx, "/cluster/resources?type=storage"))) {
		name := jstr(s, "storage")
		if len(nodes) > 1 && jstr(s, "shared") != "1" {
			name += " (" + jstr(s, "node") + ")"
		}
		stores = append(stores, map[string]any{"name": name, "type": jstr(s, "plugintype"), "capacity_bytes": int64(jnum(s, "maxdisk")), "free_bytes": int64(jnum(s, "maxdisk") - jnum(s, "disk")), "accessible": jstr(s, "status") == "available"})
	}
	extra["datastores"] = dedupeStores(stores)
	inv := map[string]any{"schema": 1, "os_family": "proxmox", "os_name": "Proxmox VE", "os_version": jstr(asObj(ver), "version"), "is_server": true, "hardware_extra": extra}
	if len(nodes) == 1 { // a single node: the asset is that node
		ci := asObj(firstStatus["cpuinfo"])
		inv["hostname"] = jstr(nodes[0], "node")
		inv["cpu"] = map[string]any{"model": jstr(ci, "model"), "count": int64(jnum(ci, "sockets")), "total_cores": int64(jnum(ci, "sockets") * jnum(ci, "cores")), "logical": int64(jnum(ci, "cpus"))}
		inv["ram_bytes"] = int64(jnum(asObj(firstStatus["memory"]), "total"))
		inv["uptime_s"] = int64(jnum(firstStatus, "uptime"))
		inv["disks"] = disks
		var nics []map[string]any
		for _, n := range asList(must(p.get(ctx, "/nodes/"+url.PathEscape(jstr(nodes[0], "node"))+"/network"))) {
			if jstr(n, "type") != "eth" {
				continue
			}
			nics = append(nics, map[string]any{"name": jstr(n, "iface"), "description": "physical port", "connected": jstr(n, "active") == "1", "ips": nonEmpty(jstr(n, "address"))})
		}
		inv["nics"] = nics
	} else {
		inv["hostname"] = host
		inv["model"] = "Proxmox VE cluster"
	}
	return inv, nil
}

func must(v any, _ error) any { return v }

func nonEmpty(s ...string) []string {
	var o []string
	for _, x := range s {
		if x != "" {
			o = append(o, x)
		}
	}
	return o
}

func pvePowerAPI(s string) string {
	switch s {
	case "running":
		return "On"
	case "stopped":
		return "Off"
	case "paused", "suspended":
		return "Suspended"
	}
	return s
}

func dedupeStores(l []map[string]any) []map[string]any {
	seen := map[string]bool{}
	var o []map[string]any
	for _, s := range l {
		k := fmt.Sprint(s["name"])
		if !seen[k] {
			seen[k] = true
			o = append(o, s)
		}
	}
	return o
}

// ================================================================ XCP-ng / Citrix Hypervisor (XAPI, XML-RPC)

type xapi struct {
	hc      *http.Client
	url     string
	session string
}

func xrString(s string) string {
	return "<param><value><string>" + esc(s) + "</string></value></param>"
}

// xrValue turns an XML-RPC <value> into Go: string, int64, bool, float64, []any or map[string]any.
func xrValue(v xnode) any {
	if len(v.Kids) == 0 {
		return strings.TrimSpace(v.Text)
	}
	k := v.Kids[0]
	switch k.XMLName.Local {
	case "string":
		return k.Text
	case "int", "i4", "i8":
		n, _ := strconv.ParseInt(strings.TrimSpace(k.Text), 10, 64)
		return n
	case "boolean":
		return strings.TrimSpace(k.Text) == "1"
	case "double":
		f, _ := strconv.ParseFloat(strings.TrimSpace(k.Text), 64)
		return f
	case "array":
		var o []any
		for _, x := range k.child("data").all("value") {
			o = append(o, xrValue(x))
		}
		return o
	case "struct":
		m := map[string]any{}
		for _, mem := range k.all("member") {
			m[mem.str("name")] = xrValue(mem.child("value"))
		}
		return m
	}
	return strings.TrimSpace(k.Text)
}

func (x *xapi) call(ctx context.Context, method string, params ...string) (any, error) {
	var b bytes.Buffer
	b.WriteString(`<?xml version="1.0"?><methodCall><methodName>` + method + `</methodName><params>`)
	if x.session != "" {
		b.WriteString(xrString(x.session))
	}
	for _, p := range params {
		b.WriteString(xrString(p))
	}
	b.WriteString(`</params></methodCall>`)
	req, err := http.NewRequestWithContext(ctx, "POST", x.url, &b)
	if err != nil {
		return nil, err
	}
	req.Header.Set("Content-Type", "text/xml")
	resp, err := x.hc.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	raw, _ := io.ReadAll(io.LimitReader(resp.Body, 64<<20))
	var doc xnode
	if err := xml.Unmarshal(raw, &doc); err != nil || doc.XMLName.Local != "methodResponse" {
		return nil, fmt.Errorf("%s did not answer like XCP-ng or Citrix Hypervisor (HTTP %d)", x.url, resp.StatusCode)
	}
	res, _ := xrValue(doc.path("params", "param", "value")).(map[string]any)
	if fmt.Sprint(res["Status"]) != "Success" {
		desc := fmt.Sprint(res["ErrorDescription"])
		if strings.Contains(desc, "SESSION_AUTHENTICATION_FAILED") {
			return nil, errHvLogin
		}
		if strings.Contains(desc, "HOST_IS_SLAVE") {
			return nil, fmt.Errorf("this host is a pool member; use the pool master's address (%s)", desc)
		}
		return nil, fmt.Errorf("%s: %s", method, desc)
	}
	return res["Value"], nil
}

func xs(m map[string]any, k string) string { return strings.TrimSpace(fmt.Sprint(nz(m[k]))) }
func nz(v any) any {
	if v == nil {
		return ""
	}
	return v
}
func xm(v any) map[string]any { m, _ := v.(map[string]any); return m }

// XenInventory reads an XCP-ng or Citrix Hypervisor pool through XAPI (port 443, the pool master's address).
func XenInventory(ctx context.Context, host, user, pass string) (map[string]any, error) {
	ctx, cancel := context.WithTimeout(ctx, 3*time.Minute)
	defer cancel()
	x := &xapi{hc: lanClient(60 * time.Second), url: "https://" + withPort(host, 443) + "/"}
	s, err := x.call(ctx, "session.login_with_password", user, pass, "1.0", "DeskAsk")
	if err != nil {
		return nil, err
	}
	x.session = fmt.Sprint(s)
	defer func() { _, _ = x.call(context.Background(), "session.logout") }()
	hostsV, err := x.call(ctx, "host.get_all_records")
	if err != nil {
		return nil, err
	}
	metrics := xm(must(x.call(ctx, "host_metrics.get_all_records")))
	names := map[string]string{}
	var hosts []map[string]any
	var firstHost map[string]any
	refs := make([]string, 0)
	for ref := range xm(hostsV) {
		refs = append(refs, ref)
	}
	sort.Strings(refs)
	api := ""
	for _, ref := range refs {
		h := xm(xm(hostsV)[ref])
		if firstHost == nil {
			firstHost = h
		}
		names[ref] = xs(h, "name_label")
		sv, cpu, bios := xm(h["software_version"]), xm(h["cpu_info"]), xm(h["bios_strings"])
		api = firstNonEmpty(api, strings.TrimSpace(xs(sv, "product_brand")+" "+xs(sv, "product_version")))
		hosts = append(hosts, map[string]any{"name": xs(h, "name_label"), "vendor": xs(bios, "system-manufacturer"), "model": xs(bios, "system-product-name"), "serial": xs(bios, "system-serial-number"),
			"esxi": strings.TrimSpace(xs(sv, "product_brand") + " " + xs(sv, "product_version")), "cpu": xs(cpu, "modelname"), "sockets": atoi64(xs(cpu, "socket_count")), "cores": atoi64(xs(cpu, "cpu_count")),
			"ram_bytes": atoi64(xs(xm(metrics[xs(h, "metrics")]), "memory_total")), "address": xs(h, "address"), "state": map[bool]string{true: "enabled", false: "disabled"}[h["enabled"] == true],
			"health": map[bool]string{true: "green", false: "yellow"}[h["enabled"] == true]})
	}
	extra := map[string]any{"platform": "xen", "api": firstNonEmpty(api, "XCP-ng / Citrix Hypervisor"), "hosts": hosts}
	gm := xm(must(x.call(ctx, "VM_guest_metrics.get_all_records")))
	var vms []map[string]any
	for _, v := range xm(must(x.call(ctx, "VM.get_all_records"))) {
		m := xm(v)
		if m["is_a_template"] == true || m["is_control_domain"] == true || m["is_a_snapshot"] == true {
			continue
		}
		g := xm(gm[xs(m, "guest_metrics")])
		var ips []string
		for k, ip := range xm(g["networks"]) {
			if strings.HasSuffix(k, "/ip") {
				ips = append(ips, fmt.Sprint(ip))
			}
		}
		sort.Strings(ips)
		power := map[string]string{"Running": "On", "Halted": "Off", "Suspended": "Suspended", "Paused": "Suspended"}[xs(m, "power_state")]
		vms = append(vms, map[string]any{"name": xs(m, "name_label"), "power": firstNonEmpty(power, xs(m, "power_state")), "host": names[xs(m, "resident_on")], "ip": strings.Join(ips, ", "),
			"guest_os": xs(xm(g["os_version"]), "name"), "cpus": atoi64(xs(m, "VCPUs_max")), "memory_bytes": atoi64(xs(m, "memory_static_max"))})
	}
	sortVMs(vms)
	extra["vms"] = vms
	var stores []map[string]any
	for _, v := range xm(must(x.call(ctx, "SR.get_all_records"))) {
		m := xm(v)
		size := atoi64(xs(m, "physical_size"))
		if size <= 0 || xs(m, "content_type") == "iso" && size == 0 {
			continue
		}
		stores = append(stores, map[string]any{"name": xs(m, "name_label"), "type": xs(m, "type"), "capacity_bytes": size, "free_bytes": size - atoi64(xs(m, "physical_utilisation")), "accessible": true})
	}
	sortVMs(stores)
	extra["datastores"] = stores
	inv := map[string]any{"schema": 1, "hostname": host, "os_family": "xen", "os_name": firstNonEmpty(api, "XCP-ng"), "is_server": true, "hardware_extra": extra}
	if len(hosts) == 1 {
		h := firstHost
		sv, cpu, bios := xm(h["software_version"]), xm(h["cpu_info"]), xm(h["bios_strings"])
		inv["hostname"] = firstNonEmpty(xs(h, "hostname"), xs(h, "name_label"))
		inv["os_version"] = xs(sv, "product_version")
		inv["make"], inv["model"], inv["serial_number"], inv["bios_version"] = xs(bios, "system-manufacturer"), xs(bios, "system-product-name"), xs(bios, "system-serial-number"), xs(bios, "bios-version")
		inv["cpu"] = map[string]any{"model": xs(cpu, "modelname"), "count": atoi64(xs(cpu, "socket_count")), "logical": atoi64(xs(cpu, "cpu_count"))}
		inv["ram_bytes"] = hosts[0]["ram_bytes"]
		delete(extra, "hosts")
	} else {
		inv["model"] = "Xen pool"
	}
	return inv, nil
}
