package collector

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"runtime"
	"strings"
	"time"

	"github.com/deskask/platform/agent/internal/config"
	"github.com/deskask/platform/agent/internal/inventory"
)

// This file reaches other computers with the tools their own systems offer: PowerShell remoting for Windows (collector on Windows) and the
// SSH client for Linux. The login comes from the collector's own credential file and is never sent to the platform.

// AskpassEnv marks a program started by ssh to supply a password; main answers it and exits.
const AskpassEnv = "DESKASK_ASKPASS"

func pick(creds []config.Cred, kind, ip string) []config.Cred {
	var out []config.Cred
	for _, c := range creds {
		if c.Kind == kind && Contains(c.CIDR, ip) {
			out = append(out, c)
		}
	}
	return out
}

func lastLines(s string, n int) string {
	s = strings.TrimSpace(s)
	l := strings.Split(s, "\n")
	if len(l) > n {
		l = l[len(l)-n:]
	}
	return trim(strings.Join(l, " | "))
}

// ---- SSH

func sshArgs(c config.Cred, host string) []string {
	known := filepath.Join(config.Dir(), "known_hosts")
	a := []string{"-o", "StrictHostKeyChecking=accept-new", "-o", "UserKnownHostsFile=" + known, "-o", "ConnectTimeout=15", "-o", "ServerAliveInterval=15", "-o", "LogLevel=ERROR"}
	if c.KeyFile != "" {
		a = append(a, "-i", c.KeyFile, "-o", "BatchMode=yes", "-o", "IdentitiesOnly=yes")
	} else {
		a = append(a, "-o", "PreferredAuthentications=password,keyboard-interactive", "-o", "NumberOfPasswordPrompts=1")
	}
	return append(a, c.User+"@"+host)
}

// SSH runs a command on host and returns what it printed.
func SSH(ctx context.Context, c config.Cred, host, command string, timeout time.Duration) (string, error) {
	if c.User == "" {
		return "", errors.New("the SSH login has no user name")
	}
	if _, err := exec.LookPath("ssh"); err != nil {
		return "", errors.New("the ssh program is not installed on the collector's computer")
	}
	ctx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	args := append(sshArgs(c, host), command)
	cmd := exec.CommandContext(ctx, "ssh", args...)
	cmd.Env = os.Environ()
	if c.KeyFile == "" {
		self, err := os.Executable()
		if err != nil {
			return "", err
		}
		cmd.Env = append(cmd.Env, "SSH_ASKPASS="+self, "SSH_ASKPASS_REQUIRE=force", AskpassEnv+"=1", "DESKASK_SSH_PASSWORD="+c.Password, "DISPLAY=:0")
	}
	var out, errb bytes.Buffer
	cmd.Stdout, cmd.Stderr = &out, &errb
	cmd.Stdin = nil
	if err := cmd.Run(); err != nil {
		msg := strings.TrimSpace(errb.String())
		if msg == "" {
			msg = err.Error()
		}
		return out.String(), fmt.Errorf("ssh %s: %s", host, lastLines(msg, 3))
	}
	return out.String(), nil
}

func sshInventory(ctx context.Context, creds []config.Cred, ip string) (*inventory.Inventory, error) {
	list := pick(creds, "ssh", ip)
	if len(list) == 0 {
		return nil, errors.New("no SSH login is saved on the collector for this address (collector-cred add --kind ssh ...)")
	}
	var last error
	for _, c := range list {
		out, err := SSH(ctx, c, ip, "sh -c "+shQuote(inventory.LinuxRemoteScript), 3*time.Minute)
		if err != nil {
			last = err
			continue
		}
		inv := inventory.ParseLinuxRemote(out)
		if inv.Hostname == "" {
			last = errors.New("the device answered but not like a Linux computer")
			continue
		}
		return inv, nil
	}
	return nil, last
}

func shQuote(s string) string { return "'" + strings.ReplaceAll(s, "'", `'"'"'`) + "'" }

func sshInstall(ctx context.Context, creds []config.Cred, ip, installURL string) (string, error) {
	list := pick(creds, "ssh", ip)
	if len(list) == 0 {
		return "", errors.New("no SSH login is saved on the collector for this address")
	}
	cmd := `if [ "$(id -u)" = 0 ]; then S=""; else S="sudo -n"; fi; curl -fsSL ` + shQuote(installURL) + ` | $S sh`
	var last error
	for _, c := range list {
		out, err := SSH(ctx, c, ip, cmd, 5*time.Minute)
		if err == nil {
			return "installed over SSH as " + c.Name + ": " + lastLines(out, 2), nil
		}
		last = err
	}
	return "", last
}

// ---- Windows (PowerShell remoting)

const remoteWrapper = `
$ErrorActionPreference = 'Stop'
$opt = New-PSSessionOption -OpenTimeout 30000 -OperationTimeout 180000
$args2 = @{ ComputerName = $env:DK_HOST; ScriptBlock = ([scriptblock]::Create($env:DK_SCRIPT)); SessionOption = $opt; Authentication = 'Negotiate' }
if ($env:DK_USER) { $sec = ConvertTo-SecureString $env:DK_PW -AsPlainText -Force; $args2.Credential = New-Object System.Management.Automation.PSCredential($env:DK_USER, $sec) }
Invoke-Command @args2
`

var errNeedsWindows = errors.New("reading or installing on Windows computers needs the collector to run on Windows (PowerShell remoting); install the agent on this computer by hand or by Group Policy")

func winRemote(ctx context.Context, c config.Cred, host, script string, timeout time.Duration) (string, error) {
	if runtime.GOOS != "windows" {
		return "", errNeedsWindows
	}
	ctx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	cmd := exec.CommandContext(ctx, "powershell.exe", inventory.PowerShellArgs(remoteWrapper)...)
	cmd.Env = append(os.Environ(), "DK_HOST="+host, "DK_SCRIPT="+script, "DK_USER="+c.User, "DK_PW="+c.Password)
	var out, errb bytes.Buffer
	cmd.Stdout, cmd.Stderr = &out, &errb
	if err := cmd.Run(); err != nil {
		msg := strings.TrimSpace(errb.String())
		if msg == "" {
			msg = err.Error()
		}
		who := c.User
		if who == "" {
			who = "the collector's own account (no Windows login is saved for this computer)"
		}
		return out.String(), fmt.Errorf("PowerShell remoting to %s as %s: %s", host, who, WinHint(CleanCLIXML(msg)))
	}
	return out.String(), nil
}

// winCreds returns the saved Windows logins for an address, or one empty login (the collector's own account) when none is saved.
func winCreds(creds []config.Cred, ip string) []config.Cred {
	if l := pick(creds, "windows", ip); len(l) > 0 {
		return l
	}
	return []config.Cred{{Name: "the collector's own account"}}
}

// hostsOf lists the names to try for a computer: its host name first (Kerberos and certificates need it), then its address.
func hostsOf(host, ip string) []string {
	var out []string
	for _, h := range []string{host, ip} {
		if h != "" && (len(out) == 0 || out[0] != h) {
			out = append(out, h)
		}
	}
	return out
}

func winInventory(ctx context.Context, creds []config.Cred, hosts ...string) (*inventory.Inventory, error) {
	var last error
	for _, host := range hosts {
		for _, c := range winCreds(creds, host) {
			out, err := winRemote(ctx, c, host, inventory.WindowsScript, 4*time.Minute)
			if err != nil {
				last = err
				continue
			}
			return inventory.ParseWindowsJSON([]byte(out))
		}
	}
	return nil, last
}

func winInstall(ctx context.Context, creds []config.Cred, hosts []string, installURL string) (string, error) {
	script := `[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; iwr -UseBasicParsing '` + strings.ReplaceAll(installURL, "'", "''") + `' | iex`
	var last error
	for _, host := range hosts {
		for _, c := range winCreds(creds, host) {
			out, err := winRemote(ctx, c, host, script, 6*time.Minute)
			if err == nil {
				return "installed by PowerShell remoting as " + c.Name + ": " + lastLines(out, 2), nil
			}
			last = err
		}
	}
	return "", last
}

var (
	clixmlErr = regexp.MustCompile(`(?s)<S S="Error">(.*?)</S>`)
	xmlEsc    = strings.NewReplacer("&lt;", "<", "&gt;", ">", "&quot;", `"`, "&apos;", "'", "&amp;", "&")
	xEsc      = regexp.MustCompile(`_x[0-9A-Fa-f]{4}_`)
)

// CleanCLIXML turns the serialized error text that powershell.exe writes ("#< CLIXML <Objs ...>") into the plain message.
func CleanCLIXML(s string) string {
	if !strings.Contains(s, "CLIXML") {
		return s
	}
	var parts []string
	for _, m := range clixmlErr.FindAllStringSubmatch(s, -1) {
		parts = append(parts, m[1])
	}
	if len(parts) == 0 {
		return s
	}
	t := xEsc.ReplaceAllString(xmlEsc.Replace(strings.Join(parts, " ")), " ")
	return strings.Join(strings.Fields(t), " ")
}

// WinHint adds what to do about the usual reasons PowerShell remoting fails.
func WinHint(msg string) string {
	m := strings.ToLower(msg)
	msg = trim(msg)
	switch {
	case strings.Contains(m, "access is denied") || strings.Contains(m, "logon failure") || strings.Contains(m, "unknown user name"):
		return msg + " - the login was refused: save a Windows login with administrator rights under Assets > Credentials (use DOMAIN\\user for domain accounts)"
	case strings.Contains(m, "trustedhosts") || strings.Contains(m, "kerberos"):
		return msg + " - the computer is not in the domain: on the collector's computer run  Set-Item WSMan:\\localhost\\Client\\TrustedHosts -Value '<computer>' -Force  (or install by hand or Group Policy)"
	case strings.Contains(m, "winrm") || strings.Contains(m, "connecting to remote server") || strings.Contains(m, "cannot connect"):
		return msg + " - check that the computer is switched on and reachable from the collector, that WinRM is enabled on it (run  Enable-PSRemoting -Force  there) and that port 5985 is open in its firewall"
	}
	return msg
}
