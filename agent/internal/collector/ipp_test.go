package collector

import (
	"context"
	"encoding/binary"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
)

// ippAnswer builds an IPP answer with a status and, for a job list, job ids.
func ippAnswer(status uint16, jobs ...int32) []byte {
	b := []byte{1, 1, byte(status >> 8), byte(status), 0, 0, 0, 1, ippTagOperation}
	for _, a := range []ippAttr{ippString(ippTagCharset, "attributes-charset", "utf-8"), ippString(ippTagLanguage, "attributes-natural-language", "en")} {
		b = append(b, a.tag, 0, byte(len(a.name)))
		b = append(b, a.name...)
		b = append(b, 0, byte(len(a.value)))
		b = append(b, a.value...)
	}
	for _, j := range jobs {
		b = append(b, ippTagJob)
		a := ippInt("job-id", j)
		b = append(b, a.tag, 0, byte(len(a.name)))
		b = append(b, a.name...)
		b = append(b, 0, 4)
		b = append(b, a.value...)
	}
	return append(b, ippTagEnd)
}

type fakePrinter struct {
	mu        sync.Mutex
	paths     []string
	ops       []uint16
	cancelled []int32
	purge     uint16 // status for Purge-Jobs
	jobs      []int32
	srv       *httptest.Server
}

func newFakePrinter(path string, purge uint16, jobs ...int32) *fakePrinter {
	f := &fakePrinter{purge: purge, jobs: jobs}
	f.srv = httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		f.mu.Lock()
		defer f.mu.Unlock()
		f.paths = append(f.paths, r.URL.Path)
		if r.URL.Path != path || r.Header.Get("Content-Type") != "application/ipp" {
			http.NotFound(w, r)
			return
		}
		body, _ := io.ReadAll(r.Body)
		op := binary.BigEndian.Uint16(body[2:4])
		f.ops = append(f.ops, op)
		w.Header().Set("Content-Type", "application/ipp")
		switch op {
		case ippPurgeJobs:
			w.Write(ippAnswer(f.purge))
		case ippGetJobs:
			w.Write(ippAnswer(0, f.jobs...))
		case ippCancelJob:
			// the job id is the last four bytes before the user name; find it by scanning for the attribute
			i := strings.Index(string(body), "job-id")
			f.cancelled = append(f.cancelled, int32(binary.BigEndian.Uint32(body[i+6+2:])))
			w.Write(ippAnswer(0))
		default:
			w.Write(ippAnswer(0x0501))
		}
	}))
	return f
}

func TestIPPPurgeClearsThePrintersQueue(t *testing.T) {
	f := newFakePrinter("/ipp/print", 0)
	defer f.srv.Close()
	msg, err := ippClearQueue(context.Background(), f.srv.URL)
	if err != nil || !strings.Contains(msg, "cleared") {
		t.Fatalf("%q %v", msg, err)
	}
	if len(f.ops) != 1 || f.ops[0] != ippPurgeJobs {
		t.Fatalf("operations: %v", f.ops)
	}
}

func TestIPPFindsTheQueueAtAnotherAddress(t *testing.T) {
	f := newFakePrinter("/ipp", 0)
	defer f.srv.Close()
	if _, err := ippClearQueue(context.Background(), f.srv.URL); err != nil {
		t.Fatal(err)
	}
	if len(f.paths) < 2 || f.paths[0] != "/ipp/print" || f.paths[1] != "/ipp" {
		t.Fatalf("tried %v", f.paths)
	}
}

func TestIPPCancelsJobByJobWhenPurgeIsNotSupported(t *testing.T) {
	f := newFakePrinter("/ipp/print", 0x0501, 11, 12, 13)
	defer f.srv.Close()
	msg, err := ippClearQueue(context.Background(), f.srv.URL)
	if err != nil || !strings.Contains(msg, "Cancelled 3 of 3") {
		t.Fatalf("%q %v", msg, err)
	}
	if len(f.cancelled) != 3 || f.cancelled[0] != 11 || f.cancelled[2] != 13 {
		t.Fatalf("cancelled %v", f.cancelled)
	}
}

func TestIPPSaysWhenTheQueueIsAlreadyEmpty(t *testing.T) {
	f := newFakePrinter("/ipp/print", 0x0501)
	defer f.srv.Close()
	msg, err := ippClearQueue(context.Background(), f.srv.URL)
	if err != nil || !strings.Contains(msg, "already empty") {
		t.Fatalf("%q %v", msg, err)
	}
}

func TestIPPExplainsAForbiddenPurge(t *testing.T) {
	f := newFakePrinter("/ipp/print", 0x0401)
	defer f.srv.Close()
	if _, err := ippClearQueue(context.Background(), f.srv.URL); err == nil || !strings.Contains(err.Error(), "without a login") {
		t.Fatalf("got %v", err)
	}
}

func TestIPPSaysWhenThePortIsClosed(t *testing.T) {
	f := newFakePrinter("/ipp/print", 0)
	url := f.srv.URL
	f.srv.Close()
	if _, err := ippClearQueue(context.Background(), url); err == nil || !strings.Contains(err.Error(), "does not answer on the IPP port") {
		t.Fatalf("got %v", err)
	}
}

func TestIPPAnswerParsingRejectsGarbage(t *testing.T) {
	if _, err := parseIPP([]byte("<html>")); err == nil {
		t.Fatal("HTML is not an IPP answer")
	}
	if r, err := parseIPP(ippAnswer(0, 5, 6)); err != nil || len(r.JobIDs) != 2 || !r.ok() {
		t.Fatalf("%v %v", r, err)
	}
}
