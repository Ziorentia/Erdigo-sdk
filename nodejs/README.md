# Erdigo SDK for Node.js

A library for Node.js 20 or later for writing pins, markers, notes, photos and tracks to an Erdigo receiver on the
local network. It runs on macOS, Windows and Linux, in Node.js and in server runtimes like it. It does not run in a
web browser, because a browser cannot open a TCP connection to the local network, and this protocol has no internet
path.

It speaks the same [protocol](../spec/integration-v1.md) as the [Swift SDK](../swift/README.md) and the
[.NET SDK](../dotnet/README.md), and works with their mock receivers. It has no runtime dependencies: the
cryptography comes from `node:crypto`, the connection from `node:net`, and discovery from `node:dgram`.

To use the SDK with a real map, install Erdigo 2.17.23 or later on a Mac or a PC on the same local network, from the
[Mac App Store](https://apps.apple.com/us/app/erdigo/id6446225102?mt=12) or the
[Microsoft Store](https://apps.microsoft.com/detail/9ndwqg2sdr7x?hl=en-US). The app's minimum and recommended
systems are listed in [System requirements for the app](../README.md#system-requirements-for-the-app).

## Try it in two minutes

You need [Node.js](https://nodejs.org/) 20 or later. This edition has no mock receiver of its own, so start the one
from the .NET SDK (it needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)) or, on a Mac, the
Swift one. In the repository's root folder, in one terminal:

```bash
dotnet run --project dotnet/src/erdigo-send -- serve --mock
```

or

```bash
swift run erdigo-send serve --mock
```

It prints a port, a pairing code and the receiver's key. In another terminal, pair with it. Type the code when asked.
The tool then shows the key it found: if it matches the one the mock printed, type `yes`. The mock is not asked to
pair, and no pairing is saved, until you do.

```bash
node nodejs/bin/erdigo-send.js pair --host 127.0.0.1 --port PORT --name "My sensor" --kinds pin,track --label test
```

Then send something:

```bash
node nodejs/bin/erdigo-send.js pin --host 127.0.0.1 --port PORT --id city-hall --lat 37.5663 --lon 126.9779 --title "Seoul City Hall"
```

The mock prints each request it accepts. Type `devices` and then `items 1` in its terminal to see what it holds.
[`examples/quickstart.js`](examples/quickstart.js) is a complete program: `node nodejs/examples/quickstart.js 127.0.0.1 PORT`.

## Use it in your code

No package is published. Install it from your clone of this repository:

```bash
npm install path/to/Erdigo-sdk/nodejs
```

It is an ES module with type declarations ([`index.d.ts`](index.d.ts)), so TypeScript sees every type.

Pair once:

```js
import { isatty } from 'node:tty';
import { FileDeviceStore, PairingTicket, ReceiverTarget, pair } from 'erdigo-sdk';
import { ask } from 'erdigo-sdk/terminal';

const store = new FileDeviceStore(FileDeviceStore.defaultDirectory());
const identity = store.loadOrCreateIdentity();

// Read this from the keyboard or from a private file. Never take it as a command-line argument.
const ticket = PairingTicket.parse(codeTheUserTyped);

const pairing = await pair({
  target: ReceiverTarget.discover,
  ticket,
  identity,
  deviceName: 'Greenhouse sensor',
  kinds: ['pin', 'track'],
  // Asked only for a typed code, which proves only that the other side knows the code.
  confirmKey: async (key) => {
    if (!isatty(0) || !isatty(1)) {
      return false; // Nobody could see this and answer.
    }
    // ask() discards anything typed before the question appears, because only a later answer counts.
    const answer = await ask([`Receiver key: ${key}`, 'If this matches the key shown on the receiver, type yes: ']);
    return answer?.trim().toLowerCase() === 'yes';
  },
});
store.save(pairing);
```

`confirmKey` runs before the receiver hears about this device, so the receiver still shows its key. Unless it returns
`true`, `pair` rejects with an `ErdigoError` whose `kind` is `keyNotConfirmed`, which a real program should catch. The
receiver has heard nothing about this device, and the code stays valid. A typed code cannot pair without `confirmKey`.
Once the person has confirmed the key, a pairing connection that meets silence is tried up to three times, half a second
and then a second apart, since the receiver may still be closing the connection that showed the key: nothing about this
device is sent until the receiver has proven the code and its key matches the one confirmed, so trying again is safe.
Text from the QR code carries the receiver's key, and `pair` compares it and stops on a mismatch, so it asks the person
nothing. `PairingTicket.parse` refuses QR text without a valid fingerprint. The terminal check makes a pipe or a file
count as a no, because nobody is there to see the key and answer, so a program with no terminal should use the QR text.
The [threat model](../spec/threat-model.md#what-this-does-not-protect-against) explains why this matters.

After that, write:

```js
import { ErdigoClient } from 'erdigo-sdk';

const client = new ErdigoClient({ identity, pairing, store });

const spot = { lat: 37.5663, lon: 126.9779 };
await client.putPin('sensor-1', spot, { title: 'Greenhouse' });
await client.move('sensor-1', spot);

await client.appendTrack('walk', [{ ...spot, ts: 1_700_000_000_000 }], { title: 'Morning walk' });
await client.finishTrack('walk');

await client.putPhoto('view', spot, jpegOrPngBytes, { title: 'View' });
await client.putNote('reminder', spot, 'Check the vent.');

await client.delete('reminder');
await client.deleteAll(); // Only this device's items.

client.close();
```

Every method takes a `signal` in its options. A stopped request rejects with the signal's reason. The `erdigo-sdk/terminal`
helpers read a code without showing it and ask a question at the terminal; they are what `erdigo-send` uses.

## What the SDK does for you

- **Finds the receiver.** It tries the last known address, then up to eight receivers advertised on the local
  network. Pass `ReceiverTarget.address(host, port)` to skip this.
- **Keeps requests in order.** They run one at a time, in the order they were made, and each resolves once the
  receiver acknowledges it. A long `appendTrack` and a photo upload are not interleaved with other requests.
- **Survives a sleeping computer or a closed app.** If the connection fails, it reconnects with growing delays and
  resends with the same sequence number, so nothing is applied twice. Choose how long with `retry`
  (`RetryPolicy.none`, `RetryPolicy.upTo(ms)` or `RetryPolicy.forever`). The default is 30 seconds, counted from
  when the connection was lost. See [How long a request can take](#how-long-a-request-can-take).
- **Decides whether an answer ends the connection before anything else.** An error marked `fatal`, `paused` and
  `replaced` end the connection, so the client never waits and resends on a connection the receiver has closed, even
  for a fatal `rate_limited`. An answer about another request's number is a protocol violation unless it ends the
  connection, and its numbers are not taken.
- **Paces itself** by the limits the receiver announces, and waits when told to slow down. Waiting as told is never
  held against a device.
- **Splits large uploads.** Photos go in chunks of 48000 bytes, and a long list of track points goes in batches of 100.

## How long a request can take

The retry policy limits each outage, not the request as a whole. Its time counts from when the connection was lost
and starts again once a connection is made, and time spent connected, such as waiting to stay within the receiver's
limits, never counts. So a receiver that keeps accepting connections and losing them before it answers keeps a
request going past 30 seconds, and the requests behind it wait. When a request must end within a fixed time, give it
a deadline:

```js
try {
  await client.putPin('sensor-1', spot, { title: 'Greenhouse', signal: AbortSignal.timeout(120_000) });
} catch (error) {
  if (error.name === 'TimeoutError') {
    // Not confirmed in time. See below.
  }
}
```

Stopping a request that is waiting its turn frees its place at once, and the ones behind it go ahead. A request
stopped after it was sent may already have been applied, because the receiver may have acted on it before the answer
was lost. Sending it again is safe for the `put…` methods, `move` and the deletes, which give the same result when
repeated. `appendTrack` would add its points a second time, and a long one may have sent some of its batches already.

## When something goes wrong

Requests reject with an `ErdigoError`, and its `kind` says what happened. The one you will meet most is `noAnswer`. A
receiver stays silent when it is off, asleep or on another network, when a pairing code is wrong or expired, and when
a device has been removed. These cannot be told apart, on purpose, because that silence is what keeps strangers from
learning anything.

| `kind` | Meaning |
|---|---|
| `noAnswer` | See above. Check the network and that the app is running. |
| `notPermitted` | The person did not allow this kind (`itemKind`). Pair again to ask for more. |
| `rejected` | The receiver refused this request. `receiverError.code` is listed in the [specification](../spec/integration-v1.md#10-errors). |
| `keyNotConfirmed` | With a typed code, `confirmKey` did not return `true`. The receiver was not asked to pair, and the code still works. |
| `fingerprintMismatch` | The receiver's key is not the one in the QR text or `--fingerprint`. Pairing stopped before the receiver heard a request. |
| `pairingDenied`, `pairingTimedOut` | The person declined, or did not answer in time. |
| `codeReused` | The code was already used. Ask for a new one. |
| `versionUnsupported` | Update the SDK. |
| `replaced`, `sequenceConflict` | Two programs are using one device key. Give each its own key, or its own `--profile` with `erdigo-send`. |
| `invalidArgument` | An id, coordinate, text or photo broke a rule of the protocol. Nothing was sent. |
| `storage` | Stored state could not be used. `storageProblem` says why. A device key that exists but cannot be read is never replaced. |

## Where things are kept

`FileDeviceStore` keeps one identity and its pairings in a folder of its own: the device key in `identity.key` and
the pairings and connection counters in `pairings.json`. Every file is written through a temporary file and a rename,
so a crash never leaves half a file, and a lock file keeps two processes from using the folder at the same moment.

| System | Folder |
|---|---|
| macOS | `~/Library/Application Support/ErdigoSDK.Node/<profile>/` |
| Linux | `$XDG_DATA_HOME/ErdigoSDK.Node/<profile>/`, or `~/.local/share/ErdigoSDK.Node/<profile>/` |
| Windows | `%LOCALAPPDATA%\ErdigoSDK.Node\<profile>\` |

The folder's name is this edition's own, so it never shares a device key with the Swift or .NET SDK.

**On macOS and Linux** the folder has mode 0700 and its files mode 0600, and the store refuses a folder or file that
is a link, belongs to someone else, or that others could read or replace, the way ssh treats its keys.

**On Windows** Node.js cannot read or change an access list without a native module, and this SDK has none. When the
store creates its folder, it removes everything the folder inherits and grants the current user full control, using
the `icacls` tool that comes with Windows. SYSTEM and the Administrators group, who control the computer anyway, can
keep access, as the .NET SDK allows; on the CI runners they do. It does not check the access list of a folder that already exists, or of
the files in it. The key is not encrypted: unlike the .NET SDK, which uses DPAPI, this edition keeps it in a file that
the folder's access list protects. A code file given to `erdigo-send pair --code-file` must be inside your own
profile folder (`%USERPROFILE%`), which by default only you, SYSTEM and administrators can read, because the tool
cannot check a file's access list itself.

The store tells these apart, and never makes a new key over an old one:

| Key file | What happens |
|---|---|
| Missing | A new key is made and saved. |
| Damaged (not a key) | `StorageProblem.identityDamaged`. The file is left as it is. |
| Unreadable (no permission) | `StorageProblem.identityUnreadable`. The file is left as it is. |
| Readable by others, or a link (macOS and Linux) | `StorageProblem.insecure`. Nothing is read. |

To start again with a new identity, move the key file away yourself. A new key means pairing again with every receiver.

### What this does not protect against

- **any program running as you**, or an administrator. They can read the key file.
- **a backup** of the folder, which holds the key as it is.
- **memory.** JavaScript gives no guarantee that a key is wiped from memory. The SDK clears the buffers it holds when
  it is done with them and keeps the private key out of the identity's visible properties, but `node:crypto` and the
  garbage collector keep short-lived copies, and a crash dump or the swap file may hold one.

If any of these matters, run the device's program under an account of its own.

## What is in a release

Nothing in this package is secret: there are no embedded keys, no debug back doors and no hidden switches. The one
test hook, which lets the tests reproduce the published vectors with fixed ephemeral keys, is an option of the
handshake class that the SDK never passes. No option, setting or environment variable reaches it.

## Dependencies, and why

None at run time, and none for the tests, which use `node:test`. Everything comes from Node.js itself:

| Module | Used for |
|---|---|
| `node:crypto` | X25519 (`diffieHellman`), ChaCha20-Poly1305, SHA-256, HMAC and HKDF. Node.js builds these on OpenSSL. |
| `node:net` | The TCP connection to the receiver. |
| `node:dgram`, `node:os` | Discovery. |
| `node:fs` | The device store and the files the tool reads. |
| `node:tty` | Reading a code and an answer from the terminal. |

The Noise handshakes (`Noise_XXpsk0_25519_ChaChaPoly_SHA256` for pairing and `Noise_IK_25519_ChaChaPoly_SHA256` for
connecting) are written here, in [`src/noise.js`](src/noise.js), on top of those primitives, and checked against the
official vectors. JSON from a receiver is read by a strict parser of this repository, because `JSON.parse` keeps the
last of two duplicate keys and accepts lone surrogates.

Discovery sends one-shot multicast DNS queries ([RFC 6762, section 5.1](https://www.rfc-editor.org/rfc/rfc6762#section-5.1))
from an ordinary port and reads the answers with a small, bounds-checked DNS reader in this repository. Receivers
answer such a query directly, so it needs neither port 5353 nor Bonjour, and it works the same on every system. It is
used only when no address is given.

## Differences from the Swift and .NET SDKs

- There is no mock receiver. `erdigo-send serve` says so and names the .NET and Swift mocks.
- The device key is kept in a file on every system, as described above. There is no keychain or DPAPI option, so
  `erdigo-send --keychain` is refused.
- On macOS and Linux, `erdigo-send pair` reads a typed code, and the answer to the question about the key, from the
  terminal itself (`/dev/tty`), as the Swift tool does. On Windows it reads them from the console, so standard input
  must be the console. Either way a code file can be used.
- Before a question, typed-ahead text is discarded. On macOS and Linux this reads and drops whatever is waiting at the
  terminal. On Windows, where a program cannot see what is waiting, whatever arrives in the first 150 milliseconds after
  the terminal is opened is dropped.
- `confirmKey` must return `true` (or a promise of it) to count as a yes. Any other value is a no.
- Public methods return promises and take an `AbortSignal`, as is usual in JavaScript. Times are in milliseconds.

## The `erdigo-send` tool

Run `node nodejs/bin/erdigo-send.js help` for every command. In a project that installed the package, it is also
`npx erdigo-send`. Add `--redact` to a command when pasting its output into a bug report. To pair with `--redact`, use the
QR text or `--fingerprint`, because a hidden key cannot be compared.

`pair` asks you to compare the receiver's key and type `yes` when it has only a bare code. It asks before the receiver
hears any request, so the receiver still shows its key. At the end of input or on any other answer it exits with an
error. No pairing is saved and the receiver is not asked to pair, although this device's own key may be in the state
folder by then. The receiver never sees that key. With the QR text or `--fingerprint` it compares the key itself and
asks nothing.

Only a person can answer that question, so `pair` refuses a bare code when standard input or output is not a terminal,
as in a pipe or a script. It stops before it contacts the receiver. Use the QR text there, or `--fingerprint` with the
full 64-digit key, which is part of the QR text.

A code file must be readable by you alone. On macOS and Linux give it mode 0600. On Windows keep it in your own
profile folder.

`note --text-file` reads the note from a UTF-8 file. It takes out what editors such as Notepad add: a byte order
mark at the start, and the carriage return of each Windows line ending (CR LF becomes LF). Nothing else is changed.
A carriage return on its own is refused before anything is sent, and the 4096-byte limit counts the note as it is
sent.

## Cloud relay example

The app takes connections only from its own local network. To send to it from the internet, for example from a drone
on a mobile network, [`examples/cloud-relay/`](examples/cloud-relay/) has two programs, written with Node.js and this
SDK and nothing else:

- **`relay.js`** runs on a server that can be reached from the internet, behind an HTTPS reverse proxy. Devices send
  operations to it, and it keeps them in memory until the gateway is done with them, or for `--keep-hours` at most.
- **`gateway.js`** runs on a computer on the same local network as the app. It fetches from the relay with outgoing
  HTTPS requests, and writes to the app as a paired device.

Run the relay with two tokens, each 32 to 256 characters from `A`-`Z`, `a`-`z`, `0`-`9`, `-`, `_`, `.` and `~`:

```bash
ERDIGO_RELAY_SOURCE_TOKENS=SOURCE_TOKEN ERDIGO_RELAY_GATEWAY_TOKEN=GATEWAY_TOKEN \
  node nodejs/examples/cloud-relay/relay.js --listen 127.0.0.1:8080
```

Pair the gateway once. It asks for the code or the QR text, as `erdigo-send pair` does, or reads it from a file only
you can read with `--code-file`:

```bash
node nodejs/examples/cloud-relay/gateway.js pair --name "Cloud gateway"
```

Then run it:

```bash
ERDIGO_RELAY_TOKEN=GATEWAY_TOKEN node nodejs/examples/cloud-relay/gateway.js run --relay https://relay.example.com
```

To try both on one computer with a mock receiver, add `--host 127.0.0.1 --port PORT` to both gateway commands and
give `run` the relay's `http://127.0.0.1:8080`. With `--once`, `run` stops when nothing more is waiting. The gateway
keeps its key, its pairing and its place in the relay's queue in the `cloud-gateway` profile, in the folder shown in
[Where things are kept](#where-things-are-kept), or in `--state-dir`. A token typed in front of a command stays in the
shell's history, so on a server keep it in a file only you can read. [`docs/cloud-relay.md`](../docs/cloud-relay.md)
has everything else: the HTTP API, what the gateway prints and its exit codes, security, and deployment.

While the app is out of reach, the SDK keeps the operation and sends the same request again with the same sequence
number (`RetryPolicy.forever`, or `RetryPolicy.upTo(30_000)` with `--once`), so the app applies it once even if its
answer was lost. The SDK counts its 30 seconds from the last connection it made, so an app that takes every connection
but never answers, or whose answers are all lost, would keep it trying. With `--once` the gateway therefore also counts,
from the first sign of trouble with an operation, and exits with code 3 if the app has not taken it 30 seconds later.
The signs are a try that failed, and the connection going away while the operation is being sent, which the gateway
sees in `client.limits`: each connection brings its own limits. Time the SDK spends pacing a track's points before it
sends is not counted, so a slow track is not given up. An app that never answers shows trouble only when the SDK stops
waiting for the answer, after 15 seconds, so that case takes about 45 seconds. The SDK does not say when it tries next,
so the gateway prints `No answer from the Erdigo app; trying again.` without a time, then at most once a minute.

Only one `run` at a time may use a profile, because two gateways with one key would take each other's connection.
Node.js has no file locks, so while `run` uses a profile it keeps a file only you can read, `.gateway.lock`, holding its
process id, in the profile's folder, and removes it when it stops. Another `run` on that profile exits with code 1 and
names that process. A lock whose process has ended, for example because the gateway was killed, is taken over. A lock
that is a link, or that someone else could replace, is refused. If a process id left in a lock has since gone to
another program, remove the file.

What was checked:

- `tests/cloud-relay.test.js` runs the relay against every case in
  [`docs/cloud-relay-cases.json`](../docs/cloud-relay-cases.json), and the rest of the API: each token's role, the order
  of 404, 405 and 401, sizes and limits, epochs, expiry with a clock of its own, paging, and waiting for operations.
- `tests/cloud-gateway.test.js` runs the gateway against that relay and this edition's test receiver: every kind of
  operation, nothing applied twice, an answer lost on the way back (the same request is sent again and applied once),
  refusals, an app or a relay that cannot be reached or does not answer (status 3 after about 30 seconds), every answer
  from the app lost on the way and an app that takes the connection and never answers (status 3 after about 30 and 45
  seconds, the operation applied once and still on the relay), a track the app paces for 35 seconds (not given up), a
  second `run` on a state folder in use (status 1), a lock left by a process that has ended, a lock that is a link, a
  restarted relay, expired operations, and relay answers that break the API (status 2).
- `tests/cloud-gateway-https.test.js` puts the relay behind a small TLS proxy, with a certificate it makes with the
  `openssl` tool, and checks that the gateway fetches over HTTPS and refuses a certificate it cannot check.
- `tests/cloud-gateway-mock.test.js` and `tests/cloud-relay-smoke.sh` run both programs, as above, with another
  edition's mock. The script also starts a second `run` on the profile of one that is running. CI runs every one of
  these with the .NET mock, on Linux and Windows with Node.js 20 and 22 (the script on Linux only). They were also run
  by hand on a Mac with Node.js 25, with both the .NET and the Swift mock.

Not checked: the released Erdigo app; a real reverse proxy such as Caddy or nginx, and a certificate from a real
authority; finding the app without `--host`; typing a code for `gateway pair`; stopping the programs with Ctrl+C on
Windows; and running for days. Neither program has had a security audit.

## Layout

| Path | Contents |
|---|---|
| `src/noise.js`, `src/json.js`, `src/messages.js`, `src/model.js`, `src/protocol.js`, `src/stream.js` | The wire format, strict JSON, framing and the Noise handshakes |
| `src/identity.js`, `src/store.js`, `src/private-file.js` | The device key, errors and the device store |
| `src/pairing.js`, `src/client.js`, `src/discovery.js`, `src/dns.js` | Pairing, the client and discovery |
| `src/cli/` | The command-line tool and the terminal helpers |
| `bin/erdigo-send.js` | The command-line tool's entry point |
| `examples/quickstart.js` | A complete small program |
| `examples/cloud-relay/` | The [cloud relay example](#cloud-relay-example): `relay.js`, `gateway.js` and their code in `lib/` |
| `index.d.ts`, `terminal.d.ts` | Type declarations |
| `tests/*.test.js` | Unit, vector, end-to-end and interoperability tests |
| `tests/cli-smoke.sh` | Checks of the command-line tool and the example, on macOS and Linux |
| `tests/cloud-relay-smoke.sh` | The cloud relay example run as its section shows, on macOS and Linux |

## Tests

```bash
cd nodejs && node --test
```

```bash
dotnet build dotnet/Erdigo.sln && nodejs/tests/cli-smoke.sh
```

`node --test` runs the official Noise vectors and every integration-v1 vector, byte for byte, and runs the SDK over
real TCP against a small receiver in the tests that can give any answer, including the ones the mocks would not give
by themselves: a fatal `rate_limited`, an answer about another request's number, a receiver that never stops limiting.
Where this edition adds a defence, its test was run without the defence and seen to fail.

`tests/interop.test.js` pairs with, and writes every item kind to, another edition's mock over real TCP. It runs when
`ERDIGO_MOCK` names that edition's `erdigo-send`:

```bash
ERDIGO_MOCK='["../dotnet/src/erdigo-send/bin/Debug/net8.0/erdigo-send"]' node --test tests/interop.test.js
ERDIGO_MOCK='["../.build/debug/erdigo-send"]' node --test tests/interop.test.js
```

With `ERDIGO_MULTICAST_TESTS=1` it also finds a mock that advertises on the local network, over real multicast.

What was checked, and where:

- **CI**, on `ubuntu-latest` and `windows-latest` with Node.js 20 and 22: every test, and interoperability with the
  .NET mock. On Linux also the command-line checks against the .NET mock, through pseudo terminals.
- **By hand on a Mac** (macOS 27, Apple silicon, Node.js 25): every test, interoperability with both the .NET and the
  Swift mock, discovery over real multicast with both, and the command-line checks against the .NET mock.

Not checked:

- **The real Erdigo app**, which accepts these connections from version 2.17.23. This SDK has so far been checked
  against the mock receivers only.
- **Typing a code or an answer on Windows.** The command-line checks need pseudo terminals, which Windows does not
  have, so the Windows console path was not exercised.
- **Discovery on Windows and in CI**, which depends on the network passing multicast.
- **Other runtimes** such as Bun or Deno, and **a Raspberry Pi** or another ARM board.
- **No independent security audit has been done.**
