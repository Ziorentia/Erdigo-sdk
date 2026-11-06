# Erdigo SDK for macOS

A Swift package for writing pins, markers, notes, photos and tracks to an Erdigo receiver on the local network. It
needs macOS 12 or later and has no dependencies beyond the system.

> **Preview.** The Erdigo app does not yet accept these connections. Until it does, develop against the mock
> receiver described below.

## Try it in two minutes

You need Xcode or the Swift command-line tools. In one terminal, start a mock receiver:

```bash
swift run erdigo-send serve --mock
```

It prints a port and a pairing code. In another terminal, pair with it and send something:

```bash
swift run erdigo-send pair --host 127.0.0.1 --port PORT --name "My sensor" --kinds pin,track --label test
```

```bash
swift run erdigo-send pin --host 127.0.0.1 --port PORT --id city-hall --lat 37.5663 --lon 126.9779 --title "Seoul City Hall"
```

The mock prints each request it accepts. Type `devices` and then `items 1` in its terminal to see what it holds.

The mock allows every pairing request by default. Start it with `--ask` to answer each one yourself, as a person
would in the app.

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

let store = try FileDeviceStore(directory: FileDeviceStore.defaultDirectory())
let identity = try store.loadOrCreateIdentity()

// Read this from the keyboard or from a private file. Never take it as a command-line argument.
let ticket = try PairingTicket(parsing: codeTheUserTyped)

let pairing = try await Pairing.pair(
    ticket: ticket,
    identity: identity,
    deviceName: "Greenhouse sensor",
    kinds: [.pin, .track]
)
try store.save(pairing)
```

If the code was typed rather than scanned from the QR code, show the person
`IntegrationV1.shortFingerprint(pairing.fingerprint)` and ask them to compare it with the key on the receiver's screen.
A typed code proves only that the other side knows the code. The
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
report.

## Tests

```bash
swift test
```

```bash
macos/Tests/cli-smoke.sh
```

The tests use the official Noise vectors and run the SDK against the mock receiver over real TCP, including the cases
that must fail: strangers, replays, reused codes, removed devices, limits and disguised images.
