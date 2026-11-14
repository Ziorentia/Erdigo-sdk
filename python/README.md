# Erdigo SDK for Python

A Python library and command-line tool for writing pins, markers, notes, photos and tracks to an Erdigo receiver on
the local network. It needs Python 3.10 or later and runs on Linux, macOS and Windows.

This is the edition for scripts and for Linux boards such as a Raspberry Pi: a sensor gateway, a ground station or a
cron job that reads its readings in its own way and sends them through the SDK. **It has not been tried on a real
Raspberry Pi or any other ARM board.** Its tests run on Linux (x64) and Windows (x64) in CI, and on macOS (Apple
silicon) locally.

It speaks the same [protocol](../spec/integration-v1.md) as the [macOS](../macos/README.md) and
[Windows](../windows/README.md) SDKs, and its `erdigo-send` tool has the same commands and options as theirs. It has no
mock receiver of its own: develop against the mock that comes with either of them. CI pairs with the .NET mock and
writes every item kind to it, and the same was done with the Swift mock on a Mac.

> **In store review.** Erdigo 2.17.23, the first version of the app that accepts these connections, is waiting for
> review on the Mac App Store and the Microsoft Store. Until it is released, develop against a mock receiver as
> described below. This notice will be removed once the review is complete.

To use the SDK with a real map, install Erdigo 2.17.23 or later on a Mac from the
[Mac App Store](https://apps.apple.com/us/app/erdigo/id6446225102?mt=12) or on a PC from the
[Microsoft Store](https://apps.microsoft.com/detail/9ndwqg2sdr7x?hl=en-US), on the same local network. The app's
minimum and recommended systems are listed in [System requirements for the app](../README.md#system-requirements-for-the-app).

## Install

No package is published. Install it from a full clone of this repository, into a virtual environment:

```bash
python3 -m venv .venv
.venv/bin/pip install ./python
```

On Windows the second line is `.venv\Scripts\pip install .\python`. To find receivers on the network without giving an
address, add the optional discovery package: `pip install "./python[discovery]"`. Everything else works without it.

That installs `erdigo-send`, which `python -m erdigo_sdk` also runs. The one dependency is `cryptography`, which has
ready-made wheels for 64-bit Linux on x64 and ARM, macOS and Windows. On a 32-bit Raspberry Pi OS pip may have to build
it, which needs Rust; use the 64-bit system where you can. Neither has been tried.

## Try it in two minutes

Start a mock receiver from one of the other SDKs in one terminal. With the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0),
in the `windows` folder:

```bash
dotnet run --project src/erdigo-send -- serve --mock
```

or with Xcode or the Swift command-line tools, at the top of the repository: `swift run erdigo-send serve --mock`.

It prints a port, a pairing code and the receiver's key. In another terminal, pair with it. Type the code when asked.
The tool then shows the key it found: if it matches the one the mock printed, type `yes`. The mock is not asked to pair,
and no pairing is saved, until you do.

```bash
.venv/bin/erdigo-send pair --host 127.0.0.1 --port PORT --name "My sensor" --kinds pin,track --label test
```

Then send something:

```bash
.venv/bin/erdigo-send pin --host 127.0.0.1 --port PORT --id city-hall --lat 37.5663 --lon 126.9779 --title "Seoul City Hall"
```

The mock prints each request it accepts. Type `devices` and then `items 1` in its terminal to see what it holds. The
other SDKs' guides describe the mock's options.

## Use it in your code

Pair once:

```python
import sys

from erdigo_sdk import DISCOVER, FileDeviceStore, ItemKind, PairingTicket, default_directory, pair

store = FileDeviceStore(default_directory())
identity = store.load_or_create_identity()

# Read this from the keyboard or from a private file. Never take it as a command-line argument.
ticket = PairingTicket.parse(code_the_user_typed)


def confirm_key(key: str) -> bool:
    """Asked only for a typed code, which proves only that the other side knows the code."""
    if not (sys.stdin.isatty() and sys.stdout.isatty()):
        return False  # Nobody could see this and answer.
    print(f"Receiver key: {key}")
    return input("If this matches the key shown on the receiver, type yes: ").strip().lower() == "yes"


pairing = pair(DISCOVER, ticket, identity, "Greenhouse sensor", [ItemKind.PIN, ItemKind.TRACK], confirm_key=confirm_key)
store.save(pairing)
```

`confirm_key` runs before the receiver hears about this device, so the receiver still shows its key. If it returns
false, `pair` raises an `ErdigoError` whose `kind` is `ErrorKind.KEY_NOT_CONFIRMED`, which a real program should catch.
The receiver has heard nothing about this device, and the code stays valid. A typed code cannot pair without
`confirm_key`. Text from the QR code carries the receiver's key, and `pair` compares it and stops on a mismatch, so it
asks the person nothing. `PairingTicket.parse` refuses QR text without a valid fingerprint. The terminal check makes a
pipe or a file count as a no, because nobody is there to see the key and answer, so a program with no terminal should
use the QR text. The [threat model](../spec/threat-model.md#what-this-does-not-protect-against) explains why this
matters.

After that, write:

```python
from erdigo_sdk import Coordinate, ErdigoClient, MarkerColor, TrackPoint

with ErdigoClient(identity, pairing, store) as client:
    spot = Coordinate(37.5663, 126.9779)
    client.put_pin("sensor-1", spot, "Greenhouse")
    client.put_marker("gate", spot, "Gate", MarkerColor.GREEN)
    client.move("sensor-1", spot)

    client.append_track("walk", [TrackPoint(spot, timestamp_ms=1_700_000_000_000, altitude_m=38.5)], "Morning walk")
    client.finish_track("walk")

    client.put_photo("view", spot, jpeg_or_png_bytes, "View")
    client.put_note("reminder", spot, "Check the vent.")

    client.delete("reminder")
    client.delete_all()  # Only this device's items.
```

[`examples/quickstart.py`](examples/quickstart.py) is a complete program. The API is synchronous, and a client may be
shared between threads: requests run one at a time, in the order they were made.

## What the SDK does for you

- **Finds the receiver.** It tries the last known address, then up to eight receivers advertised on the local
  network, if the optional `zeroconf` package is installed. Pass `ReceiverTarget.address(host, port)` to skip this.
- **Keeps requests in order.** They run one at a time, in the order they were made, and each returns once the receiver
  acknowledges it. A long `append_track` and a photo upload are not interleaved with other requests.
- **Survives a sleeping computer or a closed app.** If the connection fails, it reconnects with growing delays (1, 2,
  4 ... up to 60 seconds, with 20 percent jitter) and resends with the same sequence number, so nothing is applied
  twice. Choose how long with `retry=` (`RetryPolicy.NONE`, `RetryPolicy.up_to(seconds)` or `RetryPolicy.FOREVER`).
  The default is 30 seconds, counted from when the connection was lost. See
  [How long a request can take](#how-long-a-request-can-take).
- **Paces itself** by the limits the receiver announces, and waits when told to slow down. Waiting as told is never
  held against a device. An answer that ends the connection is recognised before any wait, so it is never resent.
- **Splits large uploads.** Photos go in chunks of 48000 bytes, and a long list of track points goes in batches of 100.

## How long a request can take

The retry policy limits each outage, not the request as a whole. Its time counts from when the connection was lost
and starts again once a connection is made, and time spent connected, such as waiting to stay within the receiver's
limits, never counts. So a receiver that keeps accepting connections and losing them before it answers keeps a request
going past 30 seconds, and the requests behind it wait. When a request must end within a fixed time, give it one:

```python
try:
    client.put_pin("sensor-1", spot, "Greenhouse", timeout=120)
except TimeoutError:
    ...  # Not confirmed in time. See below.
```

The time includes waiting for its turn. A request that runs out of time while waiting frees its place at once, and the
ones behind it go ahead. A request that runs out after it was sent may already have been applied, because the receiver
may have acted on it before the answer was lost. Sending it again is safe for the `put_…` methods, `move` and the
deletes, which give the same result when repeated. `append_track` would add its points a second time, and a long one
may have sent some of its batches already.

## When something goes wrong

Requests raise `ErdigoError`, and its `kind` says what happened. The one you will meet most is `NO_ANSWER`. A receiver
stays silent when it is off, asleep or on another network, when a pairing code is wrong or expired, and when a device
has been removed. These cannot be told apart, on purpose, because that silence is what keeps strangers from learning
anything.

| `ErrorKind` | Meaning |
|---|---|
| `NO_ANSWER` | See above. Check the network and that the app is running. |
| `NOT_PERMITTED` | The person did not allow this kind (`item_kind`). Pair again to ask for more. |
| `REJECTED` | The receiver refused this request. `receiver_error.code` is listed in the [specification](../spec/integration-v1.md#10-errors). |
| `KEY_NOT_CONFIRMED` | With a typed code, `confirm_key` returned false. The receiver was not asked to pair, and the code still works. |
| `FINGERPRINT_MISMATCH` | The receiver's key is not the one in the QR text or `--fingerprint`. Nothing was sent about this device. |
| `PAIRING_DENIED`, `PAIRING_TIMED_OUT` | The person declined, or did not answer in time. |
| `CODE_REUSED` | The code was already used. Ask for a new one. |
| `VERSION_UNSUPPORTED` | Update the SDK. |
| `REPLACED`, `SEQUENCE_CONFLICT` | Two programs are using one device key. Give each its own key, or its own `--profile` with `erdigo-send`. |
| `STORAGE` | Stored state could not be used. `storage_problem` says why. A device key that exists but cannot be read is never replaced. |
| `INVALID_ARGUMENT` | Refused before anything was sent, such as an empty title or a GIF. |

## Where things are kept

`FileDeviceStore` keeps one identity and its pairings in a folder of its own. `default_directory(profile)` is:

| System | Folder |
|---|---|
| Linux | `$XDG_DATA_HOME/ErdigoSDK.Python/<profile>/`, by default `~/.local/share/ErdigoSDK.Python/<profile>/` |
| macOS | `~/Library/Application Support/ErdigoSDK.Python/<profile>/` |
| Windows | `%LOCALAPPDATA%\ErdigoSDK.Python\<profile>\` |

The folder's name differs from the other SDKs' on purpose, so that they never share a device key. Pairings and
connection counters are in `pairings.json`. Every file is written through a temporary file and a rename, so a crash
never leaves half a file, and a lock file keeps two processes from writing at once.

**On Linux and macOS** the folder has mode 0700 and its files mode 0600, and the store refuses a folder or file that
is a link, belongs to another user or can be read by anyone else. The device key is in `identity.key` in the clear,
protected only by those permissions. Someone with your user account or root can read it, and so can anyone with a copy
of a backup that holds it. On a Raspberry Pi that runs a sensor, give the program a user account of its own.

**On Windows** the store creates the folder with a protected access list that grants the current user alone, and
refuses a folder or file that grants anyone but the current user, SYSTEM and the Administrators group. The device key is
in `identity.dpapi`, encrypted with [DPAPI](https://learn.microsoft.com/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata)
for the current user, with entropy that ties it to this purpose, called through `ctypes`. DPAPI keeps the key from other
users and from someone who copies the file to another computer. It does not protect against a program running as you,
an administrator, or someone with your password and a copy of your profile. The
[Windows SDK guide](../windows/README.md#what-dpapi-does-not-protect-against) says more.

The store tells these apart, and never makes a new key over an old one:

| Key file | What happens |
|---|---|
| Missing | A new key is made and saved. |
| Damaged (not a key) | `StorageProblem.IDENTITY_DAMAGED`. The file is left as it is. |
| Unreadable (no permission, or DPAPI cannot decrypt it) | `StorageProblem.IDENTITY_UNREADABLE`. The file is left as it is. |
| Readable by others, or a link | `StorageProblem.INSECURE`. Nothing is read. |

To start again with a new identity, move the key file away yourself. A new key means pairing again with every receiver.

**Secrets in memory.** Python gives no way to wipe a key from memory: `bytes` cannot be cleared, and the interpreter and
the `cryptography` package make copies. The key stays in the process's memory while it runs, and a crash dump or swap
may hold a copy.

## Dependencies, and why

| Package | Version | Why |
|---|---|---|
| `cryptography` | 50.0.2 | X25519 and ChaCha20-Poly1305, from OpenSSL. SHA-256, HMAC and HKDF come from Python itself. The Noise handshakes are implemented in this package and checked against the official vectors. |
| `zeroconf` | 0.154.0 (0.152.0 on Python 3.10) | Optional, for finding receivers on the network. |
| `pytest`, `setuptools` | 9.2.0, 80.9.0 | Tests and building only. |

The runtime version is pinned in [`pyproject.toml`](pyproject.toml), so every install gets the version that was tested.
CI installs exactly what [`requirements/test.txt`](requirements/test.txt) lists, with a hash for every file, including
the packages these bring with them. Regenerate it after changing a version, with the command at the top of
[`requirements/test.in`](requirements/test.in). Nothing checks these packages against a list of known vulnerabilities
yet; check `cryptography`'s security advisories before updating it, and update it when one affects you.

## Differences from the macOS and Windows SDKs

- **No mock receiver.** `erdigo-send serve --mock` says to use the .NET or Swift one, which this edition works with.
- **Synchronous.** Methods block until the receiver answers, and take `timeout=` in seconds where the others take a
  cancellation token or a Swift task. A client can be shared between threads.
- **Discovery is optional** and uses the `zeroconf` package, which shares port 5353 with the system's own responder.
  Without it, give an address. It has been checked only against the .NET mock's advertisement, on CI's Linux runner.
- **The key** is in a 0600 file on Linux and macOS, and encrypted with DPAPI on Windows. There is no keychain option,
  so `erdigo-send --keychain` is refused.
- `erdigo-send pair` reads a typed code from the terminal itself on Linux and macOS, with echo off, as the other tools
  do, and from the console on Windows. Either way a code file can be used.
- On Windows an access-list entry for OWNER RIGHTS is accepted when a trusted account owns the file or folder (see
  [The `erdigo-send` tool](#the-erdigo-send-tool)). The .NET SDK refuses such an entry.

## The `erdigo-send` tool

Run `erdigo-send help` for every command. Add `--redact` to a command when pasting its output into a bug report. To pair
with `--redact`, use the QR text or `--fingerprint`, because a hidden key cannot be compared.

`pair` asks you to compare the receiver's key and type `yes` when it has only a bare code. It asks before the receiver
hears any request, so the receiver still shows its key. Anything typed before the key appears is discarded. At the end
of input or on any other answer it exits with an error. No pairing is saved and the receiver is not asked to pair,
although this device's own key may be in the state folder by then. The receiver never sees that key. With the QR text
or `--fingerprint` it compares the key itself and asks nothing.

Only a person can answer that question, so `pair` refuses a bare code when standard input or output is not a terminal,
as in a pipe, a script or a systemd service. It stops before it contacts the receiver. Use the QR text there, or
`--fingerprint` with the full 64-digit key, which is part of the QR text.

`note --text-file` sends the file's text exactly as it is, which must be UTF-8. A note may hold line feeds and tabs but
no carriage returns (spec section 6.3), so a file saved with Windows line endings (CR LF), as Notepad saves a new file,
is refused before anything is sent, as the .NET and Swift tools refuse it. Save it with LF line endings.

The code is never accepted as a command-line argument, because arguments appear in process lists and shell history. A
code file must be readable by you alone: on Linux and macOS a regular file you own with mode 0600, on Windows a file
that grants nobody but you, SYSTEM and the Administrators group. On Windows an entry for OWNER RIGHTS counts as the
file's owner, so it is accepted when you, SYSTEM or the Administrators group own the file. Python 3.13 and later give
folders made with mode 0o700, such as those from `tempfile.mkdtemp`, exactly that entry.

## Layout

| Path | Contents |
|---|---|
| `src/erdigo_sdk/` | The library: `noise.py` (the handshakes), `strict_json.py` and `messages.py` (the wire format), `model.py`, `pairing.py`, `client.py`, `store.py`, `private_file.py` and `_windows.py` (DPAPI and access lists), `discovery.py`, and `cli.py` (the tool) |
| `examples/quickstart.py` | A complete small program |
| `tests/` | Unit, vector and end-to-end tests, with a small scripted receiver in `tests/receiver.py` |
| `scripts/interop.py` | Pairs with the .NET or Swift mock receiver and writes every item kind to it |
| `requirements/` | The exact versions and hashes CI installs |

## Tests

From the `python` folder, in a virtual environment:

```bash
pip install --require-hashes -r requirements/test.txt
pip install --no-deps --no-build-isolation .
python -m pytest
```

The tests use the official Noise vectors and every integration-v1 vector in [`spec/vectors`](../spec/vectors/README.md),
byte for byte, so run them from a full clone. They also run the SDK and the tool against a small receiver over real TCP,
including the cases that must fail: a typed code nobody confirmed, a wrong fingerprint, a receiver key spelled two ways,
low-order keys, every kind of error answer, files others can read, and a code given as an argument. The Windows-only
tests of DPAPI and access lists run on Windows, and the ones that need a pseudo terminal run on Linux and macOS. Each
defence the tests cover was also removed in turn to check that its test then fails.

To check this edition against another one's mock receiver, build that mock and run, for example:

```bash
python scripts/interop.py --cwd ../windows -- dotnet run --project src/erdigo-send --
python scripts/interop.py --cwd .. -- swift run erdigo-send
```

## What has and has not been checked

- The Noise handshakes reproduce the official vectors for `IK`, `XXpsk3` and `NNpsk0`, and the published pairing and
  connection vectors byte for byte. Every request in `messages.json` is accepted or refused with its error code.
- CI, on Linux and Windows with Python 3.10 and 3.14: the tests, and pairing with the .NET mock and writing every item
  kind to it (two photos, one in several chunks, a two-point track, a move, a delete and delete-all), checked with the
  mock's own list of what it holds. On Linux also a typed code answered `no` and then `yes` on a pseudo terminal, and
  finding the mock through its advertisement.
- Locally on a Mac (Apple silicon, Python 3.14): the same against the Swift mock.
- **Not checked:** a real Raspberry Pi or any ARM board, Python 3.11 to 3.13, macOS in CI, discovery on Windows or
  macOS or of a real receiver, and the released Erdigo app, which does not yet include a receiver.
- **No independent security audit has been done.**
