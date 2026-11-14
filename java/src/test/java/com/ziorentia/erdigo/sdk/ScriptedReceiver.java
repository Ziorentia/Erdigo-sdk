package com.ziorentia.erdigo.sdk;

import com.ziorentia.erdigo.core.ConnectionException;
import com.ziorentia.erdigo.core.FramedConnection;
import com.ziorentia.erdigo.core.IntegrationV1;
import com.ziorentia.erdigo.core.ItemKind;
import com.ziorentia.erdigo.core.Limits;
import com.ziorentia.erdigo.core.Messages.PairRequest;
import com.ziorentia.erdigo.core.Messages.PairStatus;
import com.ziorentia.erdigo.core.Messages.ReceiverError;
import com.ziorentia.erdigo.core.Messages.ReceiverMessage;
import com.ziorentia.erdigo.core.Messages.RequestEnvelope;
import com.ziorentia.erdigo.core.Messages.Welcome;
import com.ziorentia.erdigo.core.Noise;
import com.ziorentia.erdigo.core.NoiseHandshake;
import com.ziorentia.erdigo.core.NoisePattern;
import com.ziorentia.erdigo.core.NoiseTransport;
import com.ziorentia.erdigo.core.PairingCode;
import com.ziorentia.erdigo.core.PairingTicket;
import com.ziorentia.erdigo.core.Request;
import com.ziorentia.erdigo.core.Wire;
import com.ziorentia.erdigo.core.WireException;
import java.io.IOException;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.nio.channels.ServerSocketChannel;
import java.nio.channels.SocketChannel;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.function.Function;

/**
 * A small receiver for the tests, over real TCP on the loopback interface. It speaks the protocol as a receiver does,
 * stays silent where a receiver must, and lets a test replace its answers. It is not a mock receiver for people to use:
 * that comes with the .NET and Swift SDKs.
 */
final class ScriptedReceiver implements AutoCloseable {
    final byte[] staticKey = Noise.newPrivateKey();
    final byte[] publicKey = Noise.publicKey(staticKey);
    final String fingerprint = IntegrationV1.fingerprint(publicKey);
    final PairingCode code = new PairingCode("ABCDE-FGHJK-MNPQR-STVWX");

    private final ServerSocketChannel listener;
    private final Thread acceptor;
    private final Object sync = new Object();
    private final List<String> events = new ArrayList<>();
    private final Map<String, Device> devices = new HashMap<>();
    private final List<FramedConnection> open = new ArrayList<>();

    /** Whether a pairing request is approved. */
    volatile boolean approve = true;
    /** The kinds approved, or null for every kind requested. */
    volatile List<ItemKind> approvedKinds;
    /** Connections closed at once, without a word, before any is answered. */
    final AtomicInteger silentConnections = new AtomicInteger();
    /** Requests applied but not answered, the connection closed instead, as if the answer was lost. */
    final AtomicInteger answersToLose = new AtomicInteger();
    /** What welcome announces. */
    volatile Limits limits = Limits.V1;
    /** Sent in place of welcome, before the connection is closed, when set. */
    volatile ReceiverMessage helloAnswer;
    /** Replaces the answer to a request when it returns an answer. */
    volatile Function<RequestEnvelope, ReceiverMessage> override;

    static final class Device {
        final byte[] key;
        List<ItemKind> kinds;
        long counter;
        long lastSeq;
        byte[] lastRequest;
        final List<Long> seen = new ArrayList<>();
        final Map<String, Request> items = new LinkedHashMap<>();
        final Map<String, Integer> trackPoints = new HashMap<>();

        Device(byte[] key) {
            this.key = key;
        }
    }

    ScriptedReceiver() throws IOException {
        listener = ServerSocketChannel.open();
        listener.bind(new InetSocketAddress(InetAddress.getLoopbackAddress(), 0));
        acceptor = new Thread(this::acceptLoop, "scripted receiver");
        acceptor.setDaemon(true);
        acceptor.start();
    }

    int port() {
        try {
            return ((InetSocketAddress) listener.getLocalAddress()).getPort();
        } catch (IOException error) {
            throw new IllegalStateException(error);
        }
    }

    ReceiverTarget target() {
        return ReceiverTarget.address("127.0.0.1", port());
    }

    PairingTicket qrTicket() {
        return PairingTicket.parse("erdigo-pair:1?code=" + code.display() + "&fp=" + fingerprint);
    }

    PairingTicket typedTicket() {
        return PairingTicket.parse(code.display());
    }

    List<String> events() {
        synchronized (sync) {
            return List.copyOf(events);
        }
    }

    long count(String event) {
        return events().stream().filter(event::equals).count();
    }

    Device device(String deviceFingerprint) {
        synchronized (sync) {
            return devices.get(deviceFingerprint);
        }
    }

    List<Long> seen(String deviceFingerprint) {
        synchronized (sync) {
            Device device = devices.get(deviceFingerprint);
            return device == null ? List.of() : List.copyOf(device.seen);
        }
    }

    private void event(String text) {
        synchronized (sync) {
            events.add(text);
        }
    }

    private void acceptLoop() {
        while (listener.isOpen()) {
            SocketChannel channel;
            try {
                channel = listener.accept();
            } catch (IOException error) {
                return;
            }
            Thread worker = new Thread(() -> serve(channel), "scripted receiver connection");
            worker.setDaemon(true);
            worker.start();
        }
    }

    private void serve(SocketChannel channel) {
        FramedConnection connection;
        try {
            connection = FramedConnection.accepted(channel);
        } catch (IOException error) {
            return;
        }
        synchronized (sync) {
            open.add(connection);
        }
        try {
            event("connection");
            if (silentConnections.getAndUpdate(value -> Math.max(0, value - 1)) > 0) {
                event("silent");
                return;
            }
            connection.armDeadline(java.time.Duration.ofSeconds(10));
            byte[] preamble = connection.readExactly(5);
            if (Arrays.equals(preamble, IntegrationV1.preamble(IntegrationV1.Mode.PAIR))) {
                pair(connection, preamble);
            } else if (Arrays.equals(preamble, IntegrationV1.preamble(IntegrationV1.Mode.CONNECT))) {
                connect(connection, preamble);
            } else {
                event("silent: preamble");
            }
        } catch (ConnectionException | RuntimeException error) {
            event("ended: " + error.getClass().getSimpleName());
        } finally {
            connection.close();
            synchronized (sync) {
                open.remove(connection);
            }
        }
    }

    private void send(FramedConnection connection, NoiseTransport transport, ReceiverMessage message) throws ConnectionException {
        connection.sendFrame(transport.seal(Wire.encode(message)));
    }

    private void pair(FramedConnection connection, byte[] prologue) throws ConnectionException {
        byte[] first = connection.readFrame(IntegrationV1.MAX_HANDSHAKE_FRAME_LENGTH);
        try (NoiseHandshake handshake = new NoiseHandshake(NoisePattern.XX_PSK0, false, prologue, staticKey, null, code.presharedKey())) {
            try {
                handshake.readMessage(first);
            } catch (RuntimeException wrongCode) {
                event("silent: wrong code");
                return;
            }
            event("pair message 1");
            connection.sendFrame(handshake.writeMessage(new byte[0]));
            byte[] third;
            try {
                third = connection.readFrame(IntegrationV1.MAX_HANDSHAKE_FRAME_LENGTH);
            } catch (ConnectionException closed) {
                event("pair ended after message 2");
                return;
            }
            PairRequest request = Wire.decodePairRequest(handshake.readMessage(third));
            byte[] deviceKey = handshake.remoteStaticKey();
            event("pair request: " + request.name());
            try (NoiseTransport transport = handshake.split()) {
                send(connection, transport, new ReceiverMessage.PairStatusMessage(new PairStatus.Pending()));
                if (!approve) {
                    send(connection, transport, new ReceiverMessage.PairStatusMessage(new PairStatus.Denied()));
                    return;
                }
                List<ItemKind> kinds = approvedKinds == null ? request.kinds() : approvedKinds;
                long counter;
                synchronized (sync) {
                    Device device = devices.computeIfAbsent(IntegrationV1.fingerprint(deviceKey), ignored -> new Device(deviceKey));
                    device.kinds = kinds;
                    counter = device.counter;
                }
                send(connection, transport, new ReceiverMessage.PairStatusMessage(new PairStatus.Approved(kinds, counter)));
                connection.finishSending();
            }
        }
    }

    private void connect(FramedConnection connection, byte[] prologue) throws ConnectionException {
        byte[] first = connection.readFrame(IntegrationV1.MAX_HANDSHAKE_FRAME_LENGTH);
        Device device;
        NoiseTransport transport;
        try (NoiseHandshake handshake = new NoiseHandshake(NoisePattern.IK, false, prologue, staticKey, null, null)) {
            byte[] payload;
            try {
                if (first.length != IntegrationV1.CONNECT_FIRST_MESSAGE_LENGTH) {
                    throw new IllegalArgumentException("wrong length");
                }
                payload = handshake.readMessage(first);
            } catch (RuntimeException error) {
                event("silent: bad first message");
                return;
            }
            long counter = 0;
            for (byte part : payload) {
                counter = (counter << 8) | (part & 0xFF);
            }
            synchronized (sync) {
                device = devices.get(IntegrationV1.fingerprint(handshake.remoteStaticKey()));
                if (device == null || counter <= device.counter) {
                    event(device == null ? "silent: unknown device" : "silent: replayed counter");
                    return;
                }
                device.counter = counter;
            }
            connection.sendFrame(handshake.writeMessage(new byte[0]));
            transport = handshake.split();
        }
        try (NoiseTransport owned = transport) {
            Wire.decodeHello(owned.open(connection.readFrame()));
            ReceiverMessage instead = helloAnswer;
            if (instead != null) {
                // Noted before the answer goes out, because the client may look as soon as it has the answer.
                event("hello answered");
                send(connection, owned, instead);
                connection.finishSending();
                return;
            }
            long lastSeq;
            List<ItemKind> kinds;
            synchronized (sync) {
                lastSeq = device.lastSeq;
                kinds = device.kinds;
            }
            send(connection, owned, new ReceiverMessage.WelcomeMessage(new Welcome(1, lastSeq, kinds, limits)));
            connection.disarmDeadline();
            event("connected");
            while (true) {
                byte[] data;
                try {
                    data = owned.open(connection.readFrame());
                } catch (ConnectionException closed) {
                    event("disconnected");
                    return;
                }
                RequestEnvelope envelope;
                try {
                    envelope = Wire.decodeRequest(data);
                } catch (WireException error) {
                    send(connection, owned, new ReceiverMessage.Error(new ReceiverError(error.code(), null, null, null, null, false)));
                    continue;
                }
                synchronized (sync) {
                    device.seen.add(envelope.seq());
                }
                Function<RequestEnvelope, ReceiverMessage> replace = override;
                ReceiverMessage replaced = replace == null ? null : replace.apply(envelope);
                if (replaced != null) {
                    send(connection, owned, replaced);
                    if (replaced instanceof ReceiverMessage.Error error && ErdigoClient.endsConnection(error.error())) {
                        connection.finishSending();
                        event("closed after a fatal answer");
                        return;
                    }
                    continue;
                }
                ReceiverMessage answer = apply(device, envelope, data);
                if (answersToLose.getAndUpdate(value -> Math.max(0, value - 1)) > 0) {
                    event("answer lost");
                    return;
                }
                send(connection, owned, answer);
            }
        }
    }

    private ReceiverMessage apply(Device device, RequestEnvelope envelope, byte[] data) {
        synchronized (sync) {
            long seq = envelope.seq();
            if (seq == device.lastSeq && device.lastRequest != null) {
                return Arrays.equals(device.lastRequest, data)
                        ? new ReceiverMessage.Ack(seq, true)
                        : new ReceiverMessage.Error(new ReceiverError(com.ziorentia.erdigo.core.ErrorCode.SEQ_CONFLICT, seq, null, null, null, false));
            }
            if (seq <= device.lastSeq) {
                return new ReceiverMessage.Error(
                        new ReceiverError(com.ziorentia.erdigo.core.ErrorCode.STALE_SEQ, seq, device.lastSeq, null, null, false));
            }
            device.lastSeq = seq;
            device.lastRequest = data;
            Request request = envelope.request();
            if (request instanceof Request.PutPin pin) {
                device.items.put(pin.id().value(), pin);
            } else if (request instanceof Request.PutMarker marker) {
                device.items.put(marker.id().value(), marker);
            } else if (request instanceof Request.PutNote note) {
                device.items.put(note.id().value(), note);
            } else if (request instanceof Request.PhotoEnd end) {
                device.items.put(end.id().value(), end);
            } else if (request instanceof Request.TrackAppend append) {
                device.items.put(append.id().value(), append);
                device.trackPoints.merge(append.id().value(), append.points().size(), Integer::sum);
            } else if (request instanceof Request.TrackFinish finish) {
                device.items.put(finish.id().value() + "#finished", finish);
            } else if (request instanceof Request.Move move) {
                if (!device.items.containsKey(move.id().value())) {
                    return new ReceiverMessage.Error(new ReceiverError(com.ziorentia.erdigo.core.ErrorCode.NOT_FOUND, seq, null, null, null, false));
                }
                device.items.put(move.id().value() + "#moved", move);
            } else if (request instanceof Request.Delete delete) {
                device.items.remove(delete.id().value());
            } else if (request instanceof Request.DeleteAll) {
                device.items.clear();
            }
            event("applied " + seq + " " + request.getClass().getSimpleName());
            return new ReceiverMessage.Ack(seq, false);
        }
    }

    /** Ends every open connection, as a receiver that sleeps or quits does. */
    void dropConnections() {
        synchronized (sync) {
            for (FramedConnection connection : open) {
                connection.cancel();
            }
        }
    }

    @Override
    public void close() {
        try {
            listener.close();
        } catch (IOException ignored) {
            // Closing anyway.
        }
        dropConnections();
    }
}
