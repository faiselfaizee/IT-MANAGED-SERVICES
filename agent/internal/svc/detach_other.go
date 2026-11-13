//go:build !windows

package svc

import "os/exec"

func detach(cmd *exec.Cmd, breakaway bool) {}
