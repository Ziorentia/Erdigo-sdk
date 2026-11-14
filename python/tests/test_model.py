"""Values, text rules, pairing codes and strict JSON."""

from __future__ import annotations

import pytest

from erdigo_sdk import Coordinate, PairingCode, PairingTicket, TrackPoint, WireError
from erdigo_sdk import strict_json
from erdigo_sdk.model import sanitize_device_name, validate_note, validate_title
from erdigo_sdk.strict_json import JsonInteger, JsonNumber, JsonParseError

FINGERPRINT = "61a262dfb9af8d6247ef8ecf537e73b6d182e8eb2ce070930e706186bd1bed22"


@pytest.mark.parametrize("typed", ["TEST0-0NLYC-0DE00-00000", "test0 0nlyc 0de00 00000", "TEST00N1YC0DE0000000", "TESTO-ONLYC-ODEOO-OOOOO", " TEST0 0NLYC\t0DE00-00000\n"])
def test_a_code_reads_the_way_a_person_types_it(typed: str) -> None:
    assert PairingCode(typed).canonical == "TEST00N1YC0DE0000000"


@pytest.mark.parametrize("typed", ["", "TEST0-0NLYC-0DE00-0000", "TEST0-0NLYC-0DE00-000000", "TEST0-0NLYC-0DE00-0000U", "TEST0-0NLYC-0DE00-0000٠", "TEST0\x1c0NLYC0DE0000000"])
def test_anything_else_is_not_a_code(typed: str) -> None:
    with pytest.raises(WireError):
        PairingCode(typed)


def test_a_code_never_shows_itself() -> None:
    code = PairingCode.generate()
    assert code.canonical not in repr(code) and code.canonical not in str(code)
    assert len(code.display) == 23 and code.display.count("-") == 3


def test_qr_text_round_trips_and_needs_its_fingerprint() -> None:
    ticket = PairingTicket.parse(f"erdigo-pair:1?code=TEST0-0N1YC-0DE00-00000&fp={FINGERPRINT.upper()}")
    assert ticket.fingerprint == FINGERPRINT
    assert ticket.uri == f"erdigo-pair:1?code=TEST0-0N1YC-0DE00-00000&fp={FINGERPRINT}"
    assert PairingTicket.parse("TEST0-0N1YC-0DE00-00000").fingerprint is None
    assert PairingTicket.parse("TEST0-0N1YC-0DE00-00000").uri is None


@pytest.mark.parametrize(
    "text",
    [
        "erdigo-pair:1?code=TEST0-0N1YC-0DE00-00000",
        f"erdigo-pair:1?fp={FINGERPRINT}",
        f"erdigo-pair:1?code=TEST0-0N1YC-0DE00-00000&fp={FINGERPRINT}&fp={FINGERPRINT}",
        f"erdigo-pair:1?code=TEST0-0N1YC-0DE00-00000&code=TEST0-0N1YC-0DE00-00000&fp={FINGERPRINT}",
        f"erdigo-pair:1?code=TEST0-0N1YC-0DE00-00000&fp={FINGERPRINT}&host=10.0.0.1",
        f"erdigo-pair:1?code=TEST0-0N1YC-0DE00-00000&fp={FINGERPRINT}&",
        f"erdigo-pair:1?code=TEST0-0N1YC-0DE00-00000&&fp={FINGERPRINT}",
        "erdigo-pair:1?code=TEST0-0N1YC-0DE00-00000&fp=",
        f"erdigo-pair:1?code=TEST0-0N1YC-0DE00-00000&fp={FINGERPRINT[:-1]}",
        "x" * 1025,
    ],
)
def test_qr_text_that_is_not_exactly_right_is_refused(text: str) -> None:
    with pytest.raises(WireError):
        PairingTicket.parse(text)


@pytest.mark.parametrize("latitude, longitude", [(90.01, 0), (0, -180.5), (float("nan"), 0), (0, float("inf"))])
def test_coordinates_out_of_range_are_refused(latitude: float, longitude: float) -> None:
    with pytest.raises(WireError):
        Coordinate(latitude, longitude)


def test_track_point_limits() -> None:
    TrackPoint(Coordinate(0, 0), 0, -1000)
    TrackPoint(Coordinate(0, 0), 2**53 - 1, 100_000)
    for timestamp, altitude in [(-1, None), (2**53, None), (None, -1000.5), (None, float("nan"))]:
        with pytest.raises(WireError):
            TrackPoint(Coordinate(0, 0), timestamp, altitude)


@pytest.mark.parametrize("character", ["\x00", "\x1f", "\x7f", "\x85", "؜", "‎", "‮", "⁦", " ", "﻿"])
def test_control_and_direction_characters_are_refused(character: str) -> None:
    with pytest.raises(WireError):
        validate_title(f"a{character}b")
    with pytest.raises(WireError):
        validate_note(f"a{character}b")


def test_line_breaks_belong_in_notes_only() -> None:
    validate_note("line\nline\tend")
    with pytest.raises(WireError):
        validate_title("line\nline")
    validate_title("é" * 200)
    with pytest.raises(WireError):
        validate_title("é" * 201)
    validate_note("é" * 2048)
    with pytest.raises(WireError):
        validate_note("é" * 2049)
    with pytest.raises(WireError):
        validate_title("\ud800")


def test_device_names_are_cleaned_like_a_receiver_cleans_them() -> None:
    assert sanitize_device_name("  Drone‮ A\x00  ") == "Drone A"
    assert sanitize_device_name("x" * 50) == "x" * 40


@pytest.mark.parametrize(
    "number, text",
    [(37.0, "37.0"), (0.0001, "0.0001"), (0.00001, "1e-05"), (1e16, "1e+16"), (-0.0, "-0.0"), (126.977, "126.977"), (100000.0, "100000.0"), (1.5e-7, "1.5e-07")],
)
def test_numbers_are_written_as_the_swift_sdk_writes_them(number: float, text: str) -> None:
    assert strict_json.format_number(number) == text


def test_text_is_escaped_as_the_other_sdks_escape_it() -> None:
    assert strict_json.encode({"a": 'q"\\\n\t\r\x01\x7fé€'}) == '{"a":"q\\"\\\\\\n\\t\\r\\u0001\x7fé€"}'.encode()
    with pytest.raises(ValueError):
        strict_json.encode({"a": "\ud800"})


def test_strict_json_keeps_integers_apart_and_refuses_what_others_tolerate() -> None:
    assert strict_json.parse(b' {"a":[1,1.0,-0,1e2,"x",true,null]} ') == {"a": [JsonInteger(1), JsonNumber(1.0), JsonInteger(0), JsonNumber(100.0), "x", True, None]}
    assert strict_json.parse(b"9223372036854775808") == JsonNumber(9.223372036854776e18)
    for data in [b'{"a":1,"a":2}', '{"é":1,"é":2}'.encode(), b"[01]", b"[1.]", b"[.5]", b"[1e]", b"[NaN]", b"[1e999]", b'"\\x"', b'"\x01"',
                 b"[[[[[[[1]]]]]]]", b"{} {}", b"\xef\xbb\xbf{}", b'"\\udc00"', b'"\\ud800x"', b"[1,]", b"{,}", b""]:
        with pytest.raises(JsonParseError):
            strict_json.parse(data)
    assert strict_json.parse(b"[[[[[[1]]]]]]") is not None
