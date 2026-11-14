"""Strict JSON, as spec section 6.1 requires, and a writer with a stable field order.

The parser refuses input that ordinary parsers tolerate: duplicate keys, invalid UTF-8, a byte order mark, unpaired
surrogates, non-finite numbers, anything but white space after the value, and more than six levels of nesting.
Integers and fractional numbers are kept apart, so that a field documented as an integer cannot accept ``1.5``.

It is hand-written, like the parsers of the Swift and .NET SDKs, so that every SDK reads the same bytes the same way.
Keys that are the same text in different Unicode spellings count as duplicates, because Swift compares strings that
way, and two SDKs must not disagree about whether a message has a duplicate key.
"""

from __future__ import annotations

import math
import unicodedata
from dataclasses import dataclass
from typing import Union

DEFAULT_MAX_DEPTH = 6

_INT64_MIN = -(2**63)
_INT64_MAX = 2**63 - 1


class JsonParseError(ValueError):
    """Why :func:`parse` refused its input. It never repeats the input."""

    def __init__(self, reason: str, offset: int = -1) -> None:
        super().__init__(f"strict JSON: {reason}")
        self.reason = reason
        self.offset = offset


@dataclass(frozen=True)
class JsonInteger:
    """An integer that fits in 64 bits, written without a fraction or exponent."""

    value: int


@dataclass(frozen=True)
class JsonNumber:
    """Any other finite number."""

    value: float


# A decoded value: None, bool, JsonInteger, JsonNumber, str, list or dict.
JsonValue = Union[None, bool, JsonInteger, JsonNumber, str, list, dict]


def is_valid_utf8(data: bytes) -> bool:
    """Rejects overlong forms, encoded surrogates and values above U+10FFFF, exactly as the other SDKs do."""
    index = 0
    length_of_data = len(data)
    while index < length_of_data:
        first = data[index]
        if first <= 0x7F:
            index += 1
            continue
        minimum_second = 0x80
        maximum_second = 0xBF
        if 0xC2 <= first <= 0xDF:
            length = 2
        elif first == 0xE0:
            length = 3
            minimum_second = 0xA0
        elif 0xE1 <= first <= 0xEC or first in (0xEE, 0xEF):
            length = 3
        elif first == 0xED:
            length = 3
            maximum_second = 0x9F
        elif first == 0xF0:
            length = 4
            minimum_second = 0x90
        elif 0xF1 <= first <= 0xF3:
            length = 4
        elif first == 0xF4:
            length = 4
            maximum_second = 0x8F
        else:
            return False
        if index + length > length_of_data:
            return False
        if not minimum_second <= data[index + 1] <= maximum_second:
            return False
        for offset in range(2, length):
            if data[index + offset] & 0xC0 != 0x80:
                return False
        index += length
    return True


def parse(data: bytes, max_depth: int = DEFAULT_MAX_DEPTH) -> JsonValue:
    """Parses one JSON value, refusing everything spec section 6.1 refuses."""
    if not isinstance(data, (bytes, bytearray, memoryview)):
        raise TypeError("strict JSON is parsed from bytes")
    data = bytes(data)
    if not is_valid_utf8(data):
        raise JsonParseError("invalid UTF-8")
    return _Parser(data, max_depth).document()


_WHITESPACE = b" \t\n\r"
_DIGITS = b"0123456789"


class _Parser:
    def __init__(self, data: bytes, max_depth: int) -> None:
        self.data = data
        self.max_depth = max_depth
        self.index = 0

    def document(self) -> JsonValue:
        self.skip_whitespace()
        value = self.value(1)
        self.skip_whitespace()
        if self.index != len(self.data):
            raise JsonParseError("trailing data", self.index)
        return value

    def skip_whitespace(self) -> None:
        data = self.data
        while self.index < len(data) and data[self.index] in _WHITESPACE:
            self.index += 1

    def peek(self) -> int:
        if self.index >= len(self.data):
            raise JsonParseError("unexpected end", self.index)
        return self.data[self.index]

    def expect(self, literal: bytes) -> None:
        if self.index + len(literal) > len(self.data):
            raise JsonParseError("unexpected end", self.index)
        if self.data[self.index : self.index + len(literal)] != literal:
            raise JsonParseError("unexpected byte", self.index)
        self.index += len(literal)

    def value(self, depth: int) -> JsonValue:
        current = self.peek()
        if current == 0x7B:  # {
            if depth > self.max_depth:
                raise JsonParseError("too deep", self.index)
            return self.object(depth)
        if current == 0x5B:  # [
            if depth > self.max_depth:
                raise JsonParseError("too deep", self.index)
            return self.array(depth)
        if current == 0x22:  # "
            return self.string()
        if current == 0x74:  # t
            self.expect(b"true")
            return True
        if current == 0x66:  # f
            self.expect(b"false")
            return False
        if current == 0x6E:  # n
            self.expect(b"null")
            return None
        if current == 0x2D or current in _DIGITS:
            return self.number()
        raise JsonParseError("unexpected byte", self.index)

    def object(self, depth: int) -> dict:
        self.index += 1
        result: dict = {}
        canonical_keys: set = set()
        self.skip_whitespace()
        if self.peek() == 0x7D:
            self.index += 1
            return result
        while True:
            self.skip_whitespace()
            if self.peek() != 0x22:
                raise JsonParseError("unexpected byte", self.index)
            key = self.string()
            # The same key in another Unicode spelling is a duplicate too, as it is for Swift.
            canonical = unicodedata.normalize("NFC", key)
            if key in result or canonical in canonical_keys:
                raise JsonParseError("duplicate key", self.index)
            canonical_keys.add(canonical)
            self.skip_whitespace()
            if self.peek() != 0x3A:
                raise JsonParseError("unexpected byte", self.index)
            self.index += 1
            self.skip_whitespace()
            result[key] = self.value(depth + 1)
            self.skip_whitespace()
            following = self.peek()
            self.index += 1
            if following == 0x7D:
                return result
            if following != 0x2C:
                raise JsonParseError("unexpected byte", self.index - 1)

    def array(self, depth: int) -> list:
        self.index += 1
        result: list = []
        self.skip_whitespace()
        if self.peek() == 0x5D:
            self.index += 1
            return result
        while True:
            self.skip_whitespace()
            result.append(self.value(depth + 1))
            self.skip_whitespace()
            following = self.peek()
            self.index += 1
            if following == 0x5D:
                return result
            if following != 0x2C:
                raise JsonParseError("unexpected byte", self.index - 1)

    def hex4(self) -> int:
        if self.index + 4 > len(self.data):
            raise JsonParseError("unexpected end", self.index)
        value = 0
        for _ in range(4):
            digit = self.data[self.index]
            self.index += 1
            if 0x30 <= digit <= 0x39:
                number = digit - 0x30
            elif 0x61 <= digit <= 0x66:
                number = digit - 0x61 + 10
            elif 0x41 <= digit <= 0x46:
                number = digit - 0x41 + 10
            else:
                raise JsonParseError("invalid escape", self.index - 1)
            value = (value << 4) | number
        return value

    def string(self) -> str:
        self.index += 1
        output = bytearray()
        data = self.data
        while True:
            current = self.peek()
            if current == 0x22:
                self.index += 1
                # Every byte here was checked as UTF-8, and every escape is a scalar value, so this loses nothing.
                return output.decode("utf-8")
            if current == 0x5C:
                self.index += 1
                escape = self.peek()
                self.index += 1
                simple = {0x22: 0x22, 0x5C: 0x5C, 0x2F: 0x2F, 0x62: 0x08, 0x66: 0x0C, 0x6E: 0x0A, 0x72: 0x0D, 0x74: 0x09}
                if escape in simple:
                    output.append(simple[escape])
                    continue
                if escape != 0x75:
                    raise JsonParseError("invalid escape", self.index - 1)
                scalar = self.hex4()
                if 0xDC00 <= scalar <= 0xDFFF:
                    raise JsonParseError("invalid escape", self.index)
                if 0xD800 <= scalar <= 0xDBFF:
                    if self.index + 2 > len(data) or data[self.index] != 0x5C or data[self.index + 1] != 0x75:
                        raise JsonParseError("invalid escape", self.index)
                    self.index += 2
                    low = self.hex4()
                    if not 0xDC00 <= low <= 0xDFFF:
                        raise JsonParseError("invalid escape", self.index)
                    scalar = 0x10000 + ((scalar - 0xD800) << 10) + (low - 0xDC00)
                output.extend(chr(scalar).encode("utf-8"))
                continue
            if current <= 0x1F:
                raise JsonParseError("unexpected byte", self.index)
            output.append(current)
            self.index += 1

    def number(self) -> JsonValue:
        data = self.data
        start = self.index
        is_integer = True
        if data[self.index] == 0x2D:
            self.index += 1
        first = self.peek()
        if first not in _DIGITS:
            raise JsonParseError("unexpected byte", self.index)
        self.index += 1
        if first == 0x30 and self.index < len(data) and data[self.index] in _DIGITS:
            raise JsonParseError("unexpected byte", self.index)
        self.consume_digits()
        if self.index < len(data) and data[self.index] == 0x2E:
            is_integer = False
            self.index += 1
            self.require_digit()
            self.consume_digits()
        if self.index < len(data) and data[self.index] in b"eE":
            is_integer = False
            self.index += 1
            if self.index < len(data) and data[self.index] in b"+-":
                self.index += 1
            self.require_digit()
            self.consume_digits()
        text = data[start : self.index].decode("ascii")
        # An integer too long for 64 bits is still a number. A field that needs an integer refuses it later.
        if is_integer:
            integer = int(text)
            if _INT64_MIN <= integer <= _INT64_MAX:
                return JsonInteger(integer)
        value = float(text)
        if not math.isfinite(value):
            raise JsonParseError("number out of range", start)
        return JsonNumber(value)

    def consume_digits(self) -> None:
        data = self.data
        while self.index < len(data) and data[self.index] in _DIGITS:
            self.index += 1

    def require_digit(self) -> None:
        if self.peek() not in _DIGITS:
            raise JsonParseError("unexpected byte", self.index)


# Writing


class Number:
    """A fractional number to write, such as a coordinate. Plain ``int`` values are written as integers."""

    __slots__ = ("value",)

    def __init__(self, value: float) -> None:
        self.value = float(value)


def encode(value: object) -> bytes:
    """Writes a value with its fields in the order given, so that a resent message is byte-for-byte identical.

    Objects are dicts, which keep their insertion order, and arrays are lists or tuples. Integers are ``int``,
    fractional numbers are :class:`Number` or ``float``, and text is ``str``.
    """
    parts: list = []
    _write(value, parts)
    try:
        return "".join(parts).encode("utf-8")
    except UnicodeEncodeError:
        # A lone surrogate cannot be carried by UTF-8. Refuse it rather than change the text.
        raise ValueError("text holds a lone surrogate") from None


def _write(value: object, parts: list) -> None:
    if value is True:
        parts.append("true")
    elif value is False:
        parts.append("false")
    elif value is None:
        parts.append("null")
    elif isinstance(value, int):
        parts.append(str(value))
    elif isinstance(value, Number):
        parts.append(format_number(value.value))
    elif isinstance(value, float):
        parts.append(format_number(value))
    elif isinstance(value, str):
        _write_string(value, parts)
    elif isinstance(value, dict):
        parts.append("{")
        for position, (key, item) in enumerate(value.items()):
            if position:
                parts.append(",")
            _write_string(key, parts)
            parts.append(":")
            _write(item, parts)
        parts.append("}")
    elif isinstance(value, (list, tuple)):
        parts.append("[")
        for position, item in enumerate(value):
            if position:
                parts.append(",")
            _write(item, parts)
        parts.append("]")
    else:
        raise TypeError(f"cannot write {type(value).__name__} as JSON")


def _write_string(text: str, parts: list) -> None:
    parts.append('"')
    for character in text:
        if character == '"':
            parts.append('\\"')
        elif character == "\\":
            parts.append("\\\\")
        elif character == "\n":
            parts.append("\\n")
        elif character == "\t":
            parts.append("\\t")
        elif character == "\r":
            parts.append("\\r")
        elif ord(character) < 0x20:
            parts.append(f"\\u{ord(character):04x}")
        else:
            parts.append(character)
    parts.append('"')


def format_number(number: float) -> str:
    """The shortest text that reads back as the same number, spelled the way the Swift SDK spells it.

    So every SDK writes the same bytes for the same request: ``37.0``, ``0.0001``, ``1e-05``, ``1e+16``.
    """
    if not math.isfinite(number):
        raise ValueError("non-finite numbers cannot be written as JSON")
    if number == 0:
        return "-0.0" if math.copysign(1.0, number) < 0 else "0.0"
    # Python's repr gives the shortest round-trip digits too. Only the layout differs from Swift's.
    shortest = repr(float(number))
    negative = shortest.startswith("-")
    if negative:
        shortest = shortest[1:]
    exponent = 0
    lowered = shortest.lower()
    if "e" in lowered:
        mantissa, _, power = lowered.partition("e")
        exponent = int(power)
        shortest = mantissa
    whole, _, fraction = shortest.partition(".")
    all_digits = whole + fraction
    leading_zeros = len(all_digits) - len(all_digits.lstrip("0"))
    digits = all_digits.lstrip("0").rstrip("0")
    # The power of ten of the first significant digit.
    scientific = len(whole) - 1 - leading_zeros + exponent

    output = "-" if negative else ""
    if scientific < -4 or abs(number) > 9007199254740992.0:
        output += digits[0]
        if len(digits) > 1:
            output += "." + digits[1:]
        output += "e" + ("-" if scientific < 0 else "+") + f"{abs(scientific):02d}"
    elif scientific >= 0:
        integer_digits = scientific + 1
        if len(digits) <= integer_digits:
            output += digits + "0" * (integer_digits - len(digits)) + ".0"
        else:
            output += digits[:integer_digits] + "." + digits[integer_digits:]
    else:
        output += "0." + "0" * (-scientific - 1) + digits
    return output
