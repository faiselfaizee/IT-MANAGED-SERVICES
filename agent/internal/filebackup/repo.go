package filebackup

import (
	"bufio"
	"bytes"
	"compress/flate"
	"context"
	"crypto/aes"
	"crypto/cipher"
	"crypto/hmac"
	"crypto/pbkdf2"
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"sort"
	"strings"
	"sync"
	"time"
)

// Chunk sizes: pieces are cut where the content says (so inserting bytes moves only nearby cuts), 512 KiB to 8 MiB, about 1 MiB on average.
const (
	minChunk = 512 << 10
	maxChunk = 8 << 20
	cutMask  = (1 << 20) - 1
)

var gear = func() (t [256]uint64) {
	x := uint64(0x9E3779B97F4A7C15) // a fixed seed: every agent cuts the same file the same way
	for i := range t {
		x += 0x9E3779B97F4A7C15
		z := x
		z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9
		z = (z ^ (z >> 27)) * 0x94D049BB133111EB
		t[i] = z ^ (z >> 31)
	}
	return
}()

// chunker reads pieces from r.
type chunker struct {
	r   *bufio.Reader
	buf []byte
}

func newChunker(r io.Reader) *chunker { return &chunker{r: bufio.NewReaderSize(r, 1<<20)} }

// Next returns the next piece (valid until the next call), or io.EOF.
func (c *chunker) Next() ([]byte, error) {
	c.buf = c.buf[:0]
	var h uint64
	for {
		b, err := c.r.ReadByte()
		if err == io.EOF {
			if len(c.buf) == 0 {
				return nil, io.EOF
			}
			return c.buf, nil
		}
		if err != nil {
			return nil, err
		}
		c.buf = append(c.buf, b)
		h = (h << 1) + gear[b]
		if n := len(c.buf); n >= maxChunk || (n >= minChunk && h&cutMask == 0) {
			return c.buf, nil
		}
	}
}

// repoConfig is the one plain object in a repository: how to turn the password into the key that opens the repository's keys.
type repoConfig struct {
	Version int    `json:"version"`
	KDF     string `json:"kdf"`
	Iter    int    `json:"iter"`
	Salt    string `json:"salt"`
	Keys    string `json:"keys"` // the 64 key bytes, sealed with the password key
	Created string `json:"created"`
}

// Repo is an open repository.
type Repo struct {
	st     Store
	encKey []byte // AES-256 key for every object
	idKey  []byte // HMAC key that names pieces (so names say nothing about the content)
	mu     sync.Mutex
	have   map[string]bool // pieces known to be stored
}

const kdfIter = 600_000

// OpenRepo opens the repository in st, or creates it there when the storage is empty.
func OpenRepo(ctx context.Context, st Store, password string) (*Repo, error) {
	if password == "" {
		return nil, errors.New("the backup has no password")
	}
	raw, err := st.Get(ctx, "config")
	if errors.Is(err, ErrNotFound) {
		return createRepo(ctx, st, password)
	}
	if err != nil {
		return nil, fmt.Errorf("reading the backup storage (%s): %w", st.Where(), err)
	}
	var c repoConfig
	if err := json.Unmarshal(raw, &c); err != nil || c.Version != 1 || c.KDF != "pbkdf2-sha256" {
		return nil, fmt.Errorf("%s holds something that is not a DeskAsk backup", st.Where())
	}
	salt, _ := hex.DecodeString(c.Salt)
	sealed, _ := hex.DecodeString(c.Keys)
	kek, err := pbkdf2.Key(sha256.New, password, salt, c.Iter, 32)
	if err != nil {
		return nil, err
	}
	keys, err := open(kek, sealed, []byte("keys"))
	if err != nil || len(keys) != 64 {
		return nil, errors.New("the backup password does not open this backup storage")
	}
	return &Repo{st: st, encKey: keys[:32], idKey: keys[32:]}, nil
}

func createRepo(ctx context.Context, st Store, password string) (*Repo, error) {
	keys := make([]byte, 64)
	salt := make([]byte, 16)
	if _, err := rand.Read(keys); err != nil {
		return nil, err
	}
	if _, err := rand.Read(salt); err != nil {
		return nil, err
	}
	kek, err := pbkdf2.Key(sha256.New, password, salt, kdfIter, 32)
	if err != nil {
		return nil, err
	}
	sealed, err := seal(kek, keys, []byte("keys"))
	if err != nil {
		return nil, err
	}
	c, _ := json.Marshal(repoConfig{Version: 1, KDF: "pbkdf2-sha256", Iter: kdfIter, Salt: hex.EncodeToString(salt), Keys: hex.EncodeToString(sealed), Created: time.Now().UTC().Format(time.RFC3339)})
	if err := st.Put(ctx, "config", c); err != nil {
		return nil, fmt.Errorf("writing to the backup storage (%s): %w", st.Where(), err)
	}
	return &Repo{st: st, encKey: keys[:32], idKey: keys[32:], have: map[string]bool{}}, nil
}

func seal(key, plain, ad []byte) ([]byte, error) {
	b, err := aes.NewCipher(key)
	if err != nil {
		return nil, err
	}
	g, err := cipher.NewGCM(b)
	if err != nil {
		return nil, err
	}
	nonce := make([]byte, g.NonceSize())
	if _, err := rand.Read(nonce); err != nil {
		return nil, err
	}
	return g.Seal(nonce, nonce, plain, ad), nil
}

func open(key, sealed, ad []byte) ([]byte, error) {
	b, err := aes.NewCipher(key)
	if err != nil {
		return nil, err
	}
	g, err := cipher.NewGCM(b)
	if err != nil {
		return nil, err
	}
	if len(sealed) < g.NonceSize() {
		return nil, errors.New("too short")
	}
	return g.Open(nil, sealed[:g.NonceSize()], sealed[g.NonceSize():], ad)
}

func pack(key, plain, ad []byte) ([]byte, error) {
	var z bytes.Buffer
	w, _ := flate.NewWriter(&z, flate.DefaultCompression)
	w.Write(plain)
	w.Close()
	return seal(key, z.Bytes(), ad)
}

func unpack(key, sealed, ad []byte) ([]byte, error) {
	z, err := open(key, sealed, ad)
	if err != nil {
		return nil, errors.New("a stored piece does not open: it was changed or damaged")
	}
	return io.ReadAll(flate.NewReader(bytes.NewReader(z)))
}

func (r *Repo) pieceID(data []byte) string {
	m := hmac.New(sha256.New, r.idKey)
	m.Write(data)
	return hex.EncodeToString(m.Sum(nil))
}

func pieceKey(id string) string { return "data/" + id[:2] + "/" + id }

// loadIndex learns which pieces are stored (once per run).
func (r *Repo) loadIndex(ctx context.Context) error {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.have != nil {
		return nil
	}
	keys, err := r.st.List(ctx, "data/")
	if err != nil {
		return err
	}
	r.have = make(map[string]bool, len(keys))
	for _, k := range keys {
		r.have[k[strings.LastIndexByte(k, '/')+1:]] = true
	}
	return nil
}

// putPiece stores a piece unless it is already there; it reports whether it was new and the stored size.
func (r *Repo) putPiece(ctx context.Context, id string, data []byte) (bool, int, error) {
	r.mu.Lock()
	if r.have[id] {
		r.mu.Unlock()
		return false, 0, nil
	}
	r.mu.Unlock()
	sealed, err := pack(r.encKey, data, []byte(id))
	if err != nil {
		return false, 0, err
	}
	if err := r.st.Put(ctx, pieceKey(id), sealed); err != nil {
		return false, 0, err
	}
	r.mu.Lock()
	r.have[id] = true
	r.mu.Unlock()
	return true, len(sealed), nil
}

func (r *Repo) getPiece(ctx context.Context, id string) ([]byte, error) {
	sealed, err := r.st.Get(ctx, pieceKey(id))
	if err != nil {
		return nil, fmt.Errorf("piece %s…: %w", id[:12], err)
	}
	data, err := unpack(r.encKey, sealed, []byte(id))
	if err != nil {
		return nil, err
	}
	if r.pieceID(data) != id {
		return nil, errors.New("a stored piece does not match its name")
	}
	return data, nil
}

// File is one file in a snapshot.
type File struct {
	Path   string   `json:"p"`
	Size   int64    `json:"s"`
	Mode   uint32   `json:"m"`
	MTime  int64    `json:"t"` // Unix nanoseconds
	Pieces []string `json:"c"`
}

// Snapshot is what one run stored.
type Snapshot struct {
	ID       string   `json:"id"`
	Time     string   `json:"time"`
	Host     string   `json:"host"`
	Plan     string   `json:"plan"`
	Paths    []string `json:"paths"`
	Files    []File   `json:"files"`
	Bytes    int64    `json:"bytes"`    // size of all files
	NewBytes int64    `json:"newBytes"` // stored by this run (compressed and encrypted)
	Warnings []string `json:"warnings,omitempty"`
}

func (r *Repo) saveSnapshot(ctx context.Context, s *Snapshot) error {
	plain, _ := json.Marshal(s)
	sealed, err := pack(r.encKey, plain, []byte("snapshot:"+s.ID))
	if err != nil {
		return err
	}
	return r.st.Put(ctx, "snapshots/"+s.ID, sealed)
}

// LoadSnapshot reads one snapshot.
func (r *Repo) LoadSnapshot(ctx context.Context, id string) (*Snapshot, error) {
	sealed, err := r.st.Get(ctx, "snapshots/"+id)
	if err != nil {
		return nil, fmt.Errorf("snapshot %s: %w", id, err)
	}
	plain, err := unpack(r.encKey, sealed, []byte("snapshot:"+id))
	if err != nil {
		return nil, err
	}
	var s Snapshot
	return &s, json.Unmarshal(plain, &s)
}

// Snapshots lists the snapshot ids, oldest first (ids start with the time).
func (r *Repo) Snapshots(ctx context.Context) ([]string, error) {
	keys, err := r.st.List(ctx, "snapshots/")
	if err != nil {
		return nil, err
	}
	var ids []string
	for _, k := range keys {
		ids = append(ids, strings.TrimPrefix(k, "snapshots/"))
	}
	sort.Strings(ids)
	return ids, nil
}

func newSnapshotID(t time.Time) string {
	b := make([]byte, 4)
	rand.Read(b)
	return t.UTC().Format("20060102T150405.000000") + "Z-" + hex.EncodeToString(b)
}

// SnapshotTime reads the time from a snapshot id.
func SnapshotTime(id string) (time.Time, bool) {
	if len(id) < 15 {
		return time.Time{}, false
	}
	t, err := time.Parse("20060102T150405", id[:15])
	return t, err == nil
}
