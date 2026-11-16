package ldap

import (
	"crypto/sha256"
	"crypto/tls"
	"crypto/x509"
	"encoding/hex"
	"errors"
	"io"
	"net"
	"strings"
	"syscall"
	"time"
)

// Options says how to reach a directory server.
type Options struct {
	TLS        bool
	SkipVerify bool   // accept any certificate (not recommended)
	PinSHA256  string // accept exactly this certificate (hex SHA-256 of the DER), whatever its name or issuer
	Timeout    time.Duration
}

// CertInfo describes the certificate a domain controller showed, so a person can decide to trust it.
type CertInfo struct {
	Subject  string   `json:"subject"`
	Names    []string `json:"names"`
	Issuer   string   `json:"issuer"`
	NotAfter string   `json:"notAfter"`
	SHA256   string   `json:"sha256"`
}

// TLSError is an LDAPS connection that failed while setting up encryption, with the reason in plain words.
type TLSError struct {
	Kind    string    `json:"kind"` // no_certificate, name_mismatch, untrusted, expired, pin_changed, tls_version, other
	Message string    `json:"message"`
	Cert    *CertInfo `json:"cert,omitempty"`
	cause   error
}

func (e *TLSError) Error() string { return e.Message }
func (e *TLSError) Unwrap() error { return e.cause }

func Fingerprint(der []byte) string { s := sha256.Sum256(der); return hex.EncodeToString(s[:]) }

func info(c *x509.Certificate) *CertInfo {
	names := append([]string{}, c.DNSNames...)
	for _, ip := range c.IPAddresses {
		names = append(names, ip.String())
	}
	return &CertInfo{Subject: c.Subject.String(), Names: names, Issuer: c.Issuer.String(), NotAfter: c.NotAfter.UTC().Format(time.RFC3339), SHA256: Fingerprint(c.Raw)}
}

// DialWith opens a connection; for LDAPS a failed handshake comes back as a *TLSError that says why.
func DialWith(addr string, o Options) (*Conn, error) {
	if o.Timeout <= 0 {
		o.Timeout = 30 * time.Second
	}
	if !o.TLS {
		return Dial(addr, false, false, o.Timeout)
	}
	host, _, _ := net.SplitHostPort(addr)
	pin := strings.ToLower(strings.ReplaceAll(strings.TrimSpace(o.PinSHA256), ":", ""))
	cfg := &tls.Config{ServerName: host, MinVersion: tls.VersionTLS12, InsecureSkipVerify: o.SkipVerify || pin != ""}
	if pin != "" {
		cfg.VerifyPeerCertificate = func(raw [][]byte, _ [][]*x509.Certificate) error {
			if len(raw) > 0 && Fingerprint(raw[0]) == pin {
				return nil
			}
			return errPin
		}
	}
	d := net.Dialer{Timeout: o.Timeout}
	raw, err := d.Dial("tcp", addr)
	if err != nil {
		return nil, err // not reachable at all: the caller says so
	}
	tc := tls.Client(raw, cfg)
	_ = tc.SetDeadline(time.Now().Add(o.Timeout))
	if err := tc.Handshake(); err != nil {
		_ = raw.Close()
		return nil, explain(addr, host, err, o.Timeout)
	}
	_ = tc.SetDeadline(time.Time{})
	return NewConnTimeout(tc, o.Timeout), nil
}

var errPin = errors.New("certificate changed")

// NewConnTimeout wraps an open connection with the given timeout per request.
func NewConnTimeout(c net.Conn, t time.Duration) *Conn { cc := NewConn(c); cc.Timeout = t; return cc }

// peek reads the certificate without checking it, only to show it to a person.
func peek(addr, host string, timeout time.Duration) *CertInfo {
	d := net.Dialer{Timeout: timeout}
	c, err := tls.DialWithDialer(&d, "tcp", addr, &tls.Config{ServerName: host, InsecureSkipVerify: true, MinVersion: tls.VersionTLS10})
	if err != nil {
		return nil
	}
	defer c.Close()
	if s := c.ConnectionState().PeerCertificates; len(s) > 0 {
		return info(s[0])
	}
	return nil
}

func explain(addr, host string, err error, timeout time.Duration) error {
	var hn x509.HostnameError
	var ua x509.UnknownAuthorityError
	var ci x509.CertificateInvalidError
	var alert tls.AlertError
	var cv *tls.CertificateVerificationError
	switch {
	case errors.Is(err, errPin):
		return &TLSError{Kind: "pin_changed", cause: err, Cert: peek(addr, host, timeout),
			Message: addr + " showed a different certificate from the one you chose to trust. If the domain controller's certificate was renewed, check the new one and trust it again."}
	case errors.As(err, &hn):
		return &TLSError{Kind: "name_mismatch", cause: err, Cert: info(hn.Certificate),
			Message: "The certificate of " + addr + " is not for the name " + host + ". Add the domain controller by a name the certificate lists, or trust this certificate."}
	case errors.As(err, &ua):
		c := peek(addr, host, timeout)
		return &TLSError{Kind: "untrusted", cause: err, Cert: c,
			Message: "The certificate of " + addr + " was issued by a certificate authority this controller does not trust. Install your CA's root certificate on the controller computer, or trust this certificate."}
	case errors.As(err, &ci) && ci.Reason == x509.Expired:
		return &TLSError{Kind: "expired", cause: err, Cert: info(ci.Cert), Message: "The certificate of " + addr + " has expired. Renew the domain controller's certificate."}
	case errors.As(err, &cv) && len(cv.UnverifiedCertificates) > 0:
		return &TLSError{Kind: "untrusted", cause: err, Cert: info(cv.UnverifiedCertificates[0]),
			Message: "The certificate of " + addr + " could not be checked (" + cv.Err.Error() + "). Install your CA's root certificate on the controller computer, or trust this certificate."}
	case errors.As(err, &alert) && strings.Contains(strings.ToLower(alert.Error()), "protocol version"):
		return &TLSError{Kind: "tls_version", cause: err, Message: addr + " only offers an old TLS version. Turn on TLS 1.2 on the domain controller."}
	case errors.Is(err, io.EOF) || errors.Is(err, syscall.ECONNRESET) || strings.Contains(err.Error(), "forcibly closed") || strings.Contains(err.Error(), "connection reset") || strings.Contains(err.Error(), "EOF"):
		return &TLSError{Kind: "no_certificate", cause: err,
			Message: "Port 636 on " + addr + " answered, but the domain controller ended the encrypted connection at once. This happens when it has no certificate for LDAPS. " +
				"Give the domain controller a certificate with Server Authentication (Active Directory Certificate Services enrols one automatically; otherwise import one into the computer's Personal store), then test again. " +
				"Until then, a Windows controller can still set passwords with LDAPS turned off."}
	}
	var ne net.Error
	if errors.As(err, &ne) && ne.Timeout() {
		return &TLSError{Kind: "no_certificate", cause: err, Message: "Port 636 on " + addr + " answered but never started encryption. The domain controller probably has no certificate for LDAPS."}
	}
	return &TLSError{Kind: "other", cause: err, Message: "Encryption with " + addr + " failed: " + err.Error()}
}
