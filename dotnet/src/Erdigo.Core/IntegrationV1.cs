using System;
using System.Security.Cryptography;
using System.Text;

namespace Erdigo.Core;

/// <summary>Constants of the integration-v1 protocol. See spec/integration-v1.md.</summary>
public static class IntegrationV1
{
    public const int Version = 1;
    public const string ServiceType = "_erdigo-int._tcp";
    public const int MaxFrameLength = 65535;
    public const int MaxPlaintextLength = 65519;
    public const int MaxHandshakeFrameLength = 1024;
    public const int MaxChunkBytes = 48_000;
    public const int MaxPointsPerMessage = 100;
    public const ulong MaxSequence = (1UL << 53) - 1;
    public const int MaxDeviceNameScalars = 40;
    public const int ConnectFirstMessageLength = 32 + 48 + 8 + 16;
    public const int PairFirstMessageLength = 32 + 16;

    /// <summary>The fifth byte of the preamble.</summary>
    public enum Mode : byte
    {
        Connect = 0x01,
        Pair = 0x02,
    }

    /// <summary>
    /// Five bytes sent in the clear before the handshake. They are also the Noise prologue, so tampering with them
    /// makes the handshake fail.
    /// </summary>
    public static byte[] Preamble(Mode mode) => new byte[] { 0x45, 0x49, 0x4E, 0x31, (byte)mode };

    public static Mode? ParsePreamble(ReadOnlySpan<byte> data)
    {
        if (data.Length != 5 || data[0] != 0x45 || data[1] != 0x49 || data[2] != 0x4E || data[3] != 0x31)
        {
            return null;
        }
        return data[4] switch
        {
            0x01 => Mode.Connect,
            0x02 => Mode.Pair,
            _ => null,
        };
    }

    private static readonly byte[] FingerprintLabel = Encoding.ASCII.GetBytes("erdigo-integration-v1/fingerprint");

    /// <summary>The receiver's or a device's key fingerprint, as 64 lowercase hexadecimal digits.</summary>
    public static string Fingerprint(ReadOnlySpan<byte> publicKey)
    {
        var input = new byte[FingerprintLabel.Length + publicKey.Length];
        FingerprintLabel.CopyTo(input, 0);
        publicKey.CopyTo(input.AsSpan(FingerprintLabel.Length));
        return Hex.Encode(SHA256.HashData(input));
    }

    /// <summary>The first 16 hexadecimal digits in groups of four, for showing to people.</summary>
    public static string ShortFingerprint(string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        var head = fingerprint.Length > 16 ? fingerprint[..16] : fingerprint;
        var builder = new StringBuilder();
        for (var index = 0; index < head.Length; index += 4)
        {
            if (index > 0)
            {
                builder.Append('-');
            }
            builder.Append(head, index, Math.Min(4, head.Length - index));
        }
        return builder.ToString();
    }
}

/// <summary>Hexadecimal text, the way the protocol writes it.</summary>
public static class Hex
{
    /// <summary>Lowercase hexadecimal.</summary>
    public static string Encode(ReadOnlySpan<byte> data) => Convert.ToHexString(data).ToLowerInvariant();

    /// <summary>Accepts lowercase or uppercase hexadecimal digits only, in pairs. Returns null for anything else.</summary>
    public static byte[]? Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length % 2 != 0)
        {
            return null;
        }
        var output = new byte[text.Length / 2];
        for (var index = 0; index < text.Length; index += 2)
        {
            var high = Value(text[index]);
            var low = Value(text[index + 1]);
            if (high < 0 || low < 0)
            {
                return null;
            }
            output[index / 2] = (byte)((high << 4) | low);
        }
        return output;
    }

    private static int Value(char digit) => digit switch
    {
        >= '0' and <= '9' => digit - '0',
        >= 'a' and <= 'f' => digit - 'a' + 10,
        >= 'A' and <= 'F' => digit - 'A' + 10,
        _ => -1,
    };

    /// <summary>True for exactly 64 lowercase hexadecimal digits, the one spelling a fingerprint has (spec section 3).</summary>
    public static bool IsFingerprint(string? text)
    {
        if (text is null || text.Length != 64)
        {
            return false;
        }
        foreach (var digit in text)
        {
            if (!(digit is >= '0' and <= '9' || digit is >= 'a' and <= 'f'))
            {
                return false;
            }
        }
        return true;
    }
}

/// <summary>The error codes of spec section 10.</summary>
public enum ErrorCode
{
    VersionUnsupported,
    InvalidMessage,
    InvalidId,
    InvalidCoordinate,
    InvalidText,
    NotPermitted,
    NotFound,
    KindConflict,
    InvalidState,
    StaleSeq,
    SeqConflict,
    LimitItems,
    LimitPhotos,
    LimitTracks,
    LimitDisk,
    TooLarge,
    PhotoInvalid,
    RateLimited,
    TooManyErrors,
    Paused,
    Replaced,
    CodeReused,
    InternalError,
}

public static class ErrorCodes
{
    /// <summary>The spelling on the wire, such as <c>invalid_message</c>.</summary>
    public static string ToWire(this ErrorCode code) => code switch
    {
        ErrorCode.VersionUnsupported => "version_unsupported",
        ErrorCode.InvalidMessage => "invalid_message",
        ErrorCode.InvalidId => "invalid_id",
        ErrorCode.InvalidCoordinate => "invalid_coordinate",
        ErrorCode.InvalidText => "invalid_text",
        ErrorCode.NotPermitted => "not_permitted",
        ErrorCode.NotFound => "not_found",
        ErrorCode.KindConflict => "kind_conflict",
        ErrorCode.InvalidState => "invalid_state",
        ErrorCode.StaleSeq => "stale_seq",
        ErrorCode.SeqConflict => "seq_conflict",
        ErrorCode.LimitItems => "limit_items",
        ErrorCode.LimitPhotos => "limit_photos",
        ErrorCode.LimitTracks => "limit_tracks",
        ErrorCode.LimitDisk => "limit_disk",
        ErrorCode.TooLarge => "too_large",
        ErrorCode.PhotoInvalid => "photo_invalid",
        ErrorCode.RateLimited => "rate_limited",
        ErrorCode.TooManyErrors => "too_many_errors",
        ErrorCode.Paused => "paused",
        ErrorCode.Replaced => "replaced",
        ErrorCode.CodeReused => "code_reused",
        ErrorCode.InternalError => "internal_error",
        _ => throw new ArgumentOutOfRangeException(nameof(code)),
    };

    /// <summary>Reads a code from the wire. A code this SDK does not know returns null.</summary>
    public static ErrorCode? FromWire(string text)
    {
        foreach (var code in Enum.GetValues<ErrorCode>())
        {
            if (string.Equals(code.ToWire(), text, StringComparison.Ordinal))
            {
                return code;
            }
        }
        return null;
    }

    /// <summary>Retryable errors do not consume the sequence number.</summary>
    public static bool IsRetryable(this ErrorCode code) =>
        code is ErrorCode.RateLimited or ErrorCode.Paused or ErrorCode.InternalError;
}

/// <summary>A wire-level rejection, carrying the error code the receiver reports.</summary>
public sealed class WireException : Exception
{
    public WireException(ErrorCode code, string detail)
        : base(detail)
    {
        Code = code;
        Detail = detail;
    }

    public ErrorCode Code { get; }

    public string Detail { get; }
}
