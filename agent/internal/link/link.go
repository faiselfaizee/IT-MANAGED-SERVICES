// Package link is the agent's one outbound connection to the platform (ADR 0005): the agent calls out, nothing calls in.
package link

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"time"
)

// ErrRevoked means the platform does not accept this agent token (any more). Retrying will not help.
var ErrRevoked = errors.New("the platform does not accept this agent token")

// Response is what the platform answers to a heartbeat.
type Response struct {
	OK              bool `json:"ok"`
	IntervalSeconds int  `json:"intervalSeconds"`
}

type Hello struct {
	AgentID string   `json:"agentId"`
	Version string   `json:"version"`
	Plugins []string `json:"plugins"`
}

type Client struct {
	BaseURL string
	Token   string
	HTTP    *http.Client
}

func (c *Client) Heartbeat(ctx context.Context, h Hello) (Response, error) {
	var out Response
	b, _ := json.Marshal(h)
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, c.BaseURL+"/api/connect/heartbeat", bytes.NewReader(b))
	if err != nil {
		return out, err
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Authorization", "Bearer "+c.Token)
	hc := c.HTTP
	if hc == nil {
		hc = &http.Client{Timeout: 15 * time.Second}
	}
	resp, err := hc.Do(req)
	if err != nil {
		return out, err
	}
	defer resp.Body.Close()
	if resp.StatusCode == http.StatusUnauthorized || resp.StatusCode == http.StatusForbidden {
		return out, ErrRevoked
	}
	if resp.StatusCode/100 != 2 {
		return out, fmt.Errorf("platform answered %d", resp.StatusCode)
	}
	_ = json.NewDecoder(resp.Body).Decode(&out) // an empty answer is fine: keep our own interval
	return out, nil
}

// Backoff doubles from 1 s up to 60 s: a platform restart never makes agents hammer it.
func Backoff(failures int) time.Duration {
	if failures <= 0 {
		return 0
	}
	d := time.Second << min(failures-1, 6)
	return min(d, 60*time.Second)
}

// Run sends a heartbeat every interval until ctx ends, backing off while the platform is unreachable and
// following the interval the platform asks for. It returns ErrRevoked when the token is refused, nil when ctx ends.
func (c *Client) Run(ctx context.Context, h Hello, every time.Duration, onErr func(error)) error {
	fails := 0
	for {
		resp, err := c.Heartbeat(ctx, h)
		if errors.Is(err, ErrRevoked) {
			return err
		}
		if err != nil {
			fails++
			if onErr != nil && ctx.Err() == nil {
				onErr(err)
			}
		} else {
			fails = 0
			if resp.IntervalSeconds > 0 {
				every = time.Duration(resp.IntervalSeconds) * time.Second
			}
		}
		wait := every
		if fails > 0 {
			wait = Backoff(fails)
		}
		select {
		case <-ctx.Done():
			return nil
		case <-time.After(wait):
		}
	}
}
