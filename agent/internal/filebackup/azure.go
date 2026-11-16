package filebackup

import (
	"bytes"
	"context"
	"encoding/xml"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"sort"
	"strings"
	"time"
)

// azureStore keeps objects as block blobs in an Azure Storage container, reached with a container SAS address
// (https://account.blob.core.windows.net/container?sv=...&sig=...) that allows read, add, create, write, delete and list.
type azureStore struct {
	container *url.URL // without the query
	sas       string   // the query, without "?"
	prefix    string
	hc        *http.Client
}

func newAzure(s Storage) (*azureStore, error) {
	u, err := url.Parse(strings.TrimSpace(s.SasURL))
	if err != nil || u.Host == "" || u.RawQuery == "" || !strings.Contains(u.RawQuery, "sig=") {
		return nil, errors.New("give the container's SAS address (https://account.blob.core.windows.net/container?sv=...&sig=...)")
	}
	if u.Scheme != "https" && !strings.HasPrefix(u.Host, "127.0.0.1") && !strings.HasPrefix(u.Host, "localhost") {
		return nil, errors.New("the SAS address must start with https://")
	}
	sas := u.RawQuery
	u.RawQuery = ""
	u.Path = strings.TrimRight(u.Path, "/")
	return &azureStore{container: u, sas: sas, prefix: cleanPrefix(s.Prefix), hc: &http.Client{Timeout: 5 * time.Minute}}, nil
}

func (a *azureStore) Where() string { return a.container.String() + "/" + a.prefix }

func (a *azureStore) blob(key string) string {
	return a.container.String() + "/" + uriEncode(a.prefix+key, false) + "?" + a.sas
}

func (a *azureStore) do(ctx context.Context, method, u string, body []byte, hdr map[string]string) (*http.Response, error) {
	req, err := http.NewRequestWithContext(ctx, method, u, bytes.NewReader(body))
	if err != nil {
		return nil, err
	}
	req.ContentLength = int64(len(body))
	req.Header.Set("x-ms-version", "2021-08-06")
	for k, v := range hdr {
		req.Header.Set(k, v)
	}
	resp, err := a.hc.Do(req)
	if err != nil {
		return nil, err
	}
	if resp.StatusCode >= 300 && resp.StatusCode != 404 {
		b, _ := io.ReadAll(io.LimitReader(resp.Body, 4096))
		resp.Body.Close()
		var e struct {
			Code    string `xml:"Code"`
			Message string `xml:"Message"`
		}
		msg := strings.TrimSpace(string(b))
		if xml.Unmarshal(b, &e) == nil && e.Code != "" {
			msg = e.Code + ": " + strings.SplitN(e.Message, "\n", 2)[0]
		}
		return nil, fmt.Errorf("Azure %s: %s %s", method, resp.Status, msg)
	}
	return resp, nil
}

func (a *azureStore) Put(ctx context.Context, key string, data []byte) error {
	resp, err := a.do(ctx, http.MethodPut, a.blob(key), data, map[string]string{"x-ms-blob-type": "BlockBlob", "Content-Type": "application/octet-stream"})
	if err != nil {
		return err
	}
	resp.Body.Close()
	if resp.StatusCode == 404 {
		return errors.New("Azure: the container does not exist")
	}
	return nil
}

func (a *azureStore) Get(ctx context.Context, key string) ([]byte, error) {
	resp, err := a.do(ctx, http.MethodGet, a.blob(key), nil, nil)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode == 404 {
		return nil, ErrNotFound
	}
	return io.ReadAll(resp.Body)
}

func (a *azureStore) Delete(ctx context.Context, key string) error {
	resp, err := a.do(ctx, http.MethodDelete, a.blob(key), nil, nil)
	if err != nil {
		return err
	}
	resp.Body.Close()
	return nil
}

func (a *azureStore) List(ctx context.Context, prefix string) ([]string, error) {
	var out []string
	marker := ""
	for {
		q := "restype=container&comp=list&maxresults=5000&prefix=" + url.QueryEscape(a.prefix+prefix)
		if marker != "" {
			q += "&marker=" + url.QueryEscape(marker)
		}
		resp, err := a.do(ctx, http.MethodGet, a.container.String()+"?"+q+"&"+a.sas, nil, nil)
		if err != nil {
			return nil, err
		}
		if resp.StatusCode == 404 {
			resp.Body.Close()
			return nil, errors.New("Azure: the container does not exist")
		}
		var r struct {
			Blobs struct {
				Blob []struct {
					Name string `xml:"Name"`
				} `xml:"Blob"`
			} `xml:"Blobs"`
			NextMarker string `xml:"NextMarker"`
		}
		err = xml.NewDecoder(resp.Body).Decode(&r)
		resp.Body.Close()
		if err != nil {
			return nil, err
		}
		for _, b := range r.Blobs.Blob {
			out = append(out, strings.TrimPrefix(b.Name, a.prefix))
		}
		if r.NextMarker == "" {
			break
		}
		marker = r.NextMarker
	}
	sort.Strings(out)
	return out, nil
}
