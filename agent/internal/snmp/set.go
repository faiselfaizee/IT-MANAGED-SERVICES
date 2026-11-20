package snmp

import (
	"errors"
	"fmt"
	"math/rand"
	"net"
	"time"
)

const pduSet = 0xA3

// Error codes of an SNMP answer that mean "this community may not write".
var setRefusals = map[int64]string{
	6:  "noAccess",
	4:  "readOnly",
	17: "notWritable",
	16: "authorizationError",
}

// ErrReadOnly says the device refused a change because the community (or user) may only read.
var ErrReadOnly = errors.New("snmp: the device refused the change (this community is read-only)")

func buildSetInt(community string, reqID int32, oid string, v int64) ([]byte, error) {
	o, err := encOID(oid)
	if err != nil {
		return nil, err
	}
	vb := tlv(tagSequence, append(o, encInt(v)...))
	body := append(encInt(int64(reqID)), encInt(0)...)
	body = append(body, encInt(0)...)
	body = append(body, tlv(tagSequence, vb)...)
	msg := append(encInt(1), tlv(tagOctet, []byte(community))...)
	msg = append(msg, tlv(pduSet, body)...)
	return tlv(tagSequence, msg), nil
}

// SetInt writes one integer value (SNMP v2c only). It is used for the one standard "restart this printer" setting; the community must be
// able to write, which most printers only allow when an administrator turned that on. An SNMPv3 login is refused with a clear message.
func (c *Client) SetInt(oid string, v int64) error {
	if c.V3 != nil {
		return errors.New("snmp: changing a setting needs an SNMP v2c community that may write; an SNMPv3 login is not used for this")
	}
	to := c.Timeout
	if to <= 0 {
		to = 2 * time.Second
	}
	conn, err := net.Dial("udp", c.addr())
	if err != nil {
		return err
	}
	defer conn.Close()
	var last error
	// a write is sent once: repeating a restart because an answer was slow would restart twice
	id := rand.Int31()
	req, err := buildSetInt(c.Community, id, oid, v)
	if err != nil {
		return err
	}
	_ = conn.SetDeadline(time.Now().Add(to * 2))
	if _, err := conn.Write(req); err != nil {
		return err
	}
	buf := make([]byte, 65535)
	for {
		n, err := conn.Read(buf)
		if err != nil {
			last = err
			break
		}
		rid, es, _, _, err := parseResponse(buf[:n])
		if err != nil {
			last = err
			break
		}
		if rid != id {
			continue
		}
		if es == 0 {
			return nil
		}
		if _, ok := setRefusals[es]; ok {
			return ErrReadOnly
		}
		return fmt.Errorf("snmp: the device refused the change (error %d)", es)
	}
	if ne, ok := last.(net.Error); ok && ne.Timeout() {
		// a printer that restarts straight away may not answer at all
		return errNoAnswer
	}
	return last
}

var errNoAnswer = errors.New("snmp: no answer to the change")

// IsNoAnswer is true when a change got no answer: for a restart that usually means the printer is already restarting.
func IsNoAnswer(err error) bool { return errors.Is(err, errNoAnswer) }
