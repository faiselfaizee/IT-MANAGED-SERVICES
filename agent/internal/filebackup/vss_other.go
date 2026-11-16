//go:build !windows

package filebackup

import "context"

// Shadow: no shadow copies outside Windows; files are read as they are.
func Shadow(ctx context.Context, paths []string) (func(string) string, func(), []string) {
	return nil, func() {}, nil
}
