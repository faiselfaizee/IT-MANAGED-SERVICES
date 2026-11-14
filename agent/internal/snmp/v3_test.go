package snmp

import (
	"crypto/md5"  //nolint:gosec
	"crypto/sha1" //nolint:gosec
	"encoding/hex"
	"strings"
	"testing"
	"time"
)

// RFC 3414 appendix A.3: password "maplesyrup", engine ID 00..02.
func TestPasswordToKeyMatchesRFC3414(t *testing.T) {
	eng, _ := hex.DecodeString("000000000000000000000002")
	if got := hex.EncodeToString(PasswordToKey(md5.New, "maplesyrup", eng)); got != "526f5eed9fcce26f8964c2930787d82b" {
		t.Errorf("MD5 key = %s", got)
	}
	if got := hex.EncodeToString(PasswordToKey(sha1.New, "maplesyrup", eng)); got != "6695febc9288e36282235fc7151f128497b38f3f" {
		t.Errorf("SHA key = %s", got)
	}
}

func v3vals() map[string]Value {
	return map[string]Value{
		"1.3.6.1.2.1.1.1.0": Str("FortiGate-60F v7.4.3"), "1.3.6.1.2.1.1.5.0": Str("FGT-HQ"),
		"1.3.6.1.2.1.2.2.1.2.1": Str("wan1"), "1.3.6.1.2.1.2.2.1.2.2": Str("wan2"), "1.3.6.1.2.1.2.2.1.2.3": Str("internal"),
	}
}

func TestSNMPv3ReadsWithEveryProtocolPair(t *testing.T) {
	eng, _ := hex.DecodeString("80003044034647540001")
	for _, c := range []struct{ auth, priv string }{{"SHA", "AES"}, {"MD5", "DES"}, {"SHA256", "AES"}, {"SHA", ""}, {"MD5", "AES"}} {
		user := &V3{User: "deskask", AuthProto: c.auth, AuthPass: "auth-pass-123", PrivProto: c.priv, PrivPass: "priv-pass-456"}
		addr, stop := FakeDeviceV3(v3vals(), user, eng, 12)
		cl := &Client{Addr: addr, Timeout: time.Second, V3: &V3{User: "deskask", AuthProto: c.auth, AuthPass: "auth-pass-123", PrivProto: c.priv, PrivPass: "priv-pass-456"}}
		v, err := cl.Get("1.3.6.1.2.1.1.5.0")
		if err != nil || v.Text() != "FGT-HQ" {
			t.Errorf("%s/%s: %q %v", c.auth, c.priv, v.Text(), err)
		}
		rows, err := cl.Walk("1.3.6.1.2.1.2.2.1.2", 10)
		if err != nil || len(rows) != 3 || rows[2].Value.Text() != "internal" {
			t.Errorf("%s/%s walk: %v %v", c.auth, c.priv, rows, err)
		}
		stop()
	}
}

func TestSNMPv3ExplainsWhatIsWrong(t *testing.T) {
	eng, _ := hex.DecodeString("80003044034647540001")
	user := &V3{User: "deskask", AuthProto: "SHA", AuthPass: "auth-pass-123", PrivProto: "AES", PrivPass: "priv-pass-456"}
	addr, stop := FakeDeviceV3(v3vals(), user, eng, 12)
	defer stop()
	for _, c := range []struct {
		v    *V3
		want string
	}{
		{&V3{User: "nobody", AuthProto: "SHA", AuthPass: "auth-pass-123", PrivProto: "AES", PrivPass: "priv-pass-456"}, "user name"},
		{&V3{User: "deskask", AuthProto: "SHA", AuthPass: "wrong-pass-1", PrivProto: "AES", PrivPass: "priv-pass-456"}, "authentication password"},
		{&V3{User: "deskask", AuthProto: "SHA", AuthPass: "auth-pass-123", PrivProto: "AES", PrivPass: "wrong-priv-9"}, "privacy password"},
	} {
		_, err := (&Client{Addr: addr, Timeout: time.Second, V3: c.v}).Get("1.3.6.1.2.1.1.5.0")
		if err == nil || !strings.Contains(err.Error(), c.want) {
			t.Errorf("want %q, got %v", c.want, err)
		}
	}
	// a client whose idea of the device's clock is far off follows the clock window report
	v := &V3{User: "deskask", AuthProto: "SHA", AuthPass: "auth-pass-123", PrivProto: "AES", PrivPass: "priv-pass-456"}
	cl := &Client{Addr: addr, Timeout: time.Second, V3: v}
	if _, err := cl.Get("1.3.6.1.2.1.1.5.0"); err != nil {
		t.Fatal(err)
	}
	v.timeAt -= 5000
	if x, err := cl.Get("1.3.6.1.2.1.1.5.0"); err != nil || x.Text() != "FGT-HQ" {
		t.Fatalf("after clock drift: %v %v", x, err)
	}
}
