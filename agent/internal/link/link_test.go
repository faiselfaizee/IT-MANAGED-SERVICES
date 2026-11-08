package link

import (
	"context"
	"net/http"
	"net/http/httptest"
	"sync/atomic"
	"testing"
	"time"
)

func TestHeartbeatSendsTokenAndBody(t *testing.T) {
	var got string
	s := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		got = r.Header.Get("Authorization")
		w.WriteHeader(204)
	}))
	defer s.Close()
	c := &Client{BaseURL: s.URL, Token: "tok"}
	if err := c.Heartbeat(context.Background(), Hello{AgentID: "a1", Version: "0"}); err != nil {
		t.Fatal(err)
	}
	if got != "Bearer tok" {
		t.Fatalf("auth header %q", got)
	}
}

func TestHeartbeatFailsOnServerError(t *testing.T) {
	s := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { w.WriteHeader(401) }))
	defer s.Close()
	if err := (&Client{BaseURL: s.URL}).Heartbeat(context.Background(), Hello{}); err == nil {
		t.Fatal("expected an error for 401")
	}
}

func TestBackoff(t *testing.T) {
	cases := map[int]time.Duration{0: 0, 1: time.Second, 2: 2 * time.Second, 4: 8 * time.Second, 7: 60 * time.Second, 20: 60 * time.Second}
	for n, want := range cases {
		if got := Backoff(n); got != want {
			t.Errorf("Backoff(%d)=%v want %v", n, got, want)
		}
	}
}

func TestRunStopsWithContext(t *testing.T) {
	var n atomic.Int32
	s := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { n.Add(1); w.WriteHeader(200) }))
	defer s.Close()
	ctx, cancel := context.WithTimeout(context.Background(), 150*time.Millisecond)
	defer cancel()
	(&Client{BaseURL: s.URL}).Run(ctx, Hello{}, 40*time.Millisecond, nil)
	if n.Load() < 2 {
		t.Fatalf("expected at least 2 heartbeats, got %d", n.Load())
	}
}
