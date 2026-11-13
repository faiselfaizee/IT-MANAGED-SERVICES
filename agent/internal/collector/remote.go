package collector

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"io"
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
		step(ctx, "error", "No SSH login is saved for %s. Add one under Assets > Credentials for this customer.", ip)
		return "", errors.New("no SSH login is saved on the collector for this address")
	}
	cmd := `if [ "$(id -u)" = 0 ]; then S=""; else S="sudo -n"; fi; curl -fsSL ` + shQuote(installURL) + ` | $S sh`
	var last error
	for _, c := range list {
		step(ctx, "info", "Connecting to %s over SSH as %s (login %s) and running the install command", ip, c.User, c.Name)
		stop := heartbeat(ctx, 15*time.Second, "waiting for "+ip+" to run the install command")
		out, err := SSH(ctx, c, ip, cmd, 5*time.Minute)
		stop()
		if err == nil {
			step(ctx, "ok", "The install command finished on %s", ip)
			return "installed over SSH as " + c.Name + ": " + lastLines(out, 2), nil
		}
		step(ctx, "warn", "SSH to %s as %s failed: %v", ip, c.User, err)
		last = err
	}
	return "", last
}

// ---- Windows (PowerShell remoting)

const remoteWrapper = `
$ErrorActionPreference = 'Stop'
$opt = New-PSSessionOption -OpenTimeout 30000 -OperationTimeout 180000
$args2 = @{ ComputerName = $env:DK_HOST; ScriptBlock = ([scriptblock]::Create($env:DK_SCRIPT)); SessionOption = $opt; Authentication = 'Negotiate' }
if ($env:DK_SSL) { $args2.UseSSL = $true; $args2.SessionOption = New-PSSessionOption -OpenTimeout 30000 -OperationTimeout 180000 -SkipCACheck -SkipCNCheck }
if ($env:DK_USER) { $sec = ConvertTo-SecureString $env:DK_PW -AsPlainText -Force; $args2.Credential = New-Object System.Management.Automation.PSCredential($env:DK_USER, $sec) }
Invoke-Command @args2
`

var errNeedsWindows = errors.New("reading or installing on Windows computers needs the collector to run on Windows (PowerShell remoting); install the agent on this computer by hand or by Group Policy")

// winRMPorts are the HTTP and HTTPS ports of PowerShell remoting (variables so a test can point them at a local listener).
var winRMPorts = [2]int{5985, 5986}

// winReach checks from the collector that the computer answers on a WinRM port, and says which one (ssl is true when only HTTPS answers).
// A running WinRM service is not enough: remoting also needs its listener and a firewall rule that lets this computer in.
func winReach(ctx context.Context, host string) (ssl bool, err error) {
	step(ctx, "info", "Checking the WinRM ports %d and %d on %s", winRMPorts[0], winRMPorts[1], host)
	if ok, _ := TCPOpen(ctx, host, winRMPorts[0], 4*time.Second); ok {
		step(ctx, "info", "Port %d (WinRM, HTTP) answers on %s", winRMPorts[0], host)
		return false, nil
	}
	step(ctx, "warn", "Port %d does not answer on %s", winRMPorts[0], host)
	if ok, _ := TCPOpen(ctx, host, winRMPorts[1], 4*time.Second); ok {
		step(ctx, "info", "Port %d (WinRM, HTTPS) answers on %s", winRMPorts[1], host)
		return true, nil
	}
	step(ctx, "warn", "Port %d does not answer on %s either", winRMPorts[1], host)
	return false, fmt.Errorf("%s does not answer on port %d or %d from the collector. The WinRM service can be running while remoting is not set up or is blocked. On that computer run (as administrator)  Enable-PSRemoting -Force  and, if the collector is on another network, allow it in the firewall:  Set-NetFirewallRule -Name WINRM-HTTP-In-TCP -RemoteAddress Any  (on a Public network profile also  Set-NetFirewallRule -Name WINRM-HTTP-In-TCP-PUBLIC -RemoteAddress Any). Also check that the address %s is the computer's address on the collector's network", host, winRMPorts[0], winRMPorts[1], host)
}

func winRemote(ctx context.Context, c config.Cred, host, script string, timeout time.Duration) (string, error) {
	if runtime.GOOS != "windows" {
		return "", errNeedsWindows
	}
	ssl, rerr := winReach(ctx, host)
	if rerr != nil {
		return "", rerr
	}
	ctx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	return winRemoteOnce(ctx, c, host, script, ssl, false)
}

func winRemoteOnce(ctx context.Context, c config.Cred, host, script string, ssl, retried bool) (string, error) {
	who := c.User
	if who == "" {
		who = "the collector's own account (no Windows login is saved for this computer)"
	}
	step(ctx, "info", "Starting PowerShell remoting to %s as %s%s", host, who, map[bool]string{true: " over HTTPS", false: ""}[ssl])
	stop := heartbeat(ctx, 15*time.Second, "waiting for "+host+" to run the install command")
	defer stop()
	cmd := exec.CommandContext(ctx, "powershell.exe", inventory.PowerShellArgs(remoteWrapper)...)
	cmd.Env = append(os.Environ(), "DK_HOST="+host, "DK_SCRIPT="+script, "DK_USER="+c.User, "DK_PW="+c.Password)
	if ssl {
		cmd.Env = append(cmd.Env, "DK_SSL=1")
	}
	var out, errb bytes.Buffer
	cmd.Stdout, cmd.Stderr = io.MultiWriter(&out, &lineWriter{ctx: ctx, prefix: host + ": "}), &errb
	if err := cmd.Run(); err != nil {
		msg := strings.TrimSpace(errb.String())
		if msg == "" {
			msg = err.Error()
		}
		// Reaching a computer by IP address, or one outside the domain, needs it in TrustedHosts.
		// The collector runs as SYSTEM, so it adds the target once and tries again.
		if c.User != "" && !retried && needsTrust(CleanCLIXML(msg)) {
			step(ctx, "warn", "Windows will not talk to %s by address unless it is trusted: adding it to TrustedHosts on this computer and trying again", host)
			if terr := trustHost(ctx, host); terr == nil {
				stop()
				return winRemoteOnce(ctx, c, host, script, ssl, true)
			} else {
				step(ctx, "warn", "Could not change TrustedHosts (%v). Run it by hand as administrator on this computer.", terr)
			}
		}
		hint := WinHint(CleanCLIXML(msg))
		step(ctx, "warn", "PowerShell remoting to %s as %s failed: %s", host, c.User, hint)
		return out.String(), fmt.Errorf("PowerShell remoting to %s as %s: %s", host, who, hint)
	}
	step(ctx, "ok", "PowerShell remoting finished on %s", host)
	return out.String(), nil
}

func needsTrust(msg string) bool {
	m := strings.ToLower(msg)
	return strings.Contains(m, "trustedhosts") || strings.Contains(m, "cannot process the request")
}

const trustScript = `$h=$env:DK_HOST; $cur=[string](Get-Item WSMan:\localhost\Client\TrustedHosts).Value
if($cur -eq '*' -or (($cur -split ',') | ForEach-Object { $_.Trim() }) -contains $h){ exit 0 }
Set-Item WSMan:\localhost\Client\TrustedHosts -Value $h -Concatenate -Force`

// trustHost adds one computer to this computer's WinRM TrustedHosts list (needs administrator rights).
func trustHost(ctx context.Context, host string) error {
	cmd := exec.CommandContext(ctx, "powershell.exe", inventory.PowerShellArgs(trustScript)...)
	cmd.Env = append(os.Environ(), "DK_HOST="+host)
	return cmd.Run()
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
		step(ctx, "info", "Trying %s", host)
		for _, c := range winCreds(creds, host) {
			if c.User == "" {
				step(ctx, "warn", "No saved Windows login matches %s: using the controller's own account. Save one under Assets > Credentials for this customer.", host)
			} else {
				step(ctx, "info", "Using the saved login %s (%s)", c.Name, c.User)
			}
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
	clixmlErr  = regexp.MustCompile(`(?s)<S S="Error">(.*?)</S>`)
	xmlEsc     = strings.NewReplacer("&lt;", "<", "&gt;", ">", "&quot;", `"`, "&apos;", "'", "&amp;", "&")
	xEsc       = regexp.MustCompile(`_x[0-9A-Fa-f]{4}_`)
	connecting = regexp.MustCompile(`^\[[^\]]*\]\s*Connecting to remote server \S+ failed with the following error message\s*:\s*`)
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
	t = strings.Join(strings.Fields(t), " ")
	t = connecting.ReplaceAllString(t, "") // "[host] Connecting to remote server host failed with the following error message :"
	t = strings.ReplaceAll(t, "For more information, see the about_Remote_Troubleshooting Help topic.", "")
	return strings.TrimSpace(t)
}

// WinHint adds what to do about the usual reasons PowerShell remoting fails.
func WinHint(msg string) string {
	m := strings.ToLower(msg)
	msg = trim(msg)
	switch {
	case strings.Contains(m, "access is denied") || strings.Contains(m, "logon failure") || strings.Contains(m, "unknown user name"):
		return msg + " - the login was refused: save a Windows login with administrator rights under Assets > Credentials (use DOMAIN\\user for domain accounts)"
	case strings.Contains(m, "trustedhosts") || strings.Contains(m, "kerberos") || strings.Contains(m, "cannot process the request"):
		return msg + " - the target is reached by IP address or is outside the domain and could not be added to TrustedHosts automatically: on the collector's computer run as administrator  Set-Item WSMan:\\localhost\\Client\\TrustedHosts -Value '<address or name>' -Concatenate -Force"
	case strings.Contains(m, "winrm") || strings.Contains(m, "connecting to remote server") || strings.Contains(m, "cannot connect"):
		return msg + " - the collector reached the WinRM port but PowerShell remoting did not start: run  Enable-PSRemoting -Force  on that computer, check that it accepts the login's account (administrators group) and that the date and time are right on both computers"
	}
	return msg
}
