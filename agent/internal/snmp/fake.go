package snmp

import (
	"net"
	"sort"
)

// Values for building a fake device in tests (FakeDevice).
func Str(s string) Value      { return Value{Tag: tagOctet, Bytes: []byte(s)} }
func Bin(b ...byte) Value     { return Value{Tag: tagOctet, Bytes: b} }
func Int(n int64) Value       { return Value{Tag: tagInteger, Int: n} }
func Gauge(n int64) Value     { return Value{Tag: tagGauge32, Int: n} }
func Ticks(n int64) Value     { return Value{Tag: tagTimeTicks, Int: n} }
func ObjectID(o string) Value { return Value{Tag: tagOID, OID: o} }

// FakeDevice answers SNMP v2c GET and GETNEXT for community "public" from vals, on a local UDP port. It is used by tests of
// readers built on this package; stop closes it.
func FakeDevice(vals map[string]Value) (addr string, stop func()) {
	pc, err := net.ListenPacket("udp", "127.0.0.1:0")
	if err != nil {
		panic(err)
	}
	keys := make([]string, 0, len(vals))
	for k := range vals {
		keys = append(keys, k)
	}
	sort.Slice(keys, func(i, j int) bool { return oidLess(keys[i], keys[j]) })
	go func() {
		buf := make([]byte, 65535)
		for {
			n, from, err := pc.ReadFrom(buf)
			if err != nil {
				return
			}
			r := &reader{b: buf[:n]}
			_, msg, _ := r.tlv()
			m := &reader{b: msg}
			m.tlv()
			_, comm, _ := m.tlv()
			pduTag, pdu, _ := m.tlv()
			p := &reader{b: pdu}
			_, id, _ := p.tlv()
			p.tlv()
			p.tlv()
			_, vbs, _ := p.tlv()
			vr := &reader{b: vbs}
			_, vb, _ := vr.tlv()
			one := &reader{b: vb}
			_, ob, _ := one.tlv()
			ask := decOID(ob)
			if string(comm) != "public" {
				continue
			}
			answer, v := ask, Value{Tag: tagNoSuchObj}
			if pduTag == pduGet {
				if x, ok := vals[ask]; ok {
					v = x
				}
			} else {
				v = Value{Tag: tagEndOfMib}
				for _, k := range keys {
					if oidLess(ask, k) {
						answer, v = k, vals[k]
						break
					}
				}
			}
			oidb, _ := encOID(answer)
			var val []byte
			switch v.Tag {
			case tagInteger:
				val = encInt(v.Int)
			case tagOctet:
				val = tlv(tagOctet, v.Bytes)
			case tagGauge32, tagTimeTicks, tagCounter32:
				b := encInt(v.Int)
				_, body, _ := (&reader{b: b}).tlv()
				val = tlv(v.Tag, body)
			case tagOID:
				o, _ := encOID(v.OID)
				val = o
			default:
				val = tlv(v.Tag, nil)
			}
			vbOut := tlv(tagSequence, append(oidb, val...))
			body := append(append(tlv(tagInteger, id), encInt(0)...), encInt(0)...)
			body = append(body, tlv(tagSequence, vbOut)...)
			resp := tlv(tagSequence, append(append(encInt(1), tlv(tagOctet, comm)...), tlv(pduResponse, body)...))
			_, _ = pc.WriteTo(resp, from)
		}
	}()
	return pc.LocalAddr().String(), func() { pc.Close() }
}

func oidLess(a, b string) bool {
	pa, pb := oidNums(a), oidNums(b)
	for i := 0; i < len(pa) && i < len(pb); i++ {
		if pa[i] != pb[i] {
			return pa[i] < pb[i]
		}
	}
	return len(pa) < len(pb)
}

func oidNums(s string) []int {
	var out []int
	n := 0
	for _, c := range s + "." {
		if c == '.' {
			out = append(out, n)
			n = 0
		} else {
			n = n*10 + int(c-'0')
		}
	}
	return out
}
