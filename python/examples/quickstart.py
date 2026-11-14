"""Pairs once, then writes a pin and a short track to an Erdigo receiver.

Start a mock receiver from the .NET or Swift SDK first, then run, with the port it printed:

    python examples/quickstart.py --host 127.0.0.1 --port PORT

The first run asks for the pairing code, shows the receiver's key and asks you to compare it. Later runs reuse the
pairing. State is kept in this edition's default folder under the profile "quickstart".
"""

from __future__ import annotations

import argparse
import getpass
import sys
import time

from erdigo_sdk import (
    DISCOVER,
    Coordinate,
    ErdigoClient,
    ErdigoError,
    ErrorKind,
    FileDeviceStore,
    ItemKind,
    PairingTicket,
    ReceiverTarget,
    TrackPoint,
    default_directory,
    pair,
)


def confirm_key(key: str) -> bool:
    """Asked only for a typed code, which proves only that the other side knows the code."""
    if not (sys.stdin.isatty() and sys.stdout.isatty()):
        return False  # Nobody could see this and answer.
    print(f"Receiver key: {key}")
    answer = input("If this matches the key shown on the receiver, type yes: ")
    return answer.strip().lower() == "yes"


def main() -> int:
    parser = argparse.ArgumentParser(description="Write a pin and a track to an Erdigo receiver.")
    parser.add_argument("--host")
    parser.add_argument("--port", type=int)
    options = parser.parse_args()
    target = ReceiverTarget.address(options.host, options.port) if options.host else DISCOVER

    store = FileDeviceStore(default_directory("quickstart"))
    identity = store.load_or_create_identity()
    pairings = store.pairings()
    if pairings:
        pairing = pairings[0]
    else:
        # Read the code from the keyboard, never from a command-line argument.
        ticket = PairingTicket.parse(getpass.getpass("Pairing code or QR text: "))
        try:
            pairing = pair(target, ticket, identity, "Quickstart", [ItemKind.PIN, ItemKind.TRACK], confirm_key=confirm_key)
        except ErdigoError as error:
            print(error, file=sys.stderr)
            return 1
        store.save(pairing)
        print(f"Paired. Allowed: {', '.join(kind.value for kind in pairing.kinds)}")

    with ErdigoClient(identity, pairing, store, target) as client:
        try:
            spot = Coordinate(37.5663, 126.9779)
            client.put_pin("city-hall", spot, "Seoul City Hall")
            now = int(time.time() * 1000)
            points = [TrackPoint(Coordinate(37.5663 + index * 0.0001, 126.9779), now + index * 1000) for index in range(5)]
            client.append_track("walk", points, "Short walk")
            client.finish_track("walk")
        except ErdigoError as error:
            if error.kind is ErrorKind.NO_ANSWER:
                print("No receiver answered. Check that it is running and on the same network.", file=sys.stderr)
            else:
                print(error, file=sys.stderr)
            return 1
    print("Sent a pin and a track.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
