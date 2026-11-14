#!/bin/bash
# Runs the PHP erdigo-send against the mock receiver of another edition, over real TCP, and checks what comes out.
# This is the proof that the PHP edition works with the others.
#
#     MOCK=dotnet/src/erdigo-send/bin/Release/net8.0/erdigo-send php/tests/cli-smoke.sh
#     MOCK=.build/debug/erdigo-send php/tests/cli-smoke.sh
#
# MOCK is a built erdigo-send of the .NET edition (dotnet build dotnet/src/erdigo-send) or of the Swift edition
# (swift build). It needs no terminal of its own, and makes pseudo terminals for whatever has to run in one, with the
# helper the Swift SDK's checks use, so it runs on macOS and Linux.
#
set -u

TESTS_DIR="$(cd "$(dirname "$0")" && pwd)"
PHP_DIR="$(cd "$TESTS_DIR/.." && pwd)"
REPO_DIR="$(cd "$PHP_DIR/.." && pwd)"
HELPER="$REPO_DIR/swift/Tests/typed-ahead-check.py"
PHP_BIN="$(command -v "${PHP:-php}")"
SEND="$PHP_DIR/bin/erdigo-send"

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
MOCK_PID=""
LAN_MOCK_PID=""
FAILURES=0

cleanup() {
    [ -n "$MOCK_PID" ] && kill "$MOCK_PID" 2>/dev/null
    [ -n "$LAN_MOCK_PID" ] && kill "$LAN_MOCK_PID" 2>/dev/null
    rm -rf "$WORK"
}
trap cleanup EXIT

# Nothing here may wait for a person, whether or not this runs in a terminal.
exec </dev/null

send() {
    limited "$PHP_BIN" "$SEND" "$@"
}

expect_ok() {
    local name="$1"
    shift
    if output="$(send "$@" 2>&1)"; then
        echo "ok    $name"
    else
        echo "FAIL  $name: $output"
        FAILURES=$((FAILURES + 1))
    fi
}

expect_refused() {
    local name="$1" wanted="$2"
    shift 2
    if output="$(send "$@" 2>&1)"; then
        echo "FAIL  $name: should have been refused"
        FAILURES=$((FAILURES + 1))
    elif [[ "$output" != *"$wanted"* ]]; then
        echo "FAIL  $name: expected '$wanted', got: $output"
        FAILURES=$((FAILURES + 1))
    else
        echo "ok    $name"
    fi
}

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

# Keep standard input open, so the mock keeps running and can take commands.
mkfifo "$WORK/commands"
"$MOCK" serve --mock --code-file "$WORK/code" <"$WORK/commands" >"$WORK/mock.log" 2>&1 &
MOCK_PID=$!
exec 3>"$WORK/commands"
for _ in $(seq 1 300); do
    grep -q 'Type .help' "$WORK/mock.log" 2>/dev/null && break
    sleep 0.1
done
PORT="$(grep -oE 'port [0-9]+' "$WORK/mock.log" | head -1 | awk '{print $2}')"
[ -n "$PORT" ] || { echo "FAIL  the mock did not start"; cat "$WORK/mock.log"; exit 1; }
SHORT_KEY="$(sed -nE 's/^Receiver key: (.*)$/\1/p' "$WORK/mock.log" | head -1)"
echo "Mock receiver on port $PORT"

STATE=(--state-dir "$WORK/state")
ADDRESS=(--host 127.0.0.1 --port "$PORT")

# Opens a new pairing window, and sets QR_TEXT and TYPED from it.
fresh_code() {
    local count
    count="$(grep -c 'Pairing text written' "$WORK/mock.log")"
    echo "code" >&3
    for _ in $(seq 1 50); do
        [ "$(grep -c 'Pairing text written' "$WORK/mock.log")" -gt "$count" ] && break
        sleep 0.1
    done
    QR_TEXT="$(cat "$WORK/code")"
    TYPED="$(sed -E 's/.*code=([^&]*).*/\1/' "$WORK/code")"
}
QR_TEXT="$(cat "$WORK/code")"

check "the mock's code file is private" test "$(mode_of "$WORK/code")" = "600"
expect_refused "writing before pairing is refused" "no receiver is paired" ping "${STATE[@]}"
expect_refused "a wrong fingerprint stops pairing" "does not match" \
    pair "${STATE[@]}" "${ADDRESS[@]}" --name Test --kinds pin --code-file "$WORK/code" --fingerprint "$(printf '0%.0s' $(seq 1 64))"
check "the receiver heard no pairing request" bash -c '! grep -q "^\[mock\] pairing request" "$0"' "$WORK/mock.log"

# Pairing with the QR text compares the key without asking anyone.
expect_ok "pair with the QR text" pair "${STATE[@]}" "${ADDRESS[@]}" --name "PHP test" \
    --kinds pin,marker,note,photo,track --label home --code-file "$WORK/code"
check "the mock paired the device" grep -q '^\[mock\] paired .*pin,marker,note,photo,track' "$WORK/mock.log"
check "the state folder is private" test "$(mode_of "$WORK/state")" = "700"
check "the key file is private" test "$(mode_of "$WORK/state/identity.key")" = "600"
check "the pairing file is private" test "$(mode_of "$WORK/state/pairings.json")" = "600"
LIST="$(send list "${STATE[@]}")"
check "the pairing holds the mock's key" bash -c '[[ "$0" == *"key $1"* ]]' "$LIST" "$SHORT_KEY"

# A photo of several chunks: a PNG of random pixels does not compress.
python3 - "$WORK/big.png" <<'PYTHON'
import os, struct, sys, zlib
width, height = 300, 300
def chunk(kind, data):
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xffffffff)
rows = b"".join(b"\0" + os.urandom(width * 3) for _ in range(height))
png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)) \
    + chunk(b"IDAT", zlib.compress(rows, 9)) + chunk(b"IEND", b"")
open(sys.argv[1], "wb").write(png)
PYTHON
printf 'From a file\nwith two lines' >"$WORK/note.txt"
printf '\xef\xbb\xbfFirst line\r\nSecond line\r\n' >"$WORK/note-notepad.txt"
printf 'First line\rSecond line' >"$WORK/note-lone-cr.txt"

expect_ok "ping" ping "${STATE[@]}"
expect_ok "pin" pin "${STATE[@]}" --id gate --lat 37.5796 --lon 126.977 --title "Palace gate"
expect_ok "negative coordinates" pin "${STATE[@]}" --id opera --lat -33.8568 --lon 151.2153
expect_ok "marker" marker "${STATE[@]}" --id tower --lat 48.8584 --lon 2.2945 --color blue --title Tower
expect_ok "note" note "${STATE[@]}" --id meet --lat 37.5663 --lon 126.9779 --text "Meet at the gate"
expect_ok "note from a file" note "${STATE[@]}" --id filed --lat 37.5663 --lon 126.9779 --text-file "$WORK/note.txt"
expect_ok "a note file saved by Notepad, with a byte order mark and CR LF" \
    note "${STATE[@]}" --id notepad --lat 37.5664 --lon 126.978 --text-file "$WORK/note-notepad.txt"
expect_refused "a note file with a carriage return on its own" "control or text-direction" \
    note "${STATE[@]}" --id lone-cr --lat 1 --lon 1 --text-file "$WORK/note-lone-cr.txt"
expect_ok "track point" track-add "${STATE[@]}" --id walk --lat 37.5663 --lon 126.9779 --ts 1700000000000 --alt 38.5 --title Walk
expect_ok "another track point" track-add "${STATE[@]}" --id walk --lat 37.5665 --lon 126.978
expect_ok "track finish" track-finish "${STATE[@]}" --id walk
expect_ok "move" move "${STATE[@]}" --id gate --lat 37.58 --lon 126.98
expect_ok "photo (JPEG)" photo "${STATE[@]}" --id view --lat 37.5663 --lon 126.9779 --file "$PHP_DIR/tests/fixtures/photo.jpg" --title View
expect_ok "photo (PNG of several chunks)" photo "${STATE[@]}" --id big --lat 1 --lon 1 --file "$WORK/big.png"
check "the PNG took several chunks" test "$(wc -c <"$WORK/big.png")" -gt 96000
expect_refused "moving something that is not there" "not_found" move "${STATE[@]}" --id missing --lat 1 --lon 1
expect_ok "delete" delete "${STATE[@]}" --id meet

echo "items 1" >&3
for _ in $(seq 1 50); do
    grep -q '^photo big ' "$WORK/mock.log" && break
    sleep 0.1
done
check "the mock holds the moved pin" grep -q '^pin gate at 37.58,126.98 title=Palace gate' "$WORK/mock.log"
check "the mock holds the pin with negative coordinates" grep -q '^pin opera at -33.8568,151.2153' "$WORK/mock.log"
check "the mock holds the marker" grep -q '^marker tower at 48.8584,2.2945 title=Tower' "$WORK/mock.log"
check "the mock holds the note from a file" grep -q '^note filed ' "$WORK/mock.log"
check "the mock holds the note saved by Notepad" grep -q '^note notepad ' "$WORK/mock.log"
check "the mock holds the track with two points" grep -q '^track walk .*title=Walk points=2' "$WORK/mock.log"
check "the mock holds both photos" bash -c 'grep -q "^photo view " "$0" && grep -q "^photo big " "$0"' "$WORK/mock.log"
check "the deleted note is gone" bash -c '! grep -q "^note meet " "$0"' "$WORK/mock.log"
check "the mock stored two photos" test "$(grep -c 'photo stored' "$WORK/mock.log")" = "2"

# Several processes sharing the state folder, as the workers of a web server would, take turns.
WORKERS=()
for index in 1 2 3 4 5 6; do
    send pin "${STATE[@]}" --id "worker-$index" --lat 1 --lon "$index" >"$WORK/worker-$index.log" 2>&1 &
    WORKERS+=($!)
done
wait "${WORKERS[@]}"
WORKERS_OK=1
for index in 1 2 3 4 5 6; do
    grep -q '^OK' "$WORK/worker-$index.log" || { WORKERS_OK=0; cat "$WORK/worker-$index.log"; }
done
check "six processes at once all succeed" test "$WORKERS_OK" = "1"
check "no connection was replaced" bash -c '! grep -q "rejected.*replaced" "$0"' "$WORK/mock.log"

expect_ok "delete-all" delete-all "${STATE[@]}" --yes

# The example program pairs with the QR text, and writes.
fresh_code
if output="$(limited "$PHP_BIN" "$PHP_DIR/examples/quickstart.php" 127.0.0.1 "$PORT" "$WORK/code" "$WORK/state-example" 2>&1)" \
    && [[ "$output" == *"Sent a pin, a note and a track."* ]]; then
    echo "ok    the example pairs and writes"
else
    failed "the example pairs and writes: $output"
fi
echo "devices" >&3
sleep 0.5
check "the mock paired the example" grep -q "^[0-9]*\. 'PHP quickstart'" "$WORK/mock.log"

# Removing the device on the receiver locks it out.
NUMBER="$(sed -nE "s/^([0-9]+)\. 'PHP test' .*/\1/p" "$WORK/mock.log" | tail -1)"
echo "remove ${NUMBER:-1}" >&3
sleep 0.5
expect_refused "a removed device gets no answer" "No receiver answered" ping "${STATE[@]}" --retry-for 0

# The code must never reach this tool's output or the mock's log.
CODE_TEXT="$(sed -E 's/.*code=([^&]*).*/\1/' "$WORK/code")"
check "the code is not in the mock's log" bash -c '! grep -q "$0" "$1"' "$CODE_TEXT" "$WORK/mock.log"

# A typed code: the person compares the key, which the tool shows before the receiver hears about the device.
if command -v python3 >/dev/null 2>&1; then
    run_checked() {
        RUN_STATUS=""
        RUN_CONNECTIONS=""
        RUN_OUTPUT="$(python3 -I "$HELPER" "$PORT" "$@" 2>&1)" || return 1
        RUN_STATUS="$(sed -n 's/^exit status //p' <<<"$RUN_OUTPUT" | head -1)"
        RUN_CONNECTIONS="$(sed -n 's/^connections //p' <<<"$RUN_OUTPUT" | head -1)"
    }
    requests() {
        grep -c '^\[mock\] pairing request' "$WORK/mock.log"
    }
    typed_file() {
        printf '%s\n' "$TYPED" >"$WORK/typed-code"
        chmod 600 "$WORK/typed-code"
    }
    PAIR_TYPED=("$PHP_BIN" "$SEND" pair --host 127.0.0.1 --port PROXY_PORT --name Typed --kinds pin)

    fresh_code
    typed_file
    BEFORE="$(requests)"
    if run_checked --answer "no"$'\n' -- "${PAIR_TYPED[@]}" --state-dir "$WORK/state-no" --code-file "$WORK/typed-code"; then
        check "a typed code shows the key and asks" bash -c '[[ "$0" == *"Receiver key: $1"* && "$0" == *"type yes"* ]]' "$RUN_OUTPUT" "$SHORT_KEY"
        check "answering no fails" test "$RUN_STATUS" != "0"
        check "answering no saves no pairing" test ! -e "$WORK/state-no/pairings.json"
        check "answering no tells the receiver nothing" test "$(requests)" = "$BEFORE"
        check "answering no made one connection" test "$RUN_CONNECTIONS" = "1"
    else
        failed "a typed code answered no: $RUN_OUTPUT"
    fi

    # Typed before the key appeared, a yes is no answer: the tool waits for one typed after it.
    if run_checked --early "yes"$'\n' --answer "no"$'\n' -- "${PAIR_TYPED[@]}" --state-dir "$WORK/state-early" --code-file "$WORK/typed-code"; then
        check "a yes typed early does not count" test "$RUN_STATUS" != "0"
        check "a yes typed early tells the receiver nothing" test "$(requests)" = "$BEFORE"
    else
        failed "a yes typed early: $RUN_OUTPUT"
    fi
    if run_checked --early "ye" --answer "s"$'\n' -- "${PAIR_TYPED[@]}" --state-dir "$WORK/state-half" --code-file "$WORK/typed-code"; then
        check "half a yes typed early does not count" test "$RUN_STATUS" != "0"
        check "half a yes typed early tells the receiver nothing" test "$(requests)" = "$BEFORE"
    else
        failed "half a yes typed early: $RUN_OUTPUT"
    fi

    if run_checked --early "no"$'\n' --answer "yes"$'\n' -- "${PAIR_TYPED[@]}" --state-dir "$WORK/state-yes" --code-file "$WORK/typed-code"; then
        check "a yes after the key pairs" test "$RUN_STATUS" = "0"
        check "a confirmed typed code is saved" test -e "$WORK/state-yes/pairings.json"
        check "a confirmed typed code connects twice" test "$RUN_CONNECTIONS" = "2"
        check "the receiver heard one request" test "$(requests)" = "$((BEFORE + 1))"
    else
        failed "a typed code answered yes: $RUN_OUTPUT"
    fi

    # The code typed at the keyboard, hidden, and then the key confirmed.
    fresh_code
    if run_checked --type-code "$TYPED" --answer "yes"$'\n' -- "${PAIR_TYPED[@]}" --state-dir "$WORK/state-keyboard"; then
        check "a code typed at the keyboard pairs" test "$RUN_STATUS" = "0"
        check "the code typed is not shown" bash -c '[[ "$0" != *"$1"* ]]' "$RUN_OUTPUT" "$TYPED"
    else
        failed "a code typed at the keyboard: $RUN_OUTPUT"
    fi

    # Without a terminal, a bare code is refused before the receiver is contacted, whatever standard input holds.
    fresh_code
    typed_file
    if run_checked --pipe "yes"$'\n' -- "${PAIR_TYPED[@]}" --state-dir "$WORK/state-pipe" --code-file "$WORK/typed-code"; then
        check "a bare code on a pipe is refused" bash -c '[[ "$0" == *"not a terminal"* ]]' "$RUN_OUTPUT"
        check "a bare code on a pipe contacts nobody" test "$RUN_CONNECTIONS" = "0"
    else
        failed "a bare code on a pipe: $RUN_OUTPUT"
    fi

    # The QR text asks nothing, even in a terminal.
    printf '%s\n' "$QR_TEXT" >"$WORK/qr-code"
    chmod 600 "$WORK/qr-code"
    if run_checked --terminal -- "${PAIR_TYPED[@]}" --state-dir "$WORK/state-qr" --code-file "$WORK/qr-code"; then
        check "the QR text in a terminal pairs without asking" test "$RUN_STATUS" = "0"
    else
        failed "the QR text in a terminal: $RUN_OUTPUT"
    fi
else
    echo "skip  terminal checks (python3 is needed)"
fi

# Finding a receiver on the network, with the mock advertising itself over multicast DNS. A network that drops
# multicast cannot run this, and a desktop firewall may ask about a mock that listens on every interface, so it runs
# only when asked for.
if [ "${ERDIGO_MULTICAST_CHECKS:-}" = "1" ] && command -v python3 >/dev/null 2>&1 && "$PHP_BIN" -m | grep -qx sockets; then
    "$MOCK" serve --mock --lan --bonjour --auto-approve --code-file "$WORK/lan-code" </dev/null >"$WORK/lan-mock.log" 2>&1 &
    LAN_MOCK_PID=$!
    for _ in $(seq 1 300); do
        grep -q 'Type .help' "$WORK/lan-mock.log" 2>/dev/null && break
        sleep 0.1
    done
    expect_ok "pair with a receiver found on the network" pair --state-dir "$WORK/state-lan" --name Found --kinds pin \
        --code-file "$WORK/lan-code"
    LAN_PORT="$(grep -oE 'port [0-9]+' "$WORK/lan-mock.log" | head -1 | awk '{print $2}')"
    check "the receiver found is the advertised one" grep -q "\"lastPort\": $LAN_PORT" "$WORK/state-lan/pairings.json"
    expect_ok "write to it" pin --state-dir "$WORK/state-lan" --id found --lat 1 --lon 2
else
    echo "skip  discovery on the network (set ERDIGO_MULTICAST_CHECKS=1, with PHP's sockets extension)"
fi

exec 3>&-
if [ "$FAILURES" -ne 0 ]; then
    echo
    echo "$FAILURES check(s) failed. The mock's log:"
    cat "$WORK/mock.log"
    exit 1
fi
echo
echo "All checks passed."
