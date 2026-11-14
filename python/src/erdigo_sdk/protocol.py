"""Constants of the integration-v1 protocol. See spec/integration-v1.md."""

from __future__ import annotations

import enum
import hashlib
from typing import Optional

VERSION = 1
SERVICE_TYPE = "_erdigo-int._tcp"
MAX_FRAME_LENGTH = 65535
MAX_PLAINTEXT_LENGTH = 65519
MAX_HANDSHAKE_FRAME_LENGTH = 1024
MAX_CHUNK_BYTES = 48_000
MAX_POINTS_PER_MESSAGE = 100
MAX_SEQUENCE = (1 << 53) - 1
MAX_DEVICE_NAME_SCALARS = 40
CONNECT_FIRST_MESSAGE_LENGTH = 32 + 48 + 8 + 16
PAIR_FIRST_MESSAGE_LENGTH = 32 + 16


class Mode(enum.IntEnum):
    """The fifth byte of the preamble."""

    CONNECT = 0x01
    PAIR = 0x02


def preamble(mode: Mode) -> bytes:
    """Five bytes sent in the clear before the handshake. They are also the Noise prologue, so tampering with them
    makes the handshake fail."""
    return bytes([0x45, 0x49, 0x4E, 0x31, int(mode)])


def parse_preamble(data: bytes) -> Optional[Mode]:
    if len(data) != 5 or data[:4] != b"EIN1":
        return None
    try:
        return Mode(data[4])
    except ValueError:
        return None


_FINGERPRINT_LABEL = b"erdigo-integration-v1/fingerprint"


def fingerprint(public_key: bytes) -> str:
    """The receiver's or a device's key fingerprint, as 64 lowercase hexadecimal digits."""
    return hashlib.sha256(_FINGERPRINT_LABEL + bytes(public_key)).hexdigest()


def short_fingerprint(text: str) -> str:
    """The first 16 hexadecimal digits in groups of four, for showing to people."""
    head = text[:16]
    return "-".join(head[index : index + 4] for index in range(0, len(head), 4))


def hex_decode(text: str) -> Optional[bytes]:
    """Accepts lowercase or uppercase hexadecimal digits only, in pairs. Returns None for anything else."""
    if len(text) % 2 != 0 or any(digit not in "0123456789abcdefABCDEF" for digit in text):
        return None
    return bytes.fromhex(text)


def is_fingerprint(text: Optional[str]) -> bool:
    """True for exactly 64 lowercase hexadecimal digits, the one spelling a fingerprint has (spec section 3)."""
    return text is not None and len(text) == 64 and all(digit in "0123456789abcdef" for digit in text)


class ErrorCode(str, enum.Enum):
    """The error codes of spec section 10, spelled as on the wire."""

    VERSION_UNSUPPORTED = "version_unsupported"
    INVALID_MESSAGE = "invalid_message"
    INVALID_ID = "invalid_id"
    INVALID_COORDINATE = "invalid_coordinate"
    INVALID_TEXT = "invalid_text"
    NOT_PERMITTED = "not_permitted"
    NOT_FOUND = "not_found"
    KIND_CONFLICT = "kind_conflict"
    INVALID_STATE = "invalid_state"
    STALE_SEQ = "stale_seq"
    SEQ_CONFLICT = "seq_conflict"
    LIMIT_ITEMS = "limit_items"
    LIMIT_PHOTOS = "limit_photos"
    LIMIT_TRACKS = "limit_tracks"
    LIMIT_DISK = "limit_disk"
    TOO_LARGE = "too_large"
    PHOTO_INVALID = "photo_invalid"
    RATE_LIMITED = "rate_limited"
    TOO_MANY_ERRORS = "too_many_errors"
    PAUSED = "paused"
    REPLACED = "replaced"
    CODE_REUSED = "code_reused"
    INTERNAL_ERROR = "internal_error"

    @classmethod
    def from_wire(cls, text: str) -> Optional["ErrorCode"]:
        """Reads a code from the wire. A code this SDK does not know returns None."""
        try:
            return cls(text)
        except ValueError:
            return None

    @property
    def is_retryable(self) -> bool:
        """Retryable errors do not consume the sequence number."""
        return self in (ErrorCode.RATE_LIMITED, ErrorCode.PAUSED, ErrorCode.INTERNAL_ERROR)

    def __str__(self) -> str:
        return self.value


class WireError(ValueError):
    """A wire-level rejection, carrying the error code a receiver would report."""

    def __init__(self, code: ErrorCode, detail: str) -> None:
        super().__init__(detail)
        self.code = code
        self.detail = detail
