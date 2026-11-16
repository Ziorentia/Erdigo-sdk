using System;
using System.Collections.Generic;
using System.Linq;
using Erdigo.Core;

namespace Erdigo.Examples.CloudRelay;

/// <summary>Why a body sent to <c>POST /v1/ops</c> was refused, as the relay API names it.</summary>
internal sealed class BatchRefusal : Exception
{
    public BatchRefusal(string error, string? reason = null, int? index = null)
        : base(reason ?? error)
    {
        Error = error;
        Reason = reason;
        Index = index;
    }

    /// <summary><c>invalid_json</c>, <c>invalid_body</c> or <c>invalid_op</c>.</summary>
    public string Error { get; }

    public string? Reason { get; }

    public int? Index { get; }
}

/// <summary>
/// Checks the operations sources send, by the rules in docs/cloud-relay.md. The values are checked with the SDK's own
/// rules (ids, coordinates, titles, notes, track points), so the relay takes exactly what the SDK would send.
/// </summary>
internal static class Operations
{
    public const int MaxPhotoBytes = 1_048_576;

    /// <summary>Deep enough for any valid body, which needs five levels, and for a wrong one to be refused as an operation.</summary>
    private const int MaxDepth = 16;

    /// <summary>The longest base64 text a photo of <see cref="MaxPhotoBytes"/> or one byte more can have.</summary>
    private const int MaxBase64Length = (MaxPhotoBytes + 1 + 2) / 3 * 4;

    private sealed record Shape(string[] Required, string[] Optional)
    {
        public bool Allows(string field) => Required.Contains(field, StringComparer.Ordinal) || Optional.Contains(field, StringComparer.Ordinal);
    }

    private static readonly Dictionary<string, Shape> Shapes = new(StringComparer.Ordinal)
    {
        ["pin"] = new(new[] { "id", "lat", "lon" }, new[] { "title" }),
        ["marker"] = new(new[] { "id", "lat", "lon" }, new[] { "title", "color" }),
        ["note"] = new(new[] { "id", "lat", "lon", "text" }, Array.Empty<string>()),
        ["photo"] = new(new[] { "id", "lat", "lon", "type", "data" }, new[] { "title" }),
        ["track"] = new(new[] { "id", "points" }, new[] { "title" }),
        ["finish_track"] = new(new[] { "id" }, Array.Empty<string>()),
        ["move"] = new(new[] { "id", "lat", "lon" }, Array.Empty<string>()),
        ["delete"] = new(new[] { "id" }, Array.Empty<string>()),
    };

    private static readonly string[] PointFields = { "lat", "lon", "ts", "alt" };

    private sealed class OpRefusal : Exception
    {
        public OpRefusal(string reason)
            : base(reason)
        {
        }
    }

    /// <summary>
    /// Reads a whole body and returns each operation as the relay keeps it: compact JSON with its fields in a fixed
    /// order. The batch is taken whole or not at all, so the first problem refuses all of it.
    /// </summary>
    public static IReadOnlyList<byte[]> ParseBatch(ReadOnlySpan<byte> body, int maxOps)
    {
        JsonValue document;
        try
        {
            document = StrictJson.Parse(body, MaxDepth);
        }
        catch (JsonParseException)
        {
            throw new BatchRefusal("invalid_json");
        }
        if (document is not JsonValue.Object root)
        {
            throw new BatchRefusal("invalid_json");
        }
        if (root.Fields.Count != 1 || !root.Fields.TryGetValue("ops", out var listed))
        {
            throw new BatchRefusal("invalid_body", "the body must be {\"ops\":[...]} and nothing else");
        }
        if (listed is not JsonValue.Array list)
        {
            throw new BatchRefusal("invalid_body", "ops must be a list");
        }
        if (list.Values.Count == 0 || list.Values.Count > maxOps)
        {
            throw new BatchRefusal("invalid_body", $"ops must hold 1 to {maxOps} operations");
        }
        var encoded = new List<byte[]>(list.Values.Count);
        for (var index = 0; index < list.Values.Count; index++)
        {
            try
            {
                encoded.Add(Encode(list.Values[index]));
            }
            catch (OpRefusal refusal)
            {
                throw new BatchRefusal("invalid_op", refusal.Message, index);
            }
            catch (WireException error)
            {
                throw new BatchRefusal("invalid_op", error.Detail, index);
            }
        }
        return encoded;
    }

    private static byte[] Encode(JsonValue value)
    {
        if (value is not JsonValue.Object operation)
        {
            throw new OpRefusal("an operation must be an object");
        }
        var fields = operation.Fields;
        if (!fields.TryGetValue("op", out var opValue) || opValue is not JsonValue.String { Value: var name } || !Shapes.TryGetValue(name, out var shape))
        {
            throw new OpRefusal("op must be one of pin, marker, note, photo, track, finish_track, move and delete");
        }
        // A field name is not repeated in the answer: it could be anything, of any length.
        if (fields.Keys.Any(field => field != "op" && !shape.Allows(field)))
        {
            throw new OpRefusal($"a {name} operation has a field it does not take");
        }
        foreach (var required in shape.Required)
        {
            if (!fields.ContainsKey(required))
            {
                throw new OpRefusal($"a {name} operation needs {required}");
            }
        }

        var output = new List<(string, JsonWriter.Value)> { ("op", JsonWriter.Value.Of(name)) };
        var id = Text(fields, "id");
        _ = new ItemId(id);
        output.Add(("id", JsonWriter.Value.Of(id)));

        if (fields.ContainsKey("lat"))
        {
            var latitude = fields["lat"];
            var longitude = fields["lon"];
            _ = new Coordinate(Number(latitude, "lat"), Number(longitude, "lon"));
            output.Add(("lat", Written(latitude)));
            output.Add(("lon", Written(longitude)));
        }
        if (fields.ContainsKey("title"))
        {
            var title = Text(fields, "title");
            TextRules.ValidateTitle(title);
            output.Add(("title", JsonWriter.Value.Of(title)));
        }
        if (fields.ContainsKey("color"))
        {
            var color = Text(fields, "color");
            if (MarkerColors.FromWire(color) is null)
            {
                throw new OpRefusal("color must be red, orange, yellow, green, blue, purple or gray");
            }
            output.Add(("color", JsonWriter.Value.Of(color)));
        }
        if (fields.ContainsKey("text"))
        {
            var text = Text(fields, "text");
            TextRules.ValidateNote(text);
            output.Add(("text", JsonWriter.Value.Of(text)));
        }
        if (name == "photo")
        {
            var type = PhotoTypes.FromWire(Text(fields, "type")) ?? throw new OpRefusal("type must be image/jpeg or image/png");
            var data = Text(fields, "data");
            var bytes = DecodeBase64(data) ?? throw new OpRefusal("data must be standard base64 with padding, as an encoder writes it, with no line breaks");
            if (bytes.Length == 0 || bytes.Length > MaxPhotoBytes)
            {
                throw new OpRefusal($"a photo is 1 to {MaxPhotoBytes} bytes");
            }
            if (PhotoTypes.Detect(bytes) != type)
            {
                throw new OpRefusal("the photo does not start with the bytes for its type");
            }
            output.Add(("type", JsonWriter.Value.Of(type.ToWire())));
            output.Add(("data", JsonWriter.Value.Of(data)));
        }
        if (name == "track")
        {
            output.Add(("points", Points(fields["points"])));
        }
        return JsonWriter.Encode(JsonWriter.Value.Object(output));
    }

    private static JsonWriter.Value Points(JsonValue value)
    {
        if (value is not JsonValue.Array list || list.Values.Count is 0 or > IntegrationV1.MaxPointsPerMessage)
        {
            throw new OpRefusal($"points must be a list of 1 to {IntegrationV1.MaxPointsPerMessage} points");
        }
        var points = new List<JsonWriter.Value>(list.Values.Count);
        foreach (var entry in list.Values)
        {
            if (entry is not JsonValue.Object point || point.Fields.Keys.Any(field => !PointFields.Contains(field, StringComparer.Ordinal)))
            {
                throw new OpRefusal("a point is an object with lat, lon, and optionally ts and alt");
            }
            if (!point.Fields.TryGetValue("lat", out var latitude) || !point.Fields.TryGetValue("lon", out var longitude))
            {
                throw new OpRefusal("a point needs lat and lon");
            }
            var written = new List<(string, JsonWriter.Value)>
            {
                ("lat", Written(latitude)),
                ("lon", Written(longitude)),
            };
            long? timestamp = null;
            if (point.Fields.TryGetValue("ts", out var ts))
            {
                // Spec section 6.1: an integer field written as a fraction is refused.
                timestamp = ts is JsonValue.Integer { Value: var whole }
                    ? whole
                    : throw new OpRefusal("ts must be a whole number of milliseconds since 1970");
                written.Add(("ts", JsonWriter.Value.Of(whole)));
            }
            double? altitude = null;
            if (point.Fields.TryGetValue("alt", out var alt))
            {
                altitude = Number(alt, "alt");
                written.Add(("alt", Written(alt)));
            }
            _ = new TrackPoint(new Coordinate(Number(latitude, "lat"), Number(longitude, "lon")), timestamp, altitude);
            points.Add(JsonWriter.Value.Object(written));
        }
        return JsonWriter.Value.Array(points);
    }

    private static string Text(IReadOnlyDictionary<string, JsonValue> fields, string name) =>
        fields[name] is JsonValue.String { Value: var text } ? text : throw new OpRefusal($"{name} must be a string");

    private static double Number(JsonValue value, string name) => value switch
    {
        JsonValue.Integer integer => integer.Value,
        JsonValue.Number number => number.Value,
        _ => throw new OpRefusal($"{name} must be a number"),
    };

    /// <summary>A number as it was sent: a whole number stays one, so that nothing is added to it.</summary>
    private static JsonWriter.Value Written(JsonValue number) => number switch
    {
        JsonValue.Integer integer => JsonWriter.Value.Of(integer.Value),
        JsonValue.Number fraction => JsonWriter.Value.Of(fraction.Value),
        _ => throw new OpRefusal("a number was expected"),
    };

    /// <summary>
    /// Standard base64 with padding, exactly as an encoder writes it: no line breaks or spaces, no URL-safe letters,
    /// and no bits set past the end of the data. Returns null for anything else.
    /// </summary>
    internal static byte[]? DecodeBase64(string text)
    {
        if (text.Length == 0 || text.Length % 4 != 0 || text.Length > MaxBase64Length)
        {
            return null;
        }
        foreach (var character in text)
        {
            if (character is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '+' or '/' or '='))
            {
                return null;
            }
        }
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            return null;
        }
        // Writing it again gives the same text only if it was written as an encoder writes it.
        return string.Equals(Convert.ToBase64String(bytes), text, StringComparison.Ordinal) ? bytes : null;
    }
}
