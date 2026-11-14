"""Every integration-v1 message, and its encoding.

Decoding is an allow-list: unknown types and unknown fields are rejected, and nothing about identity is ever taken from
a message. Encoding writes fields in a fixed order, so a resent request is byte-for-byte the same.
"""

from __future__ import annotations

import base64
import binascii
from dataclasses import dataclass
from typing import List, Optional, Tuple, Union

from . import protocol, strict_json
from .model import (
    LIMITS_V1,
    Coordinate,
    ItemKind,
    Limits,
    MarkerColor,
    PhotoType,
    TrackPoint,
    validate_item_id,
    validate_note,
    validate_title,
)
from .protocol import ErrorCode, WireError
from .strict_json import JsonInteger, JsonNumber, Number

# Requests


@dataclass(frozen=True)
class Ping:
    pass


@dataclass(frozen=True)
class PutPin:
    id: str
    at: Coordinate
    title: Optional[str] = None


@dataclass(frozen=True)
class PutMarker:
    id: str
    at: Coordinate
    title: Optional[str] = None
    color: Optional[MarkerColor] = None


@dataclass(frozen=True)
class PutNote:
    id: str
    at: Coordinate
    text: str


@dataclass(frozen=True)
class Move:
    id: str
    to: Coordinate


@dataclass(frozen=True)
class Delete:
    id: str


@dataclass(frozen=True)
class DeleteAll:
    pass


@dataclass(frozen=True)
class PhotoHeader:
    id: str
    at: Coordinate
    title: Optional[str]
    type: PhotoType
    size: int
    sha256: str
    chunks: int


@dataclass(frozen=True)
class PhotoBegin:
    header: PhotoHeader


@dataclass(frozen=True)
class PhotoChunk:
    id: str
    index: int
    data: bytes


@dataclass(frozen=True)
class PhotoEnd:
    id: str


@dataclass(frozen=True)
class TrackAppend:
    id: str
    title: Optional[str]
    points: Tuple[TrackPoint, ...]


@dataclass(frozen=True)
class TrackFinish:
    id: str


Request = Union[Ping, PutPin, PutMarker, PutNote, Move, Delete, DeleteAll, PhotoBegin, PhotoChunk, PhotoEnd, TrackAppend, TrackFinish]


@dataclass(frozen=True)
class RequestEnvelope:
    seq: int
    request: Request


@dataclass(frozen=True)
class Hello:
    """The first message on a connection, sent by the device."""

    versions: Tuple[int, ...]
    sdk: str


@dataclass(frozen=True)
class PairRequest:
    """The payload of the third pairing message."""

    versions: Tuple[int, ...]
    sdk: str
    name: str
    kinds: Tuple[ItemKind, ...]


# Receiver to device


@dataclass(frozen=True)
class Welcome:
    version: int
    last_seq: int
    kinds: Tuple[ItemKind, ...]
    limits: Limits


@dataclass(frozen=True)
class ReceiverError:
    """An ``error`` answer. ``code`` is kept as text: a device must treat a code it does not know as a permanent
    failure."""

    code: str
    seq: Optional[int] = None
    last_seq: Optional[int] = None
    retry_after_ms: Optional[int] = None
    supported: Optional[Tuple[int, ...]] = None
    fatal: bool = False


@dataclass(frozen=True)
class Ack:
    seq: int
    dup: bool


@dataclass(frozen=True)
class PairPending:
    pass


@dataclass(frozen=True)
class PairApproved:
    """``counter`` is the highest connect counter the receiver has accepted from this device."""

    kinds: Tuple[ItemKind, ...]
    counter: int


@dataclass(frozen=True)
class PairDenied:
    pass


ReceiverMessage = Union[Welcome, Ack, ReceiverError, PairPending, PairApproved, PairDenied]


# Reading fields


class _Fields:
    """Reads a JSON object against an explicit list of fields."""

    def __init__(self, value: object) -> None:
        if not isinstance(value, dict):
            raise WireError(ErrorCode.INVALID_MESSAGE, "a message is a JSON object")
        self.map = value

    def has(self, key: str) -> bool:
        return key in self.map

    def only(self, *allowed: str) -> None:
        for key in self.map:
            if key not in allowed:
                raise WireError(ErrorCode.INVALID_MESSAGE, "unknown field")

    def string(self, key: str) -> str:
        value = self.map.get(key)
        if not isinstance(value, str):
            raise WireError(ErrorCode.INVALID_MESSAGE, f"field '{key}' must be a string")
        return value

    def optional_string(self, key: str) -> Optional[str]:
        return self.string(key) if self.has(key) else None

    def integer(self, key: str) -> int:
        value = self.map.get(key)
        if not isinstance(value, JsonInteger):
            raise WireError(ErrorCode.INVALID_MESSAGE, f"field '{key}' must be an integer")
        return value.value

    def optional_integer(self, key: str) -> Optional[int]:
        return self.integer(key) if self.has(key) else None

    def number(self, key: str) -> float:
        value = self.map.get(key)
        if isinstance(value, JsonNumber):
            return value.value
        if isinstance(value, JsonInteger):
            return float(value.value)
        raise WireError(ErrorCode.INVALID_MESSAGE, f"field '{key}' must be a number")

    def boolean(self, key: str) -> bool:
        value = self.map.get(key)
        if not isinstance(value, bool):
            raise WireError(ErrorCode.INVALID_MESSAGE, f"field '{key}' must be true or false")
        return value

    def array(self, key: str) -> list:
        value = self.map.get(key)
        if not isinstance(value, list):
            raise WireError(ErrorCode.INVALID_MESSAGE, f"field '{key}' must be an array")
        return value

    def seq(self, key: str = "seq") -> int:
        value = self.integer(key)
        if not 1 <= value <= protocol.MAX_SEQUENCE:
            raise WireError(ErrorCode.INVALID_MESSAGE, "a sequence number is within 1...2^53-1")
        return value

    def non_negative(self, key: str) -> int:
        value = self.integer(key)
        if not 0 <= value <= protocol.MAX_SEQUENCE:
            raise WireError(ErrorCode.INVALID_MESSAGE, f"field '{key}' is out of range")
        return value

    def id(self) -> str:
        return validate_item_id(self.string("id"))

    def coordinate(self) -> Coordinate:
        latitude = self.number("lat")
        longitude = self.number("lon")
        return Coordinate(latitude, longitude)

    def title(self, limits: Limits) -> Optional[str]:
        title = self.optional_string("title")
        if title is None:
            return None
        validate_title(title, limits)
        return title

    def versions(self) -> Tuple[int, ...]:
        values = self.array("versions")
        if not 1 <= len(values) <= 16:
            raise WireError(ErrorCode.INVALID_MESSAGE, "field 'versions' must have 1 to 16 entries")
        return tuple(_version(value) for value in values)

    def kinds(self) -> Tuple[ItemKind, ...]:
        kinds: List[ItemKind] = []
        for value in self.array("kinds"):
            kind = ItemKind.from_wire(value) if isinstance(value, str) else None
            if kind is None or kind in kinds:
                raise WireError(ErrorCode.INVALID_MESSAGE, "field 'kinds' lists item kinds without repeats")
            kinds.append(kind)
        return tuple(kinds)

    def sdk_name(self) -> str:
        name = self.string("sdk")
        allowed = all(character.isascii() and (character.isalnum() or character in "._/-") for character in name)
        if not 1 <= len(name) <= 64 or not allowed:
            raise WireError(ErrorCode.INVALID_MESSAGE, "field 'sdk' is 1 to 64 ASCII letters, digits or . _ / -")
        return name


def _version(value: object) -> int:
    if isinstance(value, JsonInteger) and 1 <= value.value <= 65535:
        return value.value
    raise WireError(ErrorCode.INVALID_MESSAGE, "a version is an integer within 1...65535")


def _parse(data: bytes) -> _Fields:
    if len(data) > protocol.MAX_PLAINTEXT_LENGTH:
        raise WireError(ErrorCode.TOO_LARGE, "a message is at most 65519 bytes")
    try:
        value = strict_json.parse(data)
    except strict_json.JsonParseError:
        raise WireError(ErrorCode.INVALID_MESSAGE, "the message is not valid strict JSON") from None
    return _Fields(value)


# Encoding


def _coordinate_fields(fields: dict, coordinate: Coordinate) -> None:
    fields["lat"] = Number(coordinate.latitude)
    fields["lon"] = Number(coordinate.longitude)


def encode_hello(hello: Hello) -> bytes:
    return strict_json.encode({"t": "hello", "versions": list(hello.versions), "sdk": hello.sdk})


def encode_pair_request(request: PairRequest) -> bytes:
    return strict_json.encode(
        {
            "t": "pair_request",
            "versions": list(request.versions),
            "sdk": request.sdk,
            "name": request.name,
            "kinds": [kind.value for kind in request.kinds],
        }
    )


def _encode_point(point: TrackPoint) -> dict:
    fields = {"lat": Number(point.coordinate.latitude), "lon": Number(point.coordinate.longitude)}
    if point.timestamp_ms is not None:
        fields["ts"] = int(point.timestamp_ms)
    if point.altitude_m is not None:
        fields["alt"] = Number(point.altitude_m)
    return fields


def encode_request(envelope: RequestEnvelope) -> bytes:
    request = envelope.request
    fields: dict = {}

    def put(kind: str) -> None:
        fields["t"] = kind
        fields["seq"] = int(envelope.seq)

    if isinstance(request, Ping):
        put("ping")
    elif isinstance(request, PutPin):
        put("put")
        fields["kind"] = "pin"
        fields["id"] = request.id
        _coordinate_fields(fields, request.at)
        if request.title is not None:
            fields["title"] = request.title
    elif isinstance(request, PutMarker):
        put("put")
        fields["kind"] = "marker"
        fields["id"] = request.id
        _coordinate_fields(fields, request.at)
        if request.title is not None:
            fields["title"] = request.title
        if request.color is not None:
            fields["color"] = MarkerColor(request.color).value
    elif isinstance(request, PutNote):
        put("put")
        fields["kind"] = "note"
        fields["id"] = request.id
        _coordinate_fields(fields, request.at)
        fields["text"] = request.text
    elif isinstance(request, Move):
        put("move")
        fields["id"] = request.id
        _coordinate_fields(fields, request.to)
    elif isinstance(request, Delete):
        put("delete")
        fields["id"] = request.id
    elif isinstance(request, DeleteAll):
        put("delete_all")
    elif isinstance(request, PhotoBegin):
        header = request.header
        put("photo_begin")
        fields["id"] = header.id
        _coordinate_fields(fields, header.at)
        if header.title is not None:
            fields["title"] = header.title
        fields["type"] = PhotoType(header.type).value
        fields["size"] = int(header.size)
        fields["sha256"] = header.sha256
        fields["chunks"] = int(header.chunks)
    elif isinstance(request, PhotoChunk):
        put("photo_chunk")
        fields["id"] = request.id
        fields["index"] = int(request.index)
        fields["data"] = base64.b64encode(request.data).decode("ascii")
    elif isinstance(request, PhotoEnd):
        put("photo_end")
        fields["id"] = request.id
    elif isinstance(request, TrackAppend):
        put("track_append")
        fields["id"] = request.id
        if request.title is not None:
            fields["title"] = request.title
        fields["points"] = [_encode_point(point) for point in request.points]
    elif isinstance(request, TrackFinish):
        put("track_finish")
        fields["id"] = request.id
    else:
        raise TypeError("unknown request")
    return strict_json.encode(fields)


def encode_receiver_message(message: ReceiverMessage) -> bytes:
    if isinstance(message, Welcome):
        limits = message.limits
        return strict_json.encode(
            {
                "t": "welcome",
                "version": message.version,
                "last_seq": message.last_seq,
                "kinds": [kind.value for kind in message.kinds],
                "limits": {
                    "items": limits.items,
                    "photos": limits.photos,
                    "photo_bytes": limits.photo_bytes,
                    "note_bytes": limits.note_bytes,
                    "title_chars": limits.title_chars,
                    "track_points": limits.track_points,
                    "open_tracks": limits.open_tracks,
                    "tracks": limits.tracks,
                    "msgs_per_s": limits.msgs_per_s,
                    "msg_burst": limits.msg_burst,
                    "points_per_s": limits.points_per_s,
                    "point_burst": limits.point_burst,
                    "disk_bytes": limits.disk_bytes,
                },
            }
        )
    if isinstance(message, Ack):
        return strict_json.encode({"t": "ack", "seq": message.seq, "dup": message.dup})
    if isinstance(message, ReceiverError):
        fields: dict = {"t": "error", "code": message.code}
        if message.seq is not None:
            fields["seq"] = message.seq
        if message.last_seq is not None:
            fields["last_seq"] = message.last_seq
        if message.retry_after_ms is not None:
            fields["retry_after_ms"] = message.retry_after_ms
        if message.supported is not None:
            fields["supported"] = list(message.supported)
        if message.fatal:
            fields["fatal"] = True
        return strict_json.encode(fields)
    if isinstance(message, PairPending):
        return strict_json.encode({"t": "pair_status", "status": "pending"})
    if isinstance(message, PairDenied):
        return strict_json.encode({"t": "pair_status", "status": "denied"})
    if isinstance(message, PairApproved):
        return strict_json.encode(
            {"t": "pair_status", "status": "approved", "kinds": [kind.value for kind in message.kinds], "counter": message.counter}
        )
    raise TypeError("unknown message")


# Decoding


def decode_hello(data: bytes) -> Hello:
    fields = _parse(data)
    if fields.string("t") != "hello":
        raise WireError(ErrorCode.INVALID_MESSAGE, "the first message must be hello")
    fields.only("t", "versions", "sdk")
    versions = fields.versions()
    return Hello(versions, fields.sdk_name())


def decode_pair_request(data: bytes) -> PairRequest:
    fields = _parse(data)
    if fields.string("t") != "pair_request":
        raise WireError(ErrorCode.INVALID_MESSAGE, "expected pair_request")
    fields.only("t", "versions", "sdk", "name", "kinds")
    name = fields.string("name")
    if len(name.encode("utf-8")) > 256:
        raise WireError(ErrorCode.TOO_LARGE, "a device name is too long")
    kinds = fields.kinds()
    if not kinds:
        raise WireError(ErrorCode.INVALID_MESSAGE, "field 'kinds' cannot be empty")
    versions = fields.versions()
    return PairRequest(versions, fields.sdk_name(), name, kinds)


def _decode_canonical_base64(text: str) -> Optional[bytes]:
    """Base64 with padding and nothing else: no white space, no line breaks and no stray bits in the last character,
    so that one chunk has exactly one spelling."""
    if len(text) % 4 != 0 or not all(character.isascii() and (character.isalnum() or character in "+/=") for character in text):
        return None
    try:
        data = base64.b64decode(text, validate=True)
    except (binascii.Error, ValueError):
        return None
    return data if base64.b64encode(data).decode("ascii") == text else None


def decode_request(data: bytes, limits: Limits = LIMITS_V1) -> RequestEnvelope:
    """Decodes a request. Raises :class:`WireError` for any message that is not exactly what the specification allows.
    A receiver that announced lower limits passes them here, so that it enforces what it announced."""
    fields = _parse(data)
    kind = fields.string("t")
    seq = fields.seq()
    request: Request
    if kind == "ping":
        fields.only("t", "seq")
        request = Ping()
    elif kind == "put":
        put_kind = fields.string("kind")
        if put_kind == "pin":
            fields.only("t", "seq", "kind", "id", "lat", "lon", "title")
            item_id = fields.id()
            at = fields.coordinate()
            request = PutPin(item_id, at, fields.title(limits))
        elif put_kind == "marker":
            fields.only("t", "seq", "kind", "id", "lat", "lon", "title", "color")
            color = None
            color_text = fields.optional_string("color")
            if color_text is not None:
                color = MarkerColor.from_wire(color_text)
                if color is None:
                    raise WireError(ErrorCode.INVALID_MESSAGE, "unknown marker color")
            item_id = fields.id()
            at = fields.coordinate()
            request = PutMarker(item_id, at, fields.title(limits), color)
        elif put_kind == "note":
            fields.only("t", "seq", "kind", "id", "lat", "lon", "text")
            text = fields.string("text")
            validate_note(text, limits)
            item_id = fields.id()
            request = PutNote(item_id, fields.coordinate(), text)
        else:
            raise WireError(ErrorCode.INVALID_MESSAGE, "field 'kind' must be pin, marker or note")
    elif kind == "move":
        fields.only("t", "seq", "id", "lat", "lon")
        item_id = fields.id()
        request = Move(item_id, fields.coordinate())
    elif kind == "delete":
        fields.only("t", "seq", "id")
        request = Delete(fields.id())
    elif kind == "delete_all":
        fields.only("t", "seq")
        request = DeleteAll()
    elif kind == "photo_begin":
        fields.only("t", "seq", "id", "lat", "lon", "title", "type", "size", "sha256", "chunks")
        photo_type = PhotoType.from_wire(fields.string("type"))
        if photo_type is None:
            raise WireError(ErrorCode.PHOTO_INVALID, "a photo is image/jpeg or image/png")
        size = fields.integer("size")
        chunks = fields.integer("chunks")
        digest = fields.string("sha256")
        if size < 1 or chunks < 1:
            raise WireError(ErrorCode.INVALID_MESSAGE, "fields 'size' and 'chunks' must be positive")
        if size > limits.photo_bytes:
            raise WireError(ErrorCode.TOO_LARGE, f"a photo is at most {limits.photo_bytes} bytes")
        if chunks != -(-size // protocol.MAX_CHUNK_BYTES):
            raise WireError(ErrorCode.INVALID_MESSAGE, "field 'chunks' does not match field 'size'")
        if not protocol.is_fingerprint(digest):
            raise WireError(ErrorCode.INVALID_MESSAGE, "field 'sha256' is 64 lowercase hexadecimal digits")
        item_id = fields.id()
        at = fields.coordinate()
        request = PhotoBegin(PhotoHeader(item_id, at, fields.title(limits), photo_type, size, digest, chunks))
    elif kind == "photo_chunk":
        fields.only("t", "seq", "id", "index", "data")
        index = fields.integer("index")
        if not 0 <= index <= 65534:
            raise WireError(ErrorCode.INVALID_MESSAGE, "field 'index' is out of range")
        chunk = _decode_canonical_base64(fields.string("data"))
        if chunk is None:
            raise WireError(ErrorCode.INVALID_MESSAGE, "field 'data' must be canonical base64")
        if not chunk:
            raise WireError(ErrorCode.INVALID_MESSAGE, "a photo chunk cannot be empty")
        if len(chunk) > protocol.MAX_CHUNK_BYTES:
            raise WireError(ErrorCode.TOO_LARGE, f"a photo chunk is at most {protocol.MAX_CHUNK_BYTES} bytes")
        request = PhotoChunk(fields.id(), index, chunk)
    elif kind == "photo_end":
        fields.only("t", "seq", "id")
        request = PhotoEnd(fields.id())
    elif kind == "track_append":
        fields.only("t", "seq", "id", "title", "points")
        values = fields.array("points")
        if not 1 <= len(values) <= protocol.MAX_POINTS_PER_MESSAGE:
            raise WireError(ErrorCode.INVALID_MESSAGE, f"field 'points' has 1 to {protocol.MAX_POINTS_PER_MESSAGE} entries")
        points = []
        for value in values:
            point = _Fields(value)
            point.only("lat", "lon", "ts", "alt")
            altitude = point.number("alt") if point.has("alt") else None
            coordinate = point.coordinate()
            timestamp = point.optional_integer("ts")
            points.append(TrackPoint(coordinate, timestamp, altitude))
        item_id = fields.id()
        request = TrackAppend(item_id, fields.title(limits), tuple(points))
    elif kind == "track_finish":
        fields.only("t", "seq", "id")
        request = TrackFinish(fields.id())
    else:
        raise WireError(ErrorCode.INVALID_MESSAGE, "unknown message type")
    return RequestEnvelope(seq, request)


def _decode_limits(value: object) -> Limits:
    fields = _Fields(value)
    fields.only(
        "items", "photos", "photo_bytes", "note_bytes", "title_chars", "track_points", "open_tracks", "tracks",
        "msgs_per_s", "msg_burst", "points_per_s", "point_burst", "disk_bytes",
    )

    def positive(key: str) -> int:
        number = fields.integer(key)
        if not 1 <= number <= 1 << 40:
            raise WireError(ErrorCode.INVALID_MESSAGE, "a limit is out of range")
        return number

    def positive_int(key: str) -> int:
        return min(positive(key), 2**31 - 1)

    # A device may batch this many points, so a smaller burst could never be satisfied.
    if positive("point_burst") < protocol.MAX_POINTS_PER_MESSAGE:
        raise WireError(ErrorCode.INVALID_MESSAGE, "field 'point_burst' is below the maximum batch size")
    return Limits(
        items=positive_int("items"),
        photos=positive_int("photos"),
        photo_bytes=positive_int("photo_bytes"),
        note_bytes=positive_int("note_bytes"),
        title_chars=positive_int("title_chars"),
        track_points=positive_int("track_points"),
        open_tracks=positive_int("open_tracks"),
        tracks=positive_int("tracks"),
        msgs_per_s=positive_int("msgs_per_s"),
        msg_burst=positive_int("msg_burst"),
        points_per_s=positive_int("points_per_s"),
        point_burst=positive_int("point_burst"),
        disk_bytes=positive("disk_bytes"),
    )


def decode_receiver_message(data: bytes) -> ReceiverMessage:
    fields = _parse(data)
    kind = fields.string("t")
    if kind == "welcome":
        fields.only("t", "version", "last_seq", "kinds", "limits")
        version = fields.integer("version")
        if not 1 <= version <= 65535:
            raise WireError(ErrorCode.INVALID_MESSAGE, "field 'version' is out of range")
        if not fields.has("limits"):
            raise WireError(ErrorCode.INVALID_MESSAGE, "field 'limits' is missing")
        last_seq = fields.non_negative("last_seq")
        kinds = fields.kinds()
        return Welcome(version, last_seq, kinds, _decode_limits(fields.map["limits"]))
    if kind == "ack":
        fields.only("t", "seq", "dup")
        seq = fields.seq()
        return Ack(seq, fields.boolean("dup"))
    if kind == "error":
        fields.only("t", "code", "seq", "last_seq", "retry_after_ms", "supported", "fatal")
        code = fields.string("code")
        if not 1 <= len(code) <= 64 or not all("a" <= character <= "z" or character == "_" for character in code):
            raise WireError(ErrorCode.INVALID_MESSAGE, "field 'code' is lowercase letters and underscores")
        versions = fields.array("supported") if fields.has("supported") else None
        # Empty is allowed: a receiver may have dropped every version this device could speak.
        if versions is not None and len(versions) > 16:
            raise WireError(ErrorCode.INVALID_MESSAGE, "field 'supported' has at most 16 entries")
        supported = tuple(_version(value) for value in versions) if versions is not None else None
        seq = fields.seq() if fields.has("seq") else None
        last_seq = fields.non_negative("last_seq") if fields.has("last_seq") else None
        retry = fields.non_negative("retry_after_ms") if fields.has("retry_after_ms") else None
        fatal = fields.boolean("fatal") if fields.has("fatal") else False
        return ReceiverError(code, seq, last_seq, retry, supported, fatal)
    if kind == "pair_status":
        status = fields.string("status")
        if status == "pending":
            fields.only("t", "status")
            return PairPending()
        if status == "denied":
            fields.only("t", "status")
            return PairDenied()
        if status == "approved":
            fields.only("t", "status", "kinds", "counter")
            kinds = fields.kinds()
            return PairApproved(kinds, fields.non_negative("counter"))
        raise WireError(ErrorCode.INVALID_MESSAGE, "unknown pairing status")
    raise WireError(ErrorCode.INVALID_MESSAGE, "unknown message type")
