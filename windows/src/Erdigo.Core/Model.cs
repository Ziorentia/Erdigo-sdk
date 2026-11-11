using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Erdigo.Core;

/// <summary>A validated coordinate: finite, latitude within -90...90 and longitude within -180...180 (WGS 84).</summary>
public readonly struct Coordinate : IEquatable<Coordinate>
{
    public Coordinate(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude) || latitude < -90.0 || latitude > 90.0
            || longitude < -180.0 || longitude > 180.0)
        {
            throw new WireException(ErrorCode.InvalidCoordinate, "latitude must be within -90...90 and longitude within -180...180");
        }
        Latitude = latitude;
        Longitude = longitude;
    }

    public double Latitude { get; }

    public double Longitude { get; }

    public bool Equals(Coordinate other) => Latitude.Equals(other.Latitude) && Longitude.Equals(other.Longitude);

    public override bool Equals(object? obj) => obj is Coordinate other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Latitude, Longitude);

    public static bool operator ==(Coordinate left, Coordinate right) => left.Equals(right);

    public static bool operator !=(Coordinate left, Coordinate right) => !left.Equals(right);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Latitude},{Longitude}");
}

/// <summary>An item identifier: 1 to 64 ASCII letters, digits, '-' or '_'.</summary>
public sealed class ItemId : IEquatable<ItemId>
{
    public ItemId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is < 1 or > 64 || !value.All(IsAllowed))
        {
            throw new WireException(ErrorCode.InvalidId, "an item id is 1 to 64 ASCII letters, digits, '-' or '_'");
        }
        Value = value;
    }

    public string Value { get; }

    private static bool IsAllowed(char character) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_';

    public bool Equals(ItemId? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as ItemId);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;
}

/// <summary>The kinds of item a device can write.</summary>
public enum ItemKind
{
    Pin,
    Marker,
    Note,
    Photo,
    Track,
}

public static class ItemKinds
{
    /// <summary>Every kind, in the order the protocol lists them.</summary>
    public static IReadOnlyList<ItemKind> All { get; } = new[] { ItemKind.Pin, ItemKind.Marker, ItemKind.Note, ItemKind.Photo, ItemKind.Track };

    public static string ToWire(this ItemKind kind) => kind switch
    {
        ItemKind.Pin => "pin",
        ItemKind.Marker => "marker",
        ItemKind.Note => "note",
        ItemKind.Photo => "photo",
        ItemKind.Track => "track",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static ItemKind? FromWire(string text) => text switch
    {
        "pin" => ItemKind.Pin,
        "marker" => ItemKind.Marker,
        "note" => ItemKind.Note,
        "photo" => ItemKind.Photo,
        "track" => ItemKind.Track,
        _ => null,
    };

    /// <summary><c>pin</c>, <c>marker</c> and <c>note</c> are carried by <c>put</c>. <c>photo</c> and <c>track</c> have their own messages.</summary>
    public static bool UsesPut(this ItemKind kind) => kind is ItemKind.Pin or ItemKind.Marker or ItemKind.Note;

    /// <summary>The kinds given, without repeats, in the protocol's order.</summary>
    public static IReadOnlyList<ItemKind> CanonicalList(IEnumerable<ItemKind> kinds)
    {
        var set = new HashSet<ItemKind>(kinds);
        return All.Where(set.Contains).ToArray();
    }
}

/// <summary>The colours a marker can have.</summary>
public enum MarkerColor
{
    Red,
    Orange,
    Yellow,
    Green,
    Blue,
    Purple,
    Gray,
}

public static class MarkerColors
{
    public static IReadOnlyList<MarkerColor> All { get; } = new[]
    {
        MarkerColor.Red, MarkerColor.Orange, MarkerColor.Yellow, MarkerColor.Green, MarkerColor.Blue, MarkerColor.Purple, MarkerColor.Gray,
    };

    public static string ToWire(this MarkerColor color) => color switch
    {
        MarkerColor.Red => "red",
        MarkerColor.Orange => "orange",
        MarkerColor.Yellow => "yellow",
        MarkerColor.Green => "green",
        MarkerColor.Blue => "blue",
        MarkerColor.Purple => "purple",
        MarkerColor.Gray => "gray",
        _ => throw new ArgumentOutOfRangeException(nameof(color)),
    };

    public static MarkerColor? FromWire(string text) => text switch
    {
        "red" => MarkerColor.Red,
        "orange" => MarkerColor.Orange,
        "yellow" => MarkerColor.Yellow,
        "green" => MarkerColor.Green,
        "blue" => MarkerColor.Blue,
        "purple" => MarkerColor.Purple,
        "gray" => MarkerColor.Gray,
        _ => null,
    };
}

/// <summary>One point of a track.</summary>
public sealed class TrackPoint : IEquatable<TrackPoint>
{
    public TrackPoint(Coordinate coordinate, long? timestampMs = null, double? altitudeM = null)
    {
        if (timestampMs is { } timestamp && (timestamp < 0 || timestamp > (1L << 53) - 1))
        {
            throw new WireException(ErrorCode.InvalidMessage, "a track point timestamp is milliseconds since 1970, within 0...2^53-1");
        }
        if (altitudeM is { } altitude && (!double.IsFinite(altitude) || altitude < -1000.0 || altitude > 100_000.0))
        {
            throw new WireException(ErrorCode.InvalidMessage, "a track point altitude is within -1000...100000 metres");
        }
        Coordinate = coordinate;
        TimestampMs = timestampMs;
        AltitudeM = altitudeM;
    }

    public Coordinate Coordinate { get; }

    /// <summary>Milliseconds since the Unix epoch.</summary>
    public long? TimestampMs { get; }

    public double? AltitudeM { get; }

    public bool Equals(TrackPoint? other) =>
        other is not null && Coordinate == other.Coordinate && TimestampMs == other.TimestampMs && Nullable.Equals(AltitudeM, other.AltitudeM);

    public override bool Equals(object? obj) => Equals(obj as TrackPoint);

    public override int GetHashCode() => HashCode.Combine(Coordinate, TimestampMs, AltitudeM);
}

/// <summary>The only photo formats the protocol allows.</summary>
public enum PhotoType
{
    Jpeg,
    Png,
}

public static class PhotoTypes
{
    public static string ToWire(this PhotoType type) => type switch
    {
        PhotoType.Jpeg => "image/jpeg",
        PhotoType.Png => "image/png",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static PhotoType? FromWire(string text) => text switch
    {
        "image/jpeg" => PhotoType.Jpeg,
        "image/png" => PhotoType.Png,
        _ => null,
    };

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>Detects the format from the first bytes. Anything but JPEG or PNG returns null.</summary>
    public static PhotoType? Detect(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return PhotoType.Jpeg;
        }
        if (data.Length >= 8 && data[..8].SequenceEqual(PngSignature))
        {
            return PhotoType.Png;
        }
        return null;
    }
}

/// <summary>Limits a receiver enforces per device. The receiver announces them in <c>welcome</c>.</summary>
public sealed record Limits
{
    public int Items { get; init; }

    public int Photos { get; init; }

    public int PhotoBytes { get; init; }

    public int NoteBytes { get; init; }

    public int TitleScalars { get; init; }

    public int TrackPoints { get; init; }

    public int OpenTracks { get; init; }

    public int Tracks { get; init; }

    public int MessagesPerSecond { get; init; }

    public int MessageBurst { get; init; }

    public int PointsPerSecond { get; init; }

    public int PointBurst { get; init; }

    public long DiskBytes { get; init; }

    /// <summary>The values of spec section 11.</summary>
    public static Limits V1 { get; } = new()
    {
        Items = 500,
        Photos = 50,
        PhotoBytes = 1_048_576,
        NoteBytes = 4096,
        TitleScalars = 200,
        TrackPoints = 3000,
        OpenTracks = 5,
        Tracks = 200,
        MessagesPerSecond = 5,
        MessageBurst = 20,
        PointsPerSecond = 1,
        PointBurst = 100,
        DiskBytes = 31_457_280,
    };
}

/// <summary>Rules for text that a person will eventually read on the receiver's screen.</summary>
public static class TextRules
{
    /// <summary>Control characters and invisible direction overrides, which can make text read differently from what it is.</summary>
    public static bool IsDisallowed(Rune scalar, bool allowLineBreaks)
    {
        var value = scalar.Value;
        return value switch
        {
            0x0A or 0x09 => !allowLineBreaks,
            <= 0x1F or (>= 0x7F and <= 0x9F) => true,
            0x061C or 0x200E or 0x200F or (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069) or 0x2028 or 0x2029 or 0xFEFF => true,
            _ => false,
        };
    }

    /// <summary>
    /// The Unicode scalars of a string, or null if it holds a lone surrogate, which no other platform's strings can
    /// hold and which UTF-8 cannot carry.
    /// </summary>
    public static List<Rune>? Scalars(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var scalars = new List<Rune>(text.Length);
        var rest = text.AsSpan();
        while (!rest.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(rest, out var scalar, out var used) != System.Buffers.OperationStatus.Done)
            {
                return null;
            }
            scalars.Add(scalar);
            rest = rest[used..];
        }
        return scalars;
    }

    public static void ValidateTitle(string text, Limits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        limits ??= Limits.V1;
        var scalars = Scalars(text) ?? throw new WireException(ErrorCode.InvalidText, "a title must be valid Unicode text");
        if (scalars.Count == 0)
        {
            throw new WireException(ErrorCode.InvalidText, "a title cannot be empty; leave it out instead");
        }
        if (scalars.Count > limits.TitleScalars)
        {
            throw new WireException(ErrorCode.TooLarge, $"a title is at most {limits.TitleScalars} characters");
        }
        if (scalars.Any(scalar => IsDisallowed(scalar, allowLineBreaks: false)))
        {
            throw new WireException(ErrorCode.InvalidText, "a title cannot contain control or text-direction characters");
        }
    }

    public static void ValidateNote(string text, Limits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        limits ??= Limits.V1;
        var scalars = Scalars(text) ?? throw new WireException(ErrorCode.InvalidText, "a note must be valid Unicode text");
        if (scalars.Count == 0)
        {
            throw new WireException(ErrorCode.InvalidText, "a note cannot be empty");
        }
        if (Encoding.UTF8.GetByteCount(text) > limits.NoteBytes)
        {
            throw new WireException(ErrorCode.TooLarge, $"a note is at most {limits.NoteBytes} bytes of UTF-8");
        }
        if (scalars.Any(scalar => IsDisallowed(scalar, allowLineBreaks: true)))
        {
            throw new WireException(ErrorCode.InvalidText, "a note cannot contain control or text-direction characters");
        }
    }

    /// <summary>Receivers treat a device name as untrusted: unsafe characters are removed and it is cut to 40 characters.</summary>
    public static string SanitizeDeviceName(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var kept = new StringBuilder();
        var rest = text.AsSpan();
        while (!rest.IsEmpty)
        {
            // A lone surrogate is dropped like any other character that cannot be shown safely.
            var status = Rune.DecodeFromUtf16(rest, out var scalar, out var used);
            if (status == System.Buffers.OperationStatus.Done && !IsDisallowed(scalar, allowLineBreaks: false))
            {
                kept.Append(scalar.ToString());
            }
            rest = rest[Math.Max(1, used)..];
        }
        var cleaned = kept.ToString().Trim();
        var limited = new StringBuilder();
        var count = 0;
        foreach (var scalar in cleaned.EnumerateRunes())
        {
            if (count == IntegrationV1.MaxDeviceNameScalars)
            {
                break;
            }
            limited.Append(scalar.ToString());
            count++;
        }
        return limited.ToString();
    }
}

/// <summary>The one-time code a person reads from the receiver: 100 random bits as 20 Crockford base32 characters.</summary>
public sealed class PairingCode : IEquatable<PairingCode>
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private static readonly byte[] PskSalt = Encoding.ASCII.GetBytes("erdigo-integration-v1/pairing-psk");

    /// <summary>
    /// Reads a code as a person may type it: hyphens and white space are ignored, letters are uppercased, O is read
    /// as 0, and I and L as 1. Anything else, or any length but 20, is an error.
    /// </summary>
    public PairingCode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var output = new StringBuilder(20);
        foreach (var scalar in text.EnumerateRunes())
        {
            if (scalar.Value == '-' || Rune.IsWhiteSpace(scalar))
            {
                continue;
            }
            // EnumerateRunes turns a lone surrogate into U+FFFD, which is refused here like anything else.
            if (!scalar.IsAscii)
            {
                throw new WireException(ErrorCode.InvalidMessage, "the pairing code has invalid characters");
            }
            var character = (char)scalar.Value;
            if (character is >= 'a' and <= 'z')
            {
                character = (char)(character - 0x20);
            }
            character = character switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                _ => character,
            };
            if (!Alphabet.Contains(character, StringComparison.Ordinal))
            {
                throw new WireException(ErrorCode.InvalidMessage, "the pairing code has invalid characters");
            }
            output.Append(character);
        }
        if (output.Length != 20)
        {
            throw new WireException(ErrorCode.InvalidMessage, "the pairing code has 20 characters");
        }
        Canonical = output.ToString();
    }

    /// <summary>The 20 characters without separators.</summary>
    public string Canonical { get; }

    /// <summary>Creates a new code from the system's secure random source.</summary>
    public static PairingCode Generate()
    {
        var random = new byte[20];
        RandomNumberGenerator.Fill(random);
        // Each of the 20 bytes contributes exactly 5 unbiased bits.
        var text = new char[20];
        for (var index = 0; index < random.Length; index++)
        {
            text[index] = Alphabet[random[index] & 0x1F];
        }
        CryptographicOperations.ZeroMemory(random);
        return new PairingCode(new string(text));
    }

    /// <summary><c>XXXXX-XXXXX-XXXXX-XXXXX</c>, for showing on the receiver's screen.</summary>
    public string Display => string.Join("-", Enumerable.Range(0, 4).Select(group => Canonical.Substring(group * 5, 5)));

    /// <summary>The Noise pre-shared key derived from the code. The caller owns the result and should clear it.</summary>
    public byte[] PresharedKey()
    {
        var material = Encoding.ASCII.GetBytes(Canonical);
        try
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, material, 32, PskSalt, Array.Empty<byte>());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    public bool Equals(PairingCode? other) => other is not null && string.Equals(Canonical, other.Canonical, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as PairingCode);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Canonical);

    /// <summary>Avoids writing the code to logs by accident.</summary>
    public override string ToString() => "<pairing code>";
}

/// <summary>
/// What a person gives the SDK to pair: the code, and the receiver's key fingerprint when it comes from the QR code.
/// A typed code has no fingerprint, and the person then has to compare the key.
/// </summary>
public sealed class PairingTicket : IEquatable<PairingTicket>
{
    private const string Prefix = "erdigo-pair:1?";

    /// <summary>Much longer than any valid text, which is 110 bytes without white space around it.</summary>
    private const int MaxTextLength = 1024;

    public PairingTicket(PairingCode code, string? fingerprint = null)
    {
        Code = code ?? throw new ArgumentNullException(nameof(code));
        Fingerprint = fingerprint;
    }

    public PairingCode Code { get; }

    public string? Fingerprint { get; }

    /// <summary>
    /// Accepts either a bare code or the QR form <c>erdigo-pair:1?code=...&amp;fp=...</c>.
    /// The QR form needs exactly one code and exactly one valid fingerprint, because the key comparison is what makes
    /// it stronger than a typed code. Nothing else may appear in it. Text over 1024 bytes is refused before it is
    /// looked at, because it may come from anywhere.
    /// </summary>
    public static PairingTicket Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Encoding.UTF8.GetByteCount(text) > MaxTextLength)
        {
            throw new WireException(ErrorCode.InvalidMessage, "the pairing text is too long");
        }
        var trimmed = text.Trim();
        if (!trimmed.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return new PairingTicket(new PairingCode(trimmed));
        }
        PairingCode? code = null;
        string? fingerprint = null;
        // Empty pieces are kept, so that '&&', a trailing '&' and a field starting with '=' are refused below.
        foreach (var part in trimmed[Prefix.Length..].Split('&'))
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length != 2)
            {
                throw new WireException(ErrorCode.InvalidMessage, "the pairing text is malformed");
            }
            if (pieces[0] == "code" && code is null)
            {
                code = new PairingCode(pieces[1]);
            }
            else if (pieces[0] == "fp" && fingerprint is null)
            {
                var lowered = pieces[1].ToLowerInvariant();
                if (!Hex.IsFingerprint(lowered))
                {
                    throw new WireException(ErrorCode.InvalidMessage, "the fingerprint in the pairing text is malformed");
                }
                fingerprint = lowered;
            }
            else
            {
                throw new WireException(ErrorCode.InvalidMessage, "the pairing text is malformed");
            }
        }
        if (code is null)
        {
            throw new WireException(ErrorCode.InvalidMessage, "the pairing text has no code");
        }
        if (fingerprint is null)
        {
            throw new WireException(ErrorCode.InvalidMessage, "the pairing text has no fingerprint");
        }
        return new PairingTicket(code, fingerprint);
    }

    /// <summary>
    /// The text of the QR code, or null without a valid fingerprint, because QR text without one would look stronger
    /// than it is.
    /// </summary>
    public string? Uri => Hex.IsFingerprint(Fingerprint) ? $"{Prefix}code={Code.Display}&fp={Fingerprint}" : null;

    public bool Equals(PairingTicket? other) =>
        other is not null && Code.Equals(other.Code) && string.Equals(Fingerprint, other.Fingerprint, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as PairingTicket);

    public override int GetHashCode() => HashCode.Combine(Code, Fingerprint);

    public override string ToString() => "<pairing ticket>";
}

/// <summary>A token bucket, used by receivers to enforce rates and by the SDK to stay below them.</summary>
public sealed class TokenBucket
{
    private double tokens;
    private double updatedAt;

    public TokenBucket(int capacity, int refillPerSecond, double now)
    {
        Capacity = capacity;
        RefillPerSecond = refillPerSecond;
        tokens = capacity;
        updatedAt = now;
    }

    public double Capacity { get; }

    public double RefillPerSecond { get; }

    private void Refill(double now)
    {
        var elapsed = Math.Max(0, now - updatedAt);
        tokens = Math.Min(Capacity, tokens + elapsed * RefillPerSecond);
        updatedAt = now;
    }

    /// <summary>Takes <paramref name="count"/> tokens if possible and returns null. Otherwise takes nothing and returns how long to wait, in seconds.</summary>
    public double? Take(double now, int count = 1)
    {
        Refill(now);
        double wanted = count;
        if (tokens >= wanted)
        {
            tokens -= wanted;
            return null;
        }
        if (RefillPerSecond <= 0)
        {
            return double.PositiveInfinity;
        }
        return (Math.Min(wanted, Capacity) - tokens) / RefillPerSecond;
    }
}

/// <summary>A monotonic clock in seconds, like the system uptime the Swift SDK uses.</summary>
public static class MonotonicClock
{
    public static double Now => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
}
