"""The device's key, and what it remembers about each receiver."""

from __future__ import annotations

import dataclasses
import datetime
from typing import Optional, Tuple

from . import noise, protocol
from .errors import ErdigoError, ErrorKind, StorageProblem
from .model import ItemKind


class DeviceIdentity:
    """The device's one secret: an X25519 key used only for this protocol.

    Python gives no way to wipe a key from memory. See "Secrets in memory" in the README.
    """

    __slots__ = ("_private_key", "public_key", "fingerprint")

    def __init__(self, private_key: bytes) -> None:
        if not isinstance(private_key, (bytes, bytearray)) or len(private_key) != noise.DH_LENGTH:
            raise ErdigoError(ErrorKind.STORAGE, "a device key is 32 bytes", storage_problem=StorageProblem.IDENTITY_DAMAGED)
        self._private_key = bytes(private_key)
        #: The public key, which identifies this device to a receiver.
        self.public_key = noise.public_key(self._private_key)
        self.fingerprint = protocol.fingerprint(self.public_key)

    @classmethod
    def generate(cls) -> "DeviceIdentity":
        return cls(noise.new_private_key())

    @classmethod
    def from_private_key(cls, raw: bytes) -> "DeviceIdentity":
        """Restores a key read from protected storage."""
        return cls(raw)

    @property
    def private_key(self) -> bytes:
        """The private key, for the handshake and for writing it to protected storage only."""
        return self._private_key

    def __repr__(self) -> str:
        return f"<device identity {protocol.short_fingerprint(self.fingerprint)}>"

    __str__ = __repr__


def _utc_now() -> datetime.datetime:
    return datetime.datetime.now(datetime.timezone.utc)


@dataclasses.dataclass(frozen=True)
class ReceiverPairing:
    """What a device remembers about one receiver. A device can be paired with several receivers."""

    #: A local name for this receiver. It is never sent anywhere.
    label: str
    receiver_key: bytes
    kinds: Tuple[ItemKind, ...]
    last_host: Optional[str] = None
    last_port: Optional[int] = None
    paired_at: datetime.datetime = dataclasses.field(default_factory=_utc_now)
    #: The highest connect counter the receiver had accepted when it approved this pairing. A later counter must be
    #: greater, even if this device has lost its own count.
    counter_floor: int = 0

    def __post_init__(self) -> None:
        object.__setattr__(self, "receiver_key", bytes(self.receiver_key))
        object.__setattr__(self, "kinds", tuple(ItemKind(kind) for kind in self.kinds))

    @property
    def fingerprint(self) -> str:
        return protocol.fingerprint(self.receiver_key)

    def with_address(self, host: str, port: int) -> "ReceiverPairing":
        return dataclasses.replace(self, last_host=host, last_port=port)
