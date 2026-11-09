// Package bundle is the signed release bundle (ADR 0009): a zip holding payload/<files>, manifest.json (what every file must hash to)
// and manifest.sig (ES256 signature of manifest.json by the release key). Nothing is extracted before the signature and the
// content list have been checked, and every extracted file is hashed against the manifest.
package bundle

import (
	"archive/zip"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/x509"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"encoding/pem"
	"errors"
	"fmt"
	"io"
	"math/big"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"
)

const (
	Format        = 1
	Product       = "deskask-platform"
	ManifestName  = "manifest.json"
	SigName       = "manifest.sig"
	PayloadPrefix = "payload/"
	maxManifest   = 10 << 20
	maxTotal      = 8 << 30
)

var (
	ErrBadSignature = errors.New("bundle: the signature is not valid for any trusted release key")
	ErrBadBundle    = errors.New("bundle: the bundle is damaged or not a DeskAsk bundle")
)

type File struct {
	Path   string `json:"path"`
	SHA256 string `json:"sha256"`
	Size   int64  `json:"size"`
	Mode   uint32 `json:"mode"`
}

type Manifest struct {
	Format    int    `json:"format"`
	Product   string `json:"product"`
	Component string `json:"component"`
	Version   string `json:"version"`
	Created   string `json:"created"`
	KeyID     string `json:"keyId"`
	Files     []File `json:"files"`
}

// ---------------------------------------------------------------- keys

func GenerateKey() (*ecdsa.PrivateKey, error) { return ecdsa.GenerateKey(elliptic.P256(), rand.Reader) }

func KeyID(pub *ecdsa.PublicKey) string {
	der, _ := x509.MarshalPKIXPublicKey(pub)
	h := sha256.Sum256(der)
	return hex.EncodeToString(h[:])[:16]
}

func MarshalPrivate(k *ecdsa.PrivateKey) ([]byte, error) {
	der, err := x509.MarshalPKCS8PrivateKey(k)
	if err != nil {
		return nil, err
	}
	return pem.EncodeToMemory(&pem.Block{Type: "PRIVATE KEY", Bytes: der}), nil
}

func MarshalPublic(pub *ecdsa.PublicKey) ([]byte, error) {
	der, err := x509.MarshalPKIXPublicKey(pub)
	if err != nil {
		return nil, err
	}
	return pem.EncodeToMemory(&pem.Block{Type: "PUBLIC KEY", Bytes: der}), nil
}

func ParsePrivate(p []byte) (*ecdsa.PrivateKey, error) {
	b, _ := pem.Decode(p)
	if b == nil {
		return nil, errors.New("bundle: no PEM block in the private key")
	}
	k, err := x509.ParsePKCS8PrivateKey(b.Bytes)
	if err != nil {
		return nil, err
	}
	ek, ok := k.(*ecdsa.PrivateKey)
	if !ok || ek.Curve != elliptic.P256() {
		return nil, errors.New("bundle: the release key must be an ECDSA P-256 key")
	}
	return ek, nil
}

// ParsePublicSet reads one or more PUBLIC KEY blocks (several allow a key to be replaced without a flag day).
func ParsePublicSet(p []byte) ([]*ecdsa.PublicKey, error) {
	var out []*ecdsa.PublicKey
	for {
		var b *pem.Block
		b, p = pem.Decode(p)
		if b == nil {
			break
		}
		k, err := x509.ParsePKIXPublicKey(b.Bytes)
		if err != nil {
			return nil, err
		}
		ek, ok := k.(*ecdsa.PublicKey)
		if !ok || ek.Curve != elliptic.P256() {
			return nil, errors.New("bundle: release keys must be ECDSA P-256")
		}
		out = append(out, ek)
	}
	if len(out) == 0 {
		return nil, errors.New("bundle: no public key found")
	}
	return out, nil
}

func sign(k *ecdsa.PrivateKey, data []byte) ([]byte, error) {
	h := sha256.Sum256(data)
	r, s, err := ecdsa.Sign(rand.Reader, k, h[:])
	if err != nil {
		return nil, err
	}
	sig := make([]byte, 64)
	r.FillBytes(sig[:32])
	s.FillBytes(sig[32:])
	return sig, nil
}

func verifyAny(pubs []*ecdsa.PublicKey, data, sig []byte) bool {
	if len(sig) != 64 {
		return false
	}
	h := sha256.Sum256(data)
	r, s := new(big.Int).SetBytes(sig[:32]), new(big.Int).SetBytes(sig[32:])
	for _, p := range pubs {
		if ecdsa.Verify(p, h[:], r, s) {
			return true
		}
	}
	return false
}

// ---------------------------------------------------------------- paths

// SafePath accepts only plain relative paths with forward slashes: no "..", no absolute or drive paths, no empty parts.
func SafePath(p string) bool {
	if p == "" || strings.ContainsAny(p, "\\:\x00") || strings.HasPrefix(p, "/") {
		return false
	}
	for _, part := range strings.Split(p, "/") {
		if part == "" || part == "." || part == ".." {
			return false
		}
	}
	return true
}

// ---------------------------------------------------------------- pack

// Pack builds the bundle from the files under dir. m carries product data (Component, Version); files, key id and time are filled in.
func Pack(dir, out string, m Manifest, key *ecdsa.PrivateKey) error {
	m.Format, m.Product, m.KeyID, m.Created = Format, Product, KeyID(&key.PublicKey), time.Now().UTC().Format(time.RFC3339)
	if m.Version == "" || m.Component == "" {
		return errors.New("bundle: version and component are needed")
	}
	var files []File
	err := filepath.WalkDir(dir, func(p string, d os.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if d.IsDir() {
			return nil
		}
		if !d.Type().IsRegular() {
			return fmt.Errorf("bundle: %s is not a regular file (links are not allowed)", p)
		}
		rel, _ := filepath.Rel(dir, p)
		rel = filepath.ToSlash(rel)
		if !SafePath(rel) {
			return fmt.Errorf("bundle: unsafe path %q", rel)
		}
		f, err := os.Open(p)
		if err != nil {
			return err
		}
		defer f.Close()
		h := sha256.New()
		n, err := io.Copy(h, f)
		if err != nil {
			return err
		}
		info, _ := d.Info()
		files = append(files, File{Path: rel, SHA256: hex.EncodeToString(h.Sum(nil)), Size: n, Mode: uint32(info.Mode().Perm())})
		return nil
	})
	if err != nil {
		return err
	}
	if len(files) == 0 {
		return errors.New("bundle: the folder is empty")
	}
	sort.Slice(files, func(i, j int) bool { return files[i].Path < files[j].Path })
	m.Files = files
	raw, err := json.MarshalIndent(m, "", "  ")
	if err != nil {
		return err
	}
	sig, err := sign(key, raw)
	if err != nil {
		return err
	}
	tmp := out + ".part"
	zf, err := os.Create(tmp)
	if err != nil {
		return err
	}
	zw := zip.NewWriter(zf)
	put := func(name string, data []byte) error {
		w, err := zw.Create(name)
		if err != nil {
			return err
		}
		_, err = w.Write(data)
		return err
	}
	if err = put(ManifestName, raw); err == nil {
		err = put(SigName, []byte(base64.RawURLEncoding.EncodeToString(sig)))
	}
	for _, f := range files {
		if err != nil {
			break
		}
		src, e := os.Open(filepath.Join(dir, filepath.FromSlash(f.Path)))
		if e != nil {
			err = e
			break
		}
		w, e := zw.Create(PayloadPrefix + f.Path)
		if e == nil {
			_, e = io.Copy(w, src)
		}
		src.Close()
		err = e
	}
	if e := zw.Close(); err == nil {
		err = e
	}
	if e := zf.Close(); err == nil {
		err = e
	}
	if err != nil {
		os.Remove(tmp)
		return err
	}
	return os.Rename(tmp, out)
}

// ---------------------------------------------------------------- open, verify, extract

type Bundle struct {
	Manifest Manifest
	zr       *zip.ReadCloser
	entries  map[string]*zip.File
}

func (b *Bundle) Close() error { return b.zr.Close() }

func readLimited(f *zip.File, max int64) ([]byte, error) {
	r, err := f.Open()
	if err != nil {
		return nil, err
	}
	defer r.Close()
	data, err := io.ReadAll(io.LimitReader(r, max+1))
	if err != nil {
		return nil, err
	}
	if int64(len(data)) > max {
		return nil, ErrBadBundle
	}
	return data, nil
}

// Open checks the signature, the product and the list of files. Nothing is written to disk.
func Open(path string, pubs []*ecdsa.PublicKey) (*Bundle, error) {
	zr, err := zip.OpenReader(path)
	if err != nil {
		return nil, fmt.Errorf("%w: %v", ErrBadBundle, err)
	}
	b := &Bundle{zr: zr, entries: map[string]*zip.File{}}
	fail := func(e error) (*Bundle, error) { zr.Close(); return nil, e }
	var mf, sf *zip.File
	for _, f := range zr.File {
		if strings.HasSuffix(f.Name, "/") {
			continue
		}
		switch {
		case f.Name == ManifestName:
			mf = f
		case f.Name == SigName:
			sf = f
		case strings.HasPrefix(f.Name, PayloadPrefix):
			rel := strings.TrimPrefix(f.Name, PayloadPrefix)
			if !SafePath(rel) {
				return fail(fmt.Errorf("%w: unsafe entry %q", ErrBadBundle, f.Name))
			}
			if _, dup := b.entries[rel]; dup {
				return fail(fmt.Errorf("%w: duplicate entry %q", ErrBadBundle, f.Name))
			}
			b.entries[rel] = f
		default:
			return fail(fmt.Errorf("%w: unexpected entry %q", ErrBadBundle, f.Name))
		}
	}
	if mf == nil || sf == nil {
		return fail(fmt.Errorf("%w: manifest or signature is missing", ErrBadBundle))
	}
	raw, err := readLimited(mf, maxManifest)
	if err != nil {
		return fail(err)
	}
	sigText, err := readLimited(sf, 1024)
	if err != nil {
		return fail(err)
	}
	sig, err := base64.RawURLEncoding.DecodeString(strings.TrimSpace(string(sigText)))
	if err != nil || !verifyAny(pubs, raw, sig) {
		return fail(ErrBadSignature)
	}
	if err := json.Unmarshal(raw, &b.Manifest); err != nil {
		return fail(fmt.Errorf("%w: %v", ErrBadBundle, err))
	}
	m := b.Manifest
	if m.Format != Format || m.Product != Product || m.Version == "" || m.Component == "" {
		return fail(fmt.Errorf("%w: not a %s bundle of format %d", ErrBadBundle, Product, Format))
	}
	seen := map[string]bool{}
	var total int64
	for _, f := range m.Files {
		lower := strings.ToLower(f.Path)
		if !SafePath(f.Path) || seen[lower] || f.Size < 0 || len(f.SHA256) != 64 {
			return fail(fmt.Errorf("%w: bad file entry %q", ErrBadBundle, f.Path))
		}
		seen[lower] = true
		if b.entries[f.Path] == nil {
			return fail(fmt.Errorf("%w: %q is listed but missing", ErrBadBundle, f.Path))
		}
		total += f.Size
	}
	if total > maxTotal {
		return fail(fmt.Errorf("%w: too large", ErrBadBundle))
	}
	if len(b.entries) != len(m.Files) {
		return fail(fmt.Errorf("%w: the zip holds files the manifest does not list", ErrBadBundle))
	}
	return b, nil
}

// Extract writes every file under dest, checking size and SHA-256 against the signed manifest as it goes.
func (b *Bundle) Extract(dest string) error {
	for _, f := range b.Manifest.Files {
		target := filepath.Join(dest, filepath.FromSlash(f.Path))
		if err := os.MkdirAll(filepath.Dir(target), 0o755); err != nil {
			return err
		}
		r, err := b.entries[f.Path].Open()
		if err != nil {
			return err
		}
		mode := os.FileMode(f.Mode & 0o777)
		if mode == 0 {
			mode = 0o644
		}
		out, err := os.OpenFile(target, os.O_CREATE|os.O_EXCL|os.O_WRONLY, mode)
		if err != nil {
			r.Close()
			return err
		}
		h := sha256.New()
		n, err := io.Copy(io.MultiWriter(out, h), io.LimitReader(r, f.Size+1))
		r.Close()
		if cerr := out.Close(); err == nil {
			err = cerr
		}
		if err != nil {
			return err
		}
		if n != f.Size || hex.EncodeToString(h.Sum(nil)) != strings.ToLower(f.SHA256) {
			return fmt.Errorf("%w: %q does not match the signed manifest", ErrBadBundle, f.Path)
		}
		_ = os.Chmod(target, mode)
	}
	return nil
}
