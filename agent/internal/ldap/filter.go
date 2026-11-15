package ldap

import (
	"errors"
	"strings"
)

// CompileFilter turns an RFC 4515 filter such as (&(objectClass=user)(sAMAccountName=jo*)) into its BER form.
// Supported: and, or, not, equality, presence, substrings, >=, <=, ~= and extensible match with a rule such as
// (userAccountControl:1.2.840.113556.1.4.803:=2). Values escape bytes as \HH.
func CompileFilter(s string) ([]byte, error) {
	s = strings.TrimSpace(s)
	if s == "" {
		s = "(objectClass=*)"
	}
	if s[0] != '(' {
		s = "(" + s + ")"
	}
	f, rest, err := parseFilter(s, 0)
	if err != nil {
		return nil, err
	}
	if strings.TrimSpace(rest) != "" {
		return nil, errors.New("ldap: text after the end of the filter")
	}
	return f, nil
}

func parseFilter(s string, depth int) ([]byte, string, error) {
	if depth > 32 {
		return nil, s, errors.New("ldap: filter nested too deeply")
	}
	if len(s) < 3 || s[0] != '(' {
		return nil, s, errors.New("ldap: a filter item starts with (")
	}
	s = s[1:]
	switch s[0] {
	case '&', '|':
		tag := byte(0xa0)
		if s[0] == '|' {
			tag = 0xa1
		}
		s = s[1:]
		var parts [][]byte
		for len(s) > 0 && s[0] == '(' {
			f, rest, err := parseFilter(s, depth+1)
			if err != nil {
				return nil, s, err
			}
			parts = append(parts, f)
			s = rest
		}
		if len(s) == 0 || s[0] != ')' || len(parts) == 0 {
			return nil, s, errors.New("ldap: unbalanced filter")
		}
		return enc(tag, parts...), s[1:], nil
	case '!':
		f, rest, err := parseFilter(s[1:], depth+1)
		if err != nil {
			return nil, s, err
		}
		if len(rest) == 0 || rest[0] != ')' {
			return nil, s, errors.New("ldap: unbalanced filter")
		}
		return enc(0xa2, f), rest[1:], nil
	}
	i := strings.IndexByte(s, ')')
	if i < 0 {
		return nil, s, errors.New("ldap: filter item is not closed")
	}
	f, err := parseItem(s[:i])
	return f, s[i+1:], err
}

func unescape(s string) ([]byte, error) {
	out := make([]byte, 0, len(s))
	for i := 0; i < len(s); i++ {
		if s[i] != '\\' {
			out = append(out, s[i])
			continue
		}
		if i+2 >= len(s) {
			return nil, errors.New("ldap: bad escape in filter")
		}
		h, ok1 := hexv(s[i+1])
		l, ok2 := hexv(s[i+2])
		if !ok1 || !ok2 {
			return nil, errors.New("ldap: bad escape in filter")
		}
		out = append(out, h<<4|l)
		i += 2
	}
	return out, nil
}

func hexv(c byte) (byte, bool) {
	switch {
	case c >= '0' && c <= '9':
		return c - '0', true
	case c >= 'a' && c <= 'f':
		return c - 'a' + 10, true
	case c >= 'A' && c <= 'F':
		return c - 'A' + 10, true
	}
	return 0, false
}

func parseItem(item string) ([]byte, error) {
	if k := strings.Index(item, ":="); k >= 0 { // extensible match: attr[:dn][:rule]:=value
		val, err := unescape(item[k+2:])
		if err != nil {
			return nil, err
		}
		parts := strings.Split(item[:k], ":")
		attr, rule, dn := parts[0], "", false
		for _, p := range parts[1:] {
			if strings.EqualFold(p, "dn") {
				dn = true
			} else if p != "" {
				rule = p
			}
		}
		if attr == "" && rule == "" {
			return nil, errors.New("ldap: extensible match needs an attribute or a rule")
		}
		var p [][]byte
		if rule != "" {
			p = append(p, enc(0x81, []byte(rule)))
		}
		if attr != "" {
			p = append(p, enc(0x82, []byte(attr)))
		}
		p = append(p, enc(0x83, val))
		if dn {
			p = append(p, enc(0x84, []byte{0xff}))
		}
		return enc(0xa9, p...), nil
	}
	k := strings.IndexByte(item, '=')
	if k <= 0 {
		return nil, errors.New("ldap: filter item needs attribute=value")
	}
	attr, val, tag := item[:k], item[k+1:], byte(0xa3)
	switch attr[len(attr)-1] {
	case '>':
		attr, tag = attr[:len(attr)-1], 0xa5
	case '<':
		attr, tag = attr[:len(attr)-1], 0xa6
	case '~':
		attr, tag = attr[:len(attr)-1], 0xa8
	}
	if attr == "" || strings.ContainsAny(attr, " ()*\\") {
		return nil, errors.New("ldap: bad attribute name in filter")
	}
	if tag == 0xa3 {
		if val == "*" {
			return enc(0x87, []byte(attr)), nil
		}
		if strings.Contains(val, "*") {
			pieces := strings.Split(val, "*")
			var subs [][]byte
			for n, p := range pieces {
				if p == "" {
					continue
				}
				b, err := unescape(p)
				if err != nil {
					return nil, err
				}
				switch {
				case n == 0:
					subs = append(subs, enc(0x80, b))
				case n == len(pieces)-1:
					subs = append(subs, enc(0x82, b))
				default:
					subs = append(subs, enc(0x81, b))
				}
			}
			return enc(0xa4, octet(attr), enc(0x30, subs...)), nil
		}
	}
	b, err := unescape(val)
	if err != nil {
		return nil, err
	}
	return enc(tag, octet(attr), enc(0x04, b)), nil
}

// Escape makes a value safe to put inside a filter.
func Escape(s string) string {
	var sb strings.Builder
	for i := 0; i < len(s); i++ {
		switch c := s[i]; c {
		case '*', '(', ')', '\\', 0:
			sb.WriteString("\\")
			sb.WriteByte("0123456789abcdef"[c>>4])
			sb.WriteByte("0123456789abcdef"[c&15])
		default:
			sb.WriteByte(c)
		}
	}
	return sb.String()
}
