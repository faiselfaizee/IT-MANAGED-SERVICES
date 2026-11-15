package ldap

import (
	"bufio"
	"bytes"
	"encoding/hex"
	"errors"
	"net"
	"strings"
	"sync"
	"testing"
)

func hx(b []byte) string { return hex.EncodeToString(b) }

func TestFilterBytes(t *testing.T) {
	for f, want := range map[string]string{
		"(cn=a)":        "a3070402636e040161",
		"(cn=*)":        "8702636e",
		"(&(a=b)(c=d))": "a010a306040161040162a306040163040164",
		"(!(cn=a))":     "a209a3070402636e040161",
		"(cn=jo*)":      "a40a0402636e30048002 6a6f",
		"(a>=5)":        "a5060401610401 35",
		"(a=\\2a)":      "a3060401610401 2a",
	} {
		want = strings.ReplaceAll(want, " ", "")
		got, err := CompileFilter(f)
		if err != nil {
			t.Fatalf("%s: %v", f, err)
		}
		if hx(got) != want {
			t.Errorf("%s: got %s want %s", f, hx(got), want)
		}
	}
	for _, bad := range []string{"(cn=a", "(&)", "(cn)", "(cn=\\zz)", "((cn=a))", "(cn=a))"} {
		if _, err := CompileFilter(bad); err == nil {
			t.Errorf("%s should not compile", bad)
		}
	}
	if got, _ := CompileFilter("cn=a"); hx(got) != "a3070402636e040161" {
		t.Errorf("a filter without brackets should work")
	}
}

func TestEscape(t *testing.T) {
	if got := Escape("a*(b)\\"); got != `a\2a\28b\29\5c` {
		t.Fatalf("got %s", got)
	}
}

func TestIntegers(t *testing.T) {
	for _, v := range []int{0, 1, 127, 128, 255, 256, 65535, 1 << 20, -1, -128, -129} {
		e, _, err := next(encInt(0x02, v))
		if err != nil || decInt(e.val) != v {
			t.Fatalf("%d came back as %d (%v)", v, decInt(e.val), err)
		}
	}
	long := bytes.Repeat([]byte("x"), 70000)
	e, rest, err := next(enc(0x04, long))
	if err != nil || len(rest) != 0 || len(e.val) != 70000 {
		t.Fatalf("long element: %v", err)
	}
}

// fakeServer answers the way a directory would, enough for the client's paths.
type fakeServer struct {
	mu   sync.Mutex
	seen []string
}

func (f *fakeServer) note(s string) { f.mu.Lock(); f.seen = append(f.seen, s); f.mu.Unlock() }

func res(tag byte, code int, diag string) []byte {
	return enc(tag, encInt(0x0a, code), octet(""), octet(diag))
}
func msg(id int, op []byte, ctl ...[]byte) []byte {
	return enc(0x30, append([][]byte{encInt(0x02, id), op}, ctl...)...)
}
func entry(dn string, attrs map[string][]string) []byte {
	var list [][]byte
	for n, vs := range attrs {
		var v [][]byte
		for _, x := range vs {
			v = append(v, octet(x))
		}
		list = append(list, enc(0x30, octet(n), enc(0x31, v...)))
	}
	return enc(0x64, octet(dn), enc(0x30, list...))
}

func (f *fakeServer) serve(c net.Conn) {
	defer c.Close()
	r := bufio.NewReader(c)
	page := 0
	for {
		raw, err := readMessage(r)
		if err != nil {
			return
		}
		p, _ := children(raw)
		id := decInt(p[0].val)
		op := p[1]
		switch op.tag {
		case 0x60:
			b, _ := children(op.val)
			if string(b[1].val) == "cn=admin" && string(b[2].val) == "secret" {
				c.Write(msg(id, res(0x61, 0, "")))
			} else {
				c.Write(msg(id, res(0x61, 49, "bad password")))
			}
		case 0x63:
			s, _ := children(op.val)
			f.note("search " + string(s[0].val))
			paged := len(p) > 2
			page++
			c.Write(msg(id, entry("cn=u"+string(rune('0'+page))+",dc=x", map[string][]string{"cn": {"u"}, "objectGUID": {"\x01\x02"}})))
			c.Write(msg(id, entry("cn=v"+string(rune('0'+page))+",dc=x", map[string][]string{"cn": {"v"}})))
			c.Write(msg(id, enc(0x73, octet("ldap://elsewhere"))))
			var ctl []byte
			if paged {
				cookie := []byte("more")
				if page >= 2 {
					cookie = nil
				}
				ctl = enc(0xa0, enc(0x30, octet(pagedOID), enc(0x04, enc(0x30, encInt(0x02, 0), enc(0x04, cookie)))))
			}
			if ctl != nil {
				c.Write(msg(id, res(0x65, 0, ""), ctl))
			} else {
				c.Write(msg(id, res(0x65, 0, "")))
			}
		case 0x66:
			b, _ := children(op.val)
			ch, _ := children(b[1].val)
			f.note("modify " + string(b[0].val) + " changes=" + string(rune('0'+len(ch))))
			c.Write(msg(id, res(0x67, 0, "")))
		case 0x68:
			b, _ := children(op.val)
			f.note("add " + string(b[0].val))
			c.Write(msg(id, res(0x69, 68, "exists")))
		case 0x4a:
			f.note("delete " + string(op.val))
			c.Write(msg(id, res(0x6b, 0, "")))
		case 0x6c:
			b, _ := children(op.val)
			n := "rename " + string(b[0].val) + " -> " + string(b[1].val)
			if len(b) > 3 {
				n += " under " + string(b[3].val)
			}
			f.note(n)
			c.Write(msg(id, res(0x6d, 0, "")))
		case 0x42:
			return
		}
	}
}

func dial(t *testing.T) (*Conn, *fakeServer) {
	a, b := net.Pipe()
	fs := &fakeServer{}
	go fs.serve(b)
	return NewConn(a), fs
}

func TestBindSearchPagingAndWrites(t *testing.T) {
	c, fs := dial(t)
	defer c.Close()
	if err := c.Bind("cn=admin", "wrong"); err == nil {
		t.Fatal("a wrong password must fail")
	} else {
		var re *ResultError
		if !errors.As(err, &re) || re.Code != CodeInvalidCreds {
			t.Fatalf("want invalid credentials, got %v", err)
		}
	}
	if err := c.Bind("cn=admin", "secret"); err != nil {
		t.Fatal(err)
	}
	// two pages of two entries; the referral between them is skipped
	got, cut, err := c.Search(SearchReq{Base: "dc=x", Scope: ScopeSubtree, Filter: "(objectClass=user)", Attrs: []string{"cn"}, PageSize: 2})
	if err != nil || cut || len(got) != 4 {
		t.Fatalf("paged search: %d entries, cut=%v, %v", len(got), cut, err)
	}
	if string(got[0].Attrs["objectGUID"][0]) != "\x01\x02" || got[0].DN != "cn=u1,dc=x" {
		t.Fatalf("first entry wrong: %+v", got[0])
	}
	// Max cuts the list short
	c2, _ := dial(t)
	defer c2.Close()
	got, cut, err = c2.Search(SearchReq{Base: "dc=x", Filter: "(cn=*)", PageSize: 2, Max: 3})
	if err != nil || !cut || len(got) != 3 {
		t.Fatalf("max: %d entries, cut=%v, %v", len(got), cut, err)
	}
	if err := c.Modify("cn=a,dc=x", []Change{{Op: ModReplace, Attr: "mail", Vals: [][]byte{[]byte("a@x")}}, {Op: ModDelete, Attr: "pager"}}); err != nil {
		t.Fatal(err)
	}
	err = c.Add("cn=new,dc=x", map[string][][]byte{"cn": {[]byte("new")}})
	var re *ResultError
	if !errors.As(err, &re) || re.Code != CodeAlreadyExists {
		t.Fatalf("add should report an existing object: %v", err)
	}
	if err := c.Delete("cn=old,dc=x"); err != nil {
		t.Fatal(err)
	}
	if err := c.Rename("cn=a,dc=x", "cn=a", "ou=moved,dc=x"); err != nil {
		t.Fatal(err)
	}
	want := []string{"search dc=x", "search dc=x", "modify cn=a,dc=x changes=2", "add cn=new,dc=x", "delete cn=old,dc=x", "rename cn=a,dc=x -> cn=a under ou=moved,dc=x"}
	fs.mu.Lock()
	defer fs.mu.Unlock()
	if strings.Join(fs.seen, "|") != strings.Join(want, "|") {
		t.Fatalf("server saw:\n%v\nwant:\n%v", fs.seen, want)
	}
}
