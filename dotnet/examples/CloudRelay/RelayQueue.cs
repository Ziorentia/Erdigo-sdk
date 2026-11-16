using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Erdigo.Examples.CloudRelay;

/// <summary>The limits in docs/cloud-relay.md. Tests make them smaller; nothing else changes them.</summary>
internal sealed record RelayLimits
{
    public int MaxBodyBytes { get; init; } = 2 * 1024 * 1024;

    public int MaxOpsPerPost { get; init; } = 100;

    public int MaxQueuedOps { get; init; } = 10_000;

    public long MaxQueuedBytes { get; init; } = 64L * 1024 * 1024;

    public int MaxOpsPerAnswer { get; init; } = 100;

    public int MaxAnswerBytes { get; init; } = 4 * 1024 * 1024;

    public int MaxWaitSeconds { get; init; } = 30;

    public static RelayLimits Default { get; } = new();
}

/// <summary>
/// The operations waiting for the gateway, in the order they were taken. Everything is kept in memory, so each start
/// is a new numbering with a new epoch. Safe to use from several requests at once.
/// </summary>
internal sealed class RelayQueue
{
    private sealed record Entry(long Seq, long ReceivedMs, byte[] Json);

    private readonly object sync = new();
    private readonly List<Entry> entries = new();
    private readonly RelayLimits limits;
    private readonly long keepMs;
    private readonly Func<long> clock;
    private long queuedBytes;
    private long lastSeq;
    private long lastReceivedMs;
    private long highestExpiredSeq;
    private TaskCompletionSource arrival = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RelayQueue(RelayLimits limits, int keepHours, Func<long> clock)
    {
        this.limits = limits;
        keepMs = keepHours * 3_600_000L;
        this.clock = clock;
        Epoch = NewEpoch();
    }

    public string Epoch { get; }

    /// <summary>16 random bytes, as 22 characters of URL-safe base64.</summary>
    private static string NewEpoch() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Takes the whole batch, or nothing if it would go over a limit. Returns the last sequence number, or null when full.</summary>
    public long? TryAdd(IReadOnlyList<byte[]> operations)
    {
        TaskCompletionSource woken;
        long last;
        lock (sync)
        {
            var now = Now();
            Expire(now);
            long bytes = 0;
            foreach (var operation in operations)
            {
                bytes += operation.Length;
            }
            if (entries.Count + operations.Count > limits.MaxQueuedOps || queuedBytes + bytes > limits.MaxQueuedBytes)
            {
                return null;
            }
            foreach (var operation in operations)
            {
                lastSeq += 1;
                entries.Add(new Entry(lastSeq, now, operation));
            }
            queuedBytes += bytes;
            last = lastSeq;
            woken = arrival;
            arrival = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        woken.TrySetResult();
        return last;
    }

    /// <summary>What a fetch gets, before it is written out.</summary>
    internal sealed record Batch(string Epoch, bool Reset, bool Missed, IReadOnlyList<(long Seq, long ReceivedMs, byte[] Json)> Operations, Task Arrival);

    /// <summary>
    /// Answers a fetch. With the current epoch, everything up to <paramref name="after"/> is forgotten first. Returns
    /// null if <paramref name="after"/> is past the last sequence number given.
    /// </summary>
    public Batch? Fetch(string? epoch, long after, int limit)
    {
        lock (sync)
        {
            Expire(Now());
            var matches = epoch is not null && string.Equals(epoch, Epoch, StringComparison.Ordinal);
            if (matches)
            {
                if (after > lastSeq)
                {
                    return null;
                }
                var done = 0;
                while (done < entries.Count && entries[done].Seq <= after)
                {
                    queuedBytes -= entries[done].Json.Length;
                    done += 1;
                }
                entries.RemoveRange(0, done);
            }
            else
            {
                after = 0;
            }

            var maxBytes = (long)limits.MaxAnswerBytes - EnvelopeLength(Epoch);
            var chosen = new List<(long, long, byte[])>();
            long used = 0;
            foreach (var entry in entries)
            {
                if (chosen.Count == limit)
                {
                    break;
                }
                var size = EntryLength(entry.Seq, entry.ReceivedMs, entry.Json) + (chosen.Count > 0 ? 1 : 0);
                if (chosen.Count > 0 && used + size > maxBytes)
                {
                    break;
                }
                used += size;
                chosen.Add((entry.Seq, entry.ReceivedMs, entry.Json));
            }
            return new Batch(Epoch, !matches, highestExpiredSeq > after, chosen, arrival.Task);
        }
    }

    /// <summary>Drops what has waited longer than the relay keeps operations. Also run by a timer, so memory is freed without requests.</summary>
    public void ExpireNow()
    {
        lock (sync)
        {
            Expire(Now());
        }
    }

    private void Expire(long now)
    {
        var dropped = 0;
        while (dropped < entries.Count && now - entries[dropped].ReceivedMs >= keepMs)
        {
            highestExpiredSeq = Math.Max(highestExpiredSeq, entries[dropped].Seq);
            queuedBytes -= entries[dropped].Json.Length;
            dropped += 1;
        }
        entries.RemoveRange(0, dropped);
    }

    /// <summary>The clock never goes back for the queue, so operations stay in the order of the time they were taken.</summary>
    private long Now()
    {
        lastReceivedMs = Math.Max(lastReceivedMs, clock());
        return lastReceivedMs;
    }

    // The answer, written exactly as its length is counted.

    private static readonly byte[] EntryStart = Encoding.ASCII.GetBytes("{\"seq\":");
    private static readonly byte[] EntryReceived = Encoding.ASCII.GetBytes(",\"received_ms\":");
    private static readonly byte[] EntryOp = Encoding.ASCII.GetBytes(",\"op\":");

    private static string Digits(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string EnvelopeStart(string epoch, bool reset, bool missed) =>
        $"{{\"epoch\":\"{epoch}\",\"reset\":{(reset ? "true" : "false")},\"missed\":{(missed ? "true" : "false")},\"ops\":[";

    /// <summary>The longest the envelope can be: "false" is longer than "true".</summary>
    private static long EnvelopeLength(string epoch) => EnvelopeStart(epoch, reset: false, missed: false).Length + "]}".Length;

    private static long EntryLength(long seq, long receivedMs, byte[] json) =>
        EntryStart.Length + Digits(seq).Length + EntryReceived.Length + Digits(receivedMs).Length + EntryOp.Length + json.Length + 1;

    public static byte[] Write(Batch batch)
    {
        using var output = new MemoryStream();
        output.Write(Encoding.ASCII.GetBytes(EnvelopeStart(batch.Epoch, batch.Reset, batch.Missed)));
        for (var index = 0; index < batch.Operations.Count; index++)
        {
            var (seq, receivedMs, json) = batch.Operations[index];
            if (index > 0)
            {
                output.WriteByte((byte)',');
            }
            output.Write(EntryStart);
            output.Write(Encoding.ASCII.GetBytes(Digits(seq)));
            output.Write(EntryReceived);
            output.Write(Encoding.ASCII.GetBytes(Digits(receivedMs)));
            output.Write(EntryOp);
            output.Write(json);
            output.WriteByte((byte)'}');
        }
        output.Write("]}"u8);
        return output.ToArray();
    }

    /// <summary>For tests: how much is waiting.</summary>
    internal (int Count, long Bytes) Size
    {
        get
        {
            lock (sync)
            {
                return (entries.Count, queuedBytes);
            }
        }
    }
}
