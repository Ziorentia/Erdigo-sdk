#!/bin/bash
# Runs the PHP cloud relay and gateway (examples/cloud-relay) end to end: the relay under PHP's built-in server, and the
# gateway paired with the mock receiver of another edition, over real TCP on this computer. It sends every kind of
# operation through the relay and checks what the mock holds.
#
#     MOCK=dotnet/src/erdigo-send/bin/Release/net8.0/erdigo-send php/tests/cloud-relay-smoke.sh
#     MOCK=.build/debug/erdigo-send php/tests/cloud-relay-smoke.sh
#
# MOCK is a built erdigo-send of the .NET or the Swift edition. Tokens are made here and given to the programs only in
# their environment, never in a command line, where other users of the computer could see them. Ports are picked by
# the system, on 127.0.0.1. A few checks wait about 30 seconds on purpose, for the gateway to give up; they run side by
# side.
#
set -u

TESTS_DIR="$(cd "$(dirname "$0")" && pwd)"
PHP_DIR="$(cd "$TESTS_DIR/.." && pwd)"
REPO_DIR="$(cd "$PHP_DIR/.." && pwd)"
PHP_BIN="$(command -v "${PHP:-php}")"
RELAY="$PHP_DIR/examples/cloud-relay/relay.php"
GATEWAY="$PHP_DIR/examples/cloud-relay/gateway.php"
HTTP="$TESTS_DIR/Support/cloud-relay-http.php"
CASES="$REPO_DIR/docs/cloud-relay-cases.json"

[ -n "${MOCK:-}" ] || { echo "Set MOCK to a built erdigo-send of the .NET or Swift edition."; exit 1; }
case "$MOCK" in /*) ;; *) MOCK="$PWD/$MOCK" ;; esac
[ -x "$MOCK" ] || { echo "MOCK is not an executable: $MOCK"; exit 1; }

# Runs a command that is stopped if it takes too long, so that it cannot hang this script. An alarm survives exec.
limited() {
    perl -e 'alarm 120; exec { $ARGV[0] } @ARGV or exit 127' -- "$@"
}

mode_of() {
    if stat -f '%Lp' "$1" >/dev/null 2>&1; then stat -f '%Lp' "$1"; else stat -c '%a' "$1"; fi
}

WORK="$(mktemp -d)"
FAILURES=0
OUTPUTS="$WORK/gateway-output.log"
: >"$OUTPUTS"

cleanup() {
    for file in "$WORK"/*.pid; do
        [ -f "$file" ] && kill "$(cat "$file")" 2>/dev/null
    done
    rm -rf "$WORK"
}

# Starts a program in the background, outside this shell's jobs, and keeps its process id in NAME.pid:
# spawn NAME INPUT OUTPUT COMMAND...
spawn() {
    local name="$1" input="$2" output="$3"
    shift 3
    ( "$@" <"$input" >>"$output" 2>&1 & echo $! >"$WORK/$name.pid" )
}

stop() {
    local pid
    pid="$(cat "$WORK/$1.pid")"
    kill "$pid" 2>/dev/null
    for _ in $(seq 1 100); do
        kill -0 "$pid" 2>/dev/null || break
        sleep 0.1
    done
    rm -f "$WORK/$1.pid"
}
trap cleanup EXIT

# Nothing here may wait for a person, whether or not this runs in a terminal.
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

failed() {
    echo "FAIL  $1"
    FAILURES=$((FAILURES + 1))
}

token() {
    "$PHP_BIN" -r 'echo rtrim(strtr(base64_encode(random_bytes(32)), "+/", "-_"), "=");'
}

free_port() {
    "$PHP_BIN" -r '$s = stream_socket_server("tcp://127.0.0.1:0"); echo substr(strrchr(stream_socket_get_name($s, false), ":"), 1);'
}

SOURCE_TOKEN="$(token)"
GATEWAY_TOKEN="$(token)"
# Both tokens, one per line, for grep -f, so that looking for them does not put them in a command line either.
(umask 077 && printf '%s\n%s\n' "$SOURCE_TOKEN" "$GATEWAY_TOKEN" >"$WORK/tokens")

# Starts a relay under PHP's built-in server, with display_errors off as the relay needs:
# start_relay NAME PORT DATA_DIR [VARIABLE=VALUE ...]
start_relay() {
    local name="$1" port="$2" data="$3"
    shift 3
    ERDIGO_RELAY_SOURCE_TOKENS="$SOURCE_TOKEN" ERDIGO_RELAY_GATEWAY_TOKEN="$GATEWAY_TOKEN" ERDIGO_RELAY_DATA_DIR="$data" \
        spawn "$name" /dev/null "$WORK/$name.log" env "$@" "$PHP_BIN" -d display_errors=0 -S "127.0.0.1:$port" "$RELAY"
    for _ in $(seq 1 100); do
        [ "$(relay_get "$port" '' health)" = "200 {\"ok\":true}" ] && return 0
        sleep 0.1
    done
    echo "FAIL  the relay $name did not start"
    cat "$WORK/$name.log"
    exit 1
}

# Sends the JSON given as source: relay_post PORT JSON. Prints the status and the answer.
relay_post() {
    printf '%s' "$2" | TEST_RELAY_TOKEN="$SOURCE_TOKEN" limited "$PHP_BIN" "$HTTP" post "$1"
}

# Fetches as the gateway, or asks for health: relay_get PORT QUERY [health]
relay_get() {
    if [ "${3:-}" = "health" ]; then
        "$PHP_BIN" -r '$s = @stream_socket_client("tcp://127.0.0.1:" . $argv[1], $e, $t, 1); if (!$s) { exit; }
            fwrite($s, "GET /v1/health HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n"); $a = stream_get_contents($s);
            preg_match("#^HTTP/1\\.[01] ([0-9]{3})#", $a, $m); echo $m[1] ?? "0", " ", substr($a, strpos($a, "\r\n\r\n") + 4);' "$1"
        return
    fi
    TEST_RELAY_TOKEN="$GATEWAY_TOKEN" limited "$PHP_BIN" "$HTTP" get "$1" "$2"
}

# Runs the gateway with the gateway token: gateway OUTPUT_NAME ARGUMENTS... Sets STATUS and OUTPUT.
gateway() {
    local name="$1"
    shift
    OUTPUT="$(ERDIGO_RELAY_TOKEN="$GATEWAY_TOKEN" limited "$PHP_BIN" "$GATEWAY" "$@" 2>&1)"
    STATUS=$?
    printf '%s\n' "$OUTPUT" >>"$OUTPUTS"
    printf '%s\n' "$OUTPUT" >"$WORK/$name.out"
}

has() {
    [[ "$OUTPUT" == *"$1"* ]]
}

# The mock receiver, with its standard input kept open for commands.
mkfifo "$WORK/commands"
exec 3<>"$WORK/commands"
spawn mock "$WORK/commands" "$WORK/mock.log" "$MOCK" serve --mock --code-file "$WORK/code"
for _ in $(seq 1 300); do
    grep -q 'Type .help' "$WORK/mock.log" 2>/dev/null && break
    sleep 0.1
done
PORT="$(grep -oE 'port [0-9]+' "$WORK/mock.log" | head -1 | awk '{print $2}')"
[ -n "$PORT" ] || { echo "FAIL  the mock did not start"; cat "$WORK/mock.log"; exit 1; }
echo "Mock receiver on port $PORT"

# Opens a new pairing window, so that $WORK/code holds a fresh QR text.
fresh_code() {
    local count
    count="$(grep -c 'Pairing text written' "$WORK/mock.log")"
    echo "code" >&3
    for _ in $(seq 1 50); do
        [ "$(grep -c 'Pairing text written' "$WORK/mock.log")" -gt "$count" ] && break
        sleep 0.1
    done
}

# Prints what the mock answers to a command. Its one-line answer to 'help', sent after it, marks the end: ask COMMAND
ask() {
    local before
    before="$(wc -l <"$WORK/mock.log")"
    echo "$1" >&3
    echo "help" >&3
    for _ in $(seq 1 50); do
        tail -n +"$((before + 1))" "$WORK/mock.log" | grep -q '^code | devices | items N' && break
        sleep 0.1
    done
    tail -n +"$((before + 1))" "$WORK/mock.log" | sed '/^code | devices | items N/,$d'
}

# Prints the items the mock holds for the device of that name: items NAME
items() {
    local number
    number="$(ask devices | sed -nE "s/^([0-9]+)\. '$1' .*/\1/p" | head -1)"
    ask "items ${number:-0}" | grep -E '^(pin|marker|note|photo|track) '
}

requests() {
    grep -cE '^\[mock\] #[0-9]+ ' "$WORK/mock.log"
}

RELAY_PORT="$(free_port)"
start_relay relay_a "$RELAY_PORT" "$WORK/relay-a"
RELAY_URL="http://127.0.0.1:$RELAY_PORT"
STATE=(--state-dir "$WORK/gateway")
ADDRESS=(--host 127.0.0.1 --port "$PORT")

# Pairing, with the QR text in a file only this user can read.
gateway pair pair "${STATE[@]}" "${ADDRESS[@]}" --name "Cloud gateway" --code-file "$WORK/code"
check "the gateway pairs with the QR text" bash -c '[ "$0" = 0 ] && [[ "$1" == *"Paired as"* ]]' "$STATUS" "$OUTPUT"
check "the mock paired it for every kind" grep -q '^\[mock\] paired .*pin,marker,note,photo,track' "$WORK/mock.log"
gateway pair-again pair "${STATE[@]}" "${ADDRESS[@]}" --name "Cloud gateway" --code-file "$WORK/code"
check "a second pairing in the same profile is refused" bash -c '[ "$0" = 1 ] && [[ "$1" == *"already holds a pairing"* ]]' "$STATUS" "$OUTPUT"

# Every kind of operation, through the relay.
PNG="$("$PHP_BIN" -r '$c = json_decode(file_get_contents($argv[1])); foreach ($c->valid_ops as $v) { if ($v->name === "photo, a 1x1 PNG") { echo $v->op->data; } }' "$CASES")"
check "the 1x1 PNG is in the cases file" test -n "$PNG"
FIRST="$(relay_post "$RELAY_PORT" '{"ops":[
 {"op":"pin","id":"city-hall","lat":37.5663,"lon":126.9779,"title":"Seoul City Hall"},
 {"op":"marker","id":"gate","lat":37.5796,"lon":126.977,"title":"Gate","color":"blue"},
 {"op":"note","id":"memo","lat":37.5664,"lon":126.978,"text":"line one\nline\ttwo"},
 {"op":"photo","id":"view","lat":37.5662,"lon":126.9778,"type":"image/png","data":"'"$PNG"'","title":"View"}]}')"
SECOND="$(relay_post "$RELAY_PORT" '{"ops":[
 {"op":"track","id":"walk","title":"Walk","points":[{"lat":37.5663,"lon":126.9779,"ts":1700000000000,"alt":38.5},{"lat":37.5665,"lon":126.978,"ts":1700000001000}]},
 {"op":"track","id":"walk","points":[{"lat":37.5667,"lon":126.9781}]},
 {"op":"finish_track","id":"walk"},
 {"op":"pin","id":"temporary","lat":37.5,"lon":126.5},
 {"op":"delete","id":"temporary"},
 {"op":"move","id":"city-hall","lat":37.5665,"lon":126.9785}]}')"
check "the relay takes both batches" test "$FIRST $SECOND" = '200 {"accepted":4,"last_seq":4} 200 {"accepted":6,"last_seq":10}'

gateway first run "${STATE[@]}" "${ADDRESS[@]}" --relay "$RELAY_URL" --once
check "run --once exits 0" test "$STATUS" = 0
check "it reports what it did" has "Done with 10 operations, up to 10 (0 refused)"
check "the place is kept in a private file" test "$(mode_of "$WORK/gateway/relay-state.json")" = 600
check "the place is the last operation" grep -q '"after":10' "$WORK/gateway/relay-state.json"
ITEMS="$(items "Cloud gateway")"
check "the mock holds the moved pin" grep -q '^pin city-hall at 37.5665,126.9785 title=Seoul City Hall' <<<"$ITEMS"
check "the mock holds the marker" grep -q '^marker gate at 37.5796,126.977 title=Gate' <<<"$ITEMS"
check "the mock holds the note" grep -q '^note memo at 37.5664,126.978' <<<"$ITEMS"
check "the mock holds the photo" grep -q '^photo view at 37.5662,126.9778 title=View' <<<"$ITEMS"
check "the mock holds the track with three points" grep -q '^track walk .*title=Walk points=3' <<<"$ITEMS"
check "the deleted pin is gone" bash -c '! grep -q "^pin temporary " <<<"$0"' "$ITEMS"
check "the photo was stored" grep -q 'photo stored' "$WORK/mock.log"
check "the track was finished" grep -q 'track finish' "$WORK/mock.log"
check "the relay holds nothing more" bash -c '[[ "$0" == *"\"ops\":[]"* ]]' "$(relay_get "$RELAY_PORT" '')"

BEFORE="$(requests)"
gateway again run "${STATE[@]}" "${ADDRESS[@]}" --relay "$RELAY_URL" --once
check "running again exits 0" test "$STATUS" = 0
check "running again applies nothing" bash -c '[ "$0" = "$1" ] && [[ "$2" != *"Done with"* ]]' "$BEFORE" "$(requests)" "$OUTPUT"

# A gateway allowed pins only refuses the rest for good, and moves on.
fresh_code
gateway pair-pins pair --state-dir "$WORK/gateway-pins" "${ADDRESS[@]}" --name "Pins only" --kinds pin --code-file "$WORK/code"
check "a gateway pairs for pins only" bash -c '[ "$0" = 0 ] && [[ "$1" == *"Allowed: pin."* ]]' "$STATUS" "$OUTPUT"
PINS_PORT="$(free_port)"
start_relay relay_b "$PINS_PORT" "$WORK/relay-b"
relay_post "$PINS_PORT" '{"ops":[{"op":"pin","id":"allowed-1","lat":37.57,"lon":126.98},
 {"op":"marker","id":"refused-1","lat":37.57,"lon":126.98},{"op":"note","id":"refused-2","lat":37.57,"lon":126.98,"text":"no"},
 {"op":"pin","id":"allowed-2","lat":37.571,"lon":126.981}]}' >/dev/null
gateway pins run --state-dir "$WORK/gateway-pins" "${ADDRESS[@]}" --relay "http://127.0.0.1:$PINS_PORT" --once
check "kinds not allowed: exits 0" test "$STATUS" = 0
check "the marker is refused with not_permitted" has "Refused operation 2 (marker): not_permitted"
check "the note is refused with not_permitted" has "Refused operation 3 (note): not_permitted"
check "and the gateway moves on" has "Done with 4 operations, up to 4 (2 refused)"
ITEMS="$(items "Pins only")"
check "the mock holds both pins" bash -c 'grep -q "^pin allowed-1 " <<<"$0" && grep -q "^pin allowed-2 " <<<"$0"' "$ITEMS"
check "the relay holds nothing more for it" bash -c '[[ "$0" == *"\"ops\":[]"* ]]' "$(relay_get "$PINS_PORT" '')"

# A code typed at the keyboard: the person compares the key, as with erdigo-send pair. Without a terminal, a bare code
# is refused before the receiver is contacted.
if command -v python3 >/dev/null 2>&1; then
    HELPER="$REPO_DIR/swift/Tests/typed-ahead-check.py"
    fresh_code
    TYPED="$(sed -E 's/.*code=([^&]*).*/\1/' "$WORK/code")"
    OUTPUT="$(python3 -I "$HELPER" "$PORT" --type-code "$TYPED" --answer "yes"$'\n' -- "$PHP_BIN" "$GATEWAY" pair \
        --host 127.0.0.1 --port PROXY_PORT --name Typed --kinds pin --state-dir "$WORK/gateway-typed" 2>&1)"
    check "a code typed at the keyboard pairs once the key is confirmed" \
        bash -c '[[ "$0" == *"exit status 0"* && "$0" == *"type yes"* && "$0" == *"Paired as"* ]]' "$OUTPUT"
    check "the code typed is not shown" bash -c '[[ "$0" != *"$1"* ]]' "$OUTPUT" "$TYPED"
    fresh_code
    TYPED="$(sed -E 's/.*code=([^&]*).*/\1/' "$WORK/code")"
    printf '%s\n' "$TYPED" >"$WORK/typed-code"
    chmod 600 "$WORK/typed-code"
    OUTPUT="$(python3 -I "$HELPER" "$PORT" --pipe "yes"$'\n' -- "$PHP_BIN" "$GATEWAY" pair --host 127.0.0.1 --port PROXY_PORT \
        --name Piped --kinds pin --state-dir "$WORK/gateway-piped" --code-file "$WORK/typed-code" 2>&1)"
    check "a bare code without a terminal is refused" bash -c '[[ "$0" == *"not a terminal"* && "$0" == *"connections 0"* ]]' "$OUTPUT"
else
    echo "skip  a code typed at the keyboard (python3 is needed)"
fi

# The checks that wait for the gateway to give up run side by side.
CLOSED_PORT="$(free_port)"
relay_post "$RELAY_PORT" '{"ops":[{"op":"pin","id":"late","lat":37.58,"lon":126.99}]}' >/dev/null
STARTED=$SECONDS
( gateway down run "${STATE[@]}" --host 127.0.0.1 --port "$CLOSED_PORT" --relay "$RELAY_URL" --once
  echo "$STATUS" >"$WORK/down.status"; echo $((SECONDS - STARTED)) >"$WORK/down.seconds" ) &
WAITING=($!)
( gateway no-relay run --state-dir "$WORK/gateway-pins" "${ADDRESS[@]}" --relay "http://127.0.0.1:$CLOSED_PORT" --once; echo "$STATUS" >"$WORK/no-relay.status" ) &
WAITING+=($!)

# HTTPS through a front with a self-signed certificate: trusted, it works; not trusted, the gateway does not connect.
TLS_PORT=""
if "$PHP_BIN" -m | grep -qx openssl; then
    fresh_code
    gateway pair-tls pair --state-dir "$WORK/gateway-tls" "${ADDRESS[@]}" --name "TLS gateway" --kinds pin --code-file "$WORK/code"
    mkdir "$WORK/tls"
    spawn tls /dev/null "$WORK/tls.log" "$PHP_BIN" "$TESTS_DIR/Support/tls-proxy.php" "$WORK/tls" "$PINS_PORT"
    for _ in $(seq 1 100); do
        TLS_PORT="$(sed -n 's/^port //p' "$WORK/tls.log")"
        [ -n "$TLS_PORT" ] && break
        sleep 0.1
    done
    ( gateway untrusted run --state-dir "$WORK/gateway-tls" "${ADDRESS[@]}" --relay "https://localhost:$TLS_PORT" --once; echo "$STATUS" >"$WORK/untrusted.status" ) &
    WAITING+=($!)
fi

wait "${WAITING[@]}"
ELAPSED="$(cat "$WORK/down.seconds")"
OUTPUT="$(cat "$WORK/down.out")"
check "with the app down, --once exits 3" test "$(cat "$WORK/down.status")" = 3
check "it says it is trying again" has "No answer from the Erdigo app; trying again."
check "and then that the operations stay on the relay" has "The Erdigo app could not be reached. The operations stay on the relay."
check "after about 30 seconds" test "$ELAPSED" -ge 28 -a "$ELAPSED" -le 90
check "the operation is still on the relay" bash -c '[[ "$0" == *"\"id\":\"late\""* ]]' "$(relay_get "$RELAY_PORT" '')"
gateway late run "${STATE[@]}" "${ADDRESS[@]}" --relay "$RELAY_URL" --once
check "with the app back, it is written" bash -c '[ "$0" = 0 ] && [[ "$1" == *"Done with 1 operation, up to 11 (0 refused)"* ]]' "$STATUS" "$OUTPUT"
check "the mock holds it" bash -c 'grep -q "^pin late " <<<"$0"' "$(items "Cloud gateway")"

OUTPUT="$(cat "$WORK/no-relay.out")"
check "with the relay down, --once exits 3" test "$(cat "$WORK/no-relay.status")" = 3
check "it says the relay could not be reached" has "The relay could not be reached; trying again in"

if [ -n "$TLS_PORT" ]; then
    OUTPUT="$(cat "$WORK/untrusted.out")"
    check "a relay certificate that is not trusted is not accepted" test "$(cat "$WORK/untrusted.status")" = 3
    check "and the gateway says so" has "The relay's certificate could not be verified"
    relay_post "$PINS_PORT" '{"ops":[{"op":"pin","id":"over-https","lat":37.572,"lon":126.982}]}' >/dev/null
    OUTPUT="$(ERDIGO_RELAY_TOKEN="$GATEWAY_TOKEN" limited "$PHP_BIN" -d "openssl.cafile=$WORK/tls/cert.pem" "$GATEWAY" run \
        --state-dir "$WORK/gateway-tls" "${ADDRESS[@]}" --relay "https://localhost:$TLS_PORT" --once 2>&1)"
    STATUS=$?
    printf '%s\n' "$OUTPUT" >>"$OUTPUTS"
    check "over HTTPS with a trusted certificate, the gateway fetches and writes" \
        bash -c '[ "$0" = 0 ] && [[ "$1" == *"Done with 1 operation, up to 5 (0 refused)"* ]]' "$STATUS" "$OUTPUT"
    check "the mock holds what came over HTTPS" bash -c 'grep -q "^pin over-https " <<<"$0"' "$(items "TLS gateway")"
else
    echo "skip  HTTPS (PHP's openssl extension is not loaded)"
fi

# A relay that starts a new numbering, and operations that expired before they were fetched. The relay's clock is
# moved forward with a file, which only the tests use.
stop relay_a
echo 0 >"$WORK/clock"
start_relay relay_a "$RELAY_PORT" "$WORK/relay-a-again" ERDIGO_RELAY_TEST_CLOCK_FILE="$WORK/clock"
relay_post "$RELAY_PORT" '{"ops":[{"op":"pin","id":"expired","lat":37.59,"lon":127}]}' >/dev/null
echo $((25 * 3600 * 1000)) >"$WORK/clock"
relay_post "$RELAY_PORT" '{"ops":[{"op":"pin","id":"after-restart","lat":37.591,"lon":127.001}]}' >/dev/null
gateway restart run "${STATE[@]}" "${ADDRESS[@]}" --relay "$RELAY_URL" --once
check "after the relay's restart, exits 0" test "$STATUS" = 0
check "it says the relay started a new numbering" has "The relay started a new numbering; starting from its oldest operation."
check "it says some operations expired" has "Some operations expired on the relay before they were fetched."
check "it writes what is left" has "Done with 1 operation, up to 2 (0 refused)"
ITEMS="$(items "Cloud gateway")"
check "the mock holds the operation sent after the restart" grep -q '^pin after-restart ' <<<"$ITEMS"
check "and not the one that expired" bash -c '! grep -q "^pin expired " <<<"$0"' "$ITEMS"

# Stopping: SIGTERM ends a running gateway with code 0, without waiting for the relay.
if "$PHP_BIN" -m | grep -qx pcntl; then
    ERDIGO_RELAY_TOKEN="$GATEWAY_TOKEN" "$PHP_BIN" "$GATEWAY" run "${STATE[@]}" "${ADDRESS[@]}" --relay "$RELAY_URL" >"$WORK/running.out" 2>&1 &
    RUNNING=$!
    relay_post "$RELAY_PORT" '{"ops":[{"op":"pin","id":"while-running","lat":37.592,"lon":127.002}]}' >/dev/null
    # The relay holds an empty fetch for up to 10 seconds, and this built-in server answers one request at a time.
    for _ in $(seq 1 250); do
        grep -q 'Done with' "$WORK/running.out" && break
        sleep 0.1
    done
    check "a running gateway writes what arrives" grep -q 'Done with 1 operation, up to 3 (0 refused)' "$WORK/running.out"
    gateway second run "${STATE[@]}" "${ADDRESS[@]}" --relay "$RELAY_URL" --once
    check "a second gateway on the same state folder is refused" \
        bash -c '[ "$0" = 1 ] && [[ "$1" == *"another gateway is running"* ]]' "$STATUS" "$OUTPUT"
    sleep 1
    STOPPED=$SECONDS
    kill -TERM "$RUNNING"
    wait "$RUNNING"
    STOP_STATUS=$?
    check "SIGTERM stops it with code 0" test "$STOP_STATUS" = 0
    check "at once" test $((SECONDS - STOPPED)) -le 3
    cat "$WORK/running.out" >>"$OUTPUTS"
else
    echo "skip  stopping with SIGTERM (PHP's pcntl extension is not loaded)"
fi

# Mistakes in the command line or the environment, and a token the relay refuses.
gateway plain-http run "${STATE[@]}" "${ADDRESS[@]}" --relay "http://relay.example.com" --once
check "plain http:// to another computer exits 1" bash -c '[ "$0" = 1 ] && [[ "$1" == *"https://"* ]]' "$STATUS" "$OUTPUT"
for url in "ftp://127.0.0.1/" "https://user:secret@relay.example.com" "https://relay.example.com/?a=1" "https://relay.example.com/#a" "relay.example.com"; do
    gateway bad-url run "${STATE[@]}" "${ADDRESS[@]}" --relay "$url" --once
    check "the relay address $url exits 1" test "$STATUS" = 1
done
OUTPUT="$(unset ERDIGO_RELAY_TOKEN; limited "$PHP_BIN" "$GATEWAY" run "${STATE[@]}" "${ADDRESS[@]}" --relay "$RELAY_URL" --once 2>&1)"
check "no token exits 1" bash -c '[ "$0" = 1 ] && [[ "$1" == *"ERDIGO_RELAY_TOKEN"* ]]' "$?" "$OUTPUT"
OUTPUT="$(ERDIGO_RELAY_TOKEN=short limited "$PHP_BIN" "$GATEWAY" run "${STATE[@]}" "${ADDRESS[@]}" --relay "$RELAY_URL" --once 2>&1)"
check "a token of the wrong form exits 1" test "$?" = 1
ARGUMENT_TOKEN="argument-token-$RANDOM-0123456789abcdefghij"
gateway token-argument run "${STATE[@]}" "${ADDRESS[@]}" --relay "$RELAY_URL" --once --token "$ARGUMENT_TOKEN"
check "a token given as an argument is refused" bash -c '[ "$0" = 1 ] && [[ "$1" == *"not accepted as an argument"* ]]' "$STATUS" "$OUTPUT"
check "and not repeated" bash -c '[[ "$0" != *"$1"* ]]' "$OUTPUT" "$ARGUMENT_TOKEN"
OUTPUT="$(ERDIGO_RELAY_TOKEN="$SOURCE_TOKEN" limited "$PHP_BIN" "$GATEWAY" run "${STATE[@]}" "${ADDRESS[@]}" --relay "$RELAY_URL" --once 2>&1)"
check "a token the relay refuses exits 2" bash -c '[ "$0" = 2 ] && [[ "$1" == *"The relay refused the token."* ]]' "$?" "$OUTPUT"
printf '%s\n' "$OUTPUT" >>"$OUTPUTS"
gateway wrong-path run "${STATE[@]}" "${ADDRESS[@]}" --relay "$RELAY_URL/elsewhere" --once
check "an address that is not a relay exits 2" bash -c '[ "$0" = 2 ] && [[ "$1" == *"does not follow"* ]]' "$STATUS" "$OUTPUT"
gateway unpaired run --state-dir "$WORK/nobody" "${ADDRESS[@]}" --relay "$RELAY_URL" --once
check "a profile without a pairing exits 1" bash -c '[ "$0" = 1 ] && [[ "$1" == *"no pairing"* ]]' "$STATUS" "$OUTPUT"
gateway unknown run "${STATE[@]}" "${ADDRESS[@]}" --relay "$RELAY_URL" --once --verbose
check "an unknown option exits 1" test "$STATUS" = 1
printf 'not a code\n' >"$WORK/open-code"
chmod 644 "$WORK/open-code"
gateway open-code pair --state-dir "$WORK/gateway-open" "${ADDRESS[@]}" --name Open --code-file "$WORK/open-code"
check "a code file others can read is refused" bash -c '[ "$0" = 1 ] && [[ "$1" == *"mode 0600"* ]]' "$STATUS" "$OUTPUT"

# What the programs print and log.
check "the gateway prints no token" bash -c '! grep -qF -f "$1" "$0"' "$OUTPUTS" "$WORK/tokens"
check "the gateway prints no ids, titles or text" bash -c '! grep -qE "city-hall|Seoul City Hall|line one|allowed-1|over-https" "$0"' "$OUTPUTS"
check "the gateway prints no coordinates" bash -c '! grep -qE "37\.5|126\.9" "$0"' "$OUTPUTS"
cat "$WORK"/relay_*.log >"$WORK/relays.log"
check "the relays log no token" bash -c '! grep -qF -f "$1" "$0"' "$WORK/relays.log" "$WORK/tokens"
check "the relays log no ids, titles or text" bash -c '! grep -qE "city-hall|Seoul City Hall|line one|37\.5" "$0"' "$WORK/relays.log"
check "the relays log each request" grep -q 'erdigo-relay: POST /v1/ops 200 accepted=4' "$WORK/relays.log"

exec 3>&-
if [ "$FAILURES" -ne 0 ]; then
    echo
    echo "$FAILURES check(s) failed. The mock's log:"
    cat "$WORK/mock.log"
    echo "The gateway's output:"
    cat "$OUTPUTS"
    exit 1
fi
echo
echo "All checks passed."
