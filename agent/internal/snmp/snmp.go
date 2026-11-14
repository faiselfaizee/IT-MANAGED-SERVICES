// Package snmp is a small SNMP v2c client (GET and GETNEXT over UDP) for reading printers, switches, firewalls and other devices.
// It is written with the standard library only. Read-only: it can never change a device.
package snmp

import (
	"errors"
	"fmt"
	"math/rand"
	"net"
	"strconv"
	"strings"
	"time"
)

// BER tags used by SNMP.
const (
	tagInteger   = 0x02
	tagOctet     = 0x04
	tagNull      = 0x05
	tagOID       = 0x06
	tagSequence  = 0x30
	tagIPAddress = 0x40
	tagCounter32 = 0x41
	tagGauge32   = 0x42
	tagTimeTicks = 0x43
	tagOpaque    = 0x44
	tagCounter64 = 0x46
	tagNoSuchObj = 0x80
	tagNoSuchIns = 0x81
	tagEndOfMib  = 0x82
	pduGet       = 0xA0
	pduGetNext   = 0xA1
	pduResponse  = 0xA2
)

// Value is one answer. Text() gives the readable form of any type.
type Value struct {
	Tag   byte
	Int   int64
	Bytes []byte
	OID   string
}

func (v Value) Missing() bool {
	return v.Tag == tagNoSuchObj || v.Tag == tagNoSuchIns || v.Tag == tagEndOfMib
}

// Text is the value as a person would read it; octet strings that are not printable come out as hex ("aa:bb:cc").
func (v Value) Text() string {
	switch v.Tag {
	case tagOctet, tagOpaque:
		if printable(v.Bytes) {
			return strings.TrimSpace(strings.Map(func(r rune) rune {
				if r == 0 {
					return -1
				}
				return r
			}, string(v.Bytes)))
		}
		return Hex(v.Bytes)
	case tagOID:
		return v.OID
	case tagIPAddress:
		if len(v.Bytes) == 4 {
			return net.IP(v.Bytes).String()
		}
	case tagNull, tagNoSuchObj, tagNoSuchIns, tagEndOfMib:
		return ""
	}
	return strconv.FormatInt(v.Int, 10)
}

func printable(b []byte) bool {
	for _, c := range b {
		if c == 0 {
			continue
		}
		if (c < 32 && c != '\n' && c != '\r' && c != '\t') || c > 126 {
			return false
		}
	}
	return true
}

// Hex writes bytes as aa:bb:cc, the way MAC addresses are written.
func Hex(b []byte) string {
	p := make([]string, len(b))
	for i, c := range b {
		p[i] = fmt.Sprintf("%02x", c)
	}
	return strings.Join(p, ":")
}

// ---- encoding

func encLen(n int) []byte {
	switch {
	case n < 128:
		return []byte{byte(n)}
	case n < 256:
		return []byte{0x81, byte(n)}
	default:
		return []byte{0x82, byte(n >> 8), byte(n)}
	}
}

func tlv(tag byte, body []byte) []byte {
	out := append([]byte{tag}, encLen(len(body))...)
	return append(out, body...)
}

func encInt(v int64) []byte {
	var b []byte
	for {
		b = append([]byte{byte(v)}, b...)
		if (v == 0 && b[0]&0x80 == 0) || (v == -1 && b[0]&0x80 != 0) {
			break
		}
		v >>= 8
	}
	return tlv(tagInteger, b)
}

func encOID(oid string) ([]byte, error) {
	parts := strings.Split(strings.TrimPrefix(oid, "."), ".")
	if len(parts) < 2 {
		return nil, fmt.Errorf("bad OID %q", oid)
	}
	nums := make([]uint64, len(parts))
	for i, p := range parts {
		n, err := strconv.ParseUint(p, 10, 32)
		if err != nil {
			return nil, fmt.Errorf("bad OID %q", oid)
		}
		nums[i] = n
	}
	body := []byte{byte(nums[0]*40 + nums[1])}
	for _, n := range nums[2:] {
		var tmp []byte
		tmp = append(tmp, byte(n&0x7f))
		for n >>= 7; n > 0; n >>= 7 {
			tmp = append(tmp, byte(n&0x7f)|0x80)
		}
		for i := len(tmp) - 1; i >= 0; i-- {
			body = append(body, tmp[i])
		}
	}
	return tlv(tagOID, body), nil
}

func buildRequest(pdu byte, community string, reqID int32, oid string) ([]byte, error) {
	o, err := encOID(oid)
	if err != nil {
		return nil, err
	}
	vb := tlv(tagSequence, append(o, tlv(tagNull, nil)...))
	body := append(encInt(int64(reqID)), encInt(0)...)
	body = append(body, encInt(0)...)
	body = append(body, tlv(tagSequence, vb)...)
	msg := append(encInt(1), tlv(tagOctet, []byte(community))...)
	msg = append(msg, tlv(pdu, body)...)
	return tlv(tagSequence, msg), nil
}

// ---- decoding

type reader struct {
	b []byte
	p int
}

var errShort = errors.New("snmp: answer is cut short")

func (r *reader) tlv() (tag byte, body []byte, err error) {
	if r.p+2 > len(r.b) {
		return 0, nil, errShort
	}
	tag = r.b[r.p]
	l := int(r.b[r.p+1])
	r.p += 2
	if l&0x80 != 0 {
		n := l & 0x7f
		if n == 0 || n > 3 || r.p+n > len(r.b) {
			return 0, nil, errShort
		}
		l = 0
		for i := 0; i < n; i++ {
			l = l<<8 | int(r.b[r.p+i])
		}
		r.p += n
	}
	if l < 0 || r.p+l > len(r.b) {
		return 0, nil, errShort
	}
	body = r.b[r.p : r.p+l]
	r.p += l
	return tag, body, nil
}

func decInt(b []byte) int64 {
	if len(b) == 0 {
		return 0
	}
	v := int64(0)
	if b[0]&0x80 != 0 {
		v = -1
	}
	for _, c := range b {
		v = v<<8 | int64(c)
	}
	return v
}

func decUint(b []byte) int64 {
	var v uint64
	for _, c := range b {
		v = v<<8 | uint64(c)
	}
	return int64(v)
}

func decOID(b []byte) string {
	if len(b) == 0 {
		return ""
	}
	parts := []string{strconv.Itoa(int(b[0]) / 40), strconv.Itoa(int(b[0]) % 40)}
	var n uint64
	for _, c := range b[1:] {
		n = n<<7 | uint64(c&0x7f)
		if c&0x80 == 0 {
			parts = append(parts, strconv.FormatUint(n, 10))
			n = 0
		}
	}
	return strings.Join(parts, ".")
}

func decodeValue(tag byte, body []byte) Value {
	v := Value{Tag: tag, Bytes: append([]byte(nil), body...)}
	switch tag {
	case tagInteger:
		v.Int = decInt(body)
	case tagCounter32, tagGauge32, tagTimeTicks, tagCounter64:
		v.Int = decUint(body)
	case tagOID:
		v.OID = decOID(body)
	}
	return v
}

// parseResponse returns the request id, error status and the first variable of an SNMP answer.
func parseResponse(b []byte) (reqID int32, errStatus int64, oid string, val Value, err error) {
	r := &reader{b: b}
	tag, msg, err := r.tlv()
	if err != nil || tag != tagSequence {
		return 0, 0, "", val, errors.New("snmp: not an SNMP answer")
	}
	m := &reader{b: msg}
	if _, _, err = m.tlv(); err != nil { // version
		return
	}
	if _, _, err = m.tlv(); err != nil { // community
		return
	}
	start := m.p
	if _, _, err = m.tlv(); err != nil {
		return
	}
	return parsePDU(msg[start:m.p])
}

func randInt31() int32 { return rand.Int31() }

// parsePDU reads a response PDU (its whole TLV): request id, error status and the first variable.
func parsePDU(b []byte) (reqID int32, errStatus int64, oid string, val Value, err error) {
	r := &reader{b: b}
	tag, pdu, err := r.tlv()
	if err != nil || (tag != pduResponse && tag != pduReport) {
		return 0, 0, "", val, errors.New("snmp: unexpected answer type")
	}
	p := &reader{b: pdu}
	_, id, err := p.tlv()
	if err != nil {
		return
	}
	_, es, err := p.tlv()
	if err != nil {
		return
	}
	if _, _, err = p.tlv(); err != nil { // error index
		return
	}
	_, vbs, err := p.tlv()
	if err != nil {
		return
	}
	vr := &reader{b: vbs}
	_, vb, err := vr.tlv()
	if err != nil {
		return
	}
	one := &reader{b: vb}
	_, ob, err := one.tlv()
	if err != nil {
		return
	}
	vt, vbody, err := one.tlv()
	if err != nil {
		return
	}
	return int32(decInt(id)), decInt(es), decOID(ob), decodeValue(vt, vbody), nil
}

// ---- client

type Client struct {
	Addr      string // host or host:port; port 161 by default
	Community string
	V3        *V3           // set for SNMP version 3; Community is then not used
	Timeout   time.Duration // per try; default 2 s
	Retries   int           // default 1
}

func (c *Client) addr() string {
	if _, _, err := net.SplitHostPort(c.Addr); err == nil {
		return c.Addr
	}
	return net.JoinHostPort(c.Addr, "161")
}

func (c *Client) ask(pdu byte, oid string) (string, Value, error) {
	to := c.Timeout
	if to <= 0 {
		to = 2 * time.Second
	}
	tries := c.Retries + 1
	var last error
	conn, err := net.Dial("udp", c.addr())
	if err != nil {
		return "", Value{}, err
	}
	defer conn.Close()
	if c.V3 != nil {
		return c.askV3(conn, to, tries, pdu, oid)
	}
	for i := 0; i < tries; i++ {
		id := rand.Int31()
		req, err := buildRequest(pdu, c.Community, id, oid)
		if err != nil {
			return "", Value{}, err
		}
		_ = conn.SetDeadline(time.Now().Add(to))
		if _, err := conn.Write(req); err != nil {
			last = err
			continue
		}
		buf := make([]byte, 65535)
		for {
			n, err := conn.Read(buf)
			if err != nil {
				last = err
				break
			}
			rid, es, o, v, err := parseResponse(buf[:n])
			if err != nil {
				last = err
				break
			}
			if rid != id {
				continue // an old answer to an earlier try
			}
			if es != 0 {
				return "", Value{}, fmt.Errorf("snmp: the device answered with error %d for %s", es, oid)
			}
			return o, v, nil
		}
	}
	if ne, ok := last.(net.Error); ok && ne.Timeout() {
		return "", Value{}, errors.New("snmp: no answer (wrong community, SNMP off or blocked)")
	}
	return "", Value{}, last
}

// askV3 sends one SNMPv3 request: it learns the engine on first use and follows the device's clock when told it is out of the window.
func (c *Client) askV3(conn net.Conn, to time.Duration, tries int, pdu byte, oid string) (string, Value, error) {
	v := c.V3
	v.mu.Lock()
	defer v.mu.Unlock()
	exchange := func(req []byte, want int32) ([]byte, error) {
		var last error
		for i := 0; i < tries; i++ {
			_ = conn.SetDeadline(time.Now().Add(to))
			if _, err := conn.Write(req); err != nil {
				last = err
				continue
			}
			buf := make([]byte, 65535)
			for {
				n, err := conn.Read(buf)
				if err != nil {
					last = err
					break
				}
				if a, err := parseV3Header(buf[:n]); err == nil && a.msgID != want {
					continue // an old answer
				}
				return append([]byte{}, buf[:n]...), nil
			}
		}
		if ne, ok := last.(net.Error); ok && ne.Timeout() {
			return nil, errors.New("snmp: no answer (SNMPv3 not enabled, wrong user, or blocked)")
		}
		return nil, last
	}
	if v.engineID == nil {
		var want int32
		err := v.discover(func(req []byte) ([]byte, error) {
			a, _ := parseV3(req, nil)
			if a != nil {
				want = a.msgID
			}
			return exchange(req, want)
		})
		if err != nil {
			return "", Value{}, err
		}
	}
	for attempt := 0; attempt < 2; attempt++ {
		id := rand.Int31()
		req, err := v.request(pdu, id, oid)
		if err != nil {
			return "", Value{}, err
		}
		raw, err := exchange(req, id)
		if err != nil {
			return "", Value{}, err
		}
		a, err := parseV3(raw, v)
		if err != nil {
			return "", Value{}, err
		}
		if a.pduTag == pduReport {
			ro := reportOID(a.pdu)
			if ro == oidNotInTime && attempt == 0 { // follow the device's clock and try once more
				v.boots, v.timeAt, v.learned = a.boots, a.time, time.Now()
				continue
			}
			if t, ok := reportText[ro]; ok {
				return "", Value{}, errors.New("snmp: " + t)
			}
			return "", Value{}, fmt.Errorf("snmp: the device refused the request (report %s)", ro)
		}
		_, es, o, val, err := parsePDU(a.pdu)
		if err != nil {
			return "", Value{}, err
		}
		if es != 0 {
			return "", Value{}, fmt.Errorf("snmp: the device answered with error %d for %s", es, oid)
		}
		return o, val, nil
	}
	return "", Value{}, errors.New("snmp: " + reportText[oidNotInTime])
}

// Get reads one value. A value the device does not have comes back with Missing() true.
func (c *Client) Get(oid string) (Value, error) {
	_, v, err := c.ask(pduGet, oid)
	return v, err
}

// Walk reads everything under prefix, at most max values (default 500), in order.
func (c *Client) Walk(prefix string, max int) ([]Entry, error) {
	if max <= 0 {
		max = 500
	}
	prefix = strings.TrimPrefix(prefix, ".")
	var out []Entry
	cur := prefix
	for len(out) < max {
		o, v, err := c.ask(pduGetNext, cur)
		if err != nil {
			if len(out) > 0 {
				return out, nil
			}
			return nil, err
		}
		if v.Missing() || !(o == prefix || strings.HasPrefix(o, prefix+".")) || o == cur {
			break
		}
		out = append(out, Entry{OID: o, Value: v})
		cur = o
	}
	return out, nil
}

type Entry struct {
	OID   string
	Value Value
}

// Suffix is the part of the OID after prefix, for example the interface number in a table column.
func (e Entry) Suffix(prefix string) string {
	return strings.TrimPrefix(strings.TrimPrefix(e.OID, prefix), ".")
}
