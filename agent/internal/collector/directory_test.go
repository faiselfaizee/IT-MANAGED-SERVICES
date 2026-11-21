package collector

import (
	"encoding/json"
	"errors"
	"net"
	"testing"
	"time"

	"github.com/deskask/platform/agent/internal/ldap"
)

func TestGuidSidAndPassword(t *testing.T) {
	// objectGUID is stored with the first three groups little-endian
	g := []byte{0x78, 0x56, 0x34, 0x12, 0xbc, 0x9a, 0xf0, 0xde, 0x12, 0x34, 0x56, 0x78, 0x9a, 0xbc, 0xde, 0xf0}
	if got := guidString(g); got != "12345678-9abc-def0-1234-56789abcdef0" {
		t.Fatalf("guid %s", got)
	}
	// S-1-5-21-1-2-3-500
	sid := []byte{1, 5, 0, 0, 0, 0, 0, 5, 21, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0, 0xf4, 1, 0, 0}
	if got := sidString(sid); got != "S-1-5-21-1-2-3-500" {
		t.Fatalf("sid %s", got)
	}
	if got := pwdValue("Ab1"); len(got) != 10 || got[0] != '"' || got[1] != 0 || got[2] != 'A' {
		t.Fatalf("password bytes %v", got)
	}
	if v := text("objectGUID", [][]byte{g}); v[0] != "12345678-9abc-def0-1234-56789abcdef0" {
		t.Fatalf("text guid %v", v)
	}
	if v := text("x", [][]byte{{0xff, 0xfe}}); v[0] != "base64://4=" {
		t.Fatalf("binary %v", v)
	}
}

func TestLdapAddr(t *testing.T) {
	for in, want := range map[string]string{"dc1": "dc1:389", "dc1:3269": "dc1:3269", "10.0.0.5": "10.0.0.5:389", "[::1]:636": "[::1]:636", "fe80::1": "[fe80::1]:389"} {
		if got := ldapAddr(in, false); got != want {
			t.Errorf("%s: %s want %s", in, got, want)
		}
	}
	if ldapAddr("dc1", true) != "dc1:636" {
		t.Error("LDAPS defaults to 636")
	}
}

func TestJobErrors(t *testing.T) {
	_, err := doDirectory(json.RawMessage(`{"conn":{"servers":[]},"ops":[{"op":"rootdse"}]}`))
	var de *dirError
	if !errors.As(err, &de) {
		t.Fatalf("no servers should be a readable error: %v", err)
	}
	if _, err := doDirectory(json.RawMessage(`{"conn":{"servers":["127.0.0.1:1"]},"ops":[]}`)); err == nil {
		t.Fatal("a job without steps is refused")
	}
	// nothing listens on port 1: the answer names the server
	_, err = doDirectory(json.RawMessage(`{"conn":{"servers":["127.0.0.1:1"]},"ops":[{"op":"rootdse"}]}`))
	if !errors.As(err, &de) || de.Message == "" {
		t.Fatalf("unreachable server: %v", err)
	}
}

func TestVerifyChecksThePersonsOwnPassword(t *testing.T) {
	old := dial
	defer func() { dial = old }()
	var asked dirConn
	dial = func(c dirConn, _ time.Duration) (*ldap.Conn, string, error) {
		asked = c
		if c.Password == "Right-Pass-1" {
			a, b := net.Pipe()
			go func() { _ = b.Close() }()
			return ldap.NewConn(a), c.Servers[0], nil
		}
		return nil, c.Servers[0], &dirError{Code: ldap.CodeInvalidCreds, Message: "the domain controller refused: 80090308: LdapErr: DSID-0C09044E, comment: AcceptSecurityContext error, data 775, v4563"}
	}
	conn := dirConn{Servers: []string{"dc01:636", "dc02:636"}, TLS: true, BindDN: "svc@acme", Password: "svc-secret"}
	r, err := verify("dc02:636", conn, dirOp{Op: "verify", DN: "CN=Ana,DC=acme", Password: "Right-Pass-1"})
	if err != nil || r.Valid == nil || !*r.Valid {
		t.Fatalf("a right password: %+v %v", r, err)
	}
	if asked.BindDN != "CN=Ana,DC=acme" || len(asked.Servers) != 1 || asked.Servers[0] != "dc02:636" || !asked.TLS {
		t.Fatalf("must bind as the person on the controller that answered, keeping TLS: %+v", asked)
	}
	r, err = verify("dc02:636", conn, dirOp{Op: "verify", DN: "CN=Ana,DC=acme", Password: "nope"})
	if err != nil || r.Valid == nil || *r.Valid || r.Reason != "locked" {
		t.Fatalf("a locked account: %+v %v", r, err)
	}
	if _, err := verify("dc02:636", conn, dirOp{Op: "verify", DN: "CN=Ana,DC=acme", Password: ""}); err == nil {
		t.Fatal("an empty password must never be tried (it would be an anonymous bind)")
	}
}
