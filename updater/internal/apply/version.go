package apply

import (
	"fmt"
	"strconv"
	"strings"
)

type semver struct {
	core [3]int
	pre  []string
}

func parse(v string) (semver, error) {
	var s semver
	v = strings.TrimPrefix(strings.TrimSpace(v), "v")
	if i := strings.Index(v, "+"); i >= 0 {
		v = v[:i]
	}
	if i := strings.Index(v, "-"); i >= 0 {
		s.pre = strings.Split(v[i+1:], ".")
		v = v[:i]
	}
	parts := strings.Split(v, ".")
	if len(parts) != 3 {
		return s, fmt.Errorf("version %q is not major.minor.patch", v)
	}
	for i, p := range parts {
		n, err := strconv.Atoi(p)
		if err != nil || n < 0 {
			return s, fmt.Errorf("version part %q is not a number", p)
		}
		s.core[i] = n
	}
	return s, nil
}

// CompareVersions follows semantic versioning: 2.0.0-alpha.1 < 2.0.0-alpha.2 < 2.0.0-beta < 2.0.0 < 2.0.1.
func CompareVersions(a, b string) (int, error) {
	x, err := parse(a)
	if err != nil {
		return 0, err
	}
	y, err := parse(b)
	if err != nil {
		return 0, err
	}
	for i := 0; i < 3; i++ {
		if x.core[i] != y.core[i] {
			return sign(x.core[i] - y.core[i]), nil
		}
	}
	switch {
	case len(x.pre) == 0 && len(y.pre) == 0:
		return 0, nil
	case len(x.pre) == 0:
		return 1, nil
	case len(y.pre) == 0:
		return -1, nil
	}
	for i := 0; i < len(x.pre) && i < len(y.pre); i++ {
		p, q := x.pre[i], y.pre[i]
		pn, perr := strconv.Atoi(p)
		qn, qerr := strconv.Atoi(q)
		switch {
		case perr == nil && qerr == nil:
			if pn != qn {
				return sign(pn - qn), nil
			}
		case perr == nil:
			return -1, nil
		case qerr == nil:
			return 1, nil
		case p != q:
			return sign(strings.Compare(p, q)), nil
		}
	}
	return sign(len(x.pre) - len(y.pre)), nil
}

func sign(n int) int {
	switch {
	case n < 0:
		return -1
	case n > 0:
		return 1
	}
	return 0
}
