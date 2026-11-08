// Package link is the agent's one outbound connection to the platform (ADR 0005): the agent calls out, nothing calls in.
package link

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"time"
)

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

func (c *Client) Heartbeat(ctx context.Context, h Hello) error {
	b, _ := json.Marshal(h)
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, c.BaseURL+"/api/connect/heartbeat", bytes.NewReader(b))
	if err != nil {
		return err
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Authorization", "Bearer "+c.Token)
	hc := c.HTTP
	if hc == nil {
		hc = &http.Client{Timeout: 15 * time.Second}
	}
	resp, err := hc.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode/100 != 2 {
		return fmt.Errorf("platform answered %d", resp.StatusCode)
	}
	return nil
}

// Backoff doubles from 1 s up to 60 s: a platform restart never makes agents hammer it.
func Backoff(failures int) time.Duration {
	if failures <= 0 {
		return 0
	}
	d := time.Second << min(failures-1, 6)
	return min(d, 60*time.Second)
}

// Run sends a heartbeat every interval until ctx ends, backing off while the platform is unreachable.
func (c *Client) Run(ctx context.Context, h Hello, every time.Duration, onErr func(error)) {
	fails := 0
	for {
		if err := c.Heartbeat(ctx, h); err != nil {
			fails++
			if onErr != nil {
				onErr(err)
			}
		} else {
			fails = 0
		}
		wait := every
		if fails > 0 {
			wait = Backoff(fails)
		}
		select {
		case <-ctx.Done():
			return
		case <-time.After(wait):
		}
	}
}
