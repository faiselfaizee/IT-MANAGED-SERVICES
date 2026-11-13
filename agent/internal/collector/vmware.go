package collector

import (
	"bytes"
	"context"
	"crypto/tls"
	"encoding/xml"
	"errors"
	"fmt"
	"io"
	"math"
	"net"
	"net/http"
	"net/http/cookiejar"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"time"
)

// VMware ESXi and vCenter are read over the vSphere web service (SOAP on https://host/sdk), which every ESXi from 6.5 to 8 and
// every vCenter answers the same way. Only the standard library is used: requests are small XML documents written by hand,
// answers are read into a generic tree.

// xnode is any XML element: name, attributes, text and children.
type xnode struct {
	XMLName xml.Name
	Attrs   []xml.Attr `xml:",any,attr"`
	Text    string     `xml:",chardata"`
	Kids    []xnode    `xml:",any"`
}

// child returns the first child with this local name (an empty node when there is none).
func (n xnode) child(name string) xnode {
	for _, k := range n.Kids {
		if k.XMLName.Local == name {
			return k
		}
	}
	return xnode{}
}

// path walks children: n.path("a", "b") is <a><b>.
func (n xnode) path(names ...string) xnode {
	for _, s := range names {
		n = n.child(s)
	}
	return n
}

func (n xnode) all(name string) []xnode {
	var o []xnode
	for _, k := range n.Kids {
		if k.XMLName.Local == name {
			o = append(o, k)
		}
	}
	return o
}

func (n xnode) str(names ...string) string { return strings.TrimSpace(n.path(names...).Text) }

func (n xnode) num(names ...string) int64 {
	v, _ := strconv.ParseInt(n.str(names...), 10, 64)
	return v
}

func (n xnode) attr(name string) string {
	for _, a := range n.Attrs {
		if a.Name.Local == name {
			return a.Value
		}
	}
	return ""
}

// moref is a managed object reference: <x type="HostSystem">host-12</x>.
type moref struct{ Type, Value string }

func refOf(n xnode) moref { return moref{n.attr("type"), strings.TrimSpace(n.Text)} }

func (m moref) xml(tag string) string {
	return fmt.Sprintf(`<%s type="%s">%s</%s>`, tag, esc(m.Type), esc(m.Value), tag)
}

func esc(s string) string {
	var b bytes.Buffer
	_ = xml.EscapeText(&b, []byte(s))
	return b.String()
}

type vsphere struct {
	url    string
	hc     *http.Client
	sc     xnode // the service content
	logged bool
}

// vsFault is the readable part of a SOAP fault, e.g. "Cannot complete login due to an incorrect user name or password."
type vsFault struct{ Code, Msg string }

func (f *vsFault) Error() string {
	if f.Code != "" {
		return f.Code + ": " + f.Msg
	}
	return f.Msg
}

var errVsLogin = errors.New("the VMware login was refused: check the user name and password saved under Assets > Credentials (kind VMware vCenter or ESXi)")

func newVsphere(host string, timeout time.Duration) *vsphere {
	jar, _ := cookiejar.New(nil)
	tr := &http.Transport{
		// ESXi and vCenter come with a self-signed certificate; the controller talks to them on the customer's own network
		TLSClientConfig:     &tls.Config{InsecureSkipVerify: true}, //nolint:gosec
		DialContext:         (&net.Dialer{Timeout: 10 * time.Second}).DialContext,
		TLSHandshakeTimeout: 15 * time.Second,
	}
	if !strings.Contains(host, "://") {
		host = "https://" + host
	}
	return &vsphere{url: strings.TrimRight(host, "/") + "/sdk", hc: &http.Client{Jar: jar, Transport: tr, Timeout: timeout}}
}

// call sends one SOAP request and returns the element inside <Body>.
func (v *vsphere) call(ctx context.Context, body string) (xnode, error) {
	env := `<?xml version="1.0" encoding="UTF-8"?><soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/" ` +
		`xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns="urn:vim25"><soapenv:Body>` + body + `</soapenv:Body></soapenv:Envelope>`
	req, err := http.NewRequestWithContext(ctx, "POST", v.url, strings.NewReader(env))
	if err != nil {
		return xnode{}, err
	}
	req.Header.Set("Content-Type", "text/xml; charset=utf-8")
	req.Header.Set("SOAPAction", "urn:vim25/6.5")
	resp, err := v.hc.Do(req)
	if err != nil {
		return xnode{}, err
	}
	defer resp.Body.Close()
	raw, err := io.ReadAll(io.LimitReader(resp.Body, 64<<20))
	if err != nil {
		return xnode{}, err
	}
	var doc xnode
	if err := xml.Unmarshal(raw, &doc); err != nil {
		if resp.StatusCode != 200 {
			return xnode{}, fmt.Errorf("HTTP %d from %s (is this a VMware ESXi or vCenter address?)", resp.StatusCode, v.url)
		}
		return xnode{}, fmt.Errorf("unreadable answer from %s: %v", v.url, err)
	}
	if doc.XMLName.Local != "Envelope" {
		return xnode{}, fmt.Errorf("%s did not answer like VMware ESXi or vCenter (HTTP %d); check the address and that port 443 reaches the host", v.url, resp.StatusCode)
	}
	b := doc.child("Body")
	if f := b.child("Fault"); f.XMLName.Local != "" {
		code := ""
		if d := f.child("detail"); len(d.Kids) > 0 {
			code = strings.TrimSuffix(d.Kids[0].XMLName.Local, "Fault")
			if d.Kids[0].attr("type") != "" {
				code = d.Kids[0].attr("type")
			}
		}
		return xnode{}, &vsFault{Code: code, Msg: f.str("faultstring")}
	}
	if len(b.Kids) == 0 {
		return xnode{}, fmt.Errorf("empty answer from %s (HTTP %d)", v.url, resp.StatusCode)
	}
	return b.Kids[0], nil
}

func (v *vsphere) connect(ctx context.Context, user, pass string) error {
	r, err := v.call(ctx, `<RetrieveServiceContent><_this type="ServiceInstance">ServiceInstance</_this></RetrieveServiceContent>`)
	if err != nil {
		return err
	}
	v.sc = r.child("returnval")
	if v.sc.str("about", "apiType") == "" {
		return fmt.Errorf("%s did not answer like VMware ESXi or vCenter", v.url)
	}
	_, err = v.call(ctx, `<Login>`+refOf(v.sc.child("sessionManager")).xml("_this")+`<userName>`+esc(user)+`</userName><password>`+esc(pass)+`</password></Login>`)
	if err != nil {
		var f *vsFault
		if errors.As(err, &f) && strings.Contains(f.Code+f.Msg, "InvalidLogin") || strings.Contains(strings.ToLower(err.Error()), "incorrect user name or password") {
			return errVsLogin
		}
		return err
	}
	v.logged = true
	return nil
}

func (v *vsphere) logout(ctx context.Context) {
	if v.logged {
		_, _ = v.call(ctx, `<Logout>`+refOf(v.sc.child("sessionManager")).xml("_this")+`</Logout>`)
	}
}

// vsObject is one managed object with the properties asked for.
type vsObject struct {
	Ref   moref
	Props map[string]xnode
}

// objects reads properties of every object of one type below the root folder, through a container view.
func (v *vsphere) objects(ctx context.Context, typ string, paths []string) ([]vsObject, error) {
	r, err := v.call(ctx, `<CreateContainerView>`+refOf(v.sc.child("viewManager")).xml("_this")+refOf(v.sc.child("rootFolder")).xml("container")+
		`<type>`+typ+`</type><recursive>true</recursive></CreateContainerView>`)
	if err != nil {
		return nil, err
	}
	view := refOf(r.child("returnval"))
	defer func() { _, _ = v.call(ctx, `<DestroyView>`+view.xml("_this")+`</DestroyView>`) }()
	var ps strings.Builder
	for _, p := range paths {
		ps.WriteString("<pathSet>" + esc(p) + "</pathSet>")
	}
	pc := refOf(v.sc.child("propertyCollector"))
	r, err = v.call(ctx, `<RetrievePropertiesEx>`+pc.xml("_this")+`<specSet><propSet><type>`+typ+`</type>`+ps.String()+`</propSet>`+
		`<objectSet>`+view.xml("obj")+`<skip>true</skip><selectSet xsi:type="TraversalSpec"><type>ContainerView</type><path>view</path><skip>false</skip></selectSet></objectSet>`+
		`</specSet><options><maxObjects>500</maxObjects></options></RetrievePropertiesEx>`)
	if err != nil {
		return nil, err
	}
	var out []vsObject
	for page := 0; page < 40; page++ {
		ret := r.child("returnval")
		for _, o := range ret.all("objects") {
			obj := vsObject{Ref: refOf(o.child("obj")), Props: map[string]xnode{}}
			for _, p := range o.all("propSet") {
				obj.Props[p.str("name")] = p.child("val")
			}
			out = append(out, obj)
		}
		tok := ret.str("token")
		if tok == "" {
			break
		}
		r, err = v.call(ctx, `<ContinueRetrievePropertiesEx>`+pc.xml("_this")+`<token>`+esc(tok)+`</token></ContinueRetrievePropertiesEx>`)
		if err != nil {
			return out, err
		}
	}
	return out, nil
}

var hostPaths = []string{"name", "summary.hardware", "hardware.systemInfo", "hardware.biosInfo", "hardware.cpuPkg", "hardware.pciDevice", "config.product",
	"config.network.pnic", "config.storageDevice.scsiLun", "runtime.bootTime", "runtime.connectionState", "runtime.powerState", "overallStatus",
	"runtime.healthSystemRuntime.systemHealthInfo.numericSensorInfo"}

// hostSerial prefers the serial number (ESXi 7+), then the vendor's service tag, then the enclosure serial.
func hostSerial(si xnode) string {
	if s := si.str("serialNumber"); s != "" {
		return s
	}
	best := map[string]string{}
	for _, o := range si.all("otherIdentifyingInfo") {
		best[o.str("identifierType", "key")] = o.str("identifierValue")
	}
	for _, k := range []string{"SerialNumberTag", "ServiceTag", "EnclosureSerialNumberTag", "AssetTag"} {
		if v := strings.TrimSpace(best[k]); v != "" && !strings.EqualFold(v, "unknown") && v != "None" {
			return v
		}
	}
	return ""
}

// vals returns the items of an array property: <val xsi:type="ArrayOfX"><X/>...</val>.
func vals(n xnode) []xnode { return n.Kids }

func sensorReading(s xnode) string {
	r := float64(s.num("currentReading")) * math.Pow(10, float64(s.num("unitModifier")))
	unit := s.str("baseUnits")
	if unit == "" {
		return strconv.FormatFloat(r, 'f', -1, 64)
	}
	return strings.TrimSpace(strconv.FormatFloat(r, 'f', -1, 64) + " " + unit)
}

func sensorState(s xnode) string {
	switch strings.ToLower(s.str("healthState", "key")) {
	case "green":
		return "OK"
	case "yellow":
		return "Warning"
	case "red":
		return "Critical"
	}
	return "Unknown"
}

// hostDoc turns one HostSystem into the fields of the common inventory document and its extra hardware tables.
func hostDoc(h vsObject) (inv map[string]any, sensors, psus, gpus []map[string]any) {
	hw, si, bios, prod := h.Props["summary.hardware"], h.Props["hardware.systemInfo"], h.Props["hardware.biosInfo"], h.Props["config.product"]
	vendor := firstNonEmpty(si.str("vendor"), hw.str("vendor"))
	model := firstNonEmpty(si.str("model"), hw.str("model"))
	sockets := ""
	if pk := vals(h.Props["hardware.cpuPkg"]); len(pk) > 0 {
		sockets = strings.Join(strings.Fields(pk[0].str("description")), " ")
	}
	var disks []map[string]any
	for _, l := range vals(h.Props["config.storageDevice.scsiLun"]) {
		if l.str("lunType") != "disk" {
			continue
		}
		size := l.num("capacity", "block") * l.num("capacity", "blockSize")
		kind := "HDD"
		if l.str("ssd") == "true" {
			kind = "SSD"
		}
		disks = append(disks, map[string]any{"name": firstNonEmpty(l.str("displayName"), l.str("canonicalName")), "model": strings.TrimSpace(l.str("vendor") + " " + l.str("model")),
			"type": kind, "size_bytes": size, "serial": l.str("serialNumber"), "partitions": []any{}})
	}
	var nics []map[string]any
	for _, p := range vals(h.Props["config.network.pnic"]) {
		speed := p.num("linkSpeed", "speedMb")
		nics = append(nics, map[string]any{"name": p.str("device"), "description": p.str("driver"), "mac": p.str("mac"), "connected": speed > 0, "speed_mbps": speed, "ips": []string{}})
	}
	for _, d := range vals(h.Props["hardware.pciDevice"]) {
		if c := d.num("classId"); c>>8 == 0x03 { // display controllers
			gpus = append(gpus, map[string]any{"name": d.str("deviceName"), "vendor": d.str("vendorName"), "slot": d.str("id")})
		}
	}
	for _, s := range vals(h.Props["runtime.healthSystemRuntime.systemHealthInfo.numericSensorInfo"]) {
		row := map[string]any{"name": s.str("name"), "type": s.str("sensorType"), "state": sensorState(s), "reading": sensorReading(s)}
		sensors = append(sensors, row)
		if strings.EqualFold(s.str("sensorType"), "power") && regexpPSU(s.str("name")) {
			psus = append(psus, map[string]any{"name": s.str("name"), "state": sensorState(s), "reading": sensorReading(s)})
		}
	}
	sort.SliceStable(sensors, func(i, j int) bool { return sevRank(sensors[i]["state"]) > sevRank(sensors[j]["state"]) })
	var up int64
	if bt := h.Props["runtime.bootTime"].Text; bt != "" {
		if t, err := time.Parse(time.RFC3339Nano, strings.TrimSpace(bt)); err == nil {
			up = int64(time.Since(t).Seconds())
		}
	}
	cpuMake := ""
	if m := strings.ToLower(hw.str("cpuModel")); strings.Contains(m, "intel") {
		cpuMake = "Intel"
	} else if strings.Contains(m, "amd") {
		cpuMake = "AMD"
	}
	inv = map[string]any{
		"schema": 1, "hostname": h.Props["name"].Text, "os_family": "vmware", "os_name": firstNonEmpty(prod.str("name"), "VMware ESXi"),
		"os_version": strings.TrimSpace(prod.str("version") + " build " + prod.str("build")), "make": vendor, "model": model, "serial_number": hostSerial(si),
		"bios_version": strings.TrimSpace(bios.str("biosVersion") + " " + strings.Split(bios.str("releaseDate"), "T")[0]),
		"cpu": map[string]any{"manufacturer": cpuMake, "model": strings.Join(strings.Fields(hw.str("cpuModel")), " "), "count": hw.num("numCpuPkgs"), "total_cores": hw.num("numCpuCores"),
			"logical": hw.num("numCpuThreads"), "socket": sockets},
		"ram_bytes": hw.num("memorySize"), "disks": disks, "nics": nics, "uptime_s": up, "is_server": true,
	}
	return inv, sensors, psus, gpus
}

var psuRx = regexp.MustCompile(`(?i)power supply|\bpsu\b|\bps ?\d`)

func regexpPSU(name string) bool { return psuRx.MatchString(name) }

func sevRank(v any) int {
	switch v {
	case "Critical":
		return 3
	case "Warning":
		return 2
	case "Unknown":
		return 1
	}
	return 0
}

// VMwareInventory reads an ESXi host or a vCenter. For ESXi the document describes the server itself; for vCenter it describes
// vCenter and lists its hosts. Both carry the datastores and the virtual machines.
func VMwareInventory(ctx context.Context, host, user, pass string) (map[string]any, error) {
	ctx, cancel := context.WithTimeout(ctx, 3*time.Minute)
	defer cancel()
	v := newVsphere(host, 90*time.Second)
	if err := v.connect(ctx, user, pass); err != nil {
		return nil, err
	}
	defer v.logout(context.Background())
	about := v.sc.child("about")
	hosts, err := v.objects(ctx, "HostSystem", hostPaths)
	if err != nil {
		return nil, fmt.Errorf("reading hosts: %v", err)
	}
	if len(hosts) == 0 {
		return nil, errors.New("this login sees no host: give it the Read-only role on the host (or on vCenter's top level)")
	}
	names := map[string]string{}
	for _, h := range hosts {
		names[h.Ref.Value] = h.Props["name"].Text
	}
	var inv map[string]any
	extra := map[string]any{"platform": "vmware", "api": about.str("fullName")}
	if about.str("apiType") == "HostAgent" {
		var sensors, psus, gpus []map[string]any
		inv, sensors, psus, gpus = hostDoc(hosts[0])
		extra["sensors"], extra["power_supplies"], extra["gpus"] = sensors, psus, gpus
		extra["host_state"] = map[string]any{"connection": hosts[0].Props["runtime.connectionState"].Text, "power": hosts[0].Props["runtime.powerState"].Text, "overall": hosts[0].Props["overallStatus"].Text}
	} else {
		inv = map[string]any{"schema": 1, "hostname": host, "os_family": "vmware", "os_name": firstNonEmpty(about.str("name"), "VMware vCenter Server"),
			"os_version": strings.TrimSpace(about.str("version") + " build " + about.str("build")), "make": "VMware", "model": "vCenter Server", "is_server": true}
		var list []map[string]any
		for _, h := range hosts {
			d, _, _, _ := hostDoc(h)
			c, _ := d["cpu"].(map[string]any)
			list = append(list, map[string]any{"name": d["hostname"], "vendor": d["make"], "model": d["model"], "serial": d["serial_number"], "esxi": d["os_version"],
				"cpu": c["model"], "sockets": c["count"], "cores": c["total_cores"], "ram_bytes": d["ram_bytes"], "state": h.Props["runtime.connectionState"].Text, "health": h.Props["overallStatus"].Text})
		}
		extra["hosts"] = list
	}
	if ds, err := v.objects(ctx, "Datastore", []string{"summary"}); err == nil {
		var list []map[string]any
		for _, d := range ds {
			s := d.Props["summary"]
			list = append(list, map[string]any{"name": s.str("name"), "type": s.str("type"), "capacity_bytes": s.num("capacity"), "free_bytes": s.num("freeSpace"), "accessible": s.str("accessible") == "true"})
		}
		extra["datastores"] = list
	}
	if vms, err := v.objects(ctx, "VirtualMachine", []string{"name", "runtime.powerState", "runtime.host", "config.template", "config.hardware.numCPU", "config.hardware.memoryMB",
		"config.guestFullName", "guest.ipAddress", "guest.hostName", "summary.storage.committed"}); err == nil {
		var list []map[string]any
		for _, m := range vms {
			if m.Props["config.template"].Text == "true" {
				continue
			}
			mem, _ := strconv.ParseInt(strings.TrimSpace(m.Props["config.hardware.memoryMB"].Text), 10, 64)
			disk, _ := strconv.ParseInt(strings.TrimSpace(m.Props["summary.storage.committed"].Text), 10, 64)
			cpus, _ := strconv.ParseInt(strings.TrimSpace(m.Props["config.hardware.numCPU"].Text), 10, 64)
			list = append(list, map[string]any{"name": m.Props["name"].Text, "power": strings.TrimPrefix(m.Props["runtime.powerState"].Text, "powered"), "host": names[strings.TrimSpace(m.Props["runtime.host"].Text)],
				"guest_os": m.Props["config.guestFullName"].Text, "ip": m.Props["guest.ipAddress"].Text, "guest_name": m.Props["guest.hostName"].Text, "cpus": cpus, "memory_bytes": mem << 20, "disk_bytes": disk})
		}
		sort.Slice(list, func(i, j int) bool {
			return strings.ToLower(list[i]["name"].(string)) < strings.ToLower(list[j]["name"].(string))
		})
		extra["vms"] = list
	}
	inv["hardware_extra"] = extra
	return inv, nil
}
