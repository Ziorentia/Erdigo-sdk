#!/usr/bin/env python3
"""Checks the integration-v1 vectors with code that shares nothing with the Swift SDK.

The handshakes are replayed with the independent `noiseprotocol` library, and the
messages are checked against the JSON Schema.
"""

import json
import pathlib
import sys

from jsonschema import Draft202012Validator
from noise.connection import Keypair, NoiseConnection

SPEC = pathlib.Path(__file__).resolve().parents[2]
VECTORS = SPEC / "vectors" / "integration-v1"

failures = []


def expect(condition, message):
    if not condition:
        failures.append(message)


def connection(protocol, initiator, prologue, static, ephemeral, remote=None, psk=None):
    noise = NoiseConnection.from_name(protocol.encode())
    if initiator:
        noise.set_as_initiator()
    else:
        noise.set_as_responder()
    noise.set_prologue(prologue)
    noise.set_keypair_from_private_bytes(Keypair.STATIC, static)
    noise.set_keypair_from_private_bytes(Keypair.EPHEMERAL, ephemeral)
    if remote is not None:
        noise.set_keypair_from_public_bytes(Keypair.REMOTE_STATIC, remote)
    if psk is not None:
        noise.set_psks(psk)
    noise.start_handshake()
    return noise


def replay(name, psk_field=None, device_knows_receiver=False):
    vector = json.loads((VECTORS / name).read_text())
    prologue = bytes.fromhex(vector["preamble_and_prologue"])
    psk = bytes.fromhex(vector[psk_field]) if psk_field else None
    receiver_public = bytes.fromhex(vector["receiver_static_public"])

    device = connection(
        vector["protocol_name"], True, prologue,
        bytes.fromhex(vector["device_static_private"]),
        bytes.fromhex(vector["device_ephemeral_private"]),
        remote=receiver_public if device_knows_receiver else None,
        psk=psk,
    )
    receiver = connection(
        vector["protocol_name"], False, prologue,
        bytes.fromhex(vector["receiver_static_private"]),
        bytes.fromhex(vector["receiver_ephemeral_private"]),
        psk=psk,
    )

    for frame in vector["frames"]:
        stage = frame["stage"]
        if stage == "preamble":
            expect(frame["wire"] == vector["preamble_and_prologue"], f"{name}: preamble")
            continue
        payload = bytes.fromhex(frame["payload"])
        noise = bytes.fromhex(frame["noise_message"])
        wire = bytes.fromhex(frame["wire"])
        expect(wire == len(noise).to_bytes(2, "big") + noise, f"{name} {stage}: framing")

        from_device = frame["direction"] == "device_to_receiver"
        sender, reader = (device, receiver) if from_device else (receiver, device)
        if not device.handshake_finished or not receiver.handshake_finished:
            produced = sender.write_message(payload)
            expect(bytes(produced) == noise, f"{name} {stage}: handshake bytes differ")
            expect(bytes(reader.read_message(noise)) == payload, f"{name} {stage}: payload differs")
        else:
            produced = sender.encrypt(payload)
            expect(bytes(produced) == noise, f"{name} {stage}: transport bytes differ")
            expect(bytes(reader.decrypt(noise)) == payload, f"{name} {stage}: payload differs")

    expect(device.handshake_finished and receiver.handshake_finished, f"{name}: handshake did not finish")
    expected_hash = bytes.fromhex(vector["handshake_hash"])
    expect(bytes(device.get_handshake_hash()) == expected_hash, f"{name}: handshake hash differs")
    print(f"handshake ok: {name} ({vector['protocol_name']})")
    return vector


def reject_constant(value):
    raise ValueError(f"not allowed in JSON: {value}")


def strict_pairs(pairs):
    keys = [key for key, _ in pairs]
    if len(keys) != len(set(keys)):
        raise ValueError("duplicate key")
    return dict(pairs)


def strict_parse(text):
    value = json.loads(text, object_pairs_hook=strict_pairs, parse_constant=reject_constant)

    def check(node):
        if isinstance(node, float) and (node != node or node in (float("inf"), float("-inf"))):
            raise ValueError("number out of range")
        if isinstance(node, list):
            for child in node:
                check(child)
        if isinstance(node, dict):
            for child in node.values():
                check(child)

    check(value)
    return value


def check_messages(validator):
    file = json.loads((VECTORS / "messages.json").read_text())
    for entry in file["valid"]:
        errors = list(validator.iter_errors(strict_parse(entry["json"])))
        expect(not errors, f"valid message refused by the schema: {entry['name']}")
    for entry in file["invalid"]:
        try:
            value = strict_parse(entry["json"])
        except ValueError:
            continue
        expect(
            any(True for _ in validator.iter_errors(value)) or entry["name"] in SCHEMA_CANNOT_EXPRESS,
            f"invalid message accepted by the schema: {entry['name']}",
        )
    print(f"messages ok: {len(file['valid'])} valid, {len(file['invalid'])} invalid")


# A JSON Schema cannot count UTF-8 bytes, or relate one field to another.
SCHEMA_CANNOT_EXPRESS = {"note too long", "chunk count that does not match the size"}


def check_transcript_messages(validator, vector):
    for frame in vector["frames"]:
        text = frame.get("payload_text")
        if text:
            errors = [error.message for error in validator.iter_errors(strict_parse(text))]
            expect(not errors, f"transcript message refused by the schema: {frame['stage']}")


def main():
    schema = json.loads((SPEC / "schema" / "integration-v1.schema.json").read_text())
    Draft202012Validator.check_schema(schema)
    validator = Draft202012Validator(schema)

    connect = replay("handshake-connect.json", device_knows_receiver=True)
    pair = replay("handshake-pair.json", psk_field="psk")
    check_messages(validator)
    check_transcript_messages(validator, connect)
    check_transcript_messages(validator, pair)

    if failures:
        print("\nFAILED:")
        for failure in failures:
            print(" -", failure)
        return 1
    print("\nAll vectors agree with the independent implementations.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
