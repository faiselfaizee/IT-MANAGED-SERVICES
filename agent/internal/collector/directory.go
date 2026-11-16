package collector

import (
	"context"
	"encoding/base64"
	"encoding/binary"
	"encoding/json"
	"errors"
	"fmt"
	"net"
	"slices"
	"strings"
	"time"
	"unicode/utf16"
	"unicode/utf8"

	"github.com/deskask/platform/agent/internal/client"
	"github.com/deskask/platform/agent/internal/ldap"
)

// dirConn says how to reach a customer's domain: the controller sits inside the customer's network, so the platform never
// needs an inbound path to the domain controllers.
type dirConn struct {
	Servers    []string `json:"servers"` // host or host:port, tried in order
	TLS        bool     `json:"tls"`     // LDAPS (port 636 unless given); setting a password needs it
	SkipVerify bool     `json:"skipVerify"`
	PinSHA256  string   `json:"certSha256"` // trust exactly this certificate
	BindDN     string   `json:"bindDn"`
	Password   string   `json:"password"`
}

type dirOp struct {
	Op        string              `json:"op"` // search, modify, add, delete, rename, setpw, rootdse
	Base      string              `json:"base"`
	Scope     string              `json:"scope"` // base, one, sub
	Filter    string              `json:"filter"`
	Attrs     []string            `json:"attrs"`
	PageSize  int                 `json:"pageSize"`
	Max       int                 `json:"max"`
	DN        string              `json:"dn"`
	Changes   []dirChange         `json:"changes"`
	Entry     map[string][]string `json:"entry"` // add
	NewRDN    string              `json:"newRdn"`
	NewParent string              `json:"newParent"`
	Password  string              `json:"password"` // setpw
	MustMove  bool                `json:"mustChange"`
	Ignore    []int               `json:"ignore"` // result codes that are not a failure for this step (for example "already a member")
}

type dirChange struct {
	Op     string   `json:"op"` // add, delete, replace
	Attr   string   `json:"attr"`
	Values []string `json:"values"`
}

type dirJob struct {
	Conn dirConn `json:"conn"`
	Ops  []dirOp `json:"ops"`
}

type dirEntry struct {
	DN    string              `json:"dn"`
	Attrs map[string][]string `json:"attrs"`
}

type dirResult struct {
	Server    string     `json:"server,omitempty"`
	Entries   []dirEntry `json:"entries,omitempty"`
	Truncated bool       `json:"truncated,omitempty"`
}

// dirError carries the directory's own result code so the platform can say what went wrong.
type dirError struct {
	Code    int            `json:"code"`
	Message string         `json:"message"`
	Step    int            `json:"step"`
	TLS     string         `json:"tls,omitempty"`  // why encryption failed: no_certificate, name_mismatch, untrusted, ...
	Cert    *ldap.CertInfo `json:"cert,omitempty"` // the certificate the domain controller showed
}

func (e *dirError) Error() string { return e.Message }

func ldapAddr(s string, tls bool) string {
	if _, _, err := net.SplitHostPort(s); err == nil {
		return s
	}
	if strings.Count(s, ":") > 1 && !strings.HasPrefix(s, "[") { // a bare IPv6 address
		s = "[" + s + "]"
	}
	if tls {
		return s + ":636"
	}
	return s + ":389"
}

// connect tries each server in turn. A refused login ends the attempt at once: trying the same password on every
// domain controller would lock the service account.
func connect(c dirConn, timeout time.Duration) (*ldap.Conn, string, error) {
	if len(c.Servers) == 0 {
		return nil, "", &dirError{Message: "no domain controller is set"}
	}
	var last error
	var tlsErr *ldap.TLSError
	for _, s := range c.Servers {
		addr := ldapAddr(strings.TrimSpace(s), c.TLS)
		l, err := ldap.DialWith(addr, ldap.Options{TLS: c.TLS, SkipVerify: c.SkipVerify, PinSHA256: c.PinSHA256, Timeout: timeout})
		if err != nil {
			var te *ldap.TLSError
			if errors.As(err, &te) && tlsErr == nil {
				tlsErr = te
			}
			last = fmt.Errorf("%s: %v", addr, err)
			continue
		}
		if c.BindDN != "" {
			if err := l.Bind(c.BindDN, c.Password); err != nil {
				_ = l.Close()
				var re *ldap.ResultError
				if errors.As(err, &re) {
					return nil, addr, &dirError{Code: re.Code, Message: "the domain controller " + addr + " refused the service account: " + re.Message}
				}
				last = fmt.Errorf("%s: %v", addr, err)
				continue
			}
		}
		return l, addr, nil
	}
	if tlsErr != nil { // every server failed, at least one at the encryption step: that is the useful reason
		return nil, "", &dirError{Code: -2, Message: tlsErr.Message, TLS: tlsErr.Kind, Cert: tlsErr.Cert}
	}
	return nil, "", &dirError{Message: "no domain controller could be reached: " + last.Error()}
}

func scopeOf(s string) int {
	switch s {
	case "base":
		return ldap.ScopeBase
	case "one":
		return ldap.ScopeOne
	}
	return ldap.ScopeSubtree
}

func modOf(s string) (int, error) {
	switch s {
	case "add":
		return ldap.ModAdd, nil
	case "delete":
		return ldap.ModDelete, nil
	case "replace":
		return ldap.ModReplace, nil
	}
	return 0, fmt.Errorf("unknown change %q", s)
}

func bytesOf(v []string) [][]byte {
	out := make([][]byte, 0, len(v))
	for _, s := range v {
		out = append(out, []byte(s))
	}
	return out
}

// pwdValue is how Active Directory wants a password: UTF-16LE inside quotes.
func pwdValue(p string) []byte {
	u := utf16.Encode([]rune(`"` + p + `"`))
	b := make([]byte, 2*len(u))
	for i, c := range u {
		binary.LittleEndian.PutUint16(b[2*i:], c)
	}
	return b
}

func guidString(b []byte) string {
	if len(b) != 16 {
		return base64.StdEncoding.EncodeToString(b)
	}
	return fmt.Sprintf("%08x-%04x-%04x-%x-%x", binary.LittleEndian.Uint32(b[0:4]), binary.LittleEndian.Uint16(b[4:6]), binary.LittleEndian.Uint16(b[6:8]), b[8:10], b[10:16])
}

func sidString(b []byte) string {
	if len(b) < 8 {
		return base64.StdEncoding.EncodeToString(b)
	}
	auth := uint64(0)
	for _, c := range b[2:8] {
		auth = auth<<8 | uint64(c)
	}
	s := fmt.Sprintf("S-%d-%d", b[0], auth)
	n := int(b[1])
	for i := 0; i < n && 8+4*i+4 <= len(b); i++ {
		s += fmt.Sprintf("-%d", binary.LittleEndian.Uint32(b[8+4*i:]))
	}
	return s
}

// text makes attribute values readable: the two well-known binary ones become GUID and SID strings, other binary values base64.
func text(name string, vals [][]byte) []string {
	out := make([]string, 0, len(vals))
	for _, v := range vals {
		switch strings.ToLower(name) {
		case "objectguid":
			out = append(out, guidString(v))
		case "objectsid":
			out = append(out, sidString(v))
		default:
			if utf8.Valid(v) {
				out = append(out, string(v))
			} else {
				out = append(out, "base64:"+base64.StdEncoding.EncodeToString(v))
			}
		}
	}
	return out
}

func runOp(l *ldap.Conn, server string, conn dirConn, o dirOp) (dirResult, error) {
	res := dirResult{Server: server}
	switch o.Op {
	case "rootdse":
		got, _, err := l.Search(ldap.SearchReq{Base: "", Scope: ldap.ScopeBase, Filter: "(objectClass=*)", Attrs: []string{"defaultNamingContext", "dnsHostName", "rootDomainNamingContext", "namingContexts", "supportedLDAPVersion"}})
		if err != nil {
			return res, err
		}
		for _, e := range got {
			d := dirEntry{DN: e.DN, Attrs: map[string][]string{}}
			for n, v := range e.Attrs {
				d.Attrs[n] = text(n, v)
			}
			res.Entries = append(res.Entries, d)
		}
	case "search":
		got, cut, err := l.Search(ldap.SearchReq{Base: o.Base, Scope: scopeOf(o.Scope), Filter: o.Filter, Attrs: o.Attrs, PageSize: o.PageSize, Max: o.Max})
		if err != nil {
			return res, err
		}
		res.Truncated = cut
		for _, e := range got {
			d := dirEntry{DN: e.DN, Attrs: map[string][]string{}}
			for n, v := range e.Attrs {
				d.Attrs[n] = text(n, v)
			}
			res.Entries = append(res.Entries, d)
		}
	case "modify":
		var ch []ldap.Change
		for _, c := range o.Changes {
			op, err := modOf(c.Op)
			if err != nil {
				return res, err
			}
			ch = append(ch, ldap.Change{Op: op, Attr: c.Attr, Vals: bytesOf(c.Values)})
		}
		return res, l.Modify(o.DN, ch)
	case "add":
		a := map[string][][]byte{}
		for n, v := range o.Entry {
			a[n] = bytesOf(v)
		}
		return res, l.Add(o.DN, a)
	case "delete":
		return res, l.Delete(o.DN)
	case "rename":
		return res, l.Rename(o.DN, o.NewRDN, o.NewParent)
	case "setpw":
		if !conn.TLS && windowsPasswords != nil { // without LDAPS a Windows controller sets it through Windows' own signed and sealed channel
			return res, windowsPasswords(conn, o)
		}
		ch := []ldap.Change{{Op: ldap.ModReplace, Attr: "unicodePwd", Vals: [][]byte{pwdValue(o.Password)}}}
		if err := l.Modify(o.DN, ch); err != nil {
			var re *ldap.ResultError
			if errors.As(err, &re) && re.Code == ldap.CodeUnwilling {
				return res, &ldap.ResultError{Code: re.Code, Message: "the domain controller would not set the password: it must be reached over LDAPS (turn on TLS), and the password must meet the domain's rules"}
			}
			return res, err
		}
		if o.MustMove { // "must change at next sign-in"
			return res, l.Modify(o.DN, []ldap.Change{{Op: ldap.ModReplace, Attr: "pwdLastSet", Vals: [][]byte{[]byte("0")}}})
		}
	default:
		return res, fmt.Errorf("unknown directory step %q", o.Op)
	}
	return res, nil
}

// doDirectory runs the steps in order on one connection and stops at the first one that fails.
func doDirectory(raw json.RawMessage) (any, error) {
	var j dirJob
	if err := json.Unmarshal(raw, &j); err != nil {
		return nil, &dirError{Message: "unreadable directory job"}
	}
	if len(j.Ops) == 0 || len(j.Ops) > 50 {
		return nil, &dirError{Message: "a directory job has between 1 and 50 steps"}
	}
	l, server, err := connect(j.Conn, 20*time.Second)
	if err != nil {
		return nil, err
	}
	defer l.Close()
	out := make([]dirResult, 0, len(j.Ops))
	for i, o := range j.Ops {
		r, err := runOp(l, server, j.Conn, o)
		if err != nil {
			var re *ldap.ResultError
			if errors.As(err, &re) && slices.Contains(o.Ignore, re.Code) {
				out = append(out, r)
				continue
			}
			if errors.As(err, &re) {
				return map[string]any{"results": out}, &dirError{Code: re.Code, Message: re.Message, Step: i}
			}
			return map[string]any{"results": out}, &dirError{Message: err.Error(), Step: i}
		}
		out = append(out, r)
	}
	return map[string]any{"results": out}, nil
}

// ---- the relay loop: the platform hands jobs to the controller that sits in the customer's network

type relayJob struct {
	ID      string          `json:"id"`
	Kind    string          `json:"kind"`
	Payload json.RawMessage `json:"payload"`
}

type relayDone struct {
	ID     string `json:"id"`
	OK     bool   `json:"ok"`
	Result any    `json:"result,omitempty"`
	Error  any    `json:"error,omitempty"`
}

func (r *Runner) runRelay(ctx context.Context, j relayJob) {
	var done relayDone
	done.ID = j.ID
	switch j.Kind {
	case "directory":
		res, err := doDirectory(j.Payload)
		done.Result = res
		if err != nil {
			var de *dirError
			if errors.As(err, &de) {
				done.Error = de
			} else {
				done.Error = &dirError{Message: err.Error()}
			}
		} else {
			done.OK = true
		}
	default:
		done.Error = &dirError{Message: "this controller does not know the job kind " + j.Kind + "; update it"}
	}
	for try := 0; try < 3 && ctx.Err() == nil; try++ {
		pctx, cancel := context.WithTimeout(ctx, 30*time.Second)
		err := r.cl.Do(pctx, "POST", "/collector/v1/relay/result", done, nil)
		cancel()
		if err == nil {
			return
		}
		sleepCtx(ctx, 2*time.Second)
	}
}

// relayLoop waits on the platform for jobs (up to 25 s a time, answered the moment one is queued) and runs a few at once.
func (r *Runner) relayLoop(ctx context.Context) {
	failures := 0
	sem := make(chan struct{}, 8)
	for ctx.Err() == nil {
		var out struct {
			Jobs []relayJob `json:"jobs"`
		}
		pctx, cancel := context.WithTimeout(ctx, 45*time.Second)
		err := r.cl.Do(pctx, "GET", "/collector/v1/relay/wait?wait=25", nil, &out)
		cancel()
		if err != nil {
			if ctx.Err() != nil {
				return
			}
			var ce *client.Error
			wait := 10 * time.Second
			switch {
			case errors.Is(err, client.ErrAuth):
				wait = 5 * time.Minute
			case errors.As(err, &ce) && ce.Status == 404:
				wait = 10 * time.Minute // a platform without directory jobs
			default:
				failures++
				wait = min(time.Second<<min(failures, 6), 60*time.Second)
			}
			sleepCtx(ctx, wait)
			continue
		}
		failures = 0
		for _, j := range out.Jobs {
			j := j
			sem <- struct{}{}
			go func() {
				defer func() { <-sem }()
				r.runRelay(ctx, j)
			}()
		}
	}
}
