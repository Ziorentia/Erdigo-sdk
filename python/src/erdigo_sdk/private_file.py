"""Files and folders that only their owner can read, the way ``ssh`` treats its key files.

On macOS and Linux a private folder has mode 0700 and a private file mode 0600, and both must belong to the current
user. On Windows nobody but the current user, SYSTEM and the Administrators group may be allowed anything on them.
"""

from __future__ import annotations

import enum
import os
import secrets
import stat
import sys
import time
from typing import Optional

_WINDOWS = sys.platform == "win32"
_REPARSE_POINT = 0x400


class PrivateFileProblem(enum.Enum):
    MISSING = "missing"
    UNREADABLE = "unreadable"
    INSECURE = "insecure"
    TOO_LARGE = "too large"


class PrivateFileError(OSError):
    def __init__(self, problem: PrivateFileProblem, message: str) -> None:
        super().__init__(message)
        self.problem = problem
        self.message = message

    def __str__(self) -> str:
        return self.message


def _is_link(info: os.stat_result) -> bool:
    return stat.S_ISLNK(info.st_mode) or bool(getattr(info, "st_file_attributes", 0) & _REPARSE_POINT)


def _windows_private(path: str) -> bool:
    from . import _windows

    try:
        return _windows.has_private_access_list(path)
    except OSError:
        return False


def read_private(path: str, max_bytes: int) -> bytes:
    """Reads a file only if it is a regular file that nobody else can read or replace."""
    name = os.path.basename(path)
    try:
        info = os.lstat(path)
    except FileNotFoundError:
        raise PrivateFileError(PrivateFileProblem.MISSING, f"{name} does not exist") from None
    except OSError:
        raise PrivateFileError(PrivateFileProblem.UNREADABLE, f"{name} could not be opened") from None
    if _is_link(info):
        raise PrivateFileError(PrivateFileProblem.INSECURE, f"{name} must be a regular file, not a link")
    if not stat.S_ISREG(info.st_mode):
        raise PrivateFileError(PrivateFileProblem.INSECURE, f"{name} must be a regular file")
    flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_BINARY", 0)
    try:
        descriptor = os.open(path, flags)
    except FileNotFoundError:
        raise PrivateFileError(PrivateFileProblem.MISSING, f"{name} does not exist") from None
    except OSError:
        raise PrivateFileError(PrivateFileProblem.UNREADABLE, f"{name} could not be opened") from None
    with os.fdopen(descriptor, "rb", buffering=0) as handle:
        opened = os.fstat(handle.fileno())
        if _WINDOWS:
            if not _windows_private(path):
                raise PrivateFileError(PrivateFileProblem.INSECURE, f"{name} must be readable by you alone")
        elif (
            not stat.S_ISREG(opened.st_mode)
            or opened.st_mode & 0o077
            or opened.st_uid != os.geteuid()
        ):
            raise PrivateFileError(PrivateFileProblem.INSECURE, f"{name} must be a regular file owned by you with mode 0600")
        if opened.st_size > max_bytes:
            raise PrivateFileError(PrivateFileProblem.TOO_LARGE, f"{name} is unexpectedly large")
        try:
            data = handle.read(max_bytes + 1)
        except OSError:
            raise PrivateFileError(PrivateFileProblem.UNREADABLE, f"{name} could not be read") from None
        if len(data) > max_bytes:
            raise PrivateFileError(PrivateFileProblem.TOO_LARGE, f"{name} changed while it was read")
        return data


def _create_private(path: str) -> int:
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_BINARY", 0)
    # In a private folder on Windows a new file inherits the folder's access list.
    return os.open(path, flags, 0o600)


def write_atomically(path: str, data: bytes, overwrite: bool = True) -> None:
    """Writes through a temporary file and a rename, so a crash never leaves half a file. With ``overwrite`` false it
    never replaces a file that is there, even one this process cannot see into."""
    full = os.path.abspath(path)
    directory = os.path.dirname(full)
    temporary = os.path.join(directory, f".{os.path.basename(full)}.{secrets.token_hex(16)}")
    descriptor = _create_private(temporary)
    succeeded = False
    try:
        with os.fdopen(descriptor, "wb") as handle:
            handle.write(data)
            handle.flush()
            os.fsync(handle.fileno())
        if overwrite:
            _replace(temporary, full)
        elif _WINDOWS:
            # A rename on Windows never replaces a file.
            os.rename(temporary, full)
        else:
            # A hard link fails if the name is taken, where a rename would replace it.
            os.link(temporary, full)
            os.unlink(temporary)
        succeeded = True
    finally:
        if not succeeded:
            try:
                os.unlink(temporary)
            except OSError:
                pass


def _replace(source: str, destination: str) -> None:
    # A virus scanner or an indexer can hold a file open for a moment on Windows.
    for attempt in range(21):
        try:
            os.replace(source, destination)
            return
        except PermissionError:
            if not _WINDOWS or attempt == 20:
                raise
            time.sleep(0.05)


def ensure_private_directory(path: str) -> None:
    """Creates the folder privately if it is missing, then checks it, whoever made it."""
    full = os.path.abspath(path)
    parent = os.path.dirname(full)
    if parent:
        os.makedirs(parent, exist_ok=True)
    if not os.path.lexists(full):
        try:
            if _WINDOWS:
                from . import _windows

                _windows.create_private_directory(full)
            else:
                os.mkdir(full, 0o700)
        except FileExistsError:
            # Created by someone else at the same moment. It is checked below like any other.
            pass
    check_private_directory(full)


def check_private_directory(path: str) -> None:
    try:
        info = os.lstat(path)
    except OSError:
        raise PrivateFileError(PrivateFileProblem.INSECURE, "the state folder must be a real folder") from None
    if _is_link(info) or not stat.S_ISDIR(info.st_mode):
        raise PrivateFileError(PrivateFileProblem.INSECURE, "the state folder must be a real folder")
    if _WINDOWS:
        if not _windows_private(path):
            raise PrivateFileError(
                PrivateFileProblem.INSECURE,
                "the state folder must be readable by you alone. Remove the other entries from its permissions, or choose another folder",
            )
        return
    if info.st_mode & 0o077 or info.st_uid != os.geteuid():
        raise PrivateFileError(PrivateFileProblem.INSECURE, "the state folder must be a real folder owned by you with mode 0700")


class FileLock:
    """Serialises access to a state folder between processes, with a lock file of mode 0600."""

    def __init__(self, path: str, give_up_after: float = 30.0) -> None:
        self._path = path
        self._give_up_after = give_up_after
        self._descriptor: Optional[int] = None

    def __enter__(self) -> "FileLock":
        flags = os.O_RDWR | os.O_CREAT | getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_BINARY", 0)
        self._descriptor = os.open(self._path, flags, 0o600)
        give_up = time.monotonic() + self._give_up_after
        while True:
            try:
                if _WINDOWS:
                    import msvcrt

                    os.lseek(self._descriptor, 0, os.SEEK_SET)
                    msvcrt.locking(self._descriptor, msvcrt.LK_NBLCK, 1)
                else:
                    import fcntl

                    fcntl.flock(self._descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
                return self
            except OSError:
                if time.monotonic() >= give_up:
                    os.close(self._descriptor)
                    self._descriptor = None
                    raise
                # Another process holds the lock.
                time.sleep(0.01)

    def __exit__(self, *_: object) -> None:
        if self._descriptor is None:
            return
        try:
            if _WINDOWS:
                import msvcrt

                os.lseek(self._descriptor, 0, os.SEEK_SET)
                msvcrt.locking(self._descriptor, msvcrt.LK_UNLCK, 1)
        finally:
            os.close(self._descriptor)
            self._descriptor = None
