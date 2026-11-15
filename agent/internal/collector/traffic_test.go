package collector

import (
	"context"
	"net"
	"testing"
	"time"

	"github.com/deskask/platform/agent/internal/snmp"
)

func TestTrafficRatesComeFromTwoReadings(t *testing.T) {
	t0 := time.Date(2026, 11, 15, 10, 0, 0, 0, time.UTC)
	a := trafficReading{at: t0, ports: map[string]counters{
		"1": {in: 1_000_000, out: 0, hc: true, oper: 1, admin: 1, mbps: 1000, name: "Gi1/0/1", alias: "Uplink", kind: "Ethernet"},
		"2": {in: 4_294_967_000, out: 10, oper: 1, admin: 1, mbps: 100, name: "Fa0/2", kind: "Ethernet"}, // 32-bit, about to wrap
		"3": {oper: 2, admin: 2, mbps: 1000, name: "Gi1/0/3", kind: "Ethernet"},
	}}
	b := trafficReading{at: t0.Add(5 * time.Minute), ports: map[string]counters{
		"1": {in: 1_000_000 + 7_500_000_000, out: 3_750_000_000, inErr: 30, outErr: 0, inDisc: 15, hc: true, oper: 1, admin: 1, mbps: 1000, name: "Gi1/0/1", alias: "Uplink", kind: "Ethernet"},
		"2": {in: 704, out: 10, oper: 1, admin: 1, mbps: 100, name: "Fa0/2", kind: "Ethernet"},
		"3": {oper: 2, admin: 2, mbps: 1000, name: "Gi1/0/3", kind: "Ethernet"},
	}}
	first := rates(nil, a)
	if len(first) != 3 || first[0].Rates || first[0].Oper != "up" || first[2].Admin != "down" {
		t.Fatalf("first reading %+v", first)
	}
	p := rates(&a, b)
	// 7.5 GB in 300 s = 200 Mbit/s = 20% of 1 Gbit/s; out 100 Mbit/s = 10%
	if !p[0].Rates || p[0].InBps != 200_000_000 || p[0].InPct != 20 || p[0].OutPct != 10 || p[0].Errors != 6 || p[0].Discards != 3 {
		t.Fatalf("port 1 %+v", p[0])
	}
	// the 32-bit counter went past 2^32: 296 + 704 = 1000 bytes in 300 s
	if !p[1].Rates || p[1].InBps != 26.7 {
		t.Fatalf("wrapped port %+v", p[1])
	}
	// a 64-bit counter that goes back means the device restarted: no rate rather than a huge one
	c := b
	c.at = b.at.Add(5 * time.Minute)
	c.ports = map[string]counters{"1": {in: 10, out: 10, hc: true, oper: 1, mbps: 1000, name: "Gi1/0/1"}}
	if r := rates(&b, c); r[0].Rates {
		t.Fatalf("after restart %+v", r[0])
	}
}

func TestCountersAreReadFromASwitch(t *testing.T) {
	const ift, ifx = "1.3.6.1.2.1.2.2.1", "1.3.6.1.2.1.31.1.1.1"
	v := map[string]snmp.Value{"1.3.6.1.2.1.1.3.0": snmp.Ticks(100)}
	port := func(i, name string, typ, oper int64, in int64) {
		v[ift+".3."+i], v[ift+".2."+i], v[ift+".7."+i], v[ift+".8."+i], v[ift+".5."+i] = snmp.Int(typ), snmp.Str(name), snmp.Int(1), snmp.Int(oper), snmp.Gauge(1_000_000_000)
		v[ifx+".1."+i], v[ifx+".15."+i], v[ifx+".18."+i] = snmp.Str(name), snmp.Gauge(1000), snmp.Str("")
		v[ifx+".6."+i], v[ifx+".10."+i] = snmp.Counter64(in), snmp.Counter64(in/2)
		v[ift+".14."+i], v[ift+".20."+i], v[ift+".13."+i], v[ift+".19."+i] = snmp.Counter(3), snmp.Counter(0), snmp.Counter(1), snmp.Counter(0)
	}
	port("1", "Gi1/0/1", 6, 1, 5_000_000_000)
	port("2", "Gi1/0/2", 6, 2, 0)
	port("9", "Vlan10", 53, 1, 77)
	addr, stop := snmp.FakeDevice(v)
	defer stop()
	got, err := readCounters(&snmp.Client{Addr: addr, Community: "public", Timeout: time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if len(got) != 2 || got["1"].in != 5_000_000_000 || !got["1"].hc || got["1"].out != 2_500_000_000 || got["1"].inErr != 3 || got["2"].oper != 2 || got["1"].mbps != 1000 {
		t.Fatalf("%+v", got)
	}
}

func TestSyslogMessagesAreRead(t *testing.T) {
	cases := []struct {
		in, src, text string
		sev           int
	}{
		{"<11>Nov 15 10:01:02 core-sw1 %LINK-3-UPDOWN: Interface Gi1/0/7, changed state to down", "", "%LINK-3-UPDOWN: Interface Gi1/0/7, changed state to down", 3},
		{"<30>Nov  5 10:01:02 nas01 smartd[812]: Device: /dev/sda, SMART Failure", "smartd", "Device: /dev/sda, SMART Failure", 6},
		{`<165>1 2026-11-15T10:01:02.003Z fw01 sshd 4321 ID47 [exampleSDID@32473 iut="3"] Failed password for root`, "sshd", "Failed password for root", 5},
		{"<12>1 2026-11-15T10:01:02Z host app - - - plain text", "app", "plain text", 4},
		{`date=2026-11-15 time=10:01:02 devname="FG100F" type="event" level="warning" msg="Admin login failed"`, "", `date=2026-11-15 time=10:01:02 devname="FG100F" type="event" level="warning" msg="Admin login failed"`, 5},
	}
	for _, c := range cases {
		sev, src, text := parseSyslog(c.in)
		if sev != c.sev || src != c.src || text != c.text {
			t.Errorf("%q -> %d %q %q", c.in, sev, src, text)
		}
	}
}

func TestTheReceiverQueuesSyslogAndTrapsAndCutsOffAFlood(t *testing.T) {
	got := make(chan eventRes, 50)
	l := &Listener{SyslogAddr: "127.0.0.1:0", TrapAddr: "127.0.0.1:0", PerSourcePerMinute: 3, Add: func(e eventRes) { got <- e }}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	l.Start(ctx)
	send := func(addr string, b []byte) {
		c, err := net.Dial("udp", addr)
		if err != nil {
			t.Fatal(err)
		}
		_, _ = c.Write(b)
		_ = c.Close()
	}
	send(l.Bound("syslog"), []byte("<10>Nov 15 10:00:00 sw1 kernel: fan 2 failed"))
	trap, _ := snmp.EncodeTrapV2("public", "1.3.6.1.6.3.1.1.5.3", []snmp.Entry{{OID: "1.3.6.1.2.1.2.2.1.2.7", Value: snmp.Str("Gi1/0/7")}})
	send(l.Bound("trap"), trap)
	var evs []eventRes
	for len(evs) < 2 {
		select {
		case e := <-got:
			evs = append(evs, e)
		case <-time.After(3 * time.Second):
			t.Fatalf("got %+v", evs)
		}
	}
	byKind := map[string]eventRes{}
	for _, e := range evs {
		byKind[e.Kind] = e
	}
	if s := byKind["syslog"]; s.Severity != 2 || s.Source != "kernel" || s.Text != "fan 2 failed" || s.IP != "127.0.0.1" {
		t.Fatalf("syslog %+v", s)
	}
	if tr := byKind["trap"]; tr.Severity != 3 || tr.Source != "linkDown" || tr.Text != "linkDown: 1.3.6.1.2.1.2.2.1.2.7 = Gi1/0/7" {
		t.Fatalf("trap %+v", tr)
	}
	for i := 0; i < 5; i++ { // two more allowed this minute (3 per source), the rest dropped
		send(l.Bound("syslog"), []byte("<14>flood"))
	}
	time.Sleep(300 * time.Millisecond)
	if n := len(got); n != 1 {
		t.Fatalf("expected 1 more message within the limit, got %d", n)
	}
}
