using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Erdigo.Core;

namespace Erdigo.Sdk;

/// <summary>
/// How long a request keeps trying while the receiver cannot be reached, for example while the computer is asleep or
/// the app is closed.
/// </summary>
public abstract class RetryPolicy
{
    private RetryPolicy()
    {
    }

    /// <summary>Fail at the first connection problem.</summary>
    public static RetryPolicy None { get; } = new NoRetry();

    /// <summary>Keep trying until the request is cancelled.</summary>
    public static RetryPolicy Forever { get; } = new RetryForever();

    /// <summary>Keep trying for this long, counted from when the connection was lost.</summary>
    public static RetryPolicy UpTo(TimeSpan time) => new RetryUpTo(time);

    internal sealed class NoRetry : RetryPolicy
    {
    }

    internal sealed class RetryForever : RetryPolicy
    {
    }

    internal sealed class RetryUpTo : RetryPolicy
    {
        public RetryUpTo(TimeSpan time) => Time = time;

        public TimeSpan Time { get; }
    }
}

/// <summary>
/// Writes items to one paired receiver.
/// </summary>
/// <remarks>
/// Requests run strictly one at a time, in the order they were made. A request returns once the receiver has
/// acknowledged it. If the connection breaks, the request is sent again with the same sequence number, so the
/// receiver applies it at most once. Dispose the client, or call <see cref="Close"/>, to close its connection.
/// </remarks>
public sealed class ErdigoClient : IDisposable
{
    private sealed class Session
    {
        public Session(ByteStream stream, NoiseTransport transport, Welcome welcome)
        {
            Stream = stream;
            Transport = transport;
            Welcome = welcome;
            LastSeq = welcome.LastSeq;
        }

        public ByteStream Stream { get; }

        public NoiseTransport Transport { get; }

        public Welcome Welcome { get; }

        public ulong LastSeq { get; set; }
    }

    /// <summary>A problem with the connection, as opposed to an answer from the receiver.</summary>
    private sealed class ConnectionLostException : Exception
    {
    }

    /// <summary>
    /// How many <c>rate_limited</c> answers one request will accept before giving up. An honest receiver needs only one
    /// or two, so this only stops a receiver that never relents.
    /// </summary>
    private const int MaxRateLimitedAnswers = 20;
    private const double MinimumRateLimitWait = 0.05;

    private readonly DeviceIdentity identity;
    private readonly IDeviceStore store;
    private readonly ReceiverTarget target;
    private readonly RetryPolicy retry;
    private readonly TimeSpan connectTimeout;
    private readonly TimeSpan ackTimeout;
    private readonly FifoLock gate = new();
    private readonly object sync = new();
    private ReceiverPairing pairing;
    private Session? session;
    private double lastSessionAt;

    // These outlive a connection, because the receiver's own count does too.
    private Limits? pacedLimits;
    private TokenBucket? messageBucket;
    private TokenBucket? pointBucket;

    public ErdigoClient(
        DeviceIdentity identity,
        ReceiverPairing pairing,
        IDeviceStore store,
        ReceiverTarget? target = null,
        RetryPolicy? retry = null,
        TimeSpan? connectTimeout = null,
        TimeSpan? ackTimeout = null)
    {
        this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
        this.pairing = pairing ?? throw new ArgumentNullException(nameof(pairing));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.target = target ?? ReceiverTarget.Discover;
        this.retry = retry ?? RetryPolicy.UpTo(TimeSpan.FromSeconds(30));
        this.connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
        this.ackTimeout = ackTimeout ?? TimeSpan.FromSeconds(15);
    }

    // Public requests

    /// <summary>Connects now rather than at the first request. Throws <see cref="ErdigoErrorKind.NoAnswer"/> if the retry policy runs out.</summary>
    public Task ConnectAsync(CancellationToken cancellationToken = default) =>
        SerialisedAsync(() => RetryingAsync(async () => { _ = await CurrentSessionAsync(cancellationToken).ConfigureAwait(false); }, cancellationToken), cancellationToken);

    /// <summary>
    /// Closes the connection. A later request connects again. Do not call this while a request is still running,
    /// because that request would then reconnect.
    /// </summary>
    public void Close()
    {
        lock (sync)
        {
            if (session is not null)
            {
                session.Stream.Dispose();
                session.Transport.Dispose();
                session = null;
            }
        }
    }

    public void Dispose() => Close();

    /// <summary>The limits the receiver announced on the current connection.</summary>
    public Limits? Limits
    {
        get
        {
            lock (sync)
            {
                return session?.Welcome.Limits;
            }
        }
    }

    /// <summary>The item kinds the person allowed, as announced on the current connection.</summary>
    public IReadOnlyList<ItemKind>? AllowedKinds
    {
        get
        {
            lock (sync)
            {
                return session?.Welcome.Kinds;
            }
        }
    }

    public Task PingAsync(CancellationToken cancellationToken = default) =>
        PerformAsync(Request.Ping.Instance, null, 0, cancellationToken);

    /// <summary>Creates the pin, or replaces it if this device already has one with this id.</summary>
    public Task PutPinAsync(string id, Coordinate at, string? title = null, CancellationToken cancellationToken = default)
    {
        var itemId = ValidatedId(id);
        ValidateTitle(title);
        return PerformAsync(new Request.PutPin(itemId, at, title), ItemKind.Pin, 0, cancellationToken);
    }

    public Task PutMarkerAsync(string id, Coordinate at, string? title = null, MarkerColor? color = null, CancellationToken cancellationToken = default)
    {
        var itemId = ValidatedId(id);
        ValidateTitle(title);
        return PerformAsync(new Request.PutMarker(itemId, at, title, color), ItemKind.Marker, 0, cancellationToken);
    }

    /// <summary>A note is plain text. The receiver shows links as text and never opens them.</summary>
    public Task PutNoteAsync(string id, Coordinate at, string text, CancellationToken cancellationToken = default)
    {
        var itemId = ValidatedId(id);
        Validated(() => TextRules.ValidateNote(text ?? throw new ArgumentNullException(nameof(text))));
        return PerformAsync(new Request.PutNote(itemId, at, text), ItemKind.Note, 0, cancellationToken);
    }

    public Task MoveAsync(string id, Coordinate to, CancellationToken cancellationToken = default) =>
        PerformAsync(new Request.Move(ValidatedId(id), to), null, 0, cancellationToken);

    /// <summary>Deletes one of this device's items. Deleting an id that does not exist succeeds.</summary>
    public Task DeleteAsync(string id, CancellationToken cancellationToken = default) =>
        PerformAsync(new Request.Delete(ValidatedId(id)), null, 0, cancellationToken);

    /// <summary>Deletes every item this device created on this receiver, and nothing else.</summary>
    public Task DeleteAllAsync(CancellationToken cancellationToken = default) =>
        PerformAsync(Request.DeleteAll.Instance, null, 0, cancellationToken);

    /// <summary>
    /// Appends points to a track, creating it if needed. Long lists are split into several messages, and no other
    /// request runs in between.
    /// </summary>
    public Task AppendTrackAsync(string id, IReadOnlyList<TrackPoint> points, string? title = null, CancellationToken cancellationToken = default)
    {
        var itemId = ValidatedId(id);
        ValidateTitle(title);
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            throw new ErdigoException(ErdigoErrorKind.InvalidArgument, "a track append needs at least one point");
        }
        var all = points.ToArray();
        return SerialisedAsync(async () =>
        {
            var pendingTitle = title;
            for (var start = 0; start < all.Length; start += IntegrationV1.MaxPointsPerMessage)
            {
                var batch = all.Skip(start).Take(IntegrationV1.MaxPointsPerMessage).ToArray();
                await PerformHoldingLockAsync(new Request.TrackAppend(itemId, pendingTitle, batch), ItemKind.Track, batch.Length, cancellationToken).ConfigureAwait(false);
                pendingTitle = null;
            }
        }, cancellationToken);
    }

    public Task FinishTrackAsync(string id, CancellationToken cancellationToken = default) =>
        PerformAsync(new Request.TrackFinish(ValidatedId(id)), ItemKind.Track, 0, cancellationToken);

    /// <summary>
    /// Sends a JPEG or PNG photo. The format is detected from the data, and the receiver removes metadata such as EXIF
    /// location when it stores the photo.
    /// </summary>
    public Task PutPhotoAsync(string id, Coordinate at, ReadOnlyMemory<byte> data, string? title = null, CancellationToken cancellationToken = default)
    {
        var itemId = ValidatedId(id);
        ValidateTitle(title);
        var type = PhotoTypes.Detect(data.Span)
            ?? throw new ErdigoException(ErdigoErrorKind.InvalidArgument, "a photo must be JPEG or PNG data");
        if (data.Length > Core.Limits.V1.PhotoBytes)
        {
            throw new ErdigoException(ErdigoErrorKind.InvalidArgument, $"a photo is at most {Core.Limits.V1.PhotoBytes} bytes");
        }

        var bytes = data.ToArray();
        var chunkSize = IntegrationV1.MaxChunkBytes;
        var chunkCount = (bytes.Length + chunkSize - 1) / chunkSize;
        var header = new PhotoHeader(itemId, at, title, type, bytes.Length, Hex.Encode(SHA256.HashData(bytes)), chunkCount);

        return SerialisedAsync(() => RetryingAsync(async () =>
        {
            // The receiver drops a partial upload when the connection ends, so restart from the beginning.
            var current = await CurrentSessionAsync(cancellationToken).ConfigureAwait(false);
            if (!current.Welcome.Kinds.Contains(ItemKind.Photo))
            {
                throw new ErdigoException(ErdigoErrorKind.NotPermitted, itemKind: ItemKind.Photo);
            }
            await ExchangeNextAsync(new Request.PhotoBegin(header), current, cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < chunkCount; index++)
            {
                var start = index * chunkSize;
                var slice = bytes.AsSpan(start, Math.Min(chunkSize, bytes.Length - start)).ToArray();
                await ExchangeNextAsync(new Request.PhotoChunk(itemId, index, slice), current, cancellationToken).ConfigureAwait(false);
            }
            await ExchangeNextAsync(new Request.PhotoEnd(itemId), current, cancellationToken).ConfigureAwait(false);
        }, cancellationToken), cancellationToken);
    }

    // Validation

    private static ItemId ValidatedId(string id) => Validated(() => new ItemId(id ?? throw new ArgumentNullException(nameof(id))));

    private static void ValidateTitle(string? title)
    {
        if (title is not null)
        {
            Validated(() => TextRules.ValidateTitle(title));
        }
    }

    private static T Validated<T>(Func<T> body)
    {
        try
        {
            return body();
        }
        catch (WireException error)
        {
            throw new ErdigoException(ErdigoErrorKind.InvalidArgument, error.Detail, innerException: error);
        }
    }

    private static void Validated(Action body) => Validated(() =>
    {
        body();
        return 0;
    });

    // Ordering

    private async Task SerialisedAsync(Func<Task> body, CancellationToken cancellationToken)
    {
        await gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await body().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    // Requests

    private Task PerformAsync(Request request, ItemKind? kind, int points, CancellationToken cancellationToken) =>
        SerialisedAsync(() => PerformHoldingLockAsync(request, kind, points, cancellationToken), cancellationToken);

    private async Task PerformHoldingLockAsync(Request request, ItemKind? kind, int points, CancellationToken cancellationToken)
    {
        ulong? assignedSeq = null;
        // A resend is the same request, so it does not wait for its allowance a second time.
        var pointsArePaid = false;
        await RetryingAsync(async () =>
        {
            var current = await CurrentSessionAsync(cancellationToken).ConfigureAwait(false);
            if (kind is { } wanted && !current.Welcome.Kinds.Contains(wanted))
            {
                throw new ErdigoException(ErdigoErrorKind.NotPermitted, itemKind: wanted);
            }
            // A request keeps its number across reconnections, so the receiver can recognise a resend.
            if (assignedSeq is { } assigned && current.LastSeq > assigned)
            {
                throw new ErdigoException(ErdigoErrorKind.SequenceConflict, lastSeq: current.LastSeq);
            }
            if (!pointsArePaid)
            {
                await PacePointsAsync(points, cancellationToken).ConfigureAwait(false);
                pointsArePaid = true;
            }
            var seq = assignedSeq ?? current.LastSeq + 1;
            assignedSeq = seq;
            await ExchangeAsync(request, seq, current, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Waits as long as asked, up to the longest wait a receiver may ask for.</summary>
    private static Task SleepAsync(double seconds, CancellationToken cancellationToken) =>
        Task.Delay(TimeSpan.FromSeconds(Math.Max(0, Math.Min(seconds, 3600))), cancellationToken);

    private async Task PacePointsAsync(int count, CancellationToken cancellationToken)
    {
        if (count <= 0)
        {
            return;
        }
        while (Take(() => pointBucket?.Take(MonotonicClock.Now, count)) is { } wait)
        {
            await SleepAsync(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PaceMessageAsync(CancellationToken cancellationToken)
    {
        while (Take(() => messageBucket?.Take(MonotonicClock.Now)) is { } wait)
        {
            await SleepAsync(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    private double? Take(Func<double?> take)
    {
        lock (sync)
        {
            return take();
        }
    }

    private Task ExchangeNextAsync(Request request, Session current, CancellationToken cancellationToken) =>
        ExchangeAsync(request, current.LastSeq + 1, current, cancellationToken);

    /// <summary>Sends one request and waits for its answer, honouring the receiver's rate limit.</summary>
    private async Task ExchangeAsync(Request request, ulong seq, Session current, CancellationToken cancellationToken)
    {
        if (seq > IntegrationV1.MaxSequence)
        {
            throw new ErdigoException(ErdigoErrorKind.InvalidArgument, "the sequence number is exhausted; remove this device on the receiver and pair it again");
        }
        var plaintext = Wire.Encode(new RequestEnvelope(seq, request));
        var rateLimitedAnswers = 0;
        while (true)
        {
            await PaceMessageAsync(cancellationToken).ConfigureAwait(false);

            ReceiverMessage answer;
            try
            {
                var sealedRequest = current.Transport.Seal(plaintext);
                current.Stream.ArmDeadline(ackTimeout);
                await current.Stream.SendFrameAsync(sealedRequest, cancellationToken).ConfigureAwait(false);
                var frame = await current.Stream.ReadFrameAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                current.Stream.DisarmDeadline();
                answer = Wire.DecodeReceiverMessage(current.Transport.Open(frame));
            }
            catch (WireException error)
            {
                DropSession();
                throw new ErdigoException(ErdigoErrorKind.ProtocolViolation, error.Detail, innerException: error);
            }
            catch (OperationCanceledException)
            {
                DropSession();
                throw;
            }
            catch (Exception error) when (error is not ErdigoException)
            {
                DropSession();
                throw new ConnectionLostException();
            }

            switch (answer)
            {
                case ReceiverMessage.Ack ack:
                    if (ack.Seq != seq)
                    {
                        DropSession();
                        throw new ErdigoException(ErdigoErrorKind.ProtocolViolation, "the acknowledgement has the wrong sequence number");
                    }
                    current.LastSeq = seq;
                    return;
                case ReceiverMessage.Error { Value: var error } when error.Code == ErrorCode.RateLimited.ToWire():
                    rateLimitedAnswers += 1;
                    if (rateLimitedAnswers > MaxRateLimitedAnswers)
                    {
                        DropSession();
                        throw new ErdigoException(ErdigoErrorKind.Rejected, receiverError: error);
                    }
                    var wait = (error.RetryAfterMs ?? 1000) / 1000.0;
                    await SleepAsync(Math.Max(wait, MinimumRateLimitWait), cancellationToken).ConfigureAwait(false);
                    break;
                case ReceiverMessage.Error { Value: var error }:
                    Handle(error, seq, current);
                    return;
                default:
                    DropSession();
                    throw new ErdigoException(ErdigoErrorKind.ProtocolViolation, "unexpected message in answer to a request");
            }
        }
    }

    private void Handle(ReceiverError error, ulong seq, Session current)
    {
        if (error.LastSeq is { } last)
        {
            current.LastSeq = Math.Max(current.LastSeq, last);
        }

        if (error.Code == ErrorCode.Paused.ToWire())
        {
            DropSession();
            throw new ConnectionLostException();
        }
        if (error.Code == ErrorCode.Replaced.ToWire())
        {
            DropSession();
            throw new ErdigoException(ErdigoErrorKind.Replaced, receiverError: error);
        }
        if (error.Code == ErrorCode.StaleSeq.ToWire() || error.Code == ErrorCode.SeqConflict.ToWire())
        {
            if (error.Fatal)
            {
                DropSession();
            }
            throw new ErdigoException(ErdigoErrorKind.SequenceConflict, receiverError: error, lastSeq: current.LastSeq);
        }
        if (error.Fatal)
        {
            DropSession();
        }
        else if (error.Seq == seq && ErrorCodes.FromWire(error.Code) is { } code && !code.IsRetryable())
        {
            // The receiver used up this number on the failed request.
            current.LastSeq = seq;
        }
        throw new ErdigoException(ErdigoErrorKind.Rejected, receiverError: error);
    }

    // Connection

    private void DropSession() => Close();

    private async Task<Session> CurrentSessionAsync(CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (session is not null)
            {
                return session;
            }
        }

        ReceiverPairing remembered;
        lock (sync)
        {
            remembered = pairing;
        }
        // The remembered address comes first, because looking around the network takes a moment.
        if (await FirstAnswerAsync(Discovery.KnownEndpoints(target, remembered), cancellationToken).ConfigureAwait(false) is { } created)
        {
            return created;
        }
        if (target is ReceiverTarget.DiscoverTarget
            && await FirstAnswerAsync(await Discovery.BrowseAsync(cancellationToken: cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false) is { } found)
        {
            return found;
        }
        throw new ConnectionLostException();
    }

    /// <summary>Tries each endpoint in turn. A receiver that stays silent is simply not the right one.</summary>
    private async Task<Session?> FirstAnswerAsync(IReadOnlyList<ReceiverEndpoint> endpoints, CancellationToken cancellationToken)
    {
        foreach (var endpoint in endpoints)
        {
            Session created;
            try
            {
                created = await HandshakeAsync(endpoint, cancellationToken).ConfigureAwait(false);
            }
            catch (ConnectionLostException)
            {
                continue;
            }
            lock (sync)
            {
                session = created;
                lastSessionAt = MonotonicClock.Now;
                PreparePacing(created.Welcome.Limits);
            }
            RememberAddress(created.Stream);
            return created;
        }
        return null;
    }

    private void PreparePacing(Limits limits)
    {
        if (pacedLimits == limits)
        {
            return;
        }
        pacedLimits = limits;
        var now = MonotonicClock.Now;
        messageBucket = new TokenBucket(limits.MessageBurst, limits.MessagesPerSecond, now);
        pointBucket = new TokenBucket(limits.PointBurst, limits.PointsPerSecond, now);
    }

    private void RememberAddress(ByteStream stream)
    {
        if (stream.RemoteAddress is not { } address)
        {
            return;
        }
        string fingerprint;
        lock (sync)
        {
            if (pairing.LastHost == address.Host && pairing.LastPort == address.Port)
            {
                return;
            }
            pairing = pairing with { LastHost = address.Host, LastPort = address.Port };
            fingerprint = pairing.Fingerprint;
        }
        store.UpdateAddress(fingerprint, address.Host, address.Port);
    }

    private async Task<Session> HandshakeAsync(ReceiverEndpoint endpoint, CancellationToken cancellationToken)
    {
        var stream = new ByteStream(endpoint.Host, endpoint.Port);
        NoiseHandshake? handshake = null;
        NoiseTransport? transport = null;
        try
        {
            await stream.StartAsync(connectTimeout, cancellationToken).ConfigureAwait(false);
            var prologue = IntegrationV1.Preamble(IntegrationV1.Mode.Connect);
            ReceiverPairing remembered;
            lock (sync)
            {
                remembered = pairing;
            }
            handshake = new NoiseHandshake(NoisePattern.IK, isInitiator: true, prologue, identity.PrivateKey, remembered.ReceiverKey);
            // Written to disk first: the receiver ignores a counter it has already seen.
            var counter = store.NextConnectCounter(remembered.Fingerprint, (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (counter > IntegrationV1.MaxSequence)
            {
                throw new ErdigoException(ErdigoErrorKind.Storage, "this device key has used up its connection counter. Create a new key and pair again.");
            }
            var counterBytes = new byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(counterBytes, counter);
            var first = handshake.WriteMessage(counterBytes);

            stream.ArmDeadline(connectTimeout);
            await stream.SendAsync(Pairing.Concat(prologue, ByteStream.Frame(first)), cancellationToken).ConfigureAwait(false);
            var second = await stream.ReadFrameAsync(IntegrationV1.MaxHandshakeFrameLength, cancellationToken).ConfigureAwait(false);
            if (handshake.ReadMessage(second).Length != 0)
            {
                throw new ErdigoException(ErdigoErrorKind.ProtocolViolation, "the handshake reply has an unexpected payload");
            }
            transport = handshake.Split();

            var hello = new Hello(new[] { IntegrationV1.Version }, Pairing.SdkName);
            await stream.SendFrameAsync(transport.Seal(Wire.Encode(hello)), cancellationToken).ConfigureAwait(false);
            var frame = await stream.ReadFrameAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            stream.DisarmDeadline();

            switch (Wire.DecodeReceiverMessage(transport.Open(frame)))
            {
                case ReceiverMessage.WelcomeMessage { Welcome: var welcome }:
                    if (welcome.Version != IntegrationV1.Version)
                    {
                        throw new ErdigoException(ErdigoErrorKind.ProtocolViolation, "the receiver chose a version that was not offered");
                    }
                    return new Session(stream, transport, welcome);
                case ReceiverMessage.Error { Value: var error } when error.Code == ErrorCode.VersionUnsupported.ToWire():
                    throw new ErdigoException(ErdigoErrorKind.VersionUnsupported, receiverError: error, supported: error.Supported ?? Array.Empty<int>());
                case ReceiverMessage.Error { Value: var error } when error.Code == ErrorCode.Paused.ToWire():
                    throw new ConnectionLostException();
                case ReceiverMessage.Error { Value: var error }:
                    throw new ErdigoException(ErdigoErrorKind.Rejected, receiverError: error);
                default:
                    throw new ErdigoException(ErdigoErrorKind.ProtocolViolation, "the receiver did not send welcome");
            }
        }
        catch (Exception error)
        {
            stream.Dispose();
            transport?.Dispose();
            if (error is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            if (error is ErdigoException or ConnectionLostException)
            {
                throw;
            }
            if (error is WireException wire)
            {
                throw new ErdigoException(ErdigoErrorKind.ProtocolViolation, wire.Detail, innerException: wire);
            }
            throw new ConnectionLostException();
        }
        finally
        {
            handshake?.Dispose();
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> again after connection problems until the retry policy runs out. The time allowed
    /// counts from the first failure, so a long paced wait does not use it up.
    /// </summary>
    private async Task RetryingAsync(Func<Task> body, CancellationToken cancellationToken)
    {
        double? outageStart = null;
        var attempt = 0;
        while (true)
        {
            try
            {
                await body().ConfigureAwait(false);
                return;
            }
            catch (ConnectionLostException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var now = MonotonicClock.Now;
                double connectedAt;
                lock (sync)
                {
                    connectedAt = lastSessionAt;
                }
                // Having connected since then, this is a new outage.
                if (outageStart is { } begun && connectedAt > begun)
                {
                    outageStart = null;
                    attempt = 0;
                }
                var start = outageStart ?? now;
                outageStart = start;
                var delay = Backoff(attempt);
                attempt += 1;
                switch (retry)
                {
                    case RetryPolicy.NoRetry:
                        throw new ErdigoException(ErdigoErrorKind.NoAnswer);
                    case RetryPolicy.RetryUpTo upTo:
                        // Use all the time allowed, with a last try at the end of it.
                        var remaining = upTo.Time.TotalSeconds - (now - start);
                        if (remaining <= 0)
                        {
                            throw new ErdigoException(ErdigoErrorKind.NoAnswer);
                        }
                        delay = Math.Min(delay, remaining);
                        break;
                }
                await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>1, 2, 4 ... up to 60 seconds, with 20 percent jitter so several devices do not retry in step.</summary>
    internal static double Backoff(int attempt)
    {
        var baseDelay = Math.Min(60.0, Math.Pow(2.0, Math.Min(attempt, 6)));
        var jitter = 0.8 + 0.4 * (RandomNumberGenerator.GetInt32(0, 1_000_001) / 1_000_000.0);
        return baseDelay * jitter;
    }
}

/// <summary>An asynchronous lock that admits waiters strictly in the order they arrived, and lets a waiter cancel.</summary>
internal sealed class FifoLock
{
    private readonly object sync = new();
    private readonly LinkedList<TaskCompletionSource> waiters = new();
    private bool busy;

    public async Task AcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource waiter;
        LinkedListNode<TaskCompletionSource> node;
        lock (sync)
        {
            if (!busy)
            {
                busy = true;
                return;
            }
            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            node = waiters.AddLast(waiter);
        }
        using (cancellationToken.Register(() =>
        {
            bool removed;
            lock (sync)
            {
                removed = node.List is not null;
                if (removed)
                {
                    waiters.Remove(node);
                }
            }
            if (removed)
            {
                waiter.TrySetCanceled(cancellationToken);
            }
        }))
        {
            await waiter.Task.ConfigureAwait(false);
        }
    }

    public void Release()
    {
        lock (sync)
        {
            if (waiters.First is { } first)
            {
                waiters.RemoveFirst();
                first.Value.TrySetResult();
                return;
            }
            busy = false;
        }
    }
}
