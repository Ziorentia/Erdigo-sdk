"""An ordered byte stream over TCP with the integration-v1 framing: a two-byte big-endian length, then one Noise
message."""

from __future__ import annotations

import enum
import ipaddress
import socket
import time
from typing import Optional, Tuple

from . import protocol


class StreamFailure(enum.Enum):
    CLOSED = "closed"
    TIMED_OUT = "timed out"
    FRAME_TOO_LARGE = "frame too large"
    CONNECTION_FAILED = "connection failed"


class StreamError(OSError):
    """The connection failed. This is about the connection, never an answer from the receiver."""

    def __init__(self, failure: StreamFailure) -> None:
        super().__init__(f"connection: {failure.value}")
        self.failure = failure


class DeadlinePassed(TimeoutError):
    """The caller's own time limit for a request ran out. Raised instead of retrying, and never caught by the SDK."""

    def __init__(self) -> None:
        super().__init__("the request did not finish within the time it was given")


def check_deadline(deadline: Optional[float]) -> None:
    if deadline is not None and time.monotonic() >= deadline:
        raise DeadlinePassed()


def frame(body: bytes) -> bytes:
    if len(body) > protocol.MAX_FRAME_LENGTH:
        raise StreamError(StreamFailure.FRAME_TOO_LARGE)
    return len(body).to_bytes(2, "big") + bytes(body)


def address_text(host: str) -> str:
    """An address as text, without an IPv6 zone and with an IPv4 address mapped into IPv6 written as IPv4."""
    host = host.split("%", 1)[0]
    try:
        address = ipaddress.ip_address(host)
    except ValueError:
        return host
    if isinstance(address, ipaddress.IPv6Address) and address.ipv4_mapped is not None:
        return str(address.ipv4_mapped)
    return str(address)


class FramedStream:
    """One TCP connection. One thread at a time may use it.

    ``arm`` sets a deadline for the connection itself: once it passes, reads and writes fail with
    :attr:`StreamFailure.TIMED_OUT`. Each operation may also be given the caller's own deadline, which raises
    :class:`DeadlinePassed` instead.
    """

    def __init__(self, host: str, port: int) -> None:
        if not 0 < port < 65536:
            raise StreamError(StreamFailure.CONNECTION_FAILED)
        self.host = host
        self.port = port
        self._socket: Optional[socket.socket] = None
        self._buffer = bytearray()
        self._deadline: Optional[float] = None
        self._closed = False

    def __enter__(self) -> "FramedStream":
        return self

    def __exit__(self, *_: object) -> None:
        self.close()

    def connect(self, timeout: float, call_deadline: Optional[float] = None) -> None:
        """Waits until the connection is usable, or fails."""
        check_deadline(call_deadline)
        limit = timeout
        if call_deadline is not None:
            limit = min(limit, max(0.0, call_deadline - time.monotonic()))
        try:
            connected = socket.create_connection((self.host, self.port), timeout=max(limit, 0.001))
        except socket.timeout:
            check_deadline(call_deadline)
            raise StreamError(StreamFailure.TIMED_OUT) from None
        except (OSError, ValueError, UnicodeError):
            raise StreamError(StreamFailure.CONNECTION_FAILED) from None
        connected.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        if self._closed:
            connected.close()
            raise StreamError(StreamFailure.CLOSED)
        self._socket = connected

    def arm(self, seconds: float) -> None:
        self._deadline = time.monotonic() + max(0.0, seconds)

    def disarm(self) -> None:
        self._deadline = None

    def close(self) -> None:
        self._closed = True
        if self._socket is not None:
            try:
                self._socket.close()
            except OSError:
                pass

    @property
    def remote_address(self) -> Optional[Tuple[str, int]]:
        """The peer's address, for remembering where a receiver was found."""
        if self._socket is None:
            return None
        try:
            peer = self._socket.getpeername()
        except OSError:
            return None
        return address_text(peer[0]), int(peer[1])

    def _timeout(self, call_deadline: Optional[float]) -> Optional[float]:
        """How long the next socket operation may block, and raises if a deadline has already passed."""
        now = time.monotonic()
        candidates = []
        if self._deadline is not None:
            if now >= self._deadline:
                self.close()
                raise StreamError(StreamFailure.TIMED_OUT)
            candidates.append(self._deadline - now)
        if call_deadline is not None:
            if now >= call_deadline:
                self.close()
                raise DeadlinePassed()
            candidates.append(call_deadline - now)
        return min(candidates) if candidates else None

    def _require(self) -> socket.socket:
        if self._socket is None or self._closed:
            raise StreamError(StreamFailure.CLOSED)
        return self._socket

    def _timed_out(self, call_deadline: Optional[float]) -> Exception:
        self.close()
        if call_deadline is not None and time.monotonic() >= call_deadline:
            return DeadlinePassed()
        return StreamError(StreamFailure.TIMED_OUT)

    def send(self, data: bytes, call_deadline: Optional[float] = None) -> None:
        connection = self._require()
        view = memoryview(bytes(data))
        while view:
            connection.settimeout(self._timeout(call_deadline))
            try:
                sent = connection.send(view)
            except socket.timeout:
                raise self._timed_out(call_deadline) from None
            except OSError:
                self.close()
                raise StreamError(StreamFailure.CLOSED) from None
            if sent <= 0:
                self.close()
                raise StreamError(StreamFailure.CLOSED)
            view = view[sent:]

    def send_frame(self, body: bytes, call_deadline: Optional[float] = None) -> None:
        self.send(frame(body), call_deadline)

    def read_exactly(self, count: int, call_deadline: Optional[float] = None) -> bytes:
        connection = self._require()
        while len(self._buffer) < count:
            connection.settimeout(self._timeout(call_deadline))
            try:
                received = connection.recv(max(4096, count - len(self._buffer)))
            except socket.timeout:
                raise self._timed_out(call_deadline) from None
            except OSError:
                self.close()
                raise StreamError(StreamFailure.CLOSED) from None
            if not received:
                self.close()
                raise StreamError(StreamFailure.CLOSED)
            self._buffer += received
        head = bytes(self._buffer[:count])
        del self._buffer[:count]
        return head

    def read_frame(self, max_length: int = protocol.MAX_FRAME_LENGTH, call_deadline: Optional[float] = None) -> bytes:
        """Reads one frame and returns its body. Refuses a frame longer than ``max_length`` before reading it."""
        header = self.read_exactly(2, call_deadline)
        length = int.from_bytes(header, "big")
        if length > max_length:
            self.close()
            raise StreamError(StreamFailure.FRAME_TOO_LARGE)
        return self.read_exactly(length, call_deadline)
