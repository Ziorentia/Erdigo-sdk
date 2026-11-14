# Erdigo SDK for PHP

A PHP library and command-line tool for writing pins, markers, notes, photos and tracks to an Erdigo receiver on the
local network. It is for PHP that runs on the same local network as the Erdigo app: a NAS such as a Synology or QNAP
with Web Station, an in-house camera or device server, a web server in the office, or a script run by hand or by cron.
It needs PHP 8.1 or later, 64-bit, and nothing beyond what PHP itself bundles.

It is a port of the [Windows SDK](../windows/README.md) and speaks the same [protocol](../spec/integration-v1.md). It
works with the other SDKs' mock receivers and with the Erdigo app on a Mac or a Windows PC.

> **Local network only.** The device running PHP and the computer running the Erdigo app must be on the same local
> network. The protocol has no internet path, by design: a server on the internet, for example a shop that wants to send
> a delivery's position, cannot reach the app, and this SDK cannot change that. Sending from the internet would need a
> separate relay, which is not part of this protocol.

> **In store review.** Erdigo 2.17.23, the first version of the app that accepts these connections, is waiting for
> review on the Mac App Store and the Microsoft Store. Until it is released, develop against a mock receiver as
> described below. This notice will be removed once the review is complete.

To use the SDK with a real map, install Erdigo 2.17.23 or later from the
[Mac App Store](https://apps.apple.com/us/app/erdigo/id6446225102?mt=12) or the
[Microsoft Store](https://apps.microsoft.com/detail/9ndwqg2sdr7x?hl=en-US) on a computer on the same local network. The
app's minimum and recommended systems are listed in
[System requirements for the app](../README.md#system-requirements-for-the-app).

## What you need

| | |
|---|---|
| PHP | 8.1 or later, 64-bit, from the command line (`php-cli`) or a web server |
| Extensions | `sodium` and `hash`, which PHP bundles. `sockets` is optional and only finds receivers on the network. `json` and PCRE are always there. |
| Functions | `proc_open` and the system's `stty`, only for typing a pairing code at a terminal. On Windows, `proc_open` with `icacls` and `whoami`, for checking file permissions. |
| Composer | Not needed. It can install the package from this repository. |

32-bit PHP is refused, because the protocol's counters do not fit in its integers. Some older NAS models with 32-bit ARM
processors run 32-bit PHP; check with `php -r 'echo PHP_INT_SIZE;'`, which must print `8`.

This SDK is not on Packagist. To use it with Composer, point Composer at this repository:

```json
{
    "repositories": [{ "type": "vcs", "url": "https://github.com/Ziorentia/Erdigo-sdk" }],
    "require": { "ziorentia/erdigo-sdk": "dev-main" }
}
```

Without Composer, copy the `php` folder anywhere outside your web server's document root and load it with
`require '/path/to/php/autoload.php';`.

## Try it in two minutes

This edition has no mock receiver of its own. Start the .NET or the Swift edition's, from a clone of this repository:

```bash
cd windows && dotnet run --project src/erdigo-send -- serve --mock --code-file /tmp/erdigo-code
```

It prints a port, writes the pairing's QR text to `/tmp/erdigo-code` (a file only you can read), and prints the
receiver's key. In another terminal, in the `php` folder, pair with it, replacing `PORT`:

```bash
php bin/erdigo-send pair --host 127.0.0.1 --port PORT --name "My sensor" --kinds pin,track --label test --code-file /tmp/erdigo-code
```

The QR text carries the receiver's key, so the tool compares it without asking. Then send something:

```bash
php bin/erdigo-send pin --host 127.0.0.1 --port PORT --id city-hall --lat 37.5663 --lon 126.9779 --title "Seoul City Hall"
```

The mock prints each request it accepts. Type `devices` and then `items 1` in its terminal to see what it holds. On a
Mac, `swift run erdigo-send serve --mock --code-file /tmp/erdigo-code` at the top of the repository does the same.

## Use it in your code

Pair once:

```php
require '/path/to/php/autoload.php'; // or Composer's vendor/autoload.php

use Erdigo\Core\ItemKind;
use Erdigo\Core\PairingTicket;
use Erdigo\FileDeviceStore;
use Erdigo\Pairing;
use Erdigo\ReceiverTarget;

// Outside the web server's document root, readable by the PHP user alone.
$store = new FileDeviceStore('/var/lib/erdigo/greenhouse');
$identity = $store->loadOrCreateIdentity();

// The text of the QR code the Erdigo app shows. Never take it, or a code, as a command-line argument.
$ticket = PairingTicket::parse($qrText);

$pairing = Pairing::pair(
    ReceiverTarget::address('192.168.1.20', 52345),
    $ticket,
    $identity,
    'Greenhouse sensor',
    [ItemKind::Pin, ItemKind::Track],
);
$store->save($pairing);
```

The QR text carries the receiver's key, and `Pairing::pair` compares it and stops on a mismatch before the receiver
hears anything about this device. `PairingTicket::parse` refuses QR text without a valid key. A bare typed code has no
key in it: `Pairing::pair` then needs a `confirmKey` callback that shows a person the receiver's key and returns whether
it matches the one on the receiver's screen, because a code proves only that the other side knows it. The program that
supplies that callback is responsible for a person answering it. The
[threat model](../spec/threat-model.md#what-this-does-not-protect-against) explains why this matters. On a server, use
the QR text.

After that, write:

```php
use Erdigo\Core\Coordinate;
use Erdigo\Core\TrackPoint;
use Erdigo\ErdigoClient;

$client = new ErdigoClient($identity, $store->pairings()[0], $store);

$spot = new Coordinate(37.5663, 126.9779);
$client->putPin('sensor-1', $spot, 'Greenhouse');
$client->move('sensor-1', $spot);

$client->appendTrack('walk', [new TrackPoint($spot, timestampMs: 1700000000000)], 'Morning walk');
$client->finishTrack('walk');

$client->putPhoto('view', $spot, file_get_contents('view.jpg'), 'View');
$client->putNote('reminder', $spot, 'Check the vent.');

$client->delete('reminder');
$client->deleteAll();   // Only this device's items.

$client->close();
```

[`examples/quickstart.php`](examples/quickstart.php) is a complete program.

## Pairing on a server without a terminal

A NAS, a camera server or a web server has no person at a terminal, so pair it with the QR text, which needs no
question. Either of these works:

- **From the command line,** over SSH: save the QR text in a file only you can read, and give it to `pair` with
  `--code-file`. `--fingerprint` with the receiver's full 64-digit key does the same for a bare code.

  ```bash
  umask 077 && cat > /tmp/erdigo-qr     # paste the QR text, then Ctrl-D
  php bin/erdigo-send pair --state-dir /var/lib/erdigo/site --host 192.168.1.20 --port 52345 \
      --name "Office server" --kinds pin,marker --code-file /tmp/erdigo-qr
  rm /tmp/erdigo-qr
  ```

  Run it as the user that will send later, such as `sudo -u www-data php bin/erdigo-send ...` for a web server, so that
  the state folder belongs to that user.

- **From a web page** on your own server: the person scans the QR code in the Erdigo app with a phone, copies the text,
  and pastes it into a form. The page pairs with it.

  ```php
  // pair.php: reachable only by the people who administer this server, for example behind the server's own login.
  use Erdigo\Core\ItemKind;
  use Erdigo\Core\PairingTicket;
  use Erdigo\Core\WireException;
  use Erdigo\ErdigoException;
  use Erdigo\FileDeviceStore;
  use Erdigo\Pairing;
  use Erdigo\ReceiverTarget;

  if ($_SERVER['REQUEST_METHOD'] === 'POST') {
      // The person confirms the request in the app, which can take up to two minutes.
      set_time_limit(180);
      try {
          $ticket = PairingTicket::parse((string) ($_POST['qr'] ?? ''));
          if ($ticket->fingerprint === null) {
              exit('Paste the QR text, not the code alone.');   // Nobody here can compare a key.
          }
          $store = new FileDeviceStore('/var/lib/erdigo/site');
          $pairing = Pairing::pair(
              ReceiverTarget::address((string) $_POST['host'], (int) $_POST['port']),
              $ticket,
              $store->loadOrCreateIdentity(),
              'Office server',
              [ItemKind::Pin, ItemKind::Marker],
          );
          $store->save($pairing);
          echo 'Paired.';
      } catch (WireException | ErdigoException $error) {
          echo htmlspecialchars($error->getMessage());
      }
      exit;
  }
  ?>
  <form method="post">
      <textarea name="qr" autocomplete="off" placeholder="erdigo-pair:1?code=...&amp;fp=..."></textarea>
      <input name="host" placeholder="192.168.1.20"> <input name="port" placeholder="52345">
      <button>Pair</button>
  </form>
  ```

  The QR text is a secret for its ten minutes: do not log the request body, and serve the page only on the local
  network. A web server or proxy in front of PHP may end a request sooner than two minutes (nginx's
  `fastcgi_read_timeout` is 60 seconds by default), so confirm the request in the app promptly, or pair from the
  command line instead.

## Finding the receiver

Give the address with `ReceiverTarget::address($host, $port)`, or `--host` and `--port`. The Erdigo app keeps its port,
so an address found once keeps working as long as the computer keeps its IP address; a fixed address from the router
(a DHCP reservation) makes sure of that. After each connection the SDK remembers the address it used.

With PHP's `sockets` extension, `ReceiverTarget::discover()` and the tool without `--host` also look for receivers
advertised on the local network, with a short multicast DNS query over IPv4 from an ordinary port, so no Bonjour or
Avahi service is needed. It tries the remembered address first, then up to eight receivers it found. A receiver this
device is not paired with stays silent. Without `sockets`, or where multicast does not reach (some Docker networks and
VPNs), give the address.

## Web requests

PHP usually runs one short process per web request, so each request opens its own connection, and closes it when the
client is closed or the request ends. Each connection costs:

- a TCP connection and three round trips: the handshake, `hello` and `welcome`, and the request and its answer;
- four X25519 operations, which take well under a millisecond with libsodium;
- one write of the connection counter to `pairings.json`, flushed to disk with `fsync` before anything is sent, because
  the receiver refuses a counter it has seen. On a NAS with spinning disks this write is the slowest part, typically
  some milliseconds.

On a local network this adds a few milliseconds to a request. If one web request sends several items, send them on the
same client, which keeps one connection for all of them.

A receiver allows one connection per device, and a new one ends the old one with `replaced`. So that the workers of a
web server do not end each other's connections, the client holds a lock on the device's connection in the state folder
while its connection is open, and other processes using the same state folder wait for it, up to `ackTimeout`. Close
the client as soon as you are done. A long-running program that keeps its connection open keeps the others waiting, so
give it a state folder, and so a device key, of its own.

A web request has its own time limit (`max_execution_time`). Give the client a `requestTimeout`, so that a call ends in
time with `ErrorKind::TimedOut` instead of waiting for a receiver that is asleep:

```php
$client = new ErdigoClient($identity, $pairing, $store, retry: RetryPolicy::upTo(5), requestTimeout: 10.0);
```

## What the SDK does for you

- **Keeps requests in order.** Each call returns once the receiver acknowledges it. A long `appendTrack` and a photo
  upload are not interleaved with other requests.
- **Survives a sleeping computer or a closed app.** If the connection fails, it reconnects with growing delays and
  resends with the same sequence number, so nothing is applied twice. Choose how long with `retry:`
  (`RetryPolicy::none()`, `RetryPolicy::upTo($seconds)` or `RetryPolicy::forever()`). The default is 30 seconds,
  counted from when the connection was lost. Time spent connected does not count, so limit a whole call with
  `requestTimeout`.
- **Paces itself** by the limits the receiver announces, and waits when told to slow down. Waiting as told is never
  held against a device. A receiver takes one track point a second, with bursts of 100, so a long track takes a while.
- **Splits large uploads.** Photos go in chunks of 48000 bytes, and a long list of track points goes in batches of 100.

A call that ends with `TimedOut` after it was sent may already have been applied. Sending it again is safe for the
`put...` methods, `move` and the deletes, which give the same result when repeated. `appendTrack` would add its points a
second time.

## When something goes wrong

Calls throw `Erdigo\ErdigoException`, and its `$kind` says what happened. The one you will meet most is `NoAnswer`. A
receiver stays silent when it is off, asleep or on another network, when a pairing code is wrong or expired, and when a
device has been removed. These cannot be told apart, on purpose, because that silence is what keeps strangers from
learning anything.

| `$kind` (`Erdigo\ErrorKind`) | Meaning |
|---|---|
| `NoAnswer` | See above. Check the network, the address and that the app is running. |
| `NotPermitted` | The person did not allow this kind (`$itemKind`). Pair again to ask for more. |
| `Rejected` | The receiver refused this request. `$receiverError->code` is listed in the [specification](../spec/integration-v1.md#10-errors). |
| `KeyNotConfirmed` | With a typed code, `confirmKey` returned false. The receiver was not asked to pair, and the code still works. |
| `FingerprintMismatch` | The receiver's key is not the one in the QR text or `--fingerprint`. Nothing about this device was sent. |
| `PairingDenied`, `PairingTimedOut` | The person declined, or did not answer in time. |
| `CodeReused` | The code was already used. Ask for a new one. |
| `VersionUnsupported` | Update the SDK. |
| `Replaced`, `SequenceConflict` | Two programs are using one device key. Give each its own state folder, or its own `--profile` with `erdigo-send`. |
| `TimedOut` | The call did not finish within `requestTimeout`. |
| `Storage` | Stored state could not be used. `$storageProblem` says why. A device key that exists but cannot be read is never replaced. |

## Where things are kept

`FileDeviceStore` keeps one device key and its pairings in a folder of its own. `FileDeviceStore::defaultDirectory()`
is `~/.local/share/ErdigoSDK.PHP/<profile>` on Linux (or under `$XDG_DATA_HOME`),
`~/Library/Application Support/ErdigoSDK.PHP/<profile>` on macOS and `%LOCALAPPDATA%\ErdigoSDK.PHP\<profile>` on
Windows. The folder's name differs from the other SDKs' on purpose, so that they never share a device key. **On a web
server, always pass a folder of your own outside the document root**, such as `/var/lib/erdigo/<site>`, owned by the
user PHP runs as. The store refuses a folder inside the document root of the web server running it.

On Linux, macOS and other Unix systems the folder has mode 0700 and its files mode 0600, and the store refuses a folder
or file that anyone but the user PHP runs as could read, or that is a link. On Windows the folder gets an access list
that grants the current user alone, and the store refuses one that grants anyone but that user, SYSTEM and the
Administrators group. The device key is in `identity.key`, pairings and connection counters in `pairings.json`. Every
file is written through a temporary file and a rename, so a crash never leaves half a file.

**The device key is not encrypted at rest.** Anyone who can read `identity.key`, which means the user PHP runs as and
root, or anyone who gets a backup of it, has the key, and can write to the map as this device until it is removed in
the app. In particular:

- **Do not use this SDK on shared hosting.** There, other customers' PHP often runs as the same user as yours, or can
  read your files, and the host's staff and backups hold copies. Use a NAS, a server or a virtual machine you control.
- On a web server, every PHP script that runs as the same user can read the key. Keep untrusted PHP code off that
  server, or give Erdigo a user of its own.
- PHP gives no guarantee that a key is wiped from memory. The SDK clears what it can with `sodium_memzero`, keeps the
  key out of `var_dump`, `print_r`, `var_export` and serialization, and marks key and code parameters as sensitive
  (PHP 8.2 and later leave them out of stack traces), but copies may remain until the process ends.

The store tells these apart, and never makes a new key over an old one:

| Key file | What happens |
|---|---|
| Missing | A new key is made and saved. |
| Damaged (not a key) | `StorageProblem::IdentityDamaged`. The file is left as it is. |
| Unreadable | `StorageProblem::IdentityUnreadable`. The file is left as it is. |
| Readable by others, or a link | `StorageProblem::Insecure`. Nothing is read. |

To start again with a new identity, move the key file away yourself. A new key means pairing again with every receiver.

## The `erdigo-send` tool

`php bin/erdigo-send help` lists every command. It has the same commands and options as the other editions'
`erdigo-send`. Add `--redact` to a command when pasting its output into a bug report.

The pairing code is never accepted as a command-line argument, because arguments appear in the process list and shell
history. `pair` reads it from the terminal, without showing it, or from `--code-file`, a file that must be readable by
you alone (mode 0600; on Windows, a file only you can read).

`pair` asks you to compare the receiver's key and type `yes` when it has only a bare code. It asks before the receiver
hears any request, so the receiver still shows its key, and anything typed before the key appeared is discarded, so
only an answer typed after seeing it counts. At the end of input or on any other answer it exits with an error, and no
pairing is saved. With the QR text or `--fingerprint` it compares the key itself and asks nothing. Only a person can
answer, so `pair` refuses a bare code when standard input or output is not a terminal, as in a pipe, a cron job or a
web server, and stops before it contacts the receiver.

## Differences from the other SDKs

- **No mock receiver.** `serve --mock` points to the .NET and Swift editions' mocks, which this edition is tested
  against.
- **Synchronous.** Each call blocks until the receiver answers. There are no cancellation tokens; `requestTimeout` and
  the retry policy bound a call instead.
- **A lock between processes.** The client holds a lock on the device's connection in the state folder while connected,
  for web servers whose workers share one device key. The other SDKs expect one program per key.
- **The key is a 0600 file everywhere**, including Windows, where the .NET SDK uses DPAPI and the Swift SDK can use the
  keychain. `--keychain` is refused.
- **On Windows**, PHP can neither hide what is typed nor discard what was typed ahead, so `pair` reads a code only from
  `--code-file`, and a bare code is refused: use the QR text or `--fingerprint`. For a Windows PC, the
  [.NET SDK](../windows/README.md) is the better choice.
- **Discovery** uses IPv4 only, and needs the `sockets` extension.

## Layout

| Folder | Contents |
|---|---|
| `src/Core` | The wire format, strict JSON, framing and the Noise handshakes |
| `src` | Identity, pairing, the device store, discovery and the client |
| `src/Cli` | The command-line tool |
| `bin/erdigo-send` | The command-line tool's entry point |
| `autoload.php` | Loads the classes without Composer |
| `examples/quickstart.php` | A complete small program |
| `tests/Unit` | PHPUnit tests: the vectors, the model, the store, pairing, the client and the tool |
| `tests/Support` | A scripted receiver the tests run in a process of their own, which can give any answer |
| `tests/cli-smoke.sh` | Checks of the tool and the example against the .NET or Swift mock receiver, on macOS and Linux |

## Tests

```bash
phpunit
```

```bash
MOCK=../windows/src/erdigo-send/bin/Release/net8.0/erdigo-send tests/cli-smoke.sh
```

Run them in the `php` folder of a full clone, since they read the shared vectors in `spec/`. The second needs a built
mock receiver of another edition (`dotnet build windows/src/erdigo-send --configuration Release`, or `swift build` for
`.build/debug/erdigo-send`) and Python 3, for the pseudo terminals.

What they cover:

- The official Noise vectors for `IK`, `XXpsk3` and `NNpsk0`, and every integration-v1 vector: both handshakes byte for
  byte from either side, the pairing code and its key, the QR text, and every message a receiver must accept or refuse
  with its error code.
- The client against a scripted receiver over real TCP: every kind of item, photos in chunks, tracks in batches,
  reconnection with the same sequence number, every kind of error answer (including those that end the connection
  before any rate-limit retry), pacing, timeouts, and processes sharing a key.
- Pairing with the QR text, a wrong key, a typed code confirmed or not, a denied request and a reused code.
- The store's permissions, a damaged key, links, counters and the document root.
- The tool, run as a program: a code is never an argument nor repeated, a bare code without a terminal is refused
  before anything is sent, and a code file others can read is refused.
- **With the other editions:** `tests/cli-smoke.sh` pairs with the .NET mock receiver using the QR text and a typed
  code (answered in a pseudo terminal, including a `yes` typed before the key appeared, which must not count), and
  writes every kind of item, including a photo of several chunks. CI runs it on Linux with PHP 8.1 to 8.5. It was also
  run on macOS against the Swift mock receiver.

CI runs the PHPUnit tests on Linux with PHP 8.1, 8.2, 8.3, 8.4 and 8.5, and on Windows with PHP 8.1 and 8.5, where the
access-list checks run too.

What has **not** been tested:

- The released Erdigo app, which is not out yet. Only the mock receivers of the other editions.
- A real NAS. Nothing was run on a Synology, a QNAP or another NAS, or on ARM hardware; it was run on macOS (Apple
  silicon) and on GitHub's Linux (x64) and Windows runners.
- PHP under a web server (PHP-FPM or Apache's module). The tests run PHP from the command line; the web-request
  behaviour, including the lock between processes, was checked with several command-line processes at once.
- Discovery against a real Erdigo app. The DNS messages are checked in unit tests.
- No independent security audit has been done.
