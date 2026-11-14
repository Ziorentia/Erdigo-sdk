"""Persistent device state: its key, its pairings and the per-receiver connect counter."""

from __future__ import annotations

import abc
import datetime
import json
import os
import sys
import threading
from typing import Dict, List, Optional

from . import noise, protocol
from .errors import ErdigoError, ErrorKind, StorageProblem
from .identity import DeviceIdentity, ReceiverPairing
from .model import ItemKind
from .private_file import FileLock, PrivateFileError, PrivateFileProblem, ensure_private_directory, read_private, write_atomically

_WINDOWS = sys.platform == "win32"


class DeviceStore(abc.ABC):
    """Where a device keeps its key, its pairings and its connect counters."""

    @abc.abstractmethod
    def load_or_create_identity(self) -> DeviceIdentity: ...

    @abc.abstractmethod
    def pairings(self) -> List[ReceiverPairing]: ...

    @abc.abstractmethod
    def save(self, pairing: ReceiverPairing) -> None:
        """Adds a pairing, or replaces the one for the same receiver. Its counter floor takes effect here."""

    @abc.abstractmethod
    def update_address(self, receiver_fingerprint: str, host: str, port: int) -> None:
        """Remembers where a receiver was found. Does nothing if the pairing has since been forgotten."""

    @abc.abstractmethod
    def remove_pairing(self, fingerprint: str) -> None: ...

    @abc.abstractmethod
    def next_connect_counter(self, receiver_fingerprint: str, now_ms: int) -> int:
        """Returns a value strictly greater than any previous result for this receiver. The value must be durable
        before it is returned, because a receiver ignores a counter it has seen."""


class InMemoryDeviceStore(DeviceStore):
    """Keeps everything in memory. Intended for tests and short-lived programs."""

    def __init__(self, identity: Optional[DeviceIdentity] = None) -> None:
        self._lock = threading.Lock()
        self._identity = identity
        self._pairings: List[ReceiverPairing] = []
        self._counters: Dict[str, int] = {}

    def load_or_create_identity(self) -> DeviceIdentity:
        with self._lock:
            if self._identity is None:
                self._identity = DeviceIdentity.generate()
            return self._identity

    def pairings(self) -> List[ReceiverPairing]:
        with self._lock:
            return list(self._pairings)

    def save(self, pairing: ReceiverPairing) -> None:
        with self._lock:
            self._pairings = [existing for existing in self._pairings if existing.receiver_key != pairing.receiver_key]
            self._pairings.append(pairing)
            self._counters[pairing.fingerprint] = max(self._counters.get(pairing.fingerprint, 0), pairing.counter_floor)

    def update_address(self, receiver_fingerprint: str, host: str, port: int) -> None:
        with self._lock:
            for index, pairing in enumerate(self._pairings):
                if pairing.fingerprint == receiver_fingerprint:
                    self._pairings[index] = pairing.with_address(host, port)
                    return

    def remove_pairing(self, fingerprint: str) -> None:
        with self._lock:
            self._pairings = [pairing for pairing in self._pairings if pairing.fingerprint != fingerprint]
            self._counters.pop(fingerprint, None)

    def next_connect_counter(self, receiver_fingerprint: str, now_ms: int) -> int:
        with self._lock:
            value = max(now_ms, self._counters.get(receiver_fingerprint, 0) + 1)
            self._counters[receiver_fingerprint] = value
            return value


_PAIRINGS_FILE = "pairings.json"
_DPAPI_KEY_FILE = "identity.dpapi"
_PLAIN_KEY_FILE = "identity.key"
_LOCK_FILE = ".lock"
_DPAPI_MAGIC = b"EDK1"
_PLAIN_MAGIC = b"EDK0"


def default_directory(profile: str = "default") -> str:
    """A folder of this edition's own, so that it never shares a device key with the Swift or .NET SDK.

    ``%LOCALAPPDATA%\\ErdigoSDK.Python\\<profile>`` on Windows, ``~/Library/Application Support/ErdigoSDK.Python/<profile>``
    on macOS, and ``$XDG_DATA_HOME/ErdigoSDK.Python/<profile>`` (by default ``~/.local/share``) elsewhere.
    """
    if _WINDOWS:
        base = os.environ.get("LOCALAPPDATA")
    elif sys.platform == "darwin":
        base = os.path.join(os.path.expanduser("~"), "Library", "Application Support")
    else:
        base = os.environ.get("XDG_DATA_HOME") or os.path.join(os.path.expanduser("~"), ".local", "share")
    if not base or base.startswith("~"):
        raise ErdigoError(ErrorKind.STORAGE, "the folder for application data could not be found")
    return os.path.join(base, "ErdigoSDK.Python", profile)


class FileDeviceStore(DeviceStore):
    """Stores state in a private folder. Refuses to read files that other users could read or replace.

    On Windows the device key is encrypted with DPAPI for the current user, in ``identity.dpapi``. On macOS and Linux
    it is kept in ``identity.key`` with mode 0600. A key file that exists but cannot be read is reported, and never
    silently replaced by a new key.
    """

    def __init__(self, directory: str) -> None:
        self.directory = os.path.abspath(directory)
        self._lock = threading.Lock()
        try:
            ensure_private_directory(self.directory)
        except PrivateFileError as error:
            raise ErdigoError(ErrorKind.STORAGE, error.message, storage_problem=StorageProblem.INSECURE) from error
        except OSError as error:
            raise ErdigoError(ErrorKind.STORAGE, "could not create the state folder") from error

    #: Where the key is kept: DPAPI on Windows, a 0600 file elsewhere.
    protects_key_with_dpapi = _WINDOWS

    def _path(self, name: str) -> str:
        return os.path.join(self.directory, name)

    def _locked(self) -> "_Locked":
        return _Locked(self)

    def _read(self, name: str, unreadable: StorageProblem, max_bytes: int = 4 * 1024 * 1024) -> Optional[bytes]:
        try:
            return read_private(self._path(name), max_bytes)
        except PrivateFileError as error:
            if error.problem is PrivateFileProblem.MISSING:
                return None
            if error.problem is PrivateFileProblem.INSECURE:
                raise ErdigoError(ErrorKind.STORAGE, error.message, storage_problem=StorageProblem.INSECURE) from error
            raise ErdigoError(
                ErrorKind.STORAGE, f"{name} exists but could not be read. Nothing was changed.", storage_problem=unreadable
            ) from error

    def _write(self, name: str, data: bytes, overwrite: bool = True) -> None:
        try:
            write_atomically(self._path(name), data, overwrite)
        except FileExistsError as error:
            raise ErdigoError(
                ErrorKind.STORAGE,
                f"{name} exists but could not be read. Nothing was changed, and no new key was created.",
                storage_problem=StorageProblem.IDENTITY_UNREADABLE,
            ) from error
        except OSError as error:
            raise ErdigoError(ErrorKind.STORAGE, f"could not write {name}") from error

    # The key

    def load_or_create_identity(self) -> DeviceIdentity:
        with self._locked():
            name = _DPAPI_KEY_FILE if self.protects_key_with_dpapi else _PLAIN_KEY_FILE
            stored = self._read(name, StorageProblem.IDENTITY_UNREADABLE, max_bytes=64 * 1024)
            if stored is not None:
                return self._open_dpapi_key(stored) if self.protects_key_with_dpapi else self._open_plain_key(stored)
            # Only a file that is not there at all leads to a new key.
            created = DeviceIdentity.generate()
            sealed = self._seal_dpapi_key(created.private_key) if self.protects_key_with_dpapi else _PLAIN_MAGIC + created.private_key
            # Never over an existing file, even one this process could not see.
            self._write(name, sealed, overwrite=False)
            return created

    @staticmethod
    def _open_plain_key(stored: bytes) -> DeviceIdentity:
        if len(stored) != len(_PLAIN_MAGIC) + noise.DH_LENGTH or not stored.startswith(_PLAIN_MAGIC):
            raise ErdigoError(
                ErrorKind.STORAGE,
                f"{_PLAIN_KEY_FILE} exists but does not hold a device key. It was left as it is. Move it away to create a new key, which means pairing again.",
                storage_problem=StorageProblem.IDENTITY_DAMAGED,
            )
        return DeviceIdentity.from_private_key(stored[len(_PLAIN_MAGIC) :])

    @staticmethod
    def _open_dpapi_key(stored: bytes) -> DeviceIdentity:
        from . import _windows

        if len(stored) <= len(_DPAPI_MAGIC) or not stored.startswith(_DPAPI_MAGIC):
            raise ErdigoError(
                ErrorKind.STORAGE,
                f"{_DPAPI_KEY_FILE} exists but does not hold a device key. It was left as it is. Move it away to create a new key, which means pairing again.",
                storage_problem=StorageProblem.IDENTITY_DAMAGED,
            )
        try:
            raw = _windows.unprotect(stored[len(_DPAPI_MAGIC) :])
        except OSError as error:
            raise ErdigoError(
                ErrorKind.STORAGE,
                f"{_DPAPI_KEY_FILE} exists but Windows could not decrypt it. It belongs to another Windows user or computer, or it is damaged. It was left as it is, and no new key was created.",
                storage_problem=StorageProblem.IDENTITY_UNREADABLE,
            ) from error
        if len(raw) != noise.DH_LENGTH:
            raise ErdigoError(
                ErrorKind.STORAGE,
                f"{_DPAPI_KEY_FILE} exists but does not hold a device key. It was left as it is.",
                storage_problem=StorageProblem.IDENTITY_DAMAGED,
            )
        return DeviceIdentity.from_private_key(raw)

    @staticmethod
    def _seal_dpapi_key(raw: bytes) -> bytes:
        from . import _windows

        try:
            return _DPAPI_MAGIC + _windows.protect(raw)
        except OSError as error:
            raise ErdigoError(ErrorKind.STORAGE, "Windows could not encrypt the device key") from error

    # Pairings and counters

    def _load_state(self) -> "_State":
        data = self._read(_PAIRINGS_FILE, StorageProblem.STATE_UNREADABLE)
        if data is None:
            return _State([], {})
        try:
            return _State.decode(data)
        except (ValueError, KeyError, TypeError) as error:
            raise ErdigoError(
                ErrorKind.STORAGE, f"{_PAIRINGS_FILE} could not be read", storage_problem=StorageProblem.STATE_DAMAGED
            ) from error

    def _save_state(self, state: "_State") -> None:
        self._write(_PAIRINGS_FILE, state.encode())

    def pairings(self) -> List[ReceiverPairing]:
        with self._locked():
            return list(self._load_state().pairings)

    def save(self, pairing: ReceiverPairing) -> None:
        with self._locked():
            state = self._load_state()
            state.pairings = [existing for existing in state.pairings if existing.receiver_key != pairing.receiver_key]
            state.pairings.append(pairing)
            state.counters[pairing.fingerprint] = max(state.counters.get(pairing.fingerprint, 0), pairing.counter_floor)
            self._save_state(state)

    def update_address(self, receiver_fingerprint: str, host: str, port: int) -> None:
        with self._locked():
            state = self._load_state()
            for index, pairing in enumerate(state.pairings):
                if pairing.fingerprint == receiver_fingerprint:
                    state.pairings[index] = pairing.with_address(host, port)
                    self._save_state(state)
                    return

    def remove_pairing(self, fingerprint: str) -> None:
        with self._locked():
            state = self._load_state()
            state.pairings = [pairing for pairing in state.pairings if pairing.fingerprint != fingerprint]
            state.counters.pop(fingerprint, None)
            self._save_state(state)

    def next_connect_counter(self, receiver_fingerprint: str, now_ms: int) -> int:
        with self._locked():
            state = self._load_state()
            value = max(now_ms, state.counters.get(receiver_fingerprint, 0) + 1)
            state.counters[receiver_fingerprint] = value
            self._save_state(state)
            return value


class _Locked:
    """Serialises access between threads and between processes."""

    def __init__(self, store: FileDeviceStore) -> None:
        self._store = store
        self._file_lock = FileLock(store._path(_LOCK_FILE))

    def __enter__(self) -> None:
        self._store._lock.acquire()
        try:
            self._file_lock.__enter__()
        except OSError as error:
            self._store._lock.release()
            raise ErdigoError(ErrorKind.STORAGE, "could not lock the state folder") from error

    def __exit__(self, *details: object) -> None:
        try:
            self._file_lock.__exit__(*details)
        finally:
            self._store._lock.release()


def _require(value: object, kind: type) -> object:
    if not isinstance(value, kind) or (kind is int and isinstance(value, bool)):
        raise ValueError("unexpected value in the state file")
    return value


class _State:
    def __init__(self, pairings: List[ReceiverPairing], counters: Dict[str, int]) -> None:
        self.pairings = pairings
        self.counters = counters

    @classmethod
    def decode(cls, data: bytes) -> "_State":
        root = json.loads(data.decode("utf-8"))
        if _require(root, dict).get("version") != 1:
            raise ValueError("unknown version")
        pairings = []
        for element in _require(root["pairings"], list):
            _require(element, dict)
            key = protocol.hex_decode(_require(element["receiverKey"], str))
            if key is None or not noise.is_canonical_public_key(key):
                raise ValueError("a receiver key is not valid")
            kinds = []
            for kind in _require(element["kinds"], list):
                found = ItemKind.from_wire(_require(kind, str))
                if found is None:
                    raise ValueError("unknown kind")
                kinds.append(found)
            host = element.get("lastHost")
            port = element.get("lastPort")
            if host is not None:
                _require(host, str)
            if port is not None and not 0 < _require(port, int) < 65536:
                raise ValueError("port out of range")
            paired_at = datetime.datetime.fromisoformat(_require(element["pairedAt"], str).replace("Z", "+00:00"))
            floor = _require(element["counterFloor"], int)
            if floor < 0:
                raise ValueError("negative counter")
            pairings.append(ReceiverPairing(_require(element["label"], str), key, tuple(kinds), host, port, paired_at, floor))
        counters = {}
        for name, value in _require(root["counters"], dict).items():
            if _require(value, int) < 0:
                raise ValueError("negative counter")
            counters[name] = value
        return cls(pairings, counters)

    def encode(self) -> bytes:
        pairings = []
        for pairing in self.pairings:
            entry: dict = {"label": pairing.label, "receiverKey": pairing.receiver_key.hex(), "kinds": [kind.value for kind in pairing.kinds]}
            if pairing.last_host is not None:
                entry["lastHost"] = pairing.last_host
            if pairing.last_port is not None:
                entry["lastPort"] = pairing.last_port
            paired_at = pairing.paired_at
            if paired_at.tzinfo is None:
                paired_at = paired_at.replace(tzinfo=datetime.timezone.utc)
            entry["pairedAt"] = paired_at.astimezone(datetime.timezone.utc).isoformat().replace("+00:00", "Z")
            entry["counterFloor"] = pairing.counter_floor
            pairings.append(entry)
        root = {"version": 1, "pairings": pairings, "counters": dict(sorted(self.counters.items()))}
        return json.dumps(root, indent=2, ensure_ascii=False).encode("utf-8")
