using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Erdigo.Core;
using Erdigo.MockReceiver;
using Erdigo.Sdk;
using Xunit;
using Receiver = Erdigo.MockReceiver.MockReceiver;

namespace Erdigo.Tests;

/// <summary>Runs the SDK against the mock receiver over real TCP on the loopback interface.</summary>
public abstract class EndToEndBase : IDisposable
{
    private readonly List<Receiver> receivers = new();
    private ulong rawCounter;

    private protected static readonly Coordinate Palace = new(37.5796, 126.9770);
    private protected static readonly Coordinate CityHall = new(37.5663, 126.9779);

    public void Dispose()
    {
        foreach (var receiver in receivers)
        {
            receiver.Dispose();
        }
        receivers.Clear();
    }

    private protected static Receiver.Options FastOptions() => new()
    {
        SilentDelayMin = TimeSpan.Zero,
        SilentDelayMax = TimeSpan.FromMilliseconds(10),
        AddressPenaltyBase = TimeSpan.Zero,
        HandshakeTimeout = TimeSpan.FromSeconds(3),
        MaxPreAuthConnectionsPerAddress = 8,
        // Most tests are not about pacing.
        Limits = Limits.V1 with { MessagesPerSecond = 10_000, MessageBurst = 10_000, PointsPerSecond = 10_000, PointBurst = 10_000 },
        MaxRejectionsPerMinute = 10_000,
    };

    private protected async Task<(Receiver Receiver, ushort Port)> StartReceiverAsync(Receiver.Options? options = null)
    {
        var receiver = new Receiver(options ?? FastOptions());
        receivers.Add(receiver);
        var port = await receiver.StartAsync();
        return (receiver, port);
    }

    private protected Receiver Track(Receiver receiver)
    {
        receivers.Add(receiver);
        return receiver;
    }

    internal sealed record Paired(InMemoryDeviceStore Store, DeviceIdentity Identity, ReceiverPairing Pairing, ErdigoClient Client);

    private protected static ReceiverTarget Local(ushort port) => ReceiverTarget.Address("127.0.0.1", port);

    private protected static async Task<Paired> PairedDeviceAsync(Receiver receiver, ushort port, IReadOnlyCollection<ItemKind>? kinds = null, RetryPolicy? retry = null)
    {
        var store = new InMemoryDeviceStore();
        var identity = store.LoadOrCreateIdentity();
        var ticket = receiver.OpenPairingWindow();
        var pairing = await Pairing.PairAsync(Local(port), ticket, identity, "Test device", kinds ?? ItemKinds.All.ToArray(), handshakeTimeout: TimeSpan.FromSeconds(1));
        store.Save(pairing);
        var client = new ErdigoClient(identity, pairing, store, Local(port), retry ?? RetryPolicy.None, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
        return new Paired(store, identity, pairing, client);
    }

    private protected static Task<ReceiverPairing> PairAsync(ushort port, PairingTicket ticket, DeviceIdentity? identity = null, string name = "Test device", ItemKind[]? kinds = null, ConfirmReceiverKey? confirmKey = null, TimeSpan? handshakeTimeout = null) =>
        Pairing.PairAsync(Local(port), ticket, identity ?? DeviceIdentity.Generate(), name, kinds ?? new[] { ItemKind.Pin }, handshakeTimeout: handshakeTimeout ?? TimeSpan.FromSeconds(1), confirmKey: confirmKey);

    private protected static async Task<bool> TryPairAsync(ushort port, PairingTicket ticket)
    {
        try
        {
            await PairAsync(port, ticket, name: "Racer");
            return true;
        }
        catch (ErdigoException)
        {
            return false;
        }
    }

    private protected static async Task ExpectAsync(ErdigoErrorKind kind, Func<Task> body)
    {
        var error = await Assert.ThrowsAsync<ErdigoException>(body);
        Assert.True(kind == error.Kind, $"expected {kind}, got {error.Kind}: {error.Message}");
    }

    private protected static async Task ExpectRejectedAsync(ErrorCode code, Func<Task> body)
    {
        var error = await Assert.ThrowsAsync<ErdigoException>(body);
        Assert.True(error.Kind == ErdigoErrorKind.Rejected, $"expected {code.ToWire()}, got {error.Kind}: {error.Message}");
        Assert.Equal(code.ToWire(), error.ReceiverError!.Code);
    }

    /// <summary>Reads whatever the peer sends until it closes, and reports the byte count.</summary>
    private protected static async Task<int> BytesUntilCloseAsync(ByteStream stream, double timeout = 2)
    {
        stream.ArmDeadline(TimeSpan.FromSeconds(timeout));
        var total = 0;
        while (true)
        {
            try
            {
                total += (await stream.ReadExactlyAsync(1)).Length;
            }
            catch (IOException)
            {
                return total;
            }
        }
    }

    private protected static async Task<ByteStream> RawConnectAsync(ushort port)
    {
        var stream = new ByteStream("127.0.0.1", port);
        await stream.StartAsync(TimeSpan.FromSeconds(2));
        return stream;
    }

    /// <summary>Builds a first message for an ordinary connection.</summary>
    private protected static (NoiseHandshake Handshake, byte[] Bytes) ConnectMessage(byte[] deviceKey, byte[] receiverKey, ulong counter)
    {
        var prologue = IntegrationV1.Preamble(IntegrationV1.Mode.Connect);
        var handshake = new NoiseHandshake(NoisePattern.IK, isInitiator: true, prologue, deviceKey, receiverKey);
        var counterBytes = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(counterBytes, counter);
        return (handshake, Pairing.Concat(prologue, ByteStream.Frame(handshake.WriteMessage(counterBytes))));
    }

    private protected static byte[] PairingFirstMessage(PairingCode code, out NoiseHandshake handshake)
    {
        var prologue = IntegrationV1.Preamble(IntegrationV1.Mode.Pair);
        handshake = new NoiseHandshake(NoisePattern.XXpsk0, isInitiator: true, prologue, Noise.NewPrivateKey(), psk: code.PresharedKey());
        return Pairing.Concat(prologue, ByteStream.Frame(handshake.WriteMessage()));
    }

    internal sealed record RawReply(ulong? Ack = null, bool? Duplicate = null, string? Code = null, ulong? LastSeq = null, ulong? RetryAfterMs = null, bool? Fatal = null);

    internal sealed class RawSession
    {
        public RawSession(ByteStream stream, NoiseTransport transport)
        {
            Stream = stream;
            Transport = transport;
        }

        public ByteStream Stream { get; }

        public NoiseTransport Transport { get; }

        public Welcome? Welcome { get; set; }

        public async Task<RawReply> SendAsync(string json)
        {
            await Stream.SendFrameAsync(Transport.Seal(Encoding.UTF8.GetBytes(json)));
            var frame = await Stream.ReadFrameAsync();
            return Wire.DecodeReceiverMessage(Transport.Open(frame)) switch
            {
                ReceiverMessage.Ack ack => new RawReply(Ack: ack.Seq, Duplicate: ack.Duplicate),
                ReceiverMessage.Error { Value: var error } => new RawReply(Code: error.Code, LastSeq: error.LastSeq, RetryAfterMs: error.RetryAfterMs, Fatal: error.Fatal),
                _ => throw new IOException("unexpected message"),
            };
        }
    }

    private protected Task<RawSession> AuthenticatedAsync(Paired device, Receiver receiver, ushort port) =>
        AuthenticatedAsync(device.Identity, receiver.PublicKey, port);

    /// <summary>Completes a real handshake so a test can send messages the SDK would refuse to produce.</summary>
    private protected async Task<RawSession> AuthenticatedAsync(DeviceIdentity identity, byte[] receiverKey, ushort port, int[]? versions = null, bool expectWelcome = true)
    {
        // Small enough that a real client, which starts from the clock, can still connect afterwards.
        rawCounter += 1;
        var (handshake, message) = ConnectMessage(identity.PrivateKey, receiverKey, rawCounter);
        var stream = await RawConnectAsync(port);
        await stream.SendAsync(message);
        handshake.ReadMessage(await stream.ReadFrameAsync());
        var transport = handshake.Split();
        await stream.SendFrameAsync(transport.Seal(Wire.Encode(new Hello(versions ?? new[] { 1 }, "test/1"))));
        var session = new RawSession(stream, transport);
        if (expectWelcome)
        {
            var frame = await stream.ReadFrameAsync();
            session.Welcome = Wire.DecodeReceiverMessage(transport.Open(frame)) is ReceiverMessage.WelcomeMessage welcome
                ? welcome.Welcome
                : throw new IOException("expected welcome");
        }
        return session;
    }

    private protected ulong NextRawCounter() => ++rawCounter;

    /// <summary>How long the peer keeps a connection open if this side sends <paramref name="bytes"/> and then nothing more.</summary>
    private protected static async Task<double> SecondsUntilCloseAsync(ushort port, byte[] bytes, double timeout = 5)
    {
        var stream = await RawConnectAsync(port);
        if (bytes.Length > 0)
        {
            await stream.SendAsync(bytes);
        }
        var started = DateTime.UtcNow;
        var count = await BytesUntilCloseAsync(stream, timeout);
        Assert.Equal(0, count);
        return (DateTime.UtcNow - started).TotalSeconds;
    }

    /// <summary>Waits first, because a request sent a moment ago would still be on its way.</summary>
    private protected static async Task AssertHeardNoRequestAsync(Receiver receiver)
    {
        await Task.Delay(500);
        Assert.DoesNotContain(receiver.Events, happened => happened is Receiver.Event.PairingRequested);
        Assert.Empty(receiver.PairedDevices());
    }

    /// <summary>Records what a person was shown, and answers as told.</summary>
    internal sealed class KeyQuestion
    {
        private readonly object sync = new();
        private readonly List<string> asked = new();
        private readonly bool answer;
        private readonly Func<Task>? duringQuestion;

        public KeyQuestion(bool answer, Func<Task>? duringQuestion = null)
        {
            this.answer = answer;
            this.duringQuestion = duringQuestion;
        }

        public IReadOnlyList<string> Shown
        {
            get
            {
                lock (sync)
                {
                    return asked.ToList();
                }
            }
        }

        public async Task<bool> AskAsync(string key, CancellationToken cancellationToken)
        {
            lock (sync)
            {
                asked.Add(key);
            }
            if (duringQuestion is not null)
            {
                await duringQuestion();
            }
            return answer;
        }
    }

    /// <summary>A PNG of one shade, made here so that any size can be tested.</summary>
    internal static byte[] Png(int width, int height, int colourType = 2, bool interlaced = false)
    {
        var channels = colourType switch { 0 => 1, 2 => 3, 4 => 2, _ => 4 };
        var row = new byte[1 + width * channels];
        for (var index = 1; index < row.Length; index++)
        {
            row[index] = (byte)(index % channels == 1 ? 51 : index % channels == 2 ? 128 : 204);
        }
        var raw = new MemoryStream();
        using (var deflate = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            if (!interlaced)
            {
                for (var line = 0; line < height; line++)
                {
                    deflate.Write(row);
                }
            }
            else
            {
                int[] startX = { 0, 4, 0, 2, 0, 1, 0 };
                int[] startY = { 0, 0, 4, 0, 2, 0, 1 };
                int[] stepX = { 8, 8, 4, 4, 2, 2, 1 };
                int[] stepY = { 8, 8, 8, 4, 4, 2, 2 };
                for (var pass = 0; pass < 7; pass++)
                {
                    var passWidth = width <= startX[pass] ? 0 : (width - startX[pass] + stepX[pass] - 1) / stepX[pass];
                    var passHeight = height <= startY[pass] ? 0 : (height - startY[pass] + stepY[pass] - 1) / stepY[pass];
                    if (passWidth == 0 || passHeight == 0)
                    {
                        continue;
                    }
                    for (var line = 0; line < passHeight; line++)
                    {
                        deflate.Write(row.AsSpan(0, 1 + passWidth * channels));
                    }
                }
            }
        }
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = (byte)colourType;
        header[12] = (byte)(interlaced ? 1 : 0);
        var output = new MemoryStream();
        output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        WriteChunk(output, "IHDR", header);
        WriteChunk(output, "tEXt", Encoding.ASCII.GetBytes("Comment\0made by a test"));
        WriteChunk(output, "IDAT", raw.ToArray());
        WriteChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    internal static void WriteChunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        var typed = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        output.Write(typed);
        var crc = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, PhotoPipelineAccess.Crc32(typed));
        output.Write(crc);
    }

    /// <summary>
    /// A plain socket that can stop reading. A peer that stops reading has to be played by something that does not
    /// keep taking data on its own.
    /// </summary>
    internal sealed class PlainSocketPeer : IDisposable
    {
        private readonly Socket socket;

        public PlainSocketPeer(ushort port)
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                ReceiveBufferSize = 4096,
                ReceiveTimeout = 5000,
            };
            socket.Connect(new IPEndPoint(IPAddress.Loopback, port));
        }

        public void Dispose() => socket.Dispose();

        public void WriteAll(byte[] data)
        {
            var sent = 0;
            while (sent < data.Length)
            {
                sent += socket.Send(data, sent, data.Length - sent, SocketFlags.None);
            }
        }

        /// <summary>From here on, <see cref="WriteSome"/> returns at once.</summary>
        public void StopWaitingOnWrites() => socket.Blocking = false;

        /// <summary>How many bytes were taken without waiting, or null once the connection has gone.</summary>
        public int? WriteSome(byte[] data)
        {
            try
            {
                return socket.Send(data, 0, data.Length, SocketFlags.None, out var error) is var sent && error == SocketError.Success
                    ? sent
                    : error == SocketError.WouldBlock ? 0 : null;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
        }

        public byte[] ReadFrame()
        {
            var header = ReadExactly(2);
            return ReadExactly((header[0] << 8) | header[1]);
        }

        private byte[] ReadExactly(int count)
        {
            var bytes = new byte[count];
            var filled = 0;
            while (filled < count)
            {
                var got = socket.Receive(bytes, filled, count - filled, SocketFlags.None);
                if (got <= 0)
                {
                    throw new IOException("closed");
                }
                filled += got;
            }
            return bytes;
        }
    }
}

internal static class PhotoPipelineAccess
{
    public static uint Crc32(byte[] data) => PhotoPipeline.Crc32(data);
}
