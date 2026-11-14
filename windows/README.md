# Erdigo SDK for Windows (.NET)

A .NET 8 library for writing pins, markers, notes, photos and tracks to an Erdigo receiver on the local network. It is
for Windows 10 or later. It also builds and runs on macOS and Linux, which this repository uses for development and
CI, but the device key is protected only on Windows (see [Where things are kept](#where-things-are-kept)).

It is a port of the [macOS SDK](../macos/README.md) and speaks the same [protocol](../spec/integration-v1.md). The two
SDKs and their mock receivers work with each other.

> **In store review.** Erdigo 2.17.23, the first version of the app that accepts these connections, is waiting for
> review on the Microsoft Store and the Mac App Store. Until it is released, develop against the mock receiver
> described below. This notice will be removed once the review is complete.

To use the SDK with a real map, install Erdigo 2.17.23 or later from the
[Microsoft Store](https://apps.microsoft.com/detail/9ndwqg2sdr7x?hl=en-US) on a PC on the same local network. It can
also write to [Erdigo for Mac](https://apps.apple.com/us/app/erdigo/id6446225102?mt=12).

## Try it in two minutes

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or a later one; the libraries target
.NET 8 either way. Run the tests from a full clone, since they read the shared vectors in `spec/`. In the `windows`
folder, start a mock receiver in one terminal:

```powershell
dotnet run --project src/erdigo-send -- serve --mock
```

It prints a port, a pairing code and the receiver's key. In another terminal, pair with it. Type the code when asked.
The tool then shows the key it found: if it matches the one the mock printed, type `yes`. The mock is not asked to pair,
and no pairing is saved, until you do.

```powershell
dotnet run --project src/erdigo-send -- pair --host 127.0.0.1 --port PORT --name "My sensor" --kinds pin,track --label test
```

Then send something:

```powershell
dotnet run --project src/erdigo-send -- pin --host 127.0.0.1 --port PORT --id city-hall --lat 37.5663 --lon 126.9779 --title "Seoul City Hall"
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

No package is published yet. Reference the project from yours:

```xml
<ProjectReference Include="path\to\Erdigo-sdk\windows\src\Erdigo.Sdk\Erdigo.Sdk.csproj" />
```

Pair once:

```csharp
using Erdigo.Core;
using Erdigo.Sdk;

var store = new FileDeviceStore(FileDeviceStore.DefaultDirectory());
using var identity = store.LoadOrCreateIdentity();

// Read this from the keyboard or from a private file. Never take it as a command-line argument.
var ticket = PairingTicket.Parse(codeTheUserTyped);

var pairing = await Pairing.PairAsync(
    ReceiverTarget.Discover,
    ticket,
    identity,
    "Greenhouse sensor",
    new[] { ItemKind.Pin, ItemKind.Track },
    // Asked only for a typed code, which proves only that the other side knows the code.
    confirmKey: (key, cancellationToken) =>
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            return Task.FromResult(false); // Nobody could see this and answer.
        }
        while (Console.KeyAvailable)
        {
            Console.ReadKey(intercept: true); // Only an answer given after seeing the key counts.
        }
        Console.WriteLine($"Receiver key: {key}");
        Console.WriteLine("If this matches the key shown on the receiver, type yes:");
        return Task.FromResult(Console.ReadLine()?.Trim().ToLowerInvariant() == "yes");
    });
store.Save(pairing);
```

`confirmKey` runs before the receiver hears about this device, so the receiver still shows its key. If it returns
false, `PairAsync` throws an `ErdigoException` whose `Kind` is `KeyNotConfirmed`, which a real program should catch.
The receiver has heard nothing about this device, and the code stays valid. A typed code cannot pair without
`confirmKey`. Text from the QR code carries the receiver's key, and `PairAsync` compares it and stops on a mismatch,
so it asks the person nothing. `PairingTicket.Parse` refuses QR text without a valid fingerprint. The redirection check
makes a pipe or a file count as a no, because nobody is there to see the key and answer, so a program with no
terminal should use the QR text. The [threat model](../spec/threat-model.md#what-this-does-not-protect-against) explains
why this matters.

After that, write:

```csharp
using var client = new ErdigoClient(identity, pairing, store);

var spot = new Coordinate(37.5663, 126.9779);
await client.PutPinAsync("sensor-1", spot, "Greenhouse");
await client.MoveAsync("sensor-1", spot);

var point = new TrackPoint(spot, timestampMs: 1_700_000_000_000);
await client.AppendTrackAsync("walk", new[] { point }, "Morning walk");
await client.FinishTrackAsync("walk");

await client.PutPhotoAsync("view", spot, jpegOrPngBytes, "View");
await client.PutNoteAsync("reminder", spot, "Check the vent.");

await client.DeleteAsync("reminder");
await client.DeleteAllAsync();   // Only this device's items.
```

Every method takes an optional `CancellationToken`. A cancelled request throws `OperationCanceledException`.
[`examples/Quickstart`](examples/Quickstart/Program.cs) is a complete program.

## What the SDK does for you

- **Finds the receiver.** It tries the last known address, then up to eight receivers advertised on the local
  network. Pass `ReceiverTarget.Address(host, port)` to skip this.
- **Keeps requests in order.** They run one at a time, in the order they were made, and each returns once the
  receiver acknowledges it. A long `AppendTrackAsync` and a photo upload are not interleaved with other requests.
- **Survives a sleeping computer or a closed app.** If the connection fails, it reconnects with growing delays and
  resends with the same sequence number, so nothing is applied twice. Choose how long with `retry:`
  (`RetryPolicy.None`, `RetryPolicy.UpTo(time)` or `RetryPolicy.Forever`). The default is 30 seconds, counted from
  when the connection was lost. See [How long a request can take](#how-long-a-request-can-take).
- **Paces itself** by the limits the receiver announces, and waits when told to slow down. Waiting as told is never
  held against a device.
- **Splits large uploads.** Photos go in chunks, and a long list of track points goes in batches.

## How long a request can take

The retry policy limits each outage, not the request as a whole. Its time counts from when the connection was lost
and starts again once a connection is made, and time spent connected, such as waiting to stay within the receiver's
limits, never counts. So a receiver that keeps accepting connections and losing them before it answers keeps a
request going past 30 seconds, and the requests behind it wait. When a request must end within a fixed time, give it
a deadline:

```csharp
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
try
{
    await client.PutPinAsync("sensor-1", spot, "Greenhouse", cancellationToken: deadline.Token);
}
catch (OperationCanceledException)
{
    // Not confirmed in time. See below.
}
```

Cancelling a request that is waiting its turn frees its place at once, and the ones behind it go ahead. A request
cancelled after it was sent may already have been applied, because the receiver may have acted on it before the
answer was lost. Sending it again is safe for the `Put…Async` methods, `MoveAsync` and the deletes, which give the
same result when repeated. `AppendTrackAsync` would add its points a second time, and a long one may have sent some
of its batches already.

## When something goes wrong

Requests throw `ErdigoException`, and its `Kind` says what happened. The one you will meet most is `NoAnswer`. A
receiver stays silent when it is off, asleep or on another network, when a pairing code is wrong or expired, and when
a device has been removed. These cannot be told apart, on purpose, because that silence is what keeps strangers from
learning anything.

| `Kind` | Meaning |
|---|---|
| `NoAnswer` | See above. Check the network and that the app is running. |
| `NotPermitted` | The person did not allow this kind (`ItemKind`). Pair again to ask for more. |
| `Rejected` | The receiver refused this request. `ReceiverError.Code` is listed in the [specification](../spec/integration-v1.md#10-errors). |
| `KeyNotConfirmed` | With a typed code, `confirmKey` returned false. The receiver was not asked to pair, and the code still works. |
| `PairingDenied`, `PairingTimedOut` | The person declined, or did not answer in time. |
| `CodeReused` | The code was already used. Ask for a new one. |
| `VersionUnsupported` | Update the SDK. |
| `Replaced`, `SequenceConflict` | Two programs are using one device key. Give each its own key, or its own `--profile` with `erdigo-send`. |
| `Storage` | Stored state could not be used. `StorageProblem` says why. A device key that exists but cannot be read is never replaced. |

## Where things are kept

`FileDeviceStore` keeps one identity and its pairings in a folder of its own.

**On Windows** that is `%LOCALAPPDATA%\ErdigoSDK\<profile>\`. The store creates it with a protected access list that
grants the current user alone, and refuses a folder or file that grants anyone but the current user, SYSTEM and the
Administrators group. The device key is in `identity.dpapi`, encrypted with
[DPAPI](https://learn.microsoft.com/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata) for the current user
(`DataProtectionScope.CurrentUser`), with additional entropy that ties it to this purpose. Pairings and connection
counters are in `pairings.json`. Every file is written through a temporary file and a rename, so a crash never leaves
half a file.

**On macOS and Linux**, which this edition supports for development and testing, it is
`~/Library/Application Support/ErdigoSDK.NET/<profile>/` or `~/.local/share/ErdigoSDK.NET/<profile>/`, with mode 0700
for the folder and 0600 for its files, and the key is in `identity.key` in the clear. The folder's name differs from
the Swift SDK's on purpose, so the two never share a device key.

The store tells these apart, and never makes a new key over an old one:

| Key file | What happens |
|---|---|
| Missing | A new key is made and saved. |
| Damaged (not a key) | `StorageProblem.IdentityDamaged`. The file is left as it is. |
| Unreadable (locked, no permission, or DPAPI cannot decrypt it) | `StorageProblem.IdentityUnreadable`. The file is left as it is. |
| Readable by others | `StorageProblem.Insecure`. Nothing is read. |

To start again with a new identity, move the key file away yourself. A new key means pairing again with every receiver.

### What DPAPI does not protect against

DPAPI ties the key to your Windows account. It keeps the key from other users of the computer, and from someone who
copies the file to another computer. It does not protect against:

- **any program running as you.** It can ask DPAPI to decrypt the file, exactly as this SDK does. Malware in your
  session can take the key.
- **an administrator, or SYSTEM.** They control the computer.
- **someone who has your Windows password and a copy of your profile**, such as a backup. DPAPI's master keys are
  protected by your password, so the two together are enough.
- **memory.** .NET, like most runtimes, gives no guarantee that a key is wiped from memory. The SDK keeps the key in a
  pinned array and clears it when the identity is disposed, but the cryptography library keeps short-lived copies, and
  a crash dump or the page file may hold one.

If any of these matters, run the device's program under an account of its own.

## What is in a release build

C# is easy to decompile, so nothing in these assemblies is secret: there are no embedded keys, no debug back doors and
no hidden switches. The one test hook, which lets the test suite reproduce the published vectors with fixed ephemeral
keys, is an internal constructor of the handshake. No option, setting or environment variable reaches it, and the SDK
never passes it a key.

## Dependencies, and why

| Package | Version | Why |
|---|---|---|
| `BouncyCastle.Cryptography` | 2.7.0 | X25519 and ChaCha20-Poly1305. .NET has no X25519 API, and its ChaCha20-Poly1305 is not available on every system, so one well-known implementation gives the same results everywhere. SHA-256, HMAC and HKDF come from .NET itself. |
| `System.Security.Cryptography.ProtectedData` | 8.0.0 | DPAPI, from Microsoft. Called only on Windows. |
| `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk` | 2.9.3, 3.1.5, 17.14.1 | Tests only. |

Every version is pinned in [`Directory.Packages.props`](Directory.Packages.props) and every project has a lock file.
[`nuget.config`](nuget.config) allows nuget.org and nothing else.

Restoring checks every package, including the ones packages bring with them (`NuGetAuditMode=all`), against
nuget.org's list of known vulnerabilities. A vulnerability of any severity is an error, and so is a list that cannot
be fetched, in CI and on your computer alike, so nothing builds unchecked. When one appears, update the package in
`Directory.Packages.props` and the lock files (`dotnet restore Erdigo.sln --force-evaluate`). If it cannot affect this
code, suppress that one advisory with `<NuGetAuditSuppress Include="advisory URL" />` and say why beside it. The check
covers what the advisory database covers, not the .NET runtime a program runs on, which needs its own updates.

Discovery uses no package. It sends one-shot multicast DNS queries
([RFC 6762, section 5.1](https://www.rfc-editor.org/rfc/rfc6762#section-5.1)) from an ordinary port and reads the
answers with a small, bounds-checked DNS reader in this repository. Receivers answer such a query directly, so it needs
neither port 5353, which Windows itself uses, nor Bonjour for Windows, and it works the same on every system. It is
used only when no address is given.

## Differences from the macOS SDK

- The device key is protected with DPAPI on Windows. There is no keychain option, so `erdigo-send --keychain` is
  refused. On other systems the key is in a 0600 file.
- On Windows, `erdigo-send pair` reads a typed code from the console, so it needs standard input to be the console.
  On macOS and Linux it reads from the terminal itself, as the Swift tool does. Either way a code file can be used.
- The mock receiver checks photos by their structure rather than with a system image decoder, which .NET does not
  have on every system: the declared type against the first bytes, exactly one image, the dimensions and pixel count
  from the file's own headers, at most 32 JPEG scans with nothing between segments and no second start-of-image
  marker, and, for a PNG, every chunk checksum and the size of the inflated image data. It then stores the image data
  without any metadata segment or chunk, so EXIF location, colour profiles and comments are gone, but it does not
  decode, rescale or re-encode the pixels. A real receiver must still do what spec section 8 says.
- `serve --mock --lan` asks about each pairing request unless `--auto-approve` is given. The Swift tool's mock still
  allows them unless `--ask` is given.
- `serve --mock --bonjour` advertises with a small multicast DNS responder of its own, on IPv4. It gives the computer a
  random host name for the advertisement, so the computer's own network name is not announced with it.
- Public methods are asynchronous and take a `CancellationToken`, as is usual in .NET.

## The `erdigo-send` tool

Run `dotnet run --project src/erdigo-send -- help` for every command. Add `--redact` to a command when pasting its
output into a bug report. To pair with `--redact`, use the QR text or `--fingerprint`, because a hidden key cannot be
compared.

`pair` asks you to compare the receiver's key and type `yes` when it has only a bare code. It asks before the receiver
hears any request, so the receiver still shows its key. At the end of input or on any other answer it exits with an
error. No pairing is saved and the receiver is not asked to pair, although this device's own key may be in the state
folder by then. The receiver never sees that key. With the QR text or `--fingerprint` it compares the key itself and
asks nothing.

Only a person can answer that question, so `pair` refuses a bare code when standard input or output is not a terminal,
as in a pipe or a script. It stops before it contacts the receiver. Use the QR text there, or `--fingerprint` with the
full 64-digit key, which is part of the QR text.

A code file must be readable by you alone. On Windows a file in your own profile folder usually is. On macOS and Linux
give it mode 0600.

## Layout

| Folder | Contents |
|---|---|
| `src/Erdigo.Core` | The wire format, strict JSON, framing, the Noise handshakes and multicast DNS messages |
| `src/Erdigo.Sdk` | Identity, pairing, the device store, discovery and the client |
| `src/Erdigo.MockReceiver` | The mock receiver |
| `src/erdigo-send` | The command-line tool |
| `examples/Quickstart` | A complete small program |
| `tests/Erdigo.Tests` | Unit, vector and end-to-end tests |
| `tests/cli-smoke.sh` | Checks of the command-line tool and the example, on macOS and Linux |

## Tests

```bash
dotnet test Erdigo.sln
```

```bash
dotnet build Erdigo.sln && tests/cli-smoke.sh
```

The tests use the official Noise vectors and every integration-v1 vector, and run the SDK against the mock receiver
over real TCP, including the cases that must fail: strangers, replays, reused codes, removed devices, limits and
disguised images. The Windows-only tests of DPAPI and access lists run on Windows. The command-line checks run on macOS
and Linux, because they drive the tools through pseudo terminals.
