//go:build windows

package svc

import (
	"os/exec"
	"syscall"
)

const (
	createNewProcessGroup  = 0x00000200
	detachedProcess        = 0x00000008
	createBreakawayFromJob = 0x01000000
)

// detach starts the helper outside this process's console and, where Windows allows it, outside the job Task Scheduler puts
// the agent in, so it is not ended together with the agent.
func detach(cmd *exec.Cmd, breakaway bool) {
	flags := uint32(createNewProcessGroup | detachedProcess)
	if breakaway {
		flags |= createBreakawayFromJob
	}
	cmd.SysProcAttr = &syscall.SysProcAttr{CreationFlags: flags, HideWindow: true}
}
