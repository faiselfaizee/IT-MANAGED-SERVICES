package ldap

import (
	"bufio"
	"crypto/tls"
	"errors"
	"fmt"
	"net"
	"time"
)

// Scopes of a search.
const (
	ScopeBase    = 0
	ScopeOne     = 1
	ScopeSubtree = 2
)

// Operations of a modify.
const (
	ModAdd     = 0
	ModDelete  = 1
	ModReplace = 2
)

// Result codes people meet.
const (
	CodeSizeLimit       = 4
	CodeNoSuchObject    = 32
	CodeInvalidCreds    = 49
	CodeInsufficient    = 50
	CodeUnwilling       = 53
	CodeAlreadyExists   = 68
	CodeConstraint      = 19
	CodeNoSuchAttribute = 16
)

const pagedOID = "1.2.840.113556.1.4.319"

// ResultError is a refusal from the directory.
type ResultError struct {
	Code    int
	Message string
}

func (e *ResultError) Error() string {
	if e.Message != "" {
		return fmt.Sprintf("ldap result %d: %s", e.Code, e.Message)
	}
	return fmt.Sprintf("ldap result %d", e.Code)
}

// Entry is one object found by a search; attribute values are raw bytes (objectGUID and objectSid are binary).
type Entry struct {
	DN    string
	Attrs map[string][][]byte
}

// Change is one part of a modify.
type Change struct {
	Op   int
	Attr string
	Vals [][]byte
}

// SearchReq describes a search. PageSize 0 asks for everything in one answer; Max stops after that many entries (0 = 1000).
type SearchReq struct {
	Base     string
	Scope    int
	Filter   string
	Attrs    []string
	PageSize int
	Max      int
}

// Conn is one connection. It is not safe for use from several goroutines at once.
type Conn struct {
	c       net.Conn
	r       *bufio.Reader
	id      int
	Timeout time.Duration
}

// Dial opens a connection. addr is host:port; useTLS selects LDAPS.
func Dial(addr string, useTLS, skipVerify bool, timeout time.Duration) (*Conn, error) {
	if timeout <= 0 {
		timeout = 30 * time.Second
	}
	d := net.Dialer{Timeout: timeout}
	var c net.Conn
	var err error
	if useTLS {
		c, err = tls.DialWithDialer(&d, "tcp", addr, &tls.Config{InsecureSkipVerify: skipVerify, MinVersion: tls.VersionTLS12})
	} else {
		c, err = d.Dial("tcp", addr)
	}
	if err != nil {
		return nil, err
	}
	return &Conn{c: c, r: bufio.NewReader(c), Timeout: timeout}, nil
}

// NewConn wraps an open connection (tests).
func NewConn(c net.Conn) *Conn { return &Conn{c: c, r: bufio.NewReader(c), Timeout: 10 * time.Second} }

func (c *Conn) Close() error {
	_ = c.c.SetDeadline(time.Now().Add(2 * time.Second))
	c.id++
	_, _ = c.c.Write(enc(0x30, encInt(0x02, c.id), enc(0x42)))
	return c.c.Close()
}

func (c *Conn) send(op []byte, controls []byte) (int, error) {
	c.id++
	msg := enc(0x30, encInt(0x02, c.id), op, controls)
	_ = c.c.SetDeadline(time.Now().Add(c.Timeout))
	_, err := c.c.Write(msg)
	return c.id, err
}

type message struct {
	id       int
	op       elem
	controls []elem
}

func (c *Conn) recv() (message, error) {
	_ = c.c.SetDeadline(time.Now().Add(c.Timeout))
	raw, err := readMessage(c.r)
	if err != nil {
		return message{}, err
	}
	parts, err := children(raw)
	if err != nil || len(parts) < 2 {
		return message{}, errors.New("ldap: unreadable answer")
	}
	m := message{id: decInt(parts[0].val), op: parts[1]}
	if len(parts) > 2 && parts[2].tag == 0xa0 {
		m.controls, _ = children(parts[2].val)
	}
	return m, nil
}

// result reads an LDAPResult out of an operation element.
func result(op elem) error {
	p, err := children(op.val)
	if err != nil || len(p) < 3 {
		return errors.New("ldap: unreadable result")
	}
	if code := decInt(p[0].val); code != 0 {
		return &ResultError{Code: code, Message: string(p[2].val)}
	}
	return nil
}

// roundTrip sends one request and waits for its single answer (bind, modify, add, delete, rename).
func (c *Conn) roundTrip(op []byte, want byte) error {
	id, err := c.send(op, nil)
	if err != nil {
		return err
	}
	for {
		m, err := c.recv()
		if err != nil {
			return err
		}
		if m.id != id {
			continue
		}
		if m.op.tag != want {
			return fmt.Errorf("ldap: unexpected answer %#x", m.op.tag)
		}
		return result(m.op)
	}
}

// Bind signs in with a distinguished name (or user principal name) and password.
func (c *Conn) Bind(dn, password string) error {
	return c.roundTrip(enc(0x60, encInt(0x02, 3), octet(dn), enc(0x80, []byte(password))), 0x61)
}

func pagedControl(size int, cookie []byte) []byte {
	val := enc(0x30, encInt(0x02, size), enc(0x04, cookie))
	return enc(0xa0, enc(0x30, octet(pagedOID), encBool(false), enc(0x04, val)))
}

// Search returns the entries found and whether the list was cut short (by Max or the server's size limit).
func (c *Conn) Search(q SearchReq) ([]Entry, bool, error) {
	filter, err := CompileFilter(q.Filter)
	if err != nil {
		return nil, false, err
	}
	max := q.Max
	if max <= 0 {
		max = 1000
	}
	var attrs [][]byte
	for _, a := range q.Attrs {
		attrs = append(attrs, octet(a))
	}
	var out []Entry
	var cookie []byte
	for {
		op := enc(0x63, octet(q.Base), encInt(0x0a, q.Scope), encInt(0x0a, 0), encInt(0x02, 0), encInt(0x02, 0), encBool(false), filter, enc(0x30, attrs...))
		var ctl []byte
		if q.PageSize > 0 {
			ctl = pagedControl(q.PageSize, cookie)
		}
		id, err := c.send(op, ctl)
		if err != nil {
			return out, false, err
		}
		var done *message
		for done == nil {
			m, err := c.recv()
			if err != nil {
				return out, false, err
			}
			if m.id != id {
				continue
			}
			switch m.op.tag {
			case 0x64:
				p, err := children(m.op.val)
				if err != nil || len(p) < 2 {
					return out, false, errors.New("ldap: unreadable entry")
				}
				e := Entry{DN: string(p[0].val), Attrs: map[string][][]byte{}}
				list, _ := children(p[1].val)
				for _, at := range list {
					ap, err := children(at.val)
					if err != nil || len(ap) < 2 {
						continue
					}
					vals, _ := children(ap[1].val)
					name := string(ap[0].val)
					for _, v := range vals {
						e.Attrs[name] = append(e.Attrs[name], append([]byte(nil), v.val...))
					}
					if len(vals) == 0 {
						e.Attrs[name] = nil
					}
				}
				if len(out) < max {
					out = append(out, e)
				}
			case 0x73: // a referral to another server: not followed
			case 0x65:
				mm := m
				done = &mm
			default:
				return out, false, fmt.Errorf("ldap: unexpected answer %#x", m.op.tag)
			}
		}
		if err := result(done.op); err != nil {
			var re *ResultError
			if errors.As(err, &re) && re.Code == CodeSizeLimit {
				return out, true, nil
			}
			return out, false, err
		}
		if len(out) >= max {
			return out, true, nil
		}
		cookie = nil
		for _, ct := range done.controls {
			p, _ := children(ct.val)
			if len(p) >= 2 && string(p[0].val) == pagedOID {
				last := p[len(p)-1]
				inner, _ := children(last.val)
				if len(inner) == 1 {
					v, _ := children(inner[0].val)
					if len(v) >= 2 {
						cookie = v[1].val
					}
				}
			}
		}
		if q.PageSize == 0 || len(cookie) == 0 {
			return out, false, nil
		}
	}
}

func attrSeq(name string, vals [][]byte) []byte {
	var v [][]byte
	for _, x := range vals {
		v = append(v, enc(0x04, x))
	}
	return enc(0x30, octet(name), enc(0x31, v...))
}

// Modify changes attributes of one object.
func (c *Conn) Modify(dn string, changes []Change) error {
	var list [][]byte
	for _, ch := range changes {
		list = append(list, enc(0x30, encInt(0x0a, ch.Op), attrSeq(ch.Attr, ch.Vals)))
	}
	return c.roundTrip(enc(0x66, octet(dn), enc(0x30, list...)), 0x67)
}

// Add creates an object.
func (c *Conn) Add(dn string, attrs map[string][][]byte) error {
	var list [][]byte
	for n, v := range attrs {
		list = append(list, attrSeq(n, v))
	}
	return c.roundTrip(enc(0x68, octet(dn), enc(0x30, list...)), 0x69)
}

// Delete removes an object.
func (c *Conn) Delete(dn string) error {
	return c.roundTrip(enc(0x4a, []byte(dn)), 0x6b)
}

// Rename changes the name of an object and, when newParent is not empty, moves it there.
func (c *Conn) Rename(dn, newRDN, newParent string) error {
	parts := [][]byte{octet(dn), octet(newRDN), encBool(true)}
	if newParent != "" {
		parts = append(parts, enc(0x80, []byte(newParent)))
	}
	return c.roundTrip(enc(0x6c, parts...), 0x6d)
}
