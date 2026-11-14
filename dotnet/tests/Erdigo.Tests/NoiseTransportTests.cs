using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Erdigo.Core;
using Xunit;

namespace Erdigo.Tests;

/// <summary>
/// A handshake yields one transport, and a transport uses each key and nonce pair once, however it is shared. Reusing
/// a pair would let someone who sees two messages recover their contents.
/// </summary>
public sealed class NoiseTransportTests
{
    private static (NoiseHandshake Device, NoiseHandshake Receiver) CompletedHandshakes()
    {
        var deviceKey = Noise.NewPrivateKey();
        var receiverKey = Noise.NewPrivateKey();
        var prologue = Encoding.ASCII.GetBytes("test");
        var device = new NoiseHandshake(NoisePattern.IK, isInitiator: true, prologue, deviceKey, Noise.PublicKey(receiverKey));
        var receiver = new NoiseHandshake(NoisePattern.IK, isInitiator: false, prologue, receiverKey);
        receiver.ReadMessage(device.WriteMessage());
        device.ReadMessage(receiver.WriteMessage());
        return (device, receiver);
    }

    /// <summary>Runs <paramref name="body"/> on many threads at once, each result in its own slot.</summary>
    private static T[] ConcurrentResults<T>(int count, Func<int, T> body)
    {
        var results = new T[count];
        Parallel.For(0, count, index => results[index] = body(index));
        return results;
    }

    /// <summary>Runs <paramref name="body"/> on real threads that all begin at the same moment.</summary>
    private static List<T> SimultaneousResults<T>(int count, Func<T> body)
    {
        using var start = new Barrier(count);
        var results = new ConcurrentBag<T>();
        var threads = Enumerable.Range(0, count).Select(_ => new Thread(() =>
        {
            start.SignalAndWait();
            results.Add(body());
        })).ToList();
        threads.ForEach(thread => thread.Start());
        threads.ForEach(thread => thread.Join());
        return results.ToList();
    }

    private static readonly byte[] Same = Encoding.ASCII.GetBytes("same message");

    // Splitting

    [Fact]
    public void SecondSplitIsRefused()
    {
        var (device, receiver) = CompletedHandshakes();
        foreach (var handshake in new[] { device, receiver })
        {
            handshake.Split();
            for (var round = 0; round < 2; round++)
            {
                Assert.Equal(NoiseError.AlreadySplit, Assert.Throws<NoiseException>(() => handshake.Split()).Error);
            }
        }
    }

    [Fact]
    public void SplitTooEarlyDoesNotUseUpTheSplit()
    {
        var deviceKey = Noise.NewPrivateKey();
        var receiverKey = Noise.NewPrivateKey();
        var device = new NoiseHandshake(NoisePattern.IK, isInitiator: true, Array.Empty<byte>(), deviceKey, Noise.PublicKey(receiverKey));
        var receiver = new NoiseHandshake(NoisePattern.IK, isInitiator: false, Array.Empty<byte>(), receiverKey);
        Assert.Equal(NoiseError.WrongHandshakeState, Assert.Throws<NoiseException>(() => device.Split()).Error);
        receiver.ReadMessage(device.WriteMessage());
        device.ReadMessage(receiver.WriteMessage());
        device.Split();
    }

    [Fact]
    public void ConcurrentSplitsSucceedOnce()
    {
        for (var round = 0; round < 2000; round++)
        {
            var (device, _) = CompletedHandshakes();
            var outcomes = SimultaneousResults(4, () =>
            {
                try
                {
                    device.Split();
                    return (NoiseError?)null;
                }
                catch (NoiseException error)
                {
                    return error.Error;
                }
            });
            Assert.Equal(1, outcomes.Count(outcome => outcome is null));
            Assert.All(outcomes.Where(outcome => outcome is not null), outcome => Assert.Equal(NoiseError.AlreadySplit, outcome));
        }
    }

    // Sharing a transport

    [Fact]
    public void TwoReferencesShareOneState()
    {
        var (device, receiver) = NoiseVectorTests.EstablishedPair();
        var alias = device;
        var first = device.Seal(Same);
        var second = alias.Seal(Same);
        Assert.NotEqual(first, second);
        Assert.Equal(Same, receiver.Open(first));
        Assert.Equal(Same, receiver.Open(second));
        // Traffic the other way goes on from the same state, through either reference.
        Assert.Equal(Same, alias.Open(receiver.Seal(Same)));
        Assert.Equal(Same, device.Open(receiver.Seal(Same)));
    }

    [Fact]
    public void ConcurrentSealsNeverRepeatANonce()
    {
        var (device, _) = NoiseVectorTests.EstablishedPair();
        const int iterations = 20_000;
        var sealedMessages = ConcurrentResults(iterations, _ => Convert.ToHexString(device.Seal(Same)));
        Assert.Equal(iterations, sealedMessages.Distinct().Count());
    }

    [Fact]
    public void ConcurrentSealsUseEveryNonceInTurn()
    {
        var (device, receiver) = NoiseVectorTests.EstablishedPair();
        const int iterations = 400;
        var waiting = ConcurrentResults(iterations, _ => device.Seal(Same)).ToList();
        // The peer opens messages only in order, so each step must find exactly one that fits.
        var opened = 0;
        while (true)
        {
            var next = waiting.FindIndex(candidate =>
            {
                try
                {
                    return receiver.Open(candidate).AsSpan().SequenceEqual(Same);
                }
                catch (NoiseException)
                {
                    return false;
                }
            });
            if (next < 0)
            {
                break;
            }
            waiting.RemoveAt(next);
            opened += 1;
        }
        Assert.Equal(iterations, opened);
    }

    [Fact]
    public void ConcurrentOpensAcceptAMessageOnce()
    {
        var (device, receiver) = NoiseVectorTests.EstablishedPair();
        for (var round = 0; round < 2000; round++)
        {
            var sealedMessage = device.Seal(Same);
            var opened = ConcurrentResults(8, _ =>
            {
                try
                {
                    return receiver.Open(sealedMessage);
                }
                catch (NoiseException)
                {
                    return null;
                }
            });
            Assert.Single(opened, result => result is not null);
        }
    }

    [Fact]
    public void SealingAndOpeningTogetherKeepBothDirectionsIntact()
    {
        var (device, receiver) = NoiseVectorTests.EstablishedPair();
        const int count = 5000;
        var incoming = Enumerable.Range(0, count).Select(index => receiver.Seal(Encoding.ASCII.GetBytes($"to device {index}"))).ToList();
        var sealedMessages = new List<byte[]>();
        var openedMessages = new List<string>();
        Parallel.Invoke(
            () =>
            {
                for (var index = 0; index < count; index++)
                {
                    sealedMessages.Add(device.Seal(Encoding.ASCII.GetBytes($"to receiver {index}")));
                }
            },
            () =>
            {
                for (var index = 0; index < count; index++)
                {
                    openedMessages.Add(Encoding.ASCII.GetString(device.Open(incoming[index])));
                }
            });
        Assert.Equal(Enumerable.Range(0, count).Select(index => $"to device {index}"), openedMessages);
        Assert.Equal(Enumerable.Range(0, count).Select(index => $"to receiver {index}"), sealedMessages.Select(message => Encoding.ASCII.GetString(receiver.Open(message))));
    }

    // Limits

    [Fact]
    public void TheNonceCounterStopsRatherThanWrappingAround()
    {
        var forward = Enumerable.Repeat((byte)0x11, 32).ToArray();
        var backward = Enumerable.Repeat((byte)0x22, 32).ToArray();
        NoiseTransport Transport(byte[] sendKey, byte[] receiveKey)
        {
            var send = new NoiseCipherState();
            var receive = new NoiseCipherState();
            send.Initialize(sendKey, ulong.MaxValue - 1);
            receive.Initialize(receiveKey, ulong.MaxValue - 1);
            return new NoiseTransport(send, receive, Array.Empty<byte>());
        }
        var sender = Transport(forward, backward);
        var peer = Transport(backward, forward);
        var plaintext = Encoding.ASCII.GetBytes("last message");

        // The last number the protocol allows still works.
        var last = sender.Seal(plaintext);
        Assert.Equal(plaintext, peer.Open(last));

        // After that there is no next number, however often it is tried.
        for (var round = 0; round < 2; round++)
        {
            Assert.Equal(NoiseError.NonceExhausted, Assert.Throws<NoiseException>(() => sender.Seal(plaintext)).Error);
            Assert.Equal(NoiseError.NonceExhausted, Assert.Throws<NoiseException>(() => peer.Open(last)).Error);
        }
    }

    [Fact]
    public void SizeChecksStillApply()
    {
        var (device, receiver) = NoiseVectorTests.EstablishedPair();
        var largest = Enumerable.Repeat((byte)0x20, Noise.MaxMessageLength - Noise.TagLength).ToArray();
        var sealedMessage = device.Seal(largest);
        Assert.Equal(Noise.MaxMessageLength, sealedMessage.Length);
        Assert.Equal(largest, receiver.Open(sealedMessage));
        Assert.Equal(NoiseError.MessageTooLarge, Assert.Throws<NoiseException>(() => device.Seal(largest.Append((byte)0x20).ToArray())).Error);
        Assert.Equal(NoiseError.MessageTooLarge, Assert.Throws<NoiseException>(() => receiver.Open(new byte[Noise.MaxMessageLength + 1])).Error);
    }

    [Fact]
    public void ADisposedTransportRefusesEverything()
    {
        var (device, receiver) = NoiseVectorTests.EstablishedPair();
        var message = device.Seal(Same);
        receiver.Dispose();
        Assert.Throws<NoiseException>(() => receiver.Open(message));
        Assert.Throws<NoiseException>(() => receiver.Seal(Same));
    }
}
