package apply

import (
	"context"
	"fmt"
	"net/http"
	"os/exec"
	"runtime"
	"strings"
	"time"
)

// ShellRunner stops and starts the service with the commands the server's owner configured (systemctl, sc, net, a script ...)
// and calls an HTTP address to learn whether the new version is healthy.
type ShellRunner struct {
	StopCmd, StartCmd, HealthURL string
}

func (r ShellRunner) run(ctx context.Context, line string) error {
	if strings.TrimSpace(line) == "" {
		return nil
	}
	var cmd *exec.Cmd
	if runtime.GOOS == "windows" {
		cmd = exec.CommandContext(ctx, "cmd", "/C", line)
	} else {
		cmd = exec.CommandContext(ctx, "sh", "-c", line)
	}
	if out, err := cmd.CombinedOutput(); err != nil {
		return fmt.Errorf("%q failed: %v: %s", line, err, strings.TrimSpace(string(out)))
	}
	return nil
}

func (r ShellRunner) Stop(ctx context.Context) error  { return r.run(ctx, r.StopCmd) }
func (r ShellRunner) Start(ctx context.Context) error { return r.run(ctx, r.StartCmd) }

func (r ShellRunner) Healthy(ctx context.Context) error {
	if r.HealthURL == "" {
		return nil
	}
	c, cancel := context.WithTimeout(ctx, 5*time.Second)
	defer cancel()
	req, err := http.NewRequestWithContext(c, http.MethodGet, r.HealthURL, nil)
	if err != nil {
		return err
	}
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("health address answered %d", resp.StatusCode)
	}
	return nil
}
