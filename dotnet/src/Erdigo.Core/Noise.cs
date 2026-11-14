using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math.EC.Rfc7748;

namespace Erdigo.Core;

/// <summary>Failures raised by the Noise layer. They deliberately carry no key material.</summary>
public enum NoiseError
{
    InvalidKey,
    WeakSharedSecret,
    TruncatedMessage,
    DecryptionFailed,
    NonceExhausted,
    MessageTooLarge,
    WrongHandshakeState,
    MissingKey,

    /// <summary>
    /// <see cref="NoiseHandshake.Split"/> was called a second time. A second pair of cipher states would repeat the
    /// first one's keys and nonces.
    /// </summary>
    AlreadySplit,
}

public sealed class NoiseException : Exception
{
    public NoiseException(NoiseError error)
        : base($"Noise: {error}")
    {
        Error = error;
    }

    public NoiseError Error { get; }
}

/// <summary>Constants and primitives of the Noise layer: X25519, ChaCha20-Poly1305 and SHA-256.</summary>
public static class Noise
{
    public const int DhLength = 32;
    public const int HashLength = 32;
    public const int TagLength = 16;
    public const int MaxMessageLength = 65535;

    /// <summary>
    /// X25519 ignores the top bit of a public key, so one private key has more than one spelling. A receiver
    /// identifies devices by these bytes, so it accepts only the canonical spelling: below 2^255-19, top bit clear.
    /// </summary>
    public static bool IsCanonicalPublicKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != DhLength || (key[31] & 0x80) != 0)
        {
            return false;
        }
        // Reject values from 2^255-19 up to 2^255-1, which are also non-canonical.
        var isAtLeastPrime = key[31] == 0x7F && key[0] >= 0xED;
        for (var index = 1; index <= 30 && isAtLeastPrime; index++)
        {
            isAtLeastPrime = key[index] == 0xFF;
        }
        return !isAtLeastPrime;
    }

    internal static byte[] Hmac(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data) => HMACSHA256.HashData(key, data);

    /// <summary>The Noise HKDF. Note that the input keying material is the HMAC message, keyed by the chaining key.</summary>
    internal static byte[][] Hkdf(ReadOnlySpan<byte> chainingKey, ReadOnlySpan<byte> inputKeyMaterial, int outputs)
    {
        var temporary = Hmac(chainingKey, inputKeyMaterial);
        var results = new byte[outputs][];
        var previous = Array.Empty<byte>();
        for (var index = 1; index <= outputs; index++)
        {
            var message = new byte[previous.Length + 1];
            previous.CopyTo(message, 0);
            message[^1] = (byte)index;
            previous = Hmac(temporary, message);
            CryptographicOperations.ZeroMemory(message);
            results[index - 1] = previous;
        }
        CryptographicOperations.ZeroMemory(temporary);
        return results;
    }

    /// <summary>
    /// A new X25519 private key from the system's secure random source. It lives on the pinned heap, so the garbage
    /// collector never leaves a copy behind when it is cleared.
    /// </summary>
    internal static byte[] NewPrivateKey()
    {
        var key = GC.AllocateArray<byte>(DhLength, pinned: true);
        RandomNumberGenerator.Fill(key);
        return key;
    }

    /// <summary>The public key of an X25519 private key.</summary>
    public static byte[] PublicKey(ReadOnlySpan<byte> privateKey)
    {
        if (privateKey.Length != DhLength)
        {
            throw new NoiseException(NoiseError.InvalidKey);
        }
        var scalar = GC.AllocateArray<byte>(DhLength, pinned: true);
        try
        {
            privateKey.CopyTo(scalar);
            var output = new byte[DhLength];
            X25519.ScalarMultBase(scalar, 0, output, 0);
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(scalar);
        }
    }

    /// <summary>X25519 with an explicit rejection of low-order public keys, which produce an all-zero secret.</summary>
    internal static byte[] Dh(byte[] privateKey, byte[] publicKey)
    {
        if (privateKey.Length != DhLength || publicKey.Length != DhLength)
        {
            throw new NoiseException(NoiseError.InvalidKey);
        }
        var secret = GC.AllocateArray<byte>(DhLength, pinned: true);
        X25519.ScalarMult(privateKey, 0, publicKey, 0, secret, 0);
        // Constant-time OR across every byte.
        var accumulated = 0;
        foreach (var value in secret)
        {
            accumulated |= value;
        }
        if (accumulated == 0)
        {
            throw new NoiseException(NoiseError.WeakSharedSecret);
        }
        return secret;
    }
}

/// <summary>
/// A Noise handshake pattern, limited to the DH, cipher and hash functions used by integration-v1:
/// <c>25519</c>, <c>ChaChaPoly</c> and <c>SHA256</c>.
/// </summary>
internal sealed class NoisePattern
{
    public enum Token
    {
        E,
        S,
        Ee,
        Es,
        Se,
        Ss,
        Psk,
    }

    /// <summary>Lets the test suite run official vectors for patterns this protocol does not use.</summary>
    public NoisePattern(string name, bool responderStaticIsPreMessage, Token[][] messages)
    {
        Name = name;
        ResponderStaticIsPreMessage = responderStaticIsPreMessage;
        Messages = messages;
    }

    public string Name { get; }

    public bool ResponderStaticIsPreMessage { get; }

    public Token[][] Messages { get; }

    public bool UsesPsk => Messages.Any(message => message.Contains(Token.Psk));

    public string ProtocolName => $"Noise_{Name}_25519_ChaChaPoly_SHA256";

    /// <summary>Used for ordinary connections: only a device that already knows the receiver key can start one.</summary>
    public static NoisePattern IK { get; } = new(
        "IK",
        responderStaticIsPreMessage: true,
        new[] { new[] { Token.E, Token.Es, Token.S, Token.Ss }, new[] { Token.E, Token.Ee, Token.Se } });

    /// <summary>
    /// Used for pairing: the pairing code is mixed in before anything else, so a party without the code cannot
    /// produce a first message the receiver will answer.
    /// </summary>
    public static NoisePattern XXpsk0 { get; } = new(
        "XXpsk0",
        responderStaticIsPreMessage: false,
        new[] { new[] { Token.Psk, Token.E }, new[] { Token.E, Token.Ee, Token.S, Token.Es }, new[] { Token.S, Token.Se } });
}

/// <summary>One direction of an established Noise session.</summary>
internal sealed class NoiseCipherState : IDisposable
{
    private byte[]? key;
    private ulong counter;

    public bool HasKey => key is not null;

    /// <summary><paramref name="start"/> is not zero only in tests of what happens as the counter runs out.</summary>
    public void Initialize(byte[] raw, ulong start = 0)
    {
        if (key is not null)
        {
            CryptographicOperations.ZeroMemory(key);
        }
        key = GC.AllocateArray<byte>(32, pinned: true);
        raw.AsSpan(0, 32).CopyTo(key);
        counter = start;
    }

    private byte[] Nonce()
    {
        // Noise: 32 bits of zeros followed by the little-endian 64-bit counter.
        var nonce = new byte[12];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(nonce.AsSpan(4), counter);
        return nonce;
    }

    public byte[] Encrypt(byte[] associatedData, byte[] plaintext)
    {
        if (key is null)
        {
            return plaintext.ToArray();
        }
        // 2^64-1 is reserved by the Noise specification.
        if (counter == ulong.MaxValue)
        {
            throw new NoiseException(NoiseError.NonceExhausted);
        }
        var cipher = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
        cipher.Init(true, new AeadParameters(new KeyParameter(key), Noise.TagLength * 8, Nonce(), associatedData));
        var output = new byte[cipher.GetOutputSize(plaintext.Length)];
        var length = cipher.ProcessBytes(plaintext, 0, plaintext.Length, output, 0);
        length += cipher.DoFinal(output, length);
        counter += 1;
        return length == output.Length ? output : output.AsSpan(0, length).ToArray();
    }

    public byte[] Decrypt(byte[] associatedData, byte[] ciphertext)
    {
        if (key is null)
        {
            return ciphertext.ToArray();
        }
        if (counter == ulong.MaxValue)
        {
            throw new NoiseException(NoiseError.NonceExhausted);
        }
        if (ciphertext.Length < Noise.TagLength)
        {
            throw new NoiseException(NoiseError.TruncatedMessage);
        }
        try
        {
            var cipher = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
            cipher.Init(false, new AeadParameters(new KeyParameter(key), Noise.TagLength * 8, Nonce(), associatedData));
            var output = new byte[cipher.GetOutputSize(ciphertext.Length)];
            var length = cipher.ProcessBytes(ciphertext, 0, ciphertext.Length, output, 0);
            length += cipher.DoFinal(output, length);
            counter += 1;
            return length == output.Length ? output : output.AsSpan(0, length).ToArray();
        }
        catch (InvalidCipherTextException)
        {
            throw new NoiseException(NoiseError.DecryptionFailed);
        }
    }

    public void Dispose()
    {
        if (key is not null)
        {
            CryptographicOperations.ZeroMemory(key);
            key = null;
        }
    }
}

internal sealed class NoiseSymmetricState : IDisposable
{
    private readonly NoiseCipherState cipher = new();
    private byte[] chainingKey;

    public NoiseSymmetricState(string protocolName)
    {
        var name = Encoding.ASCII.GetBytes(protocolName);
        if (name.Length <= Noise.HashLength)
        {
            Hash = new byte[Noise.HashLength];
            name.CopyTo(Hash, 0);
        }
        else
        {
            Hash = SHA256.HashData(name);
        }
        chainingKey = Hash.ToArray();
    }

    public byte[] Hash { get; private set; }

    public bool HasKey => cipher.HasKey;

    public void MixHash(ReadOnlySpan<byte> data)
    {
        var input = new byte[Hash.Length + data.Length];
        Hash.CopyTo(input, 0);
        data.CopyTo(input.AsSpan(Hash.Length));
        Hash = SHA256.HashData(input);
    }

    public void MixKey(ReadOnlySpan<byte> inputKeyMaterial)
    {
        var output = Noise.Hkdf(chainingKey, inputKeyMaterial, 2);
        CryptographicOperations.ZeroMemory(chainingKey);
        chainingKey = output[0];
        cipher.Initialize(output[1]);
        CryptographicOperations.ZeroMemory(output[1]);
    }

    public void MixKeyAndHash(ReadOnlySpan<byte> inputKeyMaterial)
    {
        var output = Noise.Hkdf(chainingKey, inputKeyMaterial, 3);
        CryptographicOperations.ZeroMemory(chainingKey);
        chainingKey = output[0];
        MixHash(output[1]);
        cipher.Initialize(output[2]);
        CryptographicOperations.ZeroMemory(output[1]);
        CryptographicOperations.ZeroMemory(output[2]);
    }

    public byte[] EncryptAndHash(byte[] plaintext)
    {
        var ciphertext = cipher.Encrypt(Hash, plaintext);
        MixHash(ciphertext);
        return ciphertext;
    }

    public byte[] DecryptAndHash(byte[] ciphertext)
    {
        var plaintext = cipher.Decrypt(Hash, ciphertext);
        MixHash(ciphertext);
        return plaintext;
    }

    public (NoiseCipherState First, NoiseCipherState Second) Split()
    {
        var output = Noise.Hkdf(chainingKey, ReadOnlySpan<byte>.Empty, 2);
        var first = new NoiseCipherState();
        var second = new NoiseCipherState();
        first.Initialize(output[0]);
        second.Initialize(output[1]);
        CryptographicOperations.ZeroMemory(output[0]);
        CryptographicOperations.ZeroMemory(output[1]);
        return (first, second);
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(chainingKey);
        cipher.Dispose();
    }
}

/// <summary>
/// Encrypts and decrypts messages after a handshake has finished.
/// </summary>
/// <remarks>
/// A handshake yields one transport, because <see cref="NoiseHandshake.Split"/> succeeds only once. This is a
/// reference type, so every reference shares the same cipher states, and a lock covers each whole operation, including
/// the nonce increment. Whichever thread or reference calls it, no key and nonce pair is used twice, and a message
/// that has been opened cannot be opened again. The lock orders the sealing, not the sending: the caller still has
/// to send frames in the order they were sealed, because the peer can only open them in that order.
/// </remarks>
internal sealed class NoiseTransport : IDisposable
{
    private readonly object sync = new();
    private readonly NoiseCipherState sendCipher;
    private readonly NoiseCipherState receiveCipher;

    internal NoiseTransport(NoiseCipherState sendCipher, NoiseCipherState receiveCipher, byte[] handshakeHash)
    {
        this.sendCipher = sendCipher;
        this.receiveCipher = receiveCipher;
        HandshakeHash = handshakeHash;
    }

    public byte[] HandshakeHash { get; }

    public byte[] Seal(byte[] plaintext)
    {
        if (plaintext.Length + Noise.TagLength > Noise.MaxMessageLength)
        {
            throw new NoiseException(NoiseError.MessageTooLarge);
        }
        lock (sync)
        {
            return sendCipher.Encrypt(Array.Empty<byte>(), plaintext);
        }
    }

    public byte[] Open(byte[] ciphertext)
    {
        if (ciphertext.Length > Noise.MaxMessageLength)
        {
            throw new NoiseException(NoiseError.MessageTooLarge);
        }
        lock (sync)
        {
            return receiveCipher.Decrypt(Array.Empty<byte>(), ciphertext);
        }
    }

    /// <summary>Clears both keys. Every later operation fails, rather than falling back to no encryption.</summary>
    public void Dispose()
    {
        lock (sync)
        {
            sendCipher.Initialize(new byte[32], ulong.MaxValue);
            receiveCipher.Initialize(new byte[32], ulong.MaxValue);
        }
    }
}

/// <summary>The Noise handshake state machine.</summary>
internal sealed class NoiseHandshake : IDisposable
{
    private readonly NoisePattern pattern;
    private readonly bool isInitiator;
    private readonly NoiseSymmetricState symmetric;
    private readonly byte[]? staticKey;
    private readonly byte[]? fixedEphemeral;
    private readonly Queue<byte[]> psks;
    private readonly object splitLock = new();
    private byte[]? ephemeralKey;
    private byte[]? remoteEphemeralKey;
    private int messageIndex;
    private bool hasSplit;

    /// <summary>
    /// Starts a handshake. <paramref name="staticKey"/> is this side's X25519 private key. The handshake keeps a
    /// reference to it rather than a copy.
    /// </summary>
    public NoiseHandshake(NoisePattern pattern, bool isInitiator, byte[] prologue, byte[]? staticKey, byte[]? remoteStaticKey = null, byte[]? psk = null)
        : this(pattern, isInitiator, prologue, staticKey, remoteStaticKey, psk is null ? Array.Empty<byte[]>() : new[] { psk }, fixedEphemeral: null)
    {
    }

    /// <summary>
    /// <paramref name="fixedEphemeral"/> exists only so that published test vectors can be reproduced. Reusing an
    /// ephemeral key in real traffic breaks the protocol, so the SDK never passes one.
    /// </summary>
    internal NoiseHandshake(
        NoisePattern pattern,
        bool isInitiator,
        byte[] prologue,
        byte[]? staticKey,
        byte[]? remoteStaticKey,
        IEnumerable<byte[]> psks,
        byte[]? fixedEphemeral)
    {
        var keys = psks.ToList();
        if (keys.Any(key => key.Length != 32))
        {
            throw new NoiseException(NoiseError.InvalidKey);
        }
        if (pattern.UsesPsk && keys.Count == 0)
        {
            throw new NoiseException(NoiseError.MissingKey);
        }
        if (remoteStaticKey is not null && remoteStaticKey.Length != Noise.DhLength)
        {
            throw new NoiseException(NoiseError.InvalidKey);
        }
        if ((staticKey is not null && staticKey.Length != Noise.DhLength) || (fixedEphemeral is not null && fixedEphemeral.Length != Noise.DhLength))
        {
            throw new NoiseException(NoiseError.InvalidKey);
        }

        this.pattern = pattern;
        this.isInitiator = isInitiator;
        this.staticKey = staticKey;
        RemoteStaticKey = remoteStaticKey?.ToArray();
        // Copies, so that the caller may clear its own.
        this.psks = new Queue<byte[]>(keys.Select(key => key.ToArray()));
        this.fixedEphemeral = fixedEphemeral;
        symmetric = new NoiseSymmetricState(pattern.ProtocolName);
        symmetric.MixHash(prologue);

        if (pattern.ResponderStaticIsPreMessage)
        {
            if (isInitiator)
            {
                symmetric.MixHash(RemoteStaticKey ?? throw new NoiseException(NoiseError.MissingKey));
            }
            else
            {
                symmetric.MixHash(Noise.PublicKey(staticKey ?? throw new NoiseException(NoiseError.MissingKey)));
            }
        }
    }

    public byte[]? RemoteStaticKey { get; private set; }

    public bool IsComplete => messageIndex == pattern.Messages.Length;

    public byte[] HandshakeHash => symmetric.Hash;

    private bool IsMyTurn => (messageIndex % 2 == 0) == isInitiator;

    private byte[] NextPsk() => psks.Count > 0 ? psks.Dequeue() : throw new NoiseException(NoiseError.MissingKey);

    private byte[] RequireStatic() => staticKey ?? throw new NoiseException(NoiseError.MissingKey);

    private byte[] RequireEphemeral() => ephemeralKey ?? throw new NoiseException(NoiseError.MissingKey);

    private static byte[] RequireRemote(byte[]? key) => key ?? throw new NoiseException(NoiseError.MissingKey);

    private void MixDh(NoisePattern.Token token)
    {
        var secret = token switch
        {
            NoisePattern.Token.Ee => Noise.Dh(RequireEphemeral(), RequireRemote(remoteEphemeralKey)),
            NoisePattern.Token.Es => isInitiator
                ? Noise.Dh(RequireEphemeral(), RequireRemote(RemoteStaticKey))
                : Noise.Dh(RequireStatic(), RequireRemote(remoteEphemeralKey)),
            NoisePattern.Token.Se => isInitiator
                ? Noise.Dh(RequireStatic(), RequireRemote(remoteEphemeralKey))
                : Noise.Dh(RequireEphemeral(), RequireRemote(RemoteStaticKey)),
            NoisePattern.Token.Ss => Noise.Dh(RequireStatic(), RequireRemote(RemoteStaticKey)),
            _ => throw new NoiseException(NoiseError.WrongHandshakeState),
        };
        try
        {
            symmetric.MixKey(secret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private void MixPsk()
    {
        var psk = NextPsk();
        try
        {
            symmetric.MixKeyAndHash(psk);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(psk);
        }
    }

    public byte[] WriteMessage(byte[]? payload = null)
    {
        payload ??= Array.Empty<byte>();
        if (IsComplete || !IsMyTurn)
        {
            throw new NoiseException(NoiseError.WrongHandshakeState);
        }
        var output = new List<byte>();
        foreach (var token in pattern.Messages[messageIndex])
        {
            switch (token)
            {
                case NoisePattern.Token.E:
                {
                    var key = GC.AllocateArray<byte>(Noise.DhLength, pinned: true);
                    if (fixedEphemeral is not null)
                    {
                        fixedEphemeral.CopyTo(key, 0);
                    }
                    else
                    {
                        RandomNumberGenerator.Fill(key);
                    }
                    if (ephemeralKey is not null)
                    {
                        CryptographicOperations.ZeroMemory(ephemeralKey);
                    }
                    ephemeralKey = key;
                    var publicKey = Noise.PublicKey(key);
                    output.AddRange(publicKey);
                    symmetric.MixHash(publicKey);
                    if (pattern.UsesPsk)
                    {
                        symmetric.MixKey(publicKey);
                    }
                    break;
                }
                case NoisePattern.Token.S:
                    output.AddRange(symmetric.EncryptAndHash(Noise.PublicKey(RequireStatic())));
                    break;
                case NoisePattern.Token.Psk:
                    MixPsk();
                    break;
                default:
                    MixDh(token);
                    break;
            }
        }
        output.AddRange(symmetric.EncryptAndHash(payload));
        if (output.Count > Noise.MaxMessageLength)
        {
            throw new NoiseException(NoiseError.MessageTooLarge);
        }
        messageIndex += 1;
        return output.ToArray();
    }

    public byte[] ReadMessage(byte[] message)
    {
        if (IsComplete || IsMyTurn)
        {
            throw new NoiseException(NoiseError.WrongHandshakeState);
        }
        if (message.Length > Noise.MaxMessageLength)
        {
            throw new NoiseException(NoiseError.MessageTooLarge);
        }
        var offset = 0;
        byte[] Take(int count)
        {
            if (message.Length - offset < count)
            {
                throw new NoiseException(NoiseError.TruncatedMessage);
            }
            var head = message.AsSpan(offset, count).ToArray();
            offset += count;
            return head;
        }

        foreach (var token in pattern.Messages[messageIndex])
        {
            switch (token)
            {
                case NoisePattern.Token.E:
                {
                    var publicKey = Take(Noise.DhLength);
                    remoteEphemeralKey = publicKey;
                    symmetric.MixHash(publicKey);
                    if (pattern.UsesPsk)
                    {
                        symmetric.MixKey(publicKey);
                    }
                    break;
                }
                case NoisePattern.Token.S:
                {
                    var length = symmetric.HasKey ? Noise.DhLength + Noise.TagLength : Noise.DhLength;
                    RemoteStaticKey = symmetric.DecryptAndHash(Take(length));
                    break;
                }
                case NoisePattern.Token.Psk:
                    MixPsk();
                    break;
                default:
                    MixDh(token);
                    break;
            }
        }
        var payload = symmetric.DecryptAndHash(message.AsSpan(offset).ToArray());
        messageIndex += 1;
        return payload;
    }

    /// <summary>
    /// Call once <see cref="IsComplete"/> is true. This succeeds only once, even if called from several threads,
    /// because a second transport would have the same keys and start from the same nonces.
    /// </summary>
    public NoiseTransport Split()
    {
        if (!IsComplete)
        {
            throw new NoiseException(NoiseError.WrongHandshakeState);
        }
        lock (splitLock)
        {
            if (hasSplit)
            {
                throw new NoiseException(NoiseError.AlreadySplit);
            }
            hasSplit = true;
            var (first, second) = symmetric.Split();
            var hash = symmetric.Hash.ToArray();
            ClearEphemeral();
            return new NoiseTransport(isInitiator ? first : second, isInitiator ? second : first, hash);
        }
    }

    private void ClearEphemeral()
    {
        if (ephemeralKey is not null)
        {
            CryptographicOperations.ZeroMemory(ephemeralKey);
            ephemeralKey = null;
        }
    }

    /// <summary>Clears the ephemeral key and the handshake's own key material. The static key belongs to the caller.</summary>
    public void Dispose()
    {
        lock (splitLock)
        {
            ClearEphemeral();
            while (psks.Count > 0)
            {
                CryptographicOperations.ZeroMemory(psks.Dequeue());
            }
            symmetric.Dispose();
        }
    }
}
