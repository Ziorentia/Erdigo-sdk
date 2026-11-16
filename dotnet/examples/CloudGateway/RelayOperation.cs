using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Erdigo.Core;
using Erdigo.Sdk;

namespace Erdigo.Examples.CloudGateway;

/// <summary>An operation the gateway cannot apply, refused for good. <see cref="Code"/> is what is printed.</summary>
internal sealed class OperationRefusal : Exception
{
    public OperationRefusal(string kind, string code)
        : base(code)
    {
        Kind = kind;
        Code = code;
    }

    public string Kind { get; }

    public string Code { get; }
}

/// <summary>
/// One operation from the relay, turned into the SDK call that applies it. The shape is checked here; the values are
/// checked by the SDK, as they are for any other program that uses it.
/// </summary>
internal sealed class RelayOperation
{
    private const int MaxPhotoBytes = 1_048_576;

    private static readonly Dictionary<string, (string[] Required, string[] Optional)> Shapes = new(StringComparer.Ordinal)
    {
        ["pin"] = (new[] { "id", "lat", "lon" }, new[] { "title" }),
        ["marker"] = (new[] { "id", "lat", "lon" }, new[] { "title", "color" }),
        ["note"] = (new[] { "id", "lat", "lon", "text" }, Array.Empty<string>()),
        ["photo"] = (new[] { "id", "lat", "lon", "type", "data" }, new[] { "title" }),
        ["track"] = (new[] { "id", "points" }, new[] { "title" }),
        ["finish_track"] = (new[] { "id" }, Array.Empty<string>()),
        ["move"] = (new[] { "id", "lat", "lon" }, Array.Empty<string>()),
        ["delete"] = (new[] { "id" }, Array.Empty<string>()),
    };

    private static readonly string[] PointFields = { "lat", "lon", "ts", "alt" };

    private readonly Func<ErdigoClient, CancellationToken, Task> apply;

    private RelayOperation(string kind, Func<ErdigoClient, CancellationToken, Task> apply)
    {
        Kind = kind;
        this.apply = apply;
    }

    /// <summary>The <c>op</c> name, which is all the gateway ever prints about an operation.</summary>
    public string Kind { get; }

    /// <summary>Applies it. The SDK refuses a bad value before sending anything, with <see cref="ErdigoErrorKind.InvalidArgument"/>.</summary>
    public Task ApplyAsync(ErdigoClient client, CancellationToken cancellationToken) => apply(client, cancellationToken);

    public static RelayOperation Decode(JsonValue.Object operation)
    {
        var fields = operation.Fields;
        if (!fields.TryGetValue("op", out var named) || named is not JsonValue.String { Value: var kind } || !Shapes.TryGetValue(kind, out var shape))
        {
            throw new OperationRefusal("unknown", "invalid_op");
        }
        if (fields.Keys.Any(field => field != "op" && !shape.Required.Contains(field) && !shape.Optional.Contains(field))
            || shape.Required.Any(field => !fields.ContainsKey(field)))
        {
            throw new OperationRefusal(kind, "invalid_op");
        }
        try
        {
            return Decode(kind, fields);
        }
        catch (WireException error)
        {
            throw new OperationRefusal(kind, error.Code.ToWire());
        }
    }

    private static RelayOperation Decode(string kind, IReadOnlyDictionary<string, JsonValue> fields)
    {
        var id = Text(kind, fields, "id");
        var title = fields.ContainsKey("title") ? Text(kind, fields, "title") : null;
        switch (kind)
        {
            case "pin":
            {
                var at = At(kind, fields);
                return new(kind, (client, token) => client.PutPinAsync(id, at, title, token));
            }
            case "marker":
            {
                var at = At(kind, fields);
                MarkerColor? color = null;
                if (fields.ContainsKey("color"))
                {
                    color = MarkerColors.FromWire(Text(kind, fields, "color")) ?? throw new OperationRefusal(kind, "invalid_op");
                }
                return new(kind, (client, token) => client.PutMarkerAsync(id, at, title, color, token));
            }
            case "note":
            {
                var at = At(kind, fields);
                var text = Text(kind, fields, "text");
                return new(kind, (client, token) => client.PutNoteAsync(id, at, text, token));
            }
            case "photo":
            {
                var at = At(kind, fields);
                var type = PhotoTypes.FromWire(Text(kind, fields, "type")) ?? throw new OperationRefusal(kind, "invalid_op");
                var data = DecodeBase64(Text(kind, fields, "data")) ?? throw new OperationRefusal(kind, "invalid_op");
                if (data.Length > MaxPhotoBytes)
                {
                    throw new OperationRefusal(kind, "too_large");
                }
                // The SDK finds the type from the bytes. A photo that says it is something else is refused here.
                if (PhotoTypes.Detect(data) != type)
                {
                    throw new OperationRefusal(kind, "invalid_op");
                }
                return new(kind, (client, token) => client.PutPhotoAsync(id, at, data, title, token));
            }
            case "track":
            {
                var points = Points(kind, fields["points"]);
                return new(kind, (client, token) => client.AppendTrackAsync(id, points, title, token));
            }
            case "finish_track":
                return new(kind, (client, token) => client.FinishTrackAsync(id, token));
            case "move":
            {
                var to = At(kind, fields);
                return new(kind, (client, token) => client.MoveAsync(id, to, token));
            }
            default:
                return new(kind, (client, token) => client.DeleteAsync(id, token));
        }
    }

    private static string Text(string kind, IReadOnlyDictionary<string, JsonValue> fields, string name) =>
        fields[name] is JsonValue.String { Value: var text } ? text : throw new OperationRefusal(kind, "invalid_op");

    private static double Number(string kind, JsonValue value) => value switch
    {
        JsonValue.Integer integer => integer.Value,
        JsonValue.Number number => number.Value,
        _ => throw new OperationRefusal(kind, "invalid_op"),
    };

    private static Coordinate At(string kind, IReadOnlyDictionary<string, JsonValue> fields) =>
        new(Number(kind, fields["lat"]), Number(kind, fields["lon"]));

    private static TrackPoint[] Points(string kind, JsonValue value)
    {
        if (value is not JsonValue.Array list || list.Values.Count is 0 or > IntegrationV1.MaxPointsPerMessage)
        {
            throw new OperationRefusal(kind, "invalid_op");
        }
        return list.Values.Select(entry =>
        {
            if (entry is not JsonValue.Object point || point.Fields.Keys.Any(field => !PointFields.Contains(field))
                || !point.Fields.TryGetValue("lat", out var latitude) || !point.Fields.TryGetValue("lon", out var longitude))
            {
                throw new OperationRefusal(kind, "invalid_op");
            }
            long? timestamp = null;
            if (point.Fields.TryGetValue("ts", out var ts))
            {
                timestamp = ts is JsonValue.Integer { Value: var whole } ? whole : throw new OperationRefusal(kind, "invalid_op");
            }
            double? altitude = point.Fields.TryGetValue("alt", out var alt) ? Number(kind, alt) : null;
            return new TrackPoint(new Coordinate(Number(kind, latitude), Number(kind, longitude)), timestamp, altitude);
        }).ToArray();
    }

    /// <summary>Standard base64 with padding, exactly as an encoder writes it, as the relay API requires. Null for anything else.</summary>
    internal static byte[]? DecodeBase64(string text)
    {
        // One byte more than a photo may have still decodes, so that it is refused for its size.
        if (text.Length == 0 || text.Length % 4 != 0 || text.Length > (MaxPhotoBytes + 3) / 3 * 4)
        {
            return null;
        }
        if (!text.All(character => char.IsAsciiLetterOrDigit(character) || character is '+' or '/' or '='))
        {
            return null;
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
        return bytes.Length > 0 && string.Equals(Convert.ToBase64String(bytes), text, StringComparison.Ordinal) ? bytes : null;
    }
}
