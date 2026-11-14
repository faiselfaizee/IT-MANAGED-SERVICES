package snmp

import (
	"crypto/hmac"
	"net"
	"sort"
	"time"
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

// FakeDeviceV3 is FakeDevice for SNMPv3: it answers discovery, checks the user, the digest and the clock window like a real
// engine (with reports), and encrypts its answers. user holds the user name, protocols and passwords it accepts.
func FakeDeviceV3(vals map[string]Value, user *V3, engineID []byte, boots int64) (addr string, stop func()) {
	pc, err := net.ListenPacket("udp", "127.0.0.1:0")
	if err != nil {
		panic(err)
	}
	keys := make([]string, 0, len(vals))
	for k := range vals {
		keys = append(keys, k)
	}
	sort.Slice(keys, func(i, j int) bool { return oidLess(keys[i], keys[j]) })
	dev := &V3{User: user.User, AuthProto: user.AuthProto, AuthPass: user.AuthPass, PrivProto: user.PrivProto, PrivPass: user.PrivPass, engineID: engineID, boots: boots, timeAt: 1000, learned: time.Now(), salt: 77}
	if h := dev.hashFn(); h != nil {
		dev.authKey = PasswordToKey(h, dev.AuthPass, engineID)
		dev.privKey = PasswordToKey(h, dev.PrivPass, engineID)
	}
	report := func(id int32, oid string) []byte {
		o, _ := encOID(oid)
		pdu := tlv(pduReport, concat(encInt(int64(id)), encInt(0), encInt(0), tlv(tagSequence, tlv(tagSequence, concat(o, encInt(1))))))
		scoped := tlv(tagSequence, concat(tlv(tagOctet, engineID), tlv(tagOctet, nil), pdu))
		sp := tlv(tagSequence, concat(tlv(tagOctet, engineID), encInt(dev.boots), encInt(dev.engineTime()), tlv(tagOctet, nil), tlv(tagOctet, nil), tlv(tagOctet, nil)))
		return v3Message(id, 0, sp, scoped)
	}
	go func() {
		buf := make([]byte, 65535)
		for {
			n, from, err := pc.ReadFrom(buf)
			if err != nil {
				return
			}
			msg := append([]byte{}, buf[:n]...)
			hdr, err := parseV3(msg, nil)
			if err != nil && hdr == nil {
				// encrypted: read the header fields without the PDU
				hdr, _ = parseV3Header(msg)
			}
			if hdr == nil {
				continue
			}
			if len(hdr.engineID) == 0 {
				_, _ = pc.WriteTo(report(hdr.msgID, oidUnknownEngID), from)
				continue
			}
			if u := userOf(msg); u != dev.User {
				_, _ = pc.WriteTo(report(hdr.msgID, oidUnknownUser), from)
				continue
			}
			if dev.flags()&flagAuth != 0 {
				a, b, err := digestSpan(msg)
				if err != nil {
					continue
				}
				got := append([]byte{}, msg[a:b]...)
				for i := a; i < b; i++ {
					msg[i] = 0
				}
				if !hmac.Equal(got, dev.mac(msg)) {
					_, _ = pc.WriteTo(report(hdr.msgID, oidWrongDigest), from)
					continue
				}
				if d := hdr.time - dev.engineTime(); hdr.boots != dev.boots || d > 150 || d < -150 {
					_, _ = pc.WriteTo(report(hdr.msgID, oidNotInTime), from)
					continue
				}
			}
			a, err := parseV3(msg, dev)
			if err != nil {
				_, _ = pc.WriteTo(report(hdr.msgID, oidDecryptErr), from)
				continue
			}
			p := &reader{b: a.pdu}
			pduTag, body, _ := p.tlv()
			pr := &reader{b: body}
			_, id, _ := pr.tlv()
			pr.tlv()
			pr.tlv()
			_, vbs, _ := pr.tlv()
			vb, _ := (&reader{b: vbs}).tlvBody()
			_, ob, _ := (&reader{b: vb}).tlv()
			ask := decOID(ob)
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
			out, err := dev.message(pduResponse, int32(decInt(id)), concat(oidb, encodeValue(v)), dev.boots, dev.engineTime())
			if err != nil {
				continue
			}
			_, _ = pc.WriteTo(out, from)
		}
	}()
	return pc.LocalAddr().String(), func() { pc.Close() }
}

func (r *reader) tlvBody() ([]byte, error) { _, b, err := r.tlv(); return b, err }

func encodeValue(v Value) []byte {
	switch v.Tag {
	case tagInteger:
		return encInt(v.Int)
	case tagOctet:
		return tlv(tagOctet, v.Bytes)
	case tagGauge32, tagTimeTicks, tagCounter32:
		_, body, _ := (&reader{b: encInt(v.Int)}).tlv()
		return tlv(v.Tag, body)
	case tagOID:
		o, _ := encOID(v.OID)
		return o
	}
	return tlv(v.Tag, nil)
}

// parseV3Header reads msgID, engine, boots and time of a message whose PDU is encrypted.
func parseV3Header(b []byte) (*v3Answer, error) {
	o := &reader{b: b, p: headerLen(b, 0)}
	o.tlv()
	_, global, err := o.tlv()
	if err != nil {
		return nil, err
	}
	_, id, _ := (&reader{b: global}).tlv()
	_, spRaw, err := o.tlv()
	if err != nil {
		return nil, err
	}
	_, sp, _ := (&reader{b: spRaw}).tlv()
	s := &reader{b: sp}
	_, eng, _ := s.tlv()
	_, bo, _ := s.tlv()
	_, ti, _ := s.tlv()
	return &v3Answer{msgID: int32(decInt(id)), engineID: eng, boots: decInt(bo), time: decInt(ti)}, nil
}

func userOf(b []byte) string {
	o := &reader{b: b, p: headerLen(b, 0)}
	o.tlv()
	o.tlv()
	_, spRaw, err := o.tlv()
	if err != nil {
		return ""
	}
	_, sp, _ := (&reader{b: spRaw}).tlv()
	s := &reader{b: sp}
	s.tlv()
	s.tlv()
	s.tlv()
	_, u, _ := s.tlv()
	return string(u)
}
