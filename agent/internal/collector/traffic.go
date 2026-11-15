package collector

import (
	"context"
	"strings"
	"time"

	"github.com/deskask/platform/agent/internal/snmp"
)

// TrafficEvery is how often the traffic counters of network devices are read (rates are the difference between two readings).
const TrafficEvery = 5 * time.Minute

// portStat is one port's state and, from the second reading on, its traffic.
type portStat struct {
	Index    string  `json:"index"`
	Name     string  `json:"name"`
	Alias    string  `json:"alias"`
	Kind     string  `json:"kind"`
	Oper     string  `json:"oper"`  // up or down
	Admin    string  `json:"admin"` // up or down (switched off)
	Mbps     int64   `json:"speed_mbps"`
	Rates    bool    `json:"rates"` // false on the first reading (nothing to compare with yet)
	InBps    float64 `json:"in_bps"`
	OutBps   float64 `json:"out_bps"`
	InPct    float64 `json:"in_pct"`
	OutPct   float64 `json:"out_pct"`
	Errors   float64 `json:"errors"`   // per minute, in + out
	Discards float64 `json:"discards"` // per minute, in + out
}

type trafficRes struct {
	Asset string     `json:"asset"`
	Ports []portStat `json:"ports"`
}

// counters is one reading of one port.
type counters struct {
	in, out, inErr, outErr, inDisc, outDisc uint64
	hc                                      bool // 64-bit octet counters (no wrap to worry about)
	oper, admin                             int64
	mbps                                    int64
	name, alias, kind                       string
}

type trafficReading struct {
	at    time.Time
	ports map[string]counters
}

// readCounters reads the interface counters of a device. Ports that are not real ports (VLANs, loopbacks, tunnels) are left out.
func readCounters(c *snmp.Client) (map[string]counters, error) {
	const ift, ifx = "1.3.6.1.2.1.2.2.1", "1.3.6.1.2.1.31.1.1.1"
	if _, err := c.Get("1.3.6.1.2.1.1.3.0"); err != nil { // sysUpTime: is the device answering with this login at all
		return nil, err
	}
	typ, descr, name, alias := column(c, ift+".3", 2048), column(c, ift+".2", 2048), column(c, ifx+".1", 2048), column(c, ifx+".18", 2048)
	admin, oper, low, high := column(c, ift+".7", 2048), column(c, ift+".8", 2048), column(c, ift+".5", 2048), column(c, ifx+".15", 2048)
	hin, hout := column(c, ifx+".6", 2048), column(c, ifx+".10", 2048)
	var in32, out32 map[string]snmp.Value
	if len(hin) == 0 {
		in32, out32 = column(c, ift+".10", 2048), column(c, ift+".16", 2048)
	}
	inErr, outErr, inDisc, outDisc := column(c, ift+".14", 2048), column(c, ift+".20", 2048), column(c, ift+".13", 2048), column(c, ift+".19", 2048)
	out := map[string]counters{}
	for k, tv := range typ {
		kind, ok := portKinds[tv.Int]
		n := firstNonEmpty(vt(name[k]), vt(descr[k]))
		ln := strings.ToLower(n)
		if !ok || strings.HasPrefix(ln, "vlan") || strings.HasPrefix(ln, "loopback") || strings.HasPrefix(ln, "null") || strings.HasPrefix(ln, "tunnel") {
			continue
		}
		p := counters{oper: oper[k].Int, admin: admin[k].Int, name: n, alias: vt(alias[k]), kind: kind,
			inErr: uint64(inErr[k].Int), outErr: uint64(outErr[k].Int), inDisc: uint64(inDisc[k].Int), outDisc: uint64(outDisc[k].Int)}
		p.mbps = high[k].Int
		if p.mbps == 0 {
			p.mbps = low[k].Int / 1_000_000
		}
		if v, ok := hin[k]; ok && !v.Missing() {
			p.in, p.out, p.hc = uint64(v.Int), uint64(hout[k].Int), true
		} else {
			p.in, p.out = uint64(in32[k].Int), uint64(out32[k].Int)
		}
		out[k] = p
	}
	return out, nil
}

// delta is how much a counter grew; a 32-bit counter that went past its top starts again from 0, a counter that went back
// otherwise (the device restarted) gives no rate.
func delta(prev, cur uint64, hc bool) (uint64, bool) {
	if cur >= prev {
		return cur - prev, true
	}
	if !hc && prev <= 1<<32 {
		return (1<<32 - prev) + cur, true
	}
	return 0, false
}

// rates compares two readings. Without a previous reading (or after a restart) the ports come back with their state only.
func rates(prev *trafficReading, cur trafficReading) []portStat {
	keys := sortedKeys(cur.ports)
	out := make([]portStat, 0, len(keys))
	for _, k := range keys {
		c := cur.ports[k]
		p := portStat{Index: k, Name: c.name, Alias: c.alias, Kind: c.kind, Oper: map[bool]string{true: "up", false: "down"}[c.oper == 1],
			Admin: map[bool]string{true: "down", false: "up"}[c.admin == 2], Mbps: c.mbps}
		if prev != nil {
			if o, ok := prev.ports[k]; ok {
				secs := cur.at.Sub(prev.at).Seconds()
				di, ok1 := delta(o.in, c.in, c.hc)
				do, ok2 := delta(o.out, c.out, c.hc)
				if secs >= 1 && ok1 && ok2 {
					p.Rates = true
					p.InBps, p.OutBps = round1(float64(di)*8/secs), round1(float64(do)*8/secs)
					if c.mbps > 0 {
						p.InPct, p.OutPct = round1(min(100, p.InBps/float64(c.mbps)/1e4)), round1(min(100, p.OutBps/float64(c.mbps)/1e4))
					}
					e1, _ := delta(o.inErr, c.inErr, false)
					e2, _ := delta(o.outErr, c.outErr, false)
					d1, _ := delta(o.inDisc, c.inDisc, false)
					d2, _ := delta(o.outDisc, c.outDisc, false)
					p.Errors, p.Discards = round1(float64(e1+e2)*60/secs), round1(float64(d1+d2)*60/secs)
				}
			}
		}
		out = append(out, p)
	}
	return out
}

func round1(f float64) float64 { return float64(int64(f*10+0.5)) / 10 }

// readTraffic reads a network device's counters with the first SNMP login that works and queues the rates.
func (r *Runner) readTraffic(ctx context.Context, a asset, target string) {
	var cur map[string]counters
	var err error
	for _, cr := range pick(r.credsFor(a.Logins), "snmp", target) {
		if cur, err = readCounters(snmpClient(target, cr)); err == nil {
			break
		}
	}
	if cur == nil {
		return // the inventory read reports a login problem; traffic says nothing more
	}
	reading := trafficReading{at: time.Now(), ports: cur}
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.traffic == nil {
		r.traffic = map[string]*trafficReading{}
	}
	ports := rates(r.traffic[a.ID], reading)
	r.traffic[a.ID] = &reading
	r.pending.Results.Traffic = append(r.pending.Results.Traffic, trafficRes{Asset: a.ID, Ports: ports})
}
