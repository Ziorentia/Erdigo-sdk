#!/bin/bash
# Runs the cloud relay and gateway examples with the commands the README shows, against erdigo-send's
# mock receiver, and sends to the relay with curl.
#
#     swift build && swift/Tests/cloud-relay-smoke.sh
#
# The XCTest suites CloudRelayTests and CloudGatewayTests check the details. This checks the programs
# as a person runs them, including typing the pairing code in a terminal.
#
set -u

TESTS_DIR="$(cd "$(dirname "$0")" && pwd)"
HELPER="$TESTS_DIR/typed-ahead-check.py"

# Nothing may run for long or wait for a person.
limited() {
    perl -e 'alarm 120; exec { $ARGV[0] } @ARGV or exit 127' -- "$@"
}
exec </dev/null

BIN_DIR="$(limited swift build --show-bin-path)"
RELAY="$BIN_DIR/erdigo-example-relay"
GATEWAY="$BIN_DIR/erdigo-example-gateway"
SEND="$BIN_DIR/erdigo-send"
WORK="$(mktemp -d)"
PIDS=""
FAILURES=0

cleanup() {
    # shellcheck disable=SC2086
    [ -n "$PIDS" ] && kill $PIDS 2>/dev/null
    rm -rf "$WORK"
}
trap cleanup EXIT

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

contains() {
    [[ "$1" == *"$2"* ]]
}

# Waits for a line starting with $2 in file $1 and prints it.
wait_for_line() {
    for _ in $(seq 1 100); do
        if grep -q "^$2" "$1" 2>/dev/null; then
            grep "^$2" "$1" | head -1
            return 0
        fi
        sleep 0.1
    done
    return 1
}

# Tokens made as the guide suggests.
token() {
    openssl rand -base64 32 | tr '+/' '-_' | tr -d '='
}
SOURCE_TOKEN="$(token)"
GATEWAY_TOKEN="$(token)"

mkfifo "$WORK/commands"
"$SEND" serve --mock --code-file "$WORK/code" <"$WORK/commands" >"$WORK/mock.log" 2>&1 &
PIDS="$PIDS $!"
# Stopped by the cleanup, so there is no need to hear about it.
disown
exec 3>"$WORK/commands"
wait_for_line "$WORK/mock.log" "Type .help" >/dev/null || { echo "FAIL  the mock did not start"; cat "$WORK/mock.log"; exit 1; }
PORT="$(grep -oE 'port [0-9]+' "$WORK/mock.log" | awk '{print $2}')"

ERDIGO_RELAY_SOURCE_TOKENS="$SOURCE_TOKEN" ERDIGO_RELAY_GATEWAY_TOKEN="$GATEWAY_TOKEN" \
    "$RELAY" --listen 127.0.0.1:0 >"$WORK/relay.log" 2>&1 &
RELAY_PID=$!
PIDS="$PIDS $RELAY_PID"
LINE="$(wait_for_line "$WORK/relay.log" "Relay listening on ")" || { echo "FAIL  the relay did not start"; cat "$WORK/relay.log"; exit 1; }
RELAY_URL="${LINE#Relay listening on }"
check "the relay prints where it listens" contains "$RELAY_URL" "http://127.0.0.1:"
check "the relay answers a health check" contains "$(curl -s "$RELAY_URL/v1/health")" '{"ok":true}'

STATE="$WORK/gateway"
OUTPUT="$(limited "$GATEWAY" pair --name "Cloud gateway" --host 127.0.0.1 --port "$PORT" --code-file "$WORK/code" --state-dir "$STATE" 2>&1)"
check "pair with the QR text in a code file" contains "$OUTPUT" "Paired. Allowed: pin, marker, note, photo, track."

ANSWER="$(curl -s "$RELAY_URL/v1/ops" \
    -H "Authorization: Bearer $SOURCE_TOKEN" \
    -H "Content-Type: application/json" \
    -d '{"ops":[{"op":"pin","id":"drone-1","lat":37.5663,"lon":126.9779,"title":"Drone 1"},{"op":"track","id":"flight-7","title":"Flight 7","points":[{"lat":37.5663,"lon":126.9779,"ts":1700000000000,"alt":120}]}]}')"
check "the relay takes the guide's example" contains "$ANSWER" '{"accepted":2,"last_seq":2}'

OUTPUT="$(ERDIGO_RELAY_TOKEN="$GATEWAY_TOKEN" limited "$GATEWAY" run --relay "$RELAY_URL" --host 127.0.0.1 --port "$PORT" --once --state-dir "$STATE" 2>&1)"
check "run --once delivers" contains "$OUTPUT" "Done with 2 operations, up to 2"
echo "items 1" >&3
sleep 0.5
check "the pin reached the receiver" grep -q "pin drone-1 at 37.5663,126.9779 title=Drone 1" "$WORK/mock.log"
check "the track reached the receiver" grep -q "track flight-7 at -,- title=Flight 7 points=1" "$WORK/mock.log"
OUTPUT="$(ERDIGO_RELAY_TOKEN="$GATEWAY_TOKEN" limited "$GATEWAY" run --relay "$RELAY_URL" --host 127.0.0.1 --port "$PORT" --once --state-dir "$STATE" 2>&1)"
check "a second run has nothing to do" bash -c '[[ "$0" != *"Done with"* ]]' "$OUTPUT"
check "the relay forgot what was delivered" contains "$(curl -s "$RELAY_URL/v1/ops" -H "Authorization: Bearer $GATEWAY_TOKEN")" '"ops":[]'
check "the place is private" test "$(stat -f '%Lp' "$STATE/relay-state.json")" = "600"

ERDIGO_RELAY_TOKEN="$(token)" limited "$GATEWAY" run --relay "$RELAY_URL" --host 127.0.0.1 --port "$PORT" --once --state-dir "$STATE" >/dev/null 2>&1
check "a wrong token exits with 2" test "$?" = "2"
ERDIGO_RELAY_TOKEN="$GATEWAY_TOKEN" limited "$GATEWAY" run --relay "http://relay.example.com" --once --state-dir "$STATE" >/dev/null 2>&1
check "plain http to another computer exits with 1" test "$?" = "1"

# A bare code typed at the keyboard, and one in a code file, both need the key compared in a terminal.
echo "code" >&3
for _ in $(seq 1 50); do
    [ "$(grep -c 'Pairing text written' "$WORK/mock.log")" -ge 2 ] && break
    sleep 0.1
done
TYPED="$(sed -E 's/.*code=([^&]*).*/\1/' "$WORK/code")"
RUN_OUTPUT="$(python3 -I "$HELPER" "$PORT" --answer "yes"$'\n' --type-code "$TYPED" -- \
    "$GATEWAY" pair --name "Typed gateway" --kinds pin --host 127.0.0.1 --port PROXY_PORT --state-dir "$WORK/typed" 2>&1)"
check "a typed code asks for the key to be compared" contains "$RUN_OUTPUT" "type yes"
check "a typed code pairs once confirmed" contains "$RUN_OUTPUT" "Paired. Allowed: pin."
check "the typed code is not shown" bash -c '[[ "$0" != *"$1"* ]]' "$RUN_OUTPUT" "$TYPED"

# Stopping is clean.
kill -TERM "$RELAY_PID"
wait "$RELAY_PID"
check "the relay stops on SIGTERM with 0" test "$?" = "0"
check "the relay's log holds no tokens or titles" bash -c '! grep -qE "$0|$1|Drone 1|Flight 7|37\.5663" "$2"' "$SOURCE_TOKEN" "$GATEWAY_TOKEN" "$WORK/relay.log"

# Listening beyond loopback can make the macOS firewall ask a question, so only where nobody is asked.
if [ "${ERDIGO_LAN_CHECKS:-}" = "1" ]; then
    ERDIGO_RELAY_SOURCE_TOKENS="$SOURCE_TOKEN" ERDIGO_RELAY_GATEWAY_TOKEN="$GATEWAY_TOKEN" \
        limited "$RELAY" --listen 0.0.0.0:0 >"$WORK/public.log" 2>&1
    check "a public address without --behind-proxy is refused" test "$?" = "1"
    ERDIGO_RELAY_SOURCE_TOKENS="$SOURCE_TOKEN" ERDIGO_RELAY_GATEWAY_TOKEN="$GATEWAY_TOKEN" \
        "$RELAY" --listen 0.0.0.0:0 --behind-proxy >"$WORK/proxy.log" 2>&1 &
    PROXY_PID=$!
    PIDS="$PIDS $PROXY_PID"
    LINE="$(wait_for_line "$WORK/proxy.log" "Relay listening on ")"
    check "behind a proxy it listens on every address" contains "$LINE" "Relay listening on http://0.0.0.0:"
    kill -INT "$PROXY_PID"
    wait "$PROXY_PID"
    check "the relay stops on Ctrl+C with 0" test "$?" = "0"
else
    echo "skip  listening beyond loopback (set ERDIGO_LAN_CHECKS=1)"
fi

echo
if [ "$FAILURES" -eq 0 ]; then
    echo "All checks passed."
else
    echo "$FAILURES check(s) failed."
    exit 1
fi
