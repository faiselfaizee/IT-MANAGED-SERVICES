//go:build !windows

package account

import (
	"context"
	"fmt"
	"os/exec"
	"strings"
	"time"
)

// chpasswd is the program used; replaced in tests.
var chpasswd = "chpasswd"

func setPassword(acct, target, password string) error {
	if target != "linux" && target != "local" {
		return fmt.Errorf("this computer cannot change a %q account", target)
	}
	if strings.ContainsAny(password, "\n\r") {
		return fmt.Errorf("the new password has a line break")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	// the password goes on standard input, never on the command line where other users could see it
	cmd := exec.CommandContext(ctx, chpasswd)
	cmd.Stdin = strings.NewReader(acct + ":" + password + "\n")
	out, err := cmd.CombinedOutput()
	if err != nil {
		msg := strings.TrimSpace(string(out))
		if msg == "" {
			msg = err.Error()
		}
		return fmt.Errorf("chpasswd failed: %s", msg)
	}
	return nil
}

func setServiceLogon(service, acct, password string) error {
	return fmt.Errorf("changing the account a service runs as is only for Windows; on Linux set User= in the unit file")
}
