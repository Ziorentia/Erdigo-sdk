#!/bin/bash
# Runs the cloud relay and gateway examples as built programs, with erdigo-send's mock receiver, on macOS or Linux.
#
#     dotnet build dotnet/Erdigo.sln && dotnet/tests/cloud-relay-smoke.sh
#
# Erdigo.Tests runs the same code inside one process. This checks the programs themselves: their command lines, exit
# codes and output, as docs/cloud-relay.md gives them. It needs curl and python3. Tokens are made here, kept in files
# of mode 0600, and never given to a program as an argument. Giving up on the app takes the real 30 seconds.
#
set -u

TESTS_DIR="$(cd "$(dirname "$0")" && pwd)"
DOTNET_DIR="$(cd "$TESTS_DIR/.." && pwd)"
CASES="$DOTNET_DIR/../docs/cloud-relay-cases.json"
CONFIGURATION="${CONFIGURATION:-Debug}"

SEND="$DOTNET_DIR/src/erdigo-send/bin/$CONFIGURATION/net8.0/erdigo-send"
RELAY="$DOTNET_DIR/examples/CloudRelay/bin/$CONFIGURATION/net8.0/erdigo-relay"
GATEWAY="$DOTNET_DIR/examples/CloudGateway/bin/$CONFIGURATION/net8.0/erdigo-gateway"
for program in "$SEND" "$RELAY" "$GATEWAY"; do
    [ -x "$program" ] || { echo "Build first: dotnet build dotnet/Erdigo.sln"; exit 1; }
done

# Stops a command that takes too long, so that it cannot hang this script.
limited() {
    perl -e 'alarm 120; exec { $ARGV[0] } @ARGV or exit 127' -- "$@"
}

mode_of() {
    if stat -f '%Lp' "$1" >/dev/null 2>&1; then stat -f '%Lp' "$1"; else stat -c '%a' "$1"; fi
}

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
umask 077

ok() { echo "ok    $1"; }
failed() {
    echo "FAIL  $1"
    FAILURES=$((FAILURES + 1))
}
check() {
    local name="$1"
    shift
    if "$@"; then ok "$name"; else failed "$name"; fi
}

new_token() { python3 -I -c 'import secrets; print(secrets.token_urlsafe(32))'; }
SOURCE_TOKEN="$(new_token)"
GATEWAY_TOKEN="$(new_token)"
WRONG_TOKEN="$(new_token)"
printf 'Authorization: Bearer %s\n' "$SOURCE_TOKEN" >"$WORK/source.header"
printf 'Authorization: Bearer %s\n' "$GATEWAY_TOKEN" >"$WORK/gateway.header"

# Runs a program and records its exit status and everything it printed.
run() {
    OUTPUT="$(limited "$@" 2>&1)"
    STATUS=$?
}

no_tokens_in_output() {
    [[ "$OUTPUT" != *"$SOURCE_TOKEN"* && "$OUTPUT" != *"$GATEWAY_TOKEN"* && "$OUTPUT" != *"$WRONG_TOKEN"* ]]
}

expect_status() {
    local name="$1" wanted="$2" text="$3"
    if [ "$STATUS" != "$wanted" ]; then
        failed "$name: exit $STATUS, expected $wanted: $OUTPUT"
    elif [[ "$OUTPUT" != *"$text"* ]]; then
        failed "$name: expected '$text', got: $OUTPUT"
    elif ! no_tokens_in_output; then
        failed "$name: a token was printed"
    else
        ok "$name"
    fi
}

# Starts the relay in the background, with its log in its own file. Sets RELAY_PID and RELAY_URL.
RELAY_STARTS=0
start_relay() {
    RELAY_STARTS=$((RELAY_STARTS + 1))
    local log="$WORK/relay-$RELAY_STARTS.log"
    ERDIGO_RELAY_SOURCE_TOKENS="$SOURCE_TOKEN" ERDIGO_RELAY_GATEWAY_TOKEN="$GATEWAY_TOKEN" \
        "$RELAY" --listen "$1" >"$log" 2>&1 &
    RELAY_PID=$!
    for _ in $(seq 1 100); do
        grep -q '^Relay listening on ' "$log" 2>/dev/null && break
        sleep 0.1
    done
    RELAY_URL="$(sed -n 's/^Relay listening on //p' "$log" | head -1)"
}

# Sends a body file to the relay with the source token. Sets HTTP_STATUS, and the answer is in $WORK/answer.
post() {
    HTTP_STATUS="$(curl -s -o "$WORK/answer" -w '%{http_code}' -H @"$WORK/source.header" \
        -H 'Content-Type: application/json' --data-binary @"$1" "$RELAY_URL/v1/ops")"
}

# How many operations the relay holds, fetched without acknowledging any.
held() {
    curl -s -H @"$WORK/gateway.header" "$RELAY_URL/v1/ops" | python3 -I -c 'import json, sys; print(len(json.load(sys.stdin)["ops"]))'
}

gateway_run() {
    local token="$1"
    shift
    ERDIGO_RELAY_TOKEN="$token" run "$GATEWAY" run "$@"
}

# The mock receiver, kept running with commands sent through a pipe.
mkfifo "$WORK/commands"
"$SEND" serve --mock --code-file "$WORK/code" <"$WORK/commands" >"$WORK/mock.log" 2>&1 &
MOCK_PID=$!
exec 3>"$WORK/commands"
for _ in $(seq 1 50); do
    grep -q 'Type .help' "$WORK/mock.log" 2>/dev/null && break
    sleep 0.1
done
MOCK_PORT="$(grep -oE 'port [0-9]+' "$WORK/mock.log" | awk '{print $2}')"
[ -n "$MOCK_PORT" ] || { echo "FAIL  the mock did not start"; cat "$WORK/mock.log"; exit 1; }
APP=(--host 127.0.0.1 --port "$MOCK_PORT")

# What the mock holds for its first device, one item per line.
items() {
    local before
    before="$(wc -l <"$WORK/mock.log")"
    echo "items 1" >&3
    sleep 0.5
    tail -n +"$((before + 1))" "$WORK/mock.log"
}

# The relay

ERDIGO_RELAY_SOURCE_TOKENS=short ERDIGO_RELAY_GATEWAY_TOKEN="$GATEWAY_TOKEN" run "$RELAY" --listen 127.0.0.1:0
expect_status "a short source token stops the relay" 1 "must be 32 to 256 characters"
ERDIGO_RELAY_SOURCE_TOKENS="$SOURCE_TOKEN" ERDIGO_RELAY_GATEWAY_TOKEN="$SOURCE_TOKEN" run "$RELAY" --listen 127.0.0.1:0
expect_status "one token for both roles stops the relay" 1 "must differ"
ERDIGO_RELAY_GATEWAY_TOKEN="$GATEWAY_TOKEN" run "$RELAY" --listen 127.0.0.1:0
expect_status "no source token stops the relay" 1 "ERDIGO_RELAY_SOURCE_TOKENS"
ERDIGO_RELAY_SOURCE_TOKENS="$SOURCE_TOKEN" ERDIGO_RELAY_GATEWAY_TOKEN="$GATEWAY_TOKEN" run "$RELAY" --listen 0.0.0.0:0
expect_status "a non-loopback address needs --behind-proxy" 1 "--behind-proxy"
ERDIGO_RELAY_SOURCE_TOKENS="$SOURCE_TOKEN" ERDIGO_RELAY_GATEWAY_TOKEN="$GATEWAY_TOKEN" run "$RELAY" --listen 127.0.0.1:0 --keep-hours 169
expect_status "--keep-hours over 168 is refused" 1 "--keep-hours"

start_relay 127.0.0.1:0
check "the relay says where it listens" bash -c '[[ "$0" =~ ^http://127\.0\.0\.1:[0-9]+$ ]]' "$RELAY_URL"
check "the health check needs no token" test "$(curl -s "$RELAY_URL/v1/health")" = '{"ok":true}'
check "fetching needs the gateway token" test "$(curl -s -o /dev/null -w '%{http_code}' -H @"$WORK/source.header" "$RELAY_URL/v1/ops")" = 401

# Pairing

run "$GATEWAY" pair --name "Cloud gateway" --code ABCDE-FGHIJ "${APP[@]}" --state-dir "$WORK/gateway"
expect_status "a pairing code is not taken as an argument" 1 "not accepted as arguments"
check "the code given as an argument is not repeated" bash -c '[[ "$0" != *ABCDE* ]]' "$OUTPUT"
chmod 644 "$WORK/code"
run "$GATEWAY" pair --name "Cloud gateway" "${APP[@]}" --code-file "$WORK/code" --state-dir "$WORK/gateway"
expect_status "a code file others can read is refused" 1 "mode 0600"
chmod 600 "$WORK/code"
run "$GATEWAY" pair --name "Cloud gateway" "${APP[@]}" --code-file "$WORK/code" --state-dir "$WORK/gateway"
expect_status "the gateway pairs through a code file holding the QR text" 0 "Paired. Allowed: pin, marker, note, photo, track."
check "the gateway's folder is private" test "$(mode_of "$WORK/gateway")" = 700

# Every kind of operation

PNG="$(python3 -I -c 'import json, sys; print(next(c["op"]["data"] for c in json.load(open(sys.argv[1]))["valid_ops"] if c["name"] == "photo, a 1x1 PNG"))' "$CASES")"
cat >"$WORK/every-kind.json" <<EOF
{"ops":[
 {"op":"pin","id":"drone-1","lat":37.5663,"lon":126.9779,"title":"Drone 1"},
 {"op":"marker","id":"gate","lat":37.5796,"lon":126.977,"title":"Gate","color":"red"},
 {"op":"marker","id":"old","lat":1,"lon":2},
 {"op":"note","id":"memo","lat":0,"lon":0,"text":"line one\nline two"},
 {"op":"photo","id":"view","lat":37.5663,"lon":126.9779,"type":"image/png","data":"$PNG","title":"View"},
 {"op":"track","id":"flight-7","title":"Flight 7","points":[{"lat":37.5663,"lon":126.9779,"ts":1700000000000,"alt":120},{"lat":37.5664,"lon":126.9779,"ts":1700000001000},{"lat":37.5665,"lon":126.978}]},
 {"op":"finish_track","id":"flight-7"},
 {"op":"move","id":"drone-1","lat":37.57,"lon":126.98},
 {"op":"delete","id":"old"}
]}
EOF
post "$WORK/every-kind.json"
check "the relay takes nine operations" test "$HTTP_STATUS $(cat "$WORK/answer")" = '200 {"accepted":9,"last_seq":9}'

gateway_run "$GATEWAY_TOKEN" --relay "$RELAY_URL" "${APP[@]}" --once --state-dir "$WORK/gateway"
expect_status "run --once applies them all" 0 "Done with 9 operations, up to 9"
check "it prints that line and nothing else" test "$OUTPUT" = "Done with 9 operations, up to 9"
check "its place is kept in a private file" test "$(mode_of "$WORK/gateway/relay-state.json")" = 600
check "the relay forgot what the gateway finished" test "$(held)" = 0

ITEMS="$(items)"
for wanted in \
    "pin drone-1 at 37.57,126.98 title=Drone 1 points=0" \
    "marker gate at 37.5796,126.977 title=Gate points=0" \
    "note memo at 0.0,0.0 title=- points=0" \
    "photo view at 37.5663,126.9779 title=View points=0" \
    "track flight-7 at -,- title=Flight 7 points=3"; do
    check "the app holds: $wanted" grep -qF "$wanted" <<<"$ITEMS"
done
check "the deleted marker is gone" bash -c '! grep -q "marker old" <<<"$0"' "$ITEMS"

gateway_run "$GATEWAY_TOKEN" --relay "$RELAY_URL" "${APP[@]}" --once --state-dir "$WORK/gateway"
expect_status "running again exits 0" 0 ""
check "running again applies nothing" test -z "$OUTPUT"
check "the track still has three points" grep -qF "track flight-7 at -,- title=Flight 7 points=3" <<<"$(items)"

# A gateway allowed fewer kinds. It runs alone against this relay, after the first one.

echo "code" >&3
sleep 0.5
run "$GATEWAY" pair --name "Pins only" --kinds pin "${APP[@]}" --code-file "$WORK/code" --state-dir "$WORK/pins-only"
expect_status "a second gateway pairs for pins only" 0 "Allowed: pin."
printf '{"ops":[{"op":"marker","id":"m1","lat":1,"lon":2},{"op":"pin","id":"p1","lat":1,"lon":2}]}' >"$WORK/two.json"
post "$WORK/two.json"
gateway_run "$GATEWAY_TOKEN" --relay "$RELAY_URL" "${APP[@]}" --once --state-dir "$WORK/pins-only"
expect_status "a kind that was not allowed is refused and skipped" 0 "Refused operation 10 (marker): not_permitted"
check "the rest goes on" grep -qxF "Done with 2 operations, up to 11 (1 refused)" <<<"$OUTPUT"
gateway_run "$GATEWAY_TOKEN" --relay "${RELAY_URL/127.0.0.1/LOCALHOST}" "${APP[@]}" --once --state-dir "$WORK/pins-only"
expect_status "plain http:// is taken for LOCALHOST in capitals" 0 ""
check "and it finds nothing left" test -z "$OUTPUT"

# Mistakes

gateway_run "$GATEWAY_TOKEN" --relay http://relay.example.com "${APP[@]}" --once --state-dir "$WORK/gateway"
expect_status "plain http:// to another computer is refused" 1 "must start with https://"
gateway_run "" --relay "$RELAY_URL" "${APP[@]}" --once --state-dir "$WORK/gateway"
expect_status "no token is a mistake in the environment" 1 "ERDIGO_RELAY_TOKEN"
gateway_run "$WRONG_TOKEN" --relay "$RELAY_URL" "${APP[@]}" --once --state-dir "$WORK/gateway"
expect_status "a wrong token exits 2" 2 "The relay refused the token."
gateway_run "$GATEWAY_TOKEN" --relay "$RELAY_URL" "${APP[@]}" --once --state-dir "$WORK/unpaired"
expect_status "no pairing exits 1" 1 "no pairing"

# One run at a time on a profile. The first runs until SIGTERM.

ERDIGO_RELAY_TOKEN="$GATEWAY_TOKEN" "$GATEWAY" run --relay "$RELAY_URL" "${APP[@]}" --state-dir "$WORK/gateway" >"$WORK/first-run.log" 2>&1 &
GATEWAY_PID=$!
for _ in $(seq 1 100); do
    grep -q '^Waiting for operations from the relay' "$WORK/first-run.log" 2>/dev/null && break
    sleep 0.1
done
gateway_run "$GATEWAY_TOKEN" --relay "$RELAY_URL" "${APP[@]}" --once --state-dir "$WORK/gateway"
expect_status "a second run on a profile in use exits 1" 1 "another 'run' is using this profile"
check "it says so in one line" test "$(printf '%s\n' "$OUTPUT" | wc -l | tr -d ' ')" = 1
kill -TERM "$GATEWAY_PID"
for _ in $(seq 1 150); do
    kill -0 "$GATEWAY_PID" 2>/dev/null || break
    sleep 0.1
done
kill -0 "$GATEWAY_PID" 2>/dev/null && kill -KILL "$GATEWAY_PID"
wait "$GATEWAY_PID"
GATEWAY_STATUS=$?
GATEWAY_PID=""
check "the first run stops cleanly on SIGTERM" test "$GATEWAY_STATUS" = 0
check "and says so" test "$(tail -n 1 "$WORK/first-run.log")" = "Stopped."

# The relay stops on SIGTERM, and starts again with a new numbering at the same address.

kill -TERM "$RELAY_PID"
wait "$RELAY_PID"
RELAY_STATUS=$?
RELAY_PID=""
check "the relay stops cleanly on SIGTERM" test "$RELAY_STATUS" = 0
OLD_URL="$RELAY_URL"
start_relay "${OLD_URL#http://}"
check "the relay starts again at the same address" test "$RELAY_URL" = "$OLD_URL"
printf '{"ops":[{"op":"pin","id":"after-restart","lat":1,"lon":2}]}' >"$WORK/one.json"
post "$WORK/one.json"
gateway_run "$GATEWAY_TOKEN" --relay "$RELAY_URL" "${APP[@]}" --once --state-dir "$WORK/gateway"
expect_status "the gateway follows the new numbering" 0 "The relay started a new numbering; starting from its oldest operation."
check "and applies what it holds" grep -qxF "Done with 1 operation, up to 1" <<<"$OUTPUT"

# Without the app

echo "quit" >&3
wait "$MOCK_PID" 2>/dev/null
MOCK_PID=""
post "$WORK/two.json"
gateway_run "$GATEWAY_TOKEN" --relay "$RELAY_URL" "${APP[@]}" --once --state-dir "$WORK/gateway"
expect_status "without the app, run --once gives up with exit 3" 3 "The Erdigo app could not be reached. The operations stay on the relay."
check "it said once that it would try again" test "$(grep -cxF "No answer from the Erdigo app; trying again." <<<"$OUTPUT")" = 1
check "the operations stay on the relay" test "$(held)" = 2

if grep -qF -e "$SOURCE_TOKEN" -e "$GATEWAY_TOKEN" -e "$WRONG_TOKEN" -e "drone-1" -e "37.5663" -e "Drone 1" "$WORK"/relay-*.log; then
    failed "the relay's log holds a token or what an operation held"
else
    ok "the relay's log holds no tokens and nothing operations held"
fi

if [ "$FAILURES" -ne 0 ]; then
    echo "$FAILURES check(s) failed"
    exit 1
fi
echo "All cloud relay checks passed."
