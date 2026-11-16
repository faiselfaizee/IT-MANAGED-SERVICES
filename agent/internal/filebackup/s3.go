package filebackup

import (
	"bytes"
	"context"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/hex"
	"encoding/xml"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"sort"
	"strings"
	"time"
)

// s3Store talks to Amazon S3 and S3-compatible storage (Wasabi, Backblaze B2, MinIO, Synology C2, Cloudflare R2) with Signature Version 4.
type s3Store struct {
	base      *url.URL // scheme and host of the endpoint
	bucket    string
	prefix    string
	region    string
	access    string
	secret    string
	pathStyle bool
	hc        *http.Client
	now       func() time.Time
}

func newS3(s Storage) (*s3Store, error) {
	ep := strings.TrimSpace(s.Endpoint)
	if ep == "" {
		r := s.Region
		if r == "" {
			r = "us-east-1"
		}
		ep = "https://s3." + r + ".amazonaws.com"
	}
	if !strings.Contains(ep, "://") {
		ep = "https://" + ep
	}
	u, err := url.Parse(ep)
	if err != nil || u.Host == "" {
		return nil, fmt.Errorf("%q is not an S3 endpoint", s.Endpoint)
	}
	if s.Bucket == "" || s.AccessKey == "" || s.SecretKey == "" {
		return nil, errors.New("S3 needs a bucket, an access key and a secret key")
	}
	region := s.Region
	if region == "" {
		region = "us-east-1"
	}
	// buckets with dots, and hosts that are addresses or have a port (MinIO), only work with the bucket in the path
	ps := s.PathStyle || strings.Contains(s.Bucket, ".") || strings.Contains(u.Host, ":") || net4(u.Hostname())
	return &s3Store{base: &url.URL{Scheme: u.Scheme, Host: u.Host}, bucket: s.Bucket, prefix: cleanPrefix(s.Prefix), region: region,
		access: s.AccessKey, secret: s.SecretKey, pathStyle: ps, hc: &http.Client{Timeout: 5 * time.Minute}, now: time.Now}, nil
}

func net4(h string) bool {
	parts := strings.Split(h, ".")
	if len(parts) != 4 {
		return false
	}
	for _, p := range parts {
		if p == "" || strings.Trim(p, "0123456789") != "" {
			return false
		}
	}
	return true
}

func cleanPrefix(p string) string {
	p = strings.Trim(strings.ReplaceAll(p, `\`, "/"), "/")
	if p == "" {
		return ""
	}
	return p + "/"
}

func (s *s3Store) Where() string {
	return "s3://" + s.bucket + "/" + s.prefix + " at " + s.base.Host
}

// target gives the host and the escaped path of an object key, or of the bucket itself for "".
func (s *s3Store) target(key string) (host, p string) {
	if key == "" {
		if s.pathStyle {
			return s.base.Host, "/" + uriEncode(s.bucket, true)
		}
		return s.bucket + "." + s.base.Host, "/"
	}
	obj := "/" + uriEncode(s.prefix+key, false)
	if s.pathStyle {
		return s.base.Host, "/" + uriEncode(s.bucket, true) + obj
	}
	return s.bucket + "." + s.base.Host, obj
}

func (s *s3Store) do(ctx context.Context, method, key string, query url.Values, body []byte) (*http.Response, error) {
	host, p := s.target(key)
	u := s.base.Scheme + "://" + host + p
	if len(query) > 0 {
		u += "?" + canonicalQuery(query)
	}
	req, err := http.NewRequestWithContext(ctx, method, u, bytes.NewReader(body))
	if err != nil {
		return nil, err
	}
	req.Host = host
	req.ContentLength = int64(len(body))
	sum := sha256.Sum256(body)
	signV4(req, host, p, query, hex.EncodeToString(sum[:]), s.access, s.secret, s.region, "s3", s.now().UTC())
	resp, err := s.hc.Do(req)
	if err != nil {
		return nil, err
	}
	if resp.StatusCode >= 300 && !(resp.StatusCode == 404 && (method == http.MethodGet || method == http.MethodDelete)) {
		b, _ := io.ReadAll(io.LimitReader(resp.Body, 4096))
		resp.Body.Close()
		return nil, fmt.Errorf("S3 %s %s: %s %s", method, key, resp.Status, s3Message(b))
	}
	return resp, nil
}

func s3Message(b []byte) string {
	var e struct {
		Code    string `xml:"Code"`
		Message string `xml:"Message"`
	}
	if xml.Unmarshal(b, &e) == nil && e.Code != "" {
		return e.Code + ": " + e.Message
	}
	return strings.TrimSpace(string(b))
}

func (s *s3Store) Put(ctx context.Context, key string, data []byte) error {
	resp, err := s.do(ctx, http.MethodPut, key, nil, data)
	if err != nil {
		return err
	}
	resp.Body.Close()
	return nil
}

func (s *s3Store) Get(ctx context.Context, key string) ([]byte, error) {
	resp, err := s.do(ctx, http.MethodGet, key, nil, nil)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode == 404 {
		return nil, ErrNotFound
	}
	return io.ReadAll(resp.Body)
}

func (s *s3Store) Delete(ctx context.Context, key string) error {
	resp, err := s.do(ctx, http.MethodDelete, key, nil, nil)
	if err != nil {
		return err
	}
	resp.Body.Close()
	return nil
}

func (s *s3Store) List(ctx context.Context, prefix string) ([]string, error) {
	var out []string
	token := ""
	for {
		q := url.Values{"list-type": {"2"}, "prefix": {s.prefix + prefix}, "max-keys": {"1000"}}
		if token != "" {
			q.Set("continuation-token", token)
		}
		resp, err := s.do(ctx, http.MethodGet, "", q, nil)
		if err != nil {
			return nil, err
		}
		var r struct {
			Contents []struct {
				Key string `xml:"Key"`
			} `xml:"Contents"`
			IsTruncated           bool   `xml:"IsTruncated"`
			NextContinuationToken string `xml:"NextContinuationToken"`
		}
		err = xml.NewDecoder(resp.Body).Decode(&r)
		resp.Body.Close()
		if err != nil {
			return nil, err
		}
		for _, c := range r.Contents {
			out = append(out, strings.TrimPrefix(c.Key, s.prefix))
		}
		if !r.IsTruncated || r.NextContinuationToken == "" {
			break
		}
		token = r.NextContinuationToken
	}
	sort.Strings(out)
	return out, nil
}

// uriEncode escapes as AWS expects: everything but A-Z a-z 0-9 - _ . ~ (and "/" unless encodeSlash).
func uriEncode(s string, encodeSlash bool) string {
	var b strings.Builder
	for i := 0; i < len(s); i++ {
		c := s[i]
		switch {
		case c >= 'A' && c <= 'Z', c >= 'a' && c <= 'z', c >= '0' && c <= '9', c == '-', c == '_', c == '.', c == '~':
			b.WriteByte(c)
		case c == '/' && !encodeSlash:
			b.WriteByte(c)
		default:
			fmt.Fprintf(&b, "%%%02X", c)
		}
	}
	return b.String()
}

func canonicalQuery(q url.Values) string {
	keys := make([]string, 0, len(q))
	for k := range q {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	var parts []string
	for _, k := range keys {
		vs := append([]string(nil), q[k]...)
		sort.Strings(vs)
		for _, v := range vs {
			parts = append(parts, uriEncode(k, true)+"="+uriEncode(v, true))
		}
	}
	return strings.Join(parts, "&")
}

func hmacSHA(key []byte, s string) []byte {
	m := hmac.New(sha256.New, key)
	m.Write([]byte(s))
	return m.Sum(nil)
}

// signV4 adds the x-amz-date, x-amz-content-sha256 and Authorization headers. Every header already on the request is signed too.
func signV4(req *http.Request, host, escapedPath string, query url.Values, payloadHash, access, secret, region, service string, t time.Time) {
	amzDate := t.Format("20060102T150405Z")
	day := t.Format("20060102")
	req.Header.Set("x-amz-date", amzDate)
	req.Header.Set("x-amz-content-sha256", payloadHash)
	names := []string{"host"}
	vals := map[string]string{"host": host}
	for k, v := range req.Header {
		lk := strings.ToLower(k)
		if lk == "authorization" {
			continue
		}
		names = append(names, lk)
		vals[lk] = strings.Join(strings.Fields(strings.Join(v, ",")), " ")
	}
	sort.Strings(names)
	var ch strings.Builder
	for _, n := range names {
		ch.WriteString(n + ":" + vals[n] + "\n")
	}
	signed := strings.Join(names, ";")
	canon := strings.Join([]string{req.Method, escapedPath, canonicalQuery(query), ch.String(), signed, payloadHash}, "\n")
	cs := sha256.Sum256([]byte(canon))
	scope := day + "/" + region + "/" + service + "/aws4_request"
	toSign := "AWS4-HMAC-SHA256\n" + amzDate + "\n" + scope + "\n" + hex.EncodeToString(cs[:])
	k := hmacSHA([]byte("AWS4"+secret), day)
	k = hmacSHA(k, region)
	k = hmacSHA(k, service)
	k = hmacSHA(k, "aws4_request")
	sig := hex.EncodeToString(hmacSHA(k, toSign))
	req.Header.Set("Authorization", "AWS4-HMAC-SHA256 Credential="+access+"/"+scope+", SignedHeaders="+signed+", Signature="+sig)
}
