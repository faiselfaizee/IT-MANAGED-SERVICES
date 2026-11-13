package collector

import (
	"context"
	"crypto/tls"
	"fmt"
	"io"
	"net"
	"net/http"
	"strconv"
	"strings"
	"time"
)

// CheckDef is a service check the platform wants run (see Assets > Service checks).
type CheckDef struct {
	ID      int    `json:"id"`
	Name    string `json:"name"`
	Type    string `json:"type"` // tcp, http, ssl, dns
	Host    string `json:"host"`
	Port    int    `json:"port"`
	URL     string `json:"url"`
	Expect  string `json:"expect"`
	Status  string `json:"status"`
	Insec   bool   `json:"insecure"`
	DNS     string `json:"dns"`
	Timeout int    `json:"timeoutS"`
}

// CheckResult is what the collector reports back.
type CheckResult struct {
	ID     int      `json:"id"`
	Asset  string   `json:"asset"`
	OK     bool     `json:"ok"`
	MS     float64  `json:"ms"`
	Days   *float64 `json:"days,omitempty"`
	Detail string   `json:"detail"`
}

// RunCheck performs one check; it never panics and always returns a readable detail.
func RunCheck(ctx context.Context, d CheckDef) CheckResult {
	to := time.Duration(d.Timeout) * time.Second
	if to <= 0 || to > 60*time.Second {
		to = 10 * time.Second
	}
	ctx, cancel := context.WithTimeout(ctx, to)
	defer cancel()
	r := CheckResult{ID: d.ID}
	start := time.Now()
	defer func() { r.MS = float64(time.Since(start).Microseconds()) / 1000 }()
	switch d.Type {
	case "tcp":
		ok, _ := TCPOpen(ctx, d.Host, d.Port, to)
		r.OK = ok
		if ok {
			r.Detail = fmt.Sprintf("port %d open", d.Port)
		} else {
			r.Detail = fmt.Sprintf("port %d did not answer", d.Port)
		}
	case "ssl":
		port := d.Port
		if port == 0 {
			port = 443
		}
		dialer := &net.Dialer{Timeout: to}
		conn, err := tls.DialWithDialer(dialer, "tcp", net.JoinHostPort(d.Host, strconv.Itoa(port)), &tls.Config{ServerName: d.Host, InsecureSkipVerify: d.Insec})
		if err != nil {
			r.Detail = trim(err.Error())
			// an expired or untrusted certificate is still readable: report its days with the failure
			if c2, e2 := tls.DialWithDialer(dialer, "tcp", net.JoinHostPort(d.Host, strconv.Itoa(port)), &tls.Config{ServerName: d.Host, InsecureSkipVerify: true}); e2 == nil {
				days := daysLeft(c2)
				r.Days = &days
				c2.Close()
			}
			return r
		}
		defer conn.Close()
		days := daysLeft(conn)
		r.Days = &days
		r.OK = days > 0
		r.Detail = fmt.Sprintf("certificate valid for %d more days", int(days))
	case "dns":
		res := &net.Resolver{}
		if d.DNS != "" {
			server := net.JoinHostPort(strings.TrimSpace(d.DNS), "53")
			res = &net.Resolver{PreferGo: true, Dial: func(c context.Context, _, _ string) (net.Conn, error) {
				return (&net.Dialer{Timeout: to}).DialContext(c, "udp", server)
			}}
		}
		ips, err := res.LookupHost(ctx, d.Host)
		switch {
		case err != nil:
			r.Detail = trim(err.Error())
		case d.Expect != "" && !contains(ips, d.Expect):
			r.Detail = "answered " + strings.Join(ips, ", ") + ", expected " + d.Expect
		default:
			r.OK, r.Detail = true, strings.Join(ips, ", ")
		}
	case "http":
		u := d.URL
		if u == "" {
			u = "http://" + d.Host
		}
		client := &http.Client{Timeout: to, Transport: &http.Transport{TLSClientConfig: &tls.Config{InsecureSkipVerify: d.Insec}}, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
		req, err := http.NewRequestWithContext(ctx, http.MethodGet, u, nil)
		if err != nil {
			r.Detail = trim(err.Error())
			return r
		}
		req.Header.Set("User-Agent", "DeskAsk-Collector")
		resp, err := client.Do(req)
		if err != nil {
			r.Detail = trim(err.Error())
			return r
		}
		defer resp.Body.Close()
		body, _ := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
		okStatus := resp.StatusCode >= 200 && resp.StatusCode < 400
		if d.Status != "" {
			okStatus = strconv.Itoa(resp.StatusCode) == strings.TrimSpace(d.Status)
		}
		okText := d.Expect == "" || strings.Contains(string(body), d.Expect)
		r.OK = okStatus && okText
		r.Detail = fmt.Sprintf("HTTP %d", resp.StatusCode)
		if !okText {
			r.Detail += ", expected text not found"
		}
	default:
		r.Detail = "unknown check type " + d.Type
	}
	return r
}

func daysLeft(c *tls.Conn) float64 {
	st := c.ConnectionState()
	if len(st.PeerCertificates) == 0 {
		return 0
	}
	return time.Until(st.PeerCertificates[0].NotAfter).Hours() / 24
}

func contains(l []string, s string) bool {
	for _, x := range l {
		if strings.EqualFold(x, strings.TrimSpace(s)) {
			return true
		}
	}
	return false
}

func trim(s string) string {
	if len(s) > 480 {
		return s[:480]
	}
	return s
}
