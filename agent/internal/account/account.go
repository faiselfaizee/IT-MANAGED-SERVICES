// Package account changes the password of an account on this computer (or, on a domain controller, of a domain account), for
// password rotation by the Vault. The new password reaches the agent once over HTTPS and is never written to disk or to a log.
package account

import (
	"errors"
	"regexp"
)

// ValidName accepts the account names Windows and Linux allow, without anything that could be read as an option or a path.
var ValidName = regexp.MustCompile(`^[A-Za-z0-9][A-Za-z0-9._$ -]{0,63}$`)

// ErrNotDC is returned when a domain account is to be changed from a computer that is not a domain controller.
var ErrNotDC = errors.New("domain accounts are changed by an agent on a domain controller; this computer is not one")

// ValidServiceName accepts Windows service names (no slashes, quotes or control characters).
var ValidServiceName = regexp.MustCompile(`^[A-Za-z0-9][A-Za-z0-9._@$ +-]{0,255}$`)

// ValidLogon accepts the accounts a service can run as: name, DOMAIN\name, .\name or name@domain.
var ValidLogon = regexp.MustCompile(`^[A-Za-z0-9.][A-Za-z0-9._$@ -]{0,63}(\\[A-Za-z0-9][A-Za-z0-9._$ -]{0,63})?$`)

// SetServiceLogon makes a Windows service run as acct with this password, and gives the account the "Log on as a service" right
// (the Services console does the same). The password is passed to Windows directly and never on a command line.
func SetServiceLogon(service, acct, password string) error {
	if !ValidServiceName.MatchString(service) {
		return errors.New("the service name is not valid")
	}
	if !ValidLogon.MatchString(acct) {
		return errors.New("the account name is not valid; use DOMAIN\\name, .\\name or name@domain")
	}
	if password == "" || len(password) > 256 {
		return errors.New("the password is empty or too long")
	}
	return setServiceLogon(service, acct, password)
}

// SetPassword sets the password of account. target is "local", "domain" or "linux".
func SetPassword(acct, target, password string) error {
	if !ValidName.MatchString(acct) {
		return errors.New("the account name is not valid")
	}
	if password == "" || len(password) > 256 {
		return errors.New("the new password is empty or too long")
	}
	return setPassword(acct, target, password)
}
