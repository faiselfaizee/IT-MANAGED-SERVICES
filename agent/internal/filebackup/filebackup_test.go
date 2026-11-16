package filebackup

import (
	"bytes"
	"context"
	"crypto/rand"
	"encoding/xml"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"testing"
	"time"
)

func write(t *testing.T, p string, data []byte) {
	t.Helper()
	if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(p, data, 0o644); err != nil {
		t.Fatal(err)
	}
}

func random(n int) []byte { b := make([]byte, n); rand.Read(b); return b }

func TestBackupRestoreDedupAndRetention(t *testing.T) {
	ctx := context.Background()
	src := t.TempDir()
	big := random(24 << 20)
	write(t, filepath.Join(src, "docs", "big.bin"), big)
	write(t, filepath.Join(src, "docs", "note.txt"), []byte("hello"))
	write(t, filepath.Join(src, "cache", "x.tmp"), []byte("skip me"))
	write(t, filepath.Join(src, "node_modules", "lib.js"), []byte("skip me too"))

	st := &fsStore{root: t.TempDir()}
	repo, err := OpenRepo(ctx, st, "correct horse battery")
	if err != nil {
		t.Fatal(err)
	}
	o := Options{Plan: "p1", Host: "PC1", Paths: []string{src}, Excludes: []string{"node_modules"}}
	s1, err := Backup(ctx, repo, o)
	if err != nil {
		t.Fatal(err)
	}
	if s1.Files != 2 || s1.Bytes != int64(len(big)+5) || s1.NewBytes == 0 {
		t.Fatalf("first run %+v", s1)
	}
	// nothing changed: nothing new is stored and the files are not read again
	s2, err := Backup(ctx, repo, o)
	if err != nil || s2.NewBytes != 0 || s2.Unchanged != 2 {
		t.Fatalf("second run %+v %v", s2, err)
	}
	// a few bytes inserted at the start of the big file: only the first piece or two are new
	changed := append([]byte("NEW"), big...)
	write(t, filepath.Join(src, "docs", "big.bin"), changed)
	future := time.Now().Add(time.Minute)
	os.Chtimes(filepath.Join(src, "docs", "big.bin"), future, future)
	s3, err := Backup(ctx, repo, o)
	if err != nil {
		t.Fatal(err)
	}
	if s3.NewBytes == 0 || s3.NewBytes > s1.NewBytes*2/5 {
		t.Fatalf("an insert should store little: first %d, now %d", s1.NewBytes, s3.NewBytes)
	}

	// a wrong password does not open the repository; the right one does
	if _, err := OpenRepo(ctx, st, "wrong"); err == nil || !strings.Contains(err.Error(), "password") {
		t.Fatalf("wrong password: %v", err)
	}
	again, err := OpenRepo(ctx, st, "correct horse battery")
	if err != nil {
		t.Fatal(err)
	}
	// restore the first snapshot into a folder: the old content comes back
	dst := t.TempDir()
	rs, err := Restore(ctx, again, s1.Snapshot, RestoreOptions{Target: dst})
	if err != nil || rs.Files != 2 || len(rs.Errors) != 0 {
		t.Fatalf("restore %+v %v", rs, err)
	}
	got, err := os.ReadFile(targetPath(dst, filepath.Join(src, "docs", "big.bin")))
	if err != nil || !bytes.Equal(got, big) {
		t.Fatalf("restored content differs (%v)", err)
	}
	// only one file, put back where it was
	os.Remove(filepath.Join(src, "docs", "note.txt"))
	rs, err = Restore(ctx, again, s3.Snapshot, RestoreOptions{Include: []string{filepath.Join(src, "docs", "note.txt")}})
	if err != nil || rs.Files != 1 {
		t.Fatalf("restore one file %+v %v", rs, err)
	}
	if b, _ := os.ReadFile(filepath.Join(src, "docs", "note.txt")); string(b) != "hello" {
		t.Fatal("the file did not come back")
	}

	// retention: keep the newest one only; pieces only the old snapshots used are deleted, the newest still restores
	before, _ := st.List(ctx, "data/")
	removed, err := Forget(ctx, again, "p1", 1, 0, time.Now())
	if err != nil || len(removed) != 2 {
		t.Fatalf("forget %v %v", removed, err)
	}
	after, _ := st.List(ctx, "data/")
	if len(after) >= len(before) {
		t.Fatalf("no pieces deleted: %d -> %d", len(before), len(after))
	}
	if _, err := Restore(ctx, again, s3.Snapshot, RestoreOptions{Target: t.TempDir()}); err != nil {
		t.Fatal(err)
	}
	// a damaged piece is noticed
	write(t, filepath.Join(st.root, filepath.FromSlash(after[0])), []byte("garbage"))
	fresh, _ := OpenRepo(ctx, st, "correct horse battery")
	rs, _ = Restore(ctx, fresh, s3.Snapshot, RestoreOptions{Target: t.TempDir()})
	if len(rs.Errors) == 0 {
		t.Fatal("a damaged piece went unnoticed")
	}
}

func TestExcludes(t *testing.T) {
	ex := append(append([]string{}, DefaultExcludes...), "node_modules", `*\AppData\Local\Temp\*`)
	for p, want := range map[string]bool{
		`C:\Users\a\Documents\report.docx`:         false,
		`C:\Users\a\Documents\~$report.docx`:       true,
		`C:\pagefile.sys`:                          true,
		`C:\src\app\node_modules`:                  true,
		`C:\Users\a\AppData\Local\Temp\x\y.log`:    true,
		`C:\Users\a\Documents\temporary\notes.txt`: false,
		`D:\$Recycle.Bin\S-1-5\file.txt`:           true,
	} {
		if got := matchAny(ex, p); got != want {
			t.Errorf("%s: got %v", p, got)
		}
	}
	if targetPath(`E:\Restore`, `C:\Users\a\f.txt`) != filepath.Join(`E:\Restore`, `C\Users\a\f.txt`) && filepath.Separator == '\\' {
		t.Error("target path")
	}
}

// The example from the AWS Signature Version 4 documentation (GET Object with a Range header).
func TestSigV4MatchesTheAwsExample(t *testing.T) {
	req, _ := http.NewRequest("GET", "https://examplebucket.s3.amazonaws.com/test.txt", nil)
	req.Header.Set("Range", "bytes=0-9")
	signV4(req, "examplebucket.s3.amazonaws.com", "/test.txt", nil, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
		"AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "us-east-1", "s3", time.Date(2013, 5, 24, 0, 0, 0, 0, time.UTC))
	want := "AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request, SignedHeaders=host;range;x-amz-content-sha256;x-amz-date, Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41"
	if got := req.Header.Get("Authorization"); got != want {
		t.Fatalf("got %s", got)
	}
}

// a tiny S3 (path style) and Azure Blob in memory, enough for the repository
type fakeCloud struct {
	mu      sync.Mutex
	objects map[string][]byte
	azure   bool
}

func (f *fakeCloud) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.azure {
		if r.URL.Query().Get("sig") != "secret" {
			w.WriteHeader(403)
			return
		}
	} else if !strings.HasPrefix(r.Header.Get("Authorization"), "AWS4-HMAC-SHA256 Credential=AK/") {
		w.WriteHeader(403)
		return
	}
	key := strings.TrimPrefix(r.URL.Path, "/bucket/")
	if f.azure {
		key = strings.TrimPrefix(r.URL.Path, "/container/")
	}
	switch {
	case r.Method == "GET" && (r.URL.Query().Get("list-type") == "2" || r.URL.Query().Get("comp") == "list"):
		pfx := r.URL.Query().Get("prefix")
		var keys []string
		for k := range f.objects {
			if strings.HasPrefix(k, pfx) {
				keys = append(keys, k)
			}
		}
		sort.Strings(keys)
		if f.azure {
			type blob struct {
				Name string `xml:"Name"`
			}
			var res struct {
				XMLName xml.Name `xml:"EnumerationResults"`
				Blobs   []blob   `xml:"Blobs>Blob"`
			}
			for _, k := range keys {
				res.Blobs = append(res.Blobs, blob{k})
			}
			xml.NewEncoder(w).Encode(res)
			return
		}
		fmt.Fprint(w, "<ListBucketResult>")
		for _, k := range keys {
			fmt.Fprintf(w, "<Contents><Key>%s</Key></Contents>", k)
		}
		fmt.Fprint(w, "<IsTruncated>false</IsTruncated></ListBucketResult>")
	case r.Method == "PUT":
		if f.azure && r.Header.Get("x-ms-blob-type") != "BlockBlob" {
			w.WriteHeader(400)
			return
		}
		b, _ := io.ReadAll(r.Body)
		f.objects[key] = b
		w.WriteHeader(201)
	case r.Method == "GET":
		b, ok := f.objects[key]
		if !ok {
			w.WriteHeader(404)
			return
		}
		w.Write(b)
	case r.Method == "DELETE":
		delete(f.objects, key)
		w.WriteHeader(204)
	}
}

func TestS3AndAzureStores(t *testing.T) {
	ctx := context.Background()
	src := t.TempDir()
	write(t, filepath.Join(src, "a.txt"), []byte("cloud copy"))
	for _, azure := range []bool{false, true} {
		cloud := &fakeCloud{objects: map[string][]byte{}, azure: azure}
		srv := httptest.NewServer(cloud)
		var s Storage
		if azure {
			s = Storage{Kind: "azure", SasURL: srv.URL + "/container?sv=2021&sig=secret", Prefix: "office"}
		} else {
			s = Storage{Kind: "s3", Endpoint: srv.URL, Bucket: "bucket", Region: "eu-west-1", AccessKey: "AK", SecretKey: "SK", Prefix: "office"}
		}
		st, err := OpenStore(ctx, s)
		if err != nil {
			t.Fatal(err)
		}
		repo, err := OpenRepo(ctx, st, "pw")
		if err != nil {
			t.Fatal(azure, err)
		}
		sum, err := Backup(ctx, repo, Options{Plan: "p", Paths: []string{src}})
		if err != nil || sum.Files != 1 {
			t.Fatalf("azure=%v %+v %v", azure, sum, err)
		}
		if _, ok := cloud.objects["office/config"]; !ok {
			t.Fatalf("azure=%v: objects not under the prefix: %v", azure, len(cloud.objects))
		}
		repo2, _ := OpenRepo(ctx, st, "pw")
		dst := t.TempDir()
		if rs, err := Restore(ctx, repo2, sum.Snapshot, RestoreOptions{Target: dst}); err != nil || rs.Files != 1 {
			t.Fatalf("azure=%v restore %+v %v", azure, rs, err)
		}
		srv.Close()
	}
	if _, err := OpenStore(ctx, Storage{Kind: "azure", SasURL: "https://x.blob.core.windows.net/c"}); err == nil {
		t.Fatal("a container address without a SAS must be refused")
	}
}
