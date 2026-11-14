package com.ziorentia.erdigo.core;

import java.io.IOException;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.SocketAddress;
import java.net.StandardSocketOptions;
import java.nio.ByteBuffer;
import java.nio.channels.ClosedByInterruptException;
import java.nio.channels.SocketChannel;
import java.time.Duration;
import java.util.Objects;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.TimeUnit;

/**
 * An ordered byte stream over TCP with the integration-v1 framing: a two-byte big-endian length followed by one Noise
 * message.
 *
 * <p>One thread at a time may read, and one thread at a time may write. Blocking operations end with
 * {@link ConnectionException} when the connection closes, when an armed deadline passes, or when the thread is
 * interrupted, in which case the interrupt flag stays set.
 */
public final class FramedConnection implements AutoCloseable {
    private static final ScheduledExecutorService DEADLINES = Executors.newSingleThreadScheduledExecutor(runnable -> {
        Thread thread = new Thread(runnable, "erdigo connection deadlines");
        thread.setDaemon(true);
        return thread;
    });

    private final Object sync = new Object();
    private final SocketChannel channel;
    private ByteBuffer buffer = ByteBuffer.allocate(4096).flip();
    private boolean deadlineExpired;
    private long deadlineGeneration;
    private ScheduledFuture<?> deadlineTimer;

    private FramedConnection(SocketChannel channel) throws IOException {
        this.channel = channel;
        channel.setOption(StandardSocketOptions.TCP_NODELAY, true);
    }

    /** Wraps a connection a listener accepted. */
    public static FramedConnection accepted(SocketChannel channel) throws IOException {
        return new FramedConnection(Objects.requireNonNull(channel, "channel"));
    }

    /**
     * Connects, or fails within {@code timeout}. A connection the network layer would keep retrying counts as failed,
     * so that callers can apply their own retry policy.
     */
    public static FramedConnection connect(String host, int port, Duration timeout) throws ConnectionException {
        Objects.requireNonNull(host, "host");
        if (port < 1 || port > 65535) {
            throw new ConnectionException(ConnectionException.Failure.CONNECTION_FAILED);
        }
        SocketChannel channel = null;
        try {
            channel = SocketChannel.open();
            SocketAddress address = new InetSocketAddress(InetAddress.getByName(host), port);
            channel.socket().connect(address, (int) Math.max(1, Math.min(Integer.MAX_VALUE, timeout.toMillis())));
            FramedConnection connection = new FramedConnection(channel);
            channel = null;
            return connection;
        } catch (java.net.SocketTimeoutException error) {
            throw new ConnectionException(ConnectionException.Failure.TIMED_OUT);
        } catch (ClosedByInterruptException error) {
            throw new ConnectionException(ConnectionException.Failure.INTERRUPTED);
        } catch (IOException | RuntimeException error) {
            if (Thread.currentThread().isInterrupted()) {
                throw new ConnectionException(ConnectionException.Failure.INTERRUPTED);
            }
            throw new ConnectionException(ConnectionException.Failure.CONNECTION_FAILED);
        } finally {
            if (channel != null) {
                try {
                    channel.close();
                } catch (IOException ignored) {
                    // Nothing more to do.
                }
            }
        }
    }

    /**
     * Closes the connection after {@code time} unless {@link #disarmDeadline()} is called first. Pending reads and
     * writes then fail with {@link ConnectionException.Failure#TIMED_OUT}.
     */
    public void armDeadline(Duration time) {
        synchronized (sync) {
            deadlineGeneration += 1;
            long generation = deadlineGeneration;
            if (deadlineTimer != null) {
                deadlineTimer.cancel(false);
            }
            long millis = Math.max(0, Math.min(time.toMillis(), TimeUnit.DAYS.toMillis(24)));
            deadlineTimer = DEADLINES.schedule(() -> deadlinePassed(generation), millis, TimeUnit.MILLISECONDS);
        }
    }

    private void deadlinePassed(long generation) {
        synchronized (sync) {
            if (generation != deadlineGeneration) {
                return;
            }
            deadlineExpired = true;
        }
        cancel();
    }

    public void disarmDeadline() {
        synchronized (sync) {
            deadlineGeneration += 1;
            if (deadlineTimer != null) {
                deadlineTimer.cancel(false);
                deadlineTimer = null;
            }
        }
    }

    /** Closes the connection. Pending reads and writes fail. */
    public void cancel() {
        try {
            channel.close();
        } catch (IOException ignored) {
            // Already closed, or closing anyway.
        }
    }

    @Override
    public void close() {
        disarmDeadline();
        cancel();
    }

    /**
     * Stops sending once everything already sent has gone out, so the peer can read it before the stream ends. The
     * connection itself still closes on {@link #cancel()} or when an armed deadline passes.
     */
    public void finishSending() {
        try {
            channel.shutdownOutput();
        } catch (IOException error) {
            cancel();
        }
    }

    private ConnectionException failure() {
        if (Thread.currentThread().isInterrupted()) {
            return new ConnectionException(ConnectionException.Failure.INTERRUPTED);
        }
        synchronized (sync) {
            return new ConnectionException(deadlineExpired ? ConnectionException.Failure.TIMED_OUT : ConnectionException.Failure.CLOSED);
        }
    }

    public void send(byte[] data) throws ConnectionException {
        ByteBuffer remaining = ByteBuffer.wrap(data);
        try {
            while (remaining.hasRemaining()) {
                if (channel.write(remaining) < 0) {
                    throw failure();
                }
            }
        } catch (ConnectionException error) {
            cancel();
            throw error;
        } catch (IOException error) {
            cancel();
            throw failure();
        }
    }

    /** Bytes received but not yet read. After a failed read, this says whether the peer sent anything. */
    public int bufferedByteCount() {
        synchronized (sync) {
            return buffer.remaining();
        }
    }

    private void receiveSome() throws ConnectionException {
        ByteBuffer target;
        synchronized (sync) {
            buffer.compact();
            if (!buffer.hasRemaining()) {
                ByteBuffer larger = ByteBuffer.allocate(buffer.capacity() * 2);
                buffer.flip();
                larger.put(buffer);
                buffer = larger;
            }
            target = buffer;
        }
        int received;
        try {
            received = channel.read(target);
        } catch (IOException error) {
            synchronized (sync) {
                buffer.flip();
            }
            cancel();
            throw failure();
        }
        synchronized (sync) {
            buffer.flip();
        }
        if (received < 0) {
            throw failure();
        }
    }

    public byte[] readExactly(int count) throws ConnectionException {
        while (bufferedByteCount() < count) {
            receiveSome();
        }
        synchronized (sync) {
            byte[] head = new byte[count];
            buffer.get(head);
            return head;
        }
    }

    /** Reads one frame and returns its body. Refuses a frame longer than {@code maxLength} before reading it. */
    public byte[] readFrame(int maxLength) throws ConnectionException {
        byte[] header = readExactly(2);
        int length = ((header[0] & 0xFF) << 8) | (header[1] & 0xFF);
        if (length > maxLength) {
            throw new ConnectionException(ConnectionException.Failure.FRAME_TOO_LARGE);
        }
        return readExactly(length);
    }

    public byte[] readFrame() throws ConnectionException {
        return readFrame(IntegrationV1.MAX_FRAME_LENGTH);
    }

    public static byte[] frame(byte[] body) {
        if (body.length > IntegrationV1.MAX_FRAME_LENGTH) {
            throw new IllegalArgumentException("a frame is at most 65535 bytes");
        }
        byte[] output = new byte[body.length + 2];
        output[0] = (byte) (body.length >> 8);
        output[1] = (byte) body.length;
        System.arraycopy(body, 0, output, 2, body.length);
        return output;
    }

    public void sendFrame(byte[] body) throws ConnectionException {
        send(frame(body));
    }

    /** The peer's address, for remembering where a receiver was found, or null. */
    public InetSocketAddress remoteAddress() {
        try {
            return channel.getRemoteAddress() instanceof InetSocketAddress address ? address : null;
        } catch (IOException error) {
            return null;
        }
    }

    /** An address as text, without an IPv6 zone, and with an IPv4 address mapped into IPv6 written as IPv4. */
    public static String addressText(InetAddress address) {
        String text = address.getHostAddress();
        int percent = text.indexOf('%');
        return percent >= 0 ? text.substring(0, percent) : text;
    }
}
