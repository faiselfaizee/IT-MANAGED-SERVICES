//go:build !windows

package backup

import "context"

// Collect finds nothing outside Windows yet (Veeam Agent for Linux and others report by mail, syslog or the report address).
func Collect(ctx context.Context) (Found, error) { return Found{}, nil }
