#!/bin/bash
# Runs erdigo-send against its own mock receiver and checks what comes out.
#
#     swift build && macos/Tests/cli-smoke.sh
#
set -u

BIN="$(swift build --show-bin-path)/erdigo-send"
WORK="$(mktemp -d)"
MOCK_PID=""
FAILURES=0

cleanup() {
    [ -n "$MOCK_PID" ] && kill "$MOCK_PID" 2>/dev/null
    rm -rf "$WORK"
}
trap cleanup EXIT

expect_ok() {
    local name="$1"
    shift
    if output="$("$@" 2>&1)"; then
        echo "ok    $name"
    else
        echo "FAIL  $name: $output"
        FAILURES=$((FAILURES + 1))
    fi
}

expect_refused() {
    local name="$1" wanted="$2"
    shift 2
    if output="$("$@" 2>&1)"; then
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

# Keep standard input open, so the mock keeps running and can take commands.
mkfifo "$WORK/commands"
"$BIN" serve --mock --code-file "$WORK/code" <"$WORK/commands" >"$WORK/mock.log" 2>&1 &
MOCK_PID=$!
exec 3>"$WORK/commands"

for _ in $(seq 1 50); do
    grep -q 'Type .help' "$WORK/mock.log" 2>/dev/null && break
    sleep 0.1
done
PORT="$(grep -oE 'port [0-9]+' "$WORK/mock.log" | awk '{print $2}')"
[ -n "$PORT" ] || { echo "FAIL  the mock did not start"; cat "$WORK/mock.log"; exit 1; }

STATE=(--state-dir "$WORK/state")
ADDRESS=(--host 127.0.0.1 --port "$PORT")

check "the mock's code file is private" test "$(stat -f '%Lp' "$WORK/code")" = "600"

expect_refused "a code argument is refused" "not accepted as an argument" \
    "$BIN" pair "${STATE[@]}" "${ADDRESS[@]}" --name Test --kinds pin --code ABCDE
expect_refused "a code given with an equals sign is refused" "not accepted as an argument" \
    "$BIN" pair "${STATE[@]}" "${ADDRESS[@]}" --name Test --kinds pin --code=ABCDE
OUTPUT="$("$BIN" pair "${STATE[@]}" --code=SECRET-CODE 2>&1)"
check "an equals-sign code is not repeated in the error" bash -c '[[ "$0" != *SECRET-CODE* ]]' "$OUTPUT"
OUTPUT="$("$BIN" pair "${STATE[@]}" SECRET-CODE 2>&1)"
check "a bare code is not repeated in the error" bash -c '[[ "$0" != *SECRET-CODE* ]]' "$OUTPUT"
OUTPUT="$("$BIN" pair "${STATE[@]}" --SECRET-CODE 2>&1)"
check "a code written as an option is not repeated" bash -c '[[ "$0" != *SECRET-CODE* ]]' "$OUTPUT"
OUTPUT="$("$BIN" pair "${STATE[@]}" --name x --kinds SECRET-CODE 2>&1)"
check "a code in --kinds is not repeated" bash -c '[[ "$0" != *SECRET-CODE* ]]' "$OUTPUT"
OUTPUT="$("$BIN" SECRET-CODE 2>&1)"
check "a code as a command is not repeated" bash -c '[[ "$0" != *SECRET-CODE* ]]' "$OUTPUT"
expect_refused "--state-dir and --profile together are refused" "cannot be used together" \
    "$BIN" identity "${STATE[@]}" --profile other
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
sed -E 's/.*code=([^&]*).*/\1/' "$WORK/code" >"$WORK/typed-code"
chmod 600 "$WORK/typed-code"
echo "code" >&3
for _ in $(seq 1 50); do
    [ "$(grep -c 'Pairing text written' "$WORK/mock.log")" -ge 2 ] && break
    sleep 0.1
done
sed -E 's/.*code=([^&]*).*/\1/' "$WORK/code" >"$WORK/typed-code"
OUTPUT="$("$BIN" pair --state-dir "$WORK/state-typed" "${ADDRESS[@]}" --name Typed --kinds pin --label typed --code-file "$WORK/typed-code" 2>&1)"
check "a typed code asks for the key to be compared" bash -c '[[ "$0" == *"forget --to typed"* ]]' "$OUTPUT"
check "the state folder is private" test "$(stat -f '%Lp' "$WORK/state")" = "700"
check "the key file is private" test "$(stat -f '%Lp' "$WORK/state/identity.json")" = "600"
check "the pairing file is private" test "$(stat -f '%Lp' "$WORK/state/pairings.json")" = "600"

expect_refused "a label already in use is refused" "already paired under that label" \
    "$BIN" pair "${STATE[@]}" "${ADDRESS[@]}" --name Test --kinds pin --label home --code-file "$WORK/code"

expect_ok "ping" "$BIN" ping "${STATE[@]}"
expect_ok "pin" "$BIN" pin "${STATE[@]}" --id gate --lat 37.5796 --lon 126.977 --title "Palace gate"
expect_ok "negative coordinates" "$BIN" pin "${STATE[@]}" --id opera --lat -33.8568 --lon 151.2153
expect_ok "marker" "$BIN" marker "${STATE[@]}" --id tower --lat 48.8584 --lon 2.2945 --color blue
expect_ok "note" "$BIN" note "${STATE[@]}" --id meet --lat 37.5663 --lon 126.9779 --text "Meet at the gate"
expect_ok "track point" "$BIN" track-add "${STATE[@]}" --id walk --lat 37.5663 --lon 126.9779 --ts 1700000000000 --title Walk
expect_ok "track finish" "$BIN" track-finish "${STATE[@]}" --id walk
expect_ok "move" "$BIN" move "${STATE[@]}" --id gate --lat 37.58 --lon 126.98
expect_ok "delete" "$BIN" delete "${STATE[@]}" --id meet
expect_ok "several sends keep working in sequence" "$BIN" ping "${STATE[@]}" --to home

printf 'not an image' >"$WORK/fake.jpg"
expect_refused "a file that is not an image" "JPEG or PNG" "$BIN" photo "${STATE[@]}" --id p --lat 1 --lon 1 --file "$WORK/fake.jpg"
expect_refused "a bad id" "item id" "$BIN" pin "${STATE[@]}" --id "bad id" --lat 1 --lon 1
expect_refused "a bad latitude" "latitude" "$BIN" pin "${STATE[@]}" --id a --lat 91 --lon 1
expect_refused "NaN" "must be a number" "$BIN" pin "${STATE[@]}" --id a --lat nan --lon 1
expect_refused "delete-all without confirmation" "--yes" "$BIN" delete-all "${STATE[@]}"
expect_refused "an unknown option" "unknown option" "$BIN" ping "${STATE[@]}" --bogus 1
expect_refused "an option for another command" "does not apply" "$BIN" ping "${STATE[@]}" --title x
expect_refused "moving something that is not there" "not_found" "$BIN" move "${STATE[@]}" --id missing --lat 1 --lon 1
expect_refused "an unknown receiver" "no receiver is paired under" "$BIN" ping "${STATE[@]}" --to elsewhere

expect_ok "delete-all" "$BIN" delete-all "${STATE[@]}" --yes

LIST="$("$BIN" list "${STATE[@]}" --redact)"
check "--redact hides the label and key" bash -c '[[ "$0" != *home* && "$0" == *"<redacted>"* ]]' "$LIST"

# The code must never reach the mock's log or this tool's output.
CODE_TEXT="$(sed -E 's/.*code=([^&]*).*/\1/' "$WORK/code")"
check "the code is not in the log" bash -c '! grep -q "$0" "$1"' "$CODE_TEXT" "$WORK/mock.log"

# With --redact the mock keeps the code and the receiver's key out of its output.
expect_refused "--redact without a code file is refused" "--code-file" "$BIN" serve --mock --redact
mkfifo "$WORK/commands2"
"$BIN" serve --mock --redact --code-file "$WORK/code2" <"$WORK/commands2" >"$WORK/mock2.log" 2>&1 &
MOCK2_PID=$!
exec 4>"$WORK/commands2"
for _ in $(seq 1 50); do
    grep -q 'Type .help' "$WORK/mock2.log" 2>/dev/null && break
    sleep 0.1
done
check "the redacted log holds no code" bash -c '! grep -qE "Pairing code|QR text|erdigo-pair" "$0"' "$WORK/mock2.log"
check "the redacted log holds no key" bash -c 'grep -q "Receiver key: <redacted>" "$0"' "$WORK/mock2.log"
kill "$MOCK2_PID" 2>/dev/null
exec 4>&-

# Removing the device on the receiver must lock it out.
# Two devices are paired by now, and removing the first leaves the other as number 1.
echo "remove 1" >&3
echo "remove 1" >&3
sleep 0.5
expect_refused "a removed device gets no answer" "No receiver answered" "$BIN" ping "${STATE[@]}" --retry-for 0

echo
if [ "$FAILURES" -eq 0 ]; then
    echo "All checks passed."
else
    echo "$FAILURES check(s) failed."
    exit 1
fi
