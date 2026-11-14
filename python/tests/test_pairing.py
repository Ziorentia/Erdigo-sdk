"""Pairing: the QR text, a typed code that a person confirms, and the cases that must stop."""

from __future__ import annotations

import pytest

from erdigo_sdk import DeviceIdentity, ErdigoError, ErrorKind, ItemKind, PairingCode, PairingTicket, ReceiverTarget, pair
from erdigo_sdk import noise, protocol

from .receiver import TestReceiver


def _target(receiver: TestReceiver) -> ReceiverTarget:
    return ReceiverTarget.address("127.0.0.1", receiver.port)


def test_qr_text_pairs_and_keeps_what_the_person_allowed(receiver: TestReceiver) -> None:
    receiver.approved_kinds = (ItemKind.PIN,)
    identity = DeviceIdentity.generate()
    pairing = pair(_target(receiver), PairingTicket.parse(receiver.ticket.uri), identity, "Greenhouse", [ItemKind.TRACK, ItemKind.PIN], label="home")
    assert pairing.label == "home"
    assert pairing.receiver_key == receiver.public_key
    assert pairing.kinds == (ItemKind.PIN,)
    assert (pairing.last_host, pairing.last_port) == ("127.0.0.1", receiver.port)
    request = receiver.record.pair_requests[0]
    # Sent in the protocol's order, whatever order they were given in.
    assert request.kinds == (ItemKind.PIN, ItemKind.TRACK)
    assert request.name == "Greenhouse" and request.sdk == "erdigo-python/0.1.0" and request.versions == (1,)


def test_wrong_fingerprint_stops_before_the_receiver_hears_the_device(receiver: TestReceiver) -> None:
    ticket = PairingTicket(receiver.code, "0" * 64)
    with pytest.raises(ErdigoError) as raised:
        pair(_target(receiver), ticket, DeviceIdentity.generate(), "Device", [ItemKind.PIN])
    assert raised.value.kind is ErrorKind.FINGERPRINT_MISMATCH
    assert receiver.record.pair_first_messages == 1
    assert receiver.record.pair_requests == []


def test_typed_code_needs_a_question_and_contacts_nobody_without_one(receiver: TestReceiver) -> None:
    with pytest.raises(ErdigoError) as raised:
        pair(_target(receiver), PairingTicket(receiver.code), DeviceIdentity.generate(), "Device", [ItemKind.PIN])
    assert raised.value.kind is ErrorKind.INVALID_ARGUMENT
    assert receiver.record.connections == 0


def test_typed_code_that_is_not_confirmed_sends_no_key_and_no_name(receiver: TestReceiver) -> None:
    shown = []

    def refuse(key: str) -> bool:
        shown.append(key)
        # The question comes after message 2, before the receiver is asked anything.
        assert receiver.record.pair_requests == []
        return False

    with pytest.raises(ErdigoError) as raised:
        pair(_target(receiver), PairingTicket(receiver.code), DeviceIdentity.generate(), "Device", [ItemKind.PIN], confirm_key=refuse)
    assert raised.value.kind is ErrorKind.KEY_NOT_CONFIRMED
    assert shown == [protocol.short_fingerprint(receiver.fingerprint)]
    assert receiver.record.pair_requests == []
    assert receiver.devices == {}


def test_typed_code_that_is_confirmed_pairs_with_the_key_that_was_shown(receiver: TestReceiver) -> None:
    identity = DeviceIdentity.generate()
    pairing = pair(_target(receiver), PairingTicket(PairingCode(receiver.code.display.lower())), identity, "Device", [ItemKind.PIN], confirm_key=lambda key: True)
    assert pairing.receiver_key == receiver.public_key
    assert identity.public_key in receiver.devices
    # Once to find the key, once to pair.
    assert receiver.record.pair_first_messages == 2


def test_wrong_code_gets_no_answer(receiver: TestReceiver) -> None:
    other = PairingTicket(PairingCode.generate(), receiver.fingerprint)
    with pytest.raises(ErdigoError) as raised:
        pair(_target(receiver), other, DeviceIdentity.generate(), "Device", [ItemKind.PIN], handshake_timeout=1)
    assert raised.value.kind is ErrorKind.NO_ANSWER
    assert receiver.record.pair_requests == []


@pytest.mark.parametrize("answer, kind", [("deny", ErrorKind.PAIRING_DENIED), ("code_reused", ErrorKind.CODE_REUSED)])
def test_the_receivers_decision_is_reported(receiver: TestReceiver, answer: str, kind: ErrorKind) -> None:
    receiver.pairing_answer = answer
    with pytest.raises(ErdigoError) as raised:
        pair(_target(receiver), receiver.ticket, DeviceIdentity.generate(), "Device", [ItemKind.PIN])
    assert raised.value.kind is kind


def test_nobody_answering_on_the_receiver_times_out(receiver: TestReceiver) -> None:
    receiver.pairing_answer = "silent"
    with pytest.raises(ErdigoError) as raised:
        pair(_target(receiver), receiver.ticket, DeviceIdentity.generate(), "Device", [ItemKind.PIN], approval_timeout=0.5)
    assert raised.value.kind is ErrorKind.PAIRING_TIMED_OUT


def test_a_receiver_key_with_the_top_bit_set_is_refused(receiver: TestReceiver) -> None:
    """X25519 ignores the top bit, so the handshake completes, but one private key must not pose as two receivers."""
    spelled_twice = bytearray(receiver.public_key)
    spelled_twice[31] |= 0x80
    receiver.advertised_static = bytes(spelled_twice)
    ticket = PairingTicket(receiver.code, protocol.fingerprint(bytes(spelled_twice)))
    with pytest.raises(ErdigoError) as raised:
        pair(_target(receiver), ticket, DeviceIdentity.generate(), "Device", [ItemKind.PIN])
    assert raised.value.kind is ErrorKind.PROTOCOL_VIOLATION
    assert receiver.record.pair_requests == []


@pytest.mark.parametrize("name, kinds, label", [("", [ItemKind.PIN], None), ("‮", [ItemKind.PIN], None), ("Device", [], None), ("Device", [ItemKind.PIN], "bad\nlabel")])
def test_bad_arguments_are_refused_before_connecting(receiver: TestReceiver, name: str, kinds: list, label) -> None:
    with pytest.raises(ErdigoError) as raised:
        pair(_target(receiver), receiver.ticket, DeviceIdentity.generate(), name, kinds, label)
    assert raised.value.kind is ErrorKind.INVALID_ARGUMENT
    assert receiver.record.connections == 0


def test_low_order_points_are_refused() -> None:
    private = noise.new_private_key()
    for point in (bytes(32), (1).to_bytes(32, "little"), bytes.fromhex("e0eb7a7c3b41b8ae1656e3faf19fc46ada098deb9c32b1fd866205165f49b800")):
        with pytest.raises(noise.NoiseError) as raised:
            noise.dh(private, point)
        assert raised.value.failure is noise.NoiseFailure.WEAK_SHARED_SECRET


def test_canonical_public_keys() -> None:
    assert noise.is_canonical_public_key(bytes(32))
    assert noise.is_canonical_public_key((2**255 - 20).to_bytes(32, "little"))
    assert not noise.is_canonical_public_key((2**255 - 19).to_bytes(32, "little"))
    assert not noise.is_canonical_public_key((2**255 - 1).to_bytes(32, "little"))
    assert not noise.is_canonical_public_key(bytes(31) + b"\x80")
    assert not noise.is_canonical_public_key(bytes(31))
