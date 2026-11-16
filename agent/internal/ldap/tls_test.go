package ldap

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"errors"
	"math/big"
	"net"
	"testing"
	"time"
)

func selfSigned(t *testing.T, names ...string) tls.Certificate {
	k, _ := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	tpl := &x509.Certificate{SerialNumber: big.NewInt(7), Subject: pkix.Name{CommonName: names[0]}, DNSNames: names,
		NotBefore: time.Now().Add(-time.Hour), NotAfter: time.Now().Add(time.Hour), KeyUsage: x509.KeyUsageDigitalSignature, ExtKeyUsage: []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth}}
	der, err := x509.CreateCertificate(rand.Reader, tpl, tpl, &k.PublicKey, k)
	if err != nil {
		t.Fatal(err)
	}
	return tls.Certificate{Certificate: [][]byte{der}, PrivateKey: k}
}

// a listener that does TLS with cert, or (cert == nil) accepts and closes at once like a DC without a certificate
func listen(t *testing.T, cert *tls.Certificate) string {
	l, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { l.Close() })
	go func() {
		for {
			c, err := l.Accept()
			if err != nil {
				return
			}
			if cert == nil {
				buf := make([]byte, 64)
				_, _ = c.Read(buf)
				c.Close()
				continue
			}
			tc := tls.Server(c, &tls.Config{Certificates: []tls.Certificate{*cert}})
			go func() { _ = tc.Handshake(); time.Sleep(200 * time.Millisecond); tc.Close() }()
		}
	}()
	return l.Addr().String()
}

func kind(t *testing.T, err error) *TLSError {
	var te *TLSError
	if !errors.As(err, &te) {
		t.Fatalf("want a TLSError, got %v", err)
	}
	return te
}

func TestLDAPSFailuresAreExplained(t *testing.T) {
	// port open, no certificate: the DC drops the handshake
	_, err := DialWith(listen(t, nil), Options{TLS: true, Timeout: 3 * time.Second})
	if k := kind(t, err); k.Kind != "no_certificate" {
		t.Fatalf("no certificate: %s %s", k.Kind, k.Message)
	}

	cert := selfSigned(t, "dc01.corp.test")
	addr := listen(t, &cert)
	// 127.0.0.1 is not a name on the certificate (Go checks the name first only when the issuer is trusted, so either reason is fair)
	te := kind(t, func() error { _, e := DialWith(addr, Options{TLS: true, Timeout: 3 * time.Second}); return e }())
	if te.Kind != "untrusted" && te.Kind != "name_mismatch" {
		t.Fatalf("self-signed: %s %s", te.Kind, te.Message)
	}
	if te.Cert == nil || te.Cert.Names[0] != "dc01.corp.test" || len(te.Cert.SHA256) != 64 {
		t.Fatalf("certificate details missing: %+v", te.Cert)
	}

	// trusting exactly that certificate works, whatever its name and issuer
	c, err := DialWith(addr, Options{TLS: true, PinSHA256: te.Cert.SHA256, Timeout: 3 * time.Second})
	if err != nil {
		t.Fatalf("pinned: %v", err)
	}
	c.c.Close()

	// a different certificate is refused and shown
	other := selfSigned(t, "dc01.corp.test")
	_, err = DialWith(listen(t, &other), Options{TLS: true, PinSHA256: te.Cert.SHA256, Timeout: 3 * time.Second})
	if k := kind(t, err); k.Kind != "pin_changed" || k.Cert == nil || k.Cert.SHA256 == te.Cert.SHA256 {
		t.Fatalf("changed certificate: %+v", k)
	}
}
