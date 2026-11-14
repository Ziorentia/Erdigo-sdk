package com.ziorentia.erdigo.core;

import com.ziorentia.erdigo.core.Messages.Hello;
import com.ziorentia.erdigo.core.Messages.PairRequest;
import com.ziorentia.erdigo.core.Messages.PairStatus;
import com.ziorentia.erdigo.core.Messages.ReceiverError;
import com.ziorentia.erdigo.core.Messages.ReceiverMessage;
import com.ziorentia.erdigo.core.Messages.RequestEnvelope;
import com.ziorentia.erdigo.core.Messages.Welcome;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Base64;
import java.util.List;
import java.util.Map;
import java.util.Objects;
import java.util.Set;

/** Encodes and decodes every integration-v1 message. */
public final class Wire {
    private Wire() {
    }

    // Device to receiver

    public static byte[] encode(Hello hello) {
        Objects.requireNonNull(hello, "hello");
        return JsonWriter.encode(JsonWriter.object()
                .put("t", "hello")
                .put("versions", integers(hello.versions()))
                .put("sdk", hello.sdk())
                .build());
    }

    public static Hello decodeHello(byte[] data) {
        Fields fields = parse(data);
        if (!fields.string("t").equals("hello")) {
            throw new WireException(ErrorCode.INVALID_MESSAGE, "the first message must be hello");
        }
        fields.only("t", "versions", "sdk");
        List<Integer> versions = fields.versions();
        return new Hello(versions, fields.sdkName());
    }

    public static byte[] encode(PairRequest request) {
        Objects.requireNonNull(request, "request");
        return JsonWriter.encode(JsonWriter.object()
                .put("t", "pair_request")
                .put("versions", integers(request.versions()))
                .put("sdk", request.sdk())
                .put("name", request.name())
                .put("kinds", kinds(request.kinds()))
                .build());
    }

    public static PairRequest decodePairRequest(byte[] data) {
        Fields fields = parse(data);
        if (!fields.string("t").equals("pair_request")) {
            throw new WireException(ErrorCode.INVALID_MESSAGE, "expected pair_request");
        }
        fields.only("t", "versions", "sdk", "name", "kinds");
        String name = fields.string("name");
        if (name.getBytes(StandardCharsets.UTF_8).length > 256) {
            throw new WireException(ErrorCode.TOO_LARGE, "a device name is too long");
        }
        List<ItemKind> kinds = fields.kinds();
        if (kinds.isEmpty()) {
            throw new WireException(ErrorCode.INVALID_MESSAGE, "field 'kinds' cannot be empty");
        }
        List<Integer> versions = fields.versions();
        return new PairRequest(versions, fields.sdkName(), name, kinds);
    }

    public static byte[] encode(RequestEnvelope envelope) {
        Objects.requireNonNull(envelope, "envelope");
        JsonWriter.ObjectBuilder fields = JsonWriter.object();
        Request request = envelope.request();
        if (request instanceof Request.Ping) {
            header(fields, "ping", envelope);
        } else if (request instanceof Request.PutPin pin) {
            header(fields, "put", envelope);
            fields.put("kind", "pin");
            fields.put("id", pin.id().value());
            coordinate(fields, pin.at());
            if (pin.title() != null) {
                fields.put("title", pin.title());
            }
        } else if (request instanceof Request.PutMarker marker) {
            header(fields, "put", envelope);
            fields.put("kind", "marker");
            fields.put("id", marker.id().value());
            coordinate(fields, marker.at());
            if (marker.title() != null) {
                fields.put("title", marker.title());
            }
            if (marker.color() != null) {
                fields.put("color", marker.color().wire());
            }
        } else if (request instanceof Request.PutNote note) {
            header(fields, "put", envelope);
            fields.put("kind", "note");
            fields.put("id", note.id().value());
            coordinate(fields, note.at());
            fields.put("text", note.text());
        } else if (request instanceof Request.Move move) {
            header(fields, "move", envelope);
            fields.put("id", move.id().value());
            coordinate(fields, move.to());
        } else if (request instanceof Request.Delete delete) {
            header(fields, "delete", envelope);
            fields.put("id", delete.id().value());
        } else if (request instanceof Request.DeleteAll) {
            header(fields, "delete_all", envelope);
        } else if (request instanceof Request.PhotoBegin begin) {
            Request.PhotoHeader photo = begin.header();
            header(fields, "photo_begin", envelope);
            fields.put("id", photo.id().value());
            coordinate(fields, photo.at());
            if (photo.title() != null) {
                fields.put("title", photo.title());
            }
            fields.put("type", photo.type().wire());
            fields.put("size", (long) photo.size());
            fields.put("sha256", photo.sha256());
            fields.put("chunks", (long) photo.chunks());
        } else if (request instanceof Request.PhotoChunk chunk) {
            header(fields, "photo_chunk", envelope);
            fields.put("id", chunk.id().value());
            fields.put("index", (long) chunk.index());
            fields.put("data", Base64.getEncoder().encodeToString(chunk.data()));
        } else if (request instanceof Request.PhotoEnd end) {
            header(fields, "photo_end", envelope);
            fields.put("id", end.id().value());
        } else if (request instanceof Request.TrackAppend append) {
            header(fields, "track_append", envelope);
            fields.put("id", append.id().value());
            if (append.title() != null) {
                fields.put("title", append.title());
            }
            List<JsonWriter.Value> points = new ArrayList<>();
            for (TrackPoint point : append.points()) {
                points.add(encode(point));
            }
            fields.put("points", JsonWriter.array(points));
        } else if (request instanceof Request.TrackFinish finish) {
            header(fields, "track_finish", envelope);
            fields.put("id", finish.id().value());
        } else {
            throw new IllegalArgumentException("unknown request");
        }
        return JsonWriter.encode(fields.build());
    }

    private static void header(JsonWriter.ObjectBuilder fields, String type, RequestEnvelope envelope) {
        fields.put("t", type);
        fields.put("seq", envelope.seq());
    }

    private static void coordinate(JsonWriter.ObjectBuilder fields, Coordinate coordinate) {
        fields.put("lat", coordinate.latitude());
        fields.put("lon", coordinate.longitude());
    }

    private static JsonWriter.Value encode(TrackPoint point) {
        JsonWriter.ObjectBuilder fields = JsonWriter.object()
                .put("lat", point.coordinate().latitude())
                .put("lon", point.coordinate().longitude());
        if (point.timestampMs() != null) {
            fields.put("ts", (long) point.timestampMs());
        }
        if (point.altitudeM() != null) {
            fields.put("alt", (double) point.altitudeM());
        }
        return fields.build();
    }

    private static JsonWriter.Value integers(List<Integer> values) {
        List<JsonWriter.Value> output = new ArrayList<>();
        for (int value : values) {
            output.add(JsonWriter.of((long) value));
        }
        return JsonWriter.array(output);
    }

    private static JsonWriter.Value kinds(List<ItemKind> values) {
        List<JsonWriter.Value> output = new ArrayList<>();
        for (ItemKind kind : values) {
            output.add(JsonWriter.of(kind.wire()));
        }
        return JsonWriter.array(output);
    }

    public static RequestEnvelope decodeRequest(byte[] data) {
        return decodeRequest(data, Limits.V1);
    }

    /**
     * Decodes a request. Throws {@link WireException} for any message that is not exactly what the specification
     * allows. A receiver that announced lower limits passes them here, so that it enforces what it announced.
     */
    public static RequestEnvelope decodeRequest(byte[] data, Limits limits) {
        Fields fields = parse(data);
        String type = fields.string("t");
        long seq = fields.seq("seq");
        Request request;
        switch (type) {
            case "ping":
                fields.only("t", "seq");
                request = new Request.Ping();
                break;
            case "put": {
                String kind = fields.string("kind");
                switch (kind) {
                    case "pin": {
                        fields.only("t", "seq", "kind", "id", "lat", "lon", "title");
                        ItemId id = fields.id();
                        Coordinate at = fields.coordinate();
                        request = new Request.PutPin(id, at, fields.title(limits));
                        break;
                    }
                    case "marker": {
                        fields.only("t", "seq", "kind", "id", "lat", "lon", "title", "color");
                        MarkerColor color = null;
                        String text = fields.optionalString("color");
                        if (text != null) {
                            color = MarkerColor.fromWire(text);
                            if (color == null) {
                                throw new WireException(ErrorCode.INVALID_MESSAGE, "unknown marker color");
                            }
                        }
                        ItemId id = fields.id();
                        Coordinate at = fields.coordinate();
                        request = new Request.PutMarker(id, at, fields.title(limits), color);
                        break;
                    }
                    case "note": {
                        fields.only("t", "seq", "kind", "id", "lat", "lon", "text");
                        String text = fields.string("text");
                        TextRules.validateNote(text, limits);
                        ItemId id = fields.id();
                        request = new Request.PutNote(id, fields.coordinate(), text);
                        break;
                    }
                    default:
                        throw new WireException(ErrorCode.INVALID_MESSAGE, "field 'kind' must be pin, marker or note");
                }
                break;
            }
            case "move": {
                fields.only("t", "seq", "id", "lat", "lon");
                ItemId id = fields.id();
                request = new Request.Move(id, fields.coordinate());
                break;
            }
            case "delete":
                fields.only("t", "seq", "id");
                request = new Request.Delete(fields.id());
                break;
            case "delete_all":
                fields.only("t", "seq");
                request = new Request.DeleteAll();
                break;
            case "photo_begin": {
                fields.only("t", "seq", "id", "lat", "lon", "title", "type", "size", "sha256", "chunks");
                PhotoType photoType = PhotoType.fromWire(fields.string("type"));
                if (photoType == null) {
                    throw new WireException(ErrorCode.PHOTO_INVALID, "a photo is image/jpeg or image/png");
                }
                long size = fields.integer("size");
                long chunks = fields.integer("chunks");
                String digest = fields.string("sha256");
                if (size < 1 || chunks < 1) {
                    throw new WireException(ErrorCode.INVALID_MESSAGE, "fields 'size' and 'chunks' must be positive");
                }
                if (size > limits.photoBytes()) {
                    throw new WireException(ErrorCode.TOO_LARGE, "a photo is at most " + limits.photoBytes() + " bytes");
                }
                long chunkSize = IntegrationV1.MAX_CHUNK_BYTES;
                if (chunks != (size + chunkSize - 1) / chunkSize) {
                    throw new WireException(ErrorCode.INVALID_MESSAGE, "field 'chunks' does not match field 'size'");
                }
                if (!Hex.isFingerprint(digest)) {
                    throw new WireException(ErrorCode.INVALID_MESSAGE, "field 'sha256' is 64 lowercase hexadecimal digits");
                }
                ItemId id = fields.id();
                Coordinate at = fields.coordinate();
                request = new Request.PhotoBegin(
                        new Request.PhotoHeader(id, at, fields.title(limits), photoType, (int) size, digest, (int) chunks));
                break;
            }
            case "photo_chunk": {
                fields.only("t", "seq", "id", "index", "data");
                long index = fields.integer("index");
                if (index < 0 || index > 65534) {
                    throw new WireException(ErrorCode.INVALID_MESSAGE, "field 'index' is out of range");
                }
                String text = fields.string("data");
                byte[] chunk = decodeCanonicalBase64(text);
                if (chunk == null) {
                    throw new WireException(ErrorCode.INVALID_MESSAGE, "field 'data' must be canonical base64");
                }
                if (chunk.length == 0) {
                    throw new WireException(ErrorCode.INVALID_MESSAGE, "a photo chunk cannot be empty");
                }
                if (chunk.length > IntegrationV1.MAX_CHUNK_BYTES) {
                    throw new WireException(ErrorCode.TOO_LARGE, "a photo chunk is at most " + IntegrationV1.MAX_CHUNK_BYTES + " bytes");
                }
                request = new Request.PhotoChunk(fields.id(), (int) index, chunk);
                break;
            }
            case "photo_end":
                fields.only("t", "seq", "id");
                request = new Request.PhotoEnd(fields.id());
                break;
            case "track_append": {
                fields.only("t", "seq", "id", "title", "points");
                List<JsonValue> values = fields.array("points");
                if (values.size() < 1 || values.size() > IntegrationV1.MAX_POINTS_PER_MESSAGE) {
                    throw new WireException(
                            ErrorCode.INVALID_MESSAGE, "field 'points' has 1 to " + IntegrationV1.MAX_POINTS_PER_MESSAGE + " entries");
                }
                List<TrackPoint> points = new ArrayList<>(values.size());
                for (JsonValue value : values) {
                    Fields point = new Fields(value);
                    point.only("lat", "lon", "ts", "alt");
                    Double altitude = point.has("alt") ? point.number("alt") : null;
                    Coordinate coordinate = point.coordinate();
                    Long timestamp = point.has("ts") ? point.integer("ts") : null;
                    points.add(new TrackPoint(coordinate, timestamp, altitude));
                }
                ItemId id = fields.id();
                request = new Request.TrackAppend(id, fields.title(limits), points);
                break;
            }
            case "track_finish":
                fields.only("t", "seq", "id");
                request = new Request.TrackFinish(fields.id());
                break;
            default:
                throw new WireException(ErrorCode.INVALID_MESSAGE, "unknown message type");
        }
        return new RequestEnvelope(seq, request);
    }

    /**
     * Base64 with padding and nothing else: no white space, no line breaks and no stray bits in the last character, so
     * that one chunk has exactly one spelling.
     */
    private static byte[] decodeCanonicalBase64(String text) {
        for (int index = 0; index < text.length(); index++) {
            char character = text.charAt(index);
            boolean allowed = (character >= 'A' && character <= 'Z') || (character >= 'a' && character <= 'z')
                    || (character >= '0' && character <= '9') || character == '+' || character == '/' || character == '=';
            if (!allowed) {
                return null;
            }
        }
        if (text.length() % 4 != 0) {
            return null;
        }
        byte[] data;
        try {
            data = Base64.getDecoder().decode(text);
        } catch (IllegalArgumentException error) {
            return null;
        }
        return Base64.getEncoder().encodeToString(data).equals(text) ? data : null;
    }

    // Receiver to device

    public static byte[] encode(ReceiverMessage message) {
        Objects.requireNonNull(message, "message");
        if (message instanceof ReceiverMessage.WelcomeMessage welcomeMessage) {
            Welcome welcome = welcomeMessage.welcome();
            Limits limits = welcome.limits();
            return JsonWriter.encode(JsonWriter.object()
                    .put("t", "welcome")
                    .put("version", (long) welcome.version())
                    .put("last_seq", welcome.lastSeq())
                    .put("kinds", kinds(welcome.kinds()))
                    .put("limits", JsonWriter.object()
                            .put("items", (long) limits.items())
                            .put("photos", (long) limits.photos())
                            .put("photo_bytes", (long) limits.photoBytes())
                            .put("note_bytes", (long) limits.noteBytes())
                            .put("title_chars", (long) limits.titleScalars())
                            .put("track_points", (long) limits.trackPoints())
                            .put("open_tracks", (long) limits.openTracks())
                            .put("tracks", (long) limits.tracks())
                            .put("msgs_per_s", (long) limits.messagesPerSecond())
                            .put("msg_burst", (long) limits.messageBurst())
                            .put("points_per_s", (long) limits.pointsPerSecond())
                            .put("point_burst", (long) limits.pointBurst())
                            .put("disk_bytes", limits.diskBytes())
                            .build())
                    .build());
        }
        if (message instanceof ReceiverMessage.Ack ack) {
            return JsonWriter.encode(JsonWriter.object().put("t", "ack").put("seq", ack.seq()).put("dup", ack.duplicate()).build());
        }
        if (message instanceof ReceiverMessage.Error errorMessage) {
            ReceiverError error = errorMessage.error();
            JsonWriter.ObjectBuilder fields = JsonWriter.object().put("t", "error").put("code", error.code());
            if (error.seq() != null) {
                fields.put("seq", (long) error.seq());
            }
            if (error.lastSeq() != null) {
                fields.put("last_seq", (long) error.lastSeq());
            }
            if (error.retryAfterMs() != null) {
                fields.put("retry_after_ms", (long) error.retryAfterMs());
            }
            if (error.supported() != null) {
                fields.put("supported", integers(error.supported()));
            }
            if (error.fatal()) {
                fields.put("fatal", true);
            }
            return JsonWriter.encode(fields.build());
        }
        if (message instanceof ReceiverMessage.PairStatusMessage statusMessage) {
            PairStatus status = statusMessage.status();
            if (status instanceof PairStatus.Pending) {
                return JsonWriter.encode(JsonWriter.object().put("t", "pair_status").put("status", "pending").build());
            }
            if (status instanceof PairStatus.Denied) {
                return JsonWriter.encode(JsonWriter.object().put("t", "pair_status").put("status", "denied").build());
            }
            if (status instanceof PairStatus.Approved approved) {
                return JsonWriter.encode(JsonWriter.object()
                        .put("t", "pair_status")
                        .put("status", "approved")
                        .put("kinds", kinds(approved.kinds()))
                        .put("counter", approved.counter())
                        .build());
            }
        }
        throw new IllegalArgumentException("unknown message");
    }

    public static ReceiverMessage decodeReceiverMessage(byte[] data) {
        Fields fields = parse(data);
        switch (fields.string("t")) {
            case "welcome": {
                fields.only("t", "version", "last_seq", "kinds", "limits");
                long version = fields.integer("version");
                if (version < 1 || version > 65535) {
                    throw new WireException(ErrorCode.INVALID_MESSAGE, "field 'version' is out of range");
                }
                JsonValue limitsValue = fields.map().get("limits");
                if (limitsValue == null) {
                    throw new WireException(ErrorCode.INVALID_MESSAGE, "field 'limits' is missing");
                }
                long lastSeq = fields.nonNegative("last_seq");
                List<ItemKind> kinds = fields.kinds();
                return new ReceiverMessage.WelcomeMessage(new Welcome((int) version, lastSeq, kinds, decodeLimits(limitsValue)));
            }
            case "ack": {
                fields.only("t", "seq", "dup");
                long seq = fields.seq("seq");
                return new ReceiverMessage.Ack(seq, fields.bool("dup"));
            }
            case "error": {
                fields.only("t", "code", "seq", "last_seq", "retry_after_ms", "supported", "fatal");
                String code = fields.string("code");
                boolean codeIsValid = code.length() >= 1 && code.length() <= 64;
                for (int index = 0; index < code.length() && codeIsValid; index++) {
                    char character = code.charAt(index);
                    codeIsValid = (character >= 'a' && character <= 'z') || character == '_';
                }
                if (!codeIsValid) {
                    throw new WireException(ErrorCode.INVALID_MESSAGE, "field 'code' is lowercase letters and underscores");
                }
                List<JsonValue> versions = fields.has("supported") ? fields.array("supported") : null;
                // Empty is allowed: a receiver may have dropped every version this device could speak.
                if (versions != null && versions.size() > 16) {
                    throw new WireException(ErrorCode.INVALID_MESSAGE, "field 'supported' has at most 16 entries");
                }
                List<Integer> supported = null;
                if (versions != null) {
                    supported = new ArrayList<>();
                    for (JsonValue value : versions) {
                        supported.add(version(value));
                    }
                }
                Long seq = fields.has("seq") ? fields.seq("seq") : null;
                Long lastSeq = fields.has("last_seq") ? fields.nonNegative("last_seq") : null;
                Long retry = fields.has("retry_after_ms") ? fields.nonNegative("retry_after_ms") : null;
                boolean fatal = fields.has("fatal") && fields.bool("fatal");
                return new ReceiverMessage.Error(new ReceiverError(code, seq, lastSeq, retry, supported, fatal));
            }
            case "pair_status":
                switch (fields.string("status")) {
                    case "pending":
                        fields.only("t", "status");
                        return new ReceiverMessage.PairStatusMessage(new PairStatus.Pending());
                    case "denied":
                        fields.only("t", "status");
                        return new ReceiverMessage.PairStatusMessage(new PairStatus.Denied());
                    case "approved": {
                        fields.only("t", "status", "kinds", "counter");
                        List<ItemKind> kinds = fields.kinds();
                        return new ReceiverMessage.PairStatusMessage(new PairStatus.Approved(kinds, fields.nonNegative("counter")));
                    }
                    default:
                        throw new WireException(ErrorCode.INVALID_MESSAGE, "unknown pairing status");
                }
            default:
                throw new WireException(ErrorCode.INVALID_MESSAGE, "unknown message type");
        }
    }

    static int version(JsonValue value) {
        if (value instanceof JsonValue.Integer number && number.value() >= 1 && number.value() <= 65535) {
            return (int) number.value();
        }
        throw new WireException(ErrorCode.INVALID_MESSAGE, "a version is an integer within 1...65535");
    }

    private static Limits decodeLimits(JsonValue value) {
        Fields fields = new Fields(value);
        fields.only(
                "items", "photos", "photo_bytes", "note_bytes", "title_chars", "track_points", "open_tracks", "tracks",
                "msgs_per_s", "msg_burst", "points_per_s", "point_burst", "disk_bytes");
        // A device may batch this many points, so a smaller burst could never be satisfied.
        if (fields.positive("point_burst") < IntegrationV1.MAX_POINTS_PER_MESSAGE) {
            throw new WireException(ErrorCode.INVALID_MESSAGE, "field 'point_burst' is below the maximum batch size");
        }
        return new Limits(
                fields.positiveInt("items"),
                fields.positiveInt("photos"),
                fields.positiveInt("photo_bytes"),
                fields.positiveInt("note_bytes"),
                fields.positiveInt("title_chars"),
                fields.positiveInt("track_points"),
                fields.positiveInt("open_tracks"),
                fields.positiveInt("tracks"),
                fields.positiveInt("msgs_per_s"),
                fields.positiveInt("msg_burst"),
                fields.positiveInt("points_per_s"),
                fields.positiveInt("point_burst"),
                fields.positive("disk_bytes"));
    }

    private static Fields parse(byte[] data) {
        if (data.length > IntegrationV1.MAX_PLAINTEXT_LENGTH) {
            throw new WireException(ErrorCode.TOO_LARGE, "a message is at most 65519 bytes");
        }
        JsonValue value;
        try {
            value = StrictJson.parse(data);
        } catch (JsonParseException error) {
            throw new WireException(ErrorCode.INVALID_MESSAGE, "the message is not valid strict JSON");
        }
        return new Fields(value);
    }

    /** Reads a JSON object against an explicit list of fields. */
    private static final class Fields {
        private final Map<String, JsonValue> map;

        Fields(JsonValue value) {
            if (!(value instanceof JsonValue.Object object)) {
                throw new WireException(ErrorCode.INVALID_MESSAGE, "a message is a JSON object");
            }
            map = object.fields();
        }

        Map<String, JsonValue> map() {
            return map;
        }

        boolean has(String key) {
            return map.containsKey(key);
        }

        void only(String... allowed) {
            Set<String> names = Set.of(allowed);
            for (String key : map.keySet()) {
                if (!names.contains(key)) {
                    throw new WireException(ErrorCode.INVALID_MESSAGE, "unknown field");
                }
            }
        }

        String string(String key) {
            if (map.get(key) instanceof JsonValue.Text text) {
                return text.value();
            }
            throw new WireException(ErrorCode.INVALID_MESSAGE, "field '" + key + "' must be a string");
        }

        String optionalString(String key) {
            return has(key) ? string(key) : null;
        }

        long integer(String key) {
            if (map.get(key) instanceof JsonValue.Integer number) {
                return number.value();
            }
            throw new WireException(ErrorCode.INVALID_MESSAGE, "field '" + key + "' must be an integer");
        }

        double number(String key) {
            JsonValue value = map.get(key);
            if (value instanceof JsonValue.Number number) {
                return number.value();
            }
            if (value instanceof JsonValue.Integer integer) {
                return integer.value();
            }
            throw new WireException(ErrorCode.INVALID_MESSAGE, "field '" + key + "' must be a number");
        }

        boolean bool(String key) {
            if (map.get(key) instanceof JsonValue.Bool flag) {
                return flag.value();
            }
            throw new WireException(ErrorCode.INVALID_MESSAGE, "field '" + key + "' must be true or false");
        }

        List<JsonValue> array(String key) {
            if (map.get(key) instanceof JsonValue.Array array) {
                return array.values();
            }
            throw new WireException(ErrorCode.INVALID_MESSAGE, "field '" + key + "' must be an array");
        }

        long seq(String key) {
            long value = integer(key);
            if (value < 1 || value > IntegrationV1.MAX_SEQUENCE) {
                throw new WireException(ErrorCode.INVALID_MESSAGE, "a sequence number is within 1...2^53-1");
            }
            return value;
        }

        long nonNegative(String key) {
            long value = integer(key);
            if (value < 0 || value > IntegrationV1.MAX_SEQUENCE) {
                throw new WireException(ErrorCode.INVALID_MESSAGE, "field '" + key + "' is out of range");
            }
            return value;
        }

        long positive(String key) {
            long number = integer(key);
            if (number < 1 || number > 1L << 40) {
                throw new WireException(ErrorCode.INVALID_MESSAGE, "a limit is out of range");
            }
            return number;
        }

        int positiveInt(String key) {
            return (int) Math.min(positive(key), Integer.MAX_VALUE);
        }

        ItemId id() {
            return new ItemId(string("id"));
        }

        Coordinate coordinate() {
            double latitude = number("lat");
            double longitude = number("lon");
            return new Coordinate(latitude, longitude);
        }

        String title(Limits limits) {
            String title = optionalString("title");
            if (title == null) {
                return null;
            }
            TextRules.validateTitle(title, limits);
            return title;
        }

        List<Integer> versions() {
            List<JsonValue> values = array("versions");
            if (values.size() < 1 || values.size() > 16) {
                throw new WireException(ErrorCode.INVALID_MESSAGE, "field 'versions' must have 1 to 16 entries");
            }
            List<Integer> versions = new ArrayList<>();
            for (JsonValue value : values) {
                versions.add(version(value));
            }
            return versions;
        }

        List<ItemKind> kinds() {
            List<ItemKind> kinds = new ArrayList<>();
            for (JsonValue value : array("kinds")) {
                ItemKind kind = value instanceof JsonValue.Text text ? ItemKind.fromWire(text.value()) : null;
                if (kind == null || kinds.contains(kind)) {
                    throw new WireException(ErrorCode.INVALID_MESSAGE, "field 'kinds' lists item kinds without repeats");
                }
                kinds.add(kind);
            }
            return kinds;
        }

        String sdkName() {
            String name = string("sdk");
            boolean allowed = name.length() >= 1 && name.length() <= 64;
            for (int index = 0; index < name.length() && allowed; index++) {
                char character = name.charAt(index);
                allowed = (character >= 'a' && character <= 'z') || (character >= 'A' && character <= 'Z')
                        || (character >= '0' && character <= '9') || character == '.' || character == '_' || character == '/'
                        || character == '-';
            }
            if (!allowed) {
                throw new WireException(ErrorCode.INVALID_MESSAGE, "field 'sdk' is 1 to 64 ASCII letters, digits or . _ / -");
            }
            return name;
        }
    }
}
