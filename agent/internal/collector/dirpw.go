package collector

// windowsPasswords sets a password without LDAPS where the controller runs on Windows; nil elsewhere.
var windowsPasswords func(c dirConn, o dirOp) error
