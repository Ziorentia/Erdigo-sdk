"""Writes items to one paired receiver."""

from __future__ import annotations

import hashlib
import math
import secrets
import threading
import time
from typing import Callable, List, Optional, Sequence, Tuple

from . import discovery, messages, noise, protocol
from .discovery import DISCOVER, Endpoint, ReceiverTarget
from .errors import ErdigoError, ErrorKind
from .identity import DeviceIdentity, ReceiverPairing
from .messages import (
    Ack,
    Delete,
    DeleteAll,
    Hello,
    Move,
    PhotoBegin,
    PhotoChunk,
    PhotoEnd,
    PhotoHeader,
    Ping,
    PutMarker,
    PutNote,
    PutPin,
    ReceiverError,
    Request,
    RequestEnvelope,
    TrackAppend,
    TrackFinish,
    Welcome,
)
from .model import (
    LIMITS_V1,
    Coordinate,
    ItemKind,
    Limits,
    MarkerColor,
    PhotoType,
    TokenBucket,
    TrackPoint,
    validate_item_id,
    validate_note,
    validate_title,
)
from .pairing import SDK_NAME
from .protocol import ErrorCode, WireError
from .store import DeviceStore
from .stream import DeadlinePassed, FramedStream, StreamError, check_deadline


class RetryPolicy:
    """How long a request keeps trying while the receiver cannot be reached, for example while the computer is asleep
    or the app is closed.

    The time allowed covers one outage: it counts from when the connection was lost, and starts again once a connection
    is made. Time spent connected, such as waiting to stay within the receiver's limits, does not count. To limit the
    whole time a request takes, pass ``timeout=`` to the request.
    """

    __slots__ = ("seconds",)

    def __init__(self, seconds: Optional[float]) -> None:
        self.seconds = seconds

    @classmethod
    def up_to(cls, seconds: float) -> "RetryPolicy":
        """Keep trying for this long, counted from when the connection was lost."""
        if not isinstance(seconds, (int, float)) or isinstance(seconds, bool) or not seconds >= 0 or math.isinf(seconds):
            raise ErdigoError(ErrorKind.INVALID_ARGUMENT, "the retry time is a number of seconds")
        return cls(float(seconds))

    def __repr__(self) -> str:
        if self is RetryPolicy.NONE:
            return "RetryPolicy.NONE"
        if self is RetryPolicy.FOREVER:
            return "RetryPolicy.FOREVER"
        return f"RetryPolicy.up_to({self.seconds})"


#: Fail at the first connection problem.
RetryPolicy.NONE = RetryPolicy(0.0)  # type: ignore[attr-defined]
#: Keep trying until the request's own ``timeout`` runs out, if it has one.
RetryPolicy.FOREVER = RetryPolicy(None)  # type: ignore[attr-defined]


class _ConnectionLost(Exception):
    """A problem with the connection, as opposed to an answer from the receiver."""


class _Session:
    def __init__(self, stream: FramedStream, transport: noise.Transport, welcome: Welcome) -> None:
        self.stream = stream
        self.transport = transport
        self.welcome = welcome
        self.last_seq = welcome.last_seq


class _FifoLock:
    """A lock that admits waiters strictly in the order they arrived, and lets a waiter give up at its deadline."""

    def __init__(self) -> None:
        self._condition = threading.Condition()
        self._next_ticket = 0
        self._serving = 0
        self._abandoned: set = set()

    def acquire(self, deadline: Optional[float]) -> None:
        with self._condition:
            ticket = self._next_ticket
            self._next_ticket += 1
            while self._serving != ticket:
                remaining = None if deadline is None else deadline - time.monotonic()
                if remaining is not None and remaining <= 0:
                    # Its place is freed at once, and the ones behind it go ahead.
                    self._abandoned.add(ticket)
                    raise DeadlinePassed()
                self._condition.wait(remaining)

    def release(self) -> None:
        with self._condition:
            self._serving += 1
            while self._serving in self._abandoned:
                self._abandoned.discard(self._serving)
                self._serving += 1
            self._condition.notify_all()


#: How many ``rate_limited`` answers one request will accept before giving up. An honest receiver needs only one or
#: two, so this only stops a receiver that never relents.
MAX_RATE_LIMITED_ANSWERS = 20
_MINIMUM_RATE_LIMIT_WAIT = 0.05


def _deadline(timeout: Optional[float]) -> Optional[float]:
    if timeout is None:
        return None
    if isinstance(timeout, bool) or not isinstance(timeout, (int, float)) or not timeout >= 0:
        raise ErdigoError(ErrorKind.INVALID_ARGUMENT, "timeout is a number of seconds")
    return time.monotonic() + timeout


def _sleep(seconds: float, deadline: Optional[float]) -> None:
    """Waits as long as asked, up to the longest wait a receiver may ask for, or until the deadline."""
    seconds = max(0.0, min(seconds, 3600.0))
    if deadline is not None:
        remaining = deadline - time.monotonic()
        if remaining < seconds:
            time.sleep(max(0.0, remaining))
            raise DeadlinePassed()
    time.sleep(seconds)


def backoff(attempt: int) -> float:
    """1, 2, 4 ... up to 60 seconds, with 20 percent jitter so several devices do not retry in step."""
    base = min(60.0, 2.0 ** min(attempt, 6))
    jitter = 0.8 + 0.4 * (secrets.randbelow(1_000_001) / 1_000_000.0)
    return base * jitter


class ErdigoClient:
    """Writes items to one paired receiver.

    Requests run strictly one at a time, in the order they were made, and each returns once the receiver has
    acknowledged it. If the connection breaks, the request is sent again with the same sequence number, so the
    receiver applies it at most once. Use the client as a context manager, or call :meth:`close`, to close its
    connection.

    Every request takes an optional ``timeout`` in seconds for the whole request, including waiting its turn. When it
    runs out the request raises :class:`TimeoutError`. A request that times out after it was sent may already have been
    applied; see the README.
    """

    def __init__(
        self,
        identity: DeviceIdentity,
        pairing: ReceiverPairing,
        store: DeviceStore,
        target: ReceiverTarget = DISCOVER,
        retry: Optional[RetryPolicy] = None,
        connect_timeout: float = 5.0,
        ack_timeout: float = 15.0,
    ) -> None:
        """``retry`` is how long each outage may last before a request fails with :attr:`ErrorKind.NO_ANSWER`, 30
        seconds by default. ``connect_timeout`` limits one attempt to connect and ``ack_timeout`` the wait for one
        answer."""
        self._identity = identity
        self._pairing = pairing
        self._store = store
        self._target = target
        self._retry = retry if retry is not None else RetryPolicy.up_to(30)
        self._connect_timeout = connect_timeout
        self._ack_timeout = ack_timeout
        self._gate = _FifoLock()
        self._lock = threading.Lock()
        self._session: Optional[_Session] = None
        self._last_session_at = -math.inf
        # These outlive a connection, because the receiver's own count does too.
        self._paced_limits: Optional[Limits] = None
        self._message_bucket: Optional[TokenBucket] = None
        self._point_bucket: Optional[TokenBucket] = None

    def __enter__(self) -> "ErdigoClient":
        return self

    def __exit__(self, *_: object) -> None:
        self.close()

    # Public requests

    def connect(self, timeout: Optional[float] = None) -> None:
        """Connects now rather than at the first request."""
        deadline = _deadline(timeout)
        self._serialised(lambda: self._retrying(lambda: self._current_session(deadline), deadline), deadline)

    def close(self) -> None:
        """Closes the connection. A later request connects again."""
        with self._lock:
            session, self._session = self._session, None
        if session is not None:
            session.stream.close()
            session.transport.close()

    @property
    def limits(self) -> Optional[Limits]:
        """The limits the receiver announced on the current connection."""
        with self._lock:
            return self._session.welcome.limits if self._session else None

    @property
    def allowed_kinds(self) -> Optional[Tuple[ItemKind, ...]]:
        """The item kinds the person allowed, as announced on the current connection."""
        with self._lock:
            return self._session.welcome.kinds if self._session else None

    def ping(self, timeout: Optional[float] = None) -> None:
        self._perform(Ping(), None, 0, _deadline(timeout))

    def put_pin(self, id: str, at: Coordinate, title: Optional[str] = None, timeout: Optional[float] = None) -> None:
        """Creates the pin, or replaces it if this device already has one with this id."""
        item_id = _validated_id(id)
        _validate_title(title)
        self._perform(PutPin(item_id, _coordinate(at), title), ItemKind.PIN, 0, _deadline(timeout))

    def put_marker(
        self, id: str, at: Coordinate, title: Optional[str] = None, color: Optional[MarkerColor] = None, timeout: Optional[float] = None
    ) -> None:
        item_id = _validated_id(id)
        _validate_title(title)
        if color is not None:
            try:
                color = MarkerColor(color)
            except ValueError:
                raise ErdigoError(ErrorKind.INVALID_ARGUMENT, "unknown marker color") from None
        self._perform(PutMarker(item_id, _coordinate(at), title, color), ItemKind.MARKER, 0, _deadline(timeout))

    def put_note(self, id: str, at: Coordinate, text: str, timeout: Optional[float] = None) -> None:
        """A note is plain text. The receiver shows links as text and never opens them."""
        item_id = _validated_id(id)
        _validated(lambda: validate_note(text))
        self._perform(PutNote(item_id, _coordinate(at), text), ItemKind.NOTE, 0, _deadline(timeout))

    def move(self, id: str, to: Coordinate, timeout: Optional[float] = None) -> None:
        self._perform(Move(_validated_id(id), _coordinate(to)), None, 0, _deadline(timeout))

    def delete(self, id: str, timeout: Optional[float] = None) -> None:
        """Deletes one of this device's items. Deleting an id that does not exist succeeds."""
        self._perform(Delete(_validated_id(id)), None, 0, _deadline(timeout))

    def delete_all(self, timeout: Optional[float] = None) -> None:
        """Deletes every item this device created on this receiver, and nothing else."""
        self._perform(DeleteAll(), None, 0, _deadline(timeout))

    def append_track(self, id: str, points: Sequence[TrackPoint], title: Optional[str] = None, timeout: Optional[float] = None) -> None:
        """Appends points to a track, creating it if needed. Long lists are split into several messages, and no other
        request runs in between."""
        item_id = _validated_id(id)
        _validate_title(title)
        batch_all = list(points)
        if not batch_all:
            raise ErdigoError(ErrorKind.INVALID_ARGUMENT, "a track append needs at least one point")
        if not all(isinstance(point, TrackPoint) for point in batch_all):
            raise ErdigoError(ErrorKind.INVALID_ARGUMENT, "track points are TrackPoint values")
        deadline = _deadline(timeout)

        def body() -> None:
            pending_title = title
            for start in range(0, len(batch_all), protocol.MAX_POINTS_PER_MESSAGE):
                batch = tuple(batch_all[start : start + protocol.MAX_POINTS_PER_MESSAGE])
                self._perform_holding_lock(TrackAppend(item_id, pending_title, batch), ItemKind.TRACK, len(batch), deadline)
                pending_title = None

        self._serialised(body, deadline)

    def finish_track(self, id: str, timeout: Optional[float] = None) -> None:
        self._perform(TrackFinish(_validated_id(id)), ItemKind.TRACK, 0, _deadline(timeout))

    def put_photo(self, id: str, at: Coordinate, data: bytes, title: Optional[str] = None, timeout: Optional[float] = None) -> None:
        """Sends a JPEG or PNG photo. The format is detected from the data, and the receiver removes metadata such as
        EXIF location when it stores the photo."""
        item_id = _validated_id(id)
        _validate_title(title)
        at = _coordinate(at)
        data = bytes(data)
        photo_type = PhotoType.detect(data)
        if photo_type is None:
            raise ErdigoError(ErrorKind.INVALID_ARGUMENT, "a photo must be JPEG or PNG data")
        if len(data) > LIMITS_V1.photo_bytes:
            raise ErdigoError(ErrorKind.INVALID_ARGUMENT, f"a photo is at most {LIMITS_V1.photo_bytes} bytes")
        chunk_size = protocol.MAX_CHUNK_BYTES
        chunk_count = -(-len(data) // chunk_size)
        header = PhotoHeader(item_id, at, title, photo_type, len(data), hashlib.sha256(data).hexdigest(), chunk_count)
        deadline = _deadline(timeout)

        def upload() -> None:
            # The receiver drops a partial upload when the connection ends, so restart from the beginning.
            current = self._current_session(deadline)
            if ItemKind.PHOTO not in current.welcome.kinds:
                raise ErdigoError(ErrorKind.NOT_PERMITTED, item_kind=ItemKind.PHOTO)
            self._exchange(PhotoBegin(header), current.last_seq + 1, current, deadline)
            for index in range(chunk_count):
                piece = data[index * chunk_size : (index + 1) * chunk_size]
                self._exchange(PhotoChunk(item_id, index, piece), current.last_seq + 1, current, deadline)
            self._exchange(PhotoEnd(item_id), current.last_seq + 1, current, deadline)

        self._serialised(lambda: self._retrying(upload, deadline), deadline)

    # Ordering

    def _serialised(self, body: Callable[[], object], deadline: Optional[float]) -> None:
        self._gate.acquire(deadline)
        try:
            body()
        finally:
            self._gate.release()

    # Requests

    def _perform(self, request: Request, kind: Optional[ItemKind], points: int, deadline: Optional[float]) -> None:
        self._serialised(lambda: self._perform_holding_lock(request, kind, points, deadline), deadline)

    def _perform_holding_lock(self, request: Request, kind: Optional[ItemKind], points: int, deadline: Optional[float]) -> None:
        assigned: List[int] = []
        # A resend is the same request, so it does not wait for its allowance a second time.
        points_paid = [False]

        def attempt() -> None:
            current = self._current_session(deadline)
            if kind is not None and kind not in current.welcome.kinds:
                raise ErdigoError(ErrorKind.NOT_PERMITTED, item_kind=kind)
            # A request keeps its number across reconnections, so the receiver can recognise a resend.
            if assigned and current.last_seq > assigned[0]:
                raise ErdigoError(ErrorKind.SEQUENCE_CONFLICT, last_seq=current.last_seq)
            if not points_paid[0]:
                self._pace(lambda now: self._point_bucket.take(now, points) if self._point_bucket and points > 0 else None, deadline)
                points_paid[0] = True
            seq = assigned[0] if assigned else current.last_seq + 1
            if not assigned:
                assigned.append(seq)
            self._exchange(request, seq, current, deadline)

        self._retrying(attempt, deadline)

    def _pace(self, take: Callable[[float], Optional[float]], deadline: Optional[float]) -> None:
        while True:
            with self._lock:
                wait = take(time.monotonic())
            if wait is None:
                return
            _sleep(wait, deadline)

    def _exchange(self, request: Request, seq: int, current: _Session, deadline: Optional[float]) -> None:
        """Sends one request and waits for its answer, honouring the receiver's rate limit."""
        if seq > protocol.MAX_SEQUENCE:
            raise ErdigoError(
                ErrorKind.INVALID_ARGUMENT, "the sequence number is exhausted; remove this device on the receiver and pair it again"
            )
        plaintext = messages.encode_request(RequestEnvelope(seq, request))
        rate_limited_answers = 0
        while True:
            self._pace(lambda now: self._message_bucket.take(now) if self._message_bucket else None, deadline)
            try:
                sealed = current.transport.seal(plaintext)
                current.stream.arm(self._ack_timeout)
                current.stream.send_frame(sealed, deadline)
                answer_frame = current.stream.read_frame(call_deadline=deadline)
                current.stream.disarm()
                answer = messages.decode_receiver_message(current.transport.open(answer_frame))
            except WireError as error:
                self._drop_session()
                raise ErdigoError(ErrorKind.PROTOCOL_VIOLATION, error.detail) from error
            except DeadlinePassed:
                self._drop_session()
                raise
            except (StreamError, noise.NoiseError, OSError):
                self._drop_session()
                raise _ConnectionLost() from None

            if isinstance(answer, Ack):
                if answer.seq != seq:
                    self._drop_session()
                    raise ErdigoError(ErrorKind.PROTOCOL_VIOLATION, "the acknowledgement has the wrong sequence number")
                current.last_seq = seq
                return
            if isinstance(answer, ReceiverError):
                ends_connection = _ends_connection(answer)
                # An answer about one request must be about this one. An answer that ends the connection is about the
                # connection, so it ends it whatever number it carries.
                answers_this_request = answer.seq is None or answer.seq == seq
                if not ends_connection and not answers_this_request:
                    self._drop_session()
                    raise ErdigoError(ErrorKind.PROTOCOL_VIOLATION, "the error answers a different sequence number")
                # Only an answer the receiver will keep the connection open after is worth waiting for.
                if not ends_connection and answer.code == ErrorCode.RATE_LIMITED.value:
                    rate_limited_answers += 1
                    if rate_limited_answers > MAX_RATE_LIMITED_ANSWERS:
                        self._drop_session()
                        raise ErdigoError(ErrorKind.REJECTED, receiver_error=answer)
                    wait = (answer.retry_after_ms if answer.retry_after_ms is not None else 1000) / 1000.0
                    _sleep(max(wait, _MINIMUM_RATE_LIMIT_WAIT), deadline)
                    continue
                self._handle(answer, seq, answers_this_request, ends_connection, current)
            self._drop_session()
            raise ErdigoError(ErrorKind.PROTOCOL_VIOLATION, "unexpected message in answer to a request")

    def _handle(self, error: ReceiverError, seq: int, answers_this_request: bool, ends_connection: bool, current: _Session) -> None:
        """Turns an error answer into the error the caller sees. It always raises."""
        # A number from an answer to some other request says nothing about where this connection stands.
        if answers_this_request and error.last_seq is not None:
            current.last_seq = max(current.last_seq, error.last_seq)
        if ends_connection:
            self._drop_session()
        if error.code == ErrorCode.PAUSED.value:
            raise _ConnectionLost()
        if error.code == ErrorCode.REPLACED.value:
            raise ErdigoError(ErrorKind.REPLACED, receiver_error=error)
        if error.code in (ErrorCode.STALE_SEQ.value, ErrorCode.SEQ_CONFLICT.value):
            raise ErdigoError(ErrorKind.SEQUENCE_CONFLICT, receiver_error=error, last_seq=current.last_seq)
        code = ErrorCode.from_wire(error.code)
        if not ends_connection and error.seq == seq and code is not None and not code.is_retryable:
            # The receiver used up this number on the failed request.
            current.last_seq = seq
        raise ErdigoError(ErrorKind.REJECTED, receiver_error=error)

    # Connection

    def _drop_session(self) -> None:
        self.close()

    def _current_session(self, deadline: Optional[float]) -> _Session:
        with self._lock:
            if self._session is not None:
                return self._session
            remembered = self._pairing
        # The remembered address comes first, because looking around the network takes a moment.
        created = self._first_answer(discovery.known_endpoints(self._target, remembered.last_host, remembered.last_port), deadline)
        if created is not None:
            return created
        if self._target.is_discover:
            found = self._first_answer(discovery.browse(deadline=deadline), deadline)
            if found is not None:
                return found
        raise _ConnectionLost()

    def _first_answer(self, endpoints: List[Endpoint], deadline: Optional[float]) -> Optional[_Session]:
        """Tries each endpoint in turn. A receiver that stays silent is simply not the right one."""
        for endpoint in endpoints:
            try:
                created = self._handshake(endpoint, deadline)
            except _ConnectionLost:
                continue
            with self._lock:
                self._session = created
                self._last_session_at = time.monotonic()
                self._prepare_pacing(created.welcome.limits)
            self._remember_address(created.stream)
            return created
        return None

    def _prepare_pacing(self, limits: Limits) -> None:
        if self._paced_limits == limits:
            return
        self._paced_limits = limits
        now = time.monotonic()
        self._message_bucket = TokenBucket(limits.msg_burst, limits.msgs_per_s, now)
        self._point_bucket = TokenBucket(limits.point_burst, limits.points_per_s, now)

    def _remember_address(self, stream: FramedStream) -> None:
        address = stream.remote_address
        if address is None:
            return
        with self._lock:
            if self._pairing.last_host == address[0] and self._pairing.last_port == address[1]:
                return
            self._pairing = self._pairing.with_address(address[0], address[1])
            fingerprint = self._pairing.fingerprint
        self._store.update_address(fingerprint, address[0], address[1])

    def _handshake(self, endpoint: Endpoint, deadline: Optional[float]) -> _Session:
        stream = FramedStream(endpoint.host, endpoint.port)
        transport: Optional[noise.Transport] = None
        try:
            stream.connect(self._connect_timeout, deadline)
            prologue = protocol.preamble(protocol.Mode.CONNECT)
            with self._lock:
                remembered = self._pairing
            handshake = noise.Handshake(noise.IK, True, prologue, self._identity.private_key, remembered.receiver_key)
            # Written to disk first: the receiver ignores a counter it has already seen.
            counter = self._store.next_connect_counter(remembered.fingerprint, int(time.time() * 1000))
            if counter > protocol.MAX_SEQUENCE:
                raise ErdigoError(
                    ErrorKind.STORAGE, "this device key has used up its connection counter. Create a new key and pair again."
                )
            first = handshake.write_message(counter.to_bytes(8, "big"))
            stream.arm(self._connect_timeout)
            stream.send(prologue + len(first).to_bytes(2, "big") + first, deadline)
            second = stream.read_frame(protocol.MAX_HANDSHAKE_FRAME_LENGTH, deadline)
            if handshake.read_message(second):
                raise ErdigoError(ErrorKind.PROTOCOL_VIOLATION, "the handshake reply has an unexpected payload")
            transport = handshake.split()
            stream.send_frame(transport.seal(messages.encode_hello(Hello((protocol.VERSION,), SDK_NAME))), deadline)
            answer_frame = stream.read_frame(call_deadline=deadline)
            stream.disarm()
            answer = messages.decode_receiver_message(transport.open(answer_frame))
            if isinstance(answer, Welcome):
                if answer.version != protocol.VERSION:
                    raise ErdigoError(ErrorKind.PROTOCOL_VIOLATION, "the receiver chose a version that was not offered")
                return _Session(stream, transport, answer)
            if isinstance(answer, ReceiverError):
                if answer.code == ErrorCode.VERSION_UNSUPPORTED.value:
                    raise ErdigoError(ErrorKind.VERSION_UNSUPPORTED, receiver_error=answer, supported=answer.supported or ())
                if answer.code == ErrorCode.PAUSED.value:
                    raise _ConnectionLost()
                raise ErdigoError(ErrorKind.REJECTED, receiver_error=answer)
            raise ErdigoError(ErrorKind.PROTOCOL_VIOLATION, "the receiver did not send welcome")
        except BaseException as error:
            stream.close()
            if transport is not None:
                transport.close()
            if isinstance(error, (ErdigoError, _ConnectionLost, DeadlinePassed)) or not isinstance(error, Exception):
                raise
            if isinstance(error, WireError):
                raise ErdigoError(ErrorKind.PROTOCOL_VIOLATION, error.detail) from error
            if isinstance(error, (StreamError, noise.NoiseError, OSError)):
                raise _ConnectionLost() from None
            raise

    def _retrying(self, body: Callable[[], object], deadline: Optional[float]) -> None:
        """Runs ``body`` again after connection problems until the retry policy runs out. The time allowed counts from
        the first failure, so a long paced wait does not use it up."""
        outage_start: Optional[float] = None
        attempt = 0
        while True:
            try:
                body()
                return
            except _ConnectionLost:
                check_deadline(deadline)
                now = time.monotonic()
                with self._lock:
                    connected_at = self._last_session_at
                # Having connected since then, this is a new outage.
                if outage_start is not None and connected_at > outage_start:
                    outage_start = None
                    attempt = 0
                start = outage_start if outage_start is not None else now
                outage_start = start
                delay = backoff(attempt)
                attempt += 1
                if self._retry.seconds is not None:
                    # Use all the time allowed, with a last try at the end of it.
                    remaining = self._retry.seconds - (now - start)
                    if remaining <= 0:
                        raise ErdigoError(ErrorKind.NO_ANSWER) from None
                    delay = min(delay, remaining)
                _sleep(delay, deadline)


def _ends_connection(error: ReceiverError) -> bool:
    """Whether the receiver closes the connection after this answer. ``paused`` and ``replaced`` always do, even if a
    receiver leaves out the flag."""
    return error.fatal or error.code in (ErrorCode.PAUSED.value, ErrorCode.REPLACED.value)


def _validated(body: Callable[[], object]) -> object:
    try:
        return body()
    except WireError as error:
        raise ErdigoError(ErrorKind.INVALID_ARGUMENT, error.detail) from None


def _validated_id(item_id: str) -> str:
    return _validated(lambda: validate_item_id(item_id))  # type: ignore[return-value]


def _validate_title(title: Optional[str]) -> None:
    if title is not None:
        _validated(lambda: validate_title(title))


def _coordinate(value: Coordinate) -> Coordinate:
    if not isinstance(value, Coordinate):
        raise ErdigoError(ErrorKind.INVALID_ARGUMENT, "a position is a Coordinate(latitude, longitude)")
    return value
