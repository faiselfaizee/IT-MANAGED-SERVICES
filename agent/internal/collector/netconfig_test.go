package collector

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"

	"github.com/deskask/platform/agent/internal/config"
)

func TestFamilyFromMakerOrLoginVendor(t *testing.T) {
	for _, c := range []struct{ maker, vendor, want string }{
		{"Cisco", "", "cisco"}, {"Cisco Meraki", "", "meraki"}, {"", "Cisco Meraki", "meraki"}, {"Fortinet", "", "fortinet"}, {"", "FortiGate 60F", "fortinet"},
		{"HP", "", "aruba"}, {"Aruba", "", "aruba"}, {"Palo Alto Networks", "", "paloalto"}, {"SonicWall", "", "sonicwall"}, {"Ubiquiti", "", "ubiquiti"},
		{"Sophos", "", "sophos"}, {"", "", ""},
	} {
		if got := family(c.maker, c.vendor); got != c.want {
			t.Errorf("family(%q, %q) = %q, want %q", c.maker, c.vendor, got, c.want)
		}
	}
	if !strings.Contains(sshCommands["cisco"], "terminal length 0\nshow running-config") || !strings.Contains(sshCommands["fortinet"], "set output standard") {
		t.Fatal("command sets changed")
	}
	if got := cleanConfig("\x1b[2Jhostname SW1\r\n --More-- \x08\x08\x08interface Gi1/0/1\r\n"); got != "hostname SW1\n  interface Gi1/0/1" {
		t.Fatalf("clean = %q", got)
	}
}

func TestFortiGateBackupFallsBackToPost(t *testing.T) {
	var methods []string
	srv := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, q *http.Request) {
		methods = append(methods, q.Method)
		if q.Header.Get("Authorization") != "Bearer tok" {
			w.WriteHeader(401)
			return
		}
		if q.URL.Path != "/api/v2/monitor/system/config/backup" || q.URL.Query().Get("scope") != "global" {
			w.WriteHeader(404)
			return
		}
		if q.Method == "GET" {
			w.WriteHeader(405)
			return
		}
		fmt.Fprint(w, "#config-version=FGT60F-7.4.3\nconfig system global\n    set hostname \"FGT-HQ\"\nend\n")
	}))
	defer srv.Close()
	host := strings.TrimPrefix(srv.URL, "https://")
	r, err := fortiBackup(context.Background(), host, "tok")
	if err != nil || r.Source != "api" || !strings.Contains(r.Text, `set hostname "FGT-HQ"`) || strings.Join(methods, ",") != "GET,POST" {
		t.Fatalf("%v %v %v", r, err, methods)
	}
	if _, err := fortiBackup(context.Background(), host, "bad"); err == nil || !strings.Contains(err.Error(), "refused") {
		t.Fatalf("bad token: %v", err)
	}
	// through fetchConfig, chosen by the vendor written on the API login
	creds := []config.Cred{{Name: "FGT token", Kind: "netapi", Password: "tok", Vendor: "Fortinet"}}
	if r, err := fetchConfig(context.Background(), creds, host, "", ""); err != nil || r.Vendor != "fortinet" {
		t.Fatalf("fetchConfig: %v %v", r, err)
	}
}

func TestPaloAltoKeygenAndExport(t *testing.T) {
	srv := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, q *http.Request) {
		v := q.URL.Query()
		switch v.Get("type") {
		case "keygen":
			if v.Get("user") != "audit" || v.Get("password") != "pw" {
				w.WriteHeader(403)
				fmt.Fprint(w, `<response status="error"><result><msg>Invalid credentials.</msg></result></response>`)
				return
			}
			fmt.Fprint(w, `<response status="success"><result><key>LUFRPT1</key></result></response>`)
		case "export":
			if v.Get("key") != "LUFRPT1" {
				w.WriteHeader(403)
				return
			}
			fmt.Fprint(w, `<?xml version="1.0"?><config version="11.0.0"><devices><entry name="localhost.localdomain"/></devices></config>`)
		}
	}))
	defer srv.Close()
	host := strings.TrimPrefix(srv.URL, "https://")
	r, err := panExport(context.Background(), host, "audit", "pw", "")
	if err != nil || !strings.Contains(r.Text, `<config version="11.0.0">`) {
		t.Fatalf("%v %v", r, err)
	}
	if _, err := panExport(context.Background(), host, "audit", "nope", ""); err == nil {
		t.Fatal("a wrong password must fail")
	}
	if r, err := panExport(context.Background(), host, "", "", "LUFRPT1"); err != nil || r.Vendor != "paloalto" {
		t.Fatalf("with key: %v %v", r, err)
	}
}

func TestMerakiSwitchFoundByAddressWithItsPorts(t *testing.T) {
	routes := map[string]any{
		"/organizations":                                []any{map[string]any{"id": "o1", "name": "Acme"}},
		"/organizations/o1/devices":                     []any{map[string]any{"serial": "Q2HP-AAAA-BBBB", "model": "MS120-8LP", "name": "SW-Lobby", "networkId": "N1", "lanIp": "10.0.0.30", "firmware": "switch-15-21", "mac": "e0:55:3d:00:00:01"}},
		"/devices/Q2HP-AAAA-BBBB/switch/ports":          []any{map[string]any{"portId": "1", "name": "Uplink", "enabled": true, "vlan": 1}, map[string]any{"portId": "2", "enabled": true}, map[string]any{"portId": "10", "enabled": false}},
		"/devices/Q2HP-AAAA-BBBB/switch/ports/statuses": []any{map[string]any{"portId": "1", "status": "Connected", "speed": "1 Gbps"}, map[string]any{"portId": "2", "status": "Disconnected"}},
	}
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, q *http.Request) {
		if q.Header.Get("Authorization") != "Bearer mk" {
			w.WriteHeader(401)
			return
		}
		v, ok := routes[q.URL.Path]
		if !ok {
			w.WriteHeader(404)
			return
		}
		_ = json.NewEncoder(w).Encode(v)
	}))
	defer srv.Close()
	r, err := merakiRead(context.Background(), srv.URL, "mk", "10.0.0.30", "")
	if err != nil || r.Vendor != "meraki" || !strings.Contains(r.Text, `"name": "SW-Lobby"`) {
		t.Fatalf("%v %v", r, err)
	}
	ports, sum := merakiPorts(r.Meraki)
	if len(ports) != 3 || ports[0]["speed"] != "1 Gbps" || ports[2]["state"] != "Disabled" || sum["connected"] != 1 || sum["free"] != 1 || sum["disabled"] != 1 {
		t.Fatalf("ports = %v %v", ports, sum)
	}
	if _, err := merakiRead(context.Background(), srv.URL, "mk", "10.9.9.9", ""); err == nil || !strings.Contains(err.Error(), "no Meraki device") {
		t.Fatalf("unknown address: %v", err)
	}
	if _, err := merakiRead(context.Background(), srv.URL, "bad", "10.0.0.30", ""); err == nil || !strings.Contains(err.Error(), "refused") {
		t.Fatalf("bad key: %v", err)
	}
}

func TestFetchConfigExplainsWhatIsMissing(t *testing.T) {
	if _, err := fetchConfig(context.Background(), nil, "10.0.0.1", "Cisco", ""); err == nil || !strings.Contains(err.Error(), "Assets > Credentials") {
		t.Fatalf("no login: %v", err)
	}
	if _, err := fetchConfig(context.Background(), []config.Cred{{Name: "fw", Kind: "netssh", User: "admin", Password: "x"}}, "10.0.0.1", "Sophos", ""); err == nil || !strings.Contains(err.Error(), "menu") {
		t.Fatalf("sophos: %v", err)
	}
	if _, err := fetchConfig(context.Background(), []config.Cred{{Name: "fw", Kind: "netssh", User: "admin", Password: "x"}}, "10.0.0.1", "", ""); err == nil || !strings.Contains(err.Error(), "write the vendor") {
		t.Fatalf("unknown maker: %v", err)
	}
}

// A stand-in ssh program that answers like a Cisco switch: it reads the typed commands and prints the configuration.
func TestSSHBackupTypesTheVendorsCommands(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("uses a shell script as ssh")
	}
	dir := t.TempDir()
	script := "#!/bin/sh\nin=$(cat)\ncase \"$in\" in *'terminal length 0'*'show running-config'*) printf 'SW1#show running-config\\r\\nBuilding configuration...\\r\\n'; i=0; while [ $i -lt 20 ]; do echo \"interface GigabitEthernet1/0/$i\"; i=$((i+1)); done; echo 'end';; *) echo 'Invalid input'; exit 1;; esac\n"
	if err := os.WriteFile(filepath.Join(dir, "ssh"), []byte(script), 0o755); err != nil {
		t.Fatal(err)
	}
	t.Setenv("PATH", dir+string(os.PathListSeparator)+os.Getenv("PATH"))
	t.Setenv("DESKASK_CONFIG_DIR", t.TempDir())
	r, err := fetchConfig(context.Background(), []config.Cred{{Name: "switch admin", Kind: "netssh", User: "admin", Password: "x"}}, "10.0.0.2", "Cisco", "")
	if err != nil {
		t.Fatal(err)
	}
	if r.Source != "ssh" || r.Vendor != "cisco" || !strings.Contains(r.Text, "interface GigabitEthernet1/0/19") || strings.Contains(r.Text, "\r") {
		t.Fatalf("%+v", r)
	}
	// the same device with the wrong commands answers too little: that is reported, not stored
	if _, err := fetchConfig(context.Background(), []config.Cred{{Name: "fw", Kind: "netssh", User: "admin", Password: "x", Vendor: "SonicWall"}}, "10.0.0.2", "", ""); err == nil {
		t.Fatal("an answer without a configuration must fail")
	}
}
