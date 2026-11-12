package collector

import (
	"crypto/sha256"
	"encoding/hex"
	"os"
	"path/filepath"

	"github.com/deskask/platform/agent/internal/config"
)

// login is a saved login the platform sends with the work for one computer or device (Assets > Discover > Credentials).
type login struct {
	Name   string `json:"name"`
	Kind   string `json:"kind"` // windows, ssh (private key), sshpw (SSH password), snmp (community in secret)
	User   string `json:"user"`
	Secret string `json:"secret"`
}

// credsFor returns the logins saved on this computer followed by the ones the platform sent for this job.
func (r *Runner) credsFor(l []login) []config.Cred {
	out := append([]config.Cred(nil), r.Cfg.Creds...)
	for _, x := range l {
		switch x.Kind {
		case "windows":
			out = append(out, config.Cred{Name: x.Name, Kind: "windows", User: x.User, Password: x.Secret})
		case "sshpw":
			out = append(out, config.Cred{Name: x.Name, Kind: "ssh", User: x.User, Password: x.Secret})
		case "ssh":
			if f := keyFile(x.Secret); f != "" {
				out = append(out, config.Cred{Name: x.Name, Kind: "ssh", User: x.User, KeyFile: f})
			}
		case "snmp":
			out = append(out, config.Cred{Name: x.Name, Kind: "snmp", Community: x.Secret})
		}
	}
	return out
}

// keyFile keeps a private key in a file only the service account can read, named after its hash, so it is written once.
func keyFile(key string) string {
	if key == "" {
		return ""
	}
	h := sha256.Sum256([]byte(key))
	dir := filepath.Join(config.Dir(), "keys")
	if os.MkdirAll(dir, 0o700) != nil {
		return ""
	}
	f := filepath.Join(dir, hex.EncodeToString(h[:8]))
	if b, err := os.ReadFile(f); err != nil || string(b) != key+"\n" {
		if os.WriteFile(f, []byte(key+"\n"), 0o600) != nil {
			return ""
		}
	}
	return f
}
