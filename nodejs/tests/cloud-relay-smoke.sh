#!/bin/bash
# Runs the cloud relay example as its README section shows, on macOS or Linux: the relay, a device sending with curl,
# and the gateway writing to the mock receiver of another edition.
#
#     nodejs/tests/cloud-relay-smoke.sh
#
# The mock is the .NET one by default (build it first with `dotnet build dotnet/Erdigo.sln`). Set ERDIGO_MOCK_BIN to
# another edition's erdigo-send, such as the Swift one at .build/debug/erdigo-send, to use that instead.
#
set -u

TESTS_DIR="$(cd "$(dirname "$0")" && pwd)"
NODE_DIR="$(cd "$TESTS_DIR/.." && pwd)"
ROOT="$(cd "$NODE_DIR/.." && pwd)"
RELAY="$NODE_DIR/examples/cloud-relay/relay.js"
GATEWAY="$NODE_DIR/examples/cloud-relay/gateway.js"

# Stops a command that takes too long, so that it cannot hang this script. An alarm survives exec.
limited() {
    perl -e 'alarm 120; exec { $ARGV[0] } @ARGV or exit 127' -- "$@"
}

MOCK_BIN="${ERDIGO_MOCK_BIN:-}"
if [ -z "$MOCK_BIN" ]; then
    for candidate in "$ROOT/dotnet/src/erdigo-send/bin/Release/net8.0/erdigo-send" \
        "$ROOT/dotnet/src/erdigo-send/bin/Debug/net8.0/erdigo-send" "$ROOT/.build/debug/erdigo-send"; do
        if [ -x "$candidate" ]; then
            MOCK_BIN="$candidate"
            break
        fi
    done
fi
[ -n "$MOCK_BIN" ] && [ -x "$MOCK_BIN" ] || { echo "Build a mock first: dotnet build dotnet/Erdigo.sln, or set ERDIGO_MOCK_BIN"; exit 1; }
[ -x "$RELAY" ] && [ -x "$GATEWAY" ] || { echo "The relay and the gateway must be executable"; exit 1; }
echo "mock  $MOCK_BIN"

WORK="$(mktemp -d)"
MOCK_PID=""
RELAY_PID=""
GATEWAY_PID=""
FAILURES=0

cleanup() {
    [ -n "$MOCK_PID" ] && kill "$MOCK_PID" 2>/dev/null
    [ -n "$RELAY_PID" ] && kill "$RELAY_PID" 2>/dev/null
    [ -n "$GATEWAY_PID" ] && kill "$GATEWAY_PID" 2>/dev/null
    rm -rf "$WORK"
}
trap cleanup EXIT
exec </dev/null

check() {
    local name="$1"
    shift
    if "$@"; then
        echo "ok    $name"
    else
        echo "FAIL  $name"
        FAILURES=$((FAILURES + 1))
    fi
}

# Waits up to ten seconds for a line matching PATTERN in FILE.
wait_for() {
    for _ in $(seq 1 100); do
        grep -q "$1" "$2" 2>/dev/null && return 0
        sleep 0.1
    done
    return 1
}

# The mock keeps running while its standard input stays open, and takes commands from it.
mkfifo "$WORK/commands"
"$MOCK_BIN" serve --mock --code-file "$WORK/code" <"$WORK/commands" >"$WORK/mock.log" 2>&1 &
MOCK_PID=$!
exec 3>"$WORK/commands"
wait_for 'Type .help' "$WORK/mock.log" || { echo "FAIL  the mock did not start"; cat "$WORK/mock.log"; exit 1; }
PORT="$(grep -oE 'port [0-9]+' "$WORK/mock.log" | head -1 | awk '{print $2}')"

# Tokens made as docs/cloud-relay.md suggests.
SOURCE_TOKEN="$(openssl rand -base64 32 | tr '+/' '-_' | tr -d '=')"
GATEWAY_TOKEN="$(openssl rand -base64 32 | tr '+/' '-_' | tr -d '=')"

ERDIGO_RELAY_SOURCE_TOKENS="$SOURCE_TOKEN" ERDIGO_RELAY_GATEWAY_TOKEN="$GATEWAY_TOKEN" \
    node "$RELAY" --listen 127.0.0.1:0 >"$WORK/relay.log" 2>&1 &
RELAY_PID=$!
wait_for '^Relay listening on http://127.0.0.1:[0-9]*$' "$WORK/relay.log" || { echo "FAIL  the relay did not start"; cat "$WORK/relay.log"; exit 1; }
RELAY_URL="$(sed -n 's/^Relay listening on //p' "$WORK/relay.log" | head -1)"
echo "ok    the relay prints where it listens"

check "the relay answers health" bash -c '[ "$(curl -s "$0/v1/health")" = "{\"ok\":true}" ]' "$RELAY_URL"

limited node "$GATEWAY" pair --name "Cloud gateway" --host 127.0.0.1 --port "$PORT" --code-file "$WORK/code" \
    --state-dir "$WORK/state" >"$WORK/pair.log" 2>&1
check "the gateway pairs through a code file" grep -q '^Paired. Allowed: pin, marker, note, photo, track.$' "$WORK/pair.log"

# Step 5 of docs/cloud-relay.md, against this relay.
ANSWER="$(curl -s "$RELAY_URL/v1/ops" \
    -H "Authorization: Bearer $SOURCE_TOKEN" \
    -H "Content-Type: application/json" \
    -d '{"ops":[{"op":"pin","id":"drone-1","lat":37.5663,"lon":126.9779,"title":"Drone 1"}]}')"
check "the relay takes a pin from curl" test "$ANSWER" = '{"accepted":1,"last_seq":1}'

ERDIGO_RELAY_TOKEN="$GATEWAY_TOKEN" limited node "$GATEWAY" run --relay "$RELAY_URL" --host 127.0.0.1 --port "$PORT" \
    --state-dir "$WORK/state" --once >"$WORK/run.log" 2>&1
STATUS=$?
check "the gateway exits with 0 when nothing more is waiting" test "$STATUS" = 0
check "the gateway says what it did" grep -qx 'Done with 1 operation, up to 1' "$WORK/run.log"
check "the gateway prints no token" bash -c '! grep -q -e "$0" -e "$1" "$2"' "$SOURCE_TOKEN" "$GATEWAY_TOKEN" "$WORK/run.log"
check "the gateway's place is private" test "$(stat -c '%a' "$WORK/state/relay-state.json" 2>/dev/null || stat -f '%Lp' "$WORK/state/relay-state.json")" = 600

echo "items 1" >&3
check "the mock holds the pin" wait_for '^pin drone-1 at 37.5663,126.9779 title=Drone 1 points=0$' "$WORK/mock.log"

ERDIGO_RELAY_TOKEN="$GATEWAY_TOKEN" limited node "$GATEWAY" run --relay "$RELAY_URL" --host 127.0.0.1 --port "$PORT" \
    --state-dir "$WORK/state" --once >"$WORK/again.log" 2>&1
check "running again applies nothing" test ! -s "$WORK/again.log"

# Only one run at a time may use a profile. The lock file holds the process id of the run that has it.
ERDIGO_RELAY_TOKEN="$GATEWAY_TOKEN" node "$GATEWAY" run --relay "$RELAY_URL" --host 127.0.0.1 --port "$PORT" \
    --state-dir "$WORK/state" >"$WORK/first.log" 2>&1 &
GATEWAY_PID=$!
check "a running gateway holds the profile" wait_for "^$GATEWAY_PID\$" "$WORK/state/.gateway.lock"
ERDIGO_RELAY_TOKEN="$GATEWAY_TOKEN" limited node "$GATEWAY" run --relay "$RELAY_URL" --host 127.0.0.1 --port "$PORT" \
    --state-dir "$WORK/state" --once >"$WORK/second.log" 2>&1
STATUS=$?
check "a second run on that profile is refused (exit status 1)" test "$STATUS" = 1
check "and says so in one line" \
    grep -qx "gateway: another gateway (process $GATEWAY_PID) is already running with this profile" "$WORK/second.log"
kill -TERM "$GATEWAY_PID"
wait "$GATEWAY_PID"
STATUS=$?
GATEWAY_PID=""
check "the first run stops on SIGTERM with status 0" test "$STATUS" = 0
check "and gives the profile back" test ! -e "$WORK/state/.gateway.lock"

ERDIGO_RELAY_TOKEN="$SOURCE_TOKEN" limited node "$GATEWAY" run --relay "$RELAY_URL" --host 127.0.0.1 --port "$PORT" \
    --state-dir "$WORK/state" --once >/dev/null 2>&1
STATUS=$?
check "a source token cannot fetch (exit status 2)" test "$STATUS" = 2

ERDIGO_RELAY_TOKEN="$GATEWAY_TOKEN" limited node "$GATEWAY" run --relay "http://relay.example.com" --state-dir "$WORK/state" --once >/dev/null 2>&1
STATUS=$?
check "plain http beyond this computer is refused (exit status 1)" test "$STATUS" = 1

check "the relay logs no token or operation" bash -c '! grep -q -e "$0" -e "$1" -e drone -e 37.5663 "$2"' "$SOURCE_TOKEN" "$GATEWAY_TOKEN" "$WORK/relay.log"

kill -TERM "$RELAY_PID"
wait "$RELAY_PID"
STATUS=$?
RELAY_PID=""
check "the relay stops on SIGTERM with status 0" test "$STATUS" = 0

if [ "$FAILURES" -gt 0 ]; then
    echo "$FAILURES failed"
    echo "--- relay"; cat "$WORK/relay.log"
    echo "--- gateway"; cat "$WORK/pair.log" "$WORK/run.log" "$WORK/first.log" "$WORK/second.log" 2>/dev/null
    echo "--- mock"; cat "$WORK/mock.log"
    exit 1
fi
echo "All passed."
