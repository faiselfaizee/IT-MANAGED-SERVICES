// Package ldap is a small LDAPv3 client built on the standard library only (the agent has no outside packages).
// It does what directory management needs: simple bind, search with paging, modify, add, delete and rename, over LDAPS or plain LDAP.
package ldap

import (
	"bufio"
	"errors"
	"io"
)

// BER encoding: single-byte tags only, which is all LDAP uses.

func encLen(n int) []byte {
	if n < 128 {
		return []byte{byte(n)}
	}
	var b []byte
	for x := n; x > 0; x >>= 8 {
		b = append([]byte{byte(x)}, b...)
	}
	return append([]byte{0x80 | byte(len(b))}, b...)
}

func enc(tag byte, parts ...[]byte) []byte {
	n := 0
	for _, p := range parts {
		n += len(p)
	}
	out := make([]byte, 0, n+6)
	out = append(out, tag)
	out = append(out, encLen(n)...)
	for _, p := range parts {
		out = append(out, p...)
	}
	return out
}

func encInt(tag byte, v int) []byte {
	var b []byte
	x := v
	for {
		b = append([]byte{byte(x)}, b...)
		x >>= 8
		if (x == 0 && b[0]&0x80 == 0) || (x == -1 && b[0]&0x80 != 0) {
			break
		}
	}
	return enc(tag, b)
}

func encBool(v bool) []byte {
	if v {
		return enc(0x01, []byte{0xff})
	}
	return enc(0x01, []byte{0x00})
}

func octet(s string) []byte { return enc(0x04, []byte(s)) }

type elem struct {
	tag byte
	val []byte
}

// next splits one element off the front of b.
func next(b []byte) (elem, []byte, error) {
	if len(b) < 2 {
		return elem{}, nil, errors.New("ldap: short element")
	}
	tag, l := b[0], int(b[1])
	b = b[2:]
	if l&0x80 != 0 {
		k := l & 0x7f
		if k == 0 || k > 4 || len(b) < k {
			return elem{}, nil, errors.New("ldap: bad length")
		}
		l = 0
		for i := 0; i < k; i++ {
			l = l<<8 | int(b[i])
		}
		b = b[k:]
	}
	if l < 0 || l > len(b) {
		return elem{}, nil, errors.New("ldap: element longer than its message")
	}
	return elem{tag, b[:l]}, b[l:], nil
}

func children(b []byte) ([]elem, error) {
	var out []elem
	for len(b) > 0 {
		e, rest, err := next(b)
		if err != nil {
			return nil, err
		}
		out = append(out, e)
		b = rest
	}
	return out, nil
}

func decInt(b []byte) int {
	if len(b) == 0 {
		return 0
	}
	v := 0
	if b[0]&0x80 != 0 {
		v = -1
	}
	for _, c := range b {
		v = v<<8 | int(c)
	}
	return v
}

// readMessage reads one whole LDAPMessage (a SEQUENCE) from the wire.
func readMessage(r *bufio.Reader) ([]byte, error) {
	tag, err := r.ReadByte()
	if err != nil {
		return nil, err
	}
	if tag != 0x30 {
		return nil, errors.New("ldap: the server did not answer in LDAP")
	}
	l, err := r.ReadByte()
	if err != nil {
		return nil, err
	}
	n := int(l)
	if l&0x80 != 0 {
		k := int(l & 0x7f)
		if k == 0 || k > 4 {
			return nil, errors.New("ldap: bad length")
		}
		n = 0
		for i := 0; i < k; i++ {
			c, err := r.ReadByte()
			if err != nil {
				return nil, err
			}
			n = n<<8 | int(c)
		}
	}
	if n < 0 || n > 64<<20 {
		return nil, errors.New("ldap: message too large")
	}
	buf := make([]byte, n)
	if _, err := io.ReadFull(r, buf); err != nil {
		return nil, err
	}
	return buf, nil
}
