package collector

import (
	"context"
	"crypto/tls"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"regexp"
	"sort"
	"strings"
	"sync"
	"time"
)

// Servers' management controllers (HPE iLO 4/5/6, Dell iDRAC 8/9, Lenovo XClarity Controller, IBM IMM2) all answer the DMTF Redfish API:
// JSON over HTTPS under /redfish/v1, with links ("@odata.id") from one resource to the next. One reader serves every vendor.

type jobj = map[string]any

func jstr(m jobj, path ...string) string {
	var v any = m
	for _, p := range path {
		mm, ok := v.(map[string]any)
		if !ok {
			return ""
		}
		v = mm[p]
	}
	switch x := v.(type) {
	case string:
		return strings.TrimSpace(x)
	case float64:
		if x == float64(int64(x)) {
			return fmt.Sprintf("%d", int64(x))
		}
		return fmt.Sprintf("%g", x)
	case bool:
		if x {
			return "true"
		}
		return "false"
	}
	return ""
}

func jnum(m jobj, path ...string) float64 {
	var v any = m
	for _, p := range path {
		mm, ok := v.(map[string]any)
		if !ok {
			return 0
		}
		v = mm[p]
	}
	f, _ := v.(float64)
	return f
}

func jarr(m jobj, key string) []jobj {
	a, _ := m[key].([]any)
	var o []jobj
	for _, x := range a {
		if mm, ok := x.(map[string]any); ok {
			o = append(o, mm)
		}
	}
	return o
}

// link is the address a resource points to: {"Processors": {"@odata.id": "/redfish/v1/Systems/1/Processors"}}.
func link(m jobj, key string) string {
	if l, ok := m[key].(map[string]any); ok {
		return jstr(l, "@odata.id")
	}
	return ""
}

type redfish struct {
	base, user, pass string
	hc               *http.Client
	mu               sync.Mutex
	gets             int
}

var errRfLogin = errors.New("the management controller refused the login: check the user name and password saved under Assets > Credentials (kind Server management)")

func newRedfish(host, user, pass string) *redfish {
	tr := &http.Transport{
		// iLO, iDRAC and XClarity come with self-signed certificates; the controller talks to them on the customer's own network
		TLSClientConfig:     &tls.Config{InsecureSkipVerify: true}, //nolint:gosec
		DialContext:         (&net.Dialer{Timeout: 10 * time.Second}).DialContext,
		TLSHandshakeTimeout: 15 * time.Second,
		MaxIdleConnsPerHost: 4,
	}
	if !strings.Contains(host, "://") {
		host = "https://" + host
	}
	return &redfish{base: strings.TrimRight(host, "/"), user: user, pass: pass, hc: &http.Client{Transport: tr, Timeout: 40 * time.Second}}
}

// get reads one resource. Links are absolute paths on the same controller.
func (r *redfish) get(ctx context.Context, path string) (jobj, error) {
	r.mu.Lock()
	r.gets++
	n := r.gets
	r.mu.Unlock()
	if n > 400 {
		return nil, errors.New("too many requests to the management controller")
	}
	if !strings.HasPrefix(path, "/") {
		path = "/" + path
	}
	req, err := http.NewRequestWithContext(ctx, "GET", r.base+path, nil)
	if err != nil {
		return nil, err
	}
	req.SetBasicAuth(r.user, r.pass)
	req.Header.Set("Accept", "application/json")
	req.Header.Set("OData-Version", "4.0")
	resp, err := r.hc.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	b, _ := io.ReadAll(io.LimitReader(resp.Body, 8<<20))
	switch {
	case resp.StatusCode == 401 || resp.StatusCode == 403:
		return nil, errRfLogin
	case resp.StatusCode == 404:
		return nil, errNotFound
	case resp.StatusCode != 200:
		return nil, fmt.Errorf("HTTP %d from %s", resp.StatusCode, path)
	}
	var m jobj
	if err := json.Unmarshal(b, &m); err != nil {
		return nil, fmt.Errorf("%s did not answer with Redfish JSON", path)
	}
	return m, nil
}

var errNotFound = errors.New("not found")

// members reads a collection and each of its members, four at a time.
func (r *redfish) members(ctx context.Context, path string) []jobj {
	if path == "" {
		return nil
	}
	c, err := r.get(ctx, path)
	if err != nil {
		return nil
	}
	var paths []string
	for _, m := range jarr(c, "Members") {
		if p := jstr(m, "@odata.id"); p != "" {
			paths = append(paths, p)
		}
	}
	out := make([]jobj, len(paths))
	sem := make(chan struct{}, 4)
	var wg sync.WaitGroup
	for i, p := range paths {
		wg.Add(1)
		go func(i int, p string) {
			defer wg.Done()
			sem <- struct{}{}
			defer func() { <-sem }()
			if m, err := r.get(ctx, p); err == nil {
				out[i] = m
			}
		}(i, p)
	}
	wg.Wait()
	var o []jobj
	for _, m := range out {
		if m != nil {
			o = append(o, m)
		}
	}
	return o
}

// health is the state shown to people: OK, Warning, Critical, or Absent for an empty slot.
func health(m jobj) string {
	if strings.EqualFold(jstr(m, "Status", "State"), "Absent") {
		return "Absent"
	}
	switch strings.ToLower(firstNonEmpty(jstr(m, "Status", "Health"), jstr(m, "Status", "HealthRollup"))) {
	case "ok":
		return "OK"
	case "warning":
		return "Warning"
	case "critical":
		return "Critical"
	}
	return "Unknown"
}

var gpuRx = regexp.MustCompile(`(?i)nvidia|tesla|quadro|geforce|radeon|instinct|\bgpu\b|a100|h100|l40|\bt4\b|a40|a30|a16|a10\b`)

// RedfishInventory reads a server through its management controller.
func RedfishInventory(ctx context.Context, host, user, pass string) (map[string]any, error) {
	ctx, cancel := context.WithTimeout(ctx, 4*time.Minute)
	defer cancel()
	r := newRedfish(host, user, pass)
	root, err := r.get(ctx, "/redfish/v1/")
	if err != nil {
		if errors.Is(err, errNotFound) {
			return nil, fmt.Errorf("%s has no Redfish service (/redfish/v1): is this an iLO, iDRAC or XClarity address, and is Redfish enabled?", host)
		}
		var ne net.Error
		if errors.As(err, &ne) || strings.Contains(err.Error(), "connect") {
			return nil, fmt.Errorf("cannot reach %s on port 443: %v", host, err)
		}
		return nil, err
	}
	systems := r.members(ctx, link(root, "Systems"))
	if len(systems) == 0 {
		// the service root is readable without a login on most controllers; the systems are not
		if _, err := r.get(ctx, link(root, "Systems")); errors.Is(err, errRfLogin) {
			return nil, errRfLogin
		}
		return nil, errors.New("the management controller lists no computer system")
	}
	sys := systems[0]
	extra := jobj{"platform": "redfish"}

	// the management controller itself
	var bmcName string
	if ms := r.members(ctx, link(root, "Managers")); len(ms) > 0 {
		m := ms[0]
		bmcName = strings.TrimSpace(firstNonEmpty(jstr(m, "Model"), jstr(m, "Name")) + " " + jstr(m, "FirmwareVersion"))
		extra["bmc"] = jobj{"model": firstNonEmpty(jstr(m, "Model"), jstr(m, "Name")), "firmware": jstr(m, "FirmwareVersion"), "state": health(m)}
	}
	vendor := firstNonEmpty(jstr(root, "Vendor"), jstr(sys, "Manufacturer"))
	extra["api"] = strings.TrimSpace(firstNonEmpty(bmcName, vendor) + " (Redfish " + jstr(root, "RedfishVersion") + ")")
	extra["host_state"] = jobj{"power": jstr(sys, "PowerState"), "overall": health(sys)}

	// processors: CPUs, and GPUs where the controller lists them here
	var procs, gpus []jobj
	var cores, threads, sockets float64
	var cpuModel, cpuMake, socketName string
	for _, p := range r.members(ctx, link(sys, "Processors")) {
		if health(p) == "Absent" {
			continue
		}
		typ := firstNonEmpty(jstr(p, "ProcessorType"), "CPU")
		model := strings.Join(strings.Fields(firstNonEmpty(jstr(p, "Model"), jstr(p, "Name"))), " ")
		if strings.EqualFold(typ, "GPU") {
			gpus = append(gpus, jobj{"name": model, "vendor": jstr(p, "Manufacturer"), "slot": firstNonEmpty(jstr(p, "Socket"), jstr(p, "Id"))})
			continue
		}
		sockets++
		cores += jnum(p, "TotalCores")
		threads += jnum(p, "TotalThreads")
		cpuModel = firstNonEmpty(cpuModel, model)
		cpuMake = firstNonEmpty(cpuMake, jstr(p, "Manufacturer"))
		socketName = firstNonEmpty(socketName, jstr(p, "Socket"))
		procs = append(procs, jobj{"socket": firstNonEmpty(jstr(p, "Socket"), jstr(p, "Id")), "model": model, "cores": int64(jnum(p, "TotalCores")), "threads": int64(jnum(p, "TotalThreads")),
			"max_mhz": int64(jnum(p, "MaxSpeedMHz")), "state": health(p)})
	}
	if sockets == 0 {
		sockets = jnum(sys, "ProcessorSummary", "Count")
		cpuModel = firstNonEmpty(cpuModel, jstr(sys, "ProcessorSummary", "Model"))
	}
	switch m := strings.ToLower(cpuMake + " " + cpuModel); {
	case strings.Contains(m, "intel"):
		cpuMake = "Intel"
	case strings.Contains(m, "amd"):
		cpuMake = "AMD"
	}

	// memory modules
	var dimms []jobj
	var ram float64
	for _, d := range r.members(ctx, link(sys, "Memory")) {
		size := jnum(d, "CapacityMiB") * 1048576
		if health(d) == "Absent" || size == 0 {
			continue
		}
		ram += size
		dimms = append(dimms, jobj{"slot": firstNonEmpty(jstr(d, "DeviceLocator"), jstr(d, "Name"), jstr(d, "Id")), "size_bytes": int64(size),
			"type": strings.TrimSpace(jstr(d, "MemoryDeviceType") + " " + jstr(d, "BaseModuleType")), "speed_mhz": int64(firstNum(jnum(d, "OperatingSpeedMhz"), jnum(d, "AllowedSpeedsMHz"))),
			"manufacturer": jstr(d, "Manufacturer"), "part": jstr(d, "PartNumber"), "serial": jstr(d, "SerialNumber"), "state": health(d)})
	}
	sort.SliceStable(dimms, func(i, j int) bool { return natLess(dimms[i]["slot"].(string), dimms[j]["slot"].(string)) })
	if ram == 0 {
		ram = jnum(sys, "MemorySummary", "TotalSystemMemoryGiB") * 1073741824
	}

	// storage: controllers and the physical drives behind them
	var ctrls, drives []jobj
	var disks []map[string]any
	for _, st := range r.members(ctx, link(sys, "Storage")) {
		for _, c := range jarr(st, "StorageControllers") {
			ctrls = append(ctrls, jobj{"name": firstNonEmpty(jstr(c, "Name"), jstr(st, "Name")), "model": jstr(c, "Model"), "firmware": jstr(c, "FirmwareVersion"), "state": health(c)})
		}
		var paths []string
		for _, l := range jarr(st, "Drives") {
			paths = append(paths, jstr(l, "@odata.id"))
		}
		for _, p := range paths {
			d, err := r.get(ctx, p)
			if err != nil || health(d) == "Absent" {
				continue
			}
			kind := strings.ToUpper(jstr(d, "MediaType"))
			drives = append(drives, jobj{"name": firstNonEmpty(jstr(d, "Name"), jstr(d, "Id")), "model": jstr(d, "Model"), "manufacturer": jstr(d, "Manufacturer"), "serial": jstr(d, "SerialNumber"),
				"size_bytes": int64(jnum(d, "CapacityBytes")), "media": kind, "protocol": jstr(d, "Protocol"), "controller": jstr(st, "Name"), "state": health(d)})
			disks = append(disks, map[string]any{"name": firstNonEmpty(jstr(d, "Name"), jstr(d, "Id")), "model": strings.TrimSpace(jstr(d, "Manufacturer") + " " + jstr(d, "Model")),
				"type": kind, "size_bytes": int64(jnum(d, "CapacityBytes")), "serial": jstr(d, "SerialNumber"), "partitions": []any{}})
		}
	}
	if len(drives) == 0 { // older controllers (iLO 4) only have SimpleStorage
		for _, st := range r.members(ctx, link(sys, "SimpleStorage")) {
			for _, d := range jarr(st, "Devices") {
				if health(d) == "Absent" {
					continue
				}
				drives = append(drives, jobj{"name": jstr(d, "Name"), "model": jstr(d, "Model"), "manufacturer": jstr(d, "Manufacturer"), "size_bytes": int64(jnum(d, "CapacityBytes")), "controller": jstr(st, "Name"), "state": health(d)})
				disks = append(disks, map[string]any{"name": jstr(d, "Name"), "model": strings.TrimSpace(jstr(d, "Manufacturer") + " " + jstr(d, "Model")), "size_bytes": int64(jnum(d, "CapacityBytes")), "partitions": []any{}})
			}
		}
	}

	// network ports
	var nics []map[string]any
	for _, e := range r.members(ctx, link(sys, "EthernetInterfaces")) {
		var ips []string
		for _, a := range jarr(e, "IPv4Addresses") {
			if ip := jstr(a, "Address"); ip != "" && ip != "0.0.0.0" {
				ips = append(ips, ip)
			}
		}
		speed := int64(jnum(e, "SpeedMbps"))
		up := strings.EqualFold(jstr(e, "LinkStatus"), "LinkUp") || (jstr(e, "LinkStatus") == "" && speed > 0)
		nics = append(nics, map[string]any{"name": firstNonEmpty(jstr(e, "Name"), jstr(e, "Id")), "description": jstr(e, "Description"), "mac": firstNonEmpty(jstr(e, "MACAddress"), jstr(e, "PermanentMACAddress")),
			"connected": up, "speed_mbps": speed, "ips": ips})
	}

	// chassis: power supplies, fans, temperatures, power use, and PCIe cards (graphics cards)
	var psus, sensors []jobj
	chassisSerial, chassisModel := "", ""
	for _, ch := range r.members(ctx, link(root, "Chassis")) {
		chassisSerial = firstNonEmpty(chassisSerial, jstr(ch, "SerialNumber"))
		chassisModel = firstNonEmpty(chassisModel, jstr(ch, "Model"))
		if p := link(ch, "Power"); p != "" {
			if pw, err := r.get(ctx, p); err == nil {
				for _, s := range jarr(pw, "PowerSupplies") {
					if health(s) == "Absent" {
						continue
					}
					row := jobj{"name": firstNonEmpty(jstr(s, "Name"), "Power supply "+jstr(s, "MemberId")), "model": jstr(s, "Model"), "serial": jstr(s, "SerialNumber"),
						"capacity_w": int64(jnum(s, "PowerCapacityWatts")), "output_w": int64(jnum(s, "LastPowerOutputWatts")), "input_v": int64(jnum(s, "LineInputVoltage")),
						"firmware": jstr(s, "FirmwareVersion"), "state": health(s)}
					row["reading"] = psuReading(row)
					psus = append(psus, row)
				}
				for _, c := range jarr(pw, "PowerControl") {
					if w := jnum(c, "PowerConsumedWatts"); w > 0 {
						sensors = append(sensors, jobj{"name": firstNonEmpty(jstr(c, "Name"), "Power use"), "type": "power", "state": firstNonEmptyState(health(c)), "reading": fmt.Sprintf("%g Watts", w)})
					}
				}
			}
		}
		if p := link(ch, "Thermal"); p != "" {
			if th, err := r.get(ctx, p); err == nil {
				for _, f := range jarr(th, "Fans") {
					if health(f) == "Absent" {
						continue
					}
					unit := firstNonEmpty(jstr(f, "ReadingUnits"), "RPM")
					if unit == "Percent" {
						unit = "%"
					}
					sensors = append(sensors, jobj{"name": firstNonEmpty(jstr(f, "Name"), jstr(f, "FanName")), "type": "fan", "state": firstNonEmptyState(health(f)), "reading": strings.TrimSpace(jstr(f, "Reading") + " " + unit)})
				}
				for _, t := range jarr(th, "Temperatures") {
					if health(t) == "Absent" || jnum(t, "ReadingCelsius") == 0 {
						continue
					}
					sensors = append(sensors, jobj{"name": jstr(t, "Name"), "type": "temperature", "state": firstNonEmptyState(health(t)), "reading": jstr(t, "ReadingCelsius") + " °C"})
				}
			}
		}
		for _, d := range r.members(ctx, link(ch, "PCIeDevices")) {
			name := strings.TrimSpace(firstNonEmpty(jstr(d, "Model"), jstr(d, "Name")))
			if gpuRx.MatchString(name+" "+jstr(d, "Manufacturer")) || strings.EqualFold(jstr(d, "DeviceType"), "GPU") {
				gpus = append(gpus, jobj{"name": name, "vendor": jstr(d, "Manufacturer"), "slot": firstNonEmpty(jstr(d, "Id"), jstr(d, "Name"))})
			}
		}
	}
	sort.SliceStable(sensors, func(i, j int) bool { return sevRank(sensors[i]["state"]) > sevRank(sensors[j]["state"]) })
	gpus = dedupe(gpus)

	extra["processors"], extra["memory_modules"], extra["storage_controllers"], extra["drives"] = procs, dimms, ctrls, drives
	extra["power_supplies"], extra["sensors"], extra["gpus"] = psus, sensors, gpus
	inv := map[string]any{
		"schema": 1, "hostname": jstr(sys, "HostName"), "make": firstNonEmpty(jstr(sys, "Manufacturer"), vendor), "model": firstNonEmpty(jstr(sys, "Model"), chassisModel),
		"serial_number": firstNonEmpty(jstr(sys, "SerialNumber"), chassisSerial), "bios_version": jstr(sys, "BiosVersion"),
		"cpu":       map[string]any{"manufacturer": cpuMake, "model": cpuModel, "count": int64(sockets), "total_cores": int64(cores), "logical": int64(threads), "socket": socketName},
		"ram_bytes": int64(ram), "disks": disks, "nics": nics, "is_server": true, "hardware_extra": extra,
	}
	if sku := jstr(sys, "SKU"); sku != "" {
		extra["sku"] = sku
	}
	return inv, nil
}

func psuReading(p jobj) string {
	var parts []string
	if v, _ := p["output_w"].(int64); v > 0 {
		parts = append(parts, fmt.Sprintf("%d W out", v))
	}
	if v, _ := p["capacity_w"].(int64); v > 0 {
		parts = append(parts, fmt.Sprintf("%d W rated", v))
	}
	if v, _ := p["input_v"].(int64); v > 0 {
		parts = append(parts, fmt.Sprintf("%d V in", v))
	}
	return strings.Join(parts, ", ")
}

func firstNonEmptyState(s string) string {
	if s == "" {
		return "Unknown"
	}
	return s
}

func firstNum(v ...float64) float64 {
	for _, x := range v {
		if x > 0 {
			return x
		}
	}
	return 0
}

func dedupe(l []jobj) []jobj {
	seen := map[string]bool{}
	var o []jobj
	for _, x := range l {
		k := strings.ToLower(fmt.Sprint(x["name"])) // the same card is often listed both as a processor and as a PCIe device
		if !seen[k] {
			seen[k] = true
			o = append(o, x)
		}
	}
	return o
}

var digitsRx = regexp.MustCompile(`\d+|\D+`)

// natLess sorts "DIMM 2" before "DIMM 10".
func natLess(a, b string) bool {
	pa, pb := digitsRx.FindAllString(a, -1), digitsRx.FindAllString(b, -1)
	for i := 0; i < len(pa) && i < len(pb); i++ {
		if pa[i] == pb[i] {
			continue
		}
		var na, nb int
		if _, e1 := fmt.Sscan(pa[i], &na); e1 == nil {
			if _, e2 := fmt.Sscan(pb[i], &nb); e2 == nil {
				return na < nb
			}
		}
		return pa[i] < pb[i]
	}
	return len(pa) < len(pb)
}
