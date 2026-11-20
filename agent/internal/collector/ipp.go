package collector

import (
	"bytes"
	"context"
	"encoding/binary"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"strings"
	"time"
)

// A very small IPP (Internet Printing Protocol, RFC 8011) client: it clears a printer's own job queue. Almost every network printer sold in
// the last ten years answers IPP on port 631; whether it allows Purge-Jobs without a login is the maker's and the administrator's choice.

const (
	ippPurgeJobs = 0x0012
	ippGetJobs   = 0x000A
	ippCancelJob = 0x0008

	ippTagOperation = 0x01
	ippTagJob       = 0x02
	ippTagEnd       = 0x03
	ippTagInteger   = 0x21
	ippTagBoolean   = 0x22
	ippTagName      = 0x42
	ippTagKeyword   = 0x44
	ippTagURI       = 0x45
	ippTagCharset   = 0x47
	ippTagLanguage  = 0x48
)

// ippPaths are the queue addresses printers use, most common first.
var ippPaths = []string{"/ipp/print", "/ipp", "/ipp/printer"}

type ippAttr struct {
	tag   byte
	name  string
	value []byte
}

func ippString(tag byte, name, v string) ippAttr { return ippAttr{tag, name, []byte(v)} }
func ippInt(name string, v int32) ippAttr {
	b := make([]byte, 4)
	binary.BigEndian.PutUint32(b, uint32(v))
	return ippAttr{ippTagInteger, name, b}
}

func ippRequest(op uint16, id uint32, attrs ...ippAttr) []byte {
	var b bytes.Buffer
	b.Write([]byte{1, 1})
	_ = binary.Write(&b, binary.BigEndian, op)
	_ = binary.Write(&b, binary.BigEndian, id)
	b.WriteByte(ippTagOperation)
	for _, a := range attrs {
		b.WriteByte(a.tag)
		_ = binary.Write(&b, binary.BigEndian, uint16(len(a.name)))
		b.WriteString(a.name)
		_ = binary.Write(&b, binary.BigEndian, uint16(len(a.value)))
		b.Write(a.value)
	}
	b.WriteByte(ippTagEnd)
	return b.Bytes()
}

type ippReply struct {
	Status uint16
	JobIDs []int32
}

// ok is true for successful-ok and its two "ok, but" variants.
func (r ippReply) ok() bool { return r.Status < 0x0100 }

func parseIPP(b []byte) (ippReply, error) {
	var r ippReply
	if len(b) < 9 {
		return r, errors.New("the printer's answer is not IPP")
	}
	r.Status = binary.BigEndian.Uint16(b[2:4])
	p := 8
	group := byte(0)
	for p < len(b) {
		t := b[p]
		p++
		if t == ippTagEnd {
			return r, nil
		}
		if t < 0x10 { // a group begins (delimiter tags are below 0x10)
			group = t
			continue
		}
		if p+2 > len(b) {
			break
		}
		nl := int(binary.BigEndian.Uint16(b[p:]))
		p += 2
		if p+nl+2 > len(b) {
			break
		}
		name := string(b[p : p+nl])
		p += nl
		vl := int(binary.BigEndian.Uint16(b[p:]))
		p += 2
		if p+vl > len(b) {
			break
		}
		val := b[p : p+vl]
		p += vl
		if group == ippTagJob && name == "job-id" && t == ippTagInteger && vl == 4 {
			r.JobIDs = append(r.JobIDs, int32(binary.BigEndian.Uint32(val)))
		}
	}
	return r, errors.New("the printer's answer is cut short")
}

type ippTarget struct {
	Base string // http://host:631
	Path string
}

func (t ippTarget) printerURI() string {
	return strings.Replace(strings.Replace(t.Base, "https://", "ipps://", 1), "http://", "ipp://", 1) + t.Path
}

func (t ippTarget) do(ctx context.Context, hc *http.Client, op uint16, extra ...ippAttr) (ippReply, int, error) {
	attrs := append([]ippAttr{ippString(ippTagCharset, "attributes-charset", "utf-8"), ippString(ippTagLanguage, "attributes-natural-language", "en"),
		ippString(ippTagURI, "printer-uri", t.printerURI())}, extra...)
	attrs = append(attrs, ippString(ippTagName, "requesting-user-name", "DeskAsk"))
	req, err := http.NewRequestWithContext(ctx, "POST", t.Base+t.Path, bytes.NewReader(ippRequest(op, 1, attrs...)))
	if err != nil {
		return ippReply{}, 0, err
	}
	req.Header.Set("Content-Type", "application/ipp")
	resp, err := hc.Do(req)
	if err != nil {
		return ippReply{}, 0, err
	}
	defer resp.Body.Close()
	body, _ := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	if resp.StatusCode != 200 {
		return ippReply{}, resp.StatusCode, fmt.Errorf("the printer answered HTTP %d", resp.StatusCode)
	}
	r, err := parseIPP(body)
	return r, 200, err
}

// ippClearQueue empties the printer's own job queue and says what it did. base is "http://host:631".
func ippClearQueue(ctx context.Context, base string) (string, error) {
	hc := &http.Client{Timeout: 15 * time.Second}
	var last error = errors.New("the printer does not answer on the IPP port (631); IPP may be switched off on it")
	for _, path := range ippPaths {
		t := ippTarget{base, path}
		r, status, err := t.do(ctx, hc, ippPurgeJobs)
		if err != nil {
			if status == 0 && isNetwork(err) { // nothing listens on the port: another path will not help
				return "", errors.New("the printer does not answer on the IPP port (631); IPP may be switched off on it")
			}
			last = err // this address is not the queue (HTTP 404 and the like): try the next one
			continue
		}
		switch {
		case r.ok():
			return "The printer's own queue was cleared (IPP purge).", nil
		case r.Status == 0x0501 || r.Status == 0x0504 || r.Status == 0x0502: // operation, version or request not supported: cancel job by job instead
			return ippCancelEach(ctx, hc, t)
		case r.Status == 0x0401 || r.Status == 0x0402:
			return "", errors.New("the printer refused to clear its queue without a login (IPP forbidden or not authenticated); allow it in the printer's web settings")
		default:
			last = fmt.Errorf("the printer refused (IPP status 0x%04x)", r.Status)
		}
	}
	return "", last
}

func isNetwork(err error) bool {
	var ne net.Error
	return errors.As(err, &ne)
}

// ippCancelEach lists the unfinished jobs and cancels them one by one, for printers that do not support Purge-Jobs.
func ippCancelEach(ctx context.Context, hc *http.Client, t ippTarget) (string, error) {
	r, _, err := t.do(ctx, hc, ippGetJobs, ippString(ippTagKeyword, "which-jobs", "not-completed"), ippString(ippTagKeyword, "requested-attributes", "job-id"))
	if err != nil {
		return "", err
	}
	if !r.ok() {
		return "", fmt.Errorf("the printer will not list or cancel its jobs (IPP status 0x%04x)", r.Status)
	}
	if len(r.JobIDs) == 0 {
		return "The printer's own queue is already empty.", nil
	}
	done := 0
	for _, id := range r.JobIDs {
		c, _, err := t.do(ctx, hc, ippCancelJob, ippInt("job-id", id))
		if err == nil && c.ok() {
			done++
		}
	}
	if done == 0 {
		return "", errors.New("the printer listed its jobs but would not cancel them")
	}
	return fmt.Sprintf("Cancelled %d of %d jobs in the printer's own queue.", done, len(r.JobIDs)), nil
}
