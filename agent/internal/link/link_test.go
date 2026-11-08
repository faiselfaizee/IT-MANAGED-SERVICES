package link

import (
	"context"
	"errors"
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
	if _, err := c.Heartbeat(context.Background(), Hello{AgentID: "a1", Version: "0"}); err != nil {
		t.Fatal(err)
	}
	if got != "Bearer tok" {
		t.Fatalf("auth header %q", got)
	}
}

func TestHeartbeatRefusedTokenIsRevoked(t *testing.T) {
	s := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { w.WriteHeader(401) }))
	defer s.Close()
	if _, err := (&Client{BaseURL: s.URL}).Heartbeat(context.Background(), Hello{}); !errors.Is(err, ErrRevoked) {
		t.Fatalf("expected ErrRevoked, got %v", err)
	}
}

func TestHeartbeatServerErrorIsNotRevoked(t *testing.T) {
	s := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { w.WriteHeader(500) }))
	defer s.Close()
	_, err := (&Client{BaseURL: s.URL}).Heartbeat(context.Background(), Hello{})
	if err == nil || errors.Is(err, ErrRevoked) {
		t.Fatalf("expected a plain error, got %v", err)
	}
}

func TestHeartbeatReadsInterval(t *testing.T) {
	s := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Write([]byte(`{"ok":true,"intervalSeconds":45,"commands":[]}`))
	}))
	defer s.Close()
	r, err := (&Client{BaseURL: s.URL}).Heartbeat(context.Background(), Hello{})
	if err != nil || r.IntervalSeconds != 45 {
		t.Fatalf("got %+v, %v", r, err)
	}
}

func TestRunStopsWhenRevoked(t *testing.T) {
	s := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { w.WriteHeader(401) }))
	defer s.Close()
	err := (&Client{BaseURL: s.URL}).Run(context.Background(), Hello{}, 10*time.Millisecond, nil)
	if !errors.Is(err, ErrRevoked) {
		t.Fatalf("expected ErrRevoked, got %v", err)
	}
}

func TestRunFollowsServerInterval(t *testing.T) {
	var n atomic.Int32
	s := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		n.Add(1)
		w.Write([]byte(`{"ok":true,"intervalSeconds":3600}`))
	}))
	defer s.Close()
	ctx, cancel := context.WithTimeout(context.Background(), 200*time.Millisecond)
	defer cancel()
	(&Client{BaseURL: s.URL}).Run(ctx, Hello{}, 20*time.Millisecond, nil)
	if n.Load() != 1 {
		t.Fatalf("after the platform asked for 3600 s only one heartbeat was expected, got %d", n.Load())
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
