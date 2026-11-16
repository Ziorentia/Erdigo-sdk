#!/bin/bash
# Runs the cloud relay example as java/README.md shows it, from source with the built jar, against the .NET SDK's
# mock receiver, on macOS or Linux.
#
#     (cd java && ./mvnw -B package) && dotnet build dotnet/src/erdigo-send -c Release && java/tests/cloud-relay-smoke.sh
#
# MOCK names the mock's erdigo-send executable (by default the .NET Release build). The JUnit tests in
# src/test/java/com/ziorentia/erdigo/examples check the example in depth; this checks that the commands work as printed.
set -u

TESTS_DIR="$(cd "$(dirname "$0")" && pwd)"
JAVA_DIR="$(cd "$TESTS_DIR/.." && pwd)"
ROOT="$(cd "$JAVA_DIR/.." && pwd)"
MOCK="${MOCK:-$ROOT/dotnet/src/erdigo-send/bin/Release/net8.0/erdigo-send}"
[ -f "$JAVA_DIR/target/erdigo-send.jar" ] || { echo "Build first: (cd java && ./mvnw -B package)"; exit 1; }
[ -x "$MOCK" ] || { echo "Build the .NET mock first: dotnet build dotnet/src/erdigo-send -c Release, or set MOCK"; exit 1; }

# Stops a command that takes too long, so that it cannot hang this script. An alarm survives exec.
limited() {
    perl -e 'alarm 120; exec { $ARGV[0] } @ARGV or exit 127' -- "$@"
}

mode_of() {
    if stat -f '%Lp' "$1" >/dev/null 2>&1; then stat -f '%Lp' "$1"; else stat -c '%a' "$1"; fi
}

WORK="$(mktemp -d)"
PIDS=()
FAILURES=0

cleanup() {
    for pid in "${PIDS[@]}"; do
        kill "$pid" 2>/dev/null
    done
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

# Waits for a line starting with $2 in file $1, and prints it.
wait_for() {
    for _ in $(seq 1 600); do
        if grep -q "^$2" "$1" 2>/dev/null; then
            grep "^$2" "$1" | head -1
            return 0
        fi
        sleep 0.1
    done
    return 1
}

exec </dev/null
# The commands in the README run from the java folder.
cd "$JAVA_DIR" || exit 1

# Tokens, made as the guide makes them.
SOURCE_TOKEN="$(openssl rand -base64 32 | tr '+/' '-_' | tr -d '=')"
GATEWAY_TOKEN="$(openssl rand -base64 32 | tr '+/' '-_' | tr -d '=')"

mkdir -m 700 "$WORK/mock"
mkfifo "$WORK/mock/commands"
"$MOCK" serve --mock --code-file "$WORK/mock/code" <"$WORK/mock/commands" >"$WORK/mock.log" 2>&1 &
PIDS+=($!)
exec 3>"$WORK/mock/commands"
wait_for "$WORK/mock.log" "Type 'help'" >/dev/null || { echo "FAIL  the mock did not start"; cat "$WORK/mock.log"; exit 1; }
PORT="$(grep -oE 'port [0-9]+' "$WORK/mock.log" | awk '{print $2}')"

ERDIGO_RELAY_SOURCE_TOKENS="$SOURCE_TOKEN" ERDIGO_RELAY_GATEWAY_TOKEN="$GATEWAY_TOKEN" \
    java -cp target/erdigo-send.jar examples/cloud-relay/Relay.java --listen 127.0.0.1:0 >"$WORK/relay.log" 2>&1 &
RELAY_PID=$!
PIDS+=("$RELAY_PID")
LINE="$(wait_for "$WORK/relay.log" "Relay listening on ")"
check "the relay says where it listens" bash -c '[[ "$0" =~ ^Relay\ listening\ on\ http://127\.0\.0\.1:[0-9]+$ ]]' "$LINE"
RELAY_URL="${LINE#Relay listening on }"

check "pair" limited java -cp target/erdigo-send.jar examples/cloud-relay/Gateway.java pair --name "Cloud gateway" \
    --host 127.0.0.1 --port "$PORT" --code-file "$WORK/mock/code" --state-dir "$WORK/gateway"

# The device's side, exactly as the guide shows it.
STATUS="$(curl -sS -o "$WORK/post.json" -w '%{http_code}' "$RELAY_URL/v1/ops" \
    -H "Authorization: Bearer $SOURCE_TOKEN" \
    -H "Content-Type: application/json" \
    -d '{"ops":[{"op":"pin","id":"drone-1","lat":37.5663,"lon":126.9779,"title":"Drone 1"}]}')"
check "the relay takes the guide's example" test "$STATUS" = "200"
check "and says so" grep -q '^{"accepted":1,"last_seq":1}$' "$WORK/post.json"

OUTPUT="$(ERDIGO_RELAY_TOKEN="$GATEWAY_TOKEN" limited java -cp target/erdigo-send.jar examples/cloud-relay/Gateway.java run \
    --relay "$RELAY_URL" --host 127.0.0.1 --port "$PORT" --state-dir "$WORK/gateway" --once 2>&1)"
check "run --once delivers it" test "$OUTPUT" = "Done with 1 operation, up to 1"
echo "items 1" >&3
echo "help" >&3
wait_for "$WORK/mock.log" "pin drone-1" >/dev/null
check "the mock holds the pin" grep -q '^pin drone-1 at 37.5663,126.9779 title=Drone 1' "$WORK/mock.log"
check "the state folder is private" test "$(mode_of "$WORK/gateway")" = "700"
check "the place in the relay's queue is private" test "$(mode_of "$WORK/gateway/relay-state.json")" = "600"
OUTPUT="$(ERDIGO_RELAY_TOKEN="$GATEWAY_TOKEN" limited java -cp target/erdigo-send.jar examples/cloud-relay/Gateway.java run \
    --relay "$RELAY_URL" --host 127.0.0.1 --port "$PORT" --state-dir "$WORK/gateway" --once 2>&1)"
check "running again does nothing" test -z "$OUTPUT"
check "the relay's log holds no token" bash -c '! grep -q -e "$0" -e "$1" "$2"' "$SOURCE_TOKEN" "$GATEWAY_TOKEN" "$WORK/relay.log"
check "nor what the pin held" bash -c '! grep -q -e "drone-1" -e "37.5663" "$0"' "$WORK/relay.log"

ERDIGO_RELAY_TOKEN="$GATEWAY_TOKEN" limited java -cp target/erdigo-send.jar examples/cloud-relay/Gateway.java run \
    --relay "http://192.0.2.1:8080" --state-dir "$WORK/gateway" --once >/dev/null 2>&1
check "the gateway refuses plain http:// to another computer" test "$?" = "1"

kill -TERM "$RELAY_PID"
wait "$RELAY_PID"
check "the relay stops on SIGTERM with status 0" test "$?" = "0"

ERDIGO_RELAY_SOURCE_TOKENS="$SOURCE_TOKEN" ERDIGO_RELAY_GATEWAY_TOKEN="$GATEWAY_TOKEN" \
    java -cp target/erdigo-send.jar examples/cloud-relay/Relay.java --listen '[::1]:0' >"$WORK/relay6.log" 2>&1 &
RELAY6_PID=$!
PIDS+=("$RELAY6_PID")
LINE="$(wait_for "$WORK/relay6.log" "Relay listening on ")"
check "an IPv6 address is written in brackets" bash -c '[[ "$0" =~ ^Relay\ listening\ on\ http://\[::1\]:[0-9]+$ ]]' "$LINE"
check "and answers there" bash -c '[ "$(curl -sS -g "$0/v1/health")" = "{\"ok\":true}" ]' "${LINE#Relay listening on }"
kill -TERM "$RELAY6_PID"
wait "$RELAY6_PID"
check "and stops on SIGTERM there too" test "$?" = "0"

ERDIGO_RELAY_GATEWAY_TOKEN="$GATEWAY_TOKEN" limited java -cp target/erdigo-send.jar examples/cloud-relay/Relay.java \
    --listen 127.0.0.1:0 >"$WORK/refused.log" 2>&1
check "the relay refuses to start without a source token" test "$?" = "1"
ERDIGO_RELAY_SOURCE_TOKENS="$SOURCE_TOKEN" ERDIGO_RELAY_GATEWAY_TOKEN="$SOURCE_TOKEN" limited java -cp target/erdigo-send.jar \
    examples/cloud-relay/Relay.java --listen 127.0.0.1:0 >"$WORK/refused.log" 2>&1
check "or with the same token for both" test "$?" = "1"
check "and does not show it" bash -c '! grep -q "$0" "$1"' "$SOURCE_TOKEN" "$WORK/refused.log"
ERDIGO_RELAY_SOURCE_TOKENS="$SOURCE_TOKEN" ERDIGO_RELAY_GATEWAY_TOKEN="$GATEWAY_TOKEN" limited java -cp target/erdigo-send.jar \
    examples/cloud-relay/Relay.java --listen 192.0.2.1:0 >"$WORK/refused.log" 2>&1
check "or on another address without --behind-proxy" test "$?" = "1"

echo
if [ "$FAILURES" -eq 0 ]; then
    echo "All checks passed."
else
    echo "$FAILURES check(s) failed."
    exit 1
fi
