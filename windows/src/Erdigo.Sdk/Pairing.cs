using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Erdigo.Core;

namespace Erdigo.Sdk;

/// <summary>Shows a person the receiver's key, and returns whether it matches the key on the receiver's screen.</summary>
/// <param name="receiverKey">The key in the form the receiver shows it: the first 16 digits of its fingerprint.</param>
public delegate Task<bool> ConfirmReceiverKey(string receiverKey, CancellationToken cancellationToken);

/// <summary>Pairs this device with a receiver, using the one-time code a person reads from the receiver's screen.</summary>
public static class Pairing
{
    public const string SdkName = "erdigo-dotnet/0.1.0";

    /// <summary>Pairs with a receiver and returns what to store about it.</summary>
    /// <param name="target">Where to find the receiver.</param>
    /// <param name="ticket">The code, and the receiver's key fingerprint when it comes from the QR code.</param>
    /// <param name="identity">This device's key.</param>
    /// <param name="deviceName">Shown to the person who confirms the request. Treated as untrusted by the receiver.</param>
    /// <param name="kinds">What this device wants to send. The person may allow fewer.</param>
    /// <param name="label">A local name for the receiver. Defaults to the start of its fingerprint.</param>
    /// <param name="handshakeTimeout">How long a handshake may take. Five seconds by default.</param>
    /// <param name="approvalTimeout">How long to wait for the person to confirm. 120 seconds by default.</param>
    /// <param name="confirmKey">
    /// Shows a person the receiver's key, and returns whether it matches the key on the receiver's screen. Needed for
    /// a typed code, because a code proves only that the other side knows it, and not called for any other ticket.
    /// </param>
    /// <param name="cancellationToken">Stops pairing. A cancelled pairing throws <see cref="OperationCanceledException"/>.</param>
    /// <remarks>
    /// A ticket with a fingerprint is compared here, and pairing stops on a mismatch. For a typed code,
    /// <paramref name="confirmKey"/> runs before the receiver hears anything about this device, so the receiver still
    /// shows its key. If it returns false, this throws <see cref="ErdigoErrorKind.KeyNotConfirmed"/>, the code stays
    /// valid, and the receiver has seen neither this device's key nor its name. If it returns true, the key the
    /// person confirmed is compared as if it came from a QR code.
    /// </remarks>
    public static async Task<ReceiverPairing> PairAsync(
        ReceiverTarget target,
        PairingTicket ticket,
        DeviceIdentity identity,
        string deviceName,
        IReadOnlyCollection<ItemKind> kinds,
        string? label = null,
        TimeSpan? handshakeTimeout = null,
        TimeSpan? approvalTimeout = null,
        ConfirmReceiverKey? confirmKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(deviceName);
        ArgumentNullException.ThrowIfNull(kinds);
        var handshakeTime = handshakeTimeout ?? TimeSpan.FromSeconds(5);
        var approvalTime = approvalTimeout ?? TimeSpan.FromSeconds(120);

        var name = TextRules.SanitizeDeviceName(deviceName);
        if (name.Length == 0)
        {
            throw new ErdigoException(ErdigoErrorKind.InvalidArgument, "the device name cannot be empty");
        }
        if (kinds.Count == 0)
        {
            throw new ErdigoException(ErdigoErrorKind.InvalidArgument, "choose at least one item kind");
        }
        if (label is not null)
        {
            ValidateLabel(label);
        }

        if (ticket.Fingerprint is null)
        {
            if (confirmKey is null)
            {
                throw new ErdigoException(ErdigoErrorKind.InvalidArgument, "a typed code needs confirmKey, because a person has to compare the receiver's key");
            }
            var found = await ReceiverFingerprintAsync(target, ticket.Code, handshakeTime, cancellationToken).ConfigureAwait(false);
            if (!await confirmKey(IntegrationV1.ShortFingerprint(found), cancellationToken).ConfigureAwait(false))
            {
                throw new ErdigoException(ErdigoErrorKind.KeyNotConfirmed);
            }
            ticket = new PairingTicket(ticket.Code, found);
        }

        var request = new PairRequest(new[] { IntegrationV1.Version }, SdkName, name, ItemKinds.CanonicalList(kinds));

        var lastError = new ErdigoException(ErdigoErrorKind.NoAnswer);
        foreach (var endpoint in await Discovery.CandidatesAsync(target, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                return await AttemptAsync(endpoint, ticket, identity, request, label, handshakeTime, approvalTime, cancellationToken).ConfigureAwait(false);
            }
            catch (ErdigoException error) when (error.Kind == ErdigoErrorKind.NoAnswer)
            {
                // A cancelled pairing lands here too, because its connection was closed.
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (ErdigoException error)
            {
                // A definite answer from a receiver holding this code ends the search.
                lastError = error;
                break;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw lastError;
    }

    /// <summary>
    /// Finds the key of the receiver that holds this code, and stops there, so that a person can compare it. This
    /// runs the exchange up to message 2, as spec section 4.1 describes.
    /// </summary>
    private static async Task<string> ReceiverFingerprintAsync(ReceiverTarget target, PairingCode code, TimeSpan handshakeTimeout, CancellationToken cancellationToken)
    {
        // The handshake needs a key, but uses it only in message 3, which is never sent here.
        using var unused = DeviceIdentity.Generate();
        var lastError = new ErdigoException(ErdigoErrorKind.NoAnswer);
        foreach (var endpoint in await Discovery.CandidatesAsync(target, cancellationToken).ConfigureAwait(false))
        {
            using var stream = new ByteStream(endpoint.Host, endpoint.Port);
            try
            {
                var (handshake, receiverKey) = await ReadReceiverKeyAsync(stream, code, unused, handshakeTimeout, cancellationToken).ConfigureAwait(false);
                handshake.Dispose();
                return IntegrationV1.Fingerprint(receiverKey);
            }
            catch (ErdigoException error) when (error.Kind == ErdigoErrorKind.NoAnswer)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (ErdigoException error)
            {
                lastError = error;
                break;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw lastError;
    }

    internal static void ValidateLabel(string label)
    {
        try
        {
            TextRules.ValidateTitle(label);
        }
        catch (WireException)
        {
            throw new ErdigoException(ErdigoErrorKind.InvalidArgument, "the receiver label cannot be empty or contain control characters");
        }
    }

    /// <summary>Runs the handshake up to the receiver's answer. This device's key and name have not been sent yet.</summary>
    private static async Task<(NoiseHandshake Handshake, byte[] ReceiverKey)> ReadReceiverKeyAsync(
        ByteStream stream,
        PairingCode code,
        DeviceIdentity identity,
        TimeSpan handshakeTimeout,
        CancellationToken cancellationToken)
    {
        var prologue = IntegrationV1.Preamble(IntegrationV1.Mode.Pair);
        NoiseHandshake handshake;
        byte[] first;
        var psk = code.PresharedKey();
        try
        {
            await stream.StartAsync(handshakeTimeout, cancellationToken).ConfigureAwait(false);
            handshake = new NoiseHandshake(NoisePattern.XXpsk0, isInitiator: true, prologue, identity.PrivateKey, psk: psk);
            first = handshake.WriteMessage();
            stream.ArmDeadline(handshakeTimeout);
            await stream.SendAsync(Concat(prologue, ByteStream.Frame(first)), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not ErdigoException)
        {
            throw new ErdigoException(ErdigoErrorKind.NoAnswer, innerException: error);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(psk);
        }

        try
        {
            var second = await stream.ReadFrameAsync(IntegrationV1.MaxHandshakeFrameLength, cancellationToken).ConfigureAwait(false);
            // Reading this message proves the receiver knows the code too.
            handshake.ReadMessage(second);
        }
        catch (Exception error)
        {
            handshake.Dispose();
            throw new ErdigoException(ErdigoErrorKind.NoAnswer, innerException: error);
        }

        var receiverKey = handshake.RemoteStaticKey;
        if (receiverKey is null || !Noise.IsCanonicalPublicKey(receiverKey))
        {
            handshake.Dispose();
            throw new ErdigoException(ErdigoErrorKind.ProtocolViolation, "the receiver sent an unusable key");
        }
        return (handshake, receiverKey);
    }

    private static async Task<ReceiverPairing> AttemptAsync(
        ReceiverEndpoint endpoint,
        PairingTicket ticket,
        DeviceIdentity identity,
        PairRequest request,
        string? label,
        TimeSpan handshakeTimeout,
        TimeSpan approvalTimeout,
        CancellationToken cancellationToken)
    {
        using var stream = new ByteStream(endpoint.Host, endpoint.Port);
        var (handshake, receiverKey) = await ReadReceiverKeyAsync(stream, ticket.Code, identity, handshakeTimeout, cancellationToken).ConfigureAwait(false);
        using var ownedHandshake = handshake;
        var fingerprint = IntegrationV1.Fingerprint(receiverKey);
        if (ticket.Fingerprint is { } expected && !string.Equals(expected, fingerprint, StringComparison.Ordinal))
        {
            throw new ErdigoException(ErdigoErrorKind.FingerprintMismatch);
        }

        NoiseTransport transport;
        try
        {
            var third = handshake.WriteMessage(Wire.Encode(request));
            await stream.SendFrameAsync(third, cancellationToken).ConfigureAwait(false);
            transport = handshake.Split();
        }
        catch (Exception error)
        {
            throw new ErdigoException(ErdigoErrorKind.NoAnswer, innerException: error);
        }

        using var ownedTransport = transport;
        // The person now decides, which can take a while.
        stream.ArmDeadline(approvalTimeout);
        while (true)
        {
            ReceiverMessage message;
            try
            {
                var frame = await stream.ReadFrameAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                message = Wire.DecodeReceiverMessage(transport.Open(frame));
            }
            catch (ByteStreamException error) when (error.Failure == ByteStreamFailure.TimedOut)
            {
                throw new ErdigoException(ErdigoErrorKind.PairingTimedOut, innerException: error);
            }
            catch (WireException error)
            {
                throw new ErdigoException(ErdigoErrorKind.ProtocolViolation, error.Detail, innerException: error);
            }
            catch (Exception error)
            {
                throw new ErdigoException(ErdigoErrorKind.NoAnswer, innerException: error);
            }

            switch (message)
            {
                case ReceiverMessage.PairStatusMessage { Status: PairStatus.Pending }:
                    continue;
                case ReceiverMessage.PairStatusMessage { Status: PairStatus.Denied }:
                    throw new ErdigoException(ErdigoErrorKind.PairingDenied);
                case ReceiverMessage.PairStatusMessage { Status: PairStatus.Approved approved }:
                    var address = stream.RemoteAddress;
                    return new ReceiverPairing(
                        label ?? $"receiver-{fingerprint[..8]}",
                        receiverKey,
                        approved.Kinds,
                        address?.Host,
                        address?.Port,
                        counterFloor: approved.Counter);
                case ReceiverMessage.Error { Value: var error }:
                    if (error.Code == ErrorCode.CodeReused.ToWire())
                    {
                        throw new ErdigoException(ErdigoErrorKind.CodeReused, receiverError: error);
                    }
                    if (error.Code == ErrorCode.VersionUnsupported.ToWire())
                    {
                        throw new ErdigoException(ErdigoErrorKind.VersionUnsupported, receiverError: error, supported: error.Supported ?? Array.Empty<int>());
                    }
                    throw new ErdigoException(ErdigoErrorKind.Rejected, receiverError: error);
                default:
                    throw new ErdigoException(ErdigoErrorKind.ProtocolViolation, "unexpected message during pairing");
            }
        }
    }

    internal static byte[] Concat(byte[] first, byte[] second)
    {
        var output = new byte[first.Length + second.Length];
        first.CopyTo(output, 0);
        second.CopyTo(output, first.Length);
        return output;
    }
}
