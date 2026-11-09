#!/usr/bin/env bash
# Release dry run with the REAL parts: published platform, real bundles, real updater, real service start/stop and health check.
#   tools/release-check.sh <published host folder>
# Proves: install, upgrade, a changed bundle is refused, a broken new version is rolled back while the old one keeps answering,
# a wrong signing key is refused, and a bundle dropped in the inbox (the platform's way) is picked up by `deskask-updater run`.
set -euo pipefail
HOST_DIR=$(cd "$1" && pwd)
HERE=$(cd "$(dirname "$0")/.." && pwd)
WORK=$(mktemp -d); ROOT=$WORK/root; mkdir -p "$ROOT"
PORT=5081
pass(){ echo "PASS  $1"; }
fail(){ echo "FAIL  $1"; echo "---- host log ----"; tail -n 40 "$WORK/host.log" 2>/dev/null || true; pkill -f DeskAsk.Platform.Host.dll || true; exit 1; }
cleanup(){ pkill -f DeskAsk.Platform.Host.dll 2>/dev/null || true; pkill -f "deskask-updater run" 2>/dev/null || true; }
trap cleanup EXIT

(cd "$HERE/updater" && go build -o "$WORK/dkr" ./cmd/deskask-release && go build -o "$WORK/dku" ./cmd/deskask-updater)
"$WORK/dkr" keygen -out "$WORK/keys" >/dev/null
"$WORK/dkr" keygen -out "$WORK/otherkeys" >/dev/null
PRIV=$WORK/keys/release-private.pem; PUB=$WORK/keys/release-public.pem

pack(){ "$WORK/dkr" pack -dir "$1" -component host-linux-x64 -version "$2" -key "${3:-$PRIV}" -out "$WORK/b-$2.dkb" >/dev/null; }
export ASPNETCORE_URLS=http://localhost:$PORT
STOP="pkill -f DeskAsk.Platform.Host.dll || true; sleep 1"
START="cd $ROOT/live && nohup dotnet DeskAsk.Platform.Host.dll > $WORK/host.log 2>&1 < /dev/null &"
upd(){ "$WORK/dku" "$@" -root "$ROOT" -pub "$PUB" -stop-cmd "$STOP" -start-cmd "$START" -health "http://localhost:$PORT/health" -health-timeout 25s; }
ver(){ cat "$ROOT/live/.deskask-version" 2>/dev/null | tr -d '\n' || true; }
healthy(){ curl -sf "http://localhost:$PORT/health" >/dev/null; }

pack "$HOST_DIR" 2.0.0-ci.1; pack "$HOST_DIR" 2.0.0-ci.2
upd apply -bundle "$WORK/b-2.0.0-ci.1.dkb" >/dev/null || fail "first install"
[ "$(ver)" = "2.0.0-ci.1" ] && healthy && pass "first install runs and answers /health" || fail "first install state"

upd apply -bundle "$WORK/b-2.0.0-ci.2.dkb" >/dev/null || fail "upgrade"
[ "$(ver)" = "2.0.0-ci.2" ] && healthy && [ -d "$ROOT/previous" ] && pass "upgrade to ci.2, previous version kept" || fail "upgrade state"

# the same version again, and an older one, are refused
if upd apply -bundle "$WORK/b-2.0.0-ci.1.dkb" >/dev/null 2>&1; then fail "an older bundle must be refused"; fi
[ "$(ver)" = "2.0.0-ci.2" ] && pass "older version refused" || fail "version changed by refused bundle"

# a bundle whose payload was changed after signing
python3 - "$WORK/b-2.0.0-ci.2.dkb" "$WORK/b-tampered.dkb" <<'PY'
import sys, zipfile
src, dst = sys.argv[1:3]
with zipfile.ZipFile(src) as zi, zipfile.ZipFile(dst, "w") as zo:
    changed = False
    for i in zi.infolist():
        d = zi.read(i.filename)
        if not changed and i.filename.startswith("payload/") and i.filename.endswith(".json"):
            d = d + b" "; changed = True
        zo.writestr(i.filename, d)
    assert changed
PY
if upd apply -bundle "$WORK/b-tampered.dkb" -force >/dev/null 2>&1; then fail "a tampered bundle must be refused"; fi
[ "$(ver)" = "2.0.0-ci.2" ] && healthy && pass "tampered bundle refused, platform untouched" || fail "tampered bundle state"

# a bundle signed with a key the server does not trust
"$WORK/dkr" pack -dir "$HOST_DIR" -component host-linux-x64 -version 2.0.0-ci.9 -key "$WORK/otherkeys/release-private.pem" -out "$WORK/b-other.dkb" >/dev/null
if upd apply -bundle "$WORK/b-other.dkb" >/dev/null 2>&1; then fail "a bundle from an unknown key must be refused"; fi
pass "bundle signed by an unknown key refused"

# a new version that cannot start: rolled back, old version answers again
cp -r "$HOST_DIR" "$WORK/broken"; rm "$WORK/broken/DeskAsk.Platform.Host.dll"
pack "$WORK/broken" 2.0.0-ci.3
if upd apply -bundle "$WORK/b-2.0.0-ci.3.dkb" > "$WORK/rollback.out" 2>&1; then fail "a broken version must not stay"; fi
grep -q "previous version is running again" "$WORK/rollback.out" || { cat "$WORK/rollback.out"; fail "rollback message missing"; }
[ "$(ver)" = "2.0.0-ci.2" ] && healthy && pass "broken ci.3 rolled back, ci.2 answers /health again" || fail "rollback state"

# the platform's way: a bundle in the inbox, picked up by the running updater
pack "$HOST_DIR" 2.0.0-ci.4
ID=0123456789abcdef0123456789abcdef; mkdir -p "$ROOT/inbox/$ID"
cp "$WORK/b-2.0.0-ci.4.dkb" "$ROOT/inbox/$ID/package.dkb"
echo "{\"id\":\"$ID\",\"file\":\"host.dkb\",\"by\":\"admin@acme.test\",\"requested\":$(date +%s)000}" > "$ROOT/inbox/$ID/job.json"
upd run -interval 1s > "$WORK/run.log" 2>&1 &
for i in $(seq 1 60); do [ -f "$ROOT/state/jobs/$ID.json" ] && grep -q '"status": "done"' "$ROOT/state/jobs/$ID.json" && break; sleep 1; done
grep -q '"status": "done"' "$ROOT/state/jobs/$ID.json" || { cat "$WORK/run.log"; fail "inbox job not completed"; }
[ "$(ver)" = "2.0.0-ci.4" ] && healthy && [ ! -d "$ROOT/inbox/$ID" ] && pass "inbox job applied, recorded, inbox cleared" || fail "inbox state"
grep -q '"current": "2.0.0-ci.4"' "$ROOT/state/status.json" && pass "status.json shows the installed version" || fail "status.json"
echo "ALL PASSED"
