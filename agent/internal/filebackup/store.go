// Package filebackup is DeskAsk's own file backup: chosen folders are cut into variable-sized pieces (so a change in a large file only
// stores the changed part), each piece is compressed, encrypted with AES-256-GCM and stored once, and each run writes an encrypted list of
// files (a snapshot). Pieces and snapshots go to a folder or USB disk, an SMB share, an S3-compatible bucket or an Azure Blob container.
package filebackup

import (
	"context"
	"errors"
	"fmt"
	"io/fs"
	"os"
	"path"
	"path/filepath"
	"sort"
	"strings"
)

// ErrNotFound is returned by Get for a key the store does not hold.
var ErrNotFound = errors.New("not found in the backup storage")

// Store holds the repository's objects under slash-separated keys.
type Store interface {
	Put(ctx context.Context, key string, data []byte) error
	Get(ctx context.Context, key string) ([]byte, error)
	// List returns every key that starts with prefix.
	List(ctx context.Context, prefix string) ([]string, error)
	Delete(ctx context.Context, key string) error
	// Where says where the store is, without secrets (for messages).
	Where() string
}

// Storage is where a plan keeps its backups, as the platform sends it.
type Storage struct {
	Kind string `json:"kind"` // local, smb, s3, azure
	// local and smb: a folder (D:\Backups, E:\ for a USB disk, \\nas\backup\office)
	Path string `json:"path,omitempty"`
	// smb: the account that may write to the share (domain\user or user)
	User     string `json:"user,omitempty"`
	Password string `json:"password,omitempty"`
	// s3: endpoint (https://s3.eu-west-1.amazonaws.com, https://s3.wasabisys.com, https://minio.local:9000), region, bucket, prefix
	Endpoint  string `json:"endpoint,omitempty"`
	Region    string `json:"region,omitempty"`
	Bucket    string `json:"bucket,omitempty"`
	Prefix    string `json:"prefix,omitempty"`
	AccessKey string `json:"accessKey,omitempty"`
	SecretKey string `json:"secretKey,omitempty"`
	PathStyle bool   `json:"pathStyle,omitempty"`
	// azure: a container address with a SAS token allowing read, write, delete and list
	SasURL string `json:"sasUrl,omitempty"`
}

// OpenStore connects to the storage (for an SMB share with an account, it signs in to the share first).
func OpenStore(ctx context.Context, s Storage) (Store, error) {
	switch s.Kind {
	case "local":
		if strings.TrimSpace(s.Path) == "" {
			return nil, errors.New("no backup folder given")
		}
		return &fsStore{root: s.Path}, nil
	case "smb":
		if !strings.HasPrefix(s.Path, `\\`) && !strings.HasPrefix(s.Path, "//") {
			return nil, fmt.Errorf("%q is not a share address (\\\\server\\share\\folder)", s.Path)
		}
		if err := connectShare(s.Path, s.User, s.Password); err != nil {
			return nil, err
		}
		return &fsStore{root: s.Path}, nil
	case "s3":
		return newS3(s)
	case "azure":
		return newAzure(s)
	}
	return nil, fmt.Errorf("unknown storage kind %q", s.Kind)
}

// fsStore keeps objects as files under a folder (a local disk, a USB disk or a share).
type fsStore struct{ root string }

func (f *fsStore) Where() string { return f.root }

func (f *fsStore) file(key string) string { return filepath.Join(f.root, filepath.FromSlash(key)) }

func (f *fsStore) Put(ctx context.Context, key string, data []byte) error {
	p := f.file(key)
	if err := os.MkdirAll(filepath.Dir(p), 0o700); err != nil {
		return err
	}
	tmp := p + ".part"
	if err := os.WriteFile(tmp, data, 0o600); err != nil {
		return err
	}
	return os.Rename(tmp, p)
}

func (f *fsStore) Get(ctx context.Context, key string) ([]byte, error) {
	b, err := os.ReadFile(f.file(key))
	if errors.Is(err, fs.ErrNotExist) {
		return nil, ErrNotFound
	}
	return b, err
}

func (f *fsStore) List(ctx context.Context, prefix string) ([]string, error) {
	dir := path.Dir(prefix + "x")
	base := f.file(dir)
	var out []string
	err := filepath.WalkDir(base, func(p string, d fs.DirEntry, err error) error {
		if err != nil {
			if errors.Is(err, fs.ErrNotExist) {
				return nil
			}
			return err
		}
		if ctx.Err() != nil {
			return ctx.Err()
		}
		if d.IsDir() || strings.HasSuffix(p, ".part") {
			return nil
		}
		rel, err := filepath.Rel(f.root, p)
		if err != nil {
			return err
		}
		k := filepath.ToSlash(rel)
		if strings.HasPrefix(k, prefix) {
			out = append(out, k)
		}
		return nil
	})
	sort.Strings(out)
	return out, err
}

func (f *fsStore) Delete(ctx context.Context, key string) error {
	err := os.Remove(f.file(key))
	if errors.Is(err, fs.ErrNotExist) {
		return nil
	}
	return err
}
