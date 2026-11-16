# Erdigo SDK for Java

A Java library for writing pins, markers, notes, photos and tracks to an Erdigo receiver on the local network. It needs
Java 17 or later and runs where Java does: Windows, macOS and Linux. It is for the local network only; there is no
internet path.

It speaks the same [protocol](../spec/integration-v1.md) as the [Swift](../swift/README.md) and
[.NET](../dotnet/README.md) SDKs, and was checked against both of their mock receivers. It has no mock receiver of
its own: develop against the .NET or Swift one, as described below.

**Android has not been tested.** The library uses only the JDK, but it has not been built or run on Android, and its
key storage and terminal handling assume a desktop or server system.

To use the SDK with a real map, install Erdigo 2.17.23 or later on a Mac or a Windows PC on the same local network: see
[Get the Erdigo app](../README.md#get-the-erdigo-app).

## Try it in two minutes

You need a JDK, version 17 or later. Nothing else is installed: the Maven wrapper in this folder downloads Maven 3.9.14
and checks its SHA-256 before using it. In the `java` folder, build the library and the tool:

```bash
./mvnw -B package
```

On Windows, use `mvnw.cmd` in place of `./mvnw`. This builds `target/erdigo-send.jar` and runs the tests, which read
the shared vectors in `spec/`, so use a full clone.

You also need a mock receiver from another SDK in this repository. With the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0),
start one from the `dotnet` folder in another terminal (on a Mac, `swift run erdigo-send serve --mock` in the
repository's root does the same):

```bash
dotnet run --project src/erdigo-send -- serve --mock
```

It prints a port, a pairing code and the receiver's key. Pair with it. Type the code when asked. The tool then shows
the key it found: if it matches the one the mock printed, type `yes`. The mock is not asked to pair, and no pairing is
saved, until you do.

```bash
java -jar target/erdigo-send.jar pair --host 127.0.0.1 --port PORT --name "My sensor" --kinds pin,track --label test
```

Then send something:

```bash
java -jar target/erdigo-send.jar pin --host 127.0.0.1 --port PORT --id city-hall --lat 37.5663 --lon 126.9779 --title "Seoul City Hall"
```

The mock prints each request it accepts. Type `devices` and then `items 1` in its terminal to see what it holds.

## Use it in your code

No package is published, and none will be put on Maven Central for now. Build the jar as above and put
`target/erdigo-send.jar` on your class path, or copy `src/main/java` into your project. The library has no
dependencies.

Pair once:

```java
import com.ziorentia.erdigo.core.ItemKind;
import com.ziorentia.erdigo.core.PairingTicket;
import com.ziorentia.erdigo.sdk.DeviceIdentity;
import com.ziorentia.erdigo.sdk.FileDeviceStore;
import com.ziorentia.erdigo.sdk.Pairing;
import com.ziorentia.erdigo.sdk.ReceiverPairing;
import com.ziorentia.erdigo.sdk.ReceiverTarget;
import java.io.Console;
import java.util.List;

FileDeviceStore store = new FileDeviceStore(FileDeviceStore.defaultDirectory());
DeviceIdentity identity = store.loadOrCreateIdentity();

// Read this from the keyboard or from a private file. Never take it as a command-line argument.
PairingTicket ticket = PairingTicket.parse(codeTheUserTyped);

ReceiverPairing pairing = Pairing.pair(
        ReceiverTarget.discover(),
        ticket,
        identity,
        "Greenhouse sensor",
        List.of(ItemKind.PIN, ItemKind.TRACK),
        new Pairing.Options().label("home").confirmKey(key -> {
            // Asked only for a typed code, which proves only that the other side knows the code.
            Console console = System.console();
            if (console == null) {
                return false; // Nobody could see this and answer.
            }
            console.printf("Receiver key: %s%nIf this matches the key shown on the receiver, type yes: ", key);
            String answer = console.readLine();
            return answer != null && answer.trim().equalsIgnoreCase("yes");
        }));
store.save(pairing);
```

`confirmKey` runs before the receiver hears about this device, so the receiver still shows its key. If it returns
false, `pair` throws an `ErdigoException` whose `kind()` is `KEY_NOT_CONFIRMED`, which a real program should catch.
The receiver has heard nothing about this device, and the code stays valid. A typed code cannot pair without
`confirmKey`. Text from the QR code carries the receiver's key, and `pair` compares it and stops on a mismatch, so it
asks the person nothing. `PairingTicket.parse` refuses QR text without a valid fingerprint. Before Java 22,
`System.console()` is null when standard input or output is not a terminal, so a pipe or a file counts as a no; from
Java 22 check `console.isTerminal()` as well. Unlike the `erdigo-send` tool, this short example does not discard keys
typed before the question appeared. The [threat model](../spec/threat-model.md#what-this-does-not-protect-against)
explains why all this matters.

After that, write:

```java
import com.ziorentia.erdigo.core.Coordinate;
import com.ziorentia.erdigo.core.TrackPoint;
import com.ziorentia.erdigo.sdk.ErdigoClient;

try (ErdigoClient client = new ErdigoClient(identity, pairing, store)) {
    Coordinate spot = new Coordinate(37.5663, 126.9779);
    client.putPin("sensor-1", spot, "Greenhouse");
    client.move("sensor-1", spot);

    client.appendTrack("walk", List.of(new TrackPoint(spot, 1_700_000_000_000L, null)), "Morning walk");
    client.finishTrack("walk");

    client.putPhoto("view", spot, jpegOrPngBytes, "View");
    client.putNote("reminder", spot, "Check the vent.");

    client.delete("reminder");
    client.deleteAll(); // Only this device's items.
}
```

## What the SDK does for you

- **Finds the receiver.** It tries the last known address, then up to eight receivers advertised on the local
  network. Pass `ReceiverTarget.address(host, port)` to skip this.
- **Keeps requests in order.** They run one at a time, in the order they were made, and each method returns once the
  receiver acknowledges it. A long `appendTrack` and a photo upload are not interleaved with other requests.
- **Survives a sleeping computer or a closed app.** If the connection fails, it reconnects with growing delays and
  resends with the same sequence number, so nothing is applied twice. Choose how long with a `RetryPolicy`
  (`none()`, `upTo(time)` or `forever()`). The default is 30 seconds, counted from when the connection was lost.
- **Paces itself** by the limits the receiver announces, and waits when told to slow down. Waiting as told is never
  held against a device. An answer that ends the connection is never waited on as a rate limit.
- **Splits large uploads.** Photos go in chunks of 48000 bytes, and a long list of track points goes in batches of 100.

## How long a request can take

The retry policy limits each outage, not the request as a whole. Its time counts from when the connection was lost
and starts again once a connection is made, and time spent connected, such as waiting to stay within the receiver's
limits, never counts. Every method blocks. When a request must end within a fixed time, run it on a thread of its own
and interrupt that thread:

```java
ExecutorService worker = Executors.newSingleThreadExecutor();
Future<?> sent = worker.submit(() -> client.putPin("sensor-1", spot, "Greenhouse"));
try {
    sent.get(2, TimeUnit.MINUTES);
} catch (TimeoutException notConfirmed) {
    sent.cancel(true); // Interrupts the request, which throws ErdigoException with kind() INTERRUPTED.
}
```

An interrupted request throws `ErdigoException` with `kind()` `INTERRUPTED` and leaves the thread's interrupt flag
set. A request interrupted after it was sent may already have been applied, because the receiver may have acted on it
before the answer was lost. Sending it again is safe for the `put…` methods, `move` and the deletes, which give the same
result when repeated. `appendTrack` would add its points a second time, and a long one may have sent some of its
batches already.

## When something goes wrong

Methods throw `ErdigoException`, an unchecked exception, and its `kind()` says what happened. The one you will meet
most is `NO_ANSWER`. A receiver stays silent when it is off, asleep or on another network, when a pairing code is
wrong or expired, and when a device has been removed. These cannot be told apart, on purpose, because that silence is
what keeps strangers from learning anything.

| `kind()` | Meaning |
|---|---|
| `NO_ANSWER` | See above. Check the network and that the app is running. |
| `NOT_PERMITTED` | The person did not allow this kind (`itemKind()`). Pair again to ask for more. |
| `REJECTED` | The receiver refused this request. `receiverError().code()` is listed in the [specification](../spec/integration-v1.md#10-errors). |
| `KEY_NOT_CONFIRMED` | With a typed code, `confirmKey` returned false. The receiver was not asked to pair, and the code still works. |
| `FINGERPRINT_MISMATCH` | The receiver's key is not the one in the QR text or `--fingerprint`. Nothing was sent about this device. |
| `PAIRING_DENIED`, `PAIRING_TIMED_OUT` | The person declined, or did not answer in time. |
| `CODE_REUSED` | The code was already used. Ask for a new one. |
| `VERSION_UNSUPPORTED` | Update the SDK. |
| `REPLACED`, `SEQUENCE_CONFLICT` | Two programs are using one device key. Give each its own key, or its own `--profile` with `erdigo-send`. |
| `STORAGE` | Stored state could not be used. `storageProblem()` says why. A device key that exists but cannot be read is never replaced. |
| `INTERRUPTED` | The thread was interrupted. |

## Where things are kept

`FileDeviceStore` keeps one identity and its pairings in a folder of its own: `%LOCALAPPDATA%\ErdigoSDK.Java\<profile>\`
on Windows, `~/Library/Application Support/ErdigoSDK.Java/<profile>/` on macOS, and
`$XDG_DATA_HOME/ErdigoSDK.Java/<profile>/` or `~/.local/share/ErdigoSDK.Java/<profile>/` elsewhere. The folder's name
differs from the other SDKs' on purpose, so that they never share a device key. The device key is in `identity.key`,
and pairings and connection counters in `pairings.json`. Every file is written through a temporary file and a rename,
so a crash never leaves half a file.

**The key is not encrypted, on any system.** It is kept in a file only you can read:

- **On macOS and Linux** the folder has mode 0700 and its files 0600, and both must belong to you. A folder or file that
  anyone else can read is refused, as `ssh` refuses such keys.
- **On Windows** the store gives the folder an access list that grants the current user alone, and refuses a folder or
  file that grants anyone but the current user, SYSTEM and the Administrators group. Those two names are looked up in
  English; on a Windows in another language a file that also grants them may be refused, which errs on the safe side.
  There is no DPAPI in the JDK, so unlike the .NET SDK this one does not encrypt the key for your Windows account: an
  administrator, a backup of your profile, or a copy of the disk holds it in the clear.

The store tells these apart, and never makes a new key over an old one:

| Key file | What happens |
|---|---|
| Missing | A new key is made and saved. |
| Damaged (not a key) | `StorageProblem.IDENTITY_DAMAGED`. The file is left as it is. |
| Unreadable (locked or no permission) | `StorageProblem.IDENTITY_UNREADABLE`. The file is left as it is. |
| Readable by others, or a link | `StorageProblem.INSECURE`. Nothing is read. |

To start again with a new identity, move the key file away yourself. A new key means pairing again with every receiver.

**What this does not protect against:** any program running as you, which can read the file exactly as this SDK
does; an administrator or root; and memory. Java gives no guarantee that a key is wiped from memory. The SDK clears its
own copy when the identity is closed, but the JDK's key objects, made for each handshake, keep copies until they are
collected, and a heap dump, a crash dump or the page file may hold one. If any of this matters, run the device's
program under an account of its own.

## What is in the jar

Java is easy to decompile, so nothing in the jar is secret: there are no embedded keys, no debug back doors and no
hidden switches. The one test hook, which lets the test suite reproduce the published vectors with fixed ephemeral
keys, is a package-private constructor of the handshake. No option, property or environment variable reaches it, and
the SDK never passes it a key.

## Dependencies, and why

The library has none at run time. Everything comes from the JDK:

| Need | From the JDK |
|---|---|
| X25519 | `KeyAgreement` and `KeyFactory` with `XDH`; raw 32-byte keys are converted to and from `XECPrivateKeySpec` and `XECPublicKeySpec` as RFC 7748 says |
| ChaCha20-Poly1305 | `Cipher` with `ChaCha20-Poly1305`, a new cipher object for each message, with the Noise nonce (4 zero bytes, then the 64-bit counter in little-endian order) |
| SHA-256, HMAC, HKDF | `MessageDigest` and `Mac` with `HmacSHA256` |
| JSON | A small strict reader and writer in this folder, written to read and write the same bytes as the other SDKs |
| Multicast DNS | `DatagramChannel`, and a small bounds-checked DNS reader in this folder |

For building and testing only:

| What | Version | Why |
|---|---|---|
| Maven | 3.9.14 | Builds and tests. Not installed: `mvnw` and `mvnw.cmd` (Maven Wrapper 3.3.4, the script-only kind, so no wrapper jar is committed) download it and refuse it unless its SHA-256 is the one pinned in [`.mvn/wrapper/maven-wrapper.properties`](.mvn/wrapper/maven-wrapper.properties). |
| JUnit Jupiter | 5.11.4 | Tests only. |
| Maven plugins | pinned in [`pom.xml`](pom.xml) | So that a build never picks up a new one unseen. |

CI builds with `-C`, so a downloaded file whose checksum does not match the repository's is an error. Maven keeps what
it downloads in `~/.m2`; to keep it inside the checkout instead, set `MAVEN_USER_HOME=.tools/m2` and add
`-Dmaven.repo.local=.tools/m2/repository`, which `.gitignore` already leaves out.

Discovery sends one-shot multicast DNS queries ([RFC 6762, section 5.1](https://www.rfc-editor.org/rfc/rfc6762#section-5.1))
from an ordinary port, over IPv4, and reads the answers with the small reader above. Receivers answer such a query
directly, so it needs neither port 5353 nor a Bonjour service. It is used only when no address is given.

## Differences from the Swift and .NET SDKs

- **No mock receiver.** `erdigo-send serve` is refused with a pointer to the .NET and Swift mocks, which this tool
  works with.
- **The device key is in a file, not encrypted,** on every system (see above). `erdigo-send --keychain` is refused.
- **Discovery uses IPv4 only.** The other SDKs also try a receiver's IPv6 link-local address. `--host` and `--port`
  always work.
- **Methods block,** as is usual in Java, and interrupting the thread cancels a request.
- **After a person confirms the key of a typed code, the pairing connection is tried up to three times**, half a second
  and then a second apart, if it meets silence. A receiver lets one address have only a few unproven connections at
  once, fewer still after a handshake that stopped early, and the connection that showed the key may not have been
  counted out yet. This is safe: nothing about the device is sent until the receiver has proven the code and its key
  matches the one the person confirmed. Without a confirmed key, silence is reported at once, as by the other SDKs.
- **The terminal.** Java has no API for the terminal's settings, so on macOS and Linux the tool reads the code from the
  terminal itself (`/dev/tty`) with echo turned off by `stty`, and before asking about the key it switches line mode
  off for a moment, again with `stty`, to discard anything typed early, a half-typed line included. On Windows it reads
  the code with `Console.readPassword`, so standard input and output must be the console, and it discards keys typed early by
  asking PowerShell to empty the console's input buffer. **The Windows side of this has not been tried with a person at
  a Windows console**; CI runs the library's tests on Windows, but not the tool in a console.

## The `erdigo-send` tool

Run `java -jar target/erdigo-send.jar help` for every command. They and their options are the same as the other SDKs'
`erdigo-send`, apart from `serve`. Add `--redact` to a command when pasting its output into a bug report. To pair with
`--redact`, use the QR text or `--fingerprint`, because a hidden key cannot be compared.

`pair` asks you to compare the receiver's key and type `yes` when it has only a bare code. It asks before the receiver
hears any request, so the receiver still shows its key. At the end of input or on any other answer it exits with an
error. No pairing is saved and the receiver is not asked to pair, although this device's own key may be in the state
folder by then. The receiver never sees that key. With the QR text or `--fingerprint` it compares the key itself and
asks nothing.

Only a person can answer that question, so `pair` refuses a bare code when standard input or output is not a terminal,
as in a pipe or a script. It stops before it contacts the receiver. Use the QR text there, or `--fingerprint` with the
full 64-digit key, which is part of the QR text. The code is never accepted as a command-line argument. A code file
must be readable by you alone: mode 0600 on macOS and Linux; on Windows a file in your own profile folder usually is.

`note --text-file` reads the note from a UTF-8 file. It takes out what editors such as Notepad add: a byte order
mark at the start, and the carriage return of each Windows line ending (CR LF becomes LF). Nothing else is changed.
A carriage return on its own is refused before anything is sent, and the 4096-byte limit counts the note as it is
sent.

## Cloud relay example

The app takes connections only from its own local network. To bring in data from the internet, `examples/cloud-relay`
has the two programs of [Sending from the internet](../docs/cloud-relay.md). Each is one file that runs from source
with the SDK's jar on the class path. They are examples to read and adapt, and are not in the jar.

- `Relay.java` runs on a server, behind an HTTPS proxy. Devices send operations to it with a source token, and it keeps
  them in memory until the gateway is done with them. It uses the JDK's HTTP server and the SDK's strict JSON reader and
  checks.
- `Gateway.java` runs on the app's local network. `pair` pairs it once: it hands over to `erdigo-send pair`, so the code
  is read exactly as above. `run` fetches from the relay over HTTPS and writes to the app as that paired device.

Build the jar as above. In the `java` folder, start the relay with the two tokens in the environment (on Windows, set
them with `set` or `$env:` instead of `export`):

```bash
export ERDIGO_RELAY_SOURCE_TOKENS=SOURCE_TOKEN
export ERDIGO_RELAY_GATEWAY_TOKEN=GATEWAY_TOKEN
java -cp target/erdigo-send.jar examples/cloud-relay/Relay.java --listen 127.0.0.1:8080
```

On a computer on the app's network, pair the gateway once, then run it:

```bash
java -cp target/erdigo-send.jar examples/cloud-relay/Gateway.java pair --name "Cloud gateway"
export ERDIGO_RELAY_TOKEN=GATEWAY_TOKEN
java -cp target/erdigo-send.jar examples/cloud-relay/Gateway.java run --relay https://relay.example.com
```

Add `--host` and `--port` to give the app's address, and `--once` to stop when the relay has nothing more. The gateway
keeps its key, its pairing and its place in the relay's queue (`relay-state.json`) in the `cloud-gateway` profile, or
in `--state-dir`. Only one `run` at a time may use a profile: a run holds a `FileChannel.tryLock` on `.gateway.lock` in
that folder for as long as it runs, and a second `run` on the same profile or `--state-dir` ends at once with code 1.
Everything else, from the HTTP API to running behind Caddy, is in the [guide](../docs/cloud-relay.md).

While the app cannot be reached, the SDK sends the same request again with the same sequence number, so an operation
whose answer was lost on the way is not applied twice. The gateway gives it `RetryPolicy.forever()`, or
`RetryPolicy.upTo(Duration.ofSeconds(30))` with `--once`. That policy counts from the last connection the SDK made, so
an app that takes every connection but never answers, or whose answers are all lost, would keep it trying. With `--once`
the gateway therefore also counts, for each operation, from the first sign of trouble: a try that failed, or the
connection it was sending on going away, which a thread beside it sees in `client.limits()` (the gateway calls
`client.connect()` first, so it knows when sending starts). If the app has not taken the operation 30 seconds later, the
gateway interrupts the request and exits with code 3. An app that never answers shows trouble only when the SDK stops
waiting for the answer, after 15 seconds, so that case takes about 45 seconds. The SDK keeps its connection while it
paces a track's points, so that wait is no trouble, and a long track is never given up. The gateway prints
`No answer from the Erdigo app; trying again.` once for that operation, and Ctrl+C or `SIGTERM` stops the wait, leaving
the operation on the relay.

`./mvnw -B verify` tests both programs. The end-to-end tests run only when `ERDIGO_MOCK` names a mock receiver's
`erdigo-send`, such as the .NET one; `tests/cloud-relay-smoke.sh` runs the commands above against that mock.

What has been checked:

- The relay against every case in [`cloud-relay-cases.json`](../docs/cloud-relay-cases.json) and the rest of the API:
  404, 405 and 401 in that order, each token only for its own use, 411, 413 (announced and read) and 415, 100
  operations and a photo of 1048576 bytes but no more, batches taken whole, `queue_full` with small limits, epochs and
  forgetting, `missed` with a clock the tests move, pages by count and by 4 MiB, held fetches, a log without tokens or
  contents, and refusing to start with bad tokens or on another address. Also that 40 clients sending their headers
  slowly do not hold up others, and that 32 bodies of 2 MiB at once fit in a heap of 256 MiB. The relay reads and
  checks only as many bodies at once as its heap allows, and answers `queue_full` to one that waits too long.
- The gateway with the .NET mock: pairing from the code file, every kind of operation, nothing applied twice, a refusal
  by the app printed and skipped, `not_permitted` for kinds not allowed, a relay started again, and a wrong token (exit
  2). Also an answer lost on the way, through a proxy that cuts both connections once the mock has answered: the SDK
  sends the request again with the same sequence number, and the track's two points are there once. With `--once`,
  every answer lost on the way, and an app that takes each connection and never answers: exit 3 after about 30 and 45
  seconds, with the track's points applied once and the operation left on the relay. Without a receiver: mistakes in
  the command line and environment (exit 1), a second run on a state folder another run is using (exit 1 at once),
  answers that break the API (exit 2), the app or the relay out of reach with `--once` (exit 3 after about 30 seconds,
  with the operations left on the relay), and a pause of about 5 seconds between fetches when a relay answers at once
  with nothing.
- Both stop with status 0 on SIGTERM, the gateway also within a few seconds while the SDK waits for an app that cannot
  be reached, leaving the operation on the relay.
- CI runs all of this on Ubuntu and Windows with Java 17 and 21, except SIGTERM and the smoke script, which run on Ubuntu
  only. It was also run by hand on a Mac, where the gateway also worked with the Python and Node.js relays, and the
  Node.js gateway with this relay.

What has not:

- **A real HTTPS proxy, the internet and the real app.** Only plain HTTP on this computer, and the mock.
- **Many slow clients.** Each connection holds a thread until its request is in, for up to 60 seconds, and the JDK's
  server takes at most 256 connections. A relay that can be reached without a buffering proxy in front can be stalled
  by enough slow clients. Heavy load was not measured either.

## Layout

| Folder | Contents |
|---|---|
| `src/main/java/com/ziorentia/erdigo/core` | The wire format, strict JSON, framing and the Noise handshakes |
| `src/main/java/com/ziorentia/erdigo/sdk` | Identity, pairing, the device store, discovery and the client |
| `src/main/java/com/ziorentia/erdigo/send` | The command-line tool |
| `src/test/java` | Unit, vector, scripted-receiver and interop tests, and the cloud relay example's tests |
| `examples/cloud-relay` | The cloud relay example: a relay and a gateway, run from source |
| `tests/cli-smoke.sh` | Checks of the command-line tool against the .NET mock, on macOS and Linux |
| `tests/cloud-relay-smoke.sh` | The cloud relay example's commands against the .NET mock, on macOS and Linux |

## Tests

```bash
./mvnw -B verify
```

```bash
./mvnw -B package && dotnet build ../dotnet/src/erdigo-send -c Release && tests/cli-smoke.sh
```

What has been checked:

- The official Noise vectors for `IK`, `XXpsk3` and `NNpsk0`, the X25519 vectors of RFC 7748, and every integration-v1
  vector: both handshakes and their messages byte for byte, and every message a receiver must accept or reject.
- The client, pairing, retries, pacing and the handling of every kind of error answer, against a scripted receiver over
  real TCP, including answers a receiver would not give by itself. Each defence that has a test was also switched off
  once to see that test fail.
- Pairing with, and writing every kind of item to, the .NET SDK's mock receiver, in CI on Ubuntu and Windows with Java
  17 and 21, and on Ubuntu finding it over real multicast DNS. The same against the Swift SDK's mock on a Mac, by hand.
- The command-line tool against the .NET mock, through pseudo terminals: refusing a code as an argument, a code file
  others can read, a bare code without a person at a terminal, and text typed before the key appeared. In CI on Ubuntu,
  and by hand on a Mac.

What has not:

- **The real Erdigo app.** This SDK has not been run against a release of the app's receiver.
- **Android**, and any Java runtime other than OpenJDK 17 and 21 (Temurin in CI, Homebrew's on a Mac).
- **The tool at a Windows console**: reading a typed code, the question about the key, and discarding keys typed early.
- **Discovery on a real network** with other devices on it, and over IPv6.
- **No independent security audit has been done.**
