//go:build linux

package inventory

import "syscall"

func freeBytes(mount string) int64 {
	var s syscall.Statfs_t
	if syscall.Statfs(mount, &s) != nil {
		return 0
	}
	return int64(s.Bavail) * int64(s.Bsize)
}
