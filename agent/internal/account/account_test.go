//go:build !windows

package account

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestSetPasswordFeedsChpasswdOnStdin(t *testing.T) {
	dir := t.TempDir()
	got := filepath.Join(dir, "got")
	script := filepath.Join(dir, "chpasswd")
	if err := os.WriteFile(script, []byte("#!/bin/sh\ncat > "+got+"\necho \"$@\" >> "+got+"\n"), 0o755); err != nil {
		t.Fatal(err)
	}
	old := chpasswd
	chpasswd = script
	defer func() { chpasswd = old }()
	if err := SetPassword("svc-backup", "linux", "N3w:Pass!word"); err != nil {
		t.Fatal(err)
	}
	b, _ := os.ReadFile(got)
	if !strings.HasPrefix(string(b), "svc-backup:N3w:Pass!word\n") || strings.Contains(strings.SplitN(string(b), "\n", 2)[1], "Pass") {
		t.Fatalf("chpasswd got %q (the password must be on stdin only)", b)
	}
}

func TestSetPasswordRejectsBadInput(t *testing.T) {
	for _, c := range []struct{ acct, pw string }{{"-o", "x"}, {"../etc", "x"}, {"root\nx", "x"}, {"ok", ""}, {"ok", "a\nb"}} {
		if err := SetPassword(c.acct, "linux", c.pw); err == nil {
			t.Errorf("SetPassword(%q, %q) should fail", c.acct, c.pw)
		}
	}
	if err := SetPassword("ok", "domain", "x"); err == nil {
		t.Error("a Linux agent must refuse domain accounts")
	}
}

func TestChpasswdFailureIsReported(t *testing.T) {
	dir := t.TempDir()
	script := filepath.Join(dir, "chpasswd")
	_ = os.WriteFile(script, []byte("#!/bin/sh\necho 'user nobody2 does not exist' >&2\nexit 1\n"), 0o755)
	old := chpasswd
	chpasswd = script
	defer func() { chpasswd = old }()
	if err := SetPassword("nobody2", "linux", "x"); err == nil || !strings.Contains(err.Error(), "does not exist") {
		t.Fatalf("got %v", err)
	}
}
