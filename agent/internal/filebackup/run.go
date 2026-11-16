package filebackup

import (
	"context"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"os"
	"path/filepath"
	"runtime"
	"sort"
	"strings"
	"sync"
	"time"
)

// Options for one backup run.
type Options struct {
	Plan     string
	Host     string
	Paths    []string // folders or files
	Excludes []string // patterns matched against the name and the full path (*.tmp, *\Temp\*, node_modules)
	// Read maps a path to where it is read from (a shadow copy); nil reads the live file.
	Read func(path string) string
}

// Summary of a run.
type Summary struct {
	Snapshot  string
	Files     int
	Bytes     int64
	NewBytes  int64
	Unchanged int
	Warnings  []string
}

// DefaultExcludes are never worth backing up.
var DefaultExcludes = []string{"pagefile.sys", "hiberfil.sys", "swapfile.sys", "*.tmp", "~$*", "Thumbs.db", "desktop.ini", `*\$Recycle.Bin\*`, `*\System Volume Information\*`}

func matchAny(patterns []string, p string) bool {
	full := strings.ReplaceAll(p, "/", `\`)
	name := full[strings.LastIndexByte(full, '\\')+1:]
	for _, pat := range patterns {
		pat = strings.TrimSpace(pat)
		if pat == "" {
			continue
		}
		wp := strings.ReplaceAll(pat, "/", `\`)
		if ok, _ := filepath.Match(strings.ToLower(pat), strings.ToLower(name)); ok {
			return true
		}
		if strings.ContainsAny(wp, `\*`) && globAll(strings.ToLower(wp), strings.ToLower(full)) {
			return true
		}
		if !strings.ContainsAny(pat, `*?\/`) && strings.EqualFold(name, pat) {
			return true
		}
	}
	return false
}

// globAll matches * across separators too (filepath.Match stops at them).
func globAll(pat, s string) bool {
	parts := strings.Split(pat, "*")
	if len(parts) == 1 {
		return pat == s
	}
	if !strings.HasPrefix(s, parts[0]) {
		return false
	}
	s = s[len(parts[0]):]
	for i, p := range parts[1:] {
		if i == len(parts)-2 {
			return strings.HasSuffix(s, p)
		}
		j := strings.Index(s, p)
		if j < 0 {
			return false
		}
		s = s[j+len(p):]
	}
	return true
}

// Backup stores the paths as a new snapshot. A file whose size and change time match the previous snapshot is not read again.
// Files that cannot be read (in use, no permission) are skipped and listed as warnings; the run still succeeds with them.
func Backup(ctx context.Context, r *Repo, o Options) (Summary, error) {
	var sum Summary
	if len(o.Paths) == 0 {
		return sum, errors.New("the plan has no folders to back up")
	}
	if err := r.loadIndex(ctx); err != nil {
		return sum, fmt.Errorf("reading the backup storage (%s): %w", r.st.Where(), err)
	}
	prev := map[string]File{}
	if ids, err := r.Snapshots(ctx); err == nil {
		for i := len(ids) - 1; i >= 0; i-- {
			if s, err := r.LoadSnapshot(ctx, ids[i]); err == nil && s.Plan == o.Plan {
				for _, f := range s.Files {
					prev[f.Path] = f
				}
				break
			}
		}
	}
	excl := append(append([]string{}, DefaultExcludes...), o.Excludes...)
	start := time.Now()
	snap := &Snapshot{ID: newSnapshotID(start), Time: start.UTC().Format(time.RFC3339), Host: o.Host, Plan: o.Plan, Paths: o.Paths}
	var mu sync.Mutex
	warn := func(s string) {
		mu.Lock()
		defer mu.Unlock()
		if len(snap.Warnings) < 200 {
			snap.Warnings = append(snap.Warnings, s)
		}
	}
	for _, root := range o.Paths {
		root = strings.TrimSpace(root)
		if root == "" {
			continue
		}
		if _, err := os.Stat(root); err != nil {
			warn(fmt.Sprintf("%s: not found", root))
			continue
		}
		err := filepath.WalkDir(root, func(p string, d fs.DirEntry, err error) error {
			if ctx.Err() != nil {
				return ctx.Err()
			}
			if err != nil {
				warn(fmt.Sprintf("%s: %v", p, err))
				if d != nil && d.IsDir() {
					return fs.SkipDir
				}
				return nil
			}
			if p != root && matchAny(excl, p) {
				if d.IsDir() {
					return fs.SkipDir
				}
				return nil
			}
			if d.IsDir() || !d.Type().IsRegular() {
				return nil
			}
			info, err := d.Info()
			if err != nil {
				warn(fmt.Sprintf("%s: %v", p, err))
				return nil
			}
			f := File{Path: p, Size: info.Size(), Mode: uint32(info.Mode().Perm()), MTime: info.ModTime().UnixNano()}
			if old, ok := prev[p]; ok && old.Size == f.Size && old.MTime == f.MTime && piecesKnown(r, old.Pieces) {
				f.Pieces = old.Pieces
				sum.Unchanged++
			} else {
				src := p
				if o.Read != nil {
					src = o.Read(p)
				}
				pieces, stored, err := storeFile(ctx, r, src)
				if err != nil {
					if ctx.Err() != nil {
						return ctx.Err()
					}
					warn(fmt.Sprintf("%s: %v", p, err))
					return nil
				}
				f.Pieces = pieces
				snap.NewBytes += stored
			}
			snap.Files = append(snap.Files, f)
			snap.Bytes += f.Size
			return nil
		})
		if err != nil {
			return sum, err
		}
	}
	if len(snap.Files) == 0 && len(snap.Warnings) > 0 {
		return sum, fmt.Errorf("nothing could be read: %s", snap.Warnings[0])
	}
	if err := r.saveSnapshot(ctx, snap); err != nil {
		return sum, fmt.Errorf("saving the snapshot: %w", err)
	}
	sum.Snapshot, sum.Files, sum.Bytes, sum.NewBytes, sum.Warnings = snap.ID, len(snap.Files), snap.Bytes, snap.NewBytes, snap.Warnings
	return sum, nil
}

func piecesKnown(r *Repo, ids []string) bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	for _, id := range ids {
		if !r.have[id] {
			return false
		}
	}
	return true
}

// storeFile cuts a file into pieces and stores the new ones, four at a time.
func storeFile(ctx context.Context, r *Repo, src string) ([]string, int64, error) {
	fh, err := os.Open(src)
	if err != nil {
		return nil, 0, err
	}
	defer fh.Close()
	ck := newChunker(fh)
	var ids []string
	var stored int64
	var mu sync.Mutex
	var firstErr error
	sem := make(chan struct{}, 4)
	var wg sync.WaitGroup
	for {
		piece, err := ck.Next()
		if err == io.EOF {
			break
		}
		if err != nil {
			wg.Wait()
			return nil, 0, err
		}
		id := r.pieceID(piece)
		ids = append(ids, id)
		data := append([]byte(nil), piece...)
		sem <- struct{}{}
		wg.Add(1)
		go func() {
			defer func() { <-sem; wg.Done() }()
			isNew, n, err := r.putPiece(ctx, id, data)
			mu.Lock()
			defer mu.Unlock()
			if err != nil && firstErr == nil {
				firstErr = err
			}
			if isNew {
				stored += int64(n)
			}
		}()
		mu.Lock()
		failed := firstErr != nil
		mu.Unlock()
		if failed {
			break
		}
	}
	wg.Wait()
	if firstErr != nil {
		return nil, 0, fmt.Errorf("storing: %w", firstErr)
	}
	return ids, stored, nil
}

// RestoreOptions says what to restore and where.
type RestoreOptions struct {
	// Include limits the restore to these files or folders (as they were backed up); empty restores everything.
	Include []string
	// Target is a folder to restore into, keeping the original folder structure under it (C:\Restore\C\Users\...);
	// empty puts the files back where they were, replacing what is there.
	Target string
}

// RestoreSummary of a restore.
type RestoreSummary struct {
	Files  int
	Bytes  int64
	Errors []string
}

func under(p, root string) bool {
	p, root = strings.ToLower(filepath.Clean(p)), strings.ToLower(filepath.Clean(root))
	return p == root || strings.HasPrefix(p, strings.TrimRight(root, `\/`)+string(filepath.Separator))
}

// targetPath maps a backed-up path into the target folder: the drive letter becomes a folder (C: -> C).
func targetPath(target, p string) string {
	rel := p
	if v := filepath.VolumeName(p); v != "" {
		rel = strings.TrimSuffix(strings.TrimPrefix(strings.TrimPrefix(v, `\\`), `\\?\`), ":") + p[len(v):]
	}
	rel = strings.TrimLeft(rel, `\/`)
	return filepath.Join(target, rel)
}

// Restore writes a snapshot's files back.
func Restore(ctx context.Context, r *Repo, snapshotID string, o RestoreOptions) (RestoreSummary, error) {
	var sum RestoreSummary
	s, err := r.LoadSnapshot(ctx, snapshotID)
	if err != nil {
		return sum, err
	}
	for _, f := range s.Files {
		if ctx.Err() != nil {
			return sum, ctx.Err()
		}
		if len(o.Include) > 0 {
			keep := false
			for _, inc := range o.Include {
				if strings.TrimSpace(inc) != "" && under(f.Path, inc) {
					keep = true
					break
				}
			}
			if !keep {
				continue
			}
		}
		dst := f.Path
		if o.Target != "" {
			dst = targetPath(o.Target, f.Path)
		}
		if err := restoreFile(ctx, r, f, dst); err != nil {
			if len(sum.Errors) < 100 {
				sum.Errors = append(sum.Errors, fmt.Sprintf("%s: %v", f.Path, err))
			}
			continue
		}
		sum.Files++
		sum.Bytes += f.Size
	}
	if sum.Files == 0 && len(sum.Errors) == 0 {
		return sum, errors.New("no file in the snapshot matches what was asked for")
	}
	return sum, nil
}

func restoreFile(ctx context.Context, r *Repo, f File, dst string) error {
	if err := os.MkdirAll(filepath.Dir(dst), 0o755); err != nil {
		return err
	}
	tmp := dst + ".deskask-restore"
	out, err := os.OpenFile(tmp, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0o600)
	if err != nil {
		return err
	}
	for _, id := range f.Pieces {
		data, err := r.getPiece(ctx, id)
		if err == nil {
			_, err = out.Write(data)
		}
		if err != nil {
			out.Close()
			os.Remove(tmp)
			return err
		}
	}
	if err := out.Close(); err != nil {
		os.Remove(tmp)
		return err
	}
	if runtime.GOOS != "windows" && f.Mode != 0 {
		os.Chmod(tmp, fs.FileMode(f.Mode))
	}
	if err := os.Rename(tmp, dst); err != nil {
		os.Remove(tmp)
		return err
	}
	t := time.Unix(0, f.MTime)
	os.Chtimes(dst, t, t)
	return nil
}

// Forget removes snapshots of a plan outside the retention: the newest keepLast are kept, and every snapshot of the last keepDays days.
// Pieces no remaining snapshot uses are then deleted. Returns the snapshot ids removed.
func Forget(ctx context.Context, r *Repo, plan string, keepLast, keepDays int, now time.Time) ([]string, error) {
	ids, err := r.Snapshots(ctx)
	if err != nil {
		return nil, err
	}
	var mine []string
	used := map[string]bool{}
	snaps := map[string]*Snapshot{}
	for _, id := range ids {
		s, err := r.LoadSnapshot(ctx, id)
		if err != nil {
			return nil, err // never delete pieces while a snapshot cannot be read
		}
		snaps[id] = s
		if s.Plan == plan {
			mine = append(mine, id)
		}
	}
	sort.Strings(mine)
	if keepLast < 1 {
		keepLast = 1
	}
	drop := map[string]bool{}
	for i, id := range mine {
		recent := len(mine)-i <= keepLast
		t, ok := SnapshotTime(id)
		young := keepDays > 0 && ok && now.Sub(t) <= time.Duration(keepDays)*24*time.Hour
		if !recent && !young {
			drop[id] = true
		}
	}
	if len(drop) == 0 {
		return nil, nil
	}
	for id, s := range snaps {
		if drop[id] {
			continue
		}
		for _, f := range s.Files {
			for _, p := range f.Pieces {
				used[p] = true
			}
		}
	}
	var removed []string
	for id := range drop {
		if err := r.st.Delete(ctx, "snapshots/"+id); err != nil {
			return removed, err
		}
		removed = append(removed, id)
	}
	sort.Strings(removed)
	keys, err := r.st.List(ctx, "data/")
	if err != nil {
		return removed, err
	}
	for _, k := range keys {
		id := k[strings.LastIndexByte(k, '/')+1:]
		if !used[id] {
			if err := r.st.Delete(ctx, k); err != nil {
				return removed, err
			}
			r.mu.Lock()
			if r.have != nil {
				delete(r.have, id)
			}
			r.mu.Unlock()
		}
	}
	return removed, nil
}
