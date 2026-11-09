// deskask-release: make the release key, pack a folder into a signed bundle, check a bundle.
//
//	deskask-release keygen -out DIR
//	deskask-release pack -dir publish/ -component host-linux-x64 -version 2.0.0 -key release-private.pem -out host-linux-x64-2.0.0.dkb
//	deskask-release verify -pub release-public.pem bundle.dkb
package main

import (
	"flag"
	"fmt"
	"os"
	"path/filepath"

	"github.com/deskask/platform/updater/internal/bundle"
)

func die(f string, a ...any) { fmt.Fprintf(os.Stderr, "error: "+f+"\n", a...); os.Exit(1) }

func main() {
	if len(os.Args) < 2 {
		fmt.Println("commands: keygen | pack | verify")
		os.Exit(2)
	}
	switch os.Args[1] {
	case "keygen":
		fs := flag.NewFlagSet("keygen", flag.ExitOnError)
		out := fs.String("out", ".", "folder")
		fs.Parse(os.Args[2:])
		k, err := bundle.GenerateKey()
		if err != nil {
			die("%v", err)
		}
		priv, _ := bundle.MarshalPrivate(k)
		pub, _ := bundle.MarshalPublic(&k.PublicKey)
		os.MkdirAll(*out, 0o755)
		if err := os.WriteFile(filepath.Join(*out, "release-private.pem"), priv, 0o600); err != nil {
			die("%v", err)
		}
		if err := os.WriteFile(filepath.Join(*out, "release-public.pem"), pub, 0o644); err != nil {
			die("%v", err)
		}
		fmt.Printf("Written release-private.pem (secret: GitHub secret RELEASE_SIGNING_KEY) and release-public.pem (goes to every server). Key id %s.\n", bundle.KeyID(&k.PublicKey))
	case "pack":
		fs := flag.NewFlagSet("pack", flag.ExitOnError)
		dir := fs.String("dir", "", "folder to pack")
		comp := fs.String("component", "", "for example host-linux-x64")
		ver := fs.String("version", "", "for example 2.0.0")
		key := fs.String("key", "", "private key file, or '-' to read it from the environment variable RELEASE_SIGNING_KEY")
		out := fs.String("out", "", "bundle file to write")
		fs.Parse(os.Args[2:])
		if *dir == "" || *comp == "" || *ver == "" || *key == "" || *out == "" {
			die("-dir, -component, -version, -key and -out are all needed")
		}
		var pem []byte
		var err error
		if *key == "-" {
			pem = []byte(os.Getenv("RELEASE_SIGNING_KEY"))
		} else if pem, err = os.ReadFile(*key); err != nil {
			die("%v", err)
		}
		k, err := bundle.ParsePrivate(pem)
		if err != nil {
			die("%v", err)
		}
		if err := bundle.Pack(*dir, *out, bundle.Manifest{Component: *comp, Version: *ver}, k); err != nil {
			die("%v", err)
		}
		fmt.Printf("%s written (%s %s, key %s)\n", *out, *comp, *ver, bundle.KeyID(&k.PublicKey))
	case "verify":
		fs := flag.NewFlagSet("verify", flag.ExitOnError)
		pub := fs.String("pub", "", "public key file")
		fs.Parse(os.Args[2:])
		if *pub == "" || fs.NArg() != 1 {
			die("usage: verify -pub release-public.pem bundle.dkb")
		}
		p, err := os.ReadFile(*pub)
		if err != nil {
			die("%v", err)
		}
		pubs, err := bundle.ParsePublicSet(p)
		if err != nil {
			die("%v", err)
		}
		b, err := bundle.Open(fs.Arg(0), pubs)
		if err != nil {
			die("%v", err)
		}
		defer b.Close()
		fmt.Printf("OK: %s %s, %d files, key %s, made %s\n", b.Manifest.Component, b.Manifest.Version, len(b.Manifest.Files), b.Manifest.KeyID, b.Manifest.Created)
	default:
		die("unknown command %q", os.Args[1])
	}
}
