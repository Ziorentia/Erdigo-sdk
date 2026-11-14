using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Erdigo.Core;

namespace Erdigo.Sdk;

/// <summary>Persistent device state: its key, its pairings and the per-receiver connect counter.</summary>
public interface IDeviceStore
{
    DeviceIdentity LoadOrCreateIdentity();

    IReadOnlyList<ReceiverPairing> Pairings();

    /// <summary>Adds a pairing, or replaces the one for the same receiver. Its counter floor takes effect here.</summary>
    void Save(ReceiverPairing pairing);

    /// <summary>Remembers where a receiver was found. Does nothing if the pairing has since been forgotten.</summary>
    void UpdateAddress(string receiverFingerprint, string host, ushort port);

    void RemovePairing(string fingerprint);

    /// <summary>
    /// Returns a value strictly greater than any previous result for this receiver. The value must be durable before
    /// it is returned, because a receiver ignores a counter it has seen.
    /// </summary>
    ulong NextConnectCounter(string receiverFingerprint, ulong nowMs);
}

/// <summary>Keeps everything in memory. Intended for tests and short-lived programs.</summary>
public sealed class InMemoryDeviceStore : IDeviceStore
{
    private readonly object sync = new();
    private readonly List<ReceiverPairing> stored = new();
    private readonly Dictionary<string, ulong> counters = new(StringComparer.Ordinal);
    private DeviceIdentity? identity;

    public InMemoryDeviceStore(DeviceIdentity? identity = null)
    {
        this.identity = identity;
    }

    public DeviceIdentity LoadOrCreateIdentity()
    {
        lock (sync)
        {
            return identity ??= DeviceIdentity.Generate();
        }
    }

    public IReadOnlyList<ReceiverPairing> Pairings()
    {
        lock (sync)
        {
            return stored.ToArray();
        }
    }

    public void Save(ReceiverPairing pairing)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        lock (sync)
        {
            stored.RemoveAll(existing => existing.ReceiverKey.AsSpan().SequenceEqual(pairing.ReceiverKey));
            stored.Add(pairing);
            counters[pairing.Fingerprint] = Math.Max(counters.GetValueOrDefault(pairing.Fingerprint), pairing.CounterFloor);
        }
    }

    public void UpdateAddress(string receiverFingerprint, string host, ushort port)
    {
        lock (sync)
        {
            var index = stored.FindIndex(pairing => pairing.Fingerprint == receiverFingerprint);
            if (index < 0)
            {
                return;
            }
            stored[index] = stored[index] with { LastHost = host, LastPort = port };
        }
    }

    public void RemovePairing(string fingerprint)
    {
        lock (sync)
        {
            stored.RemoveAll(pairing => pairing.Fingerprint == fingerprint);
            counters.Remove(fingerprint);
        }
    }

    public ulong NextConnectCounter(string receiverFingerprint, ulong nowMs)
    {
        lock (sync)
        {
            var next = Math.Max(nowMs, counters.GetValueOrDefault(receiverFingerprint) + 1);
            counters[receiverFingerprint] = next;
            return next;
        }
    }
}

/// <summary>
/// Stores state in a private folder. Refuses to read files that other users could read or replace, the same way
/// <c>ssh</c> treats its key files.
/// </summary>
/// <remarks>
/// On Windows the device key is encrypted with DPAPI for the current user, in <c>identity.dpapi</c>. On other
/// systems, which this SDK supports for development and testing, it is kept in <c>identity.key</c> with mode 0600.
/// A key file that exists but cannot be read is reported, and never silently replaced by a new key.
/// </remarks>
public sealed class FileDeviceStore : IDeviceStore
{
    private const string PairingsFile = "pairings.json";
    private const string DpapiKeyFile = "identity.dpapi";
    private const string PlainKeyFile = "identity.key";
    private const string LockFile = ".lock";
    private static readonly byte[] DpapiMagic = Encoding.ASCII.GetBytes("EDK1");
    private static readonly byte[] PlainMagic = Encoding.ASCII.GetBytes("EDK0");

    /// <summary>Binds the encrypted key to this purpose, so that no other program's DPAPI data can stand in for it.</summary>
    private static readonly byte[] DpapiEntropy = Encoding.ASCII.GetBytes("erdigo-integration-v1/device-key");

    private readonly object sync = new();

    public FileDeviceStore(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        Directory = Path.GetFullPath(directory);
        try
        {
            PrivateFile.EnsurePrivateDirectory(Directory);
        }
        catch (PrivateFileException error)
        {
            throw new ErdigoException(ErdigoErrorKind.Storage, error.Message, storageProblem: StorageProblem.Insecure, innerException: error);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ErdigoException(ErdigoErrorKind.Storage, "could not create the state folder", innerException: error);
        }
    }

    public string Directory { get; }

    /// <summary>Where the key is kept: DPAPI on Windows, a 0600 file elsewhere.</summary>
    public static bool ProtectsKeyWithDpapi => OperatingSystem.IsWindows();

    /// <summary>
    /// <c>%LOCALAPPDATA%\ErdigoSDK\&lt;profile&gt;</c> on Windows. Elsewhere a folder of its own, so that it never
    /// shares state with the Swift SDK on the same Mac.
    /// </summary>
    public static string DefaultDirectory(string profile = "default")
    {
        ArgumentNullException.ThrowIfNull(profile);
        var baseFolder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrEmpty(baseFolder))
        {
            throw new ErdigoException(ErdigoErrorKind.Storage, "the folder for application data could not be found");
        }
        return Path.Combine(baseFolder, OperatingSystem.IsWindows() ? "ErdigoSDK" : "ErdigoSDK.NET", profile);
    }

    private string PathOf(string name) => Path.Combine(Directory, name);

    /// <summary>Serialises access between threads and between processes.</summary>
    private T WithLock<T>(Func<T> body)
    {
        lock (sync)
        {
            FileStream? handle = null;
            var giveUp = DateTime.UtcNow.AddSeconds(30);
            while (handle is null)
            {
                try
                {
                    handle = OperatingSystem.IsWindows()
                        ? new FileStream(PathOf(LockFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
                        : new FileStream(PathOf(LockFile), new FileStreamOptions
                        {
                            Mode = FileMode.OpenOrCreate,
                            Access = FileAccess.ReadWrite,
                            Share = FileShare.None,
                            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                        });
                }
                catch (IOException) when (DateTime.UtcNow < giveUp)
                {
                    // Another process holds the lock.
                    Thread.Sleep(10);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    throw new ErdigoException(ErdigoErrorKind.Storage, "could not lock the state folder", innerException: error);
                }
            }
            using (handle)
            {
                return body();
            }
        }
    }

    private void WithLock(Action body) => WithLock(() =>
    {
        body();
        return 0;
    });

    private byte[]? ReadPrivate(string name, StorageProblem unreadable, int maxBytes = 4 * 1024 * 1024)
    {
        try
        {
            return PrivateFile.ReadAllBytes(PathOf(name), maxBytes);
        }
        catch (PrivateFileException error) when (error.Problem == PrivateFileProblem.Missing)
        {
            return null;
        }
        catch (PrivateFileException error) when (error.Problem == PrivateFileProblem.Insecure)
        {
            throw new ErdigoException(ErdigoErrorKind.Storage, error.Message, storageProblem: StorageProblem.Insecure, innerException: error);
        }
        catch (PrivateFileException error)
        {
            throw new ErdigoException(ErdigoErrorKind.Storage, $"{name} exists but could not be read. Nothing was changed.", storageProblem: unreadable, innerException: error);
        }
    }

    private void WritePrivate(string name, byte[] data, bool overwrite = true)
    {
        try
        {
            PrivateFile.WriteAllBytesAtomically(PathOf(name), data, overwrite);
        }
        catch (IOException error) when (!overwrite && (File.Exists(PathOf(name)) || new FileInfo(PathOf(name)).LinkTarget is not null))
        {
            throw new ErdigoException(
                ErdigoErrorKind.Storage,
                $"{name} exists but could not be read. Nothing was changed, and no new key was created.",
                storageProblem: StorageProblem.IdentityUnreadable,
                innerException: error);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ErdigoException(ErdigoErrorKind.Storage, $"could not write {name}", innerException: error);
        }
    }

    public DeviceIdentity LoadOrCreateIdentity() => WithLock(() =>
    {
        var name = ProtectsKeyWithDpapi ? DpapiKeyFile : PlainKeyFile;
        var stored = ReadPrivate(name, StorageProblem.IdentityUnreadable, maxBytes: 64 * 1024);
        if (stored is not null)
        {
            try
            {
                return ProtectsKeyWithDpapi ? OpenDpapiKey(stored) : OpenPlainKey(stored);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(stored);
            }
        }

        // Only a file that is not there at all leads to a new key.
        var created = DeviceIdentity.Generate();
        var raw = created.ExportPrivateKeyForStorage();
        byte[]? sealedKey = null;
        try
        {
            sealedKey = ProtectsKeyWithDpapi ? SealDpapiKey(raw) : SealPlainKey(raw);
            // Never over an existing file, even one this process could not see.
            WritePrivate(name, sealedKey, overwrite: false);
        }
        catch
        {
            created.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
            if (sealedKey is not null)
            {
                CryptographicOperations.ZeroMemory(sealedKey);
            }
        }
        return created;
    });

    private static DeviceIdentity OpenPlainKey(byte[] stored)
    {
        if (stored.Length != PlainMagic.Length + Noise.DhLength || !stored.AsSpan(0, PlainMagic.Length).SequenceEqual(PlainMagic))
        {
            throw new ErdigoException(
                ErdigoErrorKind.Storage,
                $"{PlainKeyFile} exists but does not hold a device key. It was left as it is. Move it away to create a new key, which means pairing again.",
                storageProblem: StorageProblem.IdentityDamaged);
        }
        return DeviceIdentity.FromPrivateKey(stored.AsSpan(PlainMagic.Length));
    }

    private static byte[] SealPlainKey(byte[] raw)
    {
        var output = GC.AllocateArray<byte>(PlainMagic.Length + raw.Length, pinned: true);
        PlainMagic.CopyTo(output, 0);
        raw.CopyTo(output, PlainMagic.Length);
        return output;
    }

    private static DeviceIdentity OpenDpapiKey(byte[] stored)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI is available on Windows only");
        }
        if (stored.Length <= DpapiMagic.Length || !stored.AsSpan(0, DpapiMagic.Length).SequenceEqual(DpapiMagic))
        {
            throw new ErdigoException(
                ErdigoErrorKind.Storage,
                $"{DpapiKeyFile} exists but does not hold a device key. It was left as it is. Move it away to create a new key, which means pairing again.",
                storageProblem: StorageProblem.IdentityDamaged);
        }
        byte[] raw;
        try
        {
            raw = Unprotect(stored.AsSpan(DpapiMagic.Length).ToArray());
        }
        catch (CryptographicException error)
        {
            throw new ErdigoException(
                ErdigoErrorKind.Storage,
                $"{DpapiKeyFile} exists but Windows could not decrypt it. It belongs to another Windows user or computer, or it is damaged. It was left as it is, and no new key was created.",
                storageProblem: StorageProblem.IdentityUnreadable,
                innerException: error);
        }
        try
        {
            if (raw.Length != Noise.DhLength)
            {
                throw new ErdigoException(
                    ErdigoErrorKind.Storage,
                    $"{DpapiKeyFile} exists but does not hold a device key. It was left as it is.",
                    storageProblem: StorageProblem.IdentityDamaged);
            }
            return DeviceIdentity.FromPrivateKey(raw);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    private static byte[] SealDpapiKey(byte[] raw)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI is available on Windows only");
        }
        byte[] blob;
        try
        {
            blob = Protect(raw);
        }
        catch (CryptographicException error)
        {
            throw new ErdigoException(ErdigoErrorKind.Storage, "Windows could not encrypt the device key", innerException: error);
        }
        var output = new byte[DpapiMagic.Length + blob.Length];
        DpapiMagic.CopyTo(output, 0);
        blob.CopyTo(output, DpapiMagic.Length);
        return output;
    }

    [SupportedOSPlatform("windows")]
    private static byte[] Protect(byte[] raw) =>
        ProtectedData.Protect(raw, DpapiEntropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] Unprotect(byte[] blob) =>
        ProtectedData.Unprotect(blob, DpapiEntropy, DataProtectionScope.CurrentUser);

    // Pairings and counters

    private sealed class State
    {
        public List<ReceiverPairing> Pairings { get; } = new();

        public Dictionary<string, ulong> Counters { get; } = new(StringComparer.Ordinal);
    }

    private State LoadState()
    {
        var data = ReadPrivate(PairingsFile, StorageProblem.StateUnreadable);
        if (data is null)
        {
            return new State();
        }
        try
        {
            return DecodeState(data);
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException or OverflowException)
        {
            throw new ErdigoException(ErdigoErrorKind.Storage, $"{PairingsFile} could not be read", storageProblem: StorageProblem.StateDamaged, innerException: error);
        }
    }

    private static State DecodeState(byte[] data)
    {
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        if (root.GetProperty("version").GetInt32() != 1)
        {
            throw new FormatException("unknown version");
        }
        var state = new State();
        foreach (var element in root.GetProperty("pairings").EnumerateArray())
        {
            var key = Hex.Decode(element.GetProperty("receiverKey").GetString() ?? string.Empty);
            if (key is null || !Noise.IsCanonicalPublicKey(key))
            {
                throw new FormatException("a receiver key is not valid");
            }
            var kinds = element.GetProperty("kinds").EnumerateArray()
                .Select(kind => ItemKinds.FromWire(kind.GetString() ?? string.Empty) ?? throw new FormatException("unknown kind"))
                .ToArray();
            string? host = null;
            ushort? port = null;
            if (element.TryGetProperty("lastHost", out var hostElement) && hostElement.ValueKind != JsonValueKind.Null)
            {
                host = hostElement.GetString();
            }
            if (element.TryGetProperty("lastPort", out var portElement) && portElement.ValueKind != JsonValueKind.Null)
            {
                port = portElement.GetUInt16();
            }
            state.Pairings.Add(new ReceiverPairing(
                element.GetProperty("label").GetString() ?? throw new FormatException("no label"),
                key,
                kinds,
                host,
                port,
                DateTimeOffset.Parse(element.GetProperty("pairedAt").GetString() ?? string.Empty, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                element.GetProperty("counterFloor").GetUInt64()));
        }
        foreach (var counter in root.GetProperty("counters").EnumerateObject())
        {
            state.Counters[counter.Name] = counter.Value.GetUInt64();
        }
        return state;
    }

    private void SaveState(State state)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteStartArray("pairings");
            foreach (var pairing in state.Pairings)
            {
                writer.WriteStartObject();
                writer.WriteString("label", pairing.Label);
                writer.WriteString("receiverKey", Hex.Encode(pairing.ReceiverKey));
                writer.WriteStartArray("kinds");
                foreach (var kind in pairing.Kinds)
                {
                    writer.WriteStringValue(kind.ToWire());
                }
                writer.WriteEndArray();
                if (pairing.LastHost is not null)
                {
                    writer.WriteString("lastHost", pairing.LastHost);
                }
                if (pairing.LastPort is { } port)
                {
                    writer.WriteNumber("lastPort", port);
                }
                writer.WriteString("pairedAt", pairing.PairedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                writer.WriteNumber("counterFloor", pairing.CounterFloor);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartObject("counters");
            foreach (var counter in state.Counters.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                writer.WriteNumber(counter.Key, counter.Value);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        WritePrivate(PairingsFile, buffer.ToArray());
    }

    public IReadOnlyList<ReceiverPairing> Pairings() => WithLock(() => (IReadOnlyList<ReceiverPairing>)LoadState().Pairings.ToArray());

    public void Save(ReceiverPairing pairing)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        WithLock(() =>
        {
            var state = LoadState();
            state.Pairings.RemoveAll(existing => existing.ReceiverKey.AsSpan().SequenceEqual(pairing.ReceiverKey));
            state.Pairings.Add(pairing);
            state.Counters[pairing.Fingerprint] = Math.Max(state.Counters.GetValueOrDefault(pairing.Fingerprint), pairing.CounterFloor);
            SaveState(state);
        });
    }

    public void UpdateAddress(string receiverFingerprint, string host, ushort port) => WithLock(() =>
    {
        var state = LoadState();
        var index = state.Pairings.FindIndex(pairing => pairing.Fingerprint == receiverFingerprint);
        if (index < 0)
        {
            return;
        }
        state.Pairings[index] = state.Pairings[index] with { LastHost = host, LastPort = port };
        SaveState(state);
    });

    public void RemovePairing(string fingerprint) => WithLock(() =>
    {
        var state = LoadState();
        state.Pairings.RemoveAll(pairing => pairing.Fingerprint == fingerprint);
        state.Counters.Remove(fingerprint);
        SaveState(state);
    });

    public ulong NextConnectCounter(string receiverFingerprint, ulong nowMs) => WithLock(() =>
    {
        var state = LoadState();
        var next = Math.Max(nowMs, state.Counters.GetValueOrDefault(receiverFingerprint) + 1);
        state.Counters[receiverFingerprint] = next;
        SaveState(state);
        return next;
    });
}
