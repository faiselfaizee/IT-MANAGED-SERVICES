package collector

import (
	"bytes"
	"context"
	"encoding/json"
	"encoding/xml"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"os"
	"os/exec"
	"regexp"
	"sort"
	"strings"
	"time"

	"github.com/deskask/platform/agent/internal/config"
)

// Backing up the configuration of firewalls, switches and access points: over SSH with the vendor's own commands, or through the
// vendor's API where it has one (FortiGate REST, Palo Alto XML API, Meraki Dashboard). The text is kept on the platform; a new
// version is stored only when it changed.

// family is the command set to use, from the device's maker (SNMP) or the vendor written on the login.
func family(maker, vendor string) string {
	s := strings.ToLower(vendor + " " + maker)
	for _, f := range []struct{ key, fam string }{
		{"meraki", "meraki"}, {"forti", "fortinet"}, {"palo alto", "paloalto"}, {"sonicwall", "sonicwall"}, {"sophos", "sophos"}, {"aruba", "aruba"},
		{"hp", "aruba"}, {"ubiquiti", "ubiquiti"}, {"unifi", "ubiquiti"}, {"edge", "ubiquiti"}, {"juniper", "juniper"}, {"mikrotik", "mikrotik"}, {"cisco", "cisco"},
	} {
		if strings.Contains(s, f.key) {
			return f.fam
		}
	}
	return ""
}

// sshCommands are typed into the device's command line, one per line, ending with a logout.
var sshCommands = map[string]string{
	"cisco":     "terminal length 0\nshow running-config\nexit\n",
	"fortinet":  "config system console\nset output standard\nend\nshow\nexit\n",
	"aruba":     "no page\nshow running-config\nexit\nexit\ny\n",
	"paloalto":  "set cli pager off\nshow config running\nexit\n",
	"sonicwall": "no cli pager session\nshow current-config\nexit\n",
	"ubiquiti":  "terminal length 0\nshow configuration commands\nshow running-config\nexit\n",
	"juniper":   "show configuration | display set | no-more\nexit\n",
	"mikrotik":  "/export\n/quit\n",
}

var ansiRx = regexp.MustCompile(`\x1b\[[0-9;?]*[A-Za-z]|--More--|\x08+`)

func cleanConfig(s string) string {
	s = ansiRx.ReplaceAllString(strings.ReplaceAll(s, "\r", ""), "")
	return strings.TrimSpace(s)
}

// sshSession logs in and types input, for devices whose command line is not a Unix shell.
func sshSession(ctx context.Context, c config.Cred, host, input string, timeout time.Duration) (string, error) {
	if _, err := exec.LookPath("ssh"); err != nil {
		return "", errors.New("the ssh program is not installed on the controller's computer")
	}
	ctx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	args := append([]string{"-T", "-o", "KexAlgorithms=+diffie-hellman-group14-sha1", "-o", "HostKeyAlgorithms=+ssh-rsa", "-o", "PubkeyAcceptedAlgorithms=+ssh-rsa"}, sshArgs(c, host)...)
	cmd := exec.CommandContext(ctx, "ssh", args...)
	cmd.Env = os.Environ()
	if c.KeyFile == "" {
		self, err := os.Executable()
		if err != nil {
			return "", err
		}
		cmd.Env = append(cmd.Env, "SSH_ASKPASS="+self, "SSH_ASKPASS_REQUIRE=force", AskpassEnv+"=1", "DESKASK_SSH_PASSWORD="+c.Password, "DISPLAY=:0")
	}
	var out, errb bytes.Buffer
	cmd.Stdout, cmd.Stderr, cmd.Stdin = &out, &errb, strings.NewReader(input)
	err := cmd.Run()
	if err != nil && out.Len() < 200 {
		msg := strings.TrimSpace(errb.String())
		if msg == "" {
			msg = err.Error()
		}
		return out.String(), fmt.Errorf("ssh %s: %s", host, lastLines(msg, 3))
	}
	return out.String(), nil // devices often end the session with an error code after "exit"; the output is what counts
}

type cfgResult struct {
	Source, Vendor, Text string
	Meraki               map[string]any // the Meraki device and its ports, when read through the Dashboard API
}

// fetchConfig tries the API logins first (they need no command line), then SSH.
func fetchConfig(ctx context.Context, creds []config.Cred, target, maker, serial string) (*cfgResult, error) {
	var last error
	tried := false
	for _, c := range pick(creds, "netapi", hostOnly(target)) {
		fam := family(maker, c.Vendor)
		tried = true
		var r *cfgResult
		var err error
		switch fam {
		case "fortinet":
			r, err = fortiBackup(ctx, target, c.Password)
		case "paloalto":
			r, err = panExport(ctx, target, "", "", c.Password)
		case "meraki":
			r, err = merakiRead(ctx, "https://api.meraki.com/api/v1", c.Password, hostOnly(target), serial)
		default:
			err = fmt.Errorf("API login %q: write the vendor (Fortinet, Palo Alto or Cisco Meraki) on the login; other vendors are read over SSH", c.Name)
		}
		if err == nil {
			return r, nil
		}
		last = fmt.Errorf("API login %q: %v", c.Name, err)
	}
	for _, c := range pick(creds, "netssh", hostOnly(target)) {
		fam := family(maker, c.Vendor)
		tried = true
		if fam == "paloalto" { // the XML API gives the whole configuration as one document
			if r, err := panExport(ctx, target, c.User, c.Password, ""); err == nil {
				return r, nil
			}
		}
		if fam == "sophos" {
			last = errors.New("Sophos firewalls have a menu, not a command line, over SSH: their configuration cannot be backed up this way (use Sophos Central or the firewall's scheduled backup)")
			continue
		}
		input, ok := sshCommands[fam]
		if !ok {
			last = fmt.Errorf("login %q: the maker is not known, so the commands to show the configuration are not known; write the vendor on the login (for example Cisco, Fortinet, Aruba)", c.Name)
			continue
		}
		out, err := sshSession(ctx, c, hostOnly(target), input, 3*time.Minute)
		text := cleanConfig(out)
		if err != nil && len(text) < 200 {
			last = fmt.Errorf("SSH login %q: %v", c.Name, err)
			continue
		}
		if strings.Count(text, "\n") < 10 {
			last = fmt.Errorf("SSH login %q: the device answered but no configuration came back (%s)", c.Name, lastLines(text, 2))
			continue
		}
		return &cfgResult{Source: "ssh", Vendor: fam, Text: text}, nil
	}
	if !tried {
		return nil, errors.New("no network device login is saved for this customer: add one under Assets > Credentials (used for Network device, SSH or API)")
	}
	return nil, last
}

// ---- FortiGate REST API (token)

func fortiBackup(ctx context.Context, target, token string) (*cfgResult, error) {
	hc := lanClient(90 * time.Second)
	base := "https://" + withPort(target, 443)
	try := func(method, body string) (string, int, error) {
		var rd io.Reader
		if body != "" {
			rd = strings.NewReader(body)
		}
		req, err := http.NewRequestWithContext(ctx, method, base+"/api/v2/monitor/system/config/backup?scope=global", rd)
		if err != nil {
			return "", 0, err
		}
		req.Header.Set("Authorization", "Bearer "+token)
		if body != "" {
			req.Header.Set("Content-Type", "application/json")
		}
		resp, err := hc.Do(req)
		if err != nil {
			return "", 0, err
		}
		defer resp.Body.Close()
		b, _ := io.ReadAll(io.LimitReader(resp.Body, 16<<20))
		return string(b), resp.StatusCode, nil
	}
	text, code, err := try("GET", "")
	if err == nil && (code == 404 || code == 405) { // FortiOS 7.4 and later take a POST
		text, code, err = try("POST", `{"destination":"file","scope":"global"}`)
	}
	switch {
	case err != nil:
		return nil, err
	case code == 401 || code == 403:
		return nil, errors.New("the FortiGate refused the API token (check the token and the trusted hosts of the API user)")
	case code != 200:
		return nil, fmt.Errorf("HTTP %d from the FortiGate", code)
	case !strings.Contains(text, "config "):
		return nil, errors.New("the FortiGate answered but no configuration came back")
	}
	return &cfgResult{Source: "api", Vendor: "fortinet", Text: strings.TrimSpace(text)}, nil
}

// ---- Palo Alto XML API (key, or user and password)

func panExport(ctx context.Context, target, user, pass, key string) (*cfgResult, error) {
	hc := lanClient(90 * time.Second)
	base := "https://" + withPort(target, 443) + "/api/"
	get := func(q url.Values) (string, error) {
		req, err := http.NewRequestWithContext(ctx, "GET", base+"?"+q.Encode(), nil)
		if err != nil {
			return "", err
		}
		resp, err := hc.Do(req)
		if err != nil {
			return "", err
		}
		defer resp.Body.Close()
		b, _ := io.ReadAll(io.LimitReader(resp.Body, 32<<20))
		if resp.StatusCode == 403 || resp.StatusCode == 401 {
			return "", errors.New("the firewall refused the login")
		}
		if resp.StatusCode != 200 {
			return "", fmt.Errorf("HTTP %d from the firewall", resp.StatusCode)
		}
		return string(b), nil
	}
	if key == "" {
		out, err := get(url.Values{"type": {"keygen"}, "user": {user}, "password": {pass}})
		if err != nil {
			return nil, err
		}
		var r struct {
			Status string `xml:"status,attr"`
			Key    string `xml:"result>key"`
		}
		if xml.Unmarshal([]byte(out), &r) != nil || r.Key == "" {
			return nil, errors.New("the firewall refused the login")
		}
		key = r.Key
	}
	out, err := get(url.Values{"type": {"export"}, "category": {"configuration"}, "key": {key}})
	if err != nil {
		return nil, err
	}
	if !strings.Contains(out, "<config") {
		return nil, errors.New("the firewall answered but no configuration came back")
	}
	return &cfgResult{Source: "api", Vendor: "paloalto", Text: strings.TrimSpace(out)}, nil
}

// ---- Cisco Meraki Dashboard API (key); the controller needs to reach api.meraki.com

func merakiRead(ctx context.Context, base, key, ip, serial string) (*cfgResult, error) {
	hc := &http.Client{Timeout: 60 * time.Second}
	get := func(path string, v any) error {
		req, err := http.NewRequestWithContext(ctx, "GET", base+path, nil)
		if err != nil {
			return err
		}
		req.Header.Set("Authorization", "Bearer "+key)
		req.Header.Set("Accept", "application/json")
		resp, err := hc.Do(req)
		if err != nil {
			return fmt.Errorf("cannot reach the Meraki Dashboard (%v); the controller needs internet access to api.meraki.com", err)
		}
		defer resp.Body.Close()
		b, _ := io.ReadAll(io.LimitReader(resp.Body, 16<<20))
		if resp.StatusCode == 401 || resp.StatusCode == 403 {
			return errors.New("the Meraki Dashboard refused the API key")
		}
		if resp.StatusCode != 200 {
			return fmt.Errorf("HTTP %d from the Meraki Dashboard (%s)", resp.StatusCode, path)
		}
		return json.Unmarshal(b, v)
	}
	var orgs []jobj
	if err := get("/organizations", &orgs); err != nil {
		return nil, err
	}
	var dev jobj
	for _, o := range orgs {
		var devs []jobj
		if err := get("/organizations/"+url.PathEscape(jstr(o, "id"))+"/devices?perPage=1000", &devs); err != nil {
			continue
		}
		for _, d := range devs {
			if (serial != "" && strings.EqualFold(jstr(d, "serial"), serial)) || (ip != "" && (jstr(d, "lanIp") == ip || jstr(d, "wan1Ip") == ip)) {
				dev = d
				break
			}
		}
		if dev != nil {
			break
		}
	}
	if dev == nil {
		return nil, fmt.Errorf("no Meraki device with address %s (or serial %q) in the organizations this API key sees", ip, serial)
	}
	s, net := url.PathEscape(jstr(dev, "serial")), url.PathEscape(jstr(dev, "networkId"))
	doc := map[string]any{"device": dev}
	model := strings.ToUpper(jstr(dev, "model"))
	switch {
	case strings.HasPrefix(model, "MS"):
		var ports, statuses []jobj
		if get("/devices/"+s+"/switch/ports", &ports) == nil {
			doc["switch_ports"] = ports
		}
		if get("/devices/"+s+"/switch/ports/statuses", &statuses) == nil {
			doc["port_statuses"] = statuses
		}
	case strings.HasPrefix(model, "MX") || strings.HasPrefix(model, "Z"):
		for k, p := range map[string]string{"vlans": "/networks/" + net + "/appliance/vlans", "l3_firewall": "/networks/" + net + "/appliance/firewall/l3FirewallRules", "ports": "/networks/" + net + "/appliance/ports"} {
			var v any
			if get(p, &v) == nil {
				doc[k] = v
			}
		}
	case strings.HasPrefix(model, "MR") || strings.HasPrefix(model, "CW"):
		var v any
		if get("/networks/"+net+"/wireless/ssids", &v) == nil {
			doc["ssids"] = v
		}
	}
	text, _ := json.MarshalIndent(doc, "", "  ")
	return &cfgResult{Source: "api", Vendor: "meraki", Text: string(text), Meraki: doc}, nil
}

// merakiPorts turns the Dashboard's switch ports and their statuses into the common ports table.
func merakiPorts(doc map[string]any) ([]map[string]any, map[string]any) {
	ports, _ := doc["switch_ports"].([]jobj)
	sts, _ := doc["port_statuses"].([]jobj)
	stat := map[string]jobj{}
	for _, s := range sts {
		stat[jstr(s, "portId")] = s
	}
	var out []map[string]any
	total, used, free, off := 0, 0, 0, 0
	speeds := map[string]int{}
	for _, p := range ports {
		id := jstr(p, "portId")
		st := stat[id]
		state := "Not connected"
		switch {
		case jstr(p, "enabled") == "false":
			state = "Disabled"
			off++
		case strings.EqualFold(jstr(st, "status"), "Connected"):
			state = "Connected"
			used++
			if sp := jstr(st, "speed"); sp != "" {
				speeds[sp]++
			}
		default:
			free++
		}
		total++
		row := map[string]any{"index": id, "name": "Port " + id, "description": jstr(p, "name"), "kind": "Ethernet", "state": state, "vlan": jstr(p, "vlan")}
		if sp := jstr(st, "speed"); sp != "" && state == "Connected" {
			row["speed"] = sp
		}
		out = append(out, row)
	}
	sort.SliceStable(out, func(i, j int) bool { return natLess(fmt.Sprint(out[i]["index"]), fmt.Sprint(out[j]["index"])) })
	return out, map[string]any{"total": total, "connected": used, "free": free, "disabled": off, "speeds": speeds}
}

// readNetwork reads a firewall, switch or access point: SNMP for the hardware and ports, then the configuration.
func readNetwork(ctx context.Context, creds []config.Cred, target, name string) (map[string]any, error) {
	var inv map[string]any
	var snmpErr error = errors.New("no SNMP community is saved for this customer")
	for _, c := range pick(creds, "snmp", hostOnly(target)) {
		i, err := SNMPInventory(hostOnly(target), c.Community, name)
		if err == nil {
			inv, snmpErr = i, nil
			break
		}
		snmpErr = err
	}
	maker, serial := "", ""
	if inv != nil {
		maker, _ = inv["make"].(string)
		serial, _ = inv["serial_number"].(string)
	}
	cfg, cfgErr := fetchConfig(ctx, creds, target, maker, serial)
	if inv == nil && (cfg == nil || cfg.Meraki == nil) {
		if cfgErr != nil {
			return nil, fmt.Errorf("SNMP: %v; configuration: %v", snmpErr, cfgErr)
		}
		return nil, fmt.Errorf("SNMP: %v", snmpErr)
	}
	if inv == nil { // a Meraki device read only through the Dashboard
		d := cfg.Meraki["device"].(jobj)
		inv = map[string]any{"schema": 1, "hostname": firstNonEmpty(jstr(d, "name"), name), "make": "Cisco Meraki", "model": jstr(d, "model"), "serial_number": jstr(d, "serial"),
			"os_version": jstr(d, "firmware"), "nics": []map[string]any{{"name": "LAN", "mac": jstr(d, "mac"), "connected": true, "ips": nonEmpty(jstr(d, "lanIp"))}}}
	}
	if cfg != nil && cfg.Meraki != nil {
		if ports, sum := merakiPorts(cfg.Meraki); len(ports) > 0 {
			x, _ := inv["hardware_extra"].(map[string]any)
			if x == nil {
				x = map[string]any{"platform": "network", "api": "Cisco Meraki Dashboard"}
				inv["hardware_extra"] = x
			}
			x["ports"], x["port_summary"] = ports, sum
		}
	}
	if cfg != nil {
		inv["config"] = map[string]any{"source": cfg.Source, "vendor": cfg.Vendor, "text": cfg.Text}
	} else if cfgErr != nil {
		inv["config_error"] = cfgErr.Error()
	}
	return inv, nil
}
