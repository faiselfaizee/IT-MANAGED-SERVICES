// Package client talks to the platform: one small JSON-over-HTTPS client used by the agent and the collector.
package client

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"strings"
	"time"
)

// ErrAuth means the platform refused the key (the device was removed or the key was revoked).
var ErrAuth = errors.New("the platform does not accept this key")

type Client struct {
	Server   string // https://acme.deskask.in
	IDHeader string // X-Agent-Id or X-Collector-Id
	ID       string
	Key      string
	HTTP     *http.Client
}

func (c *Client) hc() *http.Client {
	if c.HTTP != nil {
		return c.HTTP
	}
	return &http.Client{Timeout: 60 * time.Second}
}

// Error carries the platform's answer so a person can read what went wrong.
type Error struct {
	Status int
	Body   string
}

func (e *Error) Error() string {
	return fmt.Sprintf("the platform answered %d: %s", e.Status, strings.TrimSpace(e.Body))
}

// Do sends body (any JSON value, or nil) and decodes the JSON answer into out (may be nil).
func (c *Client) Do(ctx context.Context, method, path string, body, out any) error {
	var rd io.Reader
	if body != nil {
		b, err := json.Marshal(body)
		if err != nil {
			return err
		}
		rd = bytes.NewReader(b)
	}
	req, err := http.NewRequestWithContext(ctx, method, strings.TrimRight(c.Server, "/")+path, rd)
	if err != nil {
		return err
	}
	req.Header.Set("Content-Type", "application/json")
	if c.Key != "" {
		req.Header.Set("Authorization", "Bearer "+c.Key)
		req.Header.Set(c.IDHeader, c.ID)
	}
	resp, err := c.hc().Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	data, _ := io.ReadAll(io.LimitReader(resp.Body, 16<<20))
	if resp.StatusCode == http.StatusUnauthorized && c.Key != "" {
		return ErrAuth
	}
	if resp.StatusCode/100 != 2 {
		return &Error{resp.StatusCode, string(data)}
	}
	if out != nil && len(data) > 0 {
		return json.Unmarshal(data, out)
	}
	return nil
}

// Download saves an answer body to a file.
func (c *Client) Download(ctx context.Context, path string, w io.Writer) error {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, strings.TrimRight(c.Server, "/")+path, nil)
	if err != nil {
		return err
	}
	req.Header.Set("Authorization", "Bearer "+c.Key)
	req.Header.Set(c.IDHeader, c.ID)
	hc := *c.hc()
	hc.Timeout = 15 * time.Minute
	resp, err := hc.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode == http.StatusUnauthorized {
		return ErrAuth
	}
	if resp.StatusCode/100 != 2 {
		b, _ := io.ReadAll(io.LimitReader(resp.Body, 2048))
		return &Error{resp.StatusCode, string(b)}
	}
	_, err = io.Copy(w, resp.Body)
	return err
}
