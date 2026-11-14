"""Official Erdigo SDK for Python.

Write pins, markers, notes, photos and tracks to an Erdigo receiver on the local network, over the integration-v1
protocol. See README.md for how to pair and write, and spec/integration-v1.md for the protocol.
"""

from .client import ErdigoClient, RetryPolicy
from .discovery import DISCOVER, ReceiverTarget
from .errors import ErdigoError, ErrorKind, StorageProblem
from .identity import DeviceIdentity, ReceiverPairing
from .messages import ReceiverError
from .model import LIMITS_V1, Coordinate, ItemKind, Limits, MarkerColor, PairingCode, PairingTicket, PhotoType, TrackPoint
from .pairing import SDK_NAME, ConfirmReceiverKey, pair
from .protocol import ErrorCode, WireError, fingerprint, short_fingerprint
from .store import DeviceStore, FileDeviceStore, InMemoryDeviceStore, default_directory

__version__ = "0.1.0"

__all__ = [
    "DISCOVER",
    "LIMITS_V1",
    "SDK_NAME",
    "ConfirmReceiverKey",
    "Coordinate",
    "DeviceIdentity",
    "DeviceStore",
    "ErdigoClient",
    "ErdigoError",
    "ErrorCode",
    "ErrorKind",
    "FileDeviceStore",
    "InMemoryDeviceStore",
    "ItemKind",
    "Limits",
    "MarkerColor",
    "PairingCode",
    "PairingTicket",
    "PhotoType",
    "ReceiverError",
    "ReceiverPairing",
    "ReceiverTarget",
    "RetryPolicy",
    "StorageProblem",
    "TrackPoint",
    "WireError",
    "default_directory",
    "fingerprint",
    "pair",
    "short_fingerprint",
]
