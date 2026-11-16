#!/usr/bin/env python3
"""An example Erdigo gateway. It fetches operations from a cloud relay and writes them to the Erdigo app on this local
network through the SDK, as a paired device.

    python gateway.py pair --name "Cloud gateway" --code-file FILE
    ERDIGO_RELAY_TOKEN=... python gateway.py run --relay https://relay.example.com

It needs this repository's erdigo_sdk package and nothing else. See docs/cloud-relay.md.
"""

from __future__ import annotations

import base64
import http.client
import ipaddress
import json
import os
import re
import signal
import socket
import ssl
import sys
import threading
import time
import unicodedata
from dataclasses import dataclass, replace
from typing import Callable, Dict, Iterable, List, Optional, Sequence, Set, Tuple
from urllib.parse import urlencode, urlsplit

from erdigo_sdk import (
    DISCOVER,
    Coordinate,
    DeviceIdentity,
    DeviceStore,
    ErdigoClient,
    ErdigoError,
    ErrorKind,
    FileDeviceStore,
    PhotoType,
    ReceiverPairing,
    ReceiverTarget,
    RetryPolicy,
    TrackPoint,
    WireError,
    cli,
    default_directory,
)
from erdigo_sdk.client import backoff
from erdigo_sdk.private_file import FileLock, PrivateFileError, PrivateFileProblem, read_private, write_atomically
from erdigo_sdk.terminal import UsageError

USAGE = """\
gateway: fetches operations from an Erdigo cloud relay and writes them to the Erdigo app on this local network.

  gateway.py pair --name NAME [--kinds LIST] [--host HOST --port PORT] [--code-file FILE] [--fingerprint HEX]
                  [--profile NAME] [--state-dir DIR]
      Pairs this gateway with the app once, as 'erdigo-send pair' does. The code or the QR text is read from the
      keyboard, or from --code-file, a file only you can read. --kinds is pin,marker,note,photo,track by default.

  gateway.py run --relay URL [--host HOST --port PORT] [--once] [--profile NAME] [--state-dir DIR]
      Fetches from the relay and writes to the app until it is stopped. --relay must start with https://, except
      for a loopback host. --once stops once the relay has nothing more. The gateway token is read from the
      environment variable ERDIGO_RELAY_TOKEN, never from the command line.

  --host and --port give the app's address. Without them the gateway finds the app on the local network, which
  needs the optional zeroconf package. --profile is cloud-gateway by default, and a gateway's profile holds exactly
  one pairing.

Exit codes of run: 0 stopped, or nothing more with --once; 1 a mistake in the command line or the environment, no
pairing, or a profile another run is using; 2 the relay refused the token or broke the relay API; 3 with --once, the
app or the relay could not be reached for about 30 seconds. See docs/cloud-relay.md.
"""

DEFAULT_PROFILE = "cloud-gateway"
ALL_KINDS = "pin,marker,note,photo,track"
STATE_FILE = "relay-state.json"
LOCK_FILE = ".gateway.lock"
#: Seconds the relay may hold a fetch open while nothing is waiting.
WAIT_SECONDS = 25
#: Seconds for each read from the relay, comfortably above WAIT_SECONDS.
HTTP_TIMEOUT = 45.0
#: Seconds for a whole exchange with the relay, from sending the request to the last byte of the answer.
ANSWER_SECONDS = 120.0
MAX_ANSWER_BYTES = 8 * 1024 * 1024
PAGE = 100
#: An empty answer that comes back sooner than this is followed by a pause, so that a relay that cannot wait is not
#: asked again and again.
POLL_SECONDS = 5.0
#: With --once, how long the app or the relay may stay out of reach before the gateway gives up. For the relay it
#: counts from the start of the first try that failed, so the time each try takes counts too. For the app it counts
#: from the first sign of trouble with an operation, however many connections the SDK makes in that time (see Outage).
ONCE_GIVE_UP_SECONDS = 30.0
#: How long a request may go on without a connection to the app before the gateway says so. Looking for the app on the
#: network takes about 2 seconds, so finding it there is not mistaken for an outage.
NO_ANSWER_NOTICE_SECONDS = 3.0
#: How often the gateway looks at the SDK's connection while a request runs.
WATCH_SECONDS = 0.2
#: The app closes a connection that has been idle for 5 minutes (spec section 11). One idle for this long is closed
#: first, so that the next request does not find it gone.
IDLE_SECONDS = 240.0
MAX_SAFE_INTEGER = 2**53 - 1

TOKEN = re.compile(r"[A-Za-z0-9._~-]{32,256}")
EPOCH = re.compile(r"[A-Za-z0-9_-]{16,64}")
BASE64 = re.compile(r"[A-Za-z0-9+/]*={0,2}")
URL_PATH = re.compile(r"[A-Za-z0-9._~!$&'()*+,;=:@%/-]*")
ERROR_CODE = re.compile(r"[a-z_]{1,40}")

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

#: Receiver error codes that say "not now" rather than "never" (spec section 10).
TRANSIENT_CODES = frozenset({"rate_limited", "paused", "internal_error", "too_many_errors"})


class GatewayExit(Exception):
    def __init__(self, code: int, message: Optional[str] = None) -> None:
        super().__init__(message or "")
        self.code = code
        self.message = message


class BrokeApi(Exception):
    """The relay answered with something docs/cloud-relay.md does not allow."""


class RelayUnreachable(Exception):
    def __init__(self, timed_out: bool = False) -> None:
        super().__init__("timed out" if timed_out else "unreachable")
        #: The exchange, or one read in it, went past the time it was allowed.
        self.timed_out = timed_out


# Command line


KNOWN_OPTIONS = {"code-file", "fingerprint", "host", "kinds", "name", "once", "port", "profile", "relay", "state-dir"}


class Options:
    """``--name value`` pairs and bare flags, as erdigo-send reads them."""

    def __init__(self, arguments: Sequence[str], allowed_flags: Iterable[str]) -> None:
        allowed = set(allowed_flags)
        self._values: Dict[str, str] = {}
        self._flags: Set[str] = set()
        self._used: Set[str] = set()
        index = 0
        while index < len(arguments):
            argument = arguments[index]
            # Errors never repeat what was typed, because it might be a pairing code or a token.
            if not argument.startswith("--"):
                raise UsageError("unexpected argument. Options start with --, and neither a pairing code nor a token is given as an argument.")
            name = argument[2:]
            if name in ("code", "token") or name.startswith(("code=", "token=")):
                raise UsageError(
                    "codes and tokens are not accepted as arguments, because arguments appear in the process list and shell history. "
                    "Use --code-file for the pairing code and ERDIGO_RELAY_TOKEN for the token."
                )
            if "=" in name:
                shown = name.split("=", 1)[0]
                raise UsageError(f"write {'--' + shown if shown in KNOWN_OPTIONS else 'an option'} and its value as two separate arguments")
            if name in allowed:
                self._flags.add(name)
                index += 1
                continue
            if name not in KNOWN_OPTIONS:
                raise UsageError("an unknown option was given. See 'gateway.py help'.")
            if index + 1 >= len(arguments):
                raise UsageError(f"--{name} needs a value")
            if name in self._values:
                raise UsageError(f"--{name} was given twice")
            self._values[name] = arguments[index + 1]
            index += 2

    def flag(self, name: str) -> bool:
        self._used.add(name)
        return name in self._flags

    def optional(self, name: str) -> Optional[str]:
        self._used.add(name)
        return self._values.get(name)

    def required(self, name: str) -> str:
        value = self.optional(name)
        if value is None:
            raise UsageError(f"--{name} is required")
        return value

    def finish(self) -> None:
        unknown = sorted(name for name in list(self._values) + list(self._flags) if name not in self._used)
        if unknown:
            raise UsageError(f"--{unknown[0]} does not apply to this command")


def _is_profile_character(character: str) -> bool:
    # The rule erdigo-send applies, so that both reach the same folder for the same --profile.
    if character in "-_":
        return True
    return unicodedata.category(character) in ("Lu", "Ll", "Lt", "Lm", "Lo", "Mn", "Mc", "Me", "Nd", "Nl", "No")


def state_directory(options: Options) -> str:
    profile = options.optional("profile")
    path = options.optional("state-dir")
    if path is not None:
        if profile is not None:
            raise UsageError("--state-dir and --profile cannot be used together, because a state folder holds one identity")
        return path
    name = DEFAULT_PROFILE if profile is None else profile
    if not name or not all(_is_profile_character(character) for character in name):
        raise UsageError("--profile may contain only letters, digits, '-' and '_'")
    return default_directory(name)


def run_pair(arguments: Sequence[str]) -> None:
    options = Options(arguments, [])
    kinds = options.optional("kinds")
    # An empty --kinds goes to erdigo-send's own check, which refuses it, rather than widening to every kind.
    forwarded = ["--name", options.required("name"), "--kinds", ALL_KINDS if kinds is None else kinds]
    for name in ("host", "port", "code-file", "fingerprint"):
        value = options.optional(name)
        if value is not None:
            forwarded += [f"--{name}", value]
    directory = state_directory(options)
    options.finish()
    if FileDeviceStore(directory).pairings():
        raise UsageError(
            "this profile already holds a pairing, and a gateway's profile holds exactly one. Use another --profile or "
            "--state-dir, or remove that pairing first with 'erdigo-send list' and 'erdigo-send forget' and the same folder."
        )
    # erdigo-send's own pairing, so that the code is read and the receiver's key compared by exactly the same rules.
    cli.run_pair(forwarded + ["--state-dir", directory])


# The relay


def _is_loopback(host: str) -> bool:
    if host == "localhost":
        return True
    try:
        return ipaddress.ip_address(host).is_loopback
    except ValueError:
        return False


def _is_ip_address(host: str) -> bool:
    try:
        ipaddress.ip_address(host)
        return True
    except ValueError:
        return False


@dataclass(frozen=True)
class RelayAddress:
    #: The address as relay-state.json keeps it.
    url: str
    secure: bool
    host: str
    port: int
    path: str

    @classmethod
    def parse(cls, text: str) -> "RelayAddress":
        try:
            parts = urlsplit(text.strip())
            port = parts.port
        except ValueError:
            raise UsageError("--relay is not a valid URL") from None
        scheme = parts.scheme.lower()
        host = parts.hostname
        if scheme not in ("https", "http") or not host:
            raise UsageError("--relay must be an https:// address, such as https://relay.example.com")
        if parts.username is not None or parts.password is not None or parts.query or parts.fragment:
            raise UsageError("--relay must be the relay's address alone, without a user name, a password, a query or a fragment")
        if scheme == "http" and not _is_loopback(host):
            raise UsageError(
                "--relay must start with https://. Plain http:// is accepted only for a loopback host (127.0.0.1, ::1 or "
                "localhost), for testing on one computer."
            )
        if not _is_ip_address(host):
            # The name is looked up as IDNA, which refuses an empty label or one longer than 63 characters.
            try:
                host.encode("idna")
            except UnicodeError:
                raise UsageError("--relay has a host name that is not valid") from None
        path = parts.path.rstrip("/")
        if not URL_PATH.fullmatch(path):
            raise UsageError("--relay has characters in its path that a URL cannot hold")
        shown = f"[{host}]" if ":" in host else host
        url = f"{scheme}://{shown}{'' if port is None else f':{port}'}{path}"
        return cls(url, scheme == "https", host, port if port is not None else (443 if scheme == "https" else 80), path)


def tls_context() -> ssl.SSLContext:
    """The system's certificate authorities, with host names checked."""
    return ssl.create_default_context()


class RelayClient:
    """GET /v1/ops over one kept-alive connection, which is opened again after any failure. It never follows a
    redirect, which could carry the token somewhere else."""

    def __init__(self, address: RelayAddress, token: str) -> None:
        self.address = address
        self._token = token
        self._connection: Optional[http.client.HTTPConnection] = None

    def close(self) -> None:
        if self._connection is not None:
            self._connection.close()
            self._connection = None

    def _connect(self) -> http.client.HTTPConnection:
        if self.address.secure:
            return http.client.HTTPSConnection(self.address.host, self.address.port, timeout=HTTP_TIMEOUT, context=tls_context())
        return http.client.HTTPConnection(self.address.host, self.address.port, timeout=HTTP_TIMEOUT)

    def fetch(self, epoch: Optional[str], after: int, wait: int, seconds: Optional[float] = None) -> Tuple[int, str, bytes]:
        """Status, content type and body, all within ``seconds``, and never more than ANSWER_SECONDS. Raises
        RelayUnreachable, or BrokeApi for an answer that is too large."""
        deadline = time.monotonic() + (ANSWER_SECONDS if seconds is None else min(seconds, ANSWER_SECONDS))
        query = ([("epoch", epoch)] if epoch is not None else []) + [("after", str(after)), ("limit", str(PAGE)), ("wait", str(wait))]
        target = f"{self.address.path}/v1/ops?{urlencode(query)}"
        if self._connection is not None:
            try:
                return self._exchange(target, deadline)
            except RelayUnreachable as problem:
                # The relay may have closed a kept-alive connection while the gateway was busy with the app. That is
                # tried again at once, within the same time; an answer that took too long is not.
                if problem.timed_out or time.monotonic() >= deadline:
                    raise
        return self._exchange(target, deadline)

    def _exchange(self, target: str, deadline: float) -> Tuple[int, str, bytes]:
        headers = {"Authorization": f"Bearer {self._token}", "Accept": "application/json", "User-Agent": "erdigo-gateway-python"}
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise RelayUnreachable(True)
        if self._connection is None:
            self._connection = self._connect()
        connection = self._connection
        # Connecting and each read may take HTTP_TIMEOUT, and none of them may go past the deadline.
        connection.timeout = min(HTTP_TIMEOUT, remaining)
        if connection.sock is not None:
            connection.sock.settimeout(connection.timeout)
        # The socket timeout limits each read, not the whole answer, which a relay could send a byte at a time.
        cut = threading.Event()
        sockets: List[socket.socket] = []
        watchdog = threading.Timer(remaining, _cut_off, (connection, sockets, cut))
        watchdog.daemon = True
        watchdog.start()
        try:
            connection.request("GET", target, headers=headers)
            # An answer that ends with the connection takes the socket over, and connection.sock becomes None.
            if connection.sock is not None:
                sockets.append(connection.sock)
            response = connection.getresponse()
            body = self._read(response)
            if cut.is_set() and response.length is None and not response.chunked:
                # Without a length, the end of an answer the watchdog cut off looks like the end of a whole one.
                raise TimeoutError()
        except BrokeApi:
            self.close()
            raise
        except (OSError, http.client.HTTPException) as error:
            self.close()
            # A read that timed out is an answer that took too long too, not a kept-alive connection the relay closed.
            raise RelayUnreachable(cut.is_set() or isinstance(error, TimeoutError)) from None
        finally:
            watchdog.cancel()
            watchdog.join()
        if response.will_close or cut.is_set():
            self.close()
        return response.status, response.getheader("Content-Type") or "", body

    @staticmethod
    def _read(response: http.client.HTTPResponse) -> bytes:
        announced = (response.getheader("Content-Length") or "").strip()
        if announced.isdigit() and int(announced) > MAX_ANSWER_BYTES:
            raise BrokeApi("the answer is larger than 8 MiB")
        chunks: List[bytes] = []
        total = 0
        while True:
            chunk = response.read(65536)
            if not chunk:
                if response.length:
                    # read() with a size returns what came before the connection ended, without saying it is short.
                    raise http.client.IncompleteRead(b"", response.length)
                return b"".join(chunks)
            total += len(chunk)
            if total > MAX_ANSWER_BYTES:
                raise BrokeApi("the answer is larger than 8 MiB")
            chunks.append(chunk)


def _cut_off(connection: http.client.HTTPConnection, sockets: List[socket.socket], cut: threading.Event) -> None:
    """Ends an exchange that went past its time, from the watchdog's thread."""
    cut.set()
    for sock in [connection.sock, *sockets]:
        if sock is not None:
            try:
                # The plain socket's shutdown: an SSLSocket's own would also drop its TLS state under the reading thread.
                socket.socket.shutdown(sock, socket.SHUT_RDWR)
            except OSError:
                pass


class InvalidJson(ValueError):
    pass


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
    """One JSON value in UTF-8, refusing a byte order mark, invalid UTF-8, a duplicate key, a number too large to be
    finite and an unpaired surrogate escape, as the example relays do."""
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


def _is_integer(value: object) -> bool:
    return isinstance(value, int) and not isinstance(value, bool)


@dataclass(frozen=True)
class Answer:
    epoch: str
    reset: bool
    missed: bool
    ops: List[Tuple[int, dict]]


def parse_answer(body: bytes, sent_epoch: Optional[str], after: int) -> Answer:
    """Checks an answer to GET /v1/ops against docs/cloud-relay.md. Raises BrokeApi."""
    try:
        value = parse_json(body)
    except InvalidJson:
        raise BrokeApi("the answer is not one JSON object in UTF-8") from None
    if not isinstance(value, dict) or set(value) != {"epoch", "reset", "missed", "ops"}:
        raise BrokeApi("the answer must have exactly 'epoch', 'reset', 'missed' and 'ops'")
    epoch, reset, missed, ops = value["epoch"], value["reset"], value["missed"], value["ops"]
    if not isinstance(epoch, str) or not EPOCH.fullmatch(epoch):
        raise BrokeApi("'epoch' must be 16 to 64 letters, digits, '-' or '_'")
    if not isinstance(reset, bool) or not isinstance(missed, bool):
        raise BrokeApi("'reset' and 'missed' must be true or false")
    if reset == (epoch == sent_epoch):
        raise BrokeApi("'reset' must be false exactly when 'epoch' is the one the gateway sent")
    if not isinstance(ops, list) or len(ops) > PAGE:
        raise BrokeApi(f"'ops' must be a list of at most {PAGE} operations")
    previous = 0 if reset else after
    entries: List[Tuple[int, dict]] = []
    for entry in ops:
        if not isinstance(entry, dict) or set(entry) != {"seq", "received_ms", "op"}:
            raise BrokeApi("each operation must have exactly 'seq', 'received_ms' and 'op'")
        seq = entry["seq"]
        if not _is_integer(seq) or not previous < seq <= MAX_SAFE_INTEGER:
            raise BrokeApi("sequence numbers must go up, and start above 'after' unless 'reset' is true")
        received = entry["received_ms"]
        if not _is_integer(received) or not 0 <= received <= MAX_SAFE_INTEGER:
            raise BrokeApi("'received_ms' must be a whole number of milliseconds")
        if not isinstance(entry["op"], dict):
            raise BrokeApi("'op' must be an object")
        entries.append((seq, entry["op"]))
        previous = seq
    return Answer(epoch, reset, missed, entries)


# Operations


class Refused(Exception):
    def __init__(self, code: str) -> None:
        super().__init__(code)
        self.code = code


class AppUnavailable(Exception):
    def __init__(self, code: Optional[str]) -> None:
        super().__init__(code or "no_answer")
        self.code = code


def _string(value: object) -> str:
    if not isinstance(value, str):
        raise Refused("invalid_op")
    return value


def _optional_string(op: dict, name: str) -> Optional[str]:
    return None if name not in op else _string(op[name])


def _number(value: object) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise Refused("invalid_op")
    return value


def _coordinate(holder: dict) -> Coordinate:
    return Coordinate(_number(holder["lat"]), _number(holder["lon"]))


def _photo(kind: object, data: object) -> bytes:
    expected = PhotoType.from_wire(kind) if isinstance(kind, str) else None
    if expected is None or not isinstance(data, str) or len(data) % 4 or not BASE64.fullmatch(data):
        raise Refused("invalid_op")
    try:
        decoded = base64.b64decode(data, validate=True)
    except ValueError:
        raise Refused("invalid_op") from None
    # One spelling only, and the bytes must be what 'type' says.
    if base64.b64encode(decoded).decode("ascii") != data or PhotoType.detect(decoded) is not expected:
        raise Refused("invalid_op")
    return decoded


def _point(value: object) -> TrackPoint:
    if not isinstance(value, dict) or not set(value) <= {"lat", "lon", "ts", "alt"} or "lat" not in value or "lon" not in value:
        raise Refused("invalid_op")
    timestamp = None
    if "ts" in value:
        timestamp = _number(value["ts"])
        # A relay that cannot tell 5 from 5.0 may pass either.
        if isinstance(timestamp, float) and timestamp.is_integer():
            timestamp = int(timestamp)
    altitude = _number(value["alt"]) if "alt" in value else None
    return TrackPoint(_coordinate(value), timestamp, altitude)  # type: ignore[arg-type]


def prepare(op: dict) -> Callable[[ErdigoClient], None]:
    """The SDK call for one relay operation. Raises Refused for one that cannot be mapped (invalid_op) or that the SDK
    refuses as it is built (invalid_argument)."""
    kind = op.get("op")
    if not isinstance(kind, str) or kind not in OPERATIONS:
        raise Refused("invalid_op")
    required, optional = OPERATIONS[kind]
    if any(name != "op" and name not in required + optional for name in op) or any(name not in op for name in required):
        raise Refused("invalid_op")
    item_id = _string(op["id"])
    title = _optional_string(op, "title")
    try:
        if kind == "pin":
            at = _coordinate(op)
            return lambda client: client.put_pin(item_id, at, title)
        if kind == "marker":
            at = _coordinate(op)
            color = _optional_string(op, "color")
            return lambda client: client.put_marker(item_id, at, title, color)  # type: ignore[arg-type]
        if kind == "note":
            at = _coordinate(op)
            text = _string(op["text"])
            return lambda client: client.put_note(item_id, at, text)
        if kind == "photo":
            at = _coordinate(op)
            data = _photo(op["type"], op["data"])
            return lambda client: client.put_photo(item_id, at, data, title)
        if kind == "track":
            if not isinstance(op["points"], list):
                raise Refused("invalid_op")
            points = [_point(point) for point in op["points"]]
            return lambda client: client.append_track(item_id, points, title)
        if kind == "finish_track":
            return lambda client: client.finish_track(item_id)
        if kind == "move":
            at = _coordinate(op)
            return lambda client: client.move(item_id, at)
        return lambda client: client.delete(item_id)
    except (WireError, TypeError, ValueError, OverflowError):
        # OverflowError: a JSON integer too large for a float, such as a latitude of 1 followed by 400 zeros.
        raise Refused("invalid_argument") from None


def apply(client: ErdigoClient, call: Callable[[ErdigoClient], None]) -> Optional[str]:
    """None when the app took the operation, or the code it was refused with for good. Raises AppUnavailable when it
    should be tried again later."""
    try:
        call(client)
        return None
    except ErdigoError as error:
        if error.kind is ErrorKind.NO_ANSWER:
            raise AppUnavailable(None) from None
        if error.kind is ErrorKind.NOT_PERMITTED:
            return "not_permitted"
        if error.kind is ErrorKind.INVALID_ARGUMENT:
            return "invalid_argument"
        if error.kind is ErrorKind.STORAGE:
            raise GatewayExit(1, str(error)) from None
        if error.kind is ErrorKind.REJECTED:
            code = error.receiver_error.code if error.receiver_error is not None else "rejected"
            if code in TRANSIENT_CODES:
                raise AppUnavailable(code) from None
            # Any other code, even one this gateway does not know, is a refusal of this request (spec section 10).
            return code if ERROR_CODE.fullmatch(code) else "rejected"
        # Another program with this key, a broken answer or no common version: worth trying again, and never a
        # reason to drop the operation.
        raise AppUnavailable(error.kind.value) from None
    except (WireError, ValueError, TypeError, OverflowError):
        return "invalid_argument"


def connect(client: ErdigoClient) -> None:
    """Connects to the app, or keeps the connection there is. Nothing about the operation has been sent yet, so nothing
    here refuses it: an error answer to hello or a welcome the SDK cannot use is a problem with the connection, and the
    operation is tried again. Raises AppUnavailable."""
    try:
        client.connect()
    except ErdigoError as error:
        if error.kind is ErrorKind.STORAGE:
            raise GatewayExit(1, str(error)) from None
        if error.kind is ErrorKind.NO_ANSWER:
            raise AppUnavailable(None) from None
        code = error.receiver_error.code if error.receiver_error is not None else error.kind.value
        raise AppUnavailable(code if ERROR_CODE.fullmatch(code) else "rejected") from None


# The place in the relay's queue


@dataclass(frozen=True)
class Place:
    relay: str
    epoch: Optional[str]
    after: int
    #: The relay goes on saying 'missed' until the place moves past what expired, so it is said once per place, and
    #: not again by the next run of a scheduled job.
    missed_said: bool = False

    @classmethod
    def load(cls, directory: str, relay: str) -> "Place":
        """The saved place, or the start of the queue if none was saved for this relay."""
        try:
            data = read_private(os.path.join(directory, STATE_FILE), 64 * 1024)
        except PrivateFileError as error:
            if error.problem is PrivateFileProblem.MISSING:
                return cls(relay, None, 0)
            raise UsageError(str(error) if error.problem is PrivateFileProblem.INSECURE else f"{STATE_FILE} could not be read") from None
        try:
            value = json.loads(data.decode("utf-8"))
            if not isinstance(value, dict) or value.get("version") != 1 or set(value) - {"missed_said"} != {"version", "relay", "epoch", "after"}:
                raise ValueError()
            saved, epoch, after, missed_said = value["relay"], value["epoch"], value["after"], value.get("missed_said", False)
            if not isinstance(saved, str) or not isinstance(epoch, str) or not EPOCH.fullmatch(epoch) or not isinstance(missed_said, bool):
                raise ValueError()
            if not _is_integer(after) or not 0 <= after <= MAX_SAFE_INTEGER:
                raise ValueError()
        except ValueError:
            raise UsageError(f"{STATE_FILE} is damaged. Move it away to start again from the relay's oldest operation.") from None
        if saved != relay:
            return cls(relay, None, 0)
        return cls(relay, epoch, after, missed_said)

    def save(self, directory: str) -> None:
        value = {"version": 1, "relay": self.relay, "epoch": self.epoch, "after": self.after}
        if self.missed_said:
            value["missed_said"] = True
        data = json.dumps(value, indent=2) + "\n"
        # Through a temporary file and a rename, with mode 0600, as the SDK writes its own files.
        write_atomically(os.path.join(directory, STATE_FILE), data.encode("utf-8"))


# Running


def _seconds(delay: float) -> str:
    whole = max(1, round(delay))
    return f"{whole} second" if whole == 1 else f"{whole} seconds"


def _name(op: dict) -> str:
    kind = op.get("op")
    return kind if isinstance(kind, str) and kind in OPERATIONS else "unknown"


def say(line: str) -> None:
    print(line, flush=True)


class Outage:
    """What the gateway knows about one operation's trouble reaching the app, across all the tries for it.

    The SDK counts its retry time from the moment a connection was lost and starts again at every new connection, so an
    app that takes every connection but never answers, or whose answers are all lost, would keep it trying for ever.
    This counts from the first sign of trouble with the operation until the app takes it, however many connections that
    takes. The watcher and the gateway's own thread both use it."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._began: Optional[float] = None
        self._said = False
        self._given_up = False

    @property
    def since(self) -> Optional[float]:
        """When the first sign of trouble showed, or None while there has been none."""
        with self._lock:
            return self._began

    def note(self, moment: float) -> float:
        """Notes trouble that showed at ``moment``, and returns when the trouble began."""
        with self._lock:
            self._began = moment if self._began is None else min(self._began, moment)
            return self._began

    def take_notice(self) -> bool:
        """True the first time only, so that the gateway says once per operation that the app does not answer."""
        with self._lock:
            said, self._said = self._said, True
            return not said

    def give_up(self) -> None:
        with self._lock:
            self._given_up = True

    @property
    def given_up(self) -> bool:
        with self._lock:
            return self._given_up


class GaveUp(Exception):
    """Ends an SDK request the gateway has given up on. It passes through the SDK unchanged."""


class StoppableStore(DeviceStore):
    """The profile's store as the SDK sees it, able to end a request the gateway has given up on.

    The SDK has no way to stop a request from another thread. But it takes a new connect counter from its store for
    every connection it makes, before it sends anything on it (spec section 5), so refusing one there ends the request
    at its next connection, with nothing sent twice and nothing half written. Everything else goes to the profile's
    own store."""

    def __init__(self, inner: DeviceStore) -> None:
        self._inner = inner
        #: The operation being applied. Set by the gateway, read in the SDK's thread.
        self.outage: Optional[Outage] = None

    def next_connect_counter(self, receiver_fingerprint: str, now_ms: int) -> int:
        outage = self.outage
        if outage is not None and outage.given_up:
            raise GaveUp()
        return self._inner.next_connect_counter(receiver_fingerprint, now_ms)

    def load_or_create_identity(self) -> DeviceIdentity:
        return self._inner.load_or_create_identity()

    def pairings(self) -> List[ReceiverPairing]:
        return self._inner.pairings()

    def save(self, pairing: ReceiverPairing) -> None:
        self._inner.save(pairing)

    def update_address(self, receiver_fingerprint: str, host: str, port: int) -> None:
        self._inner.update_address(receiver_fingerprint, host, port)

    def remove_pairing(self, fingerprint: str) -> None:
        self._inner.remove_pairing(fingerprint)


class Gateway:
    def __init__(
        self, relay: RelayClient, client: ErdigoClient, directory: str, place: Place, once: bool, store: Optional[StoppableStore] = None
    ) -> None:
        self.relay = relay
        self.client = client
        self.directory = directory
        self.place = place
        self.once = once
        #: The store the client was given, through which a request given up on is ended.
        self.store = store
        self._app_used_at = -float("inf")

    def run(self) -> int:
        try:
            while True:
                started = time.monotonic()
                answer = self._fetch()
                if answer.reset:
                    if self.place.epoch is not None:
                        say("The relay started a new numbering; starting from its oldest operation.")
                    self._save(Place(self.place.relay, answer.epoch, 0))
                if answer.missed and not self.place.missed_said:
                    say("Some operations expired on the relay before they were fetched.")
                    self._save(replace(self.place, missed_said=True))
                if not answer.ops:
                    if self.once:
                        return 0
                    pause = POLL_SECONDS - (time.monotonic() - started)
                    if pause > 0:
                        time.sleep(pause)
                    continue
                if time.monotonic() - self._app_used_at > IDLE_SECONDS:
                    self.client.close()
                refused = 0
                for seq, op in answer.ops:
                    code = self._apply(op)
                    self._app_used_at = time.monotonic()
                    if code is not None:
                        refused += 1
                        say(f"Refused operation {seq} ({_name(op)}): {code}")
                    # The place moves on only once the app has taken or refused the operation: at least once.
                    self._save(Place(self.place.relay, answer.epoch, seq))
                count = len(answer.ops)
                say(f"Done with {count} operation{'' if count == 1 else 's'}, up to {answer.ops[-1][0]}" + (f" ({refused} refused)" if refused else ""))
        except KeyboardInterrupt:
            say("Stopped.")
            return 0

    def _save(self, place: Place) -> None:
        place.save(self.directory)
        self.place = place

    def _fetch(self) -> Answer:
        attempt = 0
        # When the first try that failed began. With --once the gateway gives up ONCE_GIVE_UP_SECONDS after it, so
        # the time each try takes counts, and a relay that takes the connection and never answers is given up on too.
        outage_started: Optional[float] = None
        while True:
            started = time.monotonic()
            seconds = None
            if self.once:
                # A try may use what is left of that time, and the last one gets a second at least.
                spent = 0.0 if outage_started is None else started - outage_started
                seconds = max(1.0, ONCE_GIVE_UP_SECONDS - spent)
            try:
                status, content_type, body = self.relay.fetch(self.place.epoch, self.place.after, 0 if self.once else WAIT_SECONDS, seconds)
            except RelayUnreachable:
                problem = "The relay could not be reached"
            except BrokeApi as error:
                raise GatewayExit(2, f"the relay's answer does not follow the relay API: {error}") from None
            else:
                if status == 200:
                    if content_type.split(";", 1)[0].strip().lower() != "application/json":
                        raise GatewayExit(2, "the relay's answer does not follow the relay API: it is not application/json")
                    try:
                        return parse_answer(body, self.place.epoch, self.place.after)
                    except BrokeApi as error:
                        raise GatewayExit(2, f"the relay's answer does not follow the relay API: {error}") from None
                if status == 401:
                    raise GatewayExit(2, "the relay refused the token. Check ERDIGO_RELAY_TOKEN.")
                if status < 500 and status not in (408, 429):
                    raise GatewayExit(2, f"the relay answered with status {status}, which the relay API does not allow here. Check --relay.")
                problem = f"The relay answered with an error ({status})"
            if outage_started is None:
                outage_started = started
            delay = backoff(attempt)
            attempt += 1
            if self.once:
                # All the time allowed is used, with a last try at the end of it, as the SDK does.
                remaining = ONCE_GIVE_UP_SECONDS - (time.monotonic() - outage_started)
                if remaining <= 0:
                    say("The relay could not be reached.")
                    raise GatewayExit(3)
                delay = min(delay, remaining)
            say(f"{problem}; trying again in {_seconds(delay)}.")
            time.sleep(delay)

    def _apply(self, op: dict) -> Optional[str]:
        """Applies one operation, and returns the code of a refusal.

        While the app cannot be reached the SDK waits 1, 2, 4 ... up to 60 seconds and sends the same request again
        with the same sequence number, so an operation the app applied but whose answer was lost is not applied twice
        (spec sections 7 and 13). With --once the gateway gives up once the app has not taken the operation
        ONCE_GIVE_UP_SECONDS after the first sign of trouble with it, even if the app takes every new connection."""
        try:
            call = prepare(op)
        except Refused as refusal:
            return refusal.code
        outage = Outage()
        if self.store is not None:
            self.store.outage = outage
        # For answers that say "not now", and connections that fail in a way the SDK does not wait out itself.
        attempt = 0
        try:
            while True:
                try:
                    return self._try(call, outage)
                except GaveUp:
                    self._give_up()
                except AppUnavailable as problem:
                    if problem.code is None or outage.given_up:
                        # The SDK gave up by its own count, or the request ended after the gateway had given up.
                        self._give_up()
                    now = time.monotonic()
                    # From the answer, not from the start of the try, which may have spent its time pacing points.
                    started = outage.note(now)
                    delay = backoff(attempt)
                    attempt += 1
                    if self.once:
                        remaining = ONCE_GIVE_UP_SECONDS - (now - started)
                        if remaining <= 0:
                            self._give_up()
                        delay = min(delay, remaining)
                    # This line says when, so the one without a time is not needed for this operation any more.
                    outage.take_notice()
                    say(f"The Erdigo app answered {problem.code}; trying again in {_seconds(delay)}.")
                    time.sleep(delay)
        finally:
            if self.store is not None:
                self.store.outage = None

    @staticmethod
    def _give_up() -> None:
        say("The Erdigo app could not be reached. The operations stay on the relay.")
        raise GatewayExit(3) from None

    def _try(self, call: Callable[[ErdigoClient], None], outage: Outage) -> Optional[str]:
        """One try: None when the app took the operation, or the code it was refused with. Connecting first keeps a
        failure of the connection apart from an answer about the operation, and tells the watcher when the SDK has a
        connection for this try and is sending the operation over it."""
        sending = threading.Event()

        def request() -> Optional[str]:
            connect(self.client)
            sending.set()
            return apply(self.client, call)

        return self._watching(request, outage, sending)

    def _watching(self, request: Callable[[], Optional[str]], outage: Outage, sending: threading.Event) -> Optional[str]:
        """Runs one try, with a watcher beside it, since the SDK does not say how a request is doing.

        While the app is out of reach the SDK waits 1, 2, 4 ... up to 60 seconds between attempts and sends the same
        request again. The watcher looks at the SDK's connection: a connection the SDK made for this try and then
        dropped while ``sending`` means sending failed, and the trouble began then. While the trouble lasts, or while
        the try goes on without any connection, it says once per operation that the app does not answer. The SDK does
        not tell when it tries next, so the line gives no time. A request that waits to stay within the app's limits
        keeps its connection, so that wait is never taken for trouble.

        With --once the watcher gives up once the trouble has lasted ONCE_GIVE_UP_SECONDS. The SDK then meets the
        refusal of StoppableStore at its next connection, which comes as soon as the attempt under way fails, and the
        request ends with GaveUp. A request still going over a working connection, paced or waiting for its answer, is
        never cut short, so time spent pacing points cannot make the gateway give up on a slow but healthy operation.
        Ctrl+C and SIGTERM end the SDK's wait, in this thread, and stop the gateway."""
        done = threading.Event()

        def watch() -> None:
            unconnected_since: Optional[float] = None
            while not done.wait(WATCH_SECONDS):
                connected = self.client.limits is not None
                now = time.monotonic()
                if connected:
                    unconnected_since = None
                else:
                    if unconnected_since is None:
                        unconnected_since = now
                    if sending.is_set():
                        # The SDK drops the connection a request is sent over only when sending on it failed, and then
                        # waits before it connects again, which the watcher sees.
                        outage.note(now)
                since = outage.since
                troubled = 0.0 if since is None else now - since
                unconnected = 0.0 if unconnected_since is None else now - unconnected_since
                if max(troubled, unconnected) >= NO_ANSWER_NOTICE_SECONDS and outage.take_notice():
                    say("No answer from the Erdigo app; trying again.")
                if self.once and since is not None and troubled >= ONCE_GIVE_UP_SECONDS:
                    outage.give_up()
                    return

        watcher = threading.Thread(target=watch, name="gateway-watch", daemon=True)
        watcher.start()
        try:
            return request()
        finally:
            done.set()
            watcher.join()


def run_gateway(arguments: Sequence[str]) -> int:
    options = Options(arguments, ["once"])
    address = RelayAddress.parse(options.required("relay"))
    host = options.optional("host")
    port = options.optional("port")
    once = options.flag("once")
    directory = state_directory(options)
    options.finish()
    if (host is None) != (port is None):
        raise UsageError("--host and --port go together")
    if port is not None and (not port.isascii() or not port.isdigit() or int(port) > 65535):
        raise UsageError("--port must be between 1 and 65535")
    token = os.environ.get("ERDIGO_RELAY_TOKEN", "").strip()
    if not token:
        raise UsageError("set ERDIGO_RELAY_TOKEN to the gateway token. A token is never taken as a command-line argument.")
    if not TOKEN.fullmatch(token):
        raise UsageError("ERDIGO_RELAY_TOKEN is not a valid token: 32 to 256 characters from A-Z, a-z, 0-9, '-', '_', '.' and '~'.")
    target = DISCOVER if host is None else ReceiverTarget.address(host, int(port))  # type: ignore[arg-type]

    store = FileDeviceStore(directory)
    lock = FileLock(os.path.join(store.directory, LOCK_FILE), give_up_after=0)
    try:
        lock.__enter__()
    except OSError:
        raise UsageError("another gateway is running with this profile") from None
    try:
        pairings = store.pairings()
        if not pairings:
            raise UsageError("this profile holds no pairing. Run 'gateway.py pair' first.")
        if len(pairings) > 1:
            raise UsageError(
                "this profile holds several pairings, and a gateway's profile holds exactly one. "
                "Remove the others with 'erdigo-send forget' and the same folder, or pair a new profile."
            )
        identity = store.load_or_create_identity()
        place = Place.load(store.directory, address.url)
        relay = RelayClient(address, token)
        # The SDK resends a request after a lost answer with the same sequence number, so the app applies it at most
        # once (spec sections 7 and 13). Without --once it does so for as long as the app is out of reach, and Ctrl+C
        # or SIGTERM ends its wait. With --once its policy gives up on an app it cannot connect to; it starts its count
        # again at every new connection, so the gateway keeps its own count as well (see Outage).
        retry = RetryPolicy.up_to(ONCE_GIVE_UP_SECONDS) if once else RetryPolicy.FOREVER
        stoppable = StoppableStore(store)
        with ErdigoClient(identity, pairings[0], stoppable, target, retry) as client:
            try:
                return Gateway(relay, client, store.directory, place, once, stoppable).run()
            finally:
                relay.close()
    finally:
        lock.__exit__(None, None, None)


def main(argv: Optional[Sequence[str]] = None) -> int:
    arguments = list(sys.argv[1:] if argv is None else argv)
    if not arguments or arguments[0] in ("help", "--help", "-h"):
        print(USAGE)
        return 0
    command = arguments[0]
    try:
        if command == "pair":
            run_pair(arguments[1:])
            return 0
        if command == "run":
            return run_gateway(arguments[1:])
        raise UsageError("unknown command. Use 'pair' or 'run', or see 'gateway.py help'.")
    except GatewayExit as error:
        if error.message:
            print(f"gateway: {error.message}", file=sys.stderr)
        return error.code
    except (UsageError, ErdigoError, WireError, OSError) as error:
        print(f"gateway: {error}", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print("gateway: interrupted", file=sys.stderr)
        return 130


def _stop(*_: object) -> None:
    raise KeyboardInterrupt()


if __name__ == "__main__":
    signal.signal(signal.SIGTERM, _stop)
    sys.exit(main())
