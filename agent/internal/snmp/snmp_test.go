package snmp

import (
	"net"
	"testing"
	"time"
)

func TestOIDEncodingMatchesTheKnownBytes(t *testing.T) {
	// 1.3.6.1.2.1.1.1.0 is 06 08 2b 06 01 02 01 01 01 00 in every SNMP book
	b, err := encOID("1.3.6.1.2.1.1.1.0")
	want := []byte{0x06, 0x08, 0x2b, 0x06, 0x01, 0x02, 0x01, 0x01, 0x01, 0x00}
	if err != nil || string(b) != string(want) {
		t.Fatalf("%x %v", b, err)
	}
	// 1.3.6.1.4.1.2636 (Juniper) needs a two-byte sub-identifier: 2636 = 0x14 0x4c
	b, _ = encOID("1.3.6.1.4.1.2636")
	if got := decOID(b[2:]); got != "1.3.6.1.4.1.2636" || b[len(b)-2] != 0x94 || b[len(b)-1] != 0x4c {
		t.Fatalf("%x %s", b, got)
	}
}

func TestIntegerEncodingKeepsTheSign(t *testing.T) {
	for _, v := range []int64{0, 1, 127, 128, 255, 256, -1, -128, -129, 65536, 2147483647, -2147483648} {
		b := encInt(v)
		r := &reader{b: b}
		_, body, err := r.tlv()
		if err != nil || decInt(body) != v {
			t.Fatalf("%d -> %x -> %d (%v)", v, b, decInt(body), err)
		}
	}
}

// fakeDevice answers GET and GETNEXT from a sorted list, like a small printer would.
func fakeDevice(t *testing.T, mib [][2]string, vals map[string]Value) (addr string, stop func()) {
	pc, err := net.ListenPacket("udp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
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
				continue // wrong community: no answer, like a real device
			}
			answer, v := ask, Value{Tag: tagNoSuchObj}
			if pduTag == pduGet {
				if x, ok := vals[ask]; ok {
					v = x
				}
			} else {
				v = Value{Tag: tagEndOfMib}
				for _, e := range mib {
					if lessOID(ask, e[0]) {
						answer, v = e[0], vals[e[0]]
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
			case tagTimeTicks:
				val = tlv(tagTimeTicks, []byte{byte(v.Int >> 8), byte(v.Int)})
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

func lessOID(a, b string) bool {
	pa, pb := splitNums(a), splitNums(b)
	for i := 0; i < len(pa) && i < len(pb); i++ {
		if pa[i] != pb[i] {
			return pa[i] < pb[i]
		}
	}
	return len(pa) < len(pb)
}

func splitNums(s string) []int {
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

func TestClientGetsAndWalksAFakeDevice(t *testing.T) {
	mib := [][2]string{{"1.3.6.1.2.1.1.1.0"}, {"1.3.6.1.2.1.1.5.0"}, {"1.3.6.1.2.1.2.2.1.2.1"}, {"1.3.6.1.2.1.2.2.1.2.2"}, {"1.3.6.1.2.1.2.2.1.6.1"}, {"1.3.6.1.2.1.4.20.1.1.10.0.0.5"}}
	vals := map[string]Value{
		"1.3.6.1.2.1.1.1.0":             {Tag: tagOctet, Bytes: []byte("HP LaserJet M404")},
		"1.3.6.1.2.1.1.5.0":             {Tag: tagOctet, Bytes: []byte("PRN-FRONT")},
		"1.3.6.1.2.1.2.2.1.2.1":         {Tag: tagOctet, Bytes: []byte("eth0")},
		"1.3.6.1.2.1.2.2.1.2.2":         {Tag: tagOctet, Bytes: []byte("lo")},
		"1.3.6.1.2.1.2.2.1.6.1":         {Tag: tagOctet, Bytes: []byte{0xa0, 0xb1, 0xc2, 0xd3, 0xe4, 0xf5}},
		"1.3.6.1.2.1.4.20.1.1.10.0.0.5": {Tag: tagInteger, Int: -7},
	}
	addr, stop := fakeDevice(t, mib, vals)
	defer stop()
	c := &Client{Addr: addr, Community: "public", Timeout: 500 * time.Millisecond, Retries: 1}
	v, err := c.Get("1.3.6.1.2.1.1.5.0")
	if err != nil || v.Text() != "PRN-FRONT" {
		t.Fatalf("%v %v", v, err)
	}
	if v, _ := c.Get("1.3.6.1.2.1.99.0"); !v.Missing() {
		t.Fatal("a value the device does not have must come back as missing")
	}
	rows, err := c.Walk("1.3.6.1.2.1.2.2.1.2", 50)
	if err != nil || len(rows) != 2 || rows[1].Suffix("1.3.6.1.2.1.2.2.1.2") != "2" || rows[0].Value.Text() != "eth0" {
		t.Fatalf("%v %v", rows, err)
	}
	mac, _ := c.Walk("1.3.6.1.2.1.2.2.1.6", 5)
	if len(mac) != 1 || mac[0].Value.Text() != "a0:b1:c2:d3:e4:f5" {
		t.Fatalf("%v", mac)
	}
	if n, _ := c.Get("1.3.6.1.2.1.4.20.1.1.10.0.0.5"); n.Int != -7 {
		t.Fatalf("%v", n)
	}
	bad := &Client{Addr: addr, Community: "wrong", Timeout: 200 * time.Millisecond}
	if _, err := bad.Get("1.3.6.1.2.1.1.5.0"); err == nil {
		t.Fatal("a wrong community must fail, not hang")
	}
}
