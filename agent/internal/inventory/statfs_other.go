//go:build !linux

package inventory

func freeBytes(string) int64 { return 0 }
