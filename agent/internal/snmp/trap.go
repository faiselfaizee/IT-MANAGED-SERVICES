package snmp

import (
	"errors"
	"fmt"
	"strings"
)

// Trap is a notification a device sent on its own (SNMP v1 trap or v2c trap/inform).
type Trap struct {
	Version   int // 1 or 2
	Community string
	OID       string // snmpTrapOID: what happened
	Name      string // a readable name for the common ones (linkDown, coldStart ...), else ""
	Vars      []Entry
}

const (
	pduTrapV1  = 0xA4
	pduTrapV2  = 0xA7
	pduInform  = 0xA6
	trapOIDVar = "1.3.6.1.6.3.1.1.4.1.0"
	stdTraps   = "1.3.6.1.6.3.1.1.5."
)

// ErrTrapV3 is returned for SNMPv3 traps, which this receiver does not decode.
var ErrTrapV3 = errors.New("snmp: version 3 traps are not supported yet")

var trapNames = map[string]string{"1": "coldStart", "2": "warmStart", "3": "linkDown", "4": "linkUp", "5": "authenticationFailure", "6": "egpNeighborLoss"}

// ParseTrap reads one trap datagram.
func ParseTrap(b []byte) (*Trap, error) {
	r := &reader{b: b}
	tag, msg, err := r.tlv()
	if err != nil || tag != tagSequence {
		return nil, errors.New("snmp: not an SNMP message")
	}
	m := &reader{b: msg}
	_, vb, err := m.tlv()
	if err != nil {
		return nil, err
	}
	ver := decInt(vb)
	if ver == 3 {
		return nil, ErrTrapV3
	}
	_, cb, err := m.tlv()
	if err != nil {
		return nil, err
	}
	ptag, pdu, err := m.tlv()
	if err != nil {
		return nil, err
	}
	t := &Trap{Version: int(ver) + 1, Community: string(cb)}
	p := &reader{b: pdu}
	switch ptag {
	case pduTrapV1:
		_, ent, err := p.tlv()
		if err != nil {
			return nil, err
		}
		if _, _, err = p.tlv(); err != nil { // agent address
			return nil, err
		}
		_, gen, err := p.tlv()
		if err != nil {
			return nil, err
		}
		_, spec, err := p.tlv()
		if err != nil {
			return nil, err
		}
		if _, _, err = p.tlv(); err != nil { // time stamp
			return nil, err
		}
		g := decInt(gen)
		if g == 6 { // enterpriseSpecific: enterprise.0.specific (RFC 3584)
			t.OID = fmt.Sprintf("%s.0.%d", decOID(ent), decInt(spec))
		} else {
			t.OID = fmt.Sprintf("%s%d", stdTraps, g+1)
		}
	case pduTrapV2, pduInform:
		for i := 0; i < 3; i++ { // request id, error status, error index
			if _, _, err = p.tlv(); err != nil {
				return nil, err
			}
		}
	default:
		return nil, fmt.Errorf("snmp: not a trap (PDU %#x)", ptag)
	}
	_, vbs, err := p.tlv()
	if err != nil {
		return nil, err
	}
	vr := &reader{b: vbs}
	for vr.p < len(vbs) {
		_, one, err := vr.tlv()
		if err != nil {
			break
		}
		o := &reader{b: one}
		_, ob, err := o.tlv()
		if err != nil {
			break
		}
		vt, vbody, err := o.tlv()
		if err != nil {
			break
		}
		e := Entry{OID: decOID(ob), Value: decodeValue(vt, vbody)}
		if e.OID == trapOIDVar {
			t.OID = e.Value.OID
			continue
		}
		if e.OID == "1.3.6.1.2.1.1.3.0" { // sysUpTime
			continue
		}
		t.Vars = append(t.Vars, e)
	}
	if strings.HasPrefix(t.OID, stdTraps) {
		t.Name = trapNames[strings.TrimPrefix(t.OID, stdTraps)]
	}
	return t, nil
}

// EncodeTrapV2 builds a v2c trap (used by tests and by anyone who wants to send one to the receiver).
func EncodeTrapV2(community, trapOID string, vars []Entry) ([]byte, error) {
	enc := func(oid string, v Value) ([]byte, error) {
		ob, err := encOID(oid)
		if err != nil {
			return nil, err
		}
		var val []byte
		switch v.Tag {
		case tagOID:
			b, err := encOID(v.OID)
			if err != nil {
				return nil, err
			}
			val = b
		case tagInteger:
			val = encInt(v.Int)
		default:
			val = tlv(tagOctet, v.Bytes)
		}
		return tlv(tagSequence, append(ob, val...)), nil
	}
	var vb []byte
	tv, err := enc(trapOIDVar, Value{Tag: tagOID, OID: trapOID})
	if err != nil {
		return nil, err
	}
	vb = append(vb, tv...)
	for _, e := range vars {
		x, err := enc(e.OID, e.Value)
		if err != nil {
			return nil, err
		}
		vb = append(vb, x...)
	}
	pdu := append(append(append(encInt(1), encInt(0)...), encInt(0)...), tlv(tagSequence, vb)...)
	msg := append(append(encInt(1), tlv(tagOctet, []byte(community))...), tlv(pduTrapV2, pdu)...)
	return tlv(tagSequence, msg), nil
}
