package snmp

import (
	"bytes"
	"crypto/aes"
	"crypto/cipher"
	"crypto/des" //nolint:gosec // SNMPv3 DES privacy is what older devices offer
	"crypto/hmac"
	"crypto/md5" //nolint:gosec // HMAC-MD5-96 is part of SNMPv3 (RFC 3414)
	"crypto/rand"
	"crypto/sha1" //nolint:gosec // HMAC-SHA-96 is part of SNMPv3 (RFC 3414)
	"crypto/sha256"
	"encoding/binary"
	"errors"
	"fmt"
	"hash"
	"strings"
	"sync"
	"time"
)

// SNMP version 3 with the user-based security model (RFC 3414): authentication HMAC-MD5-96, HMAC-SHA-96 or HMAC-SHA-256-192
// (RFC 7860), privacy DES-CBC or AES-128-CFB (RFC 3826). The engine ID, boots and time are learned from the device on first use.

// V3 is the security of one SNMPv3 user. AuthProto: "" (none), MD5, SHA, SHA256. PrivProto: "" (none), DES, AES.
type V3 struct {
	User                string
	AuthProto, AuthPass string
	PrivProto, PrivPass string
	Context             string

	mu       sync.Mutex
	engineID []byte
	boots    int64
	timeAt   int64
	learned  time.Time
	authKey  []byte
	privKey  []byte
	salt     uint64
}

const (
	pduReport       = 0xA8
	flagAuth        = 0x01
	flagPriv        = 0x02
	flagReportable  = 0x04
	secModelUSM     = 3
	maxMessageSize  = 65507
	oidNotInTime    = "1.3.6.1.6.3.15.1.1.2.0"
	oidUnknownUser  = "1.3.6.1.6.3.15.1.1.3.0"
	oidUnknownEngID = "1.3.6.1.6.3.15.1.1.4.0"
	oidWrongDigest  = "1.3.6.1.6.3.15.1.1.5.0"
	oidDecryptErr   = "1.3.6.1.6.3.15.1.1.6.0"
	oidUnsupported  = "1.3.6.1.6.3.15.1.1.1.0"
)

var reportText = map[string]string{
	oidUnknownUser:  "the device does not know this SNMPv3 user name",
	oidWrongDigest:  "the SNMPv3 authentication password (or protocol) is wrong",
	oidDecryptErr:   "the SNMPv3 privacy password (or protocol) is wrong",
	oidUnsupported:  "the device does not allow this security level for the user (check authentication and privacy)",
	oidUnknownEngID: "the device did not accept its own engine ID",
	oidNotInTime:    "the device's clock window was missed",
}

func (v *V3) hashFn() func() hash.Hash {
	switch strings.ToUpper(strings.ReplaceAll(v.AuthProto, "-", "")) {
	case "MD5":
		return md5.New
	case "SHA", "SHA1":
		return sha1.New
	case "SHA256":
		return sha256.New
	}
	return nil
}

func (v *V3) macLen() int {
	if strings.Contains(strings.ToUpper(v.AuthProto), "256") {
		return 24
	}
	return 12
}

func (v *V3) flags() byte {
	f := byte(flagReportable)
	if v.hashFn() != nil {
		f |= flagAuth
		if p := strings.ToUpper(v.PrivProto); p == "DES" || p == "AES" || p == "AES128" {
			f |= flagPriv
		}
	}
	return f
}

// PasswordToKey is the RFC 3414 A.2 key: the password repeated to 1 MB and hashed, then localized to the engine ID.
func PasswordToKey(h func() hash.Hash, password string, engineID []byte) []byte {
	if password == "" {
		return nil
	}
	d := h()
	pw := []byte(password)
	buf := make([]byte, 64)
	for i, n := 0, 0; n < 1048576; n += 64 {
		for j := range buf {
			buf[j] = pw[i%len(pw)]
			i++
		}
		d.Write(buf)
	}
	ku := d.Sum(nil)
	l := h()
	l.Write(ku)
	l.Write(engineID)
	l.Write(ku)
	return l.Sum(nil)
}

// discoveryRequest asks for the device's engine ID, boots and time (an empty, unauthenticated request).
func discoveryRequest(msgID int32) []byte {
	sp := tlv(tagSequence, concat(tlv(tagOctet, nil), encInt(0), encInt(0), tlv(tagOctet, nil), tlv(tagOctet, nil), tlv(tagOctet, nil)))
	pdu := tlv(pduGet, concat(encInt(int64(msgID)), encInt(0), encInt(0), tlv(tagSequence, nil)))
	scoped := tlv(tagSequence, concat(tlv(tagOctet, nil), tlv(tagOctet, nil), pdu))
	return v3Message(msgID, flagReportable, sp, scoped)
}

func concat(parts ...[]byte) []byte { return bytes.Join(parts, nil) }

func v3Message(msgID int32, flags byte, secParams, data []byte) []byte {
	global := tlv(tagSequence, concat(encInt(int64(msgID)), encInt(maxMessageSize), tlv(tagOctet, []byte{flags}), encInt(secModelUSM)))
	return tlv(tagSequence, concat(encInt(3), global, tlv(tagOctet, secParams), data))
}

// engineTime is the device's clock now, from what it said at discovery.
func (v *V3) engineTime() int64 { return v.timeAt + int64(time.Since(v.learned).Seconds()) }

// request builds an authenticated (and encrypted) request.
func (v *V3) request(pdu byte, reqID int32, oid string) ([]byte, error) {
	o, err := encOID(oid)
	if err != nil {
		return nil, err
	}
	return v.message(pdu, reqID, concat(o, tlv(tagNull, nil)), v.boots, v.engineTime())
}

// message builds an SNMPv3 message carrying one variable (its OID and value, already encoded) in a PDU of this kind.
func (v *V3) message(pdu byte, reqID int32, varbind []byte, boots, etime int64) ([]byte, error) {
	p := tlv(pdu, concat(encInt(int64(reqID)), encInt(0), encInt(0), tlv(tagSequence, tlv(tagSequence, varbind))))
	scoped := tlv(tagSequence, concat(tlv(tagOctet, v.engineID), tlv(tagOctet, []byte(v.Context)), p))
	flags := v.flags()
	if pdu != pduGet && pdu != pduGetNext {
		flags &^= flagReportable
	}
	var privParams []byte
	data := scoped
	if flags&flagPriv != 0 {
		enc, salt, err := v.encrypt(scoped, boots, etime)
		if err != nil {
			return nil, err
		}
		privParams, data = salt, tlv(tagOctet, enc)
	}
	var authParams []byte
	if flags&flagAuth != 0 {
		authParams = make([]byte, v.macLen())
	}
	sp := tlv(tagSequence, concat(tlv(tagOctet, v.engineID), encInt(boots), encInt(etime), tlv(tagOctet, []byte(v.User)), tlv(tagOctet, authParams), tlv(tagOctet, privParams)))
	msg := v3Message(reqID, flags, sp, data)
	if flags&flagAuth != 0 {
		a, b, err := digestSpan(msg)
		if err != nil {
			return nil, err
		}
		copy(msg[a:b], v.mac(msg))
	}
	return msg, nil
}

// mac is the message's HMAC, computed with the digest field set to zeros.
func (v *V3) mac(msg []byte) []byte {
	m := hmac.New(v.hashFn(), v.authKey)
	m.Write(msg)
	return m.Sum(nil)[:v.macLen()]
}

// digestSpan is where the digest (msgAuthenticationParameters) sits in a whole SNMPv3 message.
func digestSpan(msg []byte) (int, int, error) {
	if _, _, err := (&reader{b: msg}).tlv(); err != nil {
		return 0, 0, err
	}
	// walk inside the outer sequence by hand so offsets stay in msg
	o := &reader{b: msg}
	o.p = headerLen(msg, 0)
	o.tlv()                              // version
	o.tlv()                              // global data
	spStart := o.p + headerLen(msg, o.p) // start of the security parameters' sequence
	s := &reader{b: msg, p: spStart + headerLen(msg, spStart)}
	for i := 0; i < 4; i++ {
		if _, _, err := s.tlv(); err != nil {
			return 0, 0, err
		}
	}
	at := s.p
	_, d, err := s.tlv()
	if err != nil {
		return 0, 0, err
	}
	start := at + headerLen(msg, at)
	return start, start + len(d), nil
}

// headerLen is the size of the tag and length bytes of the TLV starting at p.
func headerLen(b []byte, p int) int {
	if p+1 >= len(b) {
		return 2
	}
	if l := b[p+1]; l&0x80 != 0 {
		return 2 + int(l&0x7f)
	}
	return 2
}

func (v *V3) encrypt(plain []byte, boots, etime int64) (out, salt []byte, err error) {
	v.salt++
	switch strings.ToUpper(v.PrivProto) {
	case "DES":
		if len(v.privKey) < 16 {
			return nil, nil, errors.New("snmp: the privacy key is too short for DES")
		}
		salt = make([]byte, 8)
		binary.BigEndian.PutUint32(salt[:4], uint32(boots))
		binary.BigEndian.PutUint32(salt[4:], uint32(v.salt))
		iv := make([]byte, 8)
		for i := range iv {
			iv[i] = v.privKey[8+i] ^ salt[i]
		}
		blk, err := des.NewCipher(v.privKey[:8]) //nolint:gosec
		if err != nil {
			return nil, nil, err
		}
		pad := plain
		if r := len(pad) % 8; r != 0 {
			pad = append(append([]byte{}, pad...), make([]byte, 8-r)...)
		}
		out = make([]byte, len(pad))
		cipher.NewCBCEncrypter(blk, iv).CryptBlocks(out, pad)
		return out, salt, nil
	default: // AES-128-CFB
		salt = make([]byte, 8)
		binary.BigEndian.PutUint64(salt, v.salt)
		blk, err := aes.NewCipher(v.privKey[:16])
		if err != nil {
			return nil, nil, err
		}
		out = make([]byte, len(plain))
		cipher.NewCFBEncrypter(blk, aesIV(boots, etime, salt)).XORKeyStream(out, plain) //nolint:staticcheck // RFC 3826 requires CFB
		return out, salt, nil
	}
}

func aesIV(boots, etime int64, salt []byte) []byte {
	iv := make([]byte, 16)
	binary.BigEndian.PutUint32(iv[:4], uint32(boots))
	binary.BigEndian.PutUint32(iv[4:8], uint32(etime))
	copy(iv[8:], salt)
	return iv
}

func (v *V3) decrypt(enc, salt []byte, boots, etime int64) ([]byte, error) {
	if len(salt) != 8 {
		return nil, errors.New("snmp: bad privacy parameters in the answer")
	}
	switch strings.ToUpper(v.PrivProto) {
	case "DES":
		if len(enc)%8 != 0 {
			return nil, errors.New("snmp: bad DES data in the answer")
		}
		iv := make([]byte, 8)
		for i := range iv {
			iv[i] = v.privKey[8+i] ^ salt[i]
		}
		blk, err := des.NewCipher(v.privKey[:8]) //nolint:gosec
		if err != nil {
			return nil, err
		}
		out := make([]byte, len(enc))
		cipher.NewCBCDecrypter(blk, iv).CryptBlocks(out, enc)
		return out, nil
	default:
		blk, err := aes.NewCipher(v.privKey[:16])
		if err != nil {
			return nil, err
		}
		out := make([]byte, len(enc))
		cipher.NewCFBDecrypter(blk, aesIV(boots, etime, salt)).XORKeyStream(out, enc) //nolint:staticcheck
		return out, nil
	}
}

// v3Answer is what came back: either a response PDU or a report naming a problem.
type v3Answer struct {
	msgID       int32
	engineID    []byte
	boots, time int64
	pdu         []byte // the PDU's own TLV
	pduTag      byte
}

func parseV3(b []byte, v *V3) (*v3Answer, error) {
	r := &reader{b: b}
	tag, msg, err := r.tlv()
	if err != nil || tag != tagSequence {
		return nil, errors.New("snmp: not an SNMP answer")
	}
	m := &reader{b: msg}
	if _, ver, err := m.tlv(); err != nil || decInt(ver) != 3 {
		return nil, errors.New("snmp: the answer is not SNMPv3")
	}
	_, global, err := m.tlv()
	if err != nil {
		return nil, err
	}
	g := &reader{b: global}
	_, id, _ := g.tlv()
	g.tlv()
	_, fl, err := g.tlv()
	if err != nil || len(fl) != 1 {
		return nil, errors.New("snmp: bad flags in the answer")
	}
	_, spRaw, err := m.tlv()
	if err != nil {
		return nil, err
	}
	sr := &reader{b: spRaw}
	_, sp, err := sr.tlv()
	if err != nil {
		return nil, err
	}
	s := &reader{b: sp}
	_, eng, _ := s.tlv()
	_, bo, _ := s.tlv()
	_, ti, _ := s.tlv()
	s.tlv() // user
	s.tlv() // digest
	_, priv, err := s.tlv()
	if err != nil {
		return nil, err
	}
	a := &v3Answer{msgID: int32(decInt(id)), engineID: append([]byte{}, eng...), boots: decInt(bo), time: decInt(ti)}
	dt, data, err := m.tlv()
	if err != nil {
		return nil, err
	}
	scoped := data
	if fl[0]&flagPriv != 0 {
		if dt != tagOctet || v == nil {
			return nil, errors.New("snmp: encrypted answer that cannot be read")
		}
		plain, err := v.decrypt(data, priv, a.boots, a.time)
		if err != nil {
			return nil, err
		}
		pr := &reader{b: plain}
		if _, scoped, err = pr.tlv(); err != nil {
			return nil, errors.New("snmp: " + reportText[oidDecryptErr])
		}
	}
	sc := &reader{b: scoped}
	sc.tlv() // context engine id
	sc.tlv() // context name
	start := sc.p
	pt, _, err := sc.tlv()
	if err != nil {
		return nil, err
	}
	a.pdu, a.pduTag = scoped[start:sc.p], pt
	return a, nil
}

// reportOID is the counter a report PDU names (for example usmStatsUnknownUserNames).
func reportOID(pdu []byte) string {
	_, _, oid, _, err := parsePDU(pdu)
	if err != nil {
		return ""
	}
	return oid
}

// discover learns the engine ID, boots and time, and localizes the keys to that engine.
func (v *V3) discover(send func([]byte) ([]byte, error)) error {
	id := int32(randInt31())
	ans, err := send(discoveryRequest(id))
	if err != nil {
		return err
	}
	a, err := parseV3(ans, nil)
	if err != nil {
		return err
	}
	if len(a.engineID) == 0 {
		return errors.New("snmp: the device did not tell its SNMPv3 engine ID")
	}
	v.engineID, v.boots, v.timeAt, v.learned = a.engineID, a.boots, a.time, time.Now()
	if h := v.hashFn(); h != nil {
		v.authKey = PasswordToKey(h, v.AuthPass, v.engineID)
		if v.flags()&flagPriv != 0 {
			v.privKey = PasswordToKey(h, v.PrivPass, v.engineID)
			if len(v.privKey) < 16 { // never for MD5, SHA or SHA-256, all at least 16 bytes
				return errors.New("snmp: the privacy key is too short")
			}
		}
	} else if v.AuthProto != "" {
		return fmt.Errorf("snmp: unknown authentication protocol %q (use MD5, SHA or SHA256)", v.AuthProto)
	}
	if b := make([]byte, 8); true {
		_, _ = rand.Read(b)
		v.salt = binary.BigEndian.Uint64(b)
	}
	return nil
}
