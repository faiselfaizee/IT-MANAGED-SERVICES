//go:build !linux && !windows

package metrics

const procUnitsPerSec = 1.0

func readProcs() (map[int]procRaw, error) { return nil, ErrUnsupported }
