using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Erdigo.Core;
using Erdigo.Sdk;
using Xunit;
using Receiver = Erdigo.MockReceiver.MockReceiver;

namespace Erdigo.Tests;

/// <summary>
/// What the retry policy limits, and how a caller limits the rest, as the README describes: the policy covers one
/// outage at a time, and a cancellation token covers the whole request.
/// </summary>
public sealed class RetryTimeTests : EndToEndBase
{
    private static int Connections(Receiver receiver) => receiver.Events.Count(happened => happened is Receiver.Event.Connected);

    private static int SilentDrops(Receiver receiver) => receiver.Events.Count(happened => happened is Receiver.Event.SilentlyDropped);

    [Fact]
    public async Task ConnectionsThatKeepFailingAfterConnectingAreBoundedOnlyByTheCaller()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port, retry: RetryPolicy.UpTo(TimeSpan.FromSeconds(0.5)));
        // Every connection works until the request arrives, and then ends.
        var sent = 0;
        receiver.SetAnswerOverride(_ =>
        {
            Interlocked.Increment(ref sent);
            return new ReceiverMessage.Error(new ReceiverError(ErrorCode.Paused, fatal: true));
        });

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => device.Client.PingAsync(deadline.Token));

        // Well past the half second the policy allows for each outage, because each new connection starts it again.
        Assert.True(watch.Elapsed > TimeSpan.FromSeconds(2.5), $"ended after {watch.Elapsed}");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"ended after {watch.Elapsed}");
        var connections = Connections(receiver);
        Assert.True(connections >= 3, $"{connections} connections");
        // The same request on each, except perhaps the last, if the deadline came before it was sent there.
        Assert.InRange(Volatile.Read(ref sent), connections - 1, connections);

        // The cancelled request leaves nothing behind.
        receiver.SetAnswerOverride(null);
        await device.Client.PingAsync();
        Assert.Equal(1UL, receiver.LastSeq(device.Identity.Fingerprint));
    }

    [Fact]
    public async Task ForeverKeepsTryingThroughAnOutageAndThenSucceeds()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port, retry: RetryPolicy.Forever);
        await device.Client.PingAsync();

        receiver.SetPaused(true);
        var dropsBefore = SilentDrops(receiver);
        var ping = device.Client.PingAsync();
        // Let it fail to connect twice, so that it is waiting between attempts rather than giving up.
        await WaitForEventAsync(receiver, _ => SilentDrops(receiver) >= dropsBefore + 2, timeout: 15);
        Assert.False(ping.IsCompleted);

        receiver.SetPaused(false);
        await ping.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(2UL, receiver.LastSeq(device.Identity.Fingerprint));
    }

    [Fact]
    public async Task ACancelledRequestInLineDoesNotHoldUpTheOnesBehindIt()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port, retry: RetryPolicy.Forever);
        await device.Client.PingAsync();

        receiver.SetPaused(true);
        var first = device.Client.PutPinAsync("first", Palace);
        using var cancel = new CancellationTokenSource();
        var second = device.Client.PutPinAsync("second", Palace, cancellationToken: cancel.Token);
        var third = device.Client.PutPinAsync("third", Palace);
        await WaitForEventAsync(receiver, happened => happened is Receiver.Event.SilentlyDropped);

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(first.IsCompleted);
        Assert.False(third.IsCompleted);

        receiver.SetPaused(false);
        await Task.WhenAll(first, third).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(new[] { "first", "third" }, receiver.Items(device.Identity.Fingerprint).Select(item => item.Id).OrderBy(id => id));
        Assert.Equal(3UL, receiver.LastSeq(device.Identity.Fingerprint));
    }

    [Fact]
    public async Task ADeadlineAlsoEndsALongPacedWait()
    {
        var options = FastOptions();
        options.Limits = options.Limits with { PointsPerSecond = 1, PointBurst = 100 };
        var (receiver, port) = await StartReceiverAsync(options);
        var device = await PairedDeviceAsync(receiver, port, retry: RetryPolicy.Forever);
        var point = new TrackPoint(Palace);
        await device.Client.AppendTrackAsync("w", Enumerable.Repeat(point, 100).ToArray());

        // The next hundred points would wait about a hundred seconds for their allowance, which is not an outage.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => device.Client.AppendTrackAsync("w", Enumerable.Repeat(point, 100).ToArray(), cancellationToken: deadline.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"ended after {watch.Elapsed}");
        Assert.Single(receiver.Items(device.Identity.Fingerprint));
        Assert.Equal(100, receiver.Items(device.Identity.Fingerprint)[0].Points.Count);
    }
}
