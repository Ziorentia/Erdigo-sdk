using System;
using System.Collections.Generic;
using System.Linq;

namespace Erdigo.Core;

/// <summary>
/// A request from a device to a receiver. Decoding is an allow-list: unknown types and unknown fields are rejected,
/// and nothing about identity is ever taken from the message.
/// </summary>
public abstract class Request : IEquatable<Request>
{
    private protected Request()
    {
    }

    public sealed class Ping : Request
    {
        public static Ping Instance { get; } = new();
    }

    public sealed class PutPin : Request
    {
        public PutPin(ItemId id, Coordinate at, string? title)
        {
            Id = id;
            At = at;
            Title = title;
        }

        public ItemId Id { get; }

        public Coordinate At { get; }

        public string? Title { get; }
    }

    public sealed class PutMarker : Request
    {
        public PutMarker(ItemId id, Coordinate at, string? title, MarkerColor? color)
        {
            Id = id;
            At = at;
            Title = title;
            Color = color;
        }

        public ItemId Id { get; }

        public Coordinate At { get; }

        public string? Title { get; }

        public MarkerColor? Color { get; }
    }

    public sealed class PutNote : Request
    {
        public PutNote(ItemId id, Coordinate at, string text)
        {
            Id = id;
            At = at;
            Text = text;
        }

        public ItemId Id { get; }

        public Coordinate At { get; }

        public string Text { get; }
    }

    public sealed class Move : Request
    {
        public Move(ItemId id, Coordinate to)
        {
            Id = id;
            To = to;
        }

        public ItemId Id { get; }

        public Coordinate To { get; }
    }

    public sealed class Delete : Request
    {
        public Delete(ItemId id) => Id = id;

        public ItemId Id { get; }
    }

    public sealed class DeleteAll : Request
    {
        public static DeleteAll Instance { get; } = new();
    }

    public sealed class PhotoBegin : Request
    {
        public PhotoBegin(PhotoHeader header) => Header = header;

        public PhotoHeader Header { get; }
    }

    public sealed class PhotoChunk : Request
    {
        public PhotoChunk(ItemId id, int index, byte[] data)
        {
            Id = id;
            Index = index;
            Data = data;
        }

        public ItemId Id { get; }

        public int Index { get; }

        public byte[] Data { get; }
    }

    public sealed class PhotoEnd : Request
    {
        public PhotoEnd(ItemId id) => Id = id;

        public ItemId Id { get; }
    }

    public sealed class TrackAppend : Request
    {
        public TrackAppend(ItemId id, string? title, IReadOnlyList<TrackPoint> points)
        {
            Id = id;
            Title = title;
            Points = points;
        }

        public ItemId Id { get; }

        public string? Title { get; }

        public IReadOnlyList<TrackPoint> Points { get; }
    }

    public sealed class TrackFinish : Request
    {
        public TrackFinish(ItemId id) => Id = id;

        public ItemId Id { get; }
    }

    /// <summary>Two requests are equal when they encode to the same bytes.</summary>
    public bool Equals(Request? other) =>
        other is not null && Wire.Encode(new RequestEnvelope(1, this)).AsSpan().SequenceEqual(Wire.Encode(new RequestEnvelope(1, other)));

    public override bool Equals(object? obj) => Equals(obj as Request);

    public override int GetHashCode() => GetType().GetHashCode();
}

public sealed record PhotoHeader(ItemId Id, Coordinate At, string? Title, PhotoType Type, int Size, string Sha256, int Chunks);

public sealed record RequestEnvelope(ulong Seq, Request Request);

/// <summary>The first message on a connection, sent by the device.</summary>
public sealed class Hello
{
    public Hello(IReadOnlyList<int> versions, string sdk)
    {
        Versions = versions;
        Sdk = sdk;
    }

    public IReadOnlyList<int> Versions { get; }

    public string Sdk { get; }
}

/// <summary>The payload of the third pairing message.</summary>
public sealed class PairRequest
{
    public PairRequest(IReadOnlyList<int> versions, string sdk, string name, IReadOnlyList<ItemKind> kinds)
    {
        Versions = versions;
        Sdk = sdk;
        Name = name;
        Kinds = kinds;
    }

    public IReadOnlyList<int> Versions { get; }

    public string Sdk { get; }

    public string Name { get; }

    public IReadOnlyList<ItemKind> Kinds { get; }
}

public sealed class Welcome : IEquatable<Welcome>
{
    public Welcome(int version, ulong lastSeq, IReadOnlyList<ItemKind> kinds, Limits limits)
    {
        Version = version;
        LastSeq = lastSeq;
        Kinds = kinds;
        Limits = limits;
    }

    public int Version { get; }

    public ulong LastSeq { get; }

    public IReadOnlyList<ItemKind> Kinds { get; }

    public Limits Limits { get; }

    public bool Equals(Welcome? other) =>
        other is not null && Version == other.Version && LastSeq == other.LastSeq && Kinds.SequenceEqual(other.Kinds) && Limits == other.Limits;

    public override bool Equals(object? obj) => Equals(obj as Welcome);

    public override int GetHashCode() => HashCode.Combine(Version, LastSeq);
}

public sealed class ReceiverError : IEquatable<ReceiverError>
{
    public ReceiverError(string code, ulong? seq = null, ulong? lastSeq = null, ulong? retryAfterMs = null, IReadOnlyList<int>? supported = null, bool fatal = false)
    {
        Code = code;
        Seq = seq;
        LastSeq = lastSeq;
        RetryAfterMs = retryAfterMs;
        Supported = supported;
        Fatal = fatal;
    }

    public ReceiverError(ErrorCode code, ulong? seq = null, ulong? lastSeq = null, ulong? retryAfterMs = null, IReadOnlyList<int>? supported = null, bool fatal = false)
        : this(code.ToWire(), seq, lastSeq, retryAfterMs, supported, fatal)
    {
    }

    /// <summary>Kept as text: a device must treat a code it does not know as a permanent failure.</summary>
    public string Code { get; }

    public ulong? Seq { get; }

    public ulong? LastSeq { get; }

    public ulong? RetryAfterMs { get; }

    public IReadOnlyList<int>? Supported { get; }

    public bool Fatal { get; }

    public bool Equals(ReceiverError? other) =>
        other is not null && Code == other.Code && Seq == other.Seq && LastSeq == other.LastSeq && RetryAfterMs == other.RetryAfterMs
        && Fatal == other.Fatal
        && (Supported is null ? other.Supported is null : other.Supported is not null && Supported.SequenceEqual(other.Supported));

    public override bool Equals(object? obj) => Equals(obj as ReceiverError);

    public override int GetHashCode() => HashCode.Combine(Code, Seq);
}

public abstract class PairStatus : IEquatable<PairStatus>
{
    private protected PairStatus()
    {
    }

    public sealed class Pending : PairStatus
    {
        public static Pending Instance { get; } = new();
    }

    /// <summary><see cref="Counter"/> is the highest connect counter the receiver has accepted from this device.</summary>
    public sealed class Approved : PairStatus
    {
        public Approved(IReadOnlyList<ItemKind> kinds, ulong counter)
        {
            Kinds = kinds;
            Counter = counter;
        }

        public IReadOnlyList<ItemKind> Kinds { get; }

        public ulong Counter { get; }
    }

    public sealed class Denied : PairStatus
    {
        public static Denied Instance { get; } = new();
    }

    public bool Equals(PairStatus? other) => other switch
    {
        null => false,
        Approved approved => this is Approved mine && mine.Counter == approved.Counter && mine.Kinds.SequenceEqual(approved.Kinds),
        _ => GetType() == other.GetType(),
    };

    public override bool Equals(object? obj) => Equals(obj as PairStatus);

    public override int GetHashCode() => GetType().GetHashCode();
}

public abstract class ReceiverMessage : IEquatable<ReceiverMessage>
{
    private protected ReceiverMessage()
    {
    }

    public sealed class WelcomeMessage : ReceiverMessage
    {
        public WelcomeMessage(Welcome welcome) => Welcome = welcome;

        public Welcome Welcome { get; }
    }

    public sealed class Ack : ReceiverMessage
    {
        public Ack(ulong seq, bool duplicate)
        {
            Seq = seq;
            Duplicate = duplicate;
        }

        public ulong Seq { get; }

        public bool Duplicate { get; }
    }

    public sealed class Error : ReceiverMessage
    {
        public Error(ReceiverError error) => Value = error;

        public ReceiverError Value { get; }
    }

    public sealed class PairStatusMessage : ReceiverMessage
    {
        public PairStatusMessage(PairStatus status) => Status = status;

        public PairStatus Status { get; }
    }

    /// <summary>Two messages are equal when they encode to the same bytes.</summary>
    public bool Equals(ReceiverMessage? other) => other is not null && Wire.Encode(this).AsSpan().SequenceEqual(Wire.Encode(other));

    public override bool Equals(object? obj) => Equals(obj as ReceiverMessage);

    public override int GetHashCode() => GetType().GetHashCode();
}

/// <summary>Reads a JSON object against an explicit list of fields.</summary>
internal sealed class Fields
{
    public Fields(JsonValue value)
    {
        if (value is not JsonValue.Object map)
        {
            throw new WireException(ErrorCode.InvalidMessage, "a message is a JSON object");
        }
        Map = map.Fields;
    }

    public IReadOnlyDictionary<string, JsonValue> Map { get; }

    public bool Has(string key) => Map.ContainsKey(key);

    public void Only(params string[] allowed)
    {
        foreach (var key in Map.Keys)
        {
            if (!allowed.Contains(key, StringComparer.Ordinal))
            {
                throw new WireException(ErrorCode.InvalidMessage, "unknown field");
            }
        }
    }

    public string String(string key) =>
        Map.TryGetValue(key, out var value) && value is JsonValue.String text
            ? text.Value
            : throw new WireException(ErrorCode.InvalidMessage, $"field '{key}' must be a string");

    public string? OptionalString(string key) => Has(key) ? String(key) : null;

    public long Integer(string key) =>
        Map.TryGetValue(key, out var value) && value is JsonValue.Integer number
            ? number.Value
            : throw new WireException(ErrorCode.InvalidMessage, $"field '{key}' must be an integer");

    public long? OptionalInteger(string key) => Has(key) ? Integer(key) : null;

    public double Number(string key) => Map.TryGetValue(key, out var value)
        ? value switch
        {
            JsonValue.Number number => number.Value,
            JsonValue.Integer integer => integer.Value,
            _ => throw new WireException(ErrorCode.InvalidMessage, $"field '{key}' must be a number"),
        }
        : throw new WireException(ErrorCode.InvalidMessage, $"field '{key}' must be a number");

    public bool Bool(string key) =>
        Map.TryGetValue(key, out var value) && value is JsonValue.Bool flag
            ? flag.Value
            : throw new WireException(ErrorCode.InvalidMessage, $"field '{key}' must be true or false");

    public IReadOnlyList<JsonValue> Array(string key) =>
        Map.TryGetValue(key, out var value) && value is JsonValue.Array array
            ? array.Values
            : throw new WireException(ErrorCode.InvalidMessage, $"field '{key}' must be an array");

    public ulong Seq(string key = "seq")
    {
        var value = Integer(key);
        if (value < 1 || (ulong)value > IntegrationV1.MaxSequence)
        {
            throw new WireException(ErrorCode.InvalidMessage, "a sequence number is within 1...2^53-1");
        }
        return (ulong)value;
    }

    public ulong NonNegative(string key)
    {
        var value = Integer(key);
        if (value < 0 || (ulong)value > IntegrationV1.MaxSequence)
        {
            throw new WireException(ErrorCode.InvalidMessage, $"field '{key}' is out of range");
        }
        return (ulong)value;
    }

    public ItemId Id() => new(String("id"));

    public Coordinate Coordinate()
    {
        var latitude = Number("lat");
        var longitude = Number("lon");
        return new Coordinate(latitude, longitude);
    }

    public string? Title(Limits limits)
    {
        var title = OptionalString("title");
        if (title is null)
        {
            return null;
        }
        TextRules.ValidateTitle(title, limits);
        return title;
    }

    public IReadOnlyList<int> Versions()
    {
        var values = Array("versions");
        if (values.Count is < 1 or > 16)
        {
            throw new WireException(ErrorCode.InvalidMessage, "field 'versions' must have 1 to 16 entries");
        }
        return values.Select(Wire.Version).ToArray();
    }

    public IReadOnlyList<ItemKind> Kinds()
    {
        var kinds = new List<ItemKind>();
        foreach (var value in Array("kinds"))
        {
            if (value is not JsonValue.String text || ItemKinds.FromWire(text.Value) is not { } kind || kinds.Contains(kind))
            {
                throw new WireException(ErrorCode.InvalidMessage, "field 'kinds' lists item kinds without repeats");
            }
            kinds.Add(kind);
        }
        return kinds;
    }

    public string SdkName()
    {
        var name = String("sdk");
        var allowed = name.All(character =>
            character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '/' or '-');
        if (name.Length is < 1 or > 64 || !allowed)
        {
            throw new WireException(ErrorCode.InvalidMessage, "field 'sdk' is 1 to 64 ASCII letters, digits or . _ / -");
        }
        return name;
    }
}

/// <summary>Encodes and decodes every integration-v1 message.</summary>
public static class Wire
{
    // Device to receiver

    public static byte[] Encode(Hello hello)
    {
        ArgumentNullException.ThrowIfNull(hello);
        return JsonWriter.Encode(JsonWriter.Value.Object(new[]
        {
            ("t", JsonWriter.Value.Of("hello")),
            ("versions", JsonWriter.Value.Array(hello.Versions.Select(version => JsonWriter.Value.Of((long)version)))),
            ("sdk", JsonWriter.Value.Of(hello.Sdk)),
        }));
    }

    public static Hello DecodeHello(ReadOnlySpan<byte> data)
    {
        var fields = Parse(data);
        if (fields.String("t") != "hello")
        {
            throw new WireException(ErrorCode.InvalidMessage, "the first message must be hello");
        }
        fields.Only("t", "versions", "sdk");
        var versions = fields.Versions();
        return new Hello(versions, fields.SdkName());
    }

    public static byte[] Encode(PairRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return JsonWriter.Encode(JsonWriter.Value.Object(new[]
        {
            ("t", JsonWriter.Value.Of("pair_request")),
            ("versions", JsonWriter.Value.Array(request.Versions.Select(version => JsonWriter.Value.Of((long)version)))),
            ("sdk", JsonWriter.Value.Of(request.Sdk)),
            ("name", JsonWriter.Value.Of(request.Name)),
            ("kinds", JsonWriter.Value.Array(request.Kinds.Select(kind => JsonWriter.Value.Of(kind.ToWire())))),
        }));
    }

    public static PairRequest DecodePairRequest(ReadOnlySpan<byte> data)
    {
        var fields = Parse(data);
        if (fields.String("t") != "pair_request")
        {
            throw new WireException(ErrorCode.InvalidMessage, "expected pair_request");
        }
        fields.Only("t", "versions", "sdk", "name", "kinds");
        var name = fields.String("name");
        if (System.Text.Encoding.UTF8.GetByteCount(name) > 256)
        {
            throw new WireException(ErrorCode.TooLarge, "a device name is too long");
        }
        var kinds = fields.Kinds();
        if (kinds.Count == 0)
        {
            throw new WireException(ErrorCode.InvalidMessage, "field 'kinds' cannot be empty");
        }
        var versions = fields.Versions();
        return new PairRequest(versions, fields.SdkName(), name, kinds);
    }

    public static byte[] Encode(RequestEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var fields = new List<(string, JsonWriter.Value)>();
        void Put(string type)
        {
            fields.Add(("t", JsonWriter.Value.Of(type)));
            fields.Add(("seq", JsonWriter.Value.Of((long)envelope.Seq)));
        }
        void PutCoordinate(Coordinate coordinate)
        {
            fields.Add(("lat", JsonWriter.Value.Of(coordinate.Latitude)));
            fields.Add(("lon", JsonWriter.Value.Of(coordinate.Longitude)));
        }
        void PutId(ItemId id) => fields.Add(("id", JsonWriter.Value.Of(id.Value)));

        switch (envelope.Request)
        {
            case Request.Ping:
                Put("ping");
                break;
            case Request.PutPin pin:
                Put("put");
                fields.Add(("kind", JsonWriter.Value.Of("pin")));
                PutId(pin.Id);
                PutCoordinate(pin.At);
                if (pin.Title is not null)
                {
                    fields.Add(("title", JsonWriter.Value.Of(pin.Title)));
                }
                break;
            case Request.PutMarker marker:
                Put("put");
                fields.Add(("kind", JsonWriter.Value.Of("marker")));
                PutId(marker.Id);
                PutCoordinate(marker.At);
                if (marker.Title is not null)
                {
                    fields.Add(("title", JsonWriter.Value.Of(marker.Title)));
                }
                if (marker.Color is { } color)
                {
                    fields.Add(("color", JsonWriter.Value.Of(color.ToWire())));
                }
                break;
            case Request.PutNote note:
                Put("put");
                fields.Add(("kind", JsonWriter.Value.Of("note")));
                PutId(note.Id);
                PutCoordinate(note.At);
                fields.Add(("text", JsonWriter.Value.Of(note.Text)));
                break;
            case Request.Move move:
                Put("move");
                PutId(move.Id);
                PutCoordinate(move.To);
                break;
            case Request.Delete delete:
                Put("delete");
                PutId(delete.Id);
                break;
            case Request.DeleteAll:
                Put("delete_all");
                break;
            case Request.PhotoBegin begin:
                var header = begin.Header;
                Put("photo_begin");
                PutId(header.Id);
                PutCoordinate(header.At);
                if (header.Title is not null)
                {
                    fields.Add(("title", JsonWriter.Value.Of(header.Title)));
                }
                fields.Add(("type", JsonWriter.Value.Of(header.Type.ToWire())));
                fields.Add(("size", JsonWriter.Value.Of((long)header.Size)));
                fields.Add(("sha256", JsonWriter.Value.Of(header.Sha256)));
                fields.Add(("chunks", JsonWriter.Value.Of((long)header.Chunks)));
                break;
            case Request.PhotoChunk chunk:
                Put("photo_chunk");
                PutId(chunk.Id);
                fields.Add(("index", JsonWriter.Value.Of((long)chunk.Index)));
                fields.Add(("data", JsonWriter.Value.Of(Convert.ToBase64String(chunk.Data))));
                break;
            case Request.PhotoEnd end:
                Put("photo_end");
                PutId(end.Id);
                break;
            case Request.TrackAppend append:
                Put("track_append");
                PutId(append.Id);
                if (append.Title is not null)
                {
                    fields.Add(("title", JsonWriter.Value.Of(append.Title)));
                }
                fields.Add(("points", JsonWriter.Value.Array(append.Points.Select(Encode))));
                break;
            case Request.TrackFinish finish:
                Put("track_finish");
                PutId(finish.Id);
                break;
            default:
                throw new ArgumentException("unknown request", nameof(envelope));
        }
        return JsonWriter.Encode(JsonWriter.Value.Object(fields));
    }

    private static JsonWriter.Value Encode(TrackPoint point)
    {
        var fields = new List<(string, JsonWriter.Value)>
        {
            ("lat", JsonWriter.Value.Of(point.Coordinate.Latitude)),
            ("lon", JsonWriter.Value.Of(point.Coordinate.Longitude)),
        };
        if (point.TimestampMs is { } timestamp)
        {
            fields.Add(("ts", JsonWriter.Value.Of(timestamp)));
        }
        if (point.AltitudeM is { } altitude)
        {
            fields.Add(("alt", JsonWriter.Value.Of(altitude)));
        }
        return JsonWriter.Value.Object(fields);
    }

    /// <summary>
    /// Decodes a request. Throws <see cref="WireException"/> for any message that is not exactly what the
    /// specification allows. A receiver that announced lower limits passes them here, so that it enforces what it
    /// announced.
    /// </summary>
    public static RequestEnvelope DecodeRequest(ReadOnlySpan<byte> data, Limits? limits = null)
    {
        limits ??= Limits.V1;
        var fields = Parse(data);
        var type = fields.String("t");
        var seq = fields.Seq();
        Request request;

        switch (type)
        {
            case "ping":
                fields.Only("t", "seq");
                request = Request.Ping.Instance;
                break;
            case "put":
                var kind = fields.String("kind");
                switch (kind)
                {
                    case "pin":
                    {
                        fields.Only("t", "seq", "kind", "id", "lat", "lon", "title");
                        var id = fields.Id();
                        var at = fields.Coordinate();
                        request = new Request.PutPin(id, at, fields.Title(limits));
                        break;
                    }
                    case "marker":
                    {
                        fields.Only("t", "seq", "kind", "id", "lat", "lon", "title", "color");
                        MarkerColor? color = null;
                        if (fields.OptionalString("color") is { } text)
                        {
                            color = MarkerColors.FromWire(text) ?? throw new WireException(ErrorCode.InvalidMessage, "unknown marker color");
                        }
                        var id = fields.Id();
                        var at = fields.Coordinate();
                        request = new Request.PutMarker(id, at, fields.Title(limits), color);
                        break;
                    }
                    case "note":
                    {
                        fields.Only("t", "seq", "kind", "id", "lat", "lon", "text");
                        var text = fields.String("text");
                        TextRules.ValidateNote(text, limits);
                        var id = fields.Id();
                        request = new Request.PutNote(id, fields.Coordinate(), text);
                        break;
                    }
                    default:
                        throw new WireException(ErrorCode.InvalidMessage, "field 'kind' must be pin, marker or note");
                }
                break;
            case "move":
            {
                fields.Only("t", "seq", "id", "lat", "lon");
                var id = fields.Id();
                request = new Request.Move(id, fields.Coordinate());
                break;
            }
            case "delete":
                fields.Only("t", "seq", "id");
                request = new Request.Delete(fields.Id());
                break;
            case "delete_all":
                fields.Only("t", "seq");
                request = Request.DeleteAll.Instance;
                break;
            case "photo_begin":
            {
                fields.Only("t", "seq", "id", "lat", "lon", "title", "type", "size", "sha256", "chunks");
                var photoType = PhotoTypes.FromWire(fields.String("type"))
                    ?? throw new WireException(ErrorCode.PhotoInvalid, "a photo is image/jpeg or image/png");
                var size = fields.Integer("size");
                var chunks = fields.Integer("chunks");
                var digest = fields.String("sha256");
                if (size < 1 || chunks < 1)
                {
                    throw new WireException(ErrorCode.InvalidMessage, "fields 'size' and 'chunks' must be positive");
                }
                if (size > limits.PhotoBytes)
                {
                    throw new WireException(ErrorCode.TooLarge, $"a photo is at most {limits.PhotoBytes} bytes");
                }
                long chunkSize = IntegrationV1.MaxChunkBytes;
                if (chunks != (size + chunkSize - 1) / chunkSize)
                {
                    throw new WireException(ErrorCode.InvalidMessage, "field 'chunks' does not match field 'size'");
                }
                if (!Hex.IsFingerprint(digest))
                {
                    throw new WireException(ErrorCode.InvalidMessage, "field 'sha256' is 64 lowercase hexadecimal digits");
                }
                var id = fields.Id();
                var at = fields.Coordinate();
                request = new Request.PhotoBegin(new PhotoHeader(id, at, fields.Title(limits), photoType, (int)size, digest, (int)chunks));
                break;
            }
            case "photo_chunk":
            {
                fields.Only("t", "seq", "id", "index", "data");
                var index = fields.Integer("index");
                if (index is < 0 or > 65534)
                {
                    throw new WireException(ErrorCode.InvalidMessage, "field 'index' is out of range");
                }
                var text = fields.String("data");
                var chunk = DecodeCanonicalBase64(text)
                    ?? throw new WireException(ErrorCode.InvalidMessage, "field 'data' must be canonical base64");
                if (chunk.Length == 0)
                {
                    throw new WireException(ErrorCode.InvalidMessage, "a photo chunk cannot be empty");
                }
                if (chunk.Length > IntegrationV1.MaxChunkBytes)
                {
                    throw new WireException(ErrorCode.TooLarge, $"a photo chunk is at most {IntegrationV1.MaxChunkBytes} bytes");
                }
                request = new Request.PhotoChunk(fields.Id(), (int)index, chunk);
                break;
            }
            case "photo_end":
                fields.Only("t", "seq", "id");
                request = new Request.PhotoEnd(fields.Id());
                break;
            case "track_append":
            {
                fields.Only("t", "seq", "id", "title", "points");
                var values = fields.Array("points");
                if (values.Count < 1 || values.Count > IntegrationV1.MaxPointsPerMessage)
                {
                    throw new WireException(ErrorCode.InvalidMessage, $"field 'points' has 1 to {IntegrationV1.MaxPointsPerMessage} entries");
                }
                var points = new List<TrackPoint>(values.Count);
                foreach (var value in values)
                {
                    var point = new Fields(value);
                    point.Only("lat", "lon", "ts", "alt");
                    double? altitude = point.Has("alt") ? point.Number("alt") : null;
                    var coordinate = point.Coordinate();
                    var timestamp = point.OptionalInteger("ts");
                    points.Add(new TrackPoint(coordinate, timestamp, altitude));
                }
                var id = fields.Id();
                request = new Request.TrackAppend(id, fields.Title(limits), points);
                break;
            }
            case "track_finish":
                fields.Only("t", "seq", "id");
                request = new Request.TrackFinish(fields.Id());
                break;
            default:
                throw new WireException(ErrorCode.InvalidMessage, "unknown message type");
        }
        return new RequestEnvelope(seq, request);
    }

    /// <summary>
    /// Base64 with padding and nothing else: no white space, no line breaks and no stray bits in the last character,
    /// so that one chunk has exactly one spelling.
    /// </summary>
    private static byte[]? DecodeCanonicalBase64(string text)
    {
        foreach (var character in text)
        {
            var allowed = character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/' or '=';
            if (!allowed)
            {
                return null;
            }
        }
        if (text.Length % 4 != 0)
        {
            return null;
        }
        var buffer = new byte[text.Length / 4 * 3];
        if (!Convert.TryFromBase64String(text, buffer, out var written))
        {
            return null;
        }
        var data = buffer.AsSpan(0, written).ToArray();
        return Convert.ToBase64String(data) == text ? data : null;
    }

    // Receiver to device

    public static byte[] Encode(ReceiverMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        switch (message)
        {
            case ReceiverMessage.WelcomeMessage welcomeMessage:
            {
                var welcome = welcomeMessage.Welcome;
                var limits = welcome.Limits;
                return JsonWriter.Encode(JsonWriter.Value.Object(new[]
                {
                    ("t", JsonWriter.Value.Of("welcome")),
                    ("version", JsonWriter.Value.Of((long)welcome.Version)),
                    ("last_seq", JsonWriter.Value.Of((long)welcome.LastSeq)),
                    ("kinds", JsonWriter.Value.Array(welcome.Kinds.Select(kind => JsonWriter.Value.Of(kind.ToWire())))),
                    ("limits", JsonWriter.Value.Object(new[]
                    {
                        ("items", JsonWriter.Value.Of((long)limits.Items)),
                        ("photos", JsonWriter.Value.Of((long)limits.Photos)),
                        ("photo_bytes", JsonWriter.Value.Of((long)limits.PhotoBytes)),
                        ("note_bytes", JsonWriter.Value.Of((long)limits.NoteBytes)),
                        ("title_chars", JsonWriter.Value.Of((long)limits.TitleScalars)),
                        ("track_points", JsonWriter.Value.Of((long)limits.TrackPoints)),
                        ("open_tracks", JsonWriter.Value.Of((long)limits.OpenTracks)),
                        ("tracks", JsonWriter.Value.Of((long)limits.Tracks)),
                        ("msgs_per_s", JsonWriter.Value.Of((long)limits.MessagesPerSecond)),
                        ("msg_burst", JsonWriter.Value.Of((long)limits.MessageBurst)),
                        ("points_per_s", JsonWriter.Value.Of((long)limits.PointsPerSecond)),
                        ("point_burst", JsonWriter.Value.Of((long)limits.PointBurst)),
                        ("disk_bytes", JsonWriter.Value.Of(limits.DiskBytes)),
                    })),
                }));
            }
            case ReceiverMessage.Ack ack:
                return JsonWriter.Encode(JsonWriter.Value.Object(new[]
                {
                    ("t", JsonWriter.Value.Of("ack")),
                    ("seq", JsonWriter.Value.Of((long)ack.Seq)),
                    ("dup", JsonWriter.Value.Of(ack.Duplicate)),
                }));
            case ReceiverMessage.Error errorMessage:
            {
                var error = errorMessage.Value;
                var fields = new List<(string, JsonWriter.Value)>
                {
                    ("t", JsonWriter.Value.Of("error")),
                    ("code", JsonWriter.Value.Of(error.Code)),
                };
                if (error.Seq is { } seq)
                {
                    fields.Add(("seq", JsonWriter.Value.Of((long)seq)));
                }
                if (error.LastSeq is { } lastSeq)
                {
                    fields.Add(("last_seq", JsonWriter.Value.Of((long)lastSeq)));
                }
                if (error.RetryAfterMs is { } retry)
                {
                    fields.Add(("retry_after_ms", JsonWriter.Value.Of((long)retry)));
                }
                if (error.Supported is { } supported)
                {
                    fields.Add(("supported", JsonWriter.Value.Array(supported.Select(version => JsonWriter.Value.Of((long)version)))));
                }
                if (error.Fatal)
                {
                    fields.Add(("fatal", JsonWriter.Value.Of(true)));
                }
                return JsonWriter.Encode(JsonWriter.Value.Object(fields));
            }
            case ReceiverMessage.PairStatusMessage statusMessage:
                return statusMessage.Status switch
                {
                    PairStatus.Pending => JsonWriter.Encode(JsonWriter.Value.Object(new[]
                    {
                        ("t", JsonWriter.Value.Of("pair_status")),
                        ("status", JsonWriter.Value.Of("pending")),
                    })),
                    PairStatus.Denied => JsonWriter.Encode(JsonWriter.Value.Object(new[]
                    {
                        ("t", JsonWriter.Value.Of("pair_status")),
                        ("status", JsonWriter.Value.Of("denied")),
                    })),
                    PairStatus.Approved approved => JsonWriter.Encode(JsonWriter.Value.Object(new[]
                    {
                        ("t", JsonWriter.Value.Of("pair_status")),
                        ("status", JsonWriter.Value.Of("approved")),
                        ("kinds", JsonWriter.Value.Array(approved.Kinds.Select(kind => JsonWriter.Value.Of(kind.ToWire())))),
                        ("counter", JsonWriter.Value.Of((long)approved.Counter)),
                    })),
                    _ => throw new ArgumentException("unknown pairing status", nameof(message)),
                };
            default:
                throw new ArgumentException("unknown message", nameof(message));
        }
    }

    public static ReceiverMessage DecodeReceiverMessage(ReadOnlySpan<byte> data)
    {
        var fields = Parse(data);
        switch (fields.String("t"))
        {
            case "welcome":
            {
                fields.Only("t", "version", "last_seq", "kinds", "limits");
                var version = fields.Integer("version");
                if (version is < 1 or > 65535)
                {
                    throw new WireException(ErrorCode.InvalidMessage, "field 'version' is out of range");
                }
                if (!fields.Map.TryGetValue("limits", out var limitsValue))
                {
                    throw new WireException(ErrorCode.InvalidMessage, "field 'limits' is missing");
                }
                var lastSeq = fields.NonNegative("last_seq");
                var kinds = fields.Kinds();
                return new ReceiverMessage.WelcomeMessage(new Welcome((int)version, lastSeq, kinds, DecodeLimits(limitsValue)));
            }
            case "ack":
            {
                fields.Only("t", "seq", "dup");
                var seq = fields.Seq();
                return new ReceiverMessage.Ack(seq, fields.Bool("dup"));
            }
            case "error":
            {
                fields.Only("t", "code", "seq", "last_seq", "retry_after_ms", "supported", "fatal");
                var code = fields.String("code");
                var codeIsValid = code.All(character => character is >= 'a' and <= 'z' or '_');
                if (code.Length is < 1 or > 64 || !codeIsValid)
                {
                    throw new WireException(ErrorCode.InvalidMessage, "field 'code' is lowercase letters and underscores");
                }
                var versions = fields.Has("supported") ? fields.Array("supported") : null;
                // Empty is allowed: a receiver may have dropped every version this device could speak.
                if ((versions?.Count ?? 0) > 16)
                {
                    throw new WireException(ErrorCode.InvalidMessage, "field 'supported' has at most 16 entries");
                }
                var supported = versions?.Select(Version).ToArray();
                var seq = fields.Has("seq") ? fields.Seq() : (ulong?)null;
                var lastSeq = fields.Has("last_seq") ? fields.NonNegative("last_seq") : (ulong?)null;
                var retry = fields.Has("retry_after_ms") ? fields.NonNegative("retry_after_ms") : (ulong?)null;
                var fatal = fields.Has("fatal") && fields.Bool("fatal");
                return new ReceiverMessage.Error(new ReceiverError(code, seq, lastSeq, retry, supported, fatal));
            }
            case "pair_status":
                switch (fields.String("status"))
                {
                    case "pending":
                        fields.Only("t", "status");
                        return new ReceiverMessage.PairStatusMessage(PairStatus.Pending.Instance);
                    case "denied":
                        fields.Only("t", "status");
                        return new ReceiverMessage.PairStatusMessage(PairStatus.Denied.Instance);
                    case "approved":
                    {
                        fields.Only("t", "status", "kinds", "counter");
                        var kinds = fields.Kinds();
                        return new ReceiverMessage.PairStatusMessage(new PairStatus.Approved(kinds, fields.NonNegative("counter")));
                    }
                    default:
                        throw new WireException(ErrorCode.InvalidMessage, "unknown pairing status");
                }
            default:
                throw new WireException(ErrorCode.InvalidMessage, "unknown message type");
        }
    }

    internal static int Version(JsonValue value) =>
        value is JsonValue.Integer number && number.Value is >= 1 and <= 65535
            ? (int)number.Value
            : throw new WireException(ErrorCode.InvalidMessage, "a version is an integer within 1...65535");

    private static Limits DecodeLimits(JsonValue value)
    {
        var fields = new Fields(value);
        fields.Only(
            "items", "photos", "photo_bytes", "note_bytes", "title_chars", "track_points", "open_tracks", "tracks",
            "msgs_per_s", "msg_burst", "points_per_s", "point_burst", "disk_bytes");
        long Positive(string key)
        {
            var number = fields.Integer(key);
            if (number < 1 || number > 1L << 40)
            {
                throw new WireException(ErrorCode.InvalidMessage, "a limit is out of range");
            }
            return number;
        }
        int PositiveInt(string key) => (int)Math.Min(Positive(key), int.MaxValue);
        // A device may batch this many points, so a smaller burst could never be satisfied.
        if (Positive("point_burst") < IntegrationV1.MaxPointsPerMessage)
        {
            throw new WireException(ErrorCode.InvalidMessage, "field 'point_burst' is below the maximum batch size");
        }
        return new Limits
        {
            Items = PositiveInt("items"),
            Photos = PositiveInt("photos"),
            PhotoBytes = PositiveInt("photo_bytes"),
            NoteBytes = PositiveInt("note_bytes"),
            TitleScalars = PositiveInt("title_chars"),
            TrackPoints = PositiveInt("track_points"),
            OpenTracks = PositiveInt("open_tracks"),
            Tracks = PositiveInt("tracks"),
            MessagesPerSecond = PositiveInt("msgs_per_s"),
            MessageBurst = PositiveInt("msg_burst"),
            PointsPerSecond = PositiveInt("points_per_s"),
            PointBurst = PositiveInt("point_burst"),
            DiskBytes = Positive("disk_bytes"),
        };
    }

    private static Fields Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length > IntegrationV1.MaxPlaintextLength)
        {
            throw new WireException(ErrorCode.TooLarge, "a message is at most 65519 bytes");
        }
        JsonValue value;
        try
        {
            value = StrictJson.Parse(data);
        }
        catch (JsonParseException)
        {
            throw new WireException(ErrorCode.InvalidMessage, "the message is not valid strict JSON");
        }
        return new Fields(value);
    }
}
