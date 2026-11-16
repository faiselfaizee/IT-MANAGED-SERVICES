package backup

import (
	"encoding/base64"
	"strings"
	"testing"
	"unicode/utf16"
)

func TestParseKeepsFinishedRunsAndAcceptsOneItemLists(t *testing.T) {
	out := []byte("WARNING: module loaded\r\n" + `{"tools":["wsb","veeam"],"reports":[{"tool":"wsb","job":"Windows Server Backup","started":"2026-11-16T01:00:00.0000000Z","ended":"2026-11-16T02:00:00.0000000Z","result":"success","message":"","bytes":0,"key":"wsb:1"},{"tool":"veeam","job":"  ","result":"failed","key":"x"},{"tool":"veeam","job":"Nightly","result":"running","key":"y"}]}` + "\r\n")
	f, err := Parse(out)
	if err != nil {
		t.Fatal(err)
	}
	if len(f.Tools) != 2 || len(f.Reports) != 1 || f.Reports[0].Job != "Windows Server Backup" || f.Reports[0].Key != "wsb:1" {
		t.Fatalf("got %+v", f)
	}
	one, err := Parse([]byte(`{"tools":"veeam","reports":{"tool":"veeam","job":"SQL","ended":"2026-11-16T02:00:00Z","result":"failed","message":"no space","bytes":0,"key":"veeam:Veeam Backup:9"}}`))
	if err != nil || len(one.Tools) != 1 || len(one.Reports) != 1 || one.Reports[0].Result != "failed" {
		t.Fatalf("got %+v, %v", one, err)
	}
	empty, err := Parse([]byte(`{"tools":[],"reports":[]}`))
	if err != nil || len(empty.Tools) != 0 || len(empty.Reports) != 0 {
		t.Fatalf("got %+v, %v", empty, err)
	}
	if _, err := Parse([]byte("The term 'powershell' is not recognized")); err == nil {
		t.Fatal("expected an error without JSON")
	}
}

func TestEncodedIsTheScriptInUtf16(t *testing.T) {
	b, err := base64.StdEncoding.DecodeString(Encoded())
	if err != nil || len(b)%2 != 0 {
		t.Fatal(err)
	}
	u := make([]uint16, len(b)/2)
	for i := range u {
		u[i] = uint16(b[2*i]) | uint16(b[2*i+1])<<8
	}
	if s := string(utf16.Decode(u)); s != Script {
		t.Fatal("round trip differs")
	}
	for _, want := range []string{"Get-WBJob", "Get-OBJob", "Get-DPMJob", "Veeam Agent", "ConvertTo-Json"} {
		if !strings.Contains(Script, want) {
			t.Fatalf("script lacks %s", want)
		}
	}
}
