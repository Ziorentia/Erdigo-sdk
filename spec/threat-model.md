# Threat model for integration-v1

This document says what the protocol protects, who it protects it from, and, just as importantly, what it does
not protect against.

## What is being protected

1. **The person's map and location.** A device must not be able to read them.
2. **The person's own items and what they share with friends.** A device must not be able to change them.
3. **The person's computer.** A device or a stranger must not be able to crash it, fill its disk, run anything on
   it, or pester its owner with dialogs.
4. **The person's privacy on other networks.** The receiver must not give a stranger on a café network anything
   that identifies this person or this installation of the app: no key, no answer, no stable name of its own. The
   exceptions, including what the computer itself announces, are listed below.

## Who the attackers are

| Attacker | Can do |
|---|---|
| **A stranger on the same network** | Scan, connect, send anything, watch and change traffic |
| **Someone who saw the pairing code** | Everything above, plus use the code within its 10 minutes |
| **A paired device that was taken over** | Everything its key allows |
| **Someone who captured an earlier handshake** | Replay it, on this network or another |

Not in scope: someone who controls the receiver's computer, because they already have the map.

## How each is handled

### A stranger on the same network

| Attempt | Result |
|---|---|
| Identify this installation of the app | The service name is random and changes hourly, and the receiver answers nothing. (The computer's own network name is another matter. See below.) |
| Get the receiver's key | Impossible without the code or a paired key. The receiver sends zero bytes first (spec 5.2). |
| Tell an unknown device from a replay or a wrong code | Same silence, same pause, same penalty. |
| Find out from a new connection whether a pairing window is open or the receiver is paused | The receiver looks at its state only after the whole first message has arrived, so a new connection shows nothing. (Advertising is another matter. See below.) |
| Read or change traffic | Encrypted and authenticated by Noise. Changing a byte ends the connection. |
| Swap a key during pairing | Both keys and the request are bound into the handshake hash, and the code is mixed in before anything else. |
| Pretend to be the receiver during pairing | The device checks in message 2 that the other side knows the code, before revealing its key or name. |
| Guess the code | 100 random bits, tried over a network. Not feasible, so no lockout is needed, and a stranger cannot spoil the code by guessing badly. |
| Exhaust connections | Limits on unauthenticated connections, per address and in total. Handshake deadline. Every failed or timed-out attempt costs that address a growing delay. This limits the damage, but does not stop a determined flood (below). |
| Force a known key using a low-order point | Rejected: an all-zero result ends the handshake. |

### Someone who saw the pairing code

**Towards the receiver,** the code alone is not enough, because the person still confirms on the receiver and sees
the name and address. If the thief gets there first and the person confirms by mistake, the real device arrives
second. The receiver warns the person and removes the pairing. If the real device never arrives, the person sees a
device they did not expect in the list.

**Towards the device,** the code does less. See "A typed code does not prove who answered" below.

### A paired device that was taken over

It can write within its limits until removed. It cannot:

- read anything, because the protocol has no such message and the receiver sends nothing else;
- reach anyone else's items, because an item's address includes the sender's key, taken from the handshake;
- impersonate a person, because device keys are separate from every other key in the app;
- fill the disk or put unlimited items on the map, because of the limits in spec 11;
- trick the person through text: control and direction characters are refused, links are never opened or previewed;
- attack through images as easily: only JPEG and PNG, checked by content, limited in pixels, decoded away from the
  rest of the receiver, and encoded anew.

### Someone who captured an earlier handshake

Replaying a first message the receiver has already seen fails on the counter (spec 5.1), or in pairing on the
repeated ephemeral key, so the receiver stays silent. This stays true after the device is removed or paired again.
A replay cannot displace a live connection either, because that requires a `hello` only the real device can produce.
Replaying transport messages fails, because each session has fresh keys. A message the receiver has not seen is a
different matter, and is listed below.

## What this does not protect against

**A device's first message can occasionally link it to its receiver.** Someone who was near a paired device may be
able to confirm, once, that a particular computer is that device's receiver. This reveals only the link: no key, no
data and no way to go further. To reduce it, give a device a fixed address with `target: .address` rather than letting
it look around the network. A full fix would need a change to the protocol, such as a limit on how old a message may
be, which devices without a reliable clock could not meet.

**A typed code does not prove who answered.** The code proves only that the other side knows it. If someone else saw
the code, a device could end up paired with them instead of your receiver, and would send them its items until it is
paired again. Your receiver might show no warning. To be sure, scan the QR code, which carries the receiver's
fingerprint, or compare the fingerprint the tool shows with the one on the receiver's screen.

**Someone on the same network can disturb the service.** Devices may then get no answer, and items may not arrive.
This cannot reveal or change anything.

**A paired device can tell whether the computer is on the network.** It gets an answer when the computer is home,
and silence when it is not. Pair only devices you trust with that much.

**People on the same network can tell an Erdigo receiver is there, and on which computer.** The service type says
what it is. Bonjour also gives out the computer's own network name, which often contains a person's name and stays
the same across networks. macOS announces that name anyway, whether or not Erdigo is running, and the system API for
advertising a service offers no way to change it. Choose a neutral name for your computer in System Settings if
this matters to you.

**Advertising shows some of the receiver's state.** A paused receiver stops advertising, and a pause also ends open
connections, so someone watching can see that it happened. A receiver with no paired devices advertises only while a
pairing window is open, so its appearance shows that a window is open.

**People on the network can see traffic patterns:** that a device is sending, when, and roughly how much. Message
sizes are not hidden.

**A taken-over device can write nonsense within its limits until it is removed.** Fake pins can mislead. Items
are labelled with their device, so they can be recognised and removed together.

**Coordinates and text are what the device says they are.** Nothing checks that a sensor reports the truth.

**A lost device stays valid until removed.** There is no automatic expiry, because a sensor that goes quiet for
months is normal. The receiver shows when each device last connected.

**Secrets in memory.** Swift, like most languages, gives no guarantee that a key is wiped from memory. On the device
the key sits in a file only its owner can read, or in the keychain. Someone with the same user account or root can
take it, and a backup may hold a copy.

**The keys in use are not post-quantum.** Someone who records traffic today and has a suitable quantum computer
later could read it.

**Image decoders can have bugs.** The checks narrow what reaches the system decoder, but do not eliminate the risk.

**The mock receiver is a test tool.** It listens on the loopback interface by default. It follows the protocol's rules,
with these differences: it allows every pairing request unless started with `--ask`, it keeps everything in memory, so a
restart forgets devices and counters, it keeps no last-connection time, and it prints the pairing code. It has not been
hardened for a hostile network.

## What has and has not been checked

- The Noise implementation passes the official vectors for `IK` and `XXpsk3`, and for `NNpsk0`, which exercises a
  key mixed in before the first message. `XXpsk0` itself has no official vector, so the published pairing vector
  was checked against a second, independent Noise implementation (see [`vectors/README.md`](vectors/README.md)).
- Silence (bytes and timing), removal, replay, code reuse, pause and the limits in spec 11 have tests against the mock
  receiver, over real TCP.
- **No independent security audit has been done.**
- **The protocol has not yet been tested against the Erdigo app, which does not yet implement a receiver.**
