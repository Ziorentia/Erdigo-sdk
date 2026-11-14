# Erdigo SDK for Node.js

A library for Node.js 20 or later for writing pins, markers, notes, photos and tracks to an Erdigo receiver on the
local network. It runs on macOS, Windows and Linux, in Node.js and in server runtimes like it. It does not run in a
web browser, because a browser cannot open a TCP connection to the local network, and this protocol has no internet
path.

It speaks the same [protocol](../spec/integration-v1.md) as the [macOS SDK](../macos/README.md) and the
[.NET SDK](../windows/README.md), and works with their mock receivers. It has no runtime dependencies: the
cryptography comes from `node:crypto`, the connection from `node:net`, and discovery from `node:dgram`.

> **In store review.** Erdigo 2.17.23, the first version of the app that accepts these connections, is waiting for
> review on the Mac App Store and the Microsoft Store. Until it is released, develop against a mock receiver as
> described below. This notice will be removed once the review is complete.

To use the SDK with a real map, install Erdigo 2.17.23 or later on a Mac or a PC on the same local network, from the
[Mac App Store](https://apps.apple.com/us/app/erdigo/id6446225102?mt=12) or the
[Microsoft Store](https://apps.microsoft.com/detail/9ndwqg2sdr7x?hl=en-US). The app's minimum and recommended
systems are listed in [System requirements for the app](../README.md#system-requirements-for-the-app).

## Try it in two minutes

You need [Node.js](https://nodejs.org/) 20 or later. This edition has no mock receiver of its own, so start the one
from the .NET SDK (it needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)) or, on a Mac, the
Swift one. In the repository's root folder, in one terminal:

```bash
dotnet run --project windows/src/erdigo-send -- serve --mock
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
receiver has heard nothing about this device, and the code stays valid. A typed code cannot pair without
`confirmKey`. Text from the QR code carries the receiver's key, and `pair` compares it and stops on a mismatch, so it
asks the person nothing. `PairingTicket.parse` refuses QR text without a valid fingerprint. The terminal check makes a
pipe or a file count as a no, because nobody is there to see the key and answer, so a program with no terminal should
use the QR text. The [threat model](../spec/threat-model.md#what-this-does-not-protect-against) explains why this
matters.

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
store creates its folder, it gives it an access list that grants the current user alone, with nothing inherited, using
the `icacls` tool that comes with Windows. It does not check the access list of a folder that already exists, or of
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

## Differences from the macOS and .NET SDKs

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

## Layout

| Path | Contents |
|---|---|
| `src/noise.js`, `src/json.js`, `src/messages.js`, `src/model.js`, `src/protocol.js`, `src/stream.js` | The wire format, strict JSON, framing and the Noise handshakes |
| `src/identity.js`, `src/store.js`, `src/private-file.js` | The device key, errors and the device store |
| `src/pairing.js`, `src/client.js`, `src/discovery.js`, `src/dns.js` | Pairing, the client and discovery |
| `src/cli/` | The command-line tool and the terminal helpers |
| `bin/erdigo-send.js` | The command-line tool's entry point |
| `examples/quickstart.js` | A complete small program |
| `index.d.ts`, `terminal.d.ts` | Type declarations |
| `tests/*.test.js` | Unit, vector, end-to-end and interoperability tests |
| `tests/cli-smoke.sh` | Checks of the command-line tool and the example, on macOS and Linux |

## Tests

```bash
cd nodejs && node --test
```

```bash
dotnet build windows/Erdigo.sln && nodejs/tests/cli-smoke.sh
```

`node --test` runs the official Noise vectors and every integration-v1 vector, byte for byte, and runs the SDK over
real TCP against a small receiver in the tests that can give any answer, including the ones the mocks would not give
by themselves: a fatal `rate_limited`, an answer about another request's number, a receiver that never stops limiting.
Where this edition adds a defence, its test was run without the defence and seen to fail.

`tests/interop.test.js` pairs with, and writes every item kind to, another edition's mock over real TCP. It runs when
`ERDIGO_MOCK` names that edition's `erdigo-send`:

```bash
ERDIGO_MOCK='["../windows/src/erdigo-send/bin/Debug/net8.0/erdigo-send"]' node --test tests/interop.test.js
ERDIGO_MOCK='["../.build/debug/erdigo-send"]' node --test tests/interop.test.js
```

With `ERDIGO_MULTICAST_TESTS=1` it also finds a mock that advertises on the local network, over real multicast.

What was checked, and where:

- **CI**, on `ubuntu-latest` and `windows-latest` with Node.js 20 and 22: every test, and interoperability with the
  .NET mock. On Linux also the command-line checks against the .NET mock, through pseudo terminals.
- **By hand on a Mac** (macOS 27, Apple silicon, Node.js 25): every test, interoperability with both the .NET and the
  Swift mock, discovery over real multicast with both, and the command-line checks against the .NET mock.

Not checked:

- **The released Erdigo app**, which does not yet accept these connections.
- **Typing a code or an answer on Windows.** The command-line checks need pseudo terminals, which Windows does not
  have, so the Windows console path was not exercised.
- **Discovery on Windows and in CI**, which depends on the network passing multicast.
- **Other runtimes** such as Bun or Deno, and **a Raspberry Pi** or another ARM board.
- **No independent security audit has been done.**
