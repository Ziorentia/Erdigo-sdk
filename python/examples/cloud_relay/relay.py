#!/usr/bin/env python3
"""An example cloud relay for Erdigo. Devices POST operations to it, and one gateway fetches them.

It speaks the relay API in docs/cloud-relay.md and keeps its queue in memory. It uses only the Python standard
library (3.10 or later), so this one file can be copied to a server and run on its own. It speaks plain HTTP: put an
HTTPS reverse proxy in front of it.

    ERDIGO_RELAY_SOURCE_TOKENS=... ERDIGO_RELAY_GATEWAY_TOKEN=... python3 relay.py [--listen HOST:PORT]
"""

from __future__ import annotations

import base64
import collections
import hashlib
import hmac
import ipaddress
import json
import os
import re
import secrets
import signal
import socket
import socketserver
import sys
import threading
import time
from dataclasses import dataclass
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Callable, Dict, List, Mapping, Optional, Sequence, Tuple
from urllib.parse import parse_qsl

USAGE = """\
relay: holds operations that devices send over HTTPS until an Erdigo gateway fetches them.

Usage
  relay.py [--listen HOST:PORT] [--keep-hours HOURS] [--behind-proxy]

  --listen HOST:PORT  Where to accept plain HTTP, 127.0.0.1:8080 by default. Port 0 picks a free port.
                      Write an IPv6 address as [::1]:PORT. Only a loopback address is allowed unless
                      --behind-proxy is given.
  --keep-hours HOURS  How long an operation waits for the gateway before it is dropped, 1 to 168 (24).
  --behind-proxy      HTTPS ends in a proxy in front of the relay, so it may listen on any address.

Environment
  ERDIGO_RELAY_SOURCE_TOKENS  One or more source tokens, separated by commas.
  ERDIGO_RELAY_GATEWAY_TOKEN  The gateway token. It must differ from every source token.
  A token is 32 to 256 characters from A-Z, a-z, 0-9, '-', '_', '.' and '~'.

It stops on Ctrl+C or SIGTERM. See docs/cloud-relay.md.
"""

MIB = 1024 * 1024
MAX_SAFE_INTEGER = 2**53 - 1

TOKEN = re.compile(r"[A-Za-z0-9._~-]{32,256}")
EPOCH = re.compile(r"[A-Za-z0-9_-]{16,64}")
ITEM_ID = re.compile(r"[A-Za-z0-9_-]{1,64}")
BASE64 = re.compile(r"[A-Za-z0-9+/]*={0,2}")
WHOLE = re.compile(r"[0-9]{1,16}")
# Spec section 6.3. A note may hold line feeds and tabs; a title may not.
TITLE_FORBIDDEN = re.compile(r"[\x00-\x1f\x7f-\x9f\u061c\u200e\u200f\u202a-\u202e\u2066-\u2069\u2028\u2029\ufeff]")
TEXT_FORBIDDEN = re.compile(r"[\x00-\x08\x0b-\x1f\x7f-\x9f\u061c\u200e\u200f\u202a-\u202e\u2066-\u2069\u2028\u2029\ufeff]")

COLORS = frozenset({"red", "orange", "yellow", "green", "blue", "purple", "gray"})
PHOTO_SIGNATURES = {"image/jpeg": b"\xff\xd8\xff", "image/png": b"\x89PNG\r\n\x1a\n"}
MAX_PHOTO_BYTES = 1048576
MAX_TEXT_BYTES = 4096
MAX_TITLE_CHARACTERS = 200
MAX_POINTS = 100

#: For each operation, its required fields and its optional ones.
OPERATIONS: Dict[str, Tuple[Tuple[str, ...], Tuple[str, ...]]] = {
    "pin": (("id", "lat", "lon"), ("title",)),
    "marker": (("id", "lat", "lon"), ("title", "color")),
    "note": (("id", "lat", "lon", "text"), ()),
    "photo": (("id", "lat", "lon", "type", "data"), ("title",)),
    "track": (("id", "points"), ("title",)),
    "finish_track": (("id",), ()),
    "move": (("id", "lat", "lon"), ()),
    "delete": (("id",), ()),
}
KNOWN_FIELDS = frozenset(name for required, optional in OPERATIONS.values() for name in required + optional)

ROUTES = {"/v1/ops": ("GET", "POST"), "/v1/health": ("GET",)}


@dataclass(frozen=True)
class Limits:
    """The limits of docs/cloud-relay.md. Tests make them smaller."""

    body_bytes: int = 2 * MIB
    ops_per_post: int = 100
    queue_ops: int = 10000
    queue_bytes: int = 64 * MIB
    page_ops: int = 100
    page_bytes: int = 4 * MIB
    max_wait: int = 30


class Clock:
    """Real time. Tests pass a clock they can move forward."""

    def monotonic(self) -> float:
        return time.monotonic()

    def wall_ms(self) -> int:
        return int(time.time() * 1000)


class UsageError(Exception):
    """A mistake in the command line or the environment. Its message never repeats a token."""


# Reading a request body


class InvalidJson(ValueError):
    pass


class InvalidBody(ValueError):
    pass


class InvalidOp(ValueError):
    def __init__(self, reason: str, index: int = 0) -> None:
        super().__init__(reason)
        self.reason = reason
        self.index = index


def _no_duplicates(pairs: List[Tuple[str, object]]) -> dict:
    result: dict = {}
    for key, value in pairs:
        if key in result:
            raise InvalidJson("duplicate key")
        result[key] = value
    return result


def _finite(text: str) -> float:
    value = float(text)
    if value in (float("inf"), float("-inf")):
        raise InvalidJson("number out of range")
    return value


def _no_constants(_: str) -> object:
    raise InvalidJson("NaN and Infinity are not JSON")


def parse_json(data: bytes) -> object:
    """One JSON value in UTF-8, refusing what ordinary parsers let through: a byte order mark, invalid UTF-8, a
    duplicate key, a number too large to be finite and an unpaired surrogate escape."""
    try:
        text = data.decode("utf-8")
    except UnicodeDecodeError:
        raise InvalidJson("invalid UTF-8") from None
    if text.startswith("\ufeff"):
        raise InvalidJson("byte order mark")
    try:
        value = json.loads(text, object_pairs_hook=_no_duplicates, parse_float=_finite, parse_constant=_no_constants)
    except (ValueError, RecursionError):
        raise InvalidJson("not JSON") from None
    pending = [value]
    while pending:
        item = pending.pop()
        if isinstance(item, str):
            try:
                item.encode("utf-8")
            except UnicodeEncodeError:
                raise InvalidJson("unpaired surrogate") from None
        elif isinstance(item, dict):
            pending.extend(item.keys())
            pending.extend(item.values())
        elif isinstance(item, list):
            pending.extend(item)
    return value


def _is_number(value: object) -> bool:
    return isinstance(value, (int, float)) and not isinstance(value, bool)


def _check_range(value: object, name: str, low: float, high: float) -> None:
    if not _is_number(value):
        raise InvalidOp(f"'{name}' must be a number")
    if not low <= value <= high:  # type: ignore[operator]
        raise InvalidOp(f"'{name}' must be from {low:g} to {high:g}")


def _check_id(value: object) -> None:
    if not isinstance(value, str) or not ITEM_ID.fullmatch(value):
        raise InvalidOp("'id' must be 1 to 64 ASCII letters, digits, '-' or '_'")


def _check_title(value: object) -> None:
    if not isinstance(value, str) or not 1 <= len(value) <= MAX_TITLE_CHARACTERS:
        raise InvalidOp("'title' must be 1 to 200 characters")
    if TITLE_FORBIDDEN.search(value):
        raise InvalidOp("'title' must not hold control characters or invisible direction marks")


def _check_text(value: object) -> None:
    if not isinstance(value, str) or not 1 <= len(value.encode("utf-8")) <= MAX_TEXT_BYTES:
        raise InvalidOp("'text' must be 1 to 4096 bytes of UTF-8")
    if TEXT_FORBIDDEN.search(value):
        raise InvalidOp("'text' must not hold carriage returns, other control characters or invisible direction marks")


def _check_photo(kind: object, data: object) -> None:
    if kind not in PHOTO_SIGNATURES:
        raise InvalidOp("'type' must be image/jpeg or image/png")
    if not isinstance(data, str) or len(data) % 4 or not BASE64.fullmatch(data):
        raise InvalidOp("'data' must be standard base64 with padding and no line breaks")
    try:
        decoded = base64.b64decode(data, validate=True)
    except ValueError:
        raise InvalidOp("'data' must be standard base64 with padding and no line breaks") from None
    if base64.b64encode(decoded).decode("ascii") != data:
        raise InvalidOp("'data' must be base64 exactly as a standard encoder writes it")
    if not 1 <= len(decoded) <= MAX_PHOTO_BYTES:
        raise InvalidOp("a photo must be 1 to 1048576 bytes")
    if not decoded.startswith(PHOTO_SIGNATURES[kind]):  # type: ignore[index]
        raise InvalidOp("the photo's first bytes do not match its 'type'")


def _check_points(points: object) -> None:
    if not isinstance(points, list) or not 1 <= len(points) <= MAX_POINTS:
        raise InvalidOp("'points' must be a list of 1 to 100 points")
    for point in points:
        if not isinstance(point, dict):
            raise InvalidOp("a point must be an object")
        for name in point:
            if name not in ("lat", "lon", "ts", "alt"):
                raise InvalidOp("a point has only 'lat', 'lon', 'ts' and 'alt'")
        if "lat" not in point or "lon" not in point:
            raise InvalidOp("a point needs 'lat' and 'lon'")
        _check_range(point["lat"], "lat", -90, 90)
        _check_range(point["lon"], "lon", -180, 180)
        if "ts" in point:
            timestamp = point["ts"]
            if isinstance(timestamp, bool) or not isinstance(timestamp, int) or not 0 <= timestamp <= MAX_SAFE_INTEGER:
                raise InvalidOp("'ts' must be a whole number of milliseconds from 0 to 2^53-1")
        if "alt" in point:
            _check_range(point["alt"], "alt", -1000, 100000)


def check_op(op: object) -> None:
    """Raises InvalidOp unless ``op`` is an operation of docs/cloud-relay.md."""
    if not isinstance(op, dict):
        raise InvalidOp("an operation must be an object")
    kind = op.get("op")
    if not isinstance(kind, str) or kind not in OPERATIONS:
        raise InvalidOp("'op' must be pin, marker, note, photo, track, finish_track, move or delete")
    required, optional = OPERATIONS[kind]
    for name in op:
        if name != "op" and name not in required and name not in optional:
            raise InvalidOp(f"{kind} does not take '{name}'" if name in KNOWN_FIELDS else f"{kind} has an unknown field")
    for name in required:
        if name not in op:
            raise InvalidOp(f"{kind} needs '{name}'")
    _check_id(op["id"])
    if "lat" in required:
        _check_range(op["lat"], "lat", -90, 90)
        _check_range(op["lon"], "lon", -180, 180)
    if "title" in op:
        _check_title(op["title"])
    if "color" in op and op["color"] not in COLORS:
        raise InvalidOp("'color' must be red, orange, yellow, green, blue, purple or gray")
    if kind == "note":
        _check_text(op["text"])
    elif kind == "photo":
        _check_photo(op["type"], op["data"])
    elif kind == "track":
        _check_points(op["points"])


def check_batch(value: object, limits: Limits) -> List[dict]:
    """The operations of a POST body, all valid, or an exception saying why not."""
    if not isinstance(value, dict):
        raise InvalidJson("not an object")
    if list(value) != ["ops"]:
        raise InvalidBody('the body must be {"ops":[...]} with nothing else')
    ops = value["ops"]
    if not isinstance(ops, list) or not 1 <= len(ops) <= limits.ops_per_post:
        raise InvalidBody(f"'ops' must be a list of 1 to {limits.ops_per_post} operations")
    for index, op in enumerate(ops):
        try:
            check_op(op)
        except InvalidOp as error:
            raise InvalidOp(error.reason, index) from None
    return ops


def encode_op(op: dict) -> bytes:
    return json.dumps(op, ensure_ascii=False, separators=(",", ":"), allow_nan=False).encode("utf-8")


# The queue


class QueueFull(Exception):
    pass


class AfterTooLarge(Exception):
    pass


@dataclass
class _Entry:
    seq: int
    received_ms: int
    taken_at: float
    data: bytes


class OpsQueue:
    """Operations waiting for the gateway, numbered from 1 in each epoch. Safe to use from several threads."""

    def __init__(self, keep_seconds: float, limits: Limits = Limits(), clock: Optional[Clock] = None) -> None:
        self.keep_seconds = keep_seconds
        self.limits = limits
        self.clock = clock or Clock()
        self.epoch = secrets.token_urlsafe(16)
        self._condition = threading.Condition()
        self._entries: "collections.deque[_Entry]" = collections.deque()
        self._bytes = 0
        self._last_seq = 0
        self._expired_up_to = 0
        self._stopped = False

    def _drop_expired(self) -> None:
        cutoff = self.clock.monotonic() - self.keep_seconds
        while self._entries and self._entries[0].taken_at <= cutoff:
            entry = self._entries.popleft()
            self._bytes -= len(entry.data)
            self._expired_up_to = entry.seq

    def _forget_up_to(self, after: int) -> None:
        while self._entries and self._entries[0].seq <= after:
            self._bytes -= len(self._entries.popleft().data)

    def take(self, encoded: Sequence[bytes]) -> Tuple[int, int]:
        """Takes a whole batch or nothing. Returns how many were taken and the last sequence number."""
        size = sum(len(data) for data in encoded)
        with self._condition:
            self._drop_expired()
            if len(self._entries) + len(encoded) > self.limits.queue_ops or self._bytes + size > self.limits.queue_bytes:
                raise QueueFull()
            received_ms = self.clock.wall_ms()
            taken_at = self.clock.monotonic()
            for data in encoded:
                self._last_seq += 1
                self._entries.append(_Entry(self._last_seq, received_ms, taken_at, data))
            self._bytes += size
            self._condition.notify_all()
            return len(encoded), self._last_seq

    def fetch(self, epoch: Optional[str], after: int, limit: int, wait: float) -> Tuple[bytes, int]:
        """The answer to GET /v1/ops, and how many operations it holds. May wait up to ``wait`` seconds for one."""
        with self._condition:
            self._drop_expired()
            if epoch == self.epoch:
                if after > self._last_seq:
                    raise AfterTooLarge()
                self._forget_up_to(after)
                reset = False
                start = after
            else:
                reset = True
                start = 0
            if wait > 0 and not self._entries and not self._stopped:
                self._condition.wait_for(lambda: bool(self._entries) or self._stopped, timeout=wait)
                self._drop_expired()
            missed = self._expired_up_to > start
            head = b'{"epoch":"%s","reset":%s,"missed":%s,"ops":[' % (
                self.epoch.encode("ascii"),
                b"true" if reset else b"false",
                b"true" if missed else b"false",
            )
            size = len(head) + 2
            pieces: List[bytes] = []
            for entry in self._entries:
                if len(pieces) >= min(limit, self.limits.page_ops):
                    break
                piece = b'{"seq":%d,"received_ms":%d,"op":%s}' % (entry.seq, entry.received_ms, entry.data)
                extra = len(piece) + (1 if pieces else 0)
                if pieces and size + extra > self.limits.page_bytes:
                    break
                pieces.append(piece)
                size += extra
        return head + b",".join(pieces) + b"]}", len(pieces)

    def stop(self) -> None:
        """Answers every waiting fetch at once."""
        with self._condition:
            self._stopped = True
            self._condition.notify_all()


# Tokens


def _digest(token: str) -> bytes:
    return hashlib.sha256(token.encode("utf-8", "replace")).digest()


class Tokens:
    """Source and gateway tokens, compared in constant time. Only their SHA-256 digests are kept."""

    def __init__(self, sources: Sequence[str], gateway: str) -> None:
        self._sources = [_digest(token) for token in sources]
        self._gateway = _digest(gateway)

    @classmethod
    def from_environment(cls, environ: Mapping[str, str]) -> "Tokens":
        rule = "A token is 32 to 256 characters from A-Z, a-z, 0-9, '-', '_', '.' and '~'."
        sources = [piece.strip() for piece in environ.get("ERDIGO_RELAY_SOURCE_TOKENS", "").split(",") if piece.strip()]
        if not sources:
            raise UsageError("ERDIGO_RELAY_SOURCE_TOKENS must hold at least one source token. " + rule)
        if not all(TOKEN.fullmatch(token) for token in sources):
            raise UsageError("a token in ERDIGO_RELAY_SOURCE_TOKENS is not valid. " + rule)
        gateway = environ.get("ERDIGO_RELAY_GATEWAY_TOKEN", "").strip()
        if not gateway:
            raise UsageError("ERDIGO_RELAY_GATEWAY_TOKEN must hold the gateway token. " + rule)
        if not TOKEN.fullmatch(gateway):
            raise UsageError("ERDIGO_RELAY_GATEWAY_TOKEN is not a valid token. " + rule)
        if any(hmac.compare_digest(gateway, source) for source in sources):
            raise UsageError("the gateway token must differ from every source token")
        return cls(sources, gateway)

    def role(self, token: Optional[str]) -> Optional[str]:
        """'source', 'gateway' or None."""
        if not token:
            return None
        digest = _digest(token)
        source = False
        for known in self._sources:
            source |= hmac.compare_digest(digest, known)
        gateway = hmac.compare_digest(digest, self._gateway)
        if source:
            return "source"
        return "gateway" if gateway else None


# HTTP


class InvalidQuery(ValueError):
    pass


def _whole(value: Optional[str], low: int, high: int, default: int) -> int:
    if value is None:
        return default
    if not WHOLE.fullmatch(value) or not low <= int(value) <= high:
        raise InvalidQuery()
    return int(value)


def parse_query(text: str) -> Tuple[Optional[str], int, int, int]:
    """epoch, after, limit and wait of GET /v1/ops."""
    try:
        pairs = parse_qsl(text, keep_blank_values=True, strict_parsing=True, max_num_fields=8) if text else []
    except ValueError:
        raise InvalidQuery() from None
    seen: Dict[str, str] = {}
    for name, value in pairs:
        if name not in ("epoch", "after", "limit", "wait") or name in seen:
            raise InvalidQuery()
        seen[name] = value
    epoch = seen.get("epoch")
    if epoch is not None and not EPOCH.fullmatch(epoch):
        raise InvalidQuery()
    return epoch, _whole(seen.get("after"), 0, MAX_SAFE_INTEGER, 0), _whole(seen.get("limit"), 1, 100, 100), _whole(seen.get("wait"), 0, 30, 0)


def _is_json_media_type(value: str) -> bool:
    media_type, *parameters = value.split(";")
    if media_type.strip().lower() != "application/json":
        return False
    for parameter in parameters:
        name, _, setting = parameter.partition("=")
        if name.strip().lower() != "charset" or setting.strip().strip('"').lower() != "utf-8":
            return False
    return True


def _json(value: object) -> bytes:
    return json.dumps(value, separators=(",", ":")).encode("utf-8")


ERROR_NAMES = {400: "bad_request", 404: "not_found", 405: "method_not_allowed", 408: "timeout", 413: "too_large",
               414: "too_large", 431: "too_large", 501: "method_not_allowed"}


class _Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    # Per read or write on the connection. RelayServer.read_seconds bounds a whole request.
    timeout = 30
    disable_nagle_algorithm = True
    server: "RelayServer"

    def version_string(self) -> str:
        return "erdigo-relay"

    def log_message(self, format: str, *args: object) -> None:
        # The standard log line holds the request line, which a careless client may fill with anything.
        pass

    def handle_one_request(self) -> None:
        self._status: Optional[int] = None
        self._note = ""
        self._body_read = False
        self._expects_continue = False
        self.server.watch(self)
        try:
            super().handle_one_request()
        finally:
            self.server.unwatch(self)
        if self._status is not None:
            command = getattr(self, "command", None) or ""
            method = command if re.fullmatch(r"[A-Z]{1,12}", command) else "-"
            path = (getattr(self, "path", None) or "").split("?", 1)[0]
            self.server.log(f"{method} {path if path in ROUTES else '-'} {self._status}{self._note}")

    def handle_expect_100(self) -> bool:
        # Sent once the headers have passed every check, so that a client does not send a body only to have it
        # refused.
        self._expects_continue = True
        return True

    def __getattr__(self, name: str) -> Callable[[], None]:
        # Every method reaches _route, so that an unknown one gets 404 or 405 rather than 501.
        if name.startswith("do_"):
            return self._route
        raise AttributeError(name)

    # Answers

    def _has_unread_body(self) -> bool:
        if self._body_read or self.headers is None:
            return False
        return self.headers.get("Transfer-Encoding") is not None or self.headers.get("Content-Length", "0").strip() not in ("", "0")

    def _discard_body(self) -> bool:
        """Reads and drops a refused POST body of one plain length within the limit, so that the connection stays
        usable. A client that asked for 100 Continue has not sent it, and is not asked to. Other requests rarely have a
        body, and a GET may no longer be under the watchdog, so they only linger."""
        if self.command != "POST" or self._expects_continue or self.headers.get("Transfer-Encoding") is not None:
            return False
        lengths = self.headers.get_all("Content-Length") or []
        if len(lengths) != 1 or not WHOLE.fullmatch(lengths[0].strip()):
            return False
        left = int(lengths[0].strip())
        if left > self.server.queue.limits.body_bytes:
            return False
        try:
            while left > 0:
                chunk = self.rfile.read(min(left, 65536))
                if not chunk:
                    return False
                left -= len(chunk)
        except (OSError, ValueError):
            return False
        self._body_read = True
        return True

    def _linger(self) -> None:
        """Closing a socket with unread data resets the connection, and a client still sending its body may then lose
        the answer too. So the relay stops writing and reads and drops what still comes, for a while, before it
        closes. The watchdog still bounds the whole request."""
        try:
            self.connection.shutdown(socket.SHUT_WR)
        except OSError:
            return
        give_up = time.monotonic() + self.server.linger_seconds
        left = self.server.linger_bytes
        try:
            while left > 0:
                remaining = give_up - time.monotonic()
                if remaining <= 0:
                    return
                self.connection.settimeout(min(2.0, remaining))
                chunk = self.rfile.read1(min(left, 65536))
                if not chunk:
                    return
                left -= len(chunk)
        except (OSError, ValueError):
            return

    def _respond(self, status: int, body: bytes, headers: Sequence[Tuple[str, str]] = ()) -> None:
        self._status = status
        linger = False
        if self._has_unread_body() and not self._discard_body():
            # What follows on the connection would be the body, not a request.
            self.close_connection = True
            linger = True
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        for name, value in headers:
            self.send_header(name, value)
        if self.close_connection:
            self.send_header("Connection", "close")
        self.end_headers()
        if getattr(self, "command", None) != "HEAD":
            self.wfile.write(body)
        if linger:
            self._linger()

    def _error(self, status: int, name: str, headers: Sequence[Tuple[str, str]] = (), **fields: object) -> None:
        self._respond(status, _json({"error": name, **fields}), headers)

    def send_error(self, code: int, message: Optional[str] = None, explain: Optional[str] = None) -> None:
        # Errors the standard library finds before _route, such as a malformed request line.
        self.close_connection = True
        if not hasattr(self, "headers"):
            self.headers = None  # type: ignore[assignment]
        self._error(code, ERROR_NAMES.get(code, "bad_request"))

    # Routing

    def _route(self) -> None:
        path, _, query = self.path.partition("?")
        allowed = ROUTES.get(path)
        if allowed is None:
            return self._error(404, "not_found")
        if self.command not in allowed:
            return self._error(405, "method_not_allowed", [("Allow", ", ".join(allowed))])
        if path == "/v1/health":
            return self._respond(200, b'{"ok":true}')
        role = self.server.tokens.role(self._bearer_token())
        if self.command == "POST":
            if role != "source":
                return self._error(401, "unauthorized")
            return self._post()
        if role != "gateway":
            return self._error(401, "unauthorized")
        return self._get(query)

    def _bearer_token(self) -> Optional[str]:
        values = self.headers.get_all("Authorization") or []
        if len(values) != 1:
            return None
        scheme, _, token = values[0].strip().partition(" ")
        if scheme.lower() != "bearer":
            return None
        return token.strip() or None

    def _post(self) -> None:
        limits = self.server.queue.limits
        types = self.headers.get_all("Content-Type") or []
        if len(types) != 1 or not _is_json_media_type(types[0]):
            return self._error(415, "unsupported_media_type")
        lengths = self.headers.get_all("Content-Length") or []
        if self.headers.get("Transfer-Encoding") is not None or len(lengths) != 1 or not WHOLE.fullmatch(lengths[0].strip()):
            # Whatever the client sends next cannot be told apart from a request.
            self.close_connection = True
            return self._error(411, "length_required")
        length = int(lengths[0].strip())
        if length > limits.body_bytes:
            return self._error(413, "too_large")
        if self._expects_continue:
            self.send_response_only(100)
            self.end_headers()
        try:
            body = self.rfile.read(length)
        except (OSError, ValueError):
            body = b""
        if len(body) != length:
            self.close_connection = True
            return
        self._body_read = True
        self.server.unwatch(self)
        try:
            ops = check_batch(parse_json(body), limits)
        except InvalidJson:
            return self._error(400, "invalid_json")
        except InvalidBody as error:
            return self._error(400, "invalid_body", reason=str(error))
        except InvalidOp as error:
            return self._error(400, "invalid_op", index=error.index, reason=error.reason)
        try:
            accepted, last_seq = self.server.queue.take([encode_op(op) for op in ops])
        except QueueFull:
            return self._error(503, "queue_full", [("Retry-After", "30")])
        self._note = f" accepted={accepted}"
        self._respond(200, _json({"accepted": accepted, "last_seq": last_seq}))

    def _get(self, query: str) -> None:
        self.server.unwatch(self)
        try:
            epoch, after, limit, wait = parse_query(query)
        except InvalidQuery:
            return self._error(400, "invalid_query")
        try:
            body, count = self.server.queue.fetch(epoch, after, limit, wait)
        except AfterTooLarge:
            return self._error(400, "invalid_query")
        self._note = f" ops={count}"
        self._respond(200, body)


def _stderr_log(line: str) -> None:
    stamp = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    sys.stderr.write(f"{stamp} {line}\n")
    sys.stderr.flush()


class RelayServer(ThreadingHTTPServer):
    """The relay's HTTP server. Each connection has its own thread, so a waiting fetch holds up nobody else."""

    daemon_threads = True
    request_queue_size = 64
    #: Connections served at once. More are closed at once.
    max_connections = 128
    #: Seconds to receive one request's line, headers and body, so that a slow client cannot hold a thread.
    read_seconds = 60.0
    #: After refusing a body it did not read, how long and how much the relay goes on reading and dropping before it
    #: closes the connection, so that the client gets to read the answer.
    linger_seconds = 10.0
    linger_bytes = 8 * MIB

    def __init__(self, address: Tuple[str, int], family: int, queue: OpsQueue, tokens: Tokens, log: Callable[[str], None] = _stderr_log) -> None:
        self.address_family = family
        self.queue = queue
        self.tokens = tokens
        self.log = log
        self._lock = threading.Lock()
        self._watched: Dict[_Handler, float] = {}
        self._active = 0
        super().__init__(address, _Handler)

    def server_bind(self) -> None:
        # HTTPServer.server_bind looks up the host's name, which can take seconds and is not needed.
        socketserver.TCPServer.server_bind(self)
        self.server_name, self.server_port = self.server_address[:2]

    @property
    def url(self) -> str:
        host, port = self.server_address[:2]
        return f"http://[{host}]:{port}" if ":" in host else f"http://{host}:{port}"

    def process_request(self, request: socket.socket, client_address: object) -> None:
        with self._lock:
            full = self._active >= self.max_connections
            if not full:
                self._active += 1
        if full:
            self.shutdown_request(request)
            return
        super().process_request(request, client_address)

    def process_request_thread(self, request: socket.socket, client_address: object) -> None:
        try:
            super().process_request_thread(request, client_address)
        finally:
            with self._lock:
                self._active -= 1

    def handle_error(self, request: object, client_address: object) -> None:
        # A connection that broke off. The standard handler prints a traceback, which says nothing useful here.
        pass

    def watch(self, handler: _Handler) -> None:
        with self._lock:
            self._watched[handler] = time.monotonic() + self.read_seconds

    def unwatch(self, handler: _Handler) -> None:
        with self._lock:
            self._watched.pop(handler, None)

    def service_actions(self) -> None:
        now = time.monotonic()
        with self._lock:
            late = [handler for handler, deadline in self._watched.items() if deadline <= now]
            for handler in late:
                del self._watched[handler]
        for handler in late:
            try:
                handler.connection.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass


# Command line


@dataclass(frozen=True)
class Settings:
    host: str
    port: int
    family: int
    keep_hours: int


def parse_listen(text: str, behind_proxy: bool) -> Tuple[str, int, int]:
    """HOST:PORT, or [IPV6]:PORT, as a host to bind, a port and an address family."""
    shape = "--listen takes HOST:PORT, with an IPv6 address written as [::1]:PORT"
    if text.startswith("["):
        host, closed, rest = text[1:].partition("]")
        if not closed or not rest.startswith(":"):
            raise UsageError(shape)
        port_text = rest[1:]
        try:
            ipaddress.IPv6Address(host)
        except ValueError:
            raise UsageError(shape) from None
        family = socket.AF_INET6
    else:
        host, colon, port_text = text.rpartition(":")
        if not colon or not host or ":" in host:
            raise UsageError(shape)
        family = socket.AF_INET
    if not WHOLE.fullmatch(port_text) or int(port_text) > 65535:
        raise UsageError("the port in --listen must be from 0 to 65535")
    port = int(port_text)
    try:
        address: Optional[ipaddress._BaseAddress] = ipaddress.ip_address(host)
    except ValueError:
        address = None
    loopback = host.lower() == "localhost" or (address is not None and address.is_loopback)
    if not loopback and not behind_proxy:
        raise UsageError(
            "the relay speaks plain HTTP, so --listen takes only a loopback address (127.0.0.1, ::1 or localhost) "
            "unless --behind-proxy says that HTTPS ends in a proxy in front of it"
        )
    if host.lower() == "localhost":
        return "127.0.0.1", port, socket.AF_INET
    if address is None:
        try:
            found = socket.getaddrinfo(host, port, type=socket.SOCK_STREAM)
        except OSError:
            raise UsageError("the host in --listen could not be found") from None
        return found[0][4][0], port, found[0][0]
    return host, port, family


def parse_arguments(arguments: Sequence[str]) -> Settings:
    values: Dict[str, str] = {}
    behind_proxy = False
    index = 0
    while index < len(arguments):
        argument = arguments[index]
        if argument == "--behind-proxy":
            behind_proxy = True
            index += 1
            continue
        if argument not in ("--listen", "--keep-hours"):
            if argument.startswith(("--listen=", "--keep-hours=")):
                raise UsageError(f"write {argument.split('=', 1)[0]} and its value as two separate arguments")
            raise UsageError("unknown argument. See 'relay.py --help'.")
        if index + 1 >= len(arguments):
            raise UsageError(f"{argument} needs a value")
        if argument in values:
            raise UsageError(f"{argument} was given twice")
        values[argument] = arguments[index + 1]
        index += 2
    keep_text = values.get("--keep-hours", "24")
    if not WHOLE.fullmatch(keep_text) or not 1 <= int(keep_text) <= 168:
        raise UsageError("--keep-hours must be a whole number of hours from 1 to 168")
    host, port, family = parse_listen(values.get("--listen", "127.0.0.1:8080"), behind_proxy)
    return Settings(host, port, family, int(keep_text))


def main(argv: Optional[Sequence[str]] = None, environ: Optional[Mapping[str, str]] = None) -> int:
    arguments = list(sys.argv[1:] if argv is None else argv)
    if arguments[:1] in (["help"], ["--help"], ["-h"]):
        print(USAGE)
        return 0
    try:
        settings = parse_arguments(arguments)
        tokens = Tokens.from_environment(os.environ if environ is None else environ)
    except UsageError as error:
        print(f"relay: {error}", file=sys.stderr)
        return 1
    queue = OpsQueue(settings.keep_hours * 3600.0)
    try:
        server = RelayServer((settings.host, settings.port), settings.family, queue, tokens)
    except OSError as error:
        print(f"relay: could not listen there: {error.strerror or error}", file=sys.stderr)
        return 1

    stop = threading.Event()
    previous = {}
    if threading.current_thread() is threading.main_thread():
        for number in (signal.SIGINT, signal.SIGTERM):
            previous[number] = signal.signal(number, lambda *_: stop.set())
    serving = threading.Thread(target=server.serve_forever, name="relay", daemon=True)
    serving.start()
    print(f"Relay listening on {server.url}", flush=True)
    try:
        while not stop.wait(0.5):
            pass
    except KeyboardInterrupt:
        pass
    finally:
        queue.stop()
        server.shutdown()
        server.server_close()
        for number, handler in previous.items():
            signal.signal(number, handler)
    print("Relay stopped.", file=sys.stderr, flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
