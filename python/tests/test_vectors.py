"""The official Noise vectors and every integration-v1 vector in spec/vectors, byte for byte."""

from __future__ import annotations

import json

import pytest

from erdigo_sdk import messages, noise, protocol
from erdigo_sdk.model import PairingCode, PairingTicket
from erdigo_sdk.protocol import WireError

from .support import VECTORS


def _load(path: str) -> dict:
    return json.loads((VECTORS / path).read_text(encoding="utf-8"))


_PATTERNS = {
    "Noise_IK_25519_ChaChaPoly_SHA256": noise.IK,
    "Noise_NNpsk0_25519_ChaChaPoly_SHA256": noise.Pattern("NNpsk0", False, [[noise.Token.PSK, noise.Token.E], [noise.Token.E, noise.Token.EE]]),
    "Noise_XXpsk3_25519_ChaChaPoly_SHA256": noise.Pattern(
        "XXpsk3",
        False,
        [[noise.Token.E], [noise.Token.E, noise.Token.EE, noise.Token.S, noise.Token.ES], [noise.Token.S, noise.Token.SE, noise.Token.PSK]],
    ),
}


def _hex(vector: dict, key: str):
    value = vector.get(key)
    return bytes.fromhex(value) if value is not None else None


@pytest.mark.parametrize("vector", _load("noise/cacophony-subset.json")["vectors"], ids=lambda vector: vector["protocol_name"])
def test_official_noise_vectors(vector: dict) -> None:
    pattern = _PATTERNS[vector["protocol_name"]]
    assert pattern.protocol_name == vector["protocol_name"]
    initiator = noise.Handshake(
        pattern,
        True,
        _hex(vector, "init_prologue"),
        _hex(vector, "init_static"),
        _hex(vector, "init_remote_static"),
        [bytes.fromhex(key) for key in vector.get("init_psks", [])],
        _fixed_ephemeral=_hex(vector, "init_ephemeral"),
    )
    responder = noise.Handshake(
        pattern,
        False,
        _hex(vector, "resp_prologue"),
        _hex(vector, "resp_static"),
        _hex(vector, "resp_remote_static"),
        [bytes.fromhex(key) for key in vector.get("resp_psks", [])],
        _fixed_ephemeral=_hex(vector, "resp_ephemeral"),
    )
    steps = vector["messages"]
    handshake_length = len(pattern.messages)
    for index, step in enumerate(steps[:handshake_length]):
        writer, reader = (initiator, responder) if index % 2 == 0 else (responder, initiator)
        message = writer.write_message(bytes.fromhex(step["payload"]))
        assert message.hex() == step["ciphertext"]
        assert reader.read_message(message).hex() == step["payload"]
    assert initiator.handshake_hash.hex() == vector["handshake_hash"]
    assert responder.handshake_hash.hex() == vector["handshake_hash"]
    initiator_transport = initiator.split()
    responder_transport = responder.split()
    for index, step in enumerate(steps[handshake_length:], start=handshake_length):
        # After the handshake, the initiator speaks on even steps for a pattern of odd length, and the other way round.
        initiator_speaks = (index % 2 == 0)
        sender, receiver = (initiator_transport, responder_transport) if initiator_speaks else (responder_transport, initiator_transport)
        sealed = sender.seal(bytes.fromhex(step["payload"]))
        assert sealed.hex() == step["ciphertext"]
        assert receiver.open(sealed).hex() == step["payload"]


def _frames(vector: dict) -> list:
    return vector["frames"]


def test_connect_handshake_vector_byte_for_byte() -> None:
    vector = _load("integration-v1/handshake-connect.json")
    frames = {frame["stage"]: frame for frame in _frames(vector)}
    prologue = protocol.preamble(protocol.Mode.CONNECT)
    assert prologue.hex() == vector["preamble_and_prologue"] == frames["preamble"]["wire"]
    assert noise.IK.protocol_name == vector["protocol_name"]
    device_private = bytes.fromhex(vector["device_static_private"])
    receiver_private = bytes.fromhex(vector["receiver_static_private"])
    assert noise.public_key(device_private).hex() == vector["device_static_public"]
    assert noise.public_key(receiver_private).hex() == vector["receiver_static_public"]
    assert protocol.fingerprint(bytes.fromhex(vector["device_static_public"])) == vector["device_fingerprint"]
    assert protocol.fingerprint(bytes.fromhex(vector["receiver_static_public"])) == vector["receiver_fingerprint"]

    device = noise.Handshake(
        noise.IK, True, prologue, device_private, bytes.fromhex(vector["receiver_static_public"]),
        _fixed_ephemeral=bytes.fromhex(vector["device_ephemeral_private"]),
    )
    receiver = noise.Handshake(
        noise.IK, False, prologue, receiver_private, _fixed_ephemeral=bytes.fromhex(vector["receiver_ephemeral_private"])
    )
    counter = vector["connect_counter"].to_bytes(8, "big")
    assert counter.hex() == frames["handshake_1"]["payload"]
    first = device.write_message(counter)
    assert len(first) == protocol.CONNECT_FIRST_MESSAGE_LENGTH
    assert first.hex() == frames["handshake_1"]["noise_message"]
    assert (len(first).to_bytes(2, "big") + first).hex() == frames["handshake_1"]["wire"]
    assert receiver.read_message(first) == counter
    assert receiver.remote_static_key.hex() == vector["device_static_public"]
    second = receiver.write_message()
    assert second.hex() == frames["handshake_2"]["noise_message"]
    assert device.read_message(second) == b""
    assert device.handshake_hash.hex() == vector["handshake_hash"]
    device_transport = device.split()
    receiver_transport = receiver.split()

    hello = messages.encode_hello(messages.Hello((1,), "erdigo-test/1.0"))
    assert hello.decode() == frames["hello"]["payload_text"]
    sealed = device_transport.seal(hello)
    assert sealed.hex() == frames["hello"]["noise_message"]
    assert messages.decode_hello(receiver_transport.open(sealed)) == messages.Hello((1,), "erdigo-test/1.0")

    welcome_bytes = bytes.fromhex(frames["welcome"]["payload"])
    welcome = messages.decode_receiver_message(welcome_bytes)
    assert isinstance(welcome, messages.Welcome) and welcome.last_seq == 41
    assert messages.encode_receiver_message(welcome) == welcome_bytes
    sealed = receiver_transport.seal(welcome_bytes)
    assert sealed.hex() == frames["welcome"]["noise_message"]
    assert device_transport.open(sealed) == welcome_bytes

    request = messages.decode_request(bytes.fromhex(frames["request"]["payload"]))
    encoded = messages.encode_request(request)
    assert encoded.hex() == frames["request"]["payload"]
    sealed = device_transport.seal(encoded)
    assert sealed.hex() == frames["request"]["noise_message"]
    assert receiver_transport.open(sealed) == encoded

    ack = messages.Ack(42, False)
    ack_bytes = messages.encode_receiver_message(ack)
    assert ack_bytes.hex() == frames["ack"]["payload"]
    sealed = receiver_transport.seal(ack_bytes)
    assert sealed.hex() == frames["ack"]["noise_message"]
    assert messages.decode_receiver_message(device_transport.open(sealed)) == ack


def test_pair_handshake_vector_byte_for_byte() -> None:
    vector = _load("integration-v1/handshake-pair.json")
    frames = {frame["stage"]: frame for frame in _frames(vector)}
    prologue = protocol.preamble(protocol.Mode.PAIR)
    assert prologue.hex() == vector["preamble_and_prologue"] == frames["preamble"]["wire"]
    assert noise.XXPSK0.protocol_name == vector["protocol_name"]

    code = PairingCode(vector["pairing_code_as_typed"])
    assert code.canonical == vector["pairing_code_canonical"]
    psk = code.preshared_key()
    assert psk.hex() == vector["psk"]
    receiver_public = bytes.fromhex(vector["receiver_static_public"])
    ticket = PairingTicket(code, protocol.fingerprint(receiver_public))
    assert ticket.uri == vector["qr_text"]
    assert PairingTicket.parse(vector["qr_text"]) == ticket

    device = noise.Handshake(
        noise.XXPSK0, True, prologue, bytes.fromhex(vector["device_static_private"]), psks=[psk],
        _fixed_ephemeral=bytes.fromhex(vector["device_ephemeral_private"]),
    )
    receiver = noise.Handshake(
        noise.XXPSK0, False, prologue, bytes.fromhex(vector["receiver_static_private"]), psks=[psk],
        _fixed_ephemeral=bytes.fromhex(vector["receiver_ephemeral_private"]),
    )
    first = device.write_message()
    assert len(first) == protocol.PAIR_FIRST_MESSAGE_LENGTH
    assert first.hex() == frames["handshake_1"]["noise_message"]
    assert (len(first).to_bytes(2, "big") + first).hex() == frames["handshake_1"]["wire"]
    receiver.read_message(first)
    second = receiver.write_message()
    assert second.hex() == frames["handshake_2"]["noise_message"]
    device.read_message(second)
    assert device.remote_static_key == receiver_public
    request = messages.PairRequest((1,), "erdigo-test/1.0", "Test device", (messages.ItemKind.PIN, messages.ItemKind.TRACK))
    payload = messages.encode_pair_request(request)
    assert payload.decode() == frames["handshake_3"]["payload_text"]
    third = device.write_message(payload)
    assert third.hex() == frames["handshake_3"]["noise_message"]
    assert messages.decode_pair_request(receiver.read_message(third)) == request
    assert receiver.remote_static_key.hex() == vector["device_static_public"]
    assert device.handshake_hash.hex() == vector["handshake_hash"]
    device_transport = device.split()
    receiver_transport = receiver.split()
    for stage, expected in (
        ("pair_status_pending", messages.PairPending()),
        ("pair_status_approved", messages.PairApproved((messages.ItemKind.PIN,), 0)),
    ):
        payload = messages.encode_receiver_message(expected)
        assert payload.hex() == frames[stage]["payload"]
        sealed = receiver_transport.seal(payload)
        assert sealed.hex() == frames[stage]["noise_message"]
        assert messages.decode_receiver_message(device_transport.open(sealed)) == expected


_MESSAGES = _load("integration-v1/messages.json")


@pytest.mark.parametrize("case", _MESSAGES["valid"], ids=lambda case: case["name"])
def test_valid_requests_are_accepted_and_reencode_stably(case: dict) -> None:
    envelope = messages.decode_request(case["json"].encode("utf-8"))
    encoded = messages.encode_request(envelope)
    # Whatever order the fields came in, writing it again gives one spelling that reads back the same.
    assert messages.decode_request(encoded) == envelope
    assert messages.encode_request(messages.decode_request(encoded)) == encoded


@pytest.mark.parametrize("case", _MESSAGES["invalid"], ids=lambda case: case["name"])
def test_invalid_requests_are_rejected_with_their_code(case: dict) -> None:
    with pytest.raises(WireError) as raised:
        messages.decode_request(case["json"].encode("utf-8"))
    assert raised.value.code.value == case["code"]


@pytest.mark.parametrize("case", _MESSAGES["invalid_bytes"], ids=lambda case: case["name"])
def test_invalid_bytes_are_rejected_with_their_code(case: dict) -> None:
    with pytest.raises(WireError) as raised:
        messages.decode_request(bytes.fromhex(case["hex"]))
    assert raised.value.code.value == case["code"]
