//go:build linux

package metrics

import "testing"

func TestParseProcStatHandlesBracketsAndSpacesInTheName(t *testing.T) {
	line := "4242 (my (odd) prog) S 1 4242 4242 0 -1 4194560 100 0 0 0 150 50 0 0 20 0 1 0 12345 1000 200 18446744073709551615"
	name, cpu, ok := parseProcStat(line)
	if !ok || name != "my (odd) prog" || cpu != 200 {
		t.Fatalf("got %q %d %v", name, cpu, ok)
	}
	if _, _, ok := parseProcStat("garbage"); ok {
		t.Fatal("garbage must not parse")
	}
}
