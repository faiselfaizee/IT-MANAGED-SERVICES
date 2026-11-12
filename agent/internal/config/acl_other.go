//go:build !windows

package config

import "os"

func restrict(path string) error { return os.Chmod(path, 0o600) }
