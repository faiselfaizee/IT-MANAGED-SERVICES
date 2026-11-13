package collector

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"strings"
	"sync"
	"time"

	"github.com/deskask/platform/agent/internal/client"
)

// An install reports what it is doing, line by line, so a person watching the device page sees where it is and where it stops.
// The lines travel in the context: code deep inside the install calls step() and does not need to know where they go.

type stepKey struct{}

// stepFunc receives one line of progress; level is info, warn, error or ok.
type stepFunc func(level, msg string)

func withSteps(ctx context.Context, f stepFunc) context.Context {
	return context.WithValue(ctx, stepKey{}, f)
}

// step reports one line of progress. Without a listener (an inventory run, a test) it does nothing.
func step(ctx context.Context, level, format string, a ...any) {
	if f, ok := ctx.Value(stepKey{}).(stepFunc); ok && f != nil {
		f(level, trim(fmt.Sprintf(format, a...)))
	}
}

// lineWriter turns a command's output into progress lines as they appear.
type lineWriter struct {
	ctx    context.Context
	prefix string
	mu     sync.Mutex
	buf    []byte
	lines  int
}

const maxStreamedLines = 60

func (w *lineWriter) Write(p []byte) (int, error) {
	w.mu.Lock()
	defer w.mu.Unlock()
	w.buf = append(w.buf, p...)
	for {
		i := bytes.IndexByte(w.buf, '\n')
		if i < 0 {
			break
		}
		line := strings.TrimSpace(string(w.buf[:i]))
		w.buf = w.buf[i+1:]
		if line != "" && w.lines < maxStreamedLines {
			w.lines++
			step(w.ctx, "info", "%s%s", w.prefix, line)
		}
	}
	if len(w.buf) > 8192 {
		w.buf = w.buf[len(w.buf)-8192:]
	}
	return len(p), nil
}

// heartbeat says "still working" while a long step runs, so a quiet minute is not mistaken for a hang. Call the returned function when done.
func heartbeat(ctx context.Context, every time.Duration, what string) (stop func()) {
	done := make(chan struct{})
	start := time.Now()
	go func() {
		t := time.NewTicker(every)
		defer t.Stop()
		for {
			select {
			case <-done:
				return
			case <-ctx.Done():
				return
			case <-t.C:
				step(ctx, "info", "Still working: %s (%d s)", what, int(time.Since(start).Seconds()))
			}
		}
	}()
	var once sync.Once
	return func() { once.Do(func() { close(done) }) } // safe to call twice: a retry stops it early and the defer stops it again
}

func sleepCtx(ctx context.Context, d time.Duration) {
	select {
	case <-ctx.Done():
	case <-time.After(d):
	}
}

// progress sends one line of an install's live log. It is best effort: a lost line never fails the install.
func (r *Runner) progress(ctx context.Context, asset, level, msg string) {
	pctx, cancel := context.WithTimeout(ctx, 8*time.Second)
	defer cancel()
	_ = r.cl.Do(pctx, "POST", "/collector/v1/progress", map[string]any{"asset": asset, "level": level, "msg": msg}, nil)
}

// kick asks the run loop to check in now (a result is ready, or the platform has new work).
func (r *Runner) kick() {
	select {
	case r.wake <- struct{}{}:
	default:
	}
}

// wakeLoop waits on the platform. The platform answers the moment something needs this controller (an install, a scan, a setting),
// so a button pressed in the browser starts work within a second instead of at the end of the one-minute check-in.
func (r *Runner) wakeLoop(ctx context.Context) {
	failures := 0
	for ctx.Err() == nil {
		var out struct {
			Now bool `json:"now"`
		}
		pctx, cancel := context.WithTimeout(ctx, 45*time.Second)
		err := r.cl.Do(pctx, "GET", "/collector/v1/wait?wait=25", nil, &out)
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
				wait = 10 * time.Minute // a platform without instant wake-up: the one-minute check-in still works
			default:
				failures++
				wait = min(time.Second<<min(failures, 6), 60*time.Second)
			}
			sleepCtx(ctx, wait)
			continue
		}
		failures = 0
		if out.Now {
			r.kick()
		} else {
			sleepCtx(ctx, 300*time.Millisecond) // never spin if the platform answers at once
		}
	}
}
