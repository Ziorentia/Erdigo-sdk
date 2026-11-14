"""Validated values of the protocol: coordinates, ids, kinds, text, pairing codes and limits."""

from __future__ import annotations

import enum
import hashlib
import hmac
import math
import secrets
import unicodedata
from dataclasses import dataclass
from typing import Iterable, List, Optional

from . import protocol
from .protocol import ErrorCode, WireError


@dataclass(frozen=True)
class Coordinate:
    """A validated coordinate: finite, latitude within -90...90 and longitude within -180...180 (WGS 84)."""

    latitude: float
    longitude: float

    def __post_init__(self) -> None:
        latitude = self.latitude
        longitude = self.longitude
        if isinstance(latitude, bool) or isinstance(longitude, bool):
            raise TypeError("a coordinate is made of numbers")
        latitude = float(latitude)
        longitude = float(longitude)
        if (
            not math.isfinite(latitude)
            or not math.isfinite(longitude)
            or not -90.0 <= latitude <= 90.0
            or not -180.0 <= longitude <= 180.0
        ):
            raise WireError(ErrorCode.INVALID_COORDINATE, "latitude must be within -90...90 and longitude within -180...180")
        object.__setattr__(self, "latitude", latitude)
        object.__setattr__(self, "longitude", longitude)


_ID_CHARACTERS = frozenset("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_")


def validate_item_id(value: str) -> str:
    """An item id: 1 to 64 ASCII letters, digits, '-' or '_'."""
    if not isinstance(value, str) or not 1 <= len(value) <= 64 or any(character not in _ID_CHARACTERS for character in value):
        raise WireError(ErrorCode.INVALID_ID, "an item id is 1 to 64 ASCII letters, digits, '-' or '_'")
    return value


class ItemKind(str, enum.Enum):
    """The kinds of item a device can write."""

    PIN = "pin"
    MARKER = "marker"
    NOTE = "note"
    PHOTO = "photo"
    TRACK = "track"

    @classmethod
    def from_wire(cls, text: str) -> Optional["ItemKind"]:
        try:
            return cls(text)
        except ValueError:
            return None

    @property
    def uses_put(self) -> bool:
        """``pin``, ``marker`` and ``note`` are carried by ``put``. ``photo`` and ``track`` have their own messages."""
        return self in (ItemKind.PIN, ItemKind.MARKER, ItemKind.NOTE)

    def __str__(self) -> str:
        return self.value


ALL_KINDS = (ItemKind.PIN, ItemKind.MARKER, ItemKind.NOTE, ItemKind.PHOTO, ItemKind.TRACK)


def canonical_kinds(kinds: Iterable[ItemKind]) -> List[ItemKind]:
    """The kinds given, without repeats, in the protocol's order."""
    wanted = {ItemKind(kind) for kind in kinds}
    return [kind for kind in ALL_KINDS if kind in wanted]


class MarkerColor(str, enum.Enum):
    """The colours a marker can have."""

    RED = "red"
    ORANGE = "orange"
    YELLOW = "yellow"
    GREEN = "green"
    BLUE = "blue"
    PURPLE = "purple"
    GRAY = "gray"

    @classmethod
    def from_wire(cls, text: str) -> Optional["MarkerColor"]:
        try:
            return cls(text)
        except ValueError:
            return None

    def __str__(self) -> str:
        return self.value


@dataclass(frozen=True)
class TrackPoint:
    """One point of a track. ``timestamp_ms`` is milliseconds since 1970, ``altitude_m`` is metres."""

    coordinate: Coordinate
    timestamp_ms: Optional[int] = None
    altitude_m: Optional[float] = None

    def __post_init__(self) -> None:
        if not isinstance(self.coordinate, Coordinate):
            raise TypeError("a track point needs a Coordinate")
        if self.timestamp_ms is not None:
            if isinstance(self.timestamp_ms, bool) or not isinstance(self.timestamp_ms, int) or not 0 <= self.timestamp_ms <= protocol.MAX_SEQUENCE:
                raise WireError(ErrorCode.INVALID_MESSAGE, "a track point timestamp is milliseconds since 1970, within 0...2^53-1")
        if self.altitude_m is not None:
            altitude = float(self.altitude_m)
            if not math.isfinite(altitude) or not -1000.0 <= altitude <= 100_000.0:
                raise WireError(ErrorCode.INVALID_MESSAGE, "a track point altitude is within -1000...100000 metres")
            object.__setattr__(self, "altitude_m", altitude)


class PhotoType(str, enum.Enum):
    """The only photo formats the protocol allows."""

    JPEG = "image/jpeg"
    PNG = "image/png"

    @classmethod
    def from_wire(cls, text: str) -> Optional["PhotoType"]:
        try:
            return cls(text)
        except ValueError:
            return None

    @classmethod
    def detect(cls, data: bytes) -> Optional["PhotoType"]:
        """Detects the format from the first bytes. Anything but JPEG or PNG returns None."""
        if data[:3] == b"\xff\xd8\xff":
            return cls.JPEG
        if data[:8] == b"\x89PNG\r\n\x1a\n":
            return cls.PNG
        return None


@dataclass(frozen=True)
class Limits:
    """Limits a receiver enforces per device. The receiver announces them in ``welcome``."""

    items: int = 500
    photos: int = 50
    photo_bytes: int = 1_048_576
    note_bytes: int = 4096
    title_chars: int = 200
    track_points: int = 3000
    open_tracks: int = 5
    tracks: int = 200
    msgs_per_s: int = 5
    msg_burst: int = 20
    points_per_s: int = 1
    point_burst: int = 100
    disk_bytes: int = 31_457_280


#: The values of spec section 11.
LIMITS_V1 = Limits()


def is_disallowed(scalar: int, allow_line_breaks: bool) -> bool:
    """Control characters and invisible direction overrides, which can make text read differently from what it is."""
    if scalar in (0x0A, 0x09):
        return not allow_line_breaks
    if scalar <= 0x1F or 0x7F <= scalar <= 0x9F:
        return True
    return (
        scalar in (0x061C, 0x200E, 0x200F, 0x2028, 0x2029, 0xFEFF)
        or 0x202A <= scalar <= 0x202E
        or 0x2066 <= scalar <= 0x2069
    )


def _has_lone_surrogate(text: str) -> bool:
    return any(0xD800 <= ord(character) <= 0xDFFF for character in text)


def validate_title(text: str, limits: Limits = LIMITS_V1) -> None:
    if not isinstance(text, str) or _has_lone_surrogate(text):
        raise WireError(ErrorCode.INVALID_TEXT, "a title must be valid Unicode text")
    if len(text) == 0:
        raise WireError(ErrorCode.INVALID_TEXT, "a title cannot be empty; leave it out instead")
    if len(text) > limits.title_chars:
        raise WireError(ErrorCode.TOO_LARGE, f"a title is at most {limits.title_chars} characters")
    if any(is_disallowed(ord(character), False) for character in text):
        raise WireError(ErrorCode.INVALID_TEXT, "a title cannot contain control or text-direction characters")


def validate_note(text: str, limits: Limits = LIMITS_V1) -> None:
    if not isinstance(text, str) or _has_lone_surrogate(text):
        raise WireError(ErrorCode.INVALID_TEXT, "a note must be valid Unicode text")
    if len(text) == 0:
        raise WireError(ErrorCode.INVALID_TEXT, "a note cannot be empty")
    if len(text.encode("utf-8")) > limits.note_bytes:
        raise WireError(ErrorCode.TOO_LARGE, f"a note is at most {limits.note_bytes} bytes of UTF-8")
    if any(is_disallowed(ord(character), True) for character in text):
        raise WireError(ErrorCode.INVALID_TEXT, "a note cannot contain control or text-direction characters")


def sanitize_device_name(text: str) -> str:
    """Receivers treat a device name as untrusted: unsafe characters are removed and it is cut to 40 characters."""
    kept = "".join(
        character
        for character in text
        # A lone surrogate is dropped like any other character that cannot be shown safely.
        if not 0xD800 <= ord(character) <= 0xDFFF and not is_disallowed(ord(character), False)
    )
    return kept.strip()[: protocol.MAX_DEVICE_NAME_SCALARS]


_ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"


def _is_white_space(character: str) -> bool:
    """White space as .NET and Swift see it: the ASCII controls tab to carriage return, U+0085, and Unicode spaces and
    line and paragraph separators. Python's own ``isspace`` also accepts U+001C to U+001F, which they do not."""
    return character in "\t\n\x0b\x0c\r\x85" or unicodedata.category(character) in ("Zs", "Zl", "Zp")


_PSK_SALT = b"erdigo-integration-v1/pairing-psk"


class PairingCode:
    """The one-time code a person reads from the receiver: 100 random bits as 20 Crockford base32 characters."""

    __slots__ = ("_canonical",)

    def __init__(self, text: str) -> None:
        """Reads a code as a person may type it: hyphens and white space are ignored, letters are uppercased, O is
        read as 0, and I and L as 1. Anything else, or any length but 20, is an error."""
        if not isinstance(text, str):
            raise TypeError("a pairing code is text")
        output = []
        for character in text:
            if character == "-" or _is_white_space(character):
                continue
            if not character.isascii():
                raise WireError(ErrorCode.INVALID_MESSAGE, "the pairing code has invalid characters")
            character = character.upper()
            character = {"O": "0", "I": "1", "L": "1"}.get(character, character)
            if character not in _ALPHABET:
                raise WireError(ErrorCode.INVALID_MESSAGE, "the pairing code has invalid characters")
            output.append(character)
        if len(output) != 20:
            raise WireError(ErrorCode.INVALID_MESSAGE, "the pairing code has 20 characters")
        self._canonical = "".join(output)

    @property
    def canonical(self) -> str:
        """The 20 characters without separators."""
        return self._canonical

    @property
    def display(self) -> str:
        """``XXXXX-XXXXX-XXXXX-XXXXX``, for showing on the receiver's screen."""
        return "-".join(self._canonical[group * 5 : group * 5 + 5] for group in range(4))

    @classmethod
    def generate(cls) -> "PairingCode":
        """Creates a new code from the system's secure random source."""
        # Each of the 20 bytes contributes exactly 5 unbiased bits.
        return cls("".join(_ALPHABET[byte & 0x1F] for byte in secrets.token_bytes(20)))

    def preshared_key(self) -> bytes:
        """The Noise pre-shared key: HKDF-SHA256 of the 20 characters, with the protocol's salt and no info."""
        # HKDF (RFC 5869) with an empty info and one block of output.
        pseudorandom_key = hmac.new(_PSK_SALT, self._canonical.encode("ascii"), hashlib.sha256).digest()
        return hmac.new(pseudorandom_key, b"\x01", hashlib.sha256).digest()

    def __eq__(self, other: object) -> bool:
        return isinstance(other, PairingCode) and hmac.compare_digest(self._canonical, other._canonical)

    def __hash__(self) -> int:
        return hash(self._canonical)

    def __repr__(self) -> str:
        # Avoids writing the code to logs by accident.
        return "<pairing code>"

    __str__ = __repr__


_TICKET_PREFIX = "erdigo-pair:1?"
#: Much longer than any valid text, which is 110 bytes without white space around it.
_MAX_TICKET_TEXT = 1024


class PairingTicket:
    """What a person gives the SDK to pair: the code, and the receiver's key fingerprint when it comes from the QR
    code. A typed code has no fingerprint, and the person then has to compare the key."""

    __slots__ = ("code", "fingerprint")

    def __init__(self, code: PairingCode, fingerprint: Optional[str] = None) -> None:
        if not isinstance(code, PairingCode):
            raise TypeError("a pairing ticket needs a PairingCode")
        self.code = code
        self.fingerprint = fingerprint

    @classmethod
    def parse(cls, text: str) -> "PairingTicket":
        """Accepts either a bare code or the QR form ``erdigo-pair:1?code=...&fp=...``.

        The QR form needs exactly one code and exactly one valid fingerprint, because the key comparison is what makes
        it stronger than a typed code. Nothing else may appear in it. Text over 1024 bytes is refused before it is
        looked at, because it may come from anywhere.
        """
        if not isinstance(text, str):
            raise TypeError("pairing text is a string")
        try:
            size = len(text.encode("utf-8"))
        except UnicodeEncodeError:
            raise WireError(ErrorCode.INVALID_MESSAGE, "the pairing code has invalid characters") from None
        if size > _MAX_TICKET_TEXT:
            raise WireError(ErrorCode.INVALID_MESSAGE, "the pairing text is too long")
        trimmed = text.strip()
        if not trimmed.startswith(_TICKET_PREFIX):
            return cls(PairingCode(trimmed))
        code: Optional[PairingCode] = None
        found: Optional[str] = None
        # Empty pieces are kept, so that '&&', a trailing '&' and a field starting with '=' are refused below.
        for part in trimmed[len(_TICKET_PREFIX) :].split("&"):
            pieces = part.split("=", 1)
            if len(pieces) != 2:
                raise WireError(ErrorCode.INVALID_MESSAGE, "the pairing text is malformed")
            if pieces[0] == "code" and code is None:
                code = PairingCode(pieces[1])
            elif pieces[0] == "fp" and found is None:
                lowered = pieces[1].lower()
                if not protocol.is_fingerprint(lowered):
                    raise WireError(ErrorCode.INVALID_MESSAGE, "the fingerprint in the pairing text is malformed")
                found = lowered
            else:
                raise WireError(ErrorCode.INVALID_MESSAGE, "the pairing text is malformed")
        if code is None:
            raise WireError(ErrorCode.INVALID_MESSAGE, "the pairing text has no code")
        if found is None:
            raise WireError(ErrorCode.INVALID_MESSAGE, "the pairing text has no fingerprint")
        return cls(code, found)

    @property
    def uri(self) -> Optional[str]:
        """The text of the QR code, or None without a valid fingerprint, because QR text without one would look
        stronger than it is."""
        if not protocol.is_fingerprint(self.fingerprint):
            return None
        return f"{_TICKET_PREFIX}code={self.code.display}&fp={self.fingerprint}"

    def __eq__(self, other: object) -> bool:
        return isinstance(other, PairingTicket) and self.code == other.code and self.fingerprint == other.fingerprint

    def __hash__(self) -> int:
        return hash((self.code, self.fingerprint))

    def __repr__(self) -> str:
        return "<pairing ticket>"

    __str__ = __repr__


class TokenBucket:
    """A token bucket, used by receivers to enforce rates and by the SDK to stay below them."""

    def __init__(self, capacity: int, refill_per_second: int, now: float) -> None:
        self.capacity = float(capacity)
        self.refill_per_second = float(refill_per_second)
        self._tokens = float(capacity)
        self._updated_at = now

    def _refill(self, now: float) -> None:
        elapsed = max(0.0, now - self._updated_at)
        self._tokens = min(self.capacity, self._tokens + elapsed * self.refill_per_second)
        self._updated_at = now

    def take(self, now: float, count: int = 1) -> Optional[float]:
        """Takes ``count`` tokens if possible and returns None. Otherwise takes nothing and returns how long to wait,
        in seconds."""
        self._refill(now)
        if self._tokens >= count:
            self._tokens -= count
            return None
        if self.refill_per_second <= 0:
            return math.inf
        return (min(float(count), self.capacity) - self._tokens) / self.refill_per_second
