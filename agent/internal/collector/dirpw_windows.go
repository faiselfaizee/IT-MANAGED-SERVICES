//go:build windows

package collector

import (
	"bytes"
	"context"
	"encoding/json"
	"net"
	"os/exec"
	"strings"
	"time"

	"github.com/deskask/platform/agent/internal/inventory"
)

// Sets a password through ADSI with Kerberos/NTLM signing and sealing, which Active Directory accepts without LDAPS.
// Secrets travel on standard input, never on the command line.
const adsiPassword = `$ErrorActionPreference = 'Stop'
$j = [Console]::In.ReadToEnd() | ConvertFrom-Json
Add-Type -AssemblyName System.DirectoryServices
$a = [System.DirectoryServices.AuthenticationTypes]
$flags = $a::Secure -bor $a::Sealing -bor $a::Signing
try {
  $e = New-Object System.DirectoryServices.DirectoryEntry("LDAP://$($j.server)/$($j.dn)", $j.user, $j.password, $flags)
  $null = $e.Invoke('SetPassword', @([string]$j.newPassword))
  if ($j.mustChange) { $e.Properties['pwdLastSet'].Value = 0; $e.CommitChanges() }
  'ok'
} catch {
  $m = $_.Exception; while ($m.InnerException) { $m = $m.InnerException }
  'error:' + $m.Message
}
`

func init() { windowsPasswords = adsiSetPassword }

func adsiSetPassword(c dirConn, o dirOp) error {
	user := c.BindDN
	var last string
	for _, s := range c.Servers {
		host := strings.TrimSpace(s)
		if h, _, err := net.SplitHostPort(host); err == nil {
			host = h
		}
		in, _ := json.Marshal(map[string]any{"server": host, "dn": o.DN, "user": user, "password": c.Password, "newPassword": o.Password, "mustChange": o.MustMove})
		ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
		cmd := exec.CommandContext(ctx, "powershell.exe", inventory.PowerShellArgs(adsiPassword)...)
		cmd.Stdin = bytes.NewReader(in)
		out, err := cmd.Output()
		cancel()
		r := strings.TrimSpace(string(out))
		if err == nil && strings.HasSuffix(r, "ok") {
			return nil
		}
		if i := strings.Index(r, "error:"); i >= 0 {
			last = strings.TrimSpace(r[i+6:])
		} else if err != nil {
			last = err.Error()
		}
		low := strings.ToLower(last)
		if strings.Contains(low, "password") && (strings.Contains(low, "policy") || strings.Contains(low, "requirement") || strings.Contains(low, "complex")) {
			return &dirError{Code: 19, Message: "The password does not meet the domain's rules (length, complexity or history)."}
		}
		if strings.Contains(low, "user name or password is incorrect") || strings.Contains(low, "logon failure") {
			return &dirError{Code: 49, Message: "Windows refused the service account (" + last + "). For this channel give the account as user@domain or DOMAIN\\user."}
		}
		if strings.Contains(low, "access is denied") || strings.Contains(low, "access denied") {
			return &dirError{Code: 50, Message: "The service account may not reset this password: " + last}
		}
	}
	return &dirError{Code: 53, Message: "Windows could not set the password: " + last}
}
