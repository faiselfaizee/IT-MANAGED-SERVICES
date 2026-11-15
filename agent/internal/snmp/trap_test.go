package snmp

import "testing"

func TestAV2TrapIsReadBack(t *testing.T) {
	b, err := EncodeTrapV2("public", "1.3.6.1.6.3.1.1.5.3", []Entry{{OID: "1.3.6.1.2.1.2.2.1.1.7", Value: Value{Tag: tagInteger, Int: 7}}, {OID: "1.3.6.1.2.1.2.2.1.2.7", Value: Value{Tag: tagOctet, Bytes: []byte("Gi1/0/7")}}})
	if err != nil {
		t.Fatal(err)
	}
	tr, err := ParseTrap(b)
	if err != nil {
		t.Fatal(err)
	}
	if tr.Version != 2 || tr.Community != "public" || tr.Name != "linkDown" || len(tr.Vars) != 2 || tr.Vars[1].Value.Text() != "Gi1/0/7" || tr.Vars[0].Value.Int != 7 {
		t.Fatalf("%+v", tr)
	}
}

func TestAV1EnterpriseTrapGetsItsRfc3584Oid(t *testing.T) {
	// SEQ{ v1(0), "public", Trap-PDU{ enterprise 1.3.6.1.4.1.12356, agent 10.0.0.1, generic 6, specific 101, ticks 0, varbinds{} } }
	ent, _ := encOID("1.3.6.1.4.1.12356")
	pdu := append(append(append(append(append(ent, tlv(tagIPAddress, []byte{10, 0, 0, 1})...), encInt(6)...), encInt(101)...), tlv(tagTimeTicks, []byte{0})...), tlv(tagSequence, nil)...)
	msg := tlv(tagSequence, append(append(encInt(0), tlv(tagOctet, []byte("public"))...), tlv(pduTrapV1, pdu)...))
	tr, err := ParseTrap(msg)
	if err != nil {
		t.Fatal(err)
	}
	if tr.Version != 1 || tr.OID != "1.3.6.1.4.1.12356.0.101" || tr.Name != "" {
		t.Fatalf("%+v", tr)
	}
	v1std := tlv(tagSequence, append(append(encInt(0), tlv(tagOctet, []byte("x"))...), tlv(pduTrapV1, append(append(append(append(append(ent, tlv(tagIPAddress, []byte{10, 0, 0, 1})...), encInt(2)...), encInt(0)...), tlv(tagTimeTicks, []byte{0})...), tlv(tagSequence, nil)...))...))
	if tr, _ := ParseTrap(v1std); tr == nil || tr.Name != "linkDown" {
		t.Fatalf("generic 2 is linkDown: %+v", tr)
	}
	if _, err := ParseTrap(tlv(tagSequence, encInt(3))); err != ErrTrapV3 {
		t.Fatalf("v3: %v", err)
	}
}
