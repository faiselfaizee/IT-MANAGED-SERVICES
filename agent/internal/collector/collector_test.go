package collector

import (
	"context"
	"encoding/json"
	"net"
	"net/http"
	"net/http/httptest"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/deskask/platform/agent/internal/config"
)

func TestParsePingNeedsARealReply(t *testing.T) {
	up, ms := ParsePing("64 bytes from 10.0.0.1: icmp_seq=1 ttl=64 time=0.45 ms")
	if !up || ms != 0.45 {
		t.Fatal(up, ms)
	}
	if up, ms := ParsePing("Reply from 10.0.0.9: bytes=32 time<1ms TTL=128"); !up || ms != 1 {
		t.Fatal(up, ms)
	}
	// Windows exits with 0 when a router says the host is unreachable; that is not an answer from the device
	if up, _ := ParsePing("Reply from 10.0.0.1: Destination host unreachable."); up {
		t.Fatal("unreachable must not count as up")
	}
	if up, _ := ParsePing("Request timed out."); up {
		t.Fatal("timeout")
	}
}

func TestExpandKeepsToTheRulesTheServerHas(t *testing.T) {
	l, err := Expand("192.168.1.0/30")
	if err != nil || len(l) != 2 || l[0] != "192.168.1.1" || l[1] != "192.168.1.2" {
		t.Fatalf("%v %v", l, err)
	}
	if l, _ := Expand("10.0.0.0/22"); len(l) != 1022 {
		t.Fatalf("a /22 has 1022 hosts, got %d", len(l))
	}
	for _, bad := range []string{"10.0.0.0/21", "169.254.1.0/24", "224.0.0.0/24", "nonsense", "::1/128", "127.0.0.0/24"} {
		if _, err := Expand(bad); err == nil {
			t.Fatalf("%s should be refused", bad)
		}
	}
	if !Contains("10.1.0.0/16", "10.1.4.5") || Contains("10.1.0.0/16", "10.2.0.1") || !Contains("", "1.2.3.4") {
		t.Fatal("Contains")
	}
}

func TestParseARPFindsTheMac(t *testing.T) {
	linux := "IP address       HW type     Flags       HW address            Mask     Device\n10.0.0.7   0x1   0x2   a0:b1:c2:d3:e4:f5   *   eth0\n10.0.0.8   0x1   0x0   00:00:00:00:00:00   *   eth0\n"
	if m := ParseARP(linux, "10.0.0.7"); m != "A0:B1:C2:D3:E4:F5" {
		t.Fatal(m)
	}
	if m := ParseARP(linux, "10.0.0.8"); m != "" {
		t.Fatal("an incomplete entry has no MAC")
	}
	win := "  Internet Address      Physical Address      Type\n  10.0.0.20             a0-b1-c2-d3-e4-f5     dynamic\n"
	if m := ParseARP(win, "10.0.0.20"); m != "A0:B1:C2:D3:E4:F5" {
		t.Fatal(m)
	}
	if m := ParseARP("? (10.0.0.30) at aa:bb:cc:dd:ee:ff [ether] on eth0", "10.0.0.30"); m != "AA:BB:CC:DD:EE:FF" {
		t.Fatal(m)
	}
}

func listener(t *testing.T) (port int, stop func()) {
	l, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	go func() {
		for {
			c, err := l.Accept()
			if err != nil {
				return
			}
			c.Close()
		}
	}()
	return l.Addr().(*net.TCPAddr).Port, func() { l.Close() }
}

func TestServiceChecks(t *testing.T) {
	port, stop := listener(t)
	defer stop()
	ctx := context.Background()
	if r := RunCheck(ctx, CheckDef{ID: 1, Type: "tcp", Host: "127.0.0.1", Port: port, Timeout: 3}); !r.OK || r.ID != 1 {
		t.Fatalf("%+v", r)
	}
	stop()
	if r := RunCheck(ctx, CheckDef{Type: "tcp", Host: "127.0.0.1", Port: port, Timeout: 2}); r.OK {
		t.Fatalf("closed port must fail: %+v", r)
	}

	web := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/gone" {
			w.WriteHeader(404)
			return
		}
		_, _ = w.Write([]byte("welcome to the portal"))
	}))
	defer web.Close()
	if r := RunCheck(ctx, CheckDef{Type: "http", URL: web.URL, Expect: "portal"}); !r.OK {
		t.Fatalf("%+v", r)
	}
	if r := RunCheck(ctx, CheckDef{Type: "http", URL: web.URL, Expect: "nothing like this"}); r.OK || !strings.Contains(r.Detail, "expected text") {
		t.Fatalf("%+v", r)
	}
	if r := RunCheck(ctx, CheckDef{Type: "http", URL: web.URL + "/gone"}); r.OK || r.Detail != "HTTP 404" {
		t.Fatalf("%+v", r)
	}
	if r := RunCheck(ctx, CheckDef{Type: "http", URL: web.URL + "/gone", Status: "404"}); !r.OK {
		t.Fatalf("an expected 404 is a pass: %+v", r)
	}

	tlsSrv := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {}))
	defer tlsSrv.Close()
	host, p, _ := net.SplitHostPort(strings.TrimPrefix(tlsSrv.URL, "https://"))
	pn, _ := strconv.Atoi(p)
	r := RunCheck(ctx, CheckDef{Type: "ssl", Host: host, Port: pn, Insec: true})
	if !r.OK || r.Days == nil || *r.Days < 300 {
		t.Fatalf("%+v", r)
	}
	if r := RunCheck(ctx, CheckDef{Type: "ssl", Host: host, Port: pn}); r.OK || r.Days == nil {
		t.Fatalf("an untrusted certificate fails the check but still reports its days: %+v", r)
	}
	if r := RunCheck(ctx, CheckDef{Type: "dns", Host: "localhost"}); !r.OK {
		t.Fatalf("%+v", r)
	}
	if r := RunCheck(ctx, CheckDef{Type: "weird"}); r.OK {
		t.Fatal("unknown type")
	}
}

func TestScanFindsADeviceWithAnOpenPort(t *testing.T) {
	port, stop := listener(t)
	defer stop()
	old := ScanPorts
	ScanPorts = []int{port}
	defer func() { ScanPorts = old }()
	found := scanAddrs(context.Background(), []string{"127.0.0.1"}, 4)
	if len(found) != 1 || found[0].IP != "127.0.0.1" || len(found[0].Open) != 1 || found[0].Open[0] != port {
		t.Fatalf("%+v", found)
	}
}

// the platform side of a collector check-in
type fakeServer struct {
	mu     sync.Mutex
	bodies []map[string]any
	work   string
}

func (f *fakeServer) handler() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("/collector/v1/checkin", func(w http.ResponseWriter, r *http.Request) {
		if r.Header.Get("Authorization") != "Bearer dkco_secretsecretsecretsecret" || r.Header.Get("X-Collector-Id") != "cAAA" {
			w.WriteHeader(401)
			return
		}
		var b map[string]any
		_ = json.NewDecoder(r.Body).Decode(&b)
		f.mu.Lock()
		f.bodies = append(f.bodies, b)
		f.mu.Unlock()
		_, _ = w.Write([]byte(f.work))
	})
	mux.HandleFunc("/collector/v1/agent-token", func(w http.ResponseWriter, r *http.Request) { _, _ = w.Write([]byte(`{"token":"dkat_test"}`)) })
	return mux
}

func TestACollectorChecksDevicesAndReportsOnTheNextCheckIn(t *testing.T) {
	port, stop := listener(t)
	defer stop()
	old := HealthPorts
	HealthPorts = []int{port} // this computer may have no ping program; the open port proves the device is up
	defer func() { HealthPorts = old }()
	f := &fakeServer{}
	f.work = `{"pollSeconds":60,"assets":[{"id":"7","ip":"127.0.0.1","host":"gate","name":"Gate","monitor":"device"}],
	 "checks":{"7":[{"id":3,"name":"Web","type":"tcp","host":"127.0.0.1","port":` + strconv.Itoa(port) + `,"timeoutS":3}]},"deploys":[],"scan":{"cidrs":[],"now":false}}`
	srv := httptest.NewServer(f.handler())
	defer srv.Close()
	r := NewRunner(config.Collector{Server: srv.URL, CollectorID: "cAAA", Key: "dkco_secretsecretsecretsecret"}, "1.0.0")
	ctx := context.Background()
	w, err := r.CheckIn(ctx)
	if err != nil || len(w.Assets) != 1 {
		t.Fatalf("%v %+v", err, w)
	}
	r.DoWork(ctx, w)
	if _, err := r.CheckIn(ctx); err != nil {
		t.Fatal(err)
	}
	f.mu.Lock()
	defer f.mu.Unlock()
	res := f.bodies[1]["results"].(map[string]any)
	h := res["health"].([]any)[0].(map[string]any)
	c := res["checks"].([]any)[0].(map[string]any)
	if h["asset"] != "7" || h["up"] != true || c["id"].(float64) != 3 || c["asset"] != "7" || c["ok"] != true {
		t.Fatalf("%v", res)
	}
	if f.bodies[1]["platform"] == "" || f.bodies[1]["version"] != "1.0.0" {
		t.Fatalf("%v", f.bodies[1])
	}
}

func TestAFailedCheckInKeepsTheResultsForNextTime(t *testing.T) {
	r := NewRunner(config.Collector{Server: "http://127.0.0.1:1", CollectorID: "cAAA", Key: "k"}, "1")
	r.pending.Results.Health = []healthRes{{"1", true, 2}}
	r.pending.Log = []logLine{{"info", "hello"}}
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	if _, err := r.CheckIn(ctx); err == nil {
		t.Fatal("no server")
	}
	if len(r.pending.Results.Health) != 1 || len(r.pending.Log) != 1 {
		t.Fatalf("%+v", r.pending)
	}
}

func TestAScanResultIsSentEvenWhenItFoundNothing(t *testing.T) {
	f := &fakeServer{work: `{"pollSeconds":60,"assets":[],"checks":{},"deploys":[],"scan":{"cidrs":[],"now":false}}`}
	srv := httptest.NewServer(f.handler())
	defer srv.Close()
	r := NewRunner(config.Collector{Server: srv.URL, CollectorID: "cAAA", Key: "dkco_secretsecretsecretsecret"}, "1")
	r.mu.Lock()
	r.pending.Found = []Found{}
	r.hasScan = true
	r.mu.Unlock()
	if _, err := r.CheckIn(context.Background()); err != nil {
		t.Fatal(err)
	}
	f.mu.Lock()
	defer f.mu.Unlock()
	if arr, ok := f.bodies[0]["found"].([]any); !ok || len(arr) != 0 {
		t.Fatalf("the platform only ends a scan request when it gets a found list, even an empty one: %v", f.bodies[0]["found"])
	}
}

func TestCleanCLIXMLAndHint(t *testing.T) {
	raw := `#< CLIXML
<Objs Version="1.1.0.1" xmlns="http://schemas.microsoft.com/powershell/2004/04"><S S="Error">[host.example.in] Connecting to remote server host.example.in failed with the following error message : WinRM cannot complete the operation._x000D__x000A_</S><S S="Error">At line:1 &lt;x&gt;_x000D__x000A_</S></Objs>`
	got := WinHint(CleanCLIXML(raw))
	if strings.Contains(got, "CLIXML") || strings.Contains(got, "_x000D_") || strings.Contains(got, "&lt;") {
		t.Fatalf("not cleaned: %s", got)
	}
	if !strings.Contains(got, "Enable-PSRemoting") || !strings.Contains(got, "WinRM cannot complete the operation.") {
		t.Fatalf("no readable message or hint: %s", got)
	}
	if !strings.Contains(WinHint("Access is denied."), "Assets > Credentials") {
		t.Fatal("no hint for a refused login")
	}
}

func TestCredsForUsesPlatformLogins(t *testing.T) {
	t.Setenv("DESKASK_CONFIG_DIR", t.TempDir())
	r := &Runner{Cfg: config.Collector{Creds: []config.Cred{{Name: "local", Kind: "windows", User: "a"}}}}
	got := r.credsFor([]login{{Name: "w", Kind: "windows", User: `D\x`, Secret: "pw"}, {Name: "k", Kind: "ssh", User: "root", Secret: "-----BEGIN OPENSSH PRIVATE KEY-----\nx\n"}, {Name: "s", Kind: "snmp", Secret: "public"}, {Name: "p", Kind: "sshpw", User: "u", Secret: "pw2"}})
	if len(got) != 5 || got[1].Password != "pw" || got[2].KeyFile == "" || got[3].Community != "public" || got[4].Password != "pw2" {
		t.Fatalf("%+v", got)
	}
}
