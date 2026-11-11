using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Erdigo.Core;

namespace Erdigo.Sdk;

/// <summary>
/// The device's one secret: an X25519 key used only for this protocol.
/// </summary>
/// <remarks>
/// The private key lives in an array on the pinned heap, so that <see cref="Dispose"/> clears the only copy the
/// garbage collector knows of. .NET gives no stronger guarantee than that; see the security notes in the README.
/// </remarks>
public sealed class DeviceIdentity : IDisposable
{
    private readonly byte[] privateKey;
    private bool disposed;

    private DeviceIdentity(byte[] pinnedPrivateKey)
    {
        privateKey = pinnedPrivateKey;
        PublicKey = Noise.PublicKey(privateKey);
        Fingerprint = IntegrationV1.Fingerprint(PublicKey);
    }

    /// <summary>The public key, which identifies this device to a receiver.</summary>
    public byte[] PublicKey { get; }

    public string Fingerprint { get; }

    public static DeviceIdentity Generate() => new(Noise.NewPrivateKey());

    /// <summary>Restores a key read from protected storage. The caller should clear <paramref name="rawPrivateKey"/> afterwards.</summary>
    public static DeviceIdentity FromPrivateKey(ReadOnlySpan<byte> rawPrivateKey)
    {
        if (rawPrivateKey.Length != Noise.DhLength)
        {
            throw new ErdigoException(ErdigoErrorKind.Storage, "a device key is 32 bytes", storageProblem: StorageProblem.IdentityDamaged);
        }
        var key = GC.AllocateArray<byte>(Noise.DhLength, pinned: true);
        rawPrivateKey.CopyTo(key);
        return new DeviceIdentity(key);
    }

    /// <summary>The private key, for the handshake. Not a copy.</summary>
    internal byte[] PrivateKey
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return privateKey;
        }
    }

    /// <summary>Only for writing the key to protected storage. The caller owns the copy and must clear it.</summary>
    public byte[] ExportPrivateKeyForStorage()
    {
        var copy = GC.AllocateArray<byte>(Noise.DhLength, pinned: true);
        PrivateKey.CopyTo(copy, 0);
        return copy;
    }

    /// <summary>Clears the private key. The identity cannot be used afterwards.</summary>
    public void Dispose()
    {
        disposed = true;
        CryptographicOperations.ZeroMemory(privateKey);
    }

    public override string ToString() => $"<device identity {IntegrationV1.ShortFingerprint(Fingerprint)}>";
}

/// <summary>What a device remembers about one receiver. An SDK can be paired with several receivers.</summary>
public sealed record ReceiverPairing
{
    private readonly byte[] receiverKey = Array.Empty<byte>();

    public ReceiverPairing(
        string label,
        byte[] receiverKey,
        IReadOnlyList<ItemKind> kinds,
        string? lastHost = null,
        ushort? lastPort = null,
        DateTimeOffset? pairedAt = null,
        ulong counterFloor = 0)
    {
        Label = label ?? throw new ArgumentNullException(nameof(label));
        ReceiverKey = receiverKey ?? throw new ArgumentNullException(nameof(receiverKey));
        Kinds = kinds?.ToArray() ?? throw new ArgumentNullException(nameof(kinds));
        LastHost = lastHost;
        LastPort = lastPort;
        PairedAt = pairedAt ?? DateTimeOffset.UtcNow;
        CounterFloor = counterFloor;
    }

    /// <summary>A local name for this receiver. It is never sent anywhere.</summary>
    public string Label { get; init; }

    public byte[] ReceiverKey
    {
        get => receiverKey;
        init => receiverKey = value.ToArray();
    }

    public IReadOnlyList<ItemKind> Kinds { get; init; }

    public string? LastHost { get; init; }

    public ushort? LastPort { get; init; }

    public DateTimeOffset PairedAt { get; init; }

    /// <summary>
    /// The highest connect counter the receiver had accepted when it approved this pairing. A later counter must be
    /// greater, even if this device has lost its own count.
    /// </summary>
    public ulong CounterFloor { get; init; }

    public string Fingerprint => IntegrationV1.Fingerprint(ReceiverKey);

    public bool Equals(ReceiverPairing? other) =>
        other is not null && Label == other.Label && ReceiverKey.AsSpan().SequenceEqual(other.ReceiverKey)
        && Kinds.SequenceEqual(other.Kinds) && LastHost == other.LastHost && LastPort == other.LastPort
        && PairedAt == other.PairedAt && CounterFloor == other.CounterFloor;

    public override int GetHashCode() => HashCode.Combine(Label, Fingerprint);
}

/// <summary>What went wrong, for <see cref="ErdigoException"/>.</summary>
public enum ErdigoErrorKind
{
    /// <summary>
    /// No receiver answered. A receiver stays silent when it is off, asleep, on another network, when the pairing
    /// code is wrong or expired, and when this device is not allowed. These cases cannot be told apart, by design.
    /// </summary>
    NoAnswer,

    /// <summary>The receiver's key does not match the fingerprint supplied for pairing.</summary>
    FingerprintMismatch,

    /// <summary>The person did not confirm the receiver's key, so the receiver was not asked to pair.</summary>
    KeyNotConfirmed,

    /// <summary>The person at the receiver declined the request.</summary>
    PairingDenied,

    /// <summary>The person at the receiver did not answer in time.</summary>
    PairingTimedOut,

    /// <summary>The code was used twice. The receiver warned its user and removed the pairing made with it.</summary>
    CodeReused,

    /// <summary>The receiver rejected a request. See <see cref="ErdigoException.ReceiverError"/>.</summary>
    Rejected,

    /// <summary>The item kind is not among those the person allowed for this device. See <see cref="ErdigoException.ItemKind"/>.</summary>
    NotPermitted,

    /// <summary>The receiver does not support any protocol version this SDK speaks. See <see cref="ErdigoException.Supported"/>.</summary>
    VersionUnsupported,

    /// <summary>Another connection using this device key took over.</summary>
    Replaced,

    /// <summary>Another writer using this device key advanced the sequence number. See <see cref="ErdigoException.LastSeq"/>.</summary>
    SequenceConflict,

    /// <summary>The receiver broke the protocol.</summary>
    ProtocolViolation,

    InvalidArgument,

    /// <summary>Stored state could not be used. See <see cref="ErdigoException.StorageProblem"/>.</summary>
    Storage,
}

/// <summary>What was wrong with stored state.</summary>
public enum StorageProblem
{
    None,

    /// <summary>The device key file exists but its contents are not a device key. A new key was not created.</summary>
    IdentityDamaged,

    /// <summary>The device key file exists but could not be opened, read or decrypted. A new key was not created.</summary>
    IdentityUnreadable,

    /// <summary>The pairing file exists but its contents could not be understood.</summary>
    StateDamaged,

    /// <summary>The pairing file exists but could not be opened or read.</summary>
    StateUnreadable,

    /// <summary>A folder or file could be read or replaced by someone else, so it was not used.</summary>
    Insecure,

    /// <summary>Anything else, such as a full disk.</summary>
    Other,
}

/// <summary>Failures reported by the SDK.</summary>
public sealed class ErdigoException : Exception
{
    public ErdigoException(
        ErdigoErrorKind kind,
        string? detail = null,
        ReceiverError? receiverError = null,
        ItemKind? itemKind = null,
        IReadOnlyList<int>? supported = null,
        ulong? lastSeq = null,
        StorageProblem storageProblem = StorageProblem.None,
        Exception? innerException = null)
        : base(Describe(kind, detail, receiverError, itemKind, supported), innerException)
    {
        Kind = kind;
        Detail = detail;
        ReceiverError = receiverError;
        ItemKind = itemKind;
        Supported = supported;
        LastSeq = lastSeq;
        StorageProblem = kind == ErdigoErrorKind.Storage && storageProblem == StorageProblem.None ? StorageProblem.Other : storageProblem;
    }

    public ErdigoErrorKind Kind { get; }

    public string? Detail { get; }

    public ReceiverError? ReceiverError { get; }

    public ItemKind? ItemKind { get; }

    public IReadOnlyList<int>? Supported { get; }

    public ulong? LastSeq { get; }

    public StorageProblem StorageProblem { get; }

    private static string Describe(ErdigoErrorKind kind, string? detail, ReceiverError? error, ItemKind? itemKind, IReadOnlyList<int>? supported) => kind switch
    {
        ErdigoErrorKind.NoAnswer =>
            "No receiver answered. It may be off, asleep or on another network, the pairing code may be wrong or expired, or this device may have been removed.",
        ErdigoErrorKind.FingerprintMismatch => "The receiver's key does not match the expected fingerprint. Pairing was stopped.",
        ErdigoErrorKind.KeyNotConfirmed =>
            "The receiver's key was not confirmed. No pairing was saved, no items were sent, and the receiver was not asked to pair.",
        ErdigoErrorKind.PairingDenied => "The request was declined on the receiver.",
        ErdigoErrorKind.PairingTimedOut => "Nobody confirmed the request on the receiver in time.",
        ErdigoErrorKind.CodeReused => "This pairing code was already used. The receiver removed the pairing made with it. Ask for a new code.",
        ErdigoErrorKind.Rejected => $"The receiver rejected the request: {error?.Code ?? "unknown"}.",
        ErdigoErrorKind.NotPermitted => $"This device is not allowed to send '{itemKind?.ToWire() ?? "unknown"}' items.",
        ErdigoErrorKind.VersionUnsupported =>
            $"The receiver supports protocol versions [{string.Join(", ", supported ?? Array.Empty<int>())}], which this SDK does not speak. Update the SDK.",
        ErdigoErrorKind.Replaced => "Another connection using this device key took over.",
        ErdigoErrorKind.SequenceConflict => "Another program is writing with this device key at the same time.",
        ErdigoErrorKind.ProtocolViolation => $"The receiver broke the protocol: {detail}.",
        _ => detail ?? kind.ToString(),
    };
}
