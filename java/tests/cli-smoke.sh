#!/bin/bash
# Runs the Java erdigo-send against the .NET SDK's mock receiver and checks what comes out, on macOS or Linux.
#
#     (cd java && ./mvnw -B package) && dotnet build dotnet/src/erdigo-send -c Release && java/tests/cli-smoke.sh
#
# MOCK names the mock's erdigo-send executable (by default the .NET Release build). It needs no terminal of its own,
# and it makes its own pseudo terminals for whatever has to run in one, with the helper the Swift and .NET SDKs'
# checks use. It is a port of the device side of dotnet/tests/cli-smoke.sh: this edition has no mock of its own.
#
set -u

TESTS_DIR="$(cd "$(dirname "$0")" && pwd)"
JAVA_DIR="$(cd "$TESTS_DIR/.." && pwd)"
ROOT="$(cd "$JAVA_DIR/.." && pwd)"
HELPER="$ROOT/swift/Tests/typed-ahead-check.py"
JAR="${JAR:-$JAVA_DIR/target/erdigo-send.jar}"
MOCK="${MOCK:-$ROOT/dotnet/src/erdigo-send/bin/Release/net8.0/erdigo-send}"
[ -f "$JAR" ] || { echo "Build first: (cd java && ./mvnw -B package)"; exit 1; }
[ -x "$MOCK" ] || { echo "Build the .NET mock first: dotnet build dotnet/src/erdigo-send -c Release, or set MOCK"; exit 1; }

# Runs a command that is stopped if it takes too long, so that it cannot hang this script. An alarm
# survives exec. Only the mocks, which this script stops itself, run without this.
limited() {
    perl -e 'alarm 120; exec { $ARGV[0] } @ARGV or exit 127' -- "$@"
}

# File modes and digests are spelled differently on macOS and Linux.
mode_of() {
    if stat -f '%Lp' "$1" >/dev/null 2>&1; then stat -f '%Lp' "$1"; else stat -c '%a' "$1"; fi
}
digest_of() {
    if command -v md5 >/dev/null 2>&1; then md5 -q "$1"; else md5sum "$1" | awk '{print $1}'; fi
}

WORK="$(mktemp -d)"
MOCK_PID=""
MOCK3_PID=""
FAILURES=0

cleanup() {
    [ -n "$MOCK_PID" ] && kill "$MOCK_PID" 2>/dev/null
    [ -n "$MOCK3_PID" ] && kill "$MOCK3_PID" 2>/dev/null
    rm -rf "$WORK"
}
trap cleanup EXIT

# The helper runs the tool by its path, so the jar gets a launcher of its own.
BIN="$WORK/erdigo-send"
printf '#!/bin/sh\nexec java -jar "%s" "$@"\n' "$JAR" >"$BIN"
chmod 755 "$BIN"

# Nothing here may wait for a person, whether or not this runs in a terminal.
exec </dev/null

expect_ok() {
    local name="$1"
    shift
    if output="$(limited "$@" 2>&1)"; then
        echo "ok    $name"
    else
        echo "FAIL  $name: $output"
        FAILURES=$((FAILURES + 1))
    fi
}

expect_refused() {
    local name="$1" wanted="$2"
    shift 2
    if output="$(limited "$@" 2>&1)"; then
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

# Runs a command as typed-ahead-check.py describes, reaching the mock on HELPER_PORT. Sets RUN_OUTPUT,
# and also RUN_STATUS and RUN_CONNECTIONS unless the command did not behave as that says, when this
# returns false.
run_checked() {
    RUN_STATUS=""
    RUN_CONNECTIONS=""
    RUN_OUTPUT="$(python3 -I "$HELPER" "$HELPER_PORT" "$@" 2>&1)" || return 1
    RUN_STATUS="$(sed -n 's/^exit status //p' <<<"$RUN_OUTPUT" | head -1)"
    RUN_CONNECTIONS="$(sed -n 's/^connections //p' <<<"$RUN_OUTPUT" | head -1)"
}

# Keep standard input open, so the mock keeps running and can take commands.
mkfifo "$WORK/commands"
"$MOCK" serve --mock --code-file "$WORK/code" <"$WORK/commands" >"$WORK/mock.log" 2>&1 &
MOCK_PID=$!
exec 3>"$WORK/commands"

for _ in $(seq 1 600); do
    grep -q 'Type .help' "$WORK/mock.log" 2>/dev/null && break
    sleep 0.1
done
PORT="$(grep -oE 'port [0-9]+' "$WORK/mock.log" | awk '{print $2}')"
[ -n "$PORT" ] || { echo "FAIL  the mock did not start"; cat "$WORK/mock.log"; exit 1; }

STATE=(--state-dir "$WORK/state")
ADDRESS=(--host 127.0.0.1 --port "$PORT")

expect_refused "a code argument is refused" "not accepted as an argument" \
    "$BIN" pair "${STATE[@]}" "${ADDRESS[@]}" --name Test --kinds pin --code ABCDE
expect_refused "a code given with an equals sign is refused" "not accepted as an argument" \
    "$BIN" pair "${STATE[@]}" "${ADDRESS[@]}" --name Test --kinds pin --code=ABCDE
OUTPUT="$(limited "$BIN" pair "${STATE[@]}" --code=SECRET-CODE 2>&1)"
check "an equals-sign code is not repeated in the error" bash -c '[[ "$0" != *SECRET-CODE* ]]' "$OUTPUT"
OUTPUT="$(limited "$BIN" pair "${STATE[@]}" SECRET-CODE 2>&1)"
check "a bare code is not repeated in the error" bash -c '[[ "$0" != *SECRET-CODE* ]]' "$OUTPUT"
OUTPUT="$(limited "$BIN" pair "${STATE[@]}" --SECRET-CODE 2>&1)"
check "a code written as an option is not repeated" bash -c '[[ "$0" != *SECRET-CODE* ]]' "$OUTPUT"
OUTPUT="$(limited "$BIN" pair "${STATE[@]}" --name x --kinds SECRET-CODE 2>&1)"
check "a code in --kinds is not repeated" bash -c '[[ "$0" != *SECRET-CODE* ]]' "$OUTPUT"
OUTPUT="$(limited "$BIN" SECRET-CODE 2>&1)"
check "a code as a command is not repeated" bash -c '[[ "$0" != *SECRET-CODE* ]]' "$OUTPUT"
expect_refused "--state-dir and --profile together are refused" "cannot be used together" \
    "$BIN" identity "${STATE[@]}" --profile other
expect_refused "--keychain is refused" "belongs to the Swift SDK" "$BIN" identity "${STATE[@]}" --keychain
expect_refused "this edition has no mock" "no mock receiver" "$BIN" serve --mock
chmod 644 "$WORK/code"
expect_refused "a readable code file is refused" "mode 0600" \
    "$BIN" pair "${STATE[@]}" "${ADDRESS[@]}" --name Test --kinds pin --code-file "$WORK/code"
chmod 600 "$WORK/code"
expect_refused "a wrong fingerprint stops pairing" "fingerprint" \
    "$BIN" pair "${STATE[@]}" "${ADDRESS[@]}" --name Test --kinds pin --code-file "$WORK/code" \
    --fingerprint "$(printf '0%.0s' $(seq 1 64))"
expect_refused "writing before pairing is refused" "no receiver is paired" \
    "$BIN" ping "${STATE[@]}"

expect_ok "pair" "$BIN" pair "${STATE[@]}" "${ADDRESS[@]}" --name "CLI test" \
    --kinds pin,marker,note,photo,track --label home --code-file "$WORK/code"
# A bare code, as if typed, must lead to a key comparison that can actually be done.
echo "code" >&3
for _ in $(seq 1 50); do
    [ "$(grep -c 'Pairing text written' "$WORK/mock.log")" -ge 2 ] && break
    sleep 0.1
done
sed -E 's/.*code=([^&]*).*/\1/' "$WORK/code" >"$WORK/typed-code"
chmod 600 "$WORK/typed-code"
HELPER_PORT="$PORT"
run_checked --answer "yes"$'\n' -- "$BIN" pair --state-dir "$WORK/state-typed" --host 127.0.0.1 --port PROXY_PORT --name Typed --kinds pin --label typed --code-file "$WORK/typed-code"
check "a typed code asks for the key to be compared" bash -c '[[ "$0" == *"type yes"* ]]' "$RUN_OUTPUT"
check "a typed code is saved once confirmed" test -e "$WORK/state-typed/pairings.json"
check "the state folder is private" test "$(mode_of "$WORK/state")" = "700"
check "the key file is private" test "$(mode_of "$WORK/state/identity.key")" = "600"
check "the pairing file is private" test "$(mode_of "$WORK/state/pairings.json")" = "600"

expect_refused "a label already in use is refused" "already paired under that label" \
    "$BIN" pair "${STATE[@]}" "${ADDRESS[@]}" --name Test --kinds pin --label home --code-file "$WORK/code"

python3 - "$WORK/tiny.png" <<'PY'
import struct, sys, zlib
def chunk(kind, data):
    return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data) & 0xffffffff)
raw = b''.join(b'\x00' + b'\xff\x00\x00' * 2 for _ in range(2))
png = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', 2, 2, 8, 2, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(raw)) + chunk(b'IEND', b'')
open(sys.argv[1], 'wb').write(png)
PY

expect_ok "ping" "$BIN" ping "${STATE[@]}"
expect_ok "pin" "$BIN" pin "${STATE[@]}" --id gate --lat 37.5796 --lon 126.977 --title "Palace gate"
expect_ok "negative coordinates" "$BIN" pin "${STATE[@]}" --id opera --lat -33.8568 --lon 151.2153
expect_ok "marker" "$BIN" marker "${STATE[@]}" --id tower --lat 48.8584 --lon 2.2945 --color blue
expect_ok "note" "$BIN" note "${STATE[@]}" --id meet --lat 37.5663 --lon 126.9779 --text "Meet at the gate"
expect_ok "photo" "$BIN" photo "${STATE[@]}" --id view --lat 37.5 --lon 127 --file "$WORK/tiny.png" --title View
expect_ok "track point" "$BIN" track-add "${STATE[@]}" --id walk --lat 37.5663 --lon 126.9779 --ts 1700000000000 --alt 38.5 --title Walk
expect_ok "track finish" "$BIN" track-finish "${STATE[@]}" --id walk
expect_ok "move" "$BIN" move "${STATE[@]}" --id gate --lat 37.58 --lon 126.98
expect_ok "delete" "$BIN" delete "${STATE[@]}" --id meet
expect_ok "several sends keep working in sequence" "$BIN" ping "${STATE[@]}" --to home
# The mock numbers its devices in the order of their keys, so this asks about both.
echo "items 1" >&3
echo "items 2" >&3
for _ in $(seq 1 50); do
    grep -q '^track walk' "$WORK/mock.log" && break
    sleep 0.1
done
check "the mock holds the pin, moved" grep -q '^pin gate at 37.58,126.98 title=Palace gate' "$WORK/mock.log"
check "the mock holds the marker" grep -q '^marker tower at 48.8584,2.2945' "$WORK/mock.log"
check "the mock holds the photo" grep -q '^photo view at 37.5,127.0 title=View' "$WORK/mock.log"
check "the mock holds the track" grep -q '^track walk at -,- title=Walk points=1' "$WORK/mock.log"
check "the note was deleted" bash -c '! grep -q "^note meet" "$0"' "$WORK/mock.log"

printf 'not an image' >"$WORK/fake.jpg"
expect_refused "a file that is not an image" "JPEG or PNG" "$BIN" photo "${STATE[@]}" --id p --lat 1 --lon 1 --file "$WORK/fake.jpg"

# Files over the limit are refused before the receiver is contacted, without being read in full.
connections() {
    grep -c '^\[mock\] connected' "$WORK/mock.log"
}
perl -e 'print "a" x 4096' >"$WORK/note-at-limit.txt"
perl -e 'print "a" x 4097' >"$WORK/note-over-limit.txt"
printf 'caf\xe9' >"$WORK/note-latin1.txt"
printf '\xef\xbb\xbfFirst line\r\nSecond line\r\n' >"$WORK/note-notepad.txt"
printf 'First line\rSecond line' >"$WORK/note-lone-cr.txt"
perl -e 'open(my $f, ">", $ARGV[0]) or die; truncate($f, 256 * 1024 * 1024) or die' "$WORK/huge.jpg"
expect_ok "a note file at the limit" "$BIN" note "${STATE[@]}" --id long --lat 1 --lon 1 --text-file "$WORK/note-at-limit.txt"
expect_ok "a note file saved by Notepad, with a byte order mark and CR LF" \
    "$BIN" note "${STATE[@]}" --id notepad --lat 1 --lon 1 --text-file "$WORK/note-notepad.txt"
BEFORE_CONNECTIONS="$(connections)"
expect_refused "a note file one byte over the limit" "at most 4096 bytes" \
    "$BIN" note "${STATE[@]}" --id long --lat 1 --lon 1 --text-file "$WORK/note-over-limit.txt"
expect_refused "a note file with a carriage return on its own" "control or text-direction" \
    "$BIN" note "${STATE[@]}" --id lone-cr --lat 1 --lon 1 --text-file "$WORK/note-lone-cr.txt"
expect_refused "a note file that is not UTF-8" "not UTF-8 text" \
    "$BIN" note "${STATE[@]}" --id long --lat 1 --lon 1 --text-file "$WORK/note-latin1.txt"
expect_refused "a missing note file" "the note file does not exist" \
    "$BIN" note "${STATE[@]}" --id long --lat 1 --lon 1 --text-file "$WORK/no-such-note.txt"
expect_refused "a photo file far over the limit" "at most 1048576 bytes" \
    "$BIN" photo "${STATE[@]}" --id p --lat 1 --lon 1 --file "$WORK/huge.jpg"
expect_refused "a missing photo file" "the photo file does not exist" \
    "$BIN" photo "${STATE[@]}" --id p --lat 1 --lon 1 --file "$WORK/no-such.jpg"
check "files that were refused never reached the receiver" test "$(connections)" = "$BEFORE_CONNECTIONS"
expect_refused "a bad id" "item id" "$BIN" pin "${STATE[@]}" --id "bad id" --lat 1 --lon 1
expect_refused "a bad latitude" "latitude" "$BIN" pin "${STATE[@]}" --id a --lat 91 --lon 1
expect_refused "NaN" "must be a number" "$BIN" pin "${STATE[@]}" --id a --lat nan --lon 1
expect_refused "a hexadecimal number" "must be a number" "$BIN" pin "${STATE[@]}" --id a --lat 0x1p3 --lon 1
expect_refused "a bad colour" "--color must be one of" "$BIN" marker "${STATE[@]}" --id a --lat 1 --lon 1 --color pink
expect_refused "delete-all without confirmation" "--yes" "$BIN" delete-all "${STATE[@]}"
expect_refused "an unknown option" "unknown option" "$BIN" ping "${STATE[@]}" --bogus 1
expect_refused "an option for another command" "does not apply" "$BIN" ping "${STATE[@]}" --title x
expect_refused "moving something that is not there" "not_found" "$BIN" move "${STATE[@]}" --id missing --lat 1 --lon 1
expect_refused "an unknown receiver" "no receiver is paired under" "$BIN" ping "${STATE[@]}" --to elsewhere
expect_refused "a bad --retry-for" "--retry-for must be a number of seconds" "$BIN" ping "${STATE[@]}" --retry-for -1

expect_ok "delete-all" "$BIN" delete-all "${STATE[@]}" --yes

LIST="$(limited "$BIN" list "${STATE[@]}" --redact)"
check "--redact hides the label and key" bash -c '[[ "$0" != *home* && "$0" == *"<redacted>"* ]]' "$LIST"
LIST="$(limited "$BIN" list "${STATE[@]}")"
check "list shows the label and the allowed kinds" bash -c '[[ "$0" == *"home  key "*"allowed pin,marker,note,photo,track"* ]]' "$LIST"
IDENTITY="$(limited "$BIN" identity "${STATE[@]}")"
check "identity prints a fingerprint" bash -c '[[ "$0" =~ ^[0-9a-f]{64}$ ]]' "$IDENTITY"
check "the mock knows this device by that key" grep -q "paired ${IDENTITY:0:16}" "$WORK/mock.log"

# The code must never reach this tool's output.
CODE_TEXT="$(sed -E 's/.*code=([^&]*).*/\1/' "$WORK/code")"
check "the code is not in the log" bash -c '! grep -q "$0" "$1"' "$CODE_TEXT" "$WORK/mock.log"

# Removing the device on the receiver must lock it out. Two devices are paired, and removing the first
# leaves the other as number 1.
echo "remove 1" >&3
echo "remove 1" >&3
sleep 0.5
expect_refused "a removed device gets no answer" "No receiver answered" "$BIN" ping "${STATE[@]}" --retry-for 0
expect_ok "forget" "$BIN" forget "${STATE[@]}" --to home
expect_refused "a forgotten receiver cannot be written to" "no receiver is paired yet" "$BIN" ping "${STATE[@]}"

# The receiver must hear no request, no pairing may be kept and no item sent, before its key has
# been checked. This mock is used only for these checks, so anything in its log came from them.
LOG3="$WORK/mock3.log"
mkfifo "$WORK/commands3"
"$MOCK" serve --mock --code-file "$WORK/code3" <"$WORK/commands3" >"$LOG3" 2>&1 &
MOCK3_PID=$!
exec 5>"$WORK/commands3"
for _ in $(seq 1 600); do
    grep -q 'Type .help' "$LOG3" 2>/dev/null && break
    sleep 0.1
done
PORT3="$(grep -oE 'port [0-9]+' "$LOG3" | awk '{print $2}')"
[ -n "$PORT3" ] || { echo "FAIL  the third mock did not start"; cat "$LOG3"; exit 1; }
HELPER_PORT="$PORT3"
SHORT_KEY="$(sed -nE 's/^Receiver key: (.*)$/\1/p' "$LOG3" | head -1)"
WRONG_FP="$(printf '0%.0s' $(seq 1 64))"
PROMPT="type yes"
RUN=0

# What each answer below leads to. A person pressing Ctrl-D at the question ends the input.
CTRL_D=$'\x04'
NOT_CONFIRMED="No pairing was saved"
MISMATCH="does not match"

# What the commands below run under, in a shell of its own. To put something else on standard input
# than the mode gives, set this for one call.
STDIN_SCRIPT='exec "$@"'
YES_ON_A_PIPE='printf "yes\n" | "$@"'
NOTHING_THERE='exec "$@" </dev/null'
CLOSED='exec "$@" <&-'
OUTPUT_TO_A_FILE='exec "$@" >/dev/null'
OUTPUT_CLOSED='exec "$@" >&-'

# Everything the mock logs for a device that has connected to write.
writes() {
    sleep 0.3
    grep -cE '^\[mock\] (#|connected|rejected)' "$LOG3"
}

requests() {
    grep -c 'pairing request' "$LOG3"
}

# For a refusal that has to come before anything at all reaches the receiver, whatever it may say.
not_contacted() {
    check "$1: the receiver was not contacted" test "$RUN_CONNECTIONS" = "0"
}

# Opens a new window, and sets TYPED and QR_TEXT from it. Every run below needs its own code.
fresh_code() {
    local count
    count="$(grep -c 'Pairing text written' "$LOG3")"
    echo "code" >&5
    for _ in $(seq 1 50); do
        [ "$(grep -c 'Pairing text written' "$LOG3")" -gt "$count" ] && break
        sleep 0.1
    done
    QR_TEXT="$(cat "$WORK/code3")"
    TYPED="$(sed -E 's/.*code=([^&]*).*/\1/' "$WORK/code3")"
}

# Given in place of a file below, for a code typed when the tool asks for it. --type-code is what types it.
KEYBOARD=""

# NAME, WORDS THE OUTPUT MUST HOLD, FILE WITH THE CODE, how to run it as typed-ahead-check.py takes
# that, and then -- and any further options of the tool.
pair_refused() {
    local name="pair: $1" wanted="$2" file="$3" how=()
    shift 3
    while [ "$#" -gt 0 ] && [ "$1" != "--" ]; do
        how+=("$1")
        shift
    done
    [ "$#" -gt 0 ] && shift
    local state="$WORK/pair-$((RUN += 1))" before_writes before_requests
    before_writes="$(writes)"
    before_requests="$(requests)"
    if ! run_checked "${how[@]}" -- /bin/sh -c "$STDIN_SCRIPT" sh "$BIN" pair --state-dir "$state" --host 127.0.0.1 --port PROXY_PORT --name Test --kinds pin ${file:+--code-file "$file"} "$@"; then
        failed "$name: $RUN_OUTPUT"
    elif [ "$RUN_STATUS" = "0" ]; then
        failed "$name: should have been refused, got: $RUN_OUTPUT"
    elif [[ "$RUN_OUTPUT" != *"$wanted"* ]]; then
        failed "$name: expected '$wanted', got: $RUN_OUTPUT"
    elif [ -e "$state/pairings.json" ]; then
        failed "$name: a pairing was saved"
    elif [ "$(writes)" != "$before_writes" ]; then
        failed "$name: something was sent to the receiver"
    elif [ "$(requests)" != "$before_requests" ]; then
        failed "$name: the receiver heard a pairing request"
    elif [[ "$(limited "$BIN" ping --state-dir "$state" 2>&1)" != *"no receiver is paired"* ]]; then
        failed "$name: the tool can still write"
    else
        echo "ok    $name"
    fi
}

# NAME, whether it should ask ("asks" or "quiet"), FILE WITH THE CODE, how to run it as
# typed-ahead-check.py takes that, and then -- and any further options of the tool.
pair_succeeds() {
    local name="pair: $1" asking="$2" file="$3" how=()
    shift 3
    while [ "$#" -gt 0 ] && [ "$1" != "--" ]; do
        how+=("$1")
        shift
    done
    [ "$#" -gt 0 ] && shift
    local state="$WORK/pair-$((RUN += 1))"
    if ! run_checked "${how[@]}" -- /bin/sh -c "$STDIN_SCRIPT" sh "$BIN" pair --state-dir "$state" --host 127.0.0.1 --port PROXY_PORT --name Test --kinds pin ${file:+--code-file "$file"} "$@"; then
        failed "$name: $RUN_OUTPUT"
    elif [ "$RUN_STATUS" != "0" ]; then
        failed "$name: exited with $RUN_STATUS: $RUN_OUTPUT"
    elif [ "$asking" = "asks" ] && [[ "$RUN_OUTPUT" != *"$PROMPT"* || "$RUN_OUTPUT" != *"Receiver key: $SHORT_KEY"* ]]; then
        failed "$name: did not show the key to compare: $RUN_OUTPUT"
    elif [ "$asking" = "quiet" ] && [[ "$RUN_OUTPUT" == *"$PROMPT"* ]]; then
        failed "$name: should not have asked anything: $RUN_OUTPUT"
    elif [ "$(mode_of "$state/pairings.json" 2>/dev/null)" != "600" ]; then
        failed "$name: no private pairing file was saved"
    elif ! limited "$BIN" pin --state-dir "$state" --host 127.0.0.1 --port "$PORT3" --id checked --lat 37.5663 --lon 126.9779 >/dev/null 2>&1; then
        failed "$name: the saved pairing does not work"
    else
        echo "ok    $name"
    fi
}

# Writes the current code in the form a tool reads: bare, or as QR text with or without a fingerprint.
code_files() {
    fresh_code
    local name
    for name in bare wrong-fp no-fp qr; do
        : >"$WORK/$name"
        chmod 600 "$WORK/$name"
    done
    printf '%s\n' "$TYPED" >"$WORK/bare"
    printf '%s\n' "erdigo-pair:1?code=$TYPED&fp=$WRONG_FP" >"$WORK/wrong-fp"
    printf '%s\n' "erdigo-pair:1?code=$TYPED" >"$WORK/no-fp"
    printf '%s\n' "$QR_TEXT" >"$WORK/qr"
    RIGHT_FP="${QR_TEXT##*fp=}"
}

code_files
# A person at a terminal, who answers the question about the key.
pair_refused "at a terminal, Ctrl-D" "$NOT_CONFIRMED" "$WORK/bare" --answer "$CTRL_D"
pair_refused "at a terminal, a wrong answer" "$NOT_CONFIRMED" "$WORK/bare" --answer "no"$'\n'
pair_refused "at a terminal, a cancelling answer" "$NOT_CONFIRMED" "$WORK/bare" --answer "cancel"$'\n'
pair_refused "at a terminal, an empty answer" "$NOT_CONFIRMED" "$WORK/bare" --answer $'\n'
pair_refused "at a terminal, half an answer" "$NOT_CONFIRMED" "$WORK/bare" --answer "y"$'\n'
# A hidden key cannot be compared, at a terminal or not.
pair_refused "--redact with a bare code at a terminal" "--redact hides" "$WORK/bare" --terminal -- --redact
not_contacted "pair: --redact with a bare code at a terminal"
pair_refused "--redact with a bare code and no terminal" "--redact hides" "$WORK/bare" --pipe "yes"$'\n' -- --redact
not_contacted "pair: --redact with a bare code and no terminal"
pair_refused "QR text with no fingerprint" "no fingerprint" "$WORK/no-fp" --pipe "yes"$'\n'
pair_refused "QR text with the wrong fingerprint" "$MISMATCH" "$WORK/wrong-fp" --pipe "yes"$'\n'
pair_refused "--fingerprint with the wrong value" "$MISMATCH" "$WORK/bare" --pipe "yes"$'\n' -- --fingerprint "$WRONG_FP"
# With no person to ask, a bare code must not even reach the receiver, whatever standard input holds.
NO_TERMINAL="Use the QR text, or give the full 64-digit key with --fingerprint"
pair_refused "a bare code with yes on a pipe" "$NO_TERMINAL" "$WORK/bare" --pipe "yes"$'\n'
not_contacted "pair: a bare code with yes on a pipe"
pair_refused "a bare code with nothing on a pipe" "$NO_TERMINAL" "$WORK/bare" --pipe ""
not_contacted "pair: a bare code with nothing on a pipe"
STDIN_SCRIPT="$NOTHING_THERE" pair_refused "a bare code with no terminal and /dev/null" "$NO_TERMINAL" "$WORK/bare" --pipe ""
not_contacted "pair: a bare code with no terminal and /dev/null"
STDIN_SCRIPT="$CLOSED" pair_refused "a bare code with standard input closed" "$NO_TERMINAL" "$WORK/bare" --pipe ""
not_contacted "pair: a bare code with standard input closed"
STDIN_SCRIPT="$YES_ON_A_PIPE" pair_refused "a bare code with a terminal, and yes on a pipe" "$NO_TERMINAL" "$WORK/bare" --terminal
not_contacted "pair: a bare code with a terminal, and yes on a pipe"
# A question sent to a file would be answered unseen.
STDIN_SCRIPT="$OUTPUT_TO_A_FILE" pair_refused "a bare code with the question going to a file" "$NO_TERMINAL" "$WORK/bare" --terminal
not_contacted "pair: a bare code with the question going to a file"
STDIN_SCRIPT="$OUTPUT_CLOSED" pair_refused "a bare code with standard output closed" "$NO_TERMINAL" "$WORK/bare" --terminal
not_contacted "pair: a bare code with standard output closed"
# The same when the code is typed, which reads it from the terminal whatever standard input is.
pair_refused "a typed code, answered no" "$NOT_CONFIRMED" "$KEYBOARD" --answer "no"$'\n' --type-code "$TYPED"
STDIN_SCRIPT="$YES_ON_A_PIPE" pair_refused "a typed code, with yes on a pipe" "$NO_TERMINAL" "$KEYBOARD" --terminal --type-code "$TYPED"
not_contacted "pair: a typed code, with yes on a pipe"
# With no terminal the code is not taken from standard input instead.
pair_refused "no code file and no terminal" "no terminal to read the code from" "$KEYBOARD" --pipe "$TYPED"$'\n'"yes"$'\n'
not_contacted "pair: no code file and no terminal"
# Nothing above has reached the person at the receiver, so the code is still good.
pair_succeeds "at a terminal, a person who confirms" asks "$WORK/bare" --answer "yes"$'\n'
code_files
pair_succeeds "a typed code, answered yes" asks "$KEYBOARD" --answer "yes"$'\n' --type-code "$TYPED"
check "pair: a typed code is not shown on the screen" bash -c '[[ "$0" != *"$1"* ]]' "$RUN_OUTPUT" "$TYPED"
code_files
pair_succeeds "at a terminal, a confirmation with spaces and capitals" asks "$WORK/bare" --answer "  YES "$'\n'
# The key is compared for the person, so these need no terminal and ask nothing.
code_files
pair_succeeds "QR text typed at a terminal" quiet "$KEYBOARD" --terminal --type-code "$QR_TEXT"
code_files
pair_succeeds "QR text with the right fingerprint and no terminal" quiet "$WORK/qr" --pipe ""
code_files
pair_succeeds "a bare code with the right --fingerprint and no terminal" quiet "$WORK/bare" --pipe "" -- --fingerprint "$RIGHT_FP"
code_files
pair_succeeds "QR text with --redact" quiet "$WORK/qr" --pipe "" -- --redact
code_files
pair_succeeds "QR text with the right fingerprint at a terminal" quiet "$WORK/qr" --terminal
code_files
pair_succeeds "a bare code with the right --fingerprint at a terminal" quiet "$WORK/bare" --terminal -- --fingerprint "$RIGHT_FP"
code_files
STDIN_SCRIPT="$OUTPUT_TO_A_FILE" pair_succeeds "QR text at a terminal, with the output going to a file" quiet "$WORK/qr" --terminal

# Saying no when pairing again must leave what already works exactly as it was.
REPAIR="$WORK/pair-again"
REPAIR_COMMAND=("$BIN" pair --state-dir "$REPAIR" --host 127.0.0.1 --port PROXY_PORT --name Again)
code_files
if ! run_checked --answer "yes"$'\n' -- "${REPAIR_COMMAND[@]}" --kinds pin,marker --code-file "$WORK/bare" || [ "$RUN_STATUS" != "0" ]; then
    failed "pair again: the first pairing failed: $RUN_OUTPUT"
fi
REPAIR_FILE="$(digest_of "$REPAIR/pairings.json")"
code_files
BEFORE="$(requests)"
if ! run_checked --answer "no"$'\n' -- "${REPAIR_COMMAND[@]}" --kinds pin --code-file "$WORK/bare" || [ "$RUN_STATUS" = "0" ]; then
    failed "pair again: a no should have been refused: $RUN_OUTPUT"
fi
check "pair again: a no sends the receiver no request" test "$(requests)" = "$BEFORE"
check "pair again: a no leaves the pairing file alone" test "$(digest_of "$REPAIR/pairings.json")" = "$REPAIR_FILE"
if ! run_checked --pipe "yes"$'\n' -- "${REPAIR_COMMAND[@]}" --kinds pin --code-file "$WORK/bare" || [ "$RUN_STATUS" = "0" ]; then
    failed "pair again: a bare code through a pipe should have been refused: $RUN_OUTPUT"
fi
not_contacted "pair again: a bare code through a pipe"
check "pair again: a pipe leaves the pairing file alone" test "$(digest_of "$REPAIR/pairings.json")" = "$REPAIR_FILE"
expect_ok "pair again: what was allowed before still works" "$BIN" marker --state-dir "$REPAIR" --host 127.0.0.1 --port "$PORT3" --id again --lat 37.5663 --lon 126.9779

# In a terminal, text typed before the key appears is not an answer, whatever it says.
# NAME, TEXT TYPED EARLY, ANSWER, whether the command should then be "refused" or "accepted", then
# what typed-ahead-check.py takes after those.
typed_ahead() {
    local name="typed early: $1" early="$2" answer="$3" outcome="$4"
    shift 4
    if ! run_checked --early "$early" --answer "$answer" "$@"; then
        failed "$name: $RUN_OUTPUT"
    elif [ "$outcome" = "refused" ] && [ "$RUN_STATUS" = "0" ]; then
        failed "$name: should have been refused, got: $RUN_OUTPUT"
    elif [ "$outcome" = "accepted" ] && [ "$RUN_STATUS" != "0" ]; then
        failed "$name: exited with $RUN_STATUS: $RUN_OUTPUT"
    else
        echo "ok    $name"
    fi
}

STATE_EARLY="$WORK/early-yes"
code_files
typed_ahead "yes, then no, in pair" "yes"$'\n' "no"$'\n' refused -- "$BIN" pair --state-dir "$STATE_EARLY" --host 127.0.0.1 --port PROXY_PORT --name Early --kinds pin --code-file "$WORK/bare"
check "typed early: a no saves no pairing" test ! -e "$STATE_EARLY/pairings.json"
# A line begun before the key appeared, and finished after, is not an answer either.
typed_ahead "ye, then s, in pair" "ye" "s"$'\n' refused -- "$BIN" pair --state-dir "$STATE_EARLY" --host 127.0.0.1 --port PROXY_PORT --name Early --kinds pin --code-file "$WORK/bare"
check "typed early: a yes begun early saves no pairing" test ! -e "$STATE_EARLY/pairings.json"
STATE_EARLY="$WORK/early-return"
typed_ahead "Return, then yes, in pair" $'\n' "yes"$'\n' accepted -- "$BIN" pair --state-dir "$STATE_EARLY" --host 127.0.0.1 --port PROXY_PORT --name Early --kinds pin --code-file "$WORK/bare"
check "typed early: the answer after the key counts" test -e "$STATE_EARLY/pairings.json"
code_files
STATE_EARLY="$WORK/early-half"
typed_ahead "half a word, then yes, in pair" "n" "yes"$'\n' accepted -- "$BIN" pair --state-dir "$STATE_EARLY" --host 127.0.0.1 --port PROXY_PORT --name Early --kinds pin --code-file "$WORK/bare"
check "typed early: a line begun before the key is not part of the answer" test -e "$STATE_EARLY/pairings.json"

# The examples in the README have to compile as printed. Only the values they leave to the reader are supplied.
SNIPPET="$WORK/snippet"
mkdir "$SNIPPET"
python3 -I - "$JAVA_DIR/README.md" "$SNIPPET/Snippet.java" <<'EXTRACT'
import re, sys
text = open(sys.argv[1]).read()
blocks = re.findall(r'```java\n(.*?)```', text, re.S)
pair, write, deadline = blocks[0], blocks[1], blocks[2]
imports = sorted({line for line in (pair + write).splitlines() if line.startswith('import ')})
def body(block):
    return '\n'.join(line for line in block.splitlines() if not line.startswith('import '))
source = '\n'.join(imports) + """
import java.util.concurrent.*;
class Snippet {
    static String codeTheUserTyped = "";
    static byte[] jpegOrPngBytes = new byte[0];
    static void run() throws Exception {
""" + body(pair) + '\n' + body(write) + """
    }
    static void deadline(com.ziorentia.erdigo.sdk.ErdigoClient client, com.ziorentia.erdigo.core.Coordinate spot) throws Exception {
""" + deadline + """
    }
}
"""
open(sys.argv[2], 'w').write(source)
EXTRACT
check "the README has an example of pairing" grep -q 'Pairing.pair(' "$SNIPPET/Snippet.java"
check "the README's example does not take a missing console for a person" grep -q 'if (console == null)' "$SNIPPET/Snippet.java"
expect_ok "the README's examples compile" javac -Xlint:all -Werror -cp "$JAR" -d "$SNIPPET" "$SNIPPET/Snippet.java"

echo
if [ "$FAILURES" -eq 0 ]; then
    echo "All checks passed."
else
    echo "$FAILURES check(s) failed."
    exit 1
fi
