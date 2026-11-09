// deskask-updater runs on the server next to the platform. It is the only thing that replaces the platform's files.
//
//	deskask-updater run      -root DIR -pub release-public.pem -stop-cmd "..." -start-cmd "..." -health http://localhost:5000/health
//	deskask-updater apply    -root DIR -pub ... -bundle file.dkb [-force]
//	deskask-updater rollback -root DIR ...
//	deskask-updater status   -root DIR
package main

import (
	"context"
	"crypto/ecdsa"
	"encoding/json"
	"flag"
	"fmt"
	"log"
	"os"
	"os/signal"
	"strings"
	"time"

	"github.com/deskask/platform/updater/internal/apply"
	"github.com/deskask/platform/updater/internal/bundle"
	"github.com/deskask/platform/updater/internal/jobs"
)

func die(f string, a ...any) { fmt.Fprintf(os.Stderr, "error: "+f+"\n", a...); os.Exit(1) }

func main() {
	if len(os.Args) < 2 {
		fmt.Println("commands: run | apply | rollback | status")
		os.Exit(2)
	}
	cmd := os.Args[1]
	fs := flag.NewFlagSet(cmd, flag.ExitOnError)
	root := fs.String("root", os.Getenv("DESKASK_ROOT"), "install folder (holds live/, state/, inbox/)")
	pubFile := fs.String("pub", os.Getenv("DESKASK_RELEASE_PUB"), "release public key file(s)")
	stop := fs.String("stop-cmd", "", "command that stops the platform service")
	start := fs.String("start-cmd", "", "command that starts it")
	health := fs.String("health", "", "address that answers 200 when the platform is healthy")
	keep := fs.String("keep", "appsettings.Production.json,data", "paths in live/ kept across updates, comma separated")
	htimeout := fs.Duration("health-timeout", 90*time.Second, "how long the new version has to become healthy")
	file := fs.String("bundle", "", "bundle file (apply)")
	force := fs.Bool("force", false, "apply even when not newer")
	downgrade := fs.Bool("allow-downgrade", false, "with -force: allow an older version")
	every := fs.Duration("interval", 10*time.Second, "run: how often to look in the inbox")
	fs.Parse(os.Args[2:])
	if *root == "" {
		die("-root is needed")
	}
	env := &apply.Env{Root: *root, Runner: apply.ShellRunner{StopCmd: *stop, StartCmd: *start, HealthURL: *health}, HealthTimeout: *htimeout,
		Log: func(s string) { log.Println(s) }}
	for _, k := range strings.Split(*keep, ",") {
		if k = strings.TrimSpace(k); k != "" {
			env.Keep = append(env.Keep, k)
		}
	}
	ctx, stopSig := signal.NotifyContext(context.Background(), os.Interrupt)
	defer stopSig()
	switch cmd {
	case "status":
		b, err := os.ReadFile(*root + "/state/status.json")
		if err != nil {
			fmt.Printf("installed: %q (no status file yet)\n", apply.Installed(*root))
			return
		}
		var st jobs.Status
		json.Unmarshal(b, &st)
		fmt.Printf("installed %s, updated %s ago\n", st.Current, time.Since(time.UnixMilli(st.Updated)).Round(time.Second))
	case "rollback":
		v, err := apply.Rollback(ctx, env)
		if err != nil {
			die("%v", err)
		}
		fmt.Println("now running", v)
	case "apply", "run":
		keys := mustKeys(*pubFile)
		if cmd == "apply" {
			if *file == "" {
				die("-bundle is needed")
			}
			res, err := apply.Apply(ctx, env, *file, keys, apply.Options{Force: *force, AllowDowngrade: *downgrade})
			if err != nil {
				die("%v", err)
			}
			fmt.Printf("updated %s -> %s\n", res.From, res.To)
			return
		}
		log.Printf("deskask-updater watching %s/inbox every %s", *root, *every)
		(&jobs.Runner{Env: env, Pubs: keys, Allow: *downgrade}).Run(ctx, *every)
	default:
		die("unknown command %q", cmd)
	}
}

func mustKeys(file string) []*ecdsa.PublicKey {
	if file == "" {
		die("-pub is needed: the release public key")
	}
	p, err := os.ReadFile(file)
	if err != nil {
		die("%v", err)
	}
	k, err := bundle.ParsePublicSet(p)
	if err != nil {
		die("%v", err)
	}
	return k
}
