//go:build !windows

package filebackup

import "errors"

// connectShare: outside Windows a share must already be mounted; give its mount folder as a local folder instead.
func connectShare(path, user, password string) error {
	return errors.New("SMB shares are written by Windows agents; on this system mount the share and use it as a local folder")
}
