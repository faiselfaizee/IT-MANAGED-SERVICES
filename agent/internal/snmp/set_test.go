package snmp

import (
	"errors"
	"net"
	"testing"
	"time"
)

// answerSet is a device that answers one SetRequest: status 0 accepts, anything else refuses. It reports what it was asked to write.
func answerSet(t *testing.T, status int64, community string) (addr string, got chan [2]any) {
	t.Helper()
	pc, err := net.ListenPacket("udp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { pc.Close() })
	got = make(chan [2]any, 1)
	go func() {
		buf := make([]byte, 65535)
		n, from, err := pc.ReadFrom(buf)
		if err != nil {
			return
		}
		r := &reader{b: buf[:n]}
		_, msg, _ := r.tlv()
		m := &reader{b: msg}
		m.tlv()
		_, comm, _ := m.tlv()
		tag, pdu, _ := m.tlv()
		p := &reader{b: pdu}
		_, id, _ := p.tlv()
		p.tlv()
		p.tlv()
		_, vbs, _ := p.tlv()
		vr := &reader{b: vbs}
		_, vb, _ := vr.tlv()
		one := &reader{b: vb}
		_, ob, _ := one.tlv()
		vt, vbody, _ := one.tlv()
		got <- [2]any{decOID(ob), decodeValue(vt, vbody).Int}
		if tag != pduSet || string(comm) != community {
			return
		}
		varbind := tlv(tagSequence, append(tlv(tagOID, ob), tlv(vt, vbody)...))
		body := append(tlv(tagInteger, id), encInt(status)...)
		body = append(body, encInt(0)...)
		body = append(body, tlv(tagSequence, varbind)...)
		out := tlv(tagSequence, append(append(encInt(1), tlv(tagOctet, comm)...), tlv(pduResponse, body)...))
		pc.WriteTo(out, from)
	}()
	return pc.LocalAddr().String(), got
}

func TestSetIntWritesTheValueAndReportsSuccess(t *testing.T) {
	addr, got := answerSet(t, 0, "private")
	c := &Client{Addr: addr, Community: "private", Timeout: time.Second}
	if err := c.SetInt("1.3.6.1.2.1.43.5.1.1.3.1", 4); err != nil {
		t.Fatal(err)
	}
	g := <-got
	if g[0] != "1.3.6.1.2.1.43.5.1.1.3.1" || g[1] != int64(4) {
		t.Fatalf("the device was asked to write %v", g)
	}
}

func TestSetIntSaysWhenTheCommunityMayOnlyRead(t *testing.T) {
	addr, _ := answerSet(t, 17, "public")
	c := &Client{Addr: addr, Community: "public", Timeout: time.Second}
	if err := c.SetInt("1.3.6.1.2.1.43.5.1.1.3.1", 4); !errors.Is(err, ErrReadOnly) {
		t.Fatalf("got %v, want the read-only message", err)
	}
}

func TestSetIntRefusesSNMPv3(t *testing.T) {
	c := &Client{Addr: "127.0.0.1:1", V3: &V3{User: "u"}}
	if err := c.SetInt("1.3.6.1.2.1.43.5.1.1.3.1", 4); err == nil {
		t.Fatal("an SNMPv3 login must not be used to write")
	}
}

func TestNoAnswerToAWriteIsReportedAsSuch(t *testing.T) {
	pc, _ := net.ListenPacket("udp", "127.0.0.1:0")
	defer pc.Close()
	c := &Client{Addr: pc.LocalAddr().String(), Community: "private", Timeout: 50 * time.Millisecond}
	if err := c.SetInt("1.3.6.1.2.1.43.5.1.1.3.1", 4); !IsNoAnswer(err) {
		t.Fatalf("got %v", err)
	}
}
