package main

import (
	"context"
	"flag"
	"log"
	"os"
	"os/signal"
	"time"

	"github.com/deskask/platform/agent/internal/link"
)

var version = "2.0.0-alpha.1"

func main() {
	url := flag.String("platform", os.Getenv("DESKASK_PLATFORM_URL"), "platform address, for example https://acme.deskask.in")
	token := flag.String("token", os.Getenv("DESKASK_AGENT_TOKEN"), "agent token created in the platform")
	id := flag.String("id", mustHost(), "agent id")
	flag.Parse()
	if *url == "" || *token == "" {
		log.Fatal("both -platform and -token are needed")
	}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
	defer stop()
	c := &link.Client{BaseURL: *url, Token: *token}
	log.Printf("deskask agent %s -> %s", version, *url)
	if err := c.Run(ctx, link.Hello{AgentID: *id, Version: version, Plugins: []string{}}, 30*time.Second, func(err error) { log.Printf("heartbeat failed: %v", err) }); err != nil {
		log.Fatal(err)
	}
}

func mustHost() string { h, _ := os.Hostname(); return h }
