# Erdigo SDK for macOS

A Swift package for writing pins, markers, notes, photos and tracks to an Erdigo receiver on the local network. It
needs macOS 12 or later and has no dependencies beyond the system.

> **Preview.** The released Erdigo app does not yet accept these connections. Until it does, develop against the mock
> receiver described below.

## Try it in two minutes

You need Xcode or the Swift command-line tools. In one terminal, start a mock receiver:

```bash
swift run erdigo-send serve --mock
```

It prints a port, a pairing code and the receiver's key. In another terminal, pair with it. Type the code when asked.
The tool then shows the key it found: if it matches the one the mock printed, type `yes`. The mock is not asked to pair,
and no pairing is saved, until you do.

```bash
swift run erdigo-send pair --host 127.0.0.1 --port PORT --name "My sensor" --kinds pin,track --label test
```

Then send something:

```bash
swift run erdigo-send pin --host 127.0.0.1 --port PORT --id city-hall --lat 37.5663 --lon 126.9779 --title "Seoul City Hall"
```

The mock prints each request it accepts. Type `devices` and then `items 1` in its terminal to see what it holds.

On this computer alone the mock allows every pairing request by default. Start it with `--ask` to answer each one
yourself, as a person would in the app. With `--lan` anyone on the network can reach it, so it asks you about each
request by default. Add `--auto-approve` only for a test that runs unattended on a network you control. A question
nobody can answer, because standard input has ended, is answered no at once.

The mock prints the pairing code and the QR text, which are good for ten minutes. Where its output is saved, captured
or shared, start it with `--code-file FILE` to have them written to a file only you can read instead, and add
`--redact` to hide keys, device names and what items hold. Its log says what each request did, never where an item is
or what it is called; only the `items` command shows that, and not with `--redact`. A pairing question still names
the device and its address, because that is what you decide on, and it goes to standard error so that a saved
standard output does not hold it.

## Use it in your code

```swift
// Package.swift
.package(url: "https://github.com/Ziorentia/Erdigo-sdk.git", branch: "main")

// and in your target's dependencies:
.product(name: "ErdigoSDK", package: "erdigo-sdk")
```

Pair once:

```swift
import ErdigoSDK
import Foundation

let store = try FileDeviceStore(directory: FileDeviceStore.defaultDirectory())
let identity = try store.loadOrCreateIdentity()

// Read this from the keyboard or from a private file. Never take it as a command-line argument.
let ticket = try PairingTicket(parsing: codeTheUserTyped)

let pairing = try await Pairing.pair(
    ticket: ticket,
    identity: identity,
    deviceName: "Greenhouse sensor",
    kinds: [.pin, .track],
    // Asked only for a typed code, which proves only that the other side knows the code.
    confirmKey: { key in
        guard isatty(STDIN_FILENO) == 1, isatty(STDOUT_FILENO) == 1 else { return false } // Nobody could see this and answer.
        tcflush(STDIN_FILENO, TCIFLUSH) // Only an answer given after seeing the key counts.
        print("Receiver key: \(key)")
        print("If this matches the key shown on the receiver, type yes:")
        return readLine()?.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() == "yes"
    }
)
try store.save(pairing)
```

`confirmKey` runs before the receiver hears about this device, so the receiver still shows its key. If it returns false,
`pair` throws `ErdigoError.keyNotConfirmed`, which a real program should catch. The receiver has heard nothing about this
device, and the code stays valid. A typed code cannot pair without `confirmKey`. Text from the QR code carries the
receiver's key, and `Pairing.pair` compares it and stops on a mismatch, so it asks the person nothing.
`PairingTicket(parsing:)` refuses QR text without a valid fingerprint. The `isatty` line makes a pipe or a file count as
a no, because nobody is there to see the key and answer, so a program with no terminal should use the QR text. The
[threat model](../spec/threat-model.md#what-this-does-not-protect-against) explains why this matters.

After that, write:

```swift
let client = ErdigoClient(identity: identity, pairing: pairing, store: store)

let spot = try Coordinate(latitude: 37.5663, longitude: 126.9779)
try await client.putPin(id: "sensor-1", at: spot, title: "Greenhouse")
try await client.move(id: "sensor-1", to: spot)

let point = try TrackPoint(coordinate: spot, timestampMS: 1_700_000_000_000)
try await client.appendTrack(id: "walk", points: [point], title: "Morning walk")
try await client.finishTrack(id: "walk")

try await client.putPhoto(id: "view", at: spot, data: jpegOrPNGData, title: "View")
try await client.putNote(id: "reminder", at: spot, text: "Check the vent.")

try await client.delete(id: "reminder")
try await client.deleteAll()   // Only this device's items.

await client.close()
```

[`Examples/quickstart`](Examples/quickstart/main.swift) is a complete program.

## What the SDK does for you

- **Finds the receiver.** It tries the last known address, then up to eight advertised receivers. Pass
  `target: .address(host:port:)` to skip this.
- **Keeps requests in order.** They run one at a time, and each returns once the receiver acknowledges it. A long
  `appendTrack` and a photo upload are not interleaved with other requests.
- **Survives a sleeping Mac or a closed app.** If the connection fails, it reconnects with growing delays and resends
  with the same sequence number, so nothing is applied twice. Choose how long with `retry:` (`.none`,
  `.upTo(seconds)` or `.forever`). The default is 30 seconds, counted from when the connection was lost.
- **Paces itself** by the limits the receiver announces, and waits when told to slow down. Waiting as told is never
  held against a device.
- **Splits large uploads.** Photos go in chunks, and a long array of track points goes in batches.

## When something goes wrong

Requests throw `ErdigoError`. The one you will meet most is `.noAnswer`. A receiver stays silent when it is off,
asleep or on another network, when a pairing code is wrong or expired, and when a device has been removed. These
cannot be told apart, on purpose, because that silence is what keeps strangers from learning anything.

| Error | Meaning |
|---|---|
| `.noAnswer` | See above. Check the network and that the app is running. The first time, macOS may also be asking for Local Network permission. Allow it and try again. |
| `.notPermitted(kind)` | The person did not allow this kind. Pair again to ask for more. |
| `.rejected(error)` | The receiver refused this request. `error.code` is listed in the [specification](../spec/integration-v1.md#10-errors). |
| `.keyNotConfirmed` | With a typed code, `confirmKey` returned false. The receiver was not asked to pair, and the code still works. |
| `.pairingDenied`, `.pairingTimedOut` | The person declined, or did not answer in time. |
| `.codeReused` | The code was already used. Ask for a new one. |
| `.versionUnsupported` | Update the SDK. |
| `.replaced`, `.sequenceConflict` | Two programs are using one device key. Give each its own key, or its own `--profile` with `erdigo-send`. |

## Where things are kept

`FileDeviceStore` uses `~/Library/Application Support/ErdigoSDK/<profile>/`, with mode 0700 for the folder and 0600
for its files. It refuses to read anything other users could read. To keep the key in the login keychain instead, use
`FileDeviceStore(directory:keyLocation: .keychain(service:account:))` or `erdigo-send --keychain`. The keychain may
ask for confirmation again after a program is rebuilt.

## Putting the SDK in an app

**Local network access.** macOS asks the person for permission the first time. Add `NSLocalNetworkUsageDescription` to
your `Info.plist`, and add `_erdigo-int._tcp` to `NSBonjourServices`.

**App sandbox.** Add the `com.apple.security.network.client` entitlement.

## The `erdigo-send` tool

Run `swift run erdigo-send help` for every command. Add `--redact` to a command when pasting its output into a bug
report. To pair with `--redact`, use the QR text or `--fingerprint`, because a hidden key cannot be compared.

`pair` asks you to compare the receiver's key and type `yes` when it has only a bare code. It asks before the receiver
hears any request, so the receiver still shows its key. At the end of input or on any other answer it exits with an
error. No pairing is saved and the receiver is not asked to pair, although this device's own key may be in the state
folder by then. The receiver never sees that key. With the QR text or `--fingerprint` it compares the key itself and
asks nothing.

Only a person can answer that question, so `pair` refuses a bare code when standard input or output is not a terminal,
as in a pipe or a script. It stops before it contacts the receiver. Use the QR text there, or `--fingerprint` with the
full 64-digit key, which is part of the QR text.

## Tests

```bash
swift test
```

```bash
macos/Tests/cli-smoke.sh
```

The tests use the official Noise vectors and run the SDK against the mock receiver over real TCP, including the cases
that must fail: strangers, replays, reused codes, removed devices, limits and disguised images.

The command-line checks skip the mocks that listen on the local network, because the macOS firewall may ask about
them. Run them with `ERDIGO_LAN_CHECKS=1 macos/Tests/cli-smoke.sh`; CI always does.
