// deskask-agent is one program with several jobs:
//
//	install / run / uninstall                the general agent on a desktop, laptop or server
//	collector-install / collector-run / ...  the controller (site collector) that watches and reaches the rest of the network
//	collector-cred add|list|remove           logins the controller uses on devices (kept on its own computer only)
//	inventory                                print this computer's inventory as JSON
//	version
package main

import (
	"context"
	"flag"
	"fmt"
	"log"
	"os"
	"os/signal"
	"strings"
	"syscall"
	"time"

	"github.com/deskask/platform/agent/internal/agentrun"
	"github.com/deskask/platform/agent/internal/collector"
	"github.com/deskask/platform/agent/internal/config"
	"github.com/deskask/platform/agent/internal/link"
	"github.com/deskask/platform/agent/internal/svc"
)

// version is set when the program is built (-ldflags "-X main.version=1.2.3").
var version = "2.0.0-dev"

func main() {
	if os.Getenv(collector.AskpassEnv) == "1" { // ssh started us to ask for a password
		fmt.Println(os.Getenv("DESKASK_SSH_PASSWORD"))
		return
	}
	if len(os.Args) < 2 {
		usage()
		os.Exit(2)
	}
	cmd, args := os.Args[1], os.Args[2:]
	if strings.HasPrefix(cmd, "-") && cmd != "-v" && cmd != "--version" { // the first agent was started as: deskask-agent -platform URL -token T
		cmd, args = "connect", os.Args[1:]
	}
	var err error
	switch cmd {
	case "version", "-v", "--version":
		fmt.Println(version)
	case "install":
		err = cmdInstall(args, false)
	case "collector-install":
		err = cmdInstall(args, true)
	case "run":
		err = cmdRun(false)
	case "collector-run":
		err = cmdRun(true)
	case "uninstall":
		config.Remove(agentrun.ConfigFile)
		err = svc.Agent.Uninstall()
	case "collector-uninstall":
		err = svc.Collector.Uninstall() // saved logins and settings stay, so a re-install keeps them; delete collector.json to remove them
	case "collector-cred":
		err = cmdCred(args)
	case "inventory":
		var b []byte
		if b, err = collector.Inventory(context.Background()); err == nil {
			fmt.Println(string(b))
		}
	case "connect":
		err = cmdConnect(args)
	default:
		usage()
		os.Exit(2)
	}
	if err != nil {
		fmt.Fprintln(os.Stderr, "error:", err)
		os.Exit(1)
	}
}

func usage() {
	fmt.Fprintf(os.Stderr, `DeskAsk agent %s
  deskask-agent install --server https://your.deskask.in --token dkat_...
  deskask-agent collector-install --server https://your.deskask.in --token dkco_... [--name "Head office"]
  deskask-agent collector-cred add --kind snmp --name printers --community public [--cidr 10.0.0.0/16]
  deskask-agent collector-cred add --kind ssh --name linux --user root --key /root/.ssh/id_ed25519
  deskask-agent collector-cred add --kind windows --name domain-admin --user DOMAIN\admin --password-env DK_PW
  deskask-agent collector-cred list | remove --name NAME
  deskask-agent inventory | version | uninstall | collector-uninstall
`, version)
}

func cmdInstall(args []string, isCollector bool) error {
	name := "install"
	if isCollector {
		name = "collector-install"
	}
	fs := flag.NewFlagSet(name, flag.ExitOnError)
	server := fs.String("server", os.Getenv("DESKASK_SERVER"), "address of your DeskAsk platform")
	token := fs.String("token", os.Getenv("DESKASK_TOKEN"), "enrolment token")
	cname := fs.String("name", "", "name of this collector")
	_ = fs.Parse(args)
	if *server == "" || *token == "" {
		return fmt.Errorf("both --server and --token are needed")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Minute)
	defer cancel()
	if isCollector {
		if err := collector.Install(ctx, *server, *token, *cname, version); err != nil {
			return err
		}
		fmt.Println("The collector is installed and running. Save logins for your devices with: deskask-collector collector-cred add ...")
		return nil
	}
	if err := agentrun.Install(ctx, *server, *token, version); err != nil {
		return err
	}
	fmt.Println("The agent is installed and running.")
	return nil
}

func logToFile(name string) {
	_ = os.MkdirAll(config.Dir(), 0o755)
	p := config.Dir() + string(os.PathSeparator) + name
	if st, err := os.Stat(p); err == nil && st.Size() > 5<<20 {
		_ = os.Rename(p, p+".1")
	}
	if f, err := os.OpenFile(p, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0o644); err == nil {
		log.SetOutput(f)
	}
}

func cmdRun(isCollector bool) error {
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	if isCollector {
		logToFile("collector.log")
		var cfg config.Collector
		if err := config.Load(collector.ConfigFile, &cfg); err != nil {
			return fmt.Errorf("this computer is not set up as a collector yet (run collector-install): %w", err)
		}
		log.Printf("deskask collector %s -> %s", version, cfg.Server)
		return collector.NewRunner(cfg, version).Run(ctx)
	}
	logToFile("agent.log")
	var cfg config.Agent
	if err := config.Load(agentrun.ConfigFile, &cfg); err != nil {
		return fmt.Errorf("this computer is not set up yet (run install): %w", err)
	}
	log.Printf("deskask agent %s -> %s", version, cfg.Server)
	return agentrun.NewRunner(cfg, version).Run(ctx)
}

func cmdCred(args []string) error {
	if len(args) == 0 {
		return fmt.Errorf("use: collector-cred add | list | remove")
	}
	var cfg config.Collector
	if err := config.Load(collector.ConfigFile, &cfg); err != nil {
		return fmt.Errorf("install the collector first: %w", err)
	}
	switch args[0] {
	case "list":
		if len(cfg.Creds) == 0 {
			fmt.Println("No logins saved.")
		}
		for _, c := range cfg.Creds {
			auth := "password"
			switch {
			case c.Kind == "snmp":
				auth = "community ****"
			case c.KeyFile != "":
				auth = "key " + c.KeyFile
			case c.Password == "":
				auth = "this computer's own account"
			}
			fmt.Printf("%-16s %-8s %-24s %-28s %s\n", c.Name, c.Kind, c.User, auth, c.CIDR)
		}
		return nil
	case "remove":
		fs := flag.NewFlagSet("remove", flag.ExitOnError)
		name := fs.String("name", "", "name of the login")
		_ = fs.Parse(args[1:])
		var keep []config.Cred
		for _, c := range cfg.Creds {
			if !strings.EqualFold(c.Name, *name) {
				keep = append(keep, c)
			}
		}
		if len(keep) == len(cfg.Creds) {
			return fmt.Errorf("no login called %q", *name)
		}
		cfg.Creds = keep
		return config.Save(collector.ConfigFile, cfg)
	case "add":
		fs := flag.NewFlagSet("add", flag.ExitOnError)
		c := config.Cred{}
		fs.StringVar(&c.Kind, "kind", "", "windows, ssh or snmp")
		fs.StringVar(&c.Name, "name", "", "a name for this login")
		fs.StringVar(&c.User, "user", "", "user name")
		fs.StringVar(&c.Password, "password", "", "password (prefer --password-env: command lines can be seen by other users)")
		pwEnv := fs.String("password-env", "", "name of an environment variable that holds the password")
		fs.StringVar(&c.KeyFile, "key", "", "ssh private key file")
		fs.StringVar(&c.Community, "community", "", "SNMP v2c community")
		fs.StringVar(&c.CIDR, "cidr", "", "use this login only for devices in this network, for example 10.0.0.0/16")
		_ = fs.Parse(args[1:])
		if *pwEnv != "" {
			c.Password = os.Getenv(*pwEnv)
		}
		if c.Name == "" {
			c.Name = c.Kind
		}
		switch c.Kind {
		case "snmp":
			if c.Community == "" {
				return fmt.Errorf("an SNMP login needs --community")
			}
		case "ssh":
			if c.User == "" || (c.Password == "" && c.KeyFile == "") {
				return fmt.Errorf("an SSH login needs --user and either --key or a password")
			}
		case "windows":
			if (c.User == "") != (c.Password == "") {
				return fmt.Errorf("give both --user and a password, or neither to use the collector's own account")
			}
		default:
			return fmt.Errorf("--kind must be windows, ssh or snmp")
		}
		var keep []config.Cred
		for _, x := range cfg.Creds {
			if !strings.EqualFold(x.Name, c.Name) {
				keep = append(keep, x)
			}
		}
		cfg.Creds = append(keep, c)
		if err := config.Save(collector.ConfigFile, cfg); err != nil {
			return err
		}
		fmt.Printf("Saved login %q (%s). It stays on this computer. The collector uses it from its next check-in.\n", c.Name, c.Kind)
		return nil
	}
	return fmt.Errorf("unknown collector-cred command %q", args[0])
}

// cmdConnect is the Phase 0 heartbeat link, kept for platforms that still use /api/connect.
func cmdConnect(args []string) error {
	fs := flag.NewFlagSet("connect", flag.ExitOnError)
	url := fs.String("platform", os.Getenv("DESKASK_PLATFORM_URL"), "platform address")
	token := fs.String("token", os.Getenv("DESKASK_AGENT_TOKEN"), "agent token created in the platform")
	id := fs.String("id", hostName(), "agent id")
	_ = fs.Parse(args)
	if *url == "" || *token == "" {
		return fmt.Errorf("both --platform and --token are needed")
	}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	c := &link.Client{BaseURL: *url, Token: *token}
	return c.Run(ctx, link.Hello{AgentID: *id, Version: version, Plugins: []string{}}, 30*time.Second, func(err error) { log.Printf("heartbeat failed: %v", err) })
}

func hostName() string { h, _ := os.Hostname(); return h }
