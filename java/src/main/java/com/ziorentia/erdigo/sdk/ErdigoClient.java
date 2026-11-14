package com.ziorentia.erdigo.sdk;

import com.ziorentia.erdigo.core.ConnectionException;
import com.ziorentia.erdigo.core.Coordinate;
import com.ziorentia.erdigo.core.ErrorCode;
import com.ziorentia.erdigo.core.FramedConnection;
import com.ziorentia.erdigo.core.Hex;
import com.ziorentia.erdigo.core.IntegrationV1;
import com.ziorentia.erdigo.core.ItemId;
import com.ziorentia.erdigo.core.ItemKind;
import com.ziorentia.erdigo.core.Limits;
import com.ziorentia.erdigo.core.MarkerColor;
import com.ziorentia.erdigo.core.Messages.Hello;
import com.ziorentia.erdigo.core.Messages.ReceiverError;
import com.ziorentia.erdigo.core.Messages.ReceiverMessage;
import com.ziorentia.erdigo.core.Messages.RequestEnvelope;
import com.ziorentia.erdigo.core.Messages.Welcome;
import com.ziorentia.erdigo.core.NoiseException;
import com.ziorentia.erdigo.core.NoiseHandshake;
import com.ziorentia.erdigo.core.NoisePattern;
import com.ziorentia.erdigo.core.NoiseTransport;
import com.ziorentia.erdigo.core.PhotoType;
import com.ziorentia.erdigo.core.Request;
import com.ziorentia.erdigo.core.TextRules;
import com.ziorentia.erdigo.core.TokenBucket;
import com.ziorentia.erdigo.core.TrackPoint;
import com.ziorentia.erdigo.core.Wire;
import com.ziorentia.erdigo.core.WireException;
import com.ziorentia.erdigo.sdk.ErdigoException.Kind;
import java.net.InetSocketAddress;
import java.security.SecureRandom;
import java.time.Duration;
import java.util.Arrays;
import java.util.List;
import java.util.Objects;
import java.util.concurrent.locks.ReentrantLock;

/**
 * Writes items to one paired receiver.
 *
 * <p>Requests run strictly one at a time, in the order they were made, and each method returns once the receiver has
 * acknowledged its request. If the connection breaks, the request is sent again with the same sequence number, so the
 * receiver applies it at most once. Close the client to close its connection.
 *
 * <p>Every method blocks. Interrupting the thread that is waiting stops the request with {@link Kind#INTERRUPTED} and
 * leaves the interrupt flag set; a request interrupted after it was sent may already have been applied.
 */
public final class ErdigoClient implements AutoCloseable {
    /**
     * How many {@code rate_limited} answers one request will accept before giving up. An honest receiver needs only one
     * or two, so this only stops a receiver that never relents.
     */
    static final int MAX_RATE_LIMITED_ANSWERS = 20;

    private static final double MINIMUM_RATE_LIMIT_WAIT = 0.05;
    private static final SecureRandom RANDOM = new SecureRandom();

    private final DeviceIdentity identity;
    private final DeviceStore store;
    private final ReceiverTarget target;
    private final RetryPolicy retry;
    private final Duration connectTimeout;
    private final Duration ackTimeout;
    /** Fair, so that waiting requests go ahead in the order they arrived. */
    private final ReentrantLock gate = new ReentrantLock(true);
    private final Object sync = new Object();
    private ReceiverPairing pairing;
    private Session session;
    private double lastSessionAt;
    // These outlive a connection, because the receiver's own count does too.
    private Limits pacedLimits;
    private TokenBucket messageBucket;
    private TokenBucket pointBucket;

    private static final class Session {
        final FramedConnection connection;
        final NoiseTransport transport;
        final Welcome welcome;
        long lastSeq;

        Session(FramedConnection connection, NoiseTransport transport, Welcome welcome) {
            this.connection = connection;
            this.transport = transport;
            this.welcome = welcome;
            this.lastSeq = welcome.lastSeq();
        }
    }

    /** A problem with the connection, as opposed to an answer from the receiver. */
    private static final class ConnectionLost extends Exception {
        private static final long serialVersionUID = 1L;

        ConnectionLost() {
            super(null, null, false, false);
        }
    }

    @FunctionalInterface
    private interface Body {
        void run() throws ConnectionLost;
    }

    /**
     * @param identity this device's key
     * @param pairing the receiver to write to
     * @param store where the connection counter and the receiver's last address are kept
     */
    public ErdigoClient(DeviceIdentity identity, ReceiverPairing pairing, DeviceStore store) {
        this(identity, pairing, store, ReceiverTarget.discover(), RetryPolicy.upTo(Duration.ofSeconds(30)));
    }

    /**
     * @param target where to look for the receiver: by default the last known address, then the local network
     * @param retry how long each outage may last before a request fails with {@link Kind#NO_ANSWER}; 30 seconds by default
     */
    public ErdigoClient(DeviceIdentity identity, ReceiverPairing pairing, DeviceStore store, ReceiverTarget target, RetryPolicy retry) {
        this(identity, pairing, store, target, retry, Duration.ofSeconds(5), Duration.ofSeconds(15));
    }

    /**
     * @param connectTimeout how long one attempt to connect may take; 5 seconds by default
     * @param ackTimeout how long to wait for the answer to one message; 15 seconds by default
     */
    public ErdigoClient(
            DeviceIdentity identity,
            ReceiverPairing pairing,
            DeviceStore store,
            ReceiverTarget target,
            RetryPolicy retry,
            Duration connectTimeout,
            Duration ackTimeout) {
        this.identity = Objects.requireNonNull(identity, "identity");
        this.pairing = Objects.requireNonNull(pairing, "pairing");
        this.store = Objects.requireNonNull(store, "store");
        this.target = target == null ? ReceiverTarget.discover() : target;
        this.retry = retry == null ? RetryPolicy.upTo(Duration.ofSeconds(30)) : retry;
        this.connectTimeout = Objects.requireNonNull(connectTimeout, "connectTimeout");
        this.ackTimeout = Objects.requireNonNull(ackTimeout, "ackTimeout");
    }

    // Public requests

    /** Connects now rather than at the first request. Throws {@link Kind#NO_ANSWER} if the retry policy runs out. */
    public void connect() {
        serialised(() -> retrying(() -> currentSession()));
    }

    /**
     * Closes the connection. A later request connects again. Do not call this while a request is still running, because
     * that request would then reconnect.
     */
    @Override
    public void close() {
        synchronized (sync) {
            if (session != null) {
                session.connection.close();
                session.transport.close();
                session = null;
            }
        }
    }

    /** The limits the receiver announced on the current connection, or null without one. */
    public Limits limits() {
        synchronized (sync) {
            return session == null ? null : session.welcome.limits();
        }
    }

    /** The item kinds the person allowed, as announced on the current connection, or null without one. */
    public List<ItemKind> allowedKinds() {
        synchronized (sync) {
            return session == null ? null : session.welcome.kinds();
        }
    }

    public void ping() {
        perform(new Request.Ping(), null, 0);
    }

    /** Creates the pin, or replaces it if this device already has one with this id. {@code title} may be null. */
    public void putPin(String id, Coordinate at, String title) {
        ItemId itemId = validatedId(id);
        validateTitle(title);
        perform(new Request.PutPin(itemId, Objects.requireNonNull(at, "at"), title), ItemKind.PIN, 0);
    }

    public void putPin(String id, Coordinate at) {
        putPin(id, at, null);
    }

    /** {@code title} and {@code color} may be null. */
    public void putMarker(String id, Coordinate at, String title, MarkerColor color) {
        ItemId itemId = validatedId(id);
        validateTitle(title);
        perform(new Request.PutMarker(itemId, Objects.requireNonNull(at, "at"), title, color), ItemKind.MARKER, 0);
    }

    /** A note is plain text. The receiver shows links as text and never opens them. */
    public void putNote(String id, Coordinate at, String text) {
        ItemId itemId = validatedId(id);
        Objects.requireNonNull(text, "text");
        validated(() -> TextRules.validateNote(text));
        perform(new Request.PutNote(itemId, Objects.requireNonNull(at, "at"), text), ItemKind.NOTE, 0);
    }

    public void move(String id, Coordinate to) {
        perform(new Request.Move(validatedId(id), Objects.requireNonNull(to, "to")), null, 0);
    }

    /** Deletes one of this device's items. Deleting an id that does not exist succeeds. */
    public void delete(String id) {
        perform(new Request.Delete(validatedId(id)), null, 0);
    }

    /** Deletes every item this device created on this receiver, and nothing else. */
    public void deleteAll() {
        perform(new Request.DeleteAll(), null, 0);
    }

    /**
     * Appends points to a track, creating it if needed. Long lists are split into several messages, and no other request
     * runs in between. {@code title} may be null.
     */
    public void appendTrack(String id, List<TrackPoint> points, String title) {
        ItemId itemId = validatedId(id);
        validateTitle(title);
        Objects.requireNonNull(points, "points");
        if (points.isEmpty()) {
            throw new ErdigoException(Kind.INVALID_ARGUMENT, "a track append needs at least one point");
        }
        List<TrackPoint> all = List.copyOf(points);
        serialised(() -> {
            String pendingTitle = title;
            for (int start = 0; start < all.size(); start += IntegrationV1.MAX_POINTS_PER_MESSAGE) {
                List<TrackPoint> batch = all.subList(start, Math.min(all.size(), start + IntegrationV1.MAX_POINTS_PER_MESSAGE));
                performHoldingLock(new Request.TrackAppend(itemId, pendingTitle, batch), ItemKind.TRACK, batch.size());
                pendingTitle = null;
            }
        });
    }

    public void appendTrack(String id, List<TrackPoint> points) {
        appendTrack(id, points, null);
    }

    public void finishTrack(String id) {
        perform(new Request.TrackFinish(validatedId(id)), ItemKind.TRACK, 0);
    }

    /**
     * Sends a JPEG or PNG photo. The format is detected from the data, and the receiver removes metadata such as EXIF
     * location when it stores the photo. {@code title} may be null.
     */
    public void putPhoto(String id, Coordinate at, byte[] data, String title) {
        ItemId itemId = validatedId(id);
        validateTitle(title);
        Objects.requireNonNull(at, "at");
        Objects.requireNonNull(data, "data");
        PhotoType type = PhotoType.detect(data);
        if (type == null) {
            throw new ErdigoException(Kind.INVALID_ARGUMENT, "a photo must be JPEG or PNG data");
        }
        if (data.length > Limits.V1.photoBytes()) {
            throw new ErdigoException(Kind.INVALID_ARGUMENT, "a photo is at most " + Limits.V1.photoBytes() + " bytes");
        }
        byte[] bytes = data.clone();
        int chunkSize = IntegrationV1.MAX_CHUNK_BYTES;
        int chunkCount = (bytes.length + chunkSize - 1) / chunkSize;
        Request.PhotoHeader header =
                new Request.PhotoHeader(itemId, at, title, type, bytes.length, Hex.encode(IntegrationV1.sha256(bytes)), chunkCount);
        serialised(() -> retrying(() -> {
            // The receiver drops a partial upload when the connection ends, so restart from the beginning.
            Session current = currentSession();
            if (!current.welcome.kinds().contains(ItemKind.PHOTO)) {
                throw ErdigoException.builder(Kind.NOT_PERMITTED).itemKind(ItemKind.PHOTO).build();
            }
            exchangeNext(new Request.PhotoBegin(header), current);
            for (int index = 0; index < chunkCount; index++) {
                int start = index * chunkSize;
                byte[] slice = Arrays.copyOfRange(bytes, start, Math.min(bytes.length, start + chunkSize));
                exchangeNext(new Request.PhotoChunk(itemId, index, slice), current);
            }
            exchangeNext(new Request.PhotoEnd(itemId), current);
        }));
    }

    public void putPhoto(String id, Coordinate at, byte[] data) {
        putPhoto(id, at, data, null);
    }

    // Validation

    private static ItemId validatedId(String id) {
        Objects.requireNonNull(id, "id");
        try {
            return new ItemId(id);
        } catch (WireException error) {
            throw ErdigoException.builder(Kind.INVALID_ARGUMENT).detail(error.detail()).cause(error).build();
        }
    }

    private static void validateTitle(String title) {
        if (title != null) {
            validated(() -> TextRules.validateTitle(title));
        }
    }

    private static void validated(Runnable body) {
        try {
            body.run();
        } catch (WireException error) {
            throw ErdigoException.builder(Kind.INVALID_ARGUMENT).detail(error.detail()).cause(error).build();
        }
    }

    // Ordering

    private interface Serialised {
        void run();
    }

    private void serialised(Serialised body) {
        try {
            gate.lockInterruptibly();
        } catch (InterruptedException error) {
            Thread.currentThread().interrupt();
            throw new ErdigoException(Kind.INTERRUPTED);
        }
        try {
            body.run();
        } finally {
            gate.unlock();
        }
    }

    // Requests

    private void perform(Request request, ItemKind kind, int points) {
        serialised(() -> performHoldingLock(request, kind, points));
    }

    private void performHoldingLock(Request request, ItemKind kind, int points) {
        long[] assignedSeq = {0};
        // A resend is the same request, so it does not wait for its allowance a second time.
        boolean[] pointsArePaid = {false};
        retrying(() -> {
            Session current = currentSession();
            if (kind != null && !current.welcome.kinds().contains(kind)) {
                throw ErdigoException.builder(Kind.NOT_PERMITTED).itemKind(kind).build();
            }
            // A request keeps its number across reconnections, so the receiver can recognise a resend.
            if (assignedSeq[0] != 0 && current.lastSeq > assignedSeq[0]) {
                throw ErdigoException.builder(Kind.SEQUENCE_CONFLICT).lastSeq(current.lastSeq).build();
            }
            if (!pointsArePaid[0]) {
                pacePoints(points);
                pointsArePaid[0] = true;
            }
            long seq = assignedSeq[0] != 0 ? assignedSeq[0] : current.lastSeq + 1;
            assignedSeq[0] = seq;
            exchange(request, seq, current);
        });
    }

    /** Waits as long as asked, up to the longest wait a receiver may ask for. */
    private static void sleep(double seconds) {
        long millis = (long) Math.ceil(Math.max(0, Math.min(seconds, 3600)) * 1000);
        try {
            Thread.sleep(millis);
        } catch (InterruptedException error) {
            Thread.currentThread().interrupt();
            throw new ErdigoException(Kind.INTERRUPTED);
        }
    }

    private void pacePoints(int count) {
        if (count <= 0) {
            return;
        }
        double wait;
        while ((wait = take(true, count)) > 0) {
            sleep(wait);
        }
    }

    private void paceMessage() {
        double wait;
        while ((wait = take(false, 1)) > 0) {
            sleep(wait);
        }
    }

    private double take(boolean points, int count) {
        synchronized (sync) {
            TokenBucket bucket = points ? pointBucket : messageBucket;
            return bucket == null ? 0 : bucket.take(TokenBucket.now(), count);
        }
    }

    private void exchangeNext(Request request, Session current) throws ConnectionLost {
        exchange(request, current.lastSeq + 1, current);
    }

    private static ErdigoException interruptedOr(ConnectionException error) {
        return error.failure() == ConnectionException.Failure.INTERRUPTED ? new ErdigoException(Kind.INTERRUPTED) : null;
    }

    /** Sends one request and waits for its answer, honouring the receiver's rate limit. */
    private void exchange(Request request, long seq, Session current) throws ConnectionLost {
        if (seq > IntegrationV1.MAX_SEQUENCE) {
            throw new ErdigoException(
                    Kind.INVALID_ARGUMENT, "the sequence number is exhausted; remove this device on the receiver and pair it again");
        }
        byte[] plaintext = Wire.encode(new RequestEnvelope(seq, request));
        int rateLimitedAnswers = 0;
        while (true) {
            paceMessage();
            ReceiverMessage answer;
            try {
                byte[] sealed = current.transport.seal(plaintext);
                current.connection.armDeadline(ackTimeout);
                current.connection.sendFrame(sealed);
                byte[] frame = current.connection.readFrame();
                current.connection.disarmDeadline();
                answer = Wire.decodeReceiverMessage(current.transport.open(frame));
            } catch (WireException error) {
                dropSession();
                throw ErdigoException.builder(Kind.PROTOCOL_VIOLATION).detail(error.detail()).cause(error).build();
            } catch (ConnectionException error) {
                dropSession();
                ErdigoException interrupted = interruptedOr(error);
                if (interrupted != null) {
                    throw interrupted;
                }
                throw new ConnectionLost();
            } catch (NoiseException error) {
                dropSession();
                throw new ConnectionLost();
            }
            if (answer instanceof ReceiverMessage.Ack ack) {
                if (ack.seq() != seq) {
                    dropSession();
                    throw new ErdigoException(Kind.PROTOCOL_VIOLATION, "the acknowledgement has the wrong sequence number");
                }
                current.lastSeq = seq;
                return;
            }
            if (answer instanceof ReceiverMessage.Error errorAnswer) {
                ReceiverError error = errorAnswer.error();
                boolean endsConnection = endsConnection(error);
                // An answer about one request must be about this one. An answer that ends the connection is about the
                // connection, so it ends it whatever number it carries.
                boolean answersThisRequest = error.seq() == null || error.seq() == seq;
                if (!endsConnection && !answersThisRequest) {
                    dropSession();
                    throw new ErdigoException(Kind.PROTOCOL_VIOLATION, "the error answers a different sequence number");
                }
                // Only an answer the receiver will keep the connection open after is worth waiting for.
                if (!endsConnection && error.is(ErrorCode.RATE_LIMITED)) {
                    rateLimitedAnswers += 1;
                    if (rateLimitedAnswers > MAX_RATE_LIMITED_ANSWERS) {
                        dropSession();
                        throw ErdigoException.builder(Kind.REJECTED).receiverError(error).build();
                    }
                    double wait = (error.retryAfterMs() == null ? 1000 : error.retryAfterMs()) / 1000.0;
                    sleep(Math.max(wait, MINIMUM_RATE_LIMIT_WAIT));
                    continue;
                }
                handle(error, seq, answersThisRequest, endsConnection, current);
                return;
            }
            dropSession();
            throw new ErdigoException(Kind.PROTOCOL_VIOLATION, "unexpected message in answer to a request");
        }
    }

    /**
     * Whether the receiver closes the connection after this answer. {@code paused} and {@code replaced} always do, even
     * if a receiver leaves out the flag.
     */
    static boolean endsConnection(ReceiverError error) {
        return error.fatal() || error.is(ErrorCode.PAUSED) || error.is(ErrorCode.REPLACED);
    }

    /** Turns an error answer into the error the caller sees. It always throws. */
    private void handle(ReceiverError error, long seq, boolean answersThisRequest, boolean endsConnection, Session current)
            throws ConnectionLost {
        // A number from an answer to some other request says nothing about where this connection stands.
        if (answersThisRequest && error.lastSeq() != null) {
            current.lastSeq = Math.max(current.lastSeq, error.lastSeq());
        }
        if (endsConnection) {
            dropSession();
        }
        if (error.is(ErrorCode.PAUSED)) {
            throw new ConnectionLost();
        }
        if (error.is(ErrorCode.REPLACED)) {
            throw ErdigoException.builder(Kind.REPLACED).receiverError(error).build();
        }
        if (error.is(ErrorCode.STALE_SEQ) || error.is(ErrorCode.SEQ_CONFLICT)) {
            throw ErdigoException.builder(Kind.SEQUENCE_CONFLICT).receiverError(error).lastSeq(current.lastSeq).build();
        }
        ErrorCode code = ErrorCode.fromWire(error.code());
        if (!endsConnection && error.seq() != null && error.seq() == seq && code != null && !code.isRetryable()) {
            // The receiver used up this number on the failed request.
            current.lastSeq = seq;
        }
        throw ErdigoException.builder(Kind.REJECTED).receiverError(error).build();
    }

    // Connection

    private void dropSession() {
        close();
    }

    private Session currentSession() throws ConnectionLost {
        ReceiverPairing remembered;
        synchronized (sync) {
            if (session != null) {
                return session;
            }
            remembered = pairing;
        }
        // The remembered address comes first, because looking around the network takes a moment.
        Session created = firstAnswer(Discovery.knownEndpoints(target, remembered));
        if (created != null) {
            return created;
        }
        if (target instanceof ReceiverTarget.Discover) {
            created = firstAnswer(Discovery.browse(2000, 8));
            if (created != null) {
                return created;
            }
        }
        Pairing.throwIfInterrupted();
        throw new ConnectionLost();
    }

    /** Tries each endpoint in turn. A receiver that stays silent is simply not the right one. */
    private Session firstAnswer(List<ReceiverEndpoint> endpoints) {
        for (ReceiverEndpoint endpoint : endpoints) {
            Session created;
            try {
                created = handshake(endpoint);
            } catch (ConnectionLost lost) {
                continue;
            }
            synchronized (sync) {
                session = created;
                lastSessionAt = TokenBucket.now();
                preparePacing(created.welcome.limits());
            }
            rememberAddress(created.connection);
            return created;
        }
        return null;
    }

    private void preparePacing(Limits limits) {
        if (limits.equals(pacedLimits)) {
            return;
        }
        pacedLimits = limits;
        double now = TokenBucket.now();
        messageBucket = new TokenBucket(limits.messageBurst(), limits.messagesPerSecond(), now);
        pointBucket = new TokenBucket(limits.pointBurst(), limits.pointsPerSecond(), now);
    }

    private void rememberAddress(FramedConnection connection) {
        InetSocketAddress address = connection.remoteAddress();
        if (address == null) {
            return;
        }
        String host = FramedConnection.addressText(address.getAddress());
        int port = address.getPort();
        String fingerprint;
        synchronized (sync) {
            if (host.equals(pairing.lastHost()) && pairing.lastPort() != null && pairing.lastPort() == port) {
                return;
            }
            pairing = pairing.withAddress(host, port);
            fingerprint = pairing.fingerprint();
        }
        store.updateAddress(fingerprint, host, port);
    }

    private Session handshake(ReceiverEndpoint endpoint) throws ConnectionLost {
        FramedConnection connection;
        try {
            connection = FramedConnection.connect(endpoint.host(), endpoint.port(), connectTimeout);
        } catch (ConnectionException error) {
            ErdigoException interrupted = interruptedOr(error);
            if (interrupted != null) {
                throw interrupted;
            }
            throw new ConnectionLost();
        }
        NoiseHandshake handshake = null;
        NoiseTransport transport = null;
        boolean succeeded = false;
        try {
            byte[] prologue = IntegrationV1.preamble(IntegrationV1.Mode.CONNECT);
            ReceiverPairing remembered;
            synchronized (sync) {
                remembered = pairing;
            }
            handshake = new NoiseHandshake(NoisePattern.IK, true, prologue, identity.privateKey(), remembered.receiverKey(), null);
            // Written to disk first: the receiver ignores a counter it has already seen.
            long counter = store.nextConnectCounter(remembered.fingerprint(), System.currentTimeMillis());
            if (counter > IntegrationV1.MAX_SEQUENCE) {
                throw new ErdigoException(
                        Kind.STORAGE, "this device key has used up its connection counter. Create a new key and pair again.");
            }
            byte[] counterBytes = new byte[8];
            for (int index = 0; index < 8; index++) {
                counterBytes[index] = (byte) (counter >>> (56 - 8 * index));
            }
            byte[] first = handshake.writeMessage(counterBytes);
            connection.armDeadline(connectTimeout);
            connection.send(Pairing.concat(prologue, FramedConnection.frame(first)));
            byte[] second = connection.readFrame(IntegrationV1.MAX_HANDSHAKE_FRAME_LENGTH);
            if (handshake.readMessage(second).length != 0) {
                throw new ErdigoException(Kind.PROTOCOL_VIOLATION, "the handshake reply has an unexpected payload");
            }
            transport = handshake.split();
            Hello hello = new Hello(List.of(IntegrationV1.VERSION), Pairing.SDK_NAME);
            connection.sendFrame(transport.seal(Wire.encode(hello)));
            byte[] frame = connection.readFrame();
            connection.disarmDeadline();
            ReceiverMessage answer = Wire.decodeReceiverMessage(transport.open(frame));
            if (answer instanceof ReceiverMessage.WelcomeMessage welcome) {
                if (welcome.welcome().version() != IntegrationV1.VERSION) {
                    throw new ErdigoException(Kind.PROTOCOL_VIOLATION, "the receiver chose a version that was not offered");
                }
                succeeded = true;
                return new Session(connection, transport, welcome.welcome());
            }
            if (answer instanceof ReceiverMessage.Error errorAnswer) {
                ReceiverError error = errorAnswer.error();
                if (error.is(ErrorCode.VERSION_UNSUPPORTED)) {
                    throw ErdigoException.builder(Kind.VERSION_UNSUPPORTED)
                            .receiverError(error)
                            .supported(error.supported() == null ? List.of() : error.supported())
                            .build();
                }
                if (error.is(ErrorCode.PAUSED)) {
                    throw new ConnectionLost();
                }
                throw ErdigoException.builder(Kind.REJECTED).receiverError(error).build();
            }
            throw new ErdigoException(Kind.PROTOCOL_VIOLATION, "the receiver did not send welcome");
        } catch (WireException error) {
            throw ErdigoException.builder(Kind.PROTOCOL_VIOLATION).detail(error.detail()).cause(error).build();
        } catch (ConnectionException error) {
            ErdigoException interrupted = interruptedOr(error);
            if (interrupted != null) {
                throw interrupted;
            }
            throw new ConnectionLost();
        } catch (NoiseException error) {
            throw new ConnectionLost();
        } finally {
            if (handshake != null) {
                handshake.close();
            }
            if (!succeeded) {
                connection.close();
                if (transport != null) {
                    transport.close();
                }
            }
        }
    }

    /**
     * Runs {@code body} again after connection problems until the retry policy runs out. The time allowed counts from
     * the first failure, so a long paced wait does not use it up.
     */
    private void retrying(Body body) {
        Double outageStart = null;
        int attempt = 0;
        while (true) {
            try {
                body.run();
                return;
            } catch (ConnectionLost lost) {
                Pairing.throwIfInterrupted();
                double now = TokenBucket.now();
                double connectedAt;
                synchronized (sync) {
                    connectedAt = lastSessionAt;
                }
                // Having connected since then, this is a new outage.
                if (outageStart != null && connectedAt > outageStart) {
                    outageStart = null;
                    attempt = 0;
                }
                double start = outageStart != null ? outageStart : now;
                outageStart = start;
                double delay = backoff(attempt);
                attempt += 1;
                if (retry instanceof RetryPolicy.NoRetry) {
                    throw new ErdigoException(Kind.NO_ANSWER);
                }
                if (retry instanceof RetryPolicy.UpTo upTo) {
                    // Use all the time allowed, with a last try at the end of it.
                    double allowed = upTo.time().getSeconds() + upTo.time().getNano() / 1e9;
                    double remaining = allowed - (now - start);
                    if (remaining <= 0) {
                        throw new ErdigoException(Kind.NO_ANSWER);
                    }
                    delay = Math.min(delay, remaining);
                }
                sleep(delay);
            }
        }
    }

    /** 1, 2, 4 ... up to 60 seconds, with 20 percent jitter so that several devices do not retry in step. */
    static double backoff(int attempt) {
        double base = Math.min(60.0, Math.pow(2.0, Math.min(attempt, 6)));
        double jitter = 0.8 + 0.4 * (RANDOM.nextInt(1_000_001) / 1_000_000.0);
        return base * jitter;
    }
}
