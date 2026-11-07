# Erdigo integration protocol, version 1

**Status: draft.** The wire format may still change before the first release of a receiver. The released Erdigo
app does not yet accept these connections. Until it does, use the mock receiver that comes with each SDK.

The key words MUST, MUST NOT, SHOULD, SHOULD NOT and MAY are used as described in RFC 2119.

## 1. What this protocol does

A **device** (a sensor, a drone, a Raspberry Pi, a script) writes **items** to a map kept by a **receiver**
(the Erdigo app on a person's computer), over the local network.

- **Write only.** A device can add, move and delete its own items. It can read nothing: no map, no
  location, no list of people. A receiver sends a device handshakes, acknowledgements and errors, and
  nothing else.
- **Local network only.** There is no internet path in version 1.
- **Permission first.** A device gets in only after a person types a one-time code into it and then
  confirms the request on the receiver.
- **The receiver trusts nothing a device says.** Every limit in this document is enforced by the receiver,
  whatever an SDK does.

Item kinds: `pin`, `marker`, `note` (text only), `photo` (JPEG or PNG only) and `track`.

Not in version 1: reading anything, video or cameras, messages between people, internet relay, joining
friend sharing, HEIC images, and short codes.

The reasoning, and what this design does not protect against, is in [threat-model.md](threat-model.md).

## 2. Transport

### 2.1 Finding a receiver

A receiver advertises the Bonjour service `_erdigo-int._tcp` with the TXT record `v=1`.

- The instance name MUST be random, MUST NOT contain the computer's or the person's name, and SHOULD change
  every hour. Note that Bonjour also gives out the computer's own network name, which often does contain a
  person's name (see the threat model).
- A receiver SHOULD advertise and listen only while it has a paired device or an open pairing window.
- A receiver MUST NOT share a port with any other feature.
- A receiver MUST refuse connections from outside the local network.

A device SHOULD try its last known address first. Otherwise it tries the advertised receivers in turn, and MAY
stop after a limit (the macOS SDK tries eight), so a crowded network can hide a receiver. A receiver it is not
paired with stays silent. An advertisement is not authenticated and proves nothing about who is behind it. Only the
handshake does.

### 2.2 Bytes on the connection

A connection is TCP. It starts with a five-byte **preamble**, in the clear, from the device:

| Bytes | Meaning |
|---|---|
| `45 49 4E 31` | ASCII `EIN1` |
| `01` | Mode: connect (section 5) |
| `02` | Mode: pair (section 4) |

Everything after that is a sequence of **frames**: a two-byte big-endian length followed by that many bytes
of one Noise message. A frame is at most 65535 bytes. During a handshake a frame MUST NOT exceed 1024 bytes.

## 3. Cryptography

The protocol uses the [Noise Protocol Framework](https://noiseprotocol.org/noise.html) with X25519,
ChaCha20-Poly1305 and SHA-256.

| Mode | Noise protocol name |
|---|---|
| Pair | `Noise_XXpsk0_25519_ChaChaPoly_SHA256` |
| Connect | `Noise_IK_25519_ChaChaPoly_SHA256` |

- The **prologue** is the five preamble bytes, so changing the preamble makes the handshake fail.
- Implementations MUST reject any Diffie-Hellman result that is all zeros, which is what low-order public
  keys produce.
- A long-term public key MUST be canonical: below 2^255-19, with the top bit clear. X25519 ignores that bit, so
  without this rule one private key could pose as two devices.
- Each transport message uses the Noise counter nonce: 32 zero bits followed by the 64-bit little-endian
  counter. A session ends before the counter reaches 2^64-1. Rekeying is not used.
- A message that fails authentication MUST end the connection.

**Keys.** Each side has one long-term X25519 key used for this protocol and nothing else. A device's private
key is the only secret it holds. A receiver MUST NOT use a key it uses for any other purpose, and MUST NOT
store device keys alongside the keys of any other feature.

**Fingerprint.** `SHA-256("erdigo-integration-v1/fingerprint" || public_key)`, as 64 lowercase hexadecimal
digits. People are shown the first 16 digits in groups of four.

## 4. Pairing

### 4.1 The code

The person opens a **pairing window** on the receiver, which shows a code, a QR code and the first 16 digits of
its key fingerprint, for 10 minutes.

- There is one window at a time.
- The code is 100 random bits from a secure source, written as 20 characters of Crockford base32, grouped as
  `XXXXX-XXXXX-XXXXX-XXXXX`. The alphabet is `0123456789ABCDEFGHJKMNPQRSTVWXYZ`.
- When reading a code, an implementation ignores hyphens and white space, uppercases letters, reads `O` as
  `0`, and reads `I` and `L` as `1`. Anything else, or any length but 20, is an error.
- The QR code holds `erdigo-pair:1?code=XXXXX-XXXXX-XXXXX-XXXXX&fp=<receiver fingerprint>`. No other field is
  allowed. In particular it never holds an address.
- A reader MUST refuse QR text without exactly one code and exactly one valid `fp`, which is 64 hexadecimal digits. It
  MUST also refuse a repeated field, an unknown field and an empty field. A tool MUST NOT write QR text without a
  valid fingerprint. A bare typed code has no `fp`, and is handled as "Checking the receiver" describes.

The Noise pre-shared key is `HKDF-SHA256(ikm = the 20 characters as ASCII, salt = "erdigo-integration-v1/pairing-psk", info = empty, length = 32)`.

An SDK's tools MUST NOT accept a code as a command-line argument, because arguments appear in process lists
and shell history. They MAY read it from the keyboard or from a file only its owner can read.

**Checking the receiver.** A typed code proves only that the other side knows the code, not that it is this
receiver. The fingerprint in the QR code proves that, and needs no further step from the person. With a typed code, a
tool MUST ask the person before it sends message 3 (section 4.2), because the receiver stops showing its fingerprint
once the person there has decided:

1. The tool runs the exchange up to message 2, which gives it the receiver's fingerprint, and closes the connection.
2. It shows the fingerprint, and asks the person to compare it with the one on the receiver's screen. Anything typed
   before the fingerprint appeared is not an answer. End of input, or any other answer, counts as no.
3. On a no it stops. The receiver has then seen neither the device's key nor its name.
4. On a yes it connects again and pairs, comparing the fingerprint as if it came from a QR code.

A tool MUST NOT save a pairing or send items before that yes. A tool that cannot show the fingerprint MUST refuse a
typed code. A tool whose standard input is not a terminal cannot ask the person, because what arrives there may have
been written before the fingerprint existed, so it MUST refuse a typed code too. A program that calls an SDK with its
own question, such as `Pairing.pair` with its own `confirmKey`, remains responsible for a person answering it, as the
threat model says.

### 4.2 The exchange

```
device                                               receiver
  preamble 45 49 4E 31 02 ------------------------------>
  message 1 (psk, e; empty payload) -------------------->   checks the code
  <---------------------- message 2 (e, ee, s, es; empty)   device checks the code and,
                                                            if it has one, the fingerprint
  message 3 (s, se; pair_request) ---------------------->
  <-------------------------- pair_status "pending"
                       the person decides
  <-------------------------- pair_status "approved" or "denied"
  connection closes
```

Because the code is mixed in before the first message, someone without the code cannot produce a first message
the receiver will answer, and so never learns the receiver's key. The receiver also proves it knows the code in
message 2, before the device reveals its own key or name.

A recording of a first message does not help either, because the receiver refuses an ephemeral key it has already
seen in this window. A first message it has not yet seen is answered when delivered, but message 2 reveals no key
to someone without the code, and the handshake cannot go further.

If the person scanned a QR code, the device MUST compare the receiver's key with its fingerprint after message
2, and stop on a mismatch before sending message 3.

`pair_request`:

```json
{"t":"pair_request","versions":[1],"sdk":"example/1.0","name":"Drone A","kinds":["pin","track"]}
```

| Field | Rule |
|---|---|
| `versions` | 1 to 16 integers. If none is supported, the receiver answers `version_unsupported` and closes. |
| `sdk` | 1 to 64 ASCII letters, digits, `.`, `_`, `/` or `-`. For diagnostics only. |
| `name` | At most 256 bytes. Untrusted (see below). |
| `kinds` | At least one item kind, without repeats. |

`pair_status`:

```json
{"t":"pair_status","status":"pending"}
{"t":"pair_status","status":"approved","kinds":["pin"],"counter":0}
{"t":"pair_status","status":"denied"}
```

### 4.3 What the receiver must do

**The confirmation dialog.** A correct code is not enough. The receiver MUST ask the person, showing the
device's name, the kinds it asks for with a checkbox for each, and the address it connected from. For example:
"'Drone A' wants to put pins and tracks on your map." Nothing is stored until the person allows it. If nobody
answers within 120 seconds, the answer is no.

**The name is untrusted.** Before showing it, the receiver removes the characters listed in section 6.3, trims
white space, cuts it to 40 characters and lets the person edit it. An empty result shows as a generic name.

**The person decides.** The approved kinds MUST be a subset of the requested kinds.

**What is stored.** The device's public key, its name, the allowed kinds, the sequence state, the highest counter
(section 5.1) and the last connection time. No passwords or tokens exist in this protocol.

**Failures.**

| Situation | What the receiver does |
|---|---|
| No window is open, or the window expired | Silence (section 5.2). No dialog. |
| Wrong code | Silence. The code stays valid, so a stranger cannot spoil it. The address is delayed as in section 5.2. |
| A handshake that ends after message 2 | Nothing is stored, and the person is asked nothing. The code stays valid, because a device stops here while its person compares the fingerprint. The address is delayed as in section 5.2. |
| A first message with an ephemeral key already seen in this window | Silence. It is a replay. A receiver remembers at least the latest 4096 keys of a window. It MUST NOT go silent because this list is full, since that would let a holder of the code hide its misuse from the real device. |
| A device key that is not canonical | Closes the connection. Nothing is stored. |
| The same device uses the code again | Answers as it did the first time, because the device may have lost the connection. |
| A different device uses a code that was already used | Warns the person ("Another device also used this code"), removes the pairing made with that code, stops using the code, and answers `code_reused`. |

The last rule catches a stolen code. After either device has been refused this way, the person starts again
with a new code.

## 5. Connecting

### 5.1 The exchange

```
device                                               receiver
  preamble 45 49 4E 31 01 ------------------------------>
  message 1 (e, es, s, ss; 8-byte counter) ------------->   checks the device and counter
  <---------------------------- message 2 (e, ee, se; empty)
  hello ------------------------------------------------>
  <------------------------------------------------ welcome
  requests and answers, one at a time
```

Message 1 MUST be exactly 104 bytes.

**The counter** is an unsigned 64-bit big-endian number, at most 2^53-1. The receiver stores the highest value it
has accepted from each device, durably, and keeps it when the device is removed or paired again. It answers only
if the new value is strictly greater. This stops a recording of a message it has already seen from being used to
find out whether this receiver knows that device.

**The limit of this protection.** A first message the receiver has not yet seen has a higher counter, so it is
answered once. The threat model says what this means in practice.

A device that has reached 2^53-1 can no longer connect with this key, and needs a new one.

A device SHOULD use `max(current time in milliseconds, last used value + 1)` and MUST save the value to durable
storage before sending it. `pair_status` carries the receiver's stored value in `counter`, and from then on the
device uses a greater one. So a device that has lost its counter, or whose clock is behind, gets back in by pairing
again. Until then the receiver stays silent towards it.

**Replacing a connection.** A device has at most one connection. A new connection replaces the old one, which
gets a `replaced` error, but only **after the receiver has decrypted the new connection's `hello`**. A replayed
first message cannot produce a `hello`, so it can never disturb a working connection.

### 5.2 Silence

Before a device has proven it is allowed, the receiver sends **zero bytes**. It does this for all of the
following, and the behaviour MUST be the same for each: an unknown preamble, a malformed or oversized message, a
failed decryption, a device that is not allowed, a replayed counter, a wrong pairing code, no open window, a paused
receiver, a device under a penalty, and a peer that does not finish in time.

**The receiver MUST read the preamble and the whole first message before it looks at its own state.** Otherwise how
long a connection stays open would reveal the state: a peer that sends only a preamble would be closed quickly when
no window is open or the receiver is paused, and left waiting when not. Done this way, a new connection that sends
nothing more sees the same deadline whatever the state. Pausing is the exception: it ends connections already open
and stops advertising, so it can be seen (section 15, and the threat model).

The receiver then waits a random time between 0.2 and 1.0 seconds, closes the connection, and puts that address
under a penalty, starting at 0.5 seconds and doubling up to 60 seconds. While under a penalty, an address gets one
connection at a time and one second to finish the first message. It is still read, because a good device may share an
address with a failing program, and a good device's first message arrives at once. A later successful handshake clears
the penalty. A peer that connects and leaves within a second, having sent nothing, is not penalised.

So a device cannot tell these cases apart, and neither can a stranger scanning the network. An SDK reports all
of them as "no answer".

### 5.3 Version negotiation

```json
{"t":"hello","versions":[1],"sdk":"example/1.0"}
```

The receiver picks the highest version it shares with the device and answers:

```json
{"t":"welcome","version":1,"last_seq":41,"kinds":["pin","track"],"limits":{"items":500,"photos":50,"photo_bytes":1048576,"note_bytes":4096,"title_chars":200,"track_points":3000,"open_tracks":5,"tracks":200,"msgs_per_s":5,"msg_burst":20,"points_per_s":1,"point_burst":100,"disk_bytes":31457280}}
```

If there is no shared version it answers `{"t":"error","code":"version_unsupported","supported":[2],"fatal":true}`
and closes. A `hello` with anything else wrong ends the connection.

`point_burst` MUST be at least 100, the largest batch of points one message can carry. `limits` tells a
well-behaved device what to expect. A device MUST NOT rely on it for safety, and a receiver MUST enforce its
own limits regardless.

## 6. Messages

After `welcome`, the device sends one request at a time and waits for its answer.

### 6.1 Encoding

Every message is one JSON object, UTF-8 encoded, in one Noise message, at most 65519 bytes. The decoding is an
allow-list. A message MUST be rejected if it has:

- an unknown `t`, or any field not listed for that `t` (including fields like `uid` or `name`);
- a duplicate key, invalid UTF-8, a byte order mark, an unpaired surrogate escape, or anything but white space
  after the object;
- a number that is not finite when read as a 64-bit float, or an integer field written as a fraction;
- nesting deeper than six levels.

Fields may appear in any order. [The JSON Schema](schema/integration-v1.schema.json) describes the shape of every
message, but it cannot express every rule here.

### 6.2 Identifiers and coordinates

- **Item id:** 1 to 64 ASCII letters, digits, `-` or `_`. An item's real address is the pair (device key, id),
  so two devices can use the same id without touching each other. Ids share one space across all kinds on a
  device.
- **Coordinates:** `lat` within -90 to 90, `lon` within -180 to 180, in degrees (WGS 84).

### 6.3 Text

`title` is 1 to 200 Unicode scalar values. `text` (notes) is 1 to 4096 bytes of UTF-8.

Text MUST NOT contain U+0000 to U+001F (except line feed and tab, in `text` only), U+007F to U+009F, U+061C,
U+200E, U+200F, U+202A to U+202E, U+2066 to U+2069, U+2028, U+2029 or U+FEFF. These are control characters
and invisible direction overrides, which can make text read as something other than what it is.

Receivers MUST show text as plain text. A link in a note MUST NOT open by itself and MUST NOT produce a preview.

### 6.4 Requests

Every request has `t` and `seq`.

| `t` | Other fields | Effect |
|---|---|---|
| `ping` | | None. Checks the connection. |
| `put` | `kind` = `pin`; `id`, `lat`, `lon`; optional `title` | Creates or replaces a pin. |
| `put` | `kind` = `marker`; `id`, `lat`, `lon`; optional `title`, `color` | Creates or replaces a marker. `color` is `red`, `orange`, `yellow`, `green`, `blue`, `purple` or `gray`. |
| `put` | `kind` = `note`; `id`, `lat`, `lon`, `text` | Creates or replaces a note. |
| `move` | `id`, `lat`, `lon` | Moves a pin, marker, note or photo. Unknown id: `not_found`. Tracks cannot be moved. |
| `delete` | `id` | Deletes an item of this device. An unknown id succeeds, so a retry is safe. |
| `delete_all` | | Deletes every item of this device, and nothing else. |
| `photo_begin` | `id`, `lat`, `lon`, `type`, `size`, `sha256`, `chunks`; optional `title` | Section 8. |
| `photo_chunk` | `id`, `index`, `data` | Section 8. |
| `photo_end` | `id` | Section 8. |
| `track_append` | `id`, `points`; optional `title` | Section 9. |
| `track_finish` | `id` | Section 9. |

Replacing an item with a different kind fails with `kind_conflict`. A device can never reach an item that is
not its own, because it has no way to name one.

### 6.5 Answers

```json
{"t":"ack","seq":42,"dup":false}
{"t":"error","code":"not_found","seq":42}
```

`error` may also carry `last_seq`, `retry_after_ms`, `supported` and `fatal`. After `"fatal":true` the receiver
closes the connection.

## 7. Sequence numbers

`seq` is an integer from 1 to 2^53-1. The receiver stores, for each device, the last number it used up
(`last_seq`), a hash of that message, and the answer it gave.

| New message | What happens |
|---|---|
| `seq` greater than `last_seq` | Runs. Gaps are allowed. |
| `seq` equal to `last_seq`, same bytes | Does not run again. Repeats the stored answer. An `ack` has `"dup":true`. |
| `seq` equal to `last_seq`, different bytes | `seq_conflict`. |
| `seq` less than `last_seq` | `stale_seq`, with the current `last_seq`. |

A request that runs and fails still uses up its number, and a resend gets the same error. These do **not** use up
a number: a message that does not parse, `rate_limited`, `paused` and `internal_error`.

**Before answering,** a receiver MUST record a change together with `last_seq`, the message hash and the answer, in
storage that survives the receiver's own process ending. An `ack` therefore means the change is not lost if the receiver
crashes. This does not promise that it survives a power failure. If the receiver cannot record it, it answers
`internal_error`, which does not use up the number, and has not applied the change, so a resend is safe.

**After a lost answer,** the device reconnects and sends the same bytes with the same number. It is applied at most once.

**After losing its state,** a device reads `last_seq` from `welcome` and continues from there. A device does not
have to store sequence numbers at all, only the request in flight.

Two programs using one device key at once will conflict, and one will replace the other's connection. Give each
program its own key.

## 8. Photos

A photo is sent in pieces, each with its own `seq`.

```json
{"t":"photo_begin","seq":10,"id":"view","lat":37.5663,"lon":126.9779,"title":"View","type":"image/jpeg","size":70000,"sha256":"<64 lowercase hex digits>","chunks":2}
{"t":"photo_chunk","seq":11,"id":"view","index":0,"data":"<base64>"}
{"t":"photo_chunk","seq":12,"id":"view","index":1,"data":"<base64>"}
{"t":"photo_end","seq":13,"id":"view"}
```

- `type` is `image/jpeg` or `image/png`. Nothing else is allowed. `size` is at most 1048576 bytes.
- `chunks` MUST equal `ceil(size / 48000)`. Each chunk holds at most 48000 bytes, as canonical base64 with padding.
- `index` starts at 0 and goes up by one. Out of order: `invalid_state`, and the upload is abandoned.
- A device has one upload at a time. A new `photo_begin` replaces an unfinished one. An upload ends with its
  connection, so after a reconnection the device starts again from `photo_begin`.

On `photo_end` the receiver MUST, in this order:

1. Check the total size and the SHA-256 against `photo_begin`.
2. Check the first bytes match `type`: `FF D8 FF` for JPEG, `89 50 4E 47 0D 0A 1A 0A` for PNG.
3. Ask the image decoder for the format it actually found, and require it to be the same one, without giving the
   decoder a hint. Require exactly one image in the file.
4. Read the dimensions before decoding any pixels, and refuse more than 64 million pixels. A small PNG can
   otherwise expand to gigabytes.
5. For a JPEG, walk its structure and refuse more than 32 scans, a second start-of-image marker, or anything between
   segments that is not a marker. A progressive JPEG can repeat its scans so that a small file takes half a minute to
   decode, and a second start-of-image marker can hide scans from a careless count.
6. Decode at no more than 4096 pixels on the long side.
7. Draw the result into a new image with a fixed colour profile, and **encode that**, with no metadata carried over,
   so that EXIF location, an embedded profile and the like do not survive.
8. Apply the usual rules for storing an item, since things may have changed during the upload: `kind_conflict` if the
   id now belongs to another kind of item, and `limit_photos` or `limit_disk` if a limit has been reached.

A failed check in steps 1 to 6 gives `photo_invalid`, and nothing is stored. Decoding MUST NOT hold up other requests
or the removal of a device, so it runs apart from the rest of the receiver, one image at a time. A slow image can still
make other devices' photos wait for their turn, for about as long as the limits above allow. A decode for a connection
that has ended is skipped.

## 9. Tracks

```json
{"t":"track_append","seq":20,"id":"walk","title":"Morning walk","points":[{"lat":37.5663,"lon":126.9779,"ts":1700000000000,"alt":38.5}]}
{"t":"track_finish","seq":21,"id":"walk"}
```

- `points` has 1 to 100 entries. `ts` (optional) is milliseconds since 1970, from 0 to 2^53-1. `alt` (optional)
  is metres, from -1000 to 100000. A point has no other fields.
- The first `track_append` for an id opens the track. At most five tracks can be open at once, and a device can
  have at most 200 in all, open or finished.
- Past 3000 points, the oldest points are dropped, so a long recording keeps working.
- After `track_finish`, more points give `invalid_state`. `delete` it to reuse the id.

## 10. Errors

| Code | Meaning | Uses up `seq` |
|---|---|---|
| `invalid_message` | Not what this document allows | No |
| `invalid_id` | Bad item id | No |
| `invalid_coordinate` | Coordinate out of range | No |
| `invalid_text` | Empty text, or a forbidden character | No |
| `too_large` | Over a size limit | No |
| `not_permitted` | The person did not allow this kind | Yes |
| `not_found` | No such item of this device | Yes |
| `kind_conflict` | The id belongs to another kind of item | Yes |
| `invalid_state` | The request does not fit the current state | Yes |
| `seq_conflict` | Same number, different content | No |
| `stale_seq` | The number is already past | No |
| `limit_items` | Pins, markers and notes are at their limit | Yes |
| `limit_photos` | Photos are at their limit | Yes |
| `limit_tracks` | Too many tracks, open or in all | Yes |
| `limit_disk` | Storage is at its limit | Yes |
| `photo_invalid` | The photo failed a check in section 8 | Yes, except for an unknown `type` in `photo_begin` |
| `rate_limited` | Too fast. Wait `retry_after_ms` and resend the same bytes. | No |
| `too_many_errors` | Too many rejections. Fatal. | |
| `version_unsupported` | No shared version. Fatal. | |
| `paused` | The person paused all connected devices. Fatal. | No |
| `replaced` | A newer connection from this device took over. Fatal. | |
| `code_reused` | The pairing code was already used by another device. Fatal. | |
| `internal_error` | The receiver failed | No |

A device MUST treat a code it does not know as a permanent failure of that request.

A rejection message is never a dialog. Rejections cannot make a window appear on the person's screen.

Each rejection counts against the device. `rate_limited` counts only if the device did not wait as long as it
was last told to, for that kind of limit. It may arrive up to 50 milliseconds early once, and uses that slack up until
it next arrives on time. So a device that follows `retry_after_ms` is never punished for it. After more than 20 in a minute,
the receiver answers `too_many_errors` with `retry_after_ms`, closes the connection, and stays silent towards that
device for that long. The wait starts at 60 seconds and doubles up to an hour.

## 11. Limits

A receiver MUST enforce these per device, and MAY announce lower values in `welcome`.

| Limit | Value |
|---|---|
| Authenticated connections | 1 per device |
| Connections not yet authenticated | 8 in total, 2 per address. Beyond this, nothing is read. A pairing waiting for a person no longer counts. |
| Time to finish a handshake | 10 seconds |
| Idle authenticated connection | Closed after 5 minutes |
| Peer that stops reading | Closed after 30 seconds |
| Message size | 65519 bytes |
| Message rate | 5 per second, bursts up to 20 |
| Pins, markers and notes together | 500 |
| Photos | 50, each at most 1048576 bytes as sent |
| Note | 4096 bytes |
| Title | 200 characters |
| Track points | 3000 per track, oldest dropped first |
| Point rate | 1 per second, bursts up to 100 |
| Open tracks | 5 |
| Tracks in all | 200 |
| Decoded photo | 64 million pixels, 32 JPEG scans |
| Storage | 30 MiB per device, 200 MiB in total |

## 12. Storing items on the receiver

- Items MUST be stored apart from the person's own data and from anything shared with friends.
- Items MUST NOT leave the receiver: not to friends, not in a share link, not in an export file.
- Items MUST be labelled on the map with the device they came from, and a person MUST be able to remove all of
  a device's items at once.

## 13. When a device cannot reach the receiver

A computer sleeps and an app gets closed. From the device's side all of this looks like silence, so a device retries:

- Wait 1, 2, 4 ... up to 60 seconds between attempts, with about 20 percent random variation.
- Resend the same request with the same `seq` (section 7), or restart a photo from `photo_begin` (section 8).
- Decide how long to keep trying. A sensor may retry forever, and a one-shot command may give up after 30 seconds.

## 14. Removing a device

**On the receiver.** Removing a device from the list MUST end its connection within one second, with no message.
Its key stays on a list of removed keys, and it stays silent towards that device. Getting back in takes a new code
and a new confirmation, and the dialog SHOULD mention that the device was removed before. The person chooses
whether its items stay. There is no automatic expiry. The receiver shows when each device last connected.

**A lost or stolen device.** Remove it on the receiver. That is the whole procedure: the device's key is then
useless, and it never held anything else. Until then it can write within its limits (see the threat model).

**A compromised receiver computer.** Its key can no longer be trusted. Forget the pairing on each device, make
the receiver create a new key, and pair again.

**One device, several receivers.** A device uses one key with any number of receivers, and keeps a separate
pairing and counter for each. Each receiver has its own sequence numbers and its own copy of the device's items.

## 15. If a flaw is found in version 1

There are three ways to stop version 1 connections, none of which rely on a server:

1. **Now, by the person:** a switch on the receiver pauses all connected devices. It closes every connection with
   `paused`, ends handshakes in progress, denies a pairing that is waiting, stops advertising, and stays silent until
   turned back on.
2. **For a flaw in the messages:** a receiver update removes `1` from its accepted versions. Devices get
   `version_unsupported` and report that the SDK needs updating.
3. **For a flaw in the handshake:** a receiver update stops accepting the `EIN1` preamble, so it falls silent to
   all version 1 devices. A later version uses a different preamble, and devices pair again.

Receivers deliberately do not check a remote "off switch": a receiver contacting a server for this would reveal
when it is running, and whoever controls that server could turn it off for everyone. Advisories are published
through this repository's security advisories.

## 16. Test vectors

[`vectors/`](vectors/README.md) holds:

- official Noise vectors, for checking the cryptography;
- complete handshakes and first messages, with fixed test-only keys, for checking every byte;
- messages a receiver must accept, and messages it must reject, with the required error code.

An implementation in any language should reproduce all of them. See also the mock receiver's test suite for the
behaviour that bytes alone cannot show: silence, removal, code reuse and limits.
