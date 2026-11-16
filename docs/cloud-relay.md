# Sending from the internet: a cloud relay and a gateway

The Erdigo app takes connections only from its own local network
([why](#why-the-app-listens-only-on-the-local-network)). A drone flying on a mobile network, a sensor in a field or a
web service cannot reach it directly. This guide shows how to bring their data in anyway, with two small programs:

- a **relay** on the internet. Your devices send to it over HTTPS, and it holds what they send.
- a **gateway** on the same local network as the Erdigo app. It fetches from the relay and writes to the app through
  an Erdigo SDK, as a paired device.

```text
  drone, sensor, web service                 the local network where the Erdigo app runs
                                             +-------------------------------------------------------+
   POST /v1/ops        +-------+   GET /v1/ops    |  +---------+   Erdigo protocol   +------------+  |
  ------------------>  | relay |  <---------------+--| gateway | ------------------> | Erdigo app |  |
       (HTTPS)         +-------+  (HTTPS, outgoing)  +---------+   (paired device)   +------------+  |
                                             +-------------------------------------------------------+
```

Nothing connects into the local network. The gateway makes outgoing HTTPS requests, as a web browser does, so no port
has to be opened on the router, and the app goes on refusing connections from outside the local network.

Every SDK has both programs as an example, written with the language's standard library and the SDK, and nothing else.
All the relays speak the same HTTP API, described [below](#the-relay-api), so any gateway works with any relay, and a
server of your own can take the relay's place.

| SDK | Relay | Gateway | How to run them |
|---|---|---|---|
| Swift | [`swift/Examples/cloud-relay`](../swift/Examples/cloud-relay) (macOS) | [`swift/Examples/cloud-gateway`](../swift/Examples/cloud-gateway) | [Swift README](../swift/README.md#cloud-relay-example) |
| .NET | [`dotnet/examples/CloudRelay`](../dotnet/examples/CloudRelay) | [`dotnet/examples/CloudGateway`](../dotnet/examples/CloudGateway) | [.NET README](../dotnet/README.md#cloud-relay-example) |
| Python | [`relay.py`](../python/examples/cloud_relay/relay.py) | [`gateway.py`](../python/examples/cloud_relay/gateway.py) | [Python README](../python/README.md#cloud-relay-example) |
| Node.js | [`relay.js`](../nodejs/examples/cloud-relay/relay.js) | [`gateway.js`](../nodejs/examples/cloud-relay/gateway.js) | [Node.js README](../nodejs/README.md#cloud-relay-example) |
| Java | [`Relay.java`](../java/examples/cloud-relay/Relay.java) | [`Gateway.java`](../java/examples/cloud-relay/Gateway.java) | [Java README](../java/README.md#cloud-relay-example) |
| PHP | [`relay.php`](../php/examples/cloud-relay/relay.php), behind a web server | [`gateway.php`](../php/examples/cloud-relay/gateway.php) | [PHP README](../php/README.md#cloud-relay-example) |

You can mix them: for example a Python or PHP relay on a rented Linux server, and the .NET gateway on a Windows PC
next to the app. Every gateway was tried with every relay ([What was tested](#what-was-tested)).

The relays and gateways are examples to read, run and adapt. They are small, keep to one gateway per relay, and have
not had a security audit.

## When you need this, and when you don't

- **The device, or its ground station, is on the same local network as the app.** Use an SDK directly. You need no
  relay and no gateway.
- **You already have a server that receives your devices' data**, such as an MQTT broker, a database or a web service.
  You may not need the example relay. Write a gateway that reads from your server and writes through an SDK. The
  example gateway shows the parts that matter: pairing, keeping its place, retrying, and skipping what the app refuses.
- **The data starts somewhere on the internet and you have nothing in between.** Run the relay and the gateway.

## Why the app listens only on the local network

The app's receiver answers nothing until a device proves it is allowed, and it takes connections only from its own
local network (spec section 2.1). That keeps the receiver out of reach of everyone on the internet, and the app does not
contact any server to receive items. The gateway keeps it that way: it is a paired device like any other. A person
typed a one-time code into it and approved it in the app, it can write only the kinds of item the person allowed, its
items are labelled with its name, and removing it in the **Devices** tab of the app's settings cuts it off.

## Setting it up

1. **Make two tokens**, one for your devices (the *source token*) and one for the gateway (the *gateway token*). A token
   is 32 to 256 characters from `A`-`Z`, `a`-`z`, `0`-`9`, `-`, `_`, `.` and `~`. Either of these makes a good one:

   ```bash
   openssl rand -base64 32 | tr '+/' '-_' | tr -d '='
   python3 -c "import secrets; print(secrets.token_urlsafe(32))"
   ```

2. **Run the relay** on a server that can be reached from the internet, behind an HTTPS reverse proxy
   ([Deploying](#deploying)). It reads the tokens from the environment:

   | Variable | Value |
   |---|---|
   | `ERDIGO_RELAY_SOURCE_TOKENS` | One or more source tokens, separated by commas. |
   | `ERDIGO_RELAY_GATEWAY_TOKEN` | The gateway token. It must differ from every source token. |

3. **Pair the gateway** once, on a computer on the same local network as the app that stays on: a Raspberry Pi, a NAS
   or a PC. Open the **Devices** tab in the app's settings and start pairing, then run the gateway's `pair` command and
   type the code or the QR text. It works like `erdigo-send pair`.

4. **Run the gateway** with the relay's address. It reads the gateway token from `ERDIGO_RELAY_TOKEN`.

5. **Send to the relay** from your devices, with the source token. To try it with curl, keep the header in a file
   only you can read, so the token is not on curl's command line:

   ```bash
   (umask 077; printf 'Authorization: Bearer %s\n' "$SOURCE_TOKEN" > source-auth.txt)
   curl https://relay.example.com/v1/ops \
     -H @source-auth.txt \
     -H "Content-Type: application/json" \
     -d '{"ops":[{"op":"pin","id":"drone-1","lat":37.5663,"lon":126.9779,"title":"Drone 1"}]}'
   ```

   The pin appears on the app's map within a few seconds, labelled with the gateway's name.

Tokens are never taken as command-line arguments, which other users of the same computer can see. Keep them out of
your shell history too: put them in a file only you can read, as in [Keeping the gateway
running](#keeping-the-gateway-running).

### The relay's command line

```text
relay [--listen HOST:PORT] [--keep-hours HOURS] [--behind-proxy]
```

- `--listen` is where it accepts plain HTTP, `127.0.0.1:8080` by default. An IPv6 address goes in brackets, as in
  `[::1]:8080`. Port `0` picks a free port. A host that is not a loopback address (`127.0.0.1`, `::1` or `localhost`)
  is refused unless `--behind-proxy` is given, which says that HTTPS ends in a proxy in front of it, for example in
  another container.
- `--keep-hours` is how long an operation waits for the gateway before it is dropped: whole hours from 1 to 168, 24 by
  default.
- When it is ready it prints `Relay listening on http://HOST:PORT`, with the address it bound (`127.0.0.1` for
  `localhost`) and the port it actually got. It stops on Ctrl+C or `SIGTERM`.
- It writes one line for each request: the time, the method, the path and the status. Never the query, the headers
  or the body.

The PHP relay is a script for a web server instead, and is set up as its [README](../php/README.md#the-relay) says. It
keeps its queue in a data folder, so its numbering survives a restart, and it needs PHP's `display_errors` off.

### The gateway's command line

```text
gateway pair --name NAME [--kinds LIST] [--host HOST --port PORT] [--code-file FILE] [--fingerprint HEX]
             [--profile NAME] [--state-dir DIR]
gateway run  --relay URL [--host HOST --port PORT] [--once] [--profile NAME] [--state-dir DIR]
```

- `pair` asks for the code or the QR text on the keyboard, or reads it from `--code-file`, a file only you can read,
  exactly as `erdigo-send pair` does. `--kinds` is `pin,marker,note,photo,track` by default. The person approving it in
  the app sees the name and the kinds.
- `run` fetches from the relay and writes to the app until it is stopped. `--relay` must start with `https://`. Plain
  `http://` is accepted only for a loopback host, for testing on one computer. The address may end in a path, such as
  `https://example.com/erdigo-relay`, and `/v1/ops` is added to it.
- `--once` makes `run` stop once the relay has nothing more, for a scheduled job or a test.
- `--host` and `--port` give the app's address. Without them the gateway finds the app on the local network.
- `--profile` is `cloud-gateway` by default. The gateway's key, its pairing and its place in the relay's queue are kept
  in that profile's folder, or in `--state-dir`. A profile used by the gateway holds exactly one pairing: `pair`
  refuses a profile that already has one, and only one `run` at a time may use a profile. After removing the gateway in
  the app, pair again with a new profile, or clear the old one with `erdigo-send forget`.

`run` exits with:

| Code | When |
|---|---|
| 0 | Stopped, or with `--once`, nothing more to fetch. |
| 1 | A mistake in the command line or the environment, no pairing, or a profile another `run` is using. |
| 2 | The relay refused the token, or answered with something that does not follow this API. |
| 3 | With `--once`, the app or the relay could not be reached for about 30 seconds. What was fetched stays on the relay. |

The gateway prints what it does, and never tokens, ids, coordinates, titles or text. The lines read like these, though
the wording differs a little between SDKs, so a script should go by the exit code:

```text
Done with 12 operations, up to 1234 (1 refused)
Refused operation 1230 (marker): not_permitted
The relay started a new numbering; starting from its oldest operation.
Some operations expired on the relay before they were fetched.
No answer from the Erdigo app; trying again in 4 seconds.
The relay could not be reached; trying again in 4 seconds.
The Erdigo app could not be reached. The operations stay on the relay.
The relay could not be reached.
```

## The relay API

Version 1. Every request and answer body is a JSON object in UTF-8, and every answer the relay gives has
`Content-Type: application/json` and exactly the fields shown here. Requests to `/v1/ops` carry a token as
`Authorization: Bearer TOKEN`: a source token to send, the gateway token to fetch. Numbers this API calls whole, such
as `ts`, `received_ms` and the query parameters, are written as plain integers, with no fraction or exponent.

A relay looks at a request in this order, and answers with the first that applies:

1. An unknown path: `404 {"error":"not_found"}`.
2. A known path with the wrong method: `405 {"error":"method_not_allowed"}`.
3. For `/v1/ops`, a missing token, an unknown one, or one of the wrong kind for the method:
   `401 {"error":"unauthorized"}`. Tokens are compared in constant time.
4. Everything else, as described below.

After that, the order of the checks on a `POST` body (415, 411, 413) is up to the relay. A body announced as larger
than the limit is refused without being read. A relay may refuse a body that comes without `Content-Length` with
`411 {"error":"length_required"}`, or read it up to the limit.

The web server or proxy in front of a relay may answer some requests itself, with another body or none: a malformed
request, headers that are too large, a client that is too slow, a body over the proxy's own limit, or `502` and `504`
when the relay is down. A gateway treats `408`, `429` and any `5xx` as "try again later", whatever the body, and any
other answer but `200` as a broken API. It does not follow redirects.

### Sending: `POST /v1/ops`

For sources. `Content-Type` must be `application/json` (a `charset=utf-8` parameter is allowed).

```json
{"ops":[
  {"op":"pin","id":"drone-1","lat":37.5663,"lon":126.9779,"title":"Drone 1"},
  {"op":"track","id":"flight-7","title":"Flight 7","points":[{"lat":37.5663,"lon":126.9779,"ts":1700000000000,"alt":120}]}
]}
```

- `ops` has 1 to 100 operations. The body is at most 2 MiB (2097152 bytes).
- The batch is taken whole or not at all. Operations are delivered in the order they were taken.
- An answer `200 {"accepted":2,"last_seq":41}` gives how many were taken and the sequence number of the last one.

| Status | Body | When |
|---|---|---|
| 400 | `{"error":"invalid_json"}` | The body is not one JSON object in UTF-8: for example it does not parse, starts with a byte order mark, has something after the object, has a duplicate key, or has a number too large to be finite. |
| 400 | `{"error":"invalid_body","reason":"..."}` | The object is not `{"ops":[...]}` with 1 to 100 operations and nothing else. |
| 400 | `{"error":"invalid_op","index":3,"reason":"..."}` | Operation number `index` (from 0) breaks a rule below. `reason` is a short English explanation. |
| 401 | `{"error":"unauthorized"}` | See above. |
| 413 | `{"error":"too_large"}` | The body is over 2 MiB. |
| 415 | `{"error":"unsupported_media_type"}` | `Content-Type` is not JSON. |
| 503 | `{"error":"queue_full"}` | Taking the batch would put the relay over one of its [limits](#limits-of-the-example-relays). It sends `Retry-After: 30`. Send again later. |

### Operations

An operation is an object whose `op` field gives its shape. A field not listed for that `op` is an error.

| `op` | Fields | In the app |
|---|---|---|
| `pin` | `id`, `lat`, `lon`; optional `title` | Creates or replaces a pin. |
| `marker` | `id`, `lat`, `lon`; optional `title`, `color` | Creates or replaces a marker. |
| `note` | `id`, `lat`, `lon`, `text` | Creates or replaces a note. |
| `photo` | `id`, `lat`, `lon`, `type`, `data`; optional `title` | Creates or replaces a photo. |
| `track` | `id`, `points`; optional `title` | Adds points to a track, and opens it if it is new. |
| `finish_track` | `id` | Finishes a track. |
| `move` | `id`, `lat`, `lon` | Moves a pin, marker, note or photo. |
| `delete` | `id` | Deletes an item. |

The values follow the Erdigo protocol, which the SDKs check again before sending. The cases in
[`cloud-relay-cases.json`](cloud-relay-cases.json) show where each limit lies, and every example relay is tested
against them.

- `id`: 1 to 64 ASCII letters, digits, `-` or `_`. All of a gateway's items share one set of ids, whichever device sent
  them to the relay, so give each device its own prefix, such as `drone1-` or `field3-`.
- `lat` from -90 to 90 and `lon` from -180 to 180, in degrees (WGS 84), as JSON numbers.
- `title`: 1 to 200 characters. `text`: 1 to 4096 bytes of UTF-8. Neither may hold control characters or invisible
  direction marks (spec section 6.3); `text` may hold line feeds and tabs, but not carriage returns.
- `color`: `red`, `orange`, `yellow`, `green`, `blue`, `purple` or `gray`.
- `type`: `image/jpeg` or `image/png`. `data`: the image file in standard base64 with padding, exactly as a standard
  encoder writes it, with no line breaks. Decoded, it is 1 to 1048576 bytes and starts with the bytes for its type:
  `FF D8 FF` for JPEG, `89 50 4E 47 0D 0A 1A 0A` for PNG. The relay checks no more than that. The app decodes it
  again, removes its metadata (including any EXIF location) and places it at `lat`, `lon`.
- `points`: 1 to 100 points, each with `lat`, `lon` and optionally `ts` (milliseconds since 1970, a whole number from 0
  to 2^53-1) and `alt` (metres, -1000 to 100000).

### Fetching: `GET /v1/ops`

For the gateway. Query parameters, all optional:

| Parameter | Meaning |
|---|---|
| `epoch` | The `epoch` from the gateway's last answer. |
| `after` | The sequence number of the last operation the gateway finished with. 0 by default. |
| `limit` | At most this many operations, 1 to 100, 100 by default. |
| `wait` | Seconds, 0 to 30, 0 by default. If nothing is waiting, the relay may hold the request open that long for something to arrive. A relay may also hold it for less, or answer at once (the PHP relay holds it for up to 10 seconds). |

The answer:

```json
{"epoch":"k3J9xQe2uM0vTb7w","reset":false,"missed":false,
 "ops":[{"seq":42,"received_ms":1700000000000,"op":{"op":"pin","id":"drone-1","lat":37.5663,"lon":126.9779}}]}
```

- `epoch` names the relay's current numbering: 16 to 64 characters from `A`-`Z`, `a`-`z`, `0`-`9`, `-` and `_`.
  Sequence numbers start at 1 in each epoch and go up by one for each operation taken. A relay gets a new epoch
  whenever its numbering starts again, for example each time a relay that keeps its queue in memory starts.
- **When `epoch` matches**, the relay forgets every operation up to `after`, since the gateway is done with them, and
  answers with the ones after it. `reset` is `false`. An `after` larger than the last sequence number the relay has
  given is a bad parameter.
- **When `epoch` is missing or does not match**, the relay forgets nothing and answers from the oldest operation it
  holds, with `reset` set to `true`. The gateway starts over from there.
- `missed` is `true` when an operation numbered above `after` (above 0 when `reset` is `true`) was dropped because it
  waited longer than the relay keeps operations. Operations the gateway is done with do not count.
- `ops` holds at most `limit` operations, in order, and stops before the answer would pass 4 MiB, but always holds at
  least one if any are waiting. `received_ms` is when the relay took the operation, in milliseconds since 1970, a whole
  number from 0 to 2^53-1.
- A parameter that appears twice, is not one of these four, or is out of its range (a number that is not a whole
  number in its range, or an `epoch` not in the form above) gets `400 {"error":"invalid_query"}`.

One relay serves one gateway, because fetching with `after` also tells the relay what it may forget.

### Health: `GET /v1/health`

Needs no token and answers `200 {"ok":true}`, for a load balancer or a monitoring check.

### Anything else

An unknown path gets `404 {"error":"not_found"}`, and a known path with the wrong method gets
`405 {"error":"method_not_allowed"}`.

### Limits of the example relays

| Limit | Value |
|---|---|
| Request body | 2 MiB |
| Operations in one `POST` | 100 |
| Operations waiting | 10000, and 64 MiB of them in all, counted as the operations' JSON |
| How long an operation waits | 24 hours by default (`--keep-hours`) |
| Operations in one answer | 100, and at most 4 MiB |
| `wait` | 30 seconds |

The app's own limits (spec section 11) apply after that: for example 500 pins, markers and notes, 50 photos, and track
points at 1 per second with bursts up to 100. The gateway waits whenever the app asks it to, so a burst sent to the
relay reaches the map more slowly, and a queue fed faster than that for long fills up. Send a moving position as a
`pin` or `marker` with the same `id` each time, which replaces it, and track points at most once a second.

Every refusal counts against the gateway in the app. After more than 20 in a minute the app stops listening to it for
a minute, and longer each time it happens again, up to an hour (spec section 10). So fix what keeps being refused,
such as moves of ids that do not exist or points for a finished track, rather than sending it again.

## What the gateway does

- It fetches with `wait=25`, or `wait=0` with `--once`, and applies each operation in order through the SDK, as the
  table above says. If an answer with no operations comes back within a few seconds, it waits a few seconds before
  asking again, so a relay that does not hold fetches open is not flooded.
- **The app accepted it, or refused it for good** (for example `not_permitted` for a kind the person did not allow,
  `kind_conflict`, `limit_items`, or a value the SDK refuses before sending): the gateway notes the operation as done
  and saves its place, before going on. A refusal is printed and skipped, so one bad operation cannot hold up the rest.
  An operation the gateway cannot turn into an SDK call is refused the same way, never a crash.
- **The app could not be reached**, or is paused: the gateway keeps the operation, and the SDK sends the same request
  again after 1, 2, 4, up to 60 seconds, with some random variation (spec section 13). It keeps its sequence number, so
  if the app applied it and only the answer was lost, the app does not apply it twice (spec section 7). With `--once`
  the gateway gives up once an operation has been in trouble for about 30 seconds, however the trouble shows: no
  connection, no answer, or every answer lost on the way. The SDK starts its own count again at each new connection,
  so the gateway keeps this count itself. An app that takes the connection but never answers shows trouble only once
  the SDK stops waiting for the answer, so that case takes a little longer, up to about a minute. The gateway then
  exits with code 3. Until it gets through, the operations wait on the relay.
- **Something went wrong with the connection rather than the operation**, such as an answer the SDK cannot use or
  another program using the gateway's key: the operation is never counted as refused. The gateway tries again, or stops
  with code 1, and the operation stays on the relay.
- **The relay could not be reached, or answered `408`, `429` or a server error**: the gateway waits in the same way
  and tries again. With `--once` it gives up after about 30 seconds, counting the time each try takes, and exits with
  code 3.
- **`reset`**: if the gateway had a place with that relay, it prints that the relay started a new numbering. Either
  way it goes on from the operations given.
- **`missed`**: it prints once that some operations expired on the relay before it got them. The relay goes on
  answering `missed` until the gateway's place moves past them, so the gateway does not repeat the line for the same
  place.

Its place is a small file, `relay-state.json`, in the profile's folder. It holds the relay's address, `epoch` and the
last finished `seq` (each SDK may add a little of its own), is readable only by its owner, and is replaced whole, never
half written. A gateway given another relay address starts again from that relay's oldest operation.

**Delivery is at least once.** An operation is acknowledged to the relay only after the app has taken or refused it. If
the gateway stops between the two, or gives up on a request whose answer was lost, the operation comes again. Pins,
markers, notes, photos, moves and deletes come out the same when repeated, and a finished track refuses more points,
but the points of a `track` operation can then appear twice.

## Security

- **Use HTTPS** between your devices and the relay, and between the relay and the gateway. The example relays speak
  plain HTTP and are meant to sit behind an HTTPS reverse proxy. The gateways refuse a relay address that is not
  `https://`, except on the same computer.
- **What a token can do.**
  - A leaked **source token** lets someone write to the map through the gateway, within the kinds the person allowed
    and the app's limits: add, move and delete the gateway's items. It cannot read anything. Replace it on the relay
    and on your devices.
  - A leaked **gateway token** lets someone read what is waiting on the relay, and make the relay forget it. Replace it
    on the relay and the gateway.
  - Neither token reaches the app itself. To cut the gateway off, remove it in the **Devices** tab of the app's
    settings.
- **The relay sees everything in plain text**: positions, titles, notes and photos. The Erdigo protocol's encryption
  covers only the step from the gateway to the app. Run the relay on a server you trust. It keeps each operation only
  until the gateway is done with it or it expires, in memory (or, for PHP, in its data folder), and it does not log
  what operations hold.
- **The gateway's key is the pairing.** Whoever has the gateway's state folder can write as the gateway. Keep the folder
  readable only by the account that runs it, as the SDK does for its own files.
- **Limit requests at the proxy.** The example relays check sizes and tokens but do not limit how often a token may
  send.

## Deploying

### The relay behind Caddy

[Caddy](https://caddyserver.com/) gets and renews a certificate by itself. With the relay on `127.0.0.1:8080`:

```text
relay.example.com {
	request_body {
		max_size 2MiB
	}
	reverse_proxy 127.0.0.1:8080
}
```

Any reverse proxy works the same way, for example nginx with `client_max_body_size 2m;` and `proxy_read_timeout 60s;`,
so that a fetch waiting 25 seconds is not cut off. A proxy that reads each request whole before passing it on also
keeps slow clients away from the relay, which the example relays rely on.

### Keeping the gateway running

On Linux, including a Raspberry Pi, a systemd service runs it at start-up. Run it as the user who paired it, and keep
the token in a file only that user can read:

```ini
# /etc/systemd/system/erdigo-gateway.service
[Unit]
Description=Erdigo cloud gateway
After=network-online.target
Wants=network-online.target

[Service]
User=pi
EnvironmentFile=/home/pi/.config/erdigo-gateway.env
ExecStart=/home/pi/erdigo-gateway run --relay https://relay.example.com
Restart=always
RestartSec=10

[Install]
WantedBy=multi-user.target
```

`erdigo-gateway.env` holds one line, `ERDIGO_RELAY_TOKEN=...`, and is mode 0600. Put the command for your SDK's gateway
in `ExecStart`.

On Windows, run it from Task Scheduler at log-on, as the user who paired it, because the device key is kept for that
user (the .NET SDK encrypts it with DPAPI). On a Mac, a launch agent does the same.

## Using your own server instead

The gateway needs only `GET /v1/ops` as described above. A server that already has your devices' data can answer it,
and then the example gateways work with it unchanged. Or change the gateway's fetching part to read from your server, an
MQTT topic or a database, and keep the rest: the pairing, the order, the saved place, and the handling of refusals and
retries.

## What was tested

- **Each relay**, in its SDK's CI: every case in [`cloud-relay-cases.json`](cloud-relay-cases.json), and the rest of
  the API: each token's role, the order of the checks, the size limits, a batch of 100 operations, whole batches, a
  full queue, epochs, forgetting, expiry with a clock of its own, paging and waiting fetches.
- **Each gateway**, in its SDK's CI, with its own relay and a mock receiver: every kind of operation, a second run with
  nothing to do, kinds the person did not allow, an answer lost on the way back (applied once), the app out of reach,
  never answering or losing every answer, the relay out of reach, a relay that starts a new numbering, expired
  operations, a wrong token and answers that break the API.
- **Every gateway with every relay**, by hand on one Mac, with the Swift SDK's mock receiver as the app. Each of the six
  gateways fetched from each of the six relays (36 pairs): every kind of operation arrived, a second `run --once` had
  nothing to do, and the relay had forgotten what was delivered. Each gateway was also run against a server that answers
  every fetch at once (one fetch every 2 to 5 seconds at most), with `--once` against a relay address where nothing
  listens (code 3 after about 30 seconds), through a proxy that loses the app's answers (applied once, and with every
  answer lost, code 3 after about 30 seconds), against an app that takes the connection and never answers (code 3 after
  45 to 50 seconds), and with a second `run` on a profile in use (code 1).

Not tested: the released Erdigo app (only the SDKs' mock receivers), a real HTTPS reverse proxy such as Caddy or nginx,
a relay on the internet, the PHP relay under Apache, nginx or shared hosting, running for days, and heavy load. The
relays and gateways have not had a security audit.
