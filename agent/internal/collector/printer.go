package collector

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net"
	"sort"
	"strconv"
	"strings"
	"time"

	"github.com/deskask/platform/agent/internal/config"
	"github.com/deskask/platform/agent/internal/snmp"
)

// PrinterEvery is how often a printer's status, supplies and page count are read.
const PrinterEvery = 15 * time.Minute

// The standard printer MIB (RFC 3805) and host resources MIB (RFC 2790): the same numbers answer on HP, Canon, Epson, Brother, Xerox, Ricoh,
// Kyocera, Konica Minolta, Samsung, Lexmark, Sharp, Pantum and most others.
const (
	oidHrPrinterStatus   = "1.3.6.1.2.1.25.3.5.1.1"
	oidHrPrinterErrors   = "1.3.6.1.2.1.25.3.5.1.2"
	oidHrDeviceDescr     = "1.3.6.1.2.1.25.3.2.1.3"
	oidPrtGeneralName    = "1.3.6.1.2.1.43.5.1.1.16"
	oidPrtGeneralSerial  = "1.3.6.1.2.1.43.5.1.1.17"
	oidPrtGeneralReset   = "1.3.6.1.2.1.43.5.1.1.3.1"
	oidPrtInput          = "1.3.6.1.2.1.43.8.2.1"
	oidPrtMarkerLife     = "1.3.6.1.2.1.43.10.2.1.4"
	oidPrtMarkerUnit     = "1.3.6.1.2.1.43.10.2.1.3"
	oidPrtSupplies       = "1.3.6.1.2.1.43.11.1.1"
	oidPrtColorantValue  = "1.3.6.1.2.1.43.12.1.1.4"
	oidPrtConsoleDisplay = "1.3.6.1.2.1.43.16.5.1.2"
	oidPrtAlert          = "1.3.6.1.2.1.43.18.1.1"
)

// prtGeneralReset values (RFC 3805): 4 powerCycleReset restarts the printer; 5 and 6 would also wipe settings and are never sent.
const resetPowerCycle = 4

type supply struct {
	Name  string  `json:"name"`
	Kind  string  `json:"kind"`  // toner, ink, drum, fuser, waste, transfer, maintenance, other
	Color string  `json:"color"` // black, cyan, magenta, yellow, ... or ""
	Pct   float64 `json:"pct"`   // 0 to 100; -1 when the printer does not say
	State string  `json:"state"` // ok, low, empty, full (waste container), unknown
	Waste bool    `json:"waste"` // a receptacle that fills up (waste toner): full is the problem, not empty
}

type tray struct {
	Name  string  `json:"name"`
	Media string  `json:"media"`
	Pct   float64 `json:"pct"`
	State string  `json:"state"` // ok, low, empty, unknown
}

type printerAlert struct {
	Severity string `json:"severity"` // critical, warning, info
	Text     string `json:"text"`
}

// PrinterReading is what one read of a printer found. Pages is the printer's lifetime counter (-1 when it does not report one).
type PrinterReading struct {
	Status  string         `json:"status"` // idle, printing, warmup, other, unknown
	Flags   []string       `json:"flags,omitempty"`
	Message string         `json:"message,omitempty"`
	Name    string         `json:"name,omitempty"`
	Model   string         `json:"model,omitempty"`
	Serial  string         `json:"serial,omitempty"`
	Pages   int64          `json:"pages"`
	Supply  []supply       `json:"supplies"`
	Trays   []tray         `json:"trays"`
	Alerts  []printerAlert `json:"alerts"`
}

// errorFlags names the bits of hrPrinterDetectedErrorState: the first octet, then the second.
var errorFlags = [2][8]string{
	{"low_paper", "no_paper", "low_toner", "no_toner", "door_open", "jammed", "offline", "service_requested"},
	{"input_tray_missing", "output_tray_missing", "marker_supply_missing", "output_near_full", "output_full", "input_tray_empty", "overdue_maintenance", ""},
}

func decodeFlags(b []byte) []string {
	var out []string
	for i := 0; i < len(b) && i < 2; i++ {
		for bit := 0; bit < 8; bit++ {
			if b[i]&(0x80>>bit) != 0 && errorFlags[i][bit] != "" {
				out = append(out, errorFlags[i][bit])
			}
		}
	}
	return out
}

// supplyKinds maps prtMarkerSuppliesType to a short kind.
var supplyKinds = map[int64]string{
	3: "toner", 21: "toner", 5: "ink", 6: "ink", 7: "ink", 12: "ink", 13: "ink", 4: "waste", 8: "waste", 14: "waste", 24: "waste", 26: "waste",
	9: "drum", 10: "drum", 15: "fuser", 11: "fuser", 17: "fuser", 22: "fuser", 19: "fuser", 20: "transfer", 18: "maintenance", 16: "maintenance",
}

var colorWords = []string{"black", "cyan", "magenta", "yellow", "gray", "grey", "red", "green", "blue", "orange", "violet", "light cyan", "light magenta"}

func colorOf(colorant, descr string) string {
	for _, s := range []string{colorant, descr} {
		l := strings.ToLower(s)
		for _, w := range colorWords {
			if strings.Contains(l, w) {
				if w == "grey" {
					return "gray"
				}
				return w
			}
		}
		if strings.Contains(l, "colour") || strings.Contains(l, "color") && s == colorant {
			return "colour"
		}
	}
	return ""
}

// supplyLevel turns a level and capacity into a percentage and a state. Printers report -3 for "some remaining" and -2 for "unknown".
func supplyLevel(level, max int64, unit int64) (float64, string) {
	switch {
	case level == -3:
		return -1, "ok"
	case level < 0:
		return -1, "unknown"
	case level == 0:
		return 0, "empty"
	}
	pct := -1.0
	switch {
	case max > 0:
		pct = float64(level) * 100 / float64(max)
	case unit == 19 && level <= 100: // already a percentage, capacity not given
		pct = float64(level)
	}
	if pct < 0 {
		return -1, "unknown"
	}
	if pct > 100 {
		pct = 100
	}
	pct = float64(int64(pct*10+0.5)) / 10
	if pct <= 10 {
		return pct, "low"
	}
	return pct, "ok"
}

func ifaceCol(c *snmp.Client, prefix string, max int) map[string]snmp.Value {
	m := map[string]snmp.Value{}
	rows, _ := c.Walk(prefix, max)
	for _, r := range rows {
		m[r.Suffix(prefix)] = r.Value
	}
	return m
}

func sortedKeys2(m map[string]snmp.Value) []string {
	k := make([]string, 0, len(m))
	for x := range m {
		k = append(k, x)
	}
	sort.Slice(k, func(i, j int) bool {
		a, b := strings.Split(k[i], "."), strings.Split(k[j], ".")
		for n := 0; n < len(a) && n < len(b); n++ {
			x, _ := strconv.Atoi(a[n])
			y, _ := strconv.Atoi(b[n])
			if x != y {
				return x < y
			}
		}
		return len(a) < len(b)
	})
	return k
}

// ReadPrinter reads a printer's status, supplies, paper trays, alerts and page counter through the standard printer MIB.
// It fails when the device does not answer or is not a printer (no printer MIB).
func ReadPrinter(c *snmp.Client) (*PrinterReading, error) {
	status := ifaceCol(c, oidHrPrinterStatus, 8)
	counters := ifaceCol(c, oidPrtMarkerLife, 16)
	supplies := ifaceCol(c, oidPrtSupplies+".6", 64)
	if len(status) == 0 && len(counters) == 0 && len(supplies) == 0 {
		if _, err := c.Get("1.3.6.1.2.1.1.1.0"); err != nil {
			return nil, err
		}
		return nil, errors.New("this device does not answer the printer questions (the standard printer MIB); it may not be a network printer, or SNMP is limited on it")
	}
	p := &PrinterReading{Status: "unknown", Pages: -1}
	for _, k := range sortedKeys2(status) {
		p.Status = map[int64]string{1: "other", 2: "unknown", 3: "idle", 4: "printing", 5: "warmup"}[status[k].Int]
		if p.Status == "" {
			p.Status = "unknown"
		}
		break
	}
	for _, v := range ifaceCol(c, oidHrPrinterErrors, 8) {
		p.Flags = decodeFlags(v.Bytes)
		break
	}
	// the page counter: the largest "impressions or sheets" counter (a printer with a colour and a black marker lists both; the total is the larger)
	units := ifaceCol(c, oidPrtMarkerUnit, 16)
	for k, v := range counters {
		if u, ok := units[k]; ok && u.Int != 7 && u.Int != 8 && u.Int != 0 {
			continue // hours, feet or lines of a different kind of counter
		}
		if v.Missing() || v.Int < 0 {
			continue
		}
		if v.Int > p.Pages {
			p.Pages = v.Int
		}
	}
	// supplies
	colorIdx, class, typ, unit, maxc, level := ifaceCol(c, oidPrtSupplies+".3", 64), ifaceCol(c, oidPrtSupplies+".4", 64),
		ifaceCol(c, oidPrtSupplies+".5", 64), ifaceCol(c, oidPrtSupplies+".7", 64), ifaceCol(c, oidPrtSupplies+".8", 64), ifaceCol(c, oidPrtSupplies+".9", 64)
	colorants := ifaceCol(c, oidPrtColorantValue, 32)
	for _, k := range sortedKeys2(supplies) {
		descr := supplies[k].Text()
		s := supply{Name: descr, Kind: supplyKinds[typ[k].Int]}
		if s.Kind == "" {
			s.Kind = "other"
		}
		// the colorant is addressed by (device, colorant index): the first part of the supply's index is the device
		dev := strings.SplitN(k, ".", 2)[0]
		s.Color = colorOf(colorants[dev+"."+strconv.FormatInt(colorIdx[k].Int, 10)].Text(), descr)
		s.Waste = class[k].Int == 4 || s.Kind == "waste"
		if s.Name == "" {
			s.Name = strings.ToUpper(s.Kind[:1]) + s.Kind[1:] + " " + k
		}
		s.Pct, s.State = supplyLevel(level[k].Int, maxc[k].Int, unit[k].Int)
		if level[k].Missing() {
			s.Pct, s.State = -1, "unknown"
		}
		if s.Waste { // a waste container fills up: the level is how full it is, so "low" and "empty" are the wrong words
			if s.Pct >= 90 {
				s.State = "full"
			} else if s.State != "unknown" {
				s.State = "ok"
			}
		}
		p.Supply = append(p.Supply, s)
	}
	// paper trays
	names, tmax, tcur, tmedia := ifaceCol(c, oidPrtInput+".13", 32), ifaceCol(c, oidPrtInput+".9", 32), ifaceCol(c, oidPrtInput+".10", 32), ifaceCol(c, oidPrtInput+".12", 32)
	for _, k := range sortedKeys2(tcur) {
		t := tray{Name: names[k].Text(), Media: tmedia[k].Text()}
		if t.Name == "" {
			t.Name = "Tray " + strings.TrimPrefix(k, strings.SplitN(k, ".", 2)[0]+".")
		}
		t.Pct, t.State = supplyLevel(tcur[k].Int, tmax[k].Int, 0)
		p.Trays = append(p.Trays, t)
	}
	// alerts: what the printer itself says is wrong
	sev, descr := ifaceCol(c, oidPrtAlert+".2", 40), ifaceCol(c, oidPrtAlert+".8", 40)
	for _, k := range sortedKeys2(descr) {
		text := descr[k].Text()
		if text == "" {
			continue
		}
		s := map[int64]string{3: "critical", 4: "warning", 5: "warning"}[sev[k].Int]
		if s == "" {
			s = "info"
		}
		p.Alerts = append(p.Alerts, printerAlert{s, text})
		if len(p.Alerts) >= 20 {
			break
		}
	}
	// the front panel message
	var msg []string
	disp := ifaceCol(c, oidPrtConsoleDisplay, 8)
	for _, k := range sortedKeys2(disp) {
		if t := disp[k].Text(); t != "" {
			msg = append(msg, t)
		}
	}
	p.Message = trimTo(strings.Join(msg, " "), 160)
	// identity
	for _, v := range ifaceCol(c, oidPrtGeneralName, 4) {
		p.Name = v.Text()
		break
	}
	for _, v := range ifaceCol(c, oidPrtGeneralSerial, 4) {
		p.Serial = v.Text()
		break
	}
	for _, v := range ifaceCol(c, oidHrDeviceDescr, 8) {
		if t := v.Text(); t != "" {
			p.Model = t
			break
		}
	}
	return p, nil
}

func trimTo(s string, n int) string {
	s = strings.TrimSpace(s)
	if len(s) > n {
		return s[:n]
	}
	return s
}

type printerRes struct {
	Asset   string          `json:"asset"`
	Reading *PrinterReading `json:"reading"`
	Error   string          `json:"error,omitempty"`
}

// readPrinter reads one printer with its saved SNMP logins (v3 users first, then communities, the way the other readers do) and queues the result.
func (r *Runner) readPrinter(ctx context.Context, a asset, target string) {
	var rd *PrinterReading
	var last error = errors.New("no SNMP login is saved for this customer: add one under Assets > Credentials (hardware category Printers)")
	for _, cr := range pick(r.credsFor(a.Logins), "snmp", target) {
		var err error
		if rd, err = ReadPrinter(snmpClient(target, cr)); err == nil {
			break
		} else {
			last = err
			rd = nil
		}
	}
	res := printerRes{Asset: a.ID, Reading: rd}
	if rd == nil {
		res.Error = last.Error()
	}
	r.mu.Lock()
	r.pending.Results.Printers = append(r.pending.Results.Printers, res)
	r.mu.Unlock()
}

// ---- actions asked for by a person, handed over through the relay

type printerJob struct {
	Asset  string `json:"asset"`
	Action string `json:"action"` // clear-queue, restart
	IP     string `json:"ip"`
}

// doPrinter runs one action on a printer inside the customer's network.
func (r *Runner) doPrinter(ctx context.Context, raw json.RawMessage) (any, error) {
	var j printerJob
	if err := json.Unmarshal(raw, &j); err != nil {
		return nil, err
	}
	r.mu.Lock()
	a, known := r.known[j.Asset]
	r.mu.Unlock()
	target := firstNonEmpty(j.IP, a.IP, a.Host)
	if target == "" {
		return nil, errors.New("the printer has no address")
	}
	host := hostOnly(target)
	switch j.Action {
	case "clear-queue":
		msg, err := ippClearQueue(ctx, "http://"+net.JoinHostPort(host, "631"))
		if err != nil {
			return nil, err
		}
		return map[string]any{"message": msg}, nil
	case "restart":
		if !known {
			return nil, errors.New("this controller has not been given this printer's logins yet; try again in a minute")
		}
		return restartPrinter(r.credsFor(a.Logins), host)
	}
	return nil, fmt.Errorf("unknown printer action %q", j.Action)
}

// restartPrinter asks the printer to power-cycle itself with prtGeneralReset. Every saved SNMP community is tried until one may write.
func restartPrinter(creds []config.Cred, host string) (any, error) {
	list := pick(creds, "snmp", host)
	var refused bool
	var last error = errors.New("no SNMP community is saved for this customer")
	for _, cr := range list {
		if cr.V3 {
			continue
		}
		c := snmpClient(host, cr)
		err := c.SetInt(oidPrtGeneralReset, resetPowerCycle)
		switch {
		case err == nil:
			return map[string]any{"message": "The printer was told to restart. It is back in a minute or two."}, nil
		case snmp.IsNoAnswer(err):
			return map[string]any{"message": "The printer did not answer, which usually means it is already restarting. Check it in a minute or two."}, nil
		case errors.Is(err, snmp.ErrReadOnly):
			refused = true
		default:
			last = err
		}
	}
	if refused {
		return nil, errors.New("the printer would not restart: the saved SNMP community may only read. Save a community that may write (as another SNMP login for this customer) and turn SNMP write on in the printer's web settings")
	}
	return nil, last
}
