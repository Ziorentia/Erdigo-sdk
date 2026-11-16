using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Erdigo.Core;
using Erdigo.Sdk;

namespace Erdigo.Examples.CloudGateway;

/// <summary>How long the gateway waits for things. Tests make them shorter; the defaults are those of docs/cloud-relay.md.</summary>
internal sealed record GatewayTimings
{
    /// <summary>How long the relay may hold a fetch open while nothing is waiting.</summary>
    public int WaitSeconds { get; init; } = 25;

    /// <summary>Comfortably above <see cref="WaitSeconds"/>, so a held fetch is not cut off.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// With --once, which never asks the relay to wait, how long one fetch may take: short enough that a relay that takes
    /// the connection and never answers is given up on in about <see cref="GiveUpAfter"/>.
    /// </summary>
    public TimeSpan OnceRequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// With --once, how long the relay may stay out of reach, or an operation stay in trouble with the app, before the
    /// gateway gives up.
    /// </summary>
    public TimeSpan GiveUpAfter { get; init; } = TimeSpan.FromSeconds(30);

    public double FirstRetrySeconds { get; init; } = 1;

    public double LongestRetrySeconds { get; init; } = 60;

    public TimeSpan SdkConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long the SDK waits for the app's answer to one message before it drops the connection: its default.</summary>
    public TimeSpan SdkAckTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long an operation may be in trouble, or go on without a connection to the app, before the gateway says so.</summary>
    public TimeSpan NoAnswerNotice { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The least time between two fetches that found nothing, for a relay that cannot hold a fetch open.</summary>
    public TimeSpan EmptyFetchInterval { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>A problem that stops the gateway, with exit code 1.</summary>
internal sealed class GatewayStoppedException : Exception
{
    public GatewayStoppedException(string message)
        : base(message)
    {
    }
}

/// <summary>Waits 1, 2, 4 ... up to 60 seconds between attempts, with 20 percent random variation (spec section 13).</summary>
internal sealed class Retry
{
    private readonly GatewayTimings timings;
    private readonly bool givesUp;
    private long? outageStarted;
    private int attempt;

    public Retry(GatewayTimings timings, bool givesUp)
    {
        this.timings = timings;
        this.givesUp = givesUp;
    }

    /// <summary>
    /// The time to wait before the next attempt, or null to give up. The time allowed counts from the first call, or
    /// from <paramref name="troubleBegan"/> (a <see cref="Stopwatch"/> timestamp) if that is earlier.
    /// </summary>
    public TimeSpan? Next(long? troubleBegan = null)
    {
        var now = Stopwatch.GetTimestamp();
        outageStarted = Math.Min(outageStarted ?? now, troubleBegan ?? now);
        var seconds = Math.Min(timings.LongestRetrySeconds, timings.FirstRetrySeconds * Math.Pow(2, Math.Min(attempt, 16)));
        seconds *= 0.8 + (0.4 * Random.Shared.NextDouble());
        attempt += 1;
        if (givesUp)
        {
            // All the time allowed is used, with a last attempt at the end of it, as the SDK does.
            var remaining = (timings.GiveUpAfter - Stopwatch.GetElapsedTime(outageStarted.Value)).TotalSeconds;
            if (remaining <= 0)
            {
                return null;
            }
            seconds = Math.Min(seconds, remaining);
        }
        return TimeSpan.FromSeconds(seconds);
    }

    public void Reset()
    {
        outageStarted = null;
        attempt = 0;
    }

    public static string Describe(TimeSpan delay)
    {
        var whole = Math.Max(1, (int)Math.Round(delay.TotalSeconds));
        return whole == 1 ? "1 second" : string.Create(CultureInfo.InvariantCulture, $"{whole} seconds");
    }
}

/// <summary>
/// What the gateway knows about one operation's trouble reaching the app, across all the tries for it. The SDK counts
/// its own time from the last connection it made, so an app that takes every connection but never answers, or whose
/// answers are all lost, would keep it trying for ever. This counts from the first sign of trouble until the app takes
/// the operation, however many connections that takes. Time the SDK spends pacing points before sending is no trouble.
/// </summary>
internal sealed class Outage
{
    private readonly object sync = new();
    private long? began;
    private Limits? sendingOn;
    private bool said;

    /// <summary>When the first sign of trouble showed, as a <see cref="Stopwatch"/> timestamp, or null while there has been none.</summary>
    public long? Since
    {
        get
        {
            lock (sync)
            {
                return began;
            }
        }
    }

    /// <summary>Notes trouble that showed at <paramref name="moment"/>, and returns when the trouble began.</summary>
    public long Note(long moment)
    {
        lock (sync)
        {
            began = Math.Min(began ?? moment, moment);
            return began.Value;
        }
    }

    /// <summary>
    /// The connection the SDK is sending this try over, or null before it has one. A connection is known by the limits
    /// its welcome announced, a new object for each connection. The SDK drops that connection only when sending failed.
    /// </summary>
    public Limits? SendingOn
    {
        get
        {
            lock (sync)
            {
                return sendingOn;
            }
        }
        set
        {
            lock (sync)
            {
                sendingOn = value;
            }
        }
    }

    /// <summary>True the first time only, so that the gateway says once per operation that the app does not answer.</summary>
    public bool TakeNotice()
    {
        lock (sync)
        {
            if (said)
            {
                return false;
            }
            said = true;
            return true;
        }
    }
}

/// <summary>
/// Fetches from the relay and applies each operation in order through the SDK. An operation is acknowledged to the
/// relay, by the next fetch, only after the app has taken or refused it, so delivery is at least once.
/// </summary>
internal sealed class GatewayRun
{
    private enum Outcome
    {
        Applied,
        Refused,
        GaveUp,
    }

    /// <summary>How one try ended when the app did not take the operation.</summary>
    private abstract record TryEnd
    {
        /// <summary>The app refused it for good, or the SDK refused it before sending.</summary>
        public sealed record Refused(string Code) : TryEnd;

        /// <summary>
        /// An answer that says "not now", or one the SDK cannot use. The SDK does not wait these out, so the gateway
        /// does. <paramref name="Said"/> is what the app did, as the start of a line.
        /// </summary>
        public sealed record NotNow(string Said) : TryEnd;

        /// <summary>With --once, the app did not take the operation for as long as the gateway may wait.</summary>
        public sealed record GaveUp : TryEnd;
    }

    private static readonly TimeSpan WatchInterval = TimeSpan.FromMilliseconds(200);

    private readonly RelayClient relay;
    private readonly RelayAddress address;
    private readonly ErdigoClient client;
    private readonly string stateDirectory;
    private readonly bool once;
    private readonly GatewayTimings timings;
    private readonly TextWriter output;
    private readonly CancellationToken stop;

    public GatewayRun(RelayClient relay, RelayAddress address, ErdigoClient client, string stateDirectory, bool once, GatewayTimings timings, TextWriter output, CancellationToken stop)
    {
        this.relay = relay;
        this.address = address;
        this.client = client;
        this.stateDirectory = stateDirectory;
        this.once = once;
        this.timings = timings;
        this.output = output;
        this.stop = stop;
    }

    /// <summary>Returns the exit code: 0 when stopped or, with --once, when the relay has nothing more; 3 when giving up.</summary>
    public async Task<int> RunAsync(RelayPlace? place)
    {
        var epoch = place?.Epoch;
        var after = place?.Seq ?? 0;
        var hadPlace = place is not null;
        // The relay answers 'missed' to every fetch until the gateway moves past what expired, which it cannot do while
        // nothing newer arrives. So it is said once for each place, not once for each fetch.
        (string Epoch, long After)? missedSaidAt = null;
        var relayRetry = new Retry(timings, givesUp: once);
        while (true)
        {
            var started = Stopwatch.GetTimestamp();
            RelayAnswer answer;
            try
            {
                answer = await relay.FetchAsync(epoch, after, once ? 0 : timings.WaitSeconds, stop).ConfigureAwait(false);
                relayRetry.Reset();
            }
            catch (RelayUnavailableException)
            {
                // The time this try took counts too, so a relay that never answers is given up on in time.
                if (relayRetry.Next(started) is not { } delay)
                {
                    Say("The relay could not be reached.");
                    return 3;
                }
                Say($"The relay could not be reached; trying again in {Retry.Describe(delay)}.");
                await Task.Delay(delay, stop).ConfigureAwait(false);
                continue;
            }

            if (answer.Reset)
            {
                if (hadPlace)
                {
                    Say("The relay started a new numbering; starting from its oldest operation.");
                }
                hadPlace = true;
                epoch = answer.Epoch;
                after = 0;
                Save(epoch, after);
            }
            if (!answer.Missed)
            {
                missedSaidAt = null;
            }
            else if (missedSaidAt != (answer.Epoch, after))
            {
                Say("Some operations expired on the relay before they were fetched.");
                missedSaidAt = (answer.Epoch, after);
            }

            if (answer.Operations.Count == 0)
            {
                // Nothing to write for now, so the app's connection is not held open.
                client.Close();
                if (once)
                {
                    return 0;
                }
                var spent = Stopwatch.GetElapsedTime(started);
                if (spent < timings.EmptyFetchInterval)
                {
                    await Task.Delay(timings.EmptyFetchInterval - spent, stop).ConfigureAwait(false);
                }
                continue;
            }

            var done = 0;
            var refused = 0;
            foreach (var (seq, operation) in answer.Operations)
            {
                var outcome = await ApplyAsync(seq, operation).ConfigureAwait(false);
                if (outcome == Outcome.GaveUp)
                {
                    if (done > 0)
                    {
                        Say(Summary(done, after, refused));
                    }
                    Say("The Erdigo app could not be reached. The operations stay on the relay.");
                    return 3;
                }
                if (outcome == Outcome.Refused)
                {
                    refused += 1;
                }
                done += 1;
                after = seq;
                Save(epoch!, after);
            }
            Say(Summary(done, after, refused));
        }
    }

    private static string Summary(int done, long upTo, int refused)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"Done with {done} {(done == 1 ? "operation" : "operations")}, up to {upTo}");
        return refused == 0 ? text : string.Create(CultureInfo.InvariantCulture, $"{text} ({refused} refused)");
    }

    private async Task<Outcome> ApplyAsync(long seq, JsonValue.Object operation)
    {
        RelayOperation decoded;
        try
        {
            decoded = RelayOperation.Decode(operation);
        }
        catch (OperationRefusal refusal)
        {
            SayRefused(seq, refusal.Kind, refusal.Code);
            return Outcome.Refused;
        }

        var outage = new Outage();
        // Only for the answers the SDK does not wait out itself.
        var waits = new Retry(timings, givesUp: once);
        while (true)
        {
            var began = Stopwatch.GetTimestamp();
            switch (await TryAsync(decoded, outage).ConfigureAwait(false))
            {
                case null:
                    return Outcome.Applied;
                case TryEnd.Refused refused:
                    SayRefused(seq, decoded.Kind, refused.Code);
                    return Outcome.Refused;
                case TryEnd.NotNow notNow:
                    // A failed try is trouble from the moment it began.
                    client.Close();
                    if (waits.Next(outage.Note(began)) is not { } delay)
                    {
                        return Outcome.GaveUp;
                    }
                    // This line says when, so the one without a time is not needed for this operation any more.
                    outage.TakeNotice();
                    Say($"{notNow.Said}; trying again in {Retry.Describe(delay)}.");
                    await Task.Delay(delay, stop).ConfigureAwait(false);
                    continue;
                case TryEnd.GaveUp:
                default:
                    return Outcome.GaveUp;
            }
        }
    }

    /// <summary>
    /// One try, null when the app took the operation. A watcher runs beside it, and with --once ends it when the
    /// operation has been in trouble for as long as the gateway may wait. Ctrl+C or SIGTERM ends both.
    /// </summary>
    private async Task<TryEnd?> TryAsync(RelayOperation operation, Outage outage)
    {
        using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(stop);
        outage.SendingOn = null;
        var call = SendAsync(operation, outage, giveUp.Token);
        var outOfTime = await WatchAsync(call, outage, giveUp).ConfigureAwait(false);
        try
        {
            return await call.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (outOfTime && !stop.IsCancellationRequested)
        {
            // A request stopped half way may have been applied, which at-least-once delivery allows: the operation
            // stays on the relay. Stopping, which may have come at the same moment, ends the run with 0 rather than 3.
            return new TryEnd.GaveUp();
        }
    }

    /// <summary>
    /// Sends the operation through the SDK, null when the app took it. Connecting first keeps a failure of the
    /// connection, such as a welcome this SDK cannot use or an error answer to hello, apart from an answer about this
    /// operation. Only such an answer may refuse it.
    /// </summary>
    private async Task<TryEnd?> SendAsync(RelayOperation operation, Outage outage, CancellationToken cancellation)
    {
        try
        {
            await client.ConnectAsync(cancellation).ConfigureAwait(false);
        }
        catch (ErdigoException error)
        {
            return ClassifyConnection(error);
        }
        // From here the SDK drops this connection only when sending over it failed. A paced request keeps it, so time
        // spent waiting for the app's rate limit is no trouble.
        outage.SendingOn = client.Limits;
        try
        {
            await operation.ApplyAsync(client, cancellation).ConfigureAwait(false);
            return null;
        }
        catch (ErdigoException error)
        {
            return ClassifyAnswer(error);
        }
    }

    /// <summary>
    /// Watches one try from outside the SDK, which does not say how it is doing, and returns true if it ran out of time.
    /// </summary>
    /// <remarks>
    /// A connection the SDK made for this try and then lost means sending failed, and the trouble began then. While the
    /// SDK waits 1, 2, 4 ... up to 60 seconds and resends the same request, the gateway only watches. While the trouble
    /// lasts, or while the try goes on without any connection to the app, it says once per operation that the app does
    /// not answer. The SDK does not say when it tries next, so neither does the line. With --once it cancels the try
    /// once the trouble has lasted <see cref="GatewayTimings.GiveUpAfter"/>, however many connections the app took.
    /// </remarks>
    private async Task<bool> WatchAsync(Task call, Outage outage, CancellationTokenSource giveUp)
    {
        long? unconnectedSince = null;
        while (await Task.WhenAny(call, Task.Delay(WatchInterval)).ConfigureAwait(false) != call)
        {
            var now = Stopwatch.GetTimestamp();
            var connection = client.Limits;
            if (connection is null)
            {
                unconnectedSince ??= now;
            }
            else
            {
                unconnectedSince = null;
            }
            if (outage.SendingOn is { } sendingOn && !ReferenceEquals(connection, sendingOn))
            {
                outage.Note(now);
            }
            var troubled = outage.Since is { } since ? Stopwatch.GetElapsedTime(since, now) : TimeSpan.Zero;
            var unconnected = unconnectedSince is { } start ? Stopwatch.GetElapsedTime(start, now) : TimeSpan.Zero;
            if ((troubled >= timings.NoAnswerNotice || unconnected >= timings.NoAnswerNotice) && outage.TakeNotice())
            {
                Say("No answer from the Erdigo app; trying again.");
            }
            if (once && outage.Since is not null && troubled >= timings.GiveUpAfter)
            {
                giveUp.Cancel();
                return true;
            }
        }
        return false;
    }

    /// <summary>Nothing about this operation was sent yet, so nothing here can refuse it.</summary>
    private static TryEnd ClassifyConnection(ErdigoException error) => error.Kind switch
    {
        ErdigoErrorKind.NoAnswer or ErdigoErrorKind.VersionUnsupported or ErdigoErrorKind.Storage => ClassifyAnswer(error),
        ErdigoErrorKind.ProtocolViolation => new TryEnd.NotNow(BrokeTheProtocol),
        ErdigoErrorKind.Rejected => new TryEnd.NotNow($"The Erdigo app answered {Code(error)}"),
        _ => new TryEnd.NotNow("The Erdigo app turned the connection away"),
    };

    private const string BrokeTheProtocol = "The Erdigo app answered in a way that breaks the protocol";

    /// <summary>
    /// An error from sending this operation. A lost connection never comes here: the SDK waits for the app and sends
    /// the request again, until it is cancelled or, with --once, gives up with NoAnswer (or the watcher gives up first).
    /// </summary>
    private static TryEnd ClassifyAnswer(ErdigoException error)
    {
        switch (error.Kind)
        {
            case ErdigoErrorKind.NoAnswer:
                // The SDK kept the request for as long as --once allows. Without --once it never gives up.
                return new TryEnd.GaveUp();
            case ErdigoErrorKind.Rejected when error.ReceiverError?.Code is "rate_limited" or "internal_error" or "too_many_errors" or "paused":
                // Not the operation's fault. rate_limited, paused and internal_error leave the request unapplied (spec
                // section 7) and too_many_errors comes instead of running it, so a new request is safe.
                return new TryEnd.NotNow($"The Erdigo app answered {error.ReceiverError!.Code}");
            case ErdigoErrorKind.ProtocolViolation:
                // A receiver whose answers break the protocol is tried again too, though it may have applied the
                // request, which at-least-once delivery allows.
                return new TryEnd.NotNow(BrokeTheProtocol);
            case ErdigoErrorKind.Replaced or ErdigoErrorKind.SequenceConflict:
                // Sending the operation again as a new request could apply it twice.
                throw new GatewayStoppedException("another program is writing with this gateway's key. Run one gateway per profile.");
            case ErdigoErrorKind.VersionUnsupported or ErdigoErrorKind.Storage:
                throw new GatewayStoppedException(error.Message);
            default:
                return new TryEnd.Refused(Code(error));
        }
    }

    /// <summary>The code printed for a refusal: the app's own code, or the SDK's when it refused before sending.</summary>
    private static string Code(ErdigoException error) => error.Kind switch
    {
        ErdigoErrorKind.NotPermitted => "not_permitted",
        // Only a code that looks like one is printed, since it comes from the other side.
        ErdigoErrorKind.Rejected => error.ReceiverError?.Code is { Length: > 0 and <= 40 } code && IsCodeText(code) ? code : "rejected",
        ErdigoErrorKind.InvalidArgument => error.InnerException is WireException wire ? wire.Code.ToWire() : "invalid_argument",
        _ => "refused",
    };

    private static bool IsCodeText(string code)
    {
        foreach (var character in code)
        {
            if (character is not ((>= 'a' and <= 'z') or '_'))
            {
                return false;
            }
        }
        return true;
    }

    private void SayRefused(long seq, string kind, string code) =>
        Say(string.Create(CultureInfo.InvariantCulture, $"Refused operation {seq} ({kind}): {code}"));

    private void Save(string epoch, long seq)
    {
        try
        {
            RelayState.Save(stateDirectory, address, new RelayPlace(epoch, seq));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new GatewayStoppedException($"could not save {RelayState.FileName} in the state folder");
        }
    }

    private void Say(string line)
    {
        output.WriteLine(line);
        output.Flush();
    }
}
