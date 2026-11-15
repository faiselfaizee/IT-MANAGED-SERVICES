package collector

import (
	"context"
	"errors"
	"fmt"
	"log"
	"net"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/deskask/platform/agent/internal/snmp"
)

// eventRes is one syslog message or SNMP trap a device sent to the site controller.
type eventRes struct {
	IP       string `json:"ip"`
	Kind     string `json:"kind"`     // syslog or trap
	Severity int    `json:"severity"` // syslog scale: 0 emergency ... 7 debug
	Source   string `json:"source"`   // program (syslog) or trap name
	Text     string `json:"text"`
	At       string `json:"at"`
}

// Listener receives syslog (UDP 514) and SNMP traps (UDP 162) and queues them for the next check-in. A source that sends more
// than PerSourcePerMinute messages a minute is cut off for the rest of that minute (one line says how many were dropped).
type Listener struct {
	SyslogAddr, TrapAddr string
	PerSourcePerMinute   int
	Add                  func(eventRes)

	mu      sync.Mutex
	counts  map[string]int
	dropped map[string]int
	minute  int64
	bound   map[string]string // kind -> address actually listening on (tests listen on port 0)
}

// Start opens the two ports. A port that cannot be opened (in use, or no permission) is logged and skipped.
func (l *Listener) Start(ctx context.Context) {
	if l.PerSourcePerMinute == 0 {
		l.PerSourcePerMinute = 120
	}
	l.bound = map[string]string{}
	for kind, addr := range map[string]string{"syslog": l.SyslogAddr, "trap": l.TrapAddr} {
		if addr == "" {
			continue
		}
		pc, err := net.ListenPacket("udp", addr)
		if err != nil {
			log.Printf("%s receiver not started on %s: %v", kind, addr, err)
			continue
		}
		l.mu.Lock()
		l.bound[kind] = pc.LocalAddr().String()
		l.mu.Unlock()
		go func() { <-ctx.Done(); _ = pc.Close() }()
		go l.serve(pc, kind)
	}
}

// Bound is the address a receiver listens on, or "" when it is not running.
func (l *Listener) Bound(kind string) string { l.mu.Lock(); defer l.mu.Unlock(); return l.bound[kind] }

func (l *Listener) serve(pc net.PacketConn, kind string) {
	buf := make([]byte, 65535)
	for {
		n, from, err := pc.ReadFrom(buf)
		if err != nil {
			if errors.Is(err, net.ErrClosed) {
				return
			}
			continue
		}
		ip := from.String()
		if h, _, e := net.SplitHostPort(ip); e == nil {
			ip = h
		}
		if !l.allow(ip) {
			continue
		}
		msg := append([]byte(nil), buf[:n]...)
		var ev eventRes
		if kind == "syslog" {
			sev, src, text := parseSyslog(string(msg))
			ev = eventRes{IP: ip, Kind: "syslog", Severity: sev, Source: src, Text: text}
		} else {
			t, err := snmp.ParseTrap(msg)
			if err != nil {
				continue
			}
			ev = trapEvent(ip, t)
		}
		ev.At = time.Now().UTC().Format(time.RFC3339)
		if l.Add != nil {
			l.Add(ev)
		}
	}
}

func (l *Listener) allow(ip string) bool {
	l.mu.Lock()
	defer l.mu.Unlock()
	m := time.Now().Unix() / 60
	if m != l.minute {
		for src, n := range l.dropped {
			if l.Add != nil {
				go l.Add(eventRes{IP: src, Kind: "syslog", Severity: 4, Source: "site controller", Text: fmt.Sprintf("%d more messages from this device were dropped in one minute (limit %d a minute).", n, l.PerSourcePerMinute), At: time.Now().UTC().Format(time.RFC3339)})
			}
		}
		l.minute, l.counts, l.dropped = m, map[string]int{}, map[string]int{}
	}
	l.counts[ip]++
	if l.counts[ip] > l.PerSourcePerMinute {
		l.dropped[ip]++
		return false
	}
	return true
}

var (
	rfc3164 = regexp.MustCompile(`^[A-Z][a-z]{2} [ 0-9]\d \d\d:\d\d:\d\d (\S+) (.*)$`)
	tagRe   = regexp.MustCompile(`^([A-Za-z0-9_.\-/]+)(\[\d+\])?: ?(.*)$`)
)

// parseSyslog reads RFC 3164 and RFC 5424 messages (and the key=value lines some firewalls send). Severity is the low three bits of <PRI>.
func parseSyslog(s string) (sev int, source, text string) {
	sev = 5 // notice, when there is no <PRI>
	s = strings.TrimRight(s, "\r\n\x00")
	if strings.HasPrefix(s, "<") {
		if i := strings.IndexByte(s, '>'); i > 1 && i <= 4 {
			if pri, err := strconv.Atoi(s[1:i]); err == nil {
				sev, s = pri&7, s[i+1:]
			}
		}
	}
	if strings.HasPrefix(s, "1 ") { // RFC 5424: VERSION TIMESTAMP HOST APP PROCID MSGID SD MSG
		f := strings.SplitN(s, " ", 7)
		if len(f) == 7 {
			source = dash(f[3])
			rest := f[6]
			if strings.HasPrefix(rest, "[") { // structured data
				depth := 0
				for i, c := range rest {
					if c == '[' {
						depth++
					} else if c == ']' {
						depth--
						if depth == 0 && (i+1 >= len(rest) || rest[i+1] == ' ') {
							rest = strings.TrimPrefix(rest[i+1:], " ")
							break
						}
					}
				}
			} else {
				rest = strings.TrimPrefix(strings.TrimPrefix(rest, "-"), " ")
			}
			return sev, source, clipText(strings.TrimPrefix(rest, "\ufeff"))
		}
	}
	if m := rfc3164.FindStringSubmatch(s); m != nil {
		s = m[2]
	}
	if m := tagRe.FindStringSubmatch(s); m != nil && len(m[1]) <= 48 {
		return sev, m[1], clipText(m[3])
	}
	return sev, "", clipText(s)
}

func dash(s string) string {
	if s == "-" {
		return ""
	}
	return s
}

func clipText(s string) string {
	s = strings.TrimSpace(s)
	if len(s) > 1000 {
		return s[:1000]
	}
	return s
}

var trapSeverity = map[string]int{"linkDown": 3, "authenticationFailure": 4, "coldStart": 5, "warmStart": 5, "linkUp": 6}

func trapEvent(ip string, t *snmp.Trap) eventRes {
	name := t.Name
	if name == "" {
		name = t.OID
	}
	parts := []string{}
	for i, v := range t.Vars {
		if i >= 8 {
			break
		}
		if x := v.Value.Text(); x != "" {
			parts = append(parts, v.OID+" = "+x)
		}
	}
	sev, ok := trapSeverity[t.Name]
	if !ok {
		sev = 4
	}
	text := name
	if len(parts) > 0 {
		text += ": " + strings.Join(parts, "; ")
	}
	return eventRes{IP: ip, Kind: "trap", Severity: sev, Source: name, Text: clipText(text)}
}
