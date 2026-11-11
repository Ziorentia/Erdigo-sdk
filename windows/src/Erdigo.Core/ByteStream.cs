using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Erdigo.Core;

/// <summary>Why a <see cref="ByteStream"/> operation failed.</summary>
public enum ByteStreamFailure
{
    Closed,
    TimedOut,
    FrameTooLarge,
    ConnectionFailed,
}

public sealed class ByteStreamException : IOException
{
    public ByteStreamException(ByteStreamFailure failure)
        : base($"connection: {failure}")
    {
        Failure = failure;
    }

    public ByteStreamFailure Failure { get; }
}

/// <summary>
/// An ordered byte stream over TCP with the integration-v1 framing: a two-byte big-endian length followed by one
/// Noise message.
/// </summary>
/// <remarks>One task at a time may read, and one task at a time may write.</remarks>
public sealed class ByteStream : IDisposable
{
    private readonly Socket socket;
    private readonly string? host;
    private readonly ushort port;
    private readonly object sync = new();
    private byte[] buffer = new byte[4096];
    private int bufferStart;
    private int bufferEnd;
    private bool deadlineExpired;
    private int deadlineGeneration;
    private Timer? deadlineTimer;

    /// <summary>Wraps a connection a listener accepted.</summary>
    public ByteStream(Socket connected)
    {
        socket = connected ?? throw new ArgumentNullException(nameof(connected));
        socket.NoDelay = true;
    }

    /// <summary>Prepares a connection to <paramref name="host"/>. <see cref="StartAsync"/> makes it.</summary>
    public ByteStream(string host, ushort port)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (port == 0)
        {
            throw new ByteStreamException(ByteStreamFailure.ConnectionFailed);
        }
        this.host = host;
        this.port = port;
        socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
    }

    /// <summary>Identifies this stream in a receiver's bookkeeping.</summary>
    public Guid Identifier { get; } = Guid.NewGuid();

    /// <summary>Bytes received but not yet read. After a failed read, this says whether the peer sent anything.</summary>
    public int BufferedByteCount
    {
        get
        {
            lock (sync)
            {
                return bufferEnd - bufferStart;
            }
        }
    }

    /// <summary>
    /// Waits until the connection is usable, or fails. A connection the network layer would keep retrying counts as
    /// failed, so that callers can apply their own retry policy.
    /// </summary>
    public async Task StartAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (host is null)
        {
            return;
        }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            if (IPAddress.TryParse(host, out var address))
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), limit.Token).ConfigureAwait(false);
            }
            else
            {
                await socket.ConnectAsync(host, port, limit.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException or ArgumentException)
        {
            Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new ByteStreamException(limit.IsCancellationRequested ? ByteStreamFailure.TimedOut : ByteStreamFailure.ConnectionFailed);
        }
    }

    /// <summary>
    /// Closes the connection after <paramref name="time"/> unless <see cref="DisarmDeadline"/> is called first.
    /// Pending reads and writes then fail with <see cref="ByteStreamFailure.TimedOut"/>.
    /// </summary>
    public void ArmDeadline(TimeSpan time)
    {
        lock (sync)
        {
            deadlineGeneration += 1;
            var generation = deadlineGeneration;
            deadlineTimer?.Dispose();
            deadlineTimer = new Timer(_ => DeadlinePassed(generation), null, Clamp(time), Timeout.InfiniteTimeSpan);
        }
    }

    private static TimeSpan Clamp(TimeSpan time) =>
        time < TimeSpan.Zero ? TimeSpan.Zero : time > TimeSpan.FromDays(24) ? TimeSpan.FromDays(24) : time;

    private void DeadlinePassed(int generation)
    {
        lock (sync)
        {
            if (generation != deadlineGeneration)
            {
                return;
            }
            deadlineExpired = true;
        }
        Cancel();
    }

    public void DisarmDeadline()
    {
        lock (sync)
        {
            deadlineGeneration += 1;
            deadlineTimer?.Dispose();
            deadlineTimer = null;
        }
    }

    /// <summary>
    /// Stops sending once everything already sent has gone out, so the peer can read it before the stream ends.
    /// Closing a socket that still has a read pending makes Windows reset the connection, and a reset can discard
    /// data the peer has received but not yet read. The connection itself still closes on <see cref="Cancel"/> or
    /// when an armed deadline passes.
    /// </summary>
    public void FinishSending()
    {
        try
        {
            socket.Shutdown(SocketShutdown.Send);
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException)
        {
            Cancel();
        }
    }

    /// <summary>Closes the connection. Pending reads and writes fail.</summary>
    public void Cancel()
    {
        try
        {
            socket.Close();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        DisarmDeadline();
        Cancel();
    }

    private ByteStreamException MapFailure()
    {
        lock (sync)
        {
            return new ByteStreamException(deadlineExpired ? ByteStreamFailure.TimedOut : ByteStreamFailure.Closed);
        }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        using var registration = cancellationToken.Register(Cancel);
        try
        {
            while (!data.IsEmpty)
            {
                var sent = await socket.SendAsync(data, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                if (sent <= 0)
                {
                    throw MapFailure();
                }
                data = data[sent..];
            }
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException or OperationCanceledException or IOException)
        {
            if (error is ByteStreamException)
            {
                throw;
            }
            Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw MapFailure();
        }
    }

    private async Task ReceiveSomeAsync(CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (bufferStart > 0)
            {
                Buffer.BlockCopy(buffer, bufferStart, buffer, 0, bufferEnd - bufferStart);
                bufferEnd -= bufferStart;
                bufferStart = 0;
            }
            if (bufferEnd == buffer.Length)
            {
                Array.Resize(ref buffer, buffer.Length * 2);
            }
        }
        int received;
        using var registration = cancellationToken.Register(Cancel);
        try
        {
            received = await socket.ReceiveAsync(buffer.AsMemory(bufferEnd), SocketFlags.None, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException or OperationCanceledException)
        {
            Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw MapFailure();
        }
        if (received <= 0)
        {
            throw MapFailure();
        }
        lock (sync)
        {
            bufferEnd += received;
        }
    }

    public async Task<byte[]> ReadExactlyAsync(int count, CancellationToken cancellationToken = default)
    {
        while (BufferedByteCount < count)
        {
            await ReceiveSomeAsync(cancellationToken).ConfigureAwait(false);
        }
        lock (sync)
        {
            var head = buffer.AsSpan(bufferStart, count).ToArray();
            bufferStart += count;
            return head;
        }
    }

    /// <summary>Reads one frame and returns its body. Refuses a frame longer than <paramref name="maxLength"/> before reading it.</summary>
    public async Task<byte[]> ReadFrameAsync(int maxLength = IntegrationV1.MaxFrameLength, CancellationToken cancellationToken = default)
    {
        var header = await ReadExactlyAsync(2, cancellationToken).ConfigureAwait(false);
        var length = (header[0] << 8) | header[1];
        if (length > maxLength)
        {
            throw new ByteStreamException(ByteStreamFailure.FrameTooLarge);
        }
        return await ReadExactlyAsync(length, cancellationToken).ConfigureAwait(false);
    }

    public static byte[] Frame(ReadOnlySpan<byte> body)
    {
        if (body.Length > IntegrationV1.MaxFrameLength)
        {
            throw new ByteStreamException(ByteStreamFailure.FrameTooLarge);
        }
        var output = new byte[body.Length + 2];
        output[0] = (byte)(body.Length >> 8);
        output[1] = (byte)(body.Length & 0xFF);
        body.CopyTo(output.AsSpan(2));
        return output;
    }

    public Task SendFrameAsync(ReadOnlySpan<byte> body, CancellationToken cancellationToken = default) =>
        SendAsync(Frame(body), cancellationToken);

    /// <summary>The peer's address, for per-address accounting and for remembering where a receiver was found.</summary>
    public (string Host, ushort Port)? RemoteAddress
    {
        get
        {
            try
            {
                if (socket.RemoteEndPoint is not IPEndPoint endpoint)
                {
                    return null;
                }
                return (AddressText(endpoint.Address), (ushort)endpoint.Port);
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException)
            {
                return null;
            }
        }
    }

    /// <summary>An address as text, without an IPv6 zone and with an IPv4 address mapped into IPv6 written as IPv4.</summary>
    public static string AddressText(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        var text = address.ToString();
        var percent = text.IndexOf('%', StringComparison.Ordinal);
        return percent >= 0 ? text[..percent] : text;
    }
}
