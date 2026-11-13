using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Erdigo.Core;
using Erdigo.Sdk;
using Xunit;
using Receiver = Erdigo.MockReceiver.MockReceiver;

namespace Erdigo.Tests;

/// <summary>
/// How the client takes error answers, including ones the mock receiver would not give by itself. Each test replaces
/// the receiver's answer to chosen requests and records the numbers it was sent.
/// </summary>
public sealed class AnswerHandlingTests : EndToEndBase
{
    /// <summary>Answers the first <paramref name="times"/> requests as told, then lets requests run.</summary>
    private sealed class Script
    {
        private readonly object sync = new();
        private readonly List<ulong> seen = new();
        private readonly Func<RequestEnvelope, ReceiverError> answer;
        private readonly int times;

        public Script(Func<RequestEnvelope, ReceiverError> answer, int times = 1)
        {
            this.answer = answer;
            this.times = times;
        }

        public IReadOnlyList<ulong> Seen
        {
            get
            {
                lock (sync)
                {
                    return seen.ToArray();
                }
            }
        }

        public ReceiverMessage? Answer(RequestEnvelope request)
        {
            lock (sync)
            {
                seen.Add(request.Seq);
                return seen.Count <= times ? new ReceiverMessage.Error(answer(request)) : null;
            }
        }
    }

    private static int Connections(Receiver receiver) => receiver.Events.Count(happened => happened is Receiver.Event.Connected);

    private async Task<(Receiver Receiver, Paired Device)> ScriptedAsync(Script script, RetryPolicy? retry = null)
    {
        var (receiver, port) = await StartReceiverAsync();
        // Retrying for a while means a wrongly classified answer would show up as a second connection.
        var device = await PairedDeviceAsync(receiver, port, retry: retry ?? RetryPolicy.UpTo(TimeSpan.FromSeconds(10)));
        receiver.SetAnswerOverride(script.Answer);
        return (receiver, device);
    }

    // Answers that end the connection

    [Fact]
    public async Task FatalRateLimitedEndsTheConnectionWithOneError()
    {
        var script = new Script(request => new ReceiverError(ErrorCode.RateLimited, request.Seq, retryAfterMs: 50, fatal: true));
        var (receiver, device) = await ScriptedAsync(script);

        var error = await Assert.ThrowsAsync<ErdigoException>(() => device.Client.PingAsync());
        Assert.Equal(ErdigoErrorKind.Rejected, error.Kind);
        Assert.Equal(ErrorCode.RateLimited.ToWire(), error.ReceiverError!.Code);
        Assert.True(error.ReceiverError.Fatal);

        // Sent once, never resent on the closed connection or on a new one.
        Assert.Equal(new ulong[] { 1 }, script.Seen);
        Assert.Equal(1, Connections(receiver));
        Assert.Null(device.Client.Limits);
        await WaitForEventAsync(receiver, happened => happened is Receiver.Event.Disconnected);
        Assert.Equal(0UL, receiver.LastSeq(device.Identity.Fingerprint));
    }

    /// <summary>A fatal answer is about the connection, so a number it carries for another request changes nothing.</summary>
    [Theory]
    [InlineData("rate_limited", ErdigoErrorKind.Rejected, false)]
    [InlineData("rate_limited", ErdigoErrorKind.Rejected, true)]
    [InlineData("too_many_errors", ErdigoErrorKind.Rejected, false)]
    [InlineData("too_many_errors", ErdigoErrorKind.Rejected, true)]
    [InlineData("internal_error", ErdigoErrorKind.Rejected, false)]
    [InlineData("replaced", ErdigoErrorKind.Replaced, false)]
    [InlineData("replaced", ErdigoErrorKind.Replaced, true)]
    [InlineData("seq_conflict", ErdigoErrorKind.SequenceConflict, false)]
    [InlineData("stale_seq", ErdigoErrorKind.SequenceConflict, true)]
    [InlineData("future_code", ErdigoErrorKind.Rejected, true)]
    public async Task FatalAnswersEndTheConnectionAsTheirCodeSays(string code, ErdigoErrorKind expected, bool numberForAnotherRequest)
    {
        var script = new Script(request => new ReceiverError(
            code, numberForAnotherRequest ? request.Seq + 7 : request.Seq, lastSeq: numberForAnotherRequest ? 1000UL : null, retryAfterMs: 50, fatal: true));
        var (receiver, device) = await ScriptedAsync(script);

        var error = await Assert.ThrowsAsync<ErdigoException>(() => device.Client.PingAsync());
        Assert.True(expected == error.Kind, $"expected {expected}, got {error.Kind}: {error.Message}");
        Assert.Equal(new ulong[] { 1 }, script.Seen);
        Assert.Equal(1, Connections(receiver));
        Assert.Null(device.Client.Limits);
        if (expected == ErdigoErrorKind.SequenceConflict)
        {
            // The number in an answer to another request is not taken as where the receiver stands.
            Assert.Equal(0UL, error.LastSeq);
        }
    }

    [Fact]
    public async Task PausedStillMeansReconnectAndResendWithTheSameNumber()
    {
        var script = new Script(request => new ReceiverError(ErrorCode.Paused, fatal: true));
        var (receiver, device) = await ScriptedAsync(script);

        await device.Client.PingAsync();
        Assert.Equal(new ulong[] { 1, 1 }, script.Seen);
        Assert.Equal(2, Connections(receiver));
        Assert.Equal(1UL, receiver.LastSeq(device.Identity.Fingerprint));
    }

    [Fact]
    public async Task PausedWithoutRetryingReportsNoAnswer()
    {
        var script = new Script(request => new ReceiverError(ErrorCode.Paused, fatal: true));
        var (_, device) = await ScriptedAsync(script, RetryPolicy.None);
        await ExpectAsync(ErdigoErrorKind.NoAnswer, () => device.Client.PingAsync());
        Assert.Equal(new ulong[] { 1 }, script.Seen);
    }

    // Answers that keep the connection

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RateLimitedIsResentWithTheSameNumberOnTheSameConnection(bool answerCarriesTheNumber)
    {
        // A receiver leaves out the number when it limits a message before reading it.
        var script = new Script(request => new ReceiverError(ErrorCode.RateLimited, answerCarriesTheNumber ? request.Seq : null, retryAfterMs: 20), times: 2);
        var (receiver, device) = await ScriptedAsync(script);

        await device.Client.PingAsync();
        await device.Client.PingAsync();
        Assert.Equal(new ulong[] { 1, 1, 1, 2 }, script.Seen);
        Assert.Equal(1, Connections(receiver));
        Assert.Equal(2UL, receiver.LastSeq(device.Identity.Fingerprint));
    }

    [Fact]
    public async Task ReceiverThatNeverStopsLimitingIsGivenUpOn()
    {
        var script = new Script(request => new ReceiverError(ErrorCode.RateLimited, request.Seq, retryAfterMs: 1), times: int.MaxValue);
        var (receiver, device) = await ScriptedAsync(script);

        await ExpectRejectedAsync(ErrorCode.RateLimited, () => device.Client.PingAsync());
        // The first answer and twenty more, all for the same number.
        Assert.Equal(Enumerable.Repeat(1UL, 21), script.Seen);
        Assert.Equal(1, Connections(receiver));
    }

    [Theory]
    [InlineData("not_found")]
    [InlineData("rate_limited")]
    [InlineData("stale_seq")]
    public async Task AnswerForAnotherNumberIsAProtocolViolationAndChangesNoNumbers(string code)
    {
        var script = new Script(request => new ReceiverError(code, request.Seq + 5, lastSeq: 1000, retryAfterMs: 20));
        var (receiver, device) = await ScriptedAsync(script);

        await ExpectAsync(ErdigoErrorKind.ProtocolViolation, () => device.Client.PingAsync());
        Assert.Null(device.Client.Limits);

        // The next request starts from what the receiver says on a new connection, not from the stray number.
        await device.Client.PingAsync();
        Assert.Equal(new ulong[] { 1, 1 }, script.Seen);
        Assert.Equal(1UL, receiver.LastSeq(device.Identity.Fingerprint));
    }

    [Fact]
    public async Task StaleNumberForThisRequestMovesTheNumberOn()
    {
        var script = new Script(request => new ReceiverError(ErrorCode.StaleSeq, request.Seq, lastSeq: 10));
        var (receiver, device) = await ScriptedAsync(script);

        var error = await Assert.ThrowsAsync<ErdigoException>(() => device.Client.PingAsync());
        Assert.Equal(ErdigoErrorKind.SequenceConflict, error.Kind);
        Assert.Equal(10UL, error.LastSeq);

        await device.Client.PingAsync();
        Assert.Equal(new ulong[] { 1, 11 }, script.Seen);
        Assert.Equal(1, Connections(receiver));
        Assert.Equal(11UL, receiver.LastSeq(device.Identity.Fingerprint));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectionKeepsTheConnectionAndUsesUpTheNumberOnlyWhenItSaysWhichRequest(bool answerCarriesTheNumber)
    {
        var script = new Script(request => new ReceiverError(ErrorCode.NotFound, answerCarriesTheNumber ? request.Seq : null));
        var (receiver, device) = await ScriptedAsync(script);

        await ExpectRejectedAsync(ErrorCode.NotFound, () => device.Client.MoveAsync("missing", Palace));
        Assert.NotNull(device.Client.Limits);

        await device.Client.PingAsync();
        Assert.Equal(answerCarriesTheNumber ? new ulong[] { 1, 2 } : new ulong[] { 1, 1 }, script.Seen);
        Assert.Equal(1, Connections(receiver));
    }
}
