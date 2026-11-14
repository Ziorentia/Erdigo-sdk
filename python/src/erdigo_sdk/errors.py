"""The errors the SDK reports."""

from __future__ import annotations

import enum
from typing import Optional, Sequence, Tuple

from .messages import ReceiverError
from .model import ItemKind


class ErrorKind(enum.Enum):
    """What went wrong, for :class:`ErdigoError`."""

    #: No receiver answered. A receiver stays silent when it is off, asleep, on another network, when the pairing code
    #: is wrong or expired, and when this device is not allowed. These cases cannot be told apart, by design.
    NO_ANSWER = "no_answer"
    #: The receiver's key does not match the fingerprint supplied for pairing.
    FINGERPRINT_MISMATCH = "fingerprint_mismatch"
    #: The person did not confirm the receiver's key, so the receiver was not asked to pair.
    KEY_NOT_CONFIRMED = "key_not_confirmed"
    #: The person at the receiver declined the request.
    PAIRING_DENIED = "pairing_denied"
    #: The person at the receiver did not answer in time.
    PAIRING_TIMED_OUT = "pairing_timed_out"
    #: The code was used twice. The receiver warned its user and removed the pairing made with it.
    CODE_REUSED = "code_reused"
    #: The receiver rejected a request. See :attr:`ErdigoError.receiver_error`.
    REJECTED = "rejected"
    #: The item kind is not among those the person allowed for this device. See :attr:`ErdigoError.item_kind`.
    NOT_PERMITTED = "not_permitted"
    #: The receiver does not support any protocol version this SDK speaks. See :attr:`ErdigoError.supported`.
    VERSION_UNSUPPORTED = "version_unsupported"
    #: Another connection using this device key took over.
    REPLACED = "replaced"
    #: Another writer using this device key advanced the sequence number. See :attr:`ErdigoError.last_seq`.
    SEQUENCE_CONFLICT = "sequence_conflict"
    #: The receiver broke the protocol.
    PROTOCOL_VIOLATION = "protocol_violation"
    INVALID_ARGUMENT = "invalid_argument"
    #: Stored state could not be used. See :attr:`ErdigoError.storage_problem`.
    STORAGE = "storage"


class StorageProblem(enum.Enum):
    """What was wrong with stored state."""

    NONE = "none"
    #: The device key file exists but its contents are not a device key. A new key was not created.
    IDENTITY_DAMAGED = "identity_damaged"
    #: The device key file exists but could not be opened, read or decrypted. A new key was not created.
    IDENTITY_UNREADABLE = "identity_unreadable"
    #: The pairing file exists but its contents could not be understood.
    STATE_DAMAGED = "state_damaged"
    #: The pairing file exists but could not be opened or read.
    STATE_UNREADABLE = "state_unreadable"
    #: A folder or file could be read or replaced by someone else, so it was not used.
    INSECURE = "insecure"
    #: Anything else, such as a full disk.
    OTHER = "other"


def _describe(kind: ErrorKind, detail: Optional[str], error: Optional[ReceiverError], item_kind: Optional[ItemKind], supported: Optional[Sequence[int]]) -> str:
    if kind is ErrorKind.NO_ANSWER:
        return (
            "No receiver answered. It may be off, asleep or on another network, the pairing code may be wrong or expired, "
            "or this device may have been removed."
        )
    if kind is ErrorKind.FINGERPRINT_MISMATCH:
        return "The receiver's key does not match the expected fingerprint. Pairing was stopped."
    if kind is ErrorKind.KEY_NOT_CONFIRMED:
        return "The receiver's key was not confirmed. No pairing was saved, no items were sent, and the receiver was not asked to pair."
    if kind is ErrorKind.PAIRING_DENIED:
        return "The request was declined on the receiver."
    if kind is ErrorKind.PAIRING_TIMED_OUT:
        return "Nobody confirmed the request on the receiver in time."
    if kind is ErrorKind.CODE_REUSED:
        return "This pairing code was already used. The receiver removed the pairing made with it. Ask for a new code."
    if kind is ErrorKind.REJECTED:
        return f"The receiver rejected the request: {error.code if error else 'unknown'}."
    if kind is ErrorKind.NOT_PERMITTED:
        return f"This device is not allowed to send '{item_kind.value if item_kind else 'unknown'}' items."
    if kind is ErrorKind.VERSION_UNSUPPORTED:
        versions = ", ".join(str(version) for version in (supported or ()))
        return f"The receiver supports protocol versions [{versions}], which this SDK does not speak. Update the SDK."
    if kind is ErrorKind.REPLACED:
        return "Another connection using this device key took over."
    if kind is ErrorKind.SEQUENCE_CONFLICT:
        return "Another program is writing with this device key at the same time."
    if kind is ErrorKind.PROTOCOL_VIOLATION:
        return f"The receiver broke the protocol: {detail}."
    return detail or kind.value


class ErdigoError(Exception):
    """Failures reported by the SDK. :attr:`kind` says what happened."""

    def __init__(
        self,
        kind: ErrorKind,
        detail: Optional[str] = None,
        *,
        receiver_error: Optional[ReceiverError] = None,
        item_kind: Optional[ItemKind] = None,
        supported: Optional[Sequence[int]] = None,
        last_seq: Optional[int] = None,
        storage_problem: StorageProblem = StorageProblem.NONE,
    ) -> None:
        super().__init__(_describe(kind, detail, receiver_error, item_kind, supported))
        self.kind = kind
        self.detail = detail
        self.receiver_error = receiver_error
        self.item_kind = item_kind
        self.supported: Optional[Tuple[int, ...]] = tuple(supported) if supported is not None else None
        self.last_seq = last_seq
        if kind is ErrorKind.STORAGE and storage_problem is StorageProblem.NONE:
            storage_problem = StorageProblem.OTHER
        self.storage_problem = storage_problem
