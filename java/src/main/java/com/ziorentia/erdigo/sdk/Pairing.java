package com.ziorentia.erdigo.sdk;

import com.ziorentia.erdigo.core.ConnectionException;
import com.ziorentia.erdigo.core.ErrorCode;
import com.ziorentia.erdigo.core.FramedConnection;
import com.ziorentia.erdigo.core.IntegrationV1;
import com.ziorentia.erdigo.core.ItemKind;
import com.ziorentia.erdigo.core.Messages.PairRequest;
import com.ziorentia.erdigo.core.Messages.PairStatus;
import com.ziorentia.erdigo.core.Messages.ReceiverError;
import com.ziorentia.erdigo.core.Messages.ReceiverMessage;
import com.ziorentia.erdigo.core.Noise;
import com.ziorentia.erdigo.core.NoiseException;
import com.ziorentia.erdigo.core.NoiseHandshake;
import com.ziorentia.erdigo.core.NoisePattern;
import com.ziorentia.erdigo.core.NoiseTransport;
import com.ziorentia.erdigo.core.PairingCode;
import com.ziorentia.erdigo.core.PairingTicket;
import com.ziorentia.erdigo.core.TextRules;
import com.ziorentia.erdigo.core.Wire;
import com.ziorentia.erdigo.core.WireException;
import com.ziorentia.erdigo.sdk.ErdigoException.Kind;
import java.net.InetSocketAddress;
import java.time.Duration;
import java.util.Arrays;
import java.util.Collection;
import java.util.List;
import java.util.Objects;

/** Pairs this device with a receiver, using the one-time code a person reads from the receiver's screen. */
public final class Pairing {
    /** What this SDK calls itself in {@code hello} and {@code pair_request}. */
    public static final String SDK_NAME = "erdigo-java/0.1.0";

    private Pairing() {
    }

    /** Shows a person the receiver's key, and returns whether it matches the key on the receiver's screen. */
    @FunctionalInterface
    public interface ConfirmReceiverKey {
        /**
         * @param receiverKey the key in the form the receiver shows it: the first 16 digits of its fingerprint
         * @return true only if a person compared the key and said it matches
         */
        boolean confirm(String receiverKey);
    }

    /** Optional settings for {@link #pair}. */
    public static final class Options {
        private String label;
        private Duration handshakeTimeout = Duration.ofSeconds(5);
        private Duration approvalTimeout = Duration.ofSeconds(120);
        private ConfirmReceiverKey confirmKey;

        /** A local name for the receiver. Defaults to the start of its fingerprint. */
        public Options label(String value) {
            label = value;
            return this;
        }

        /** How long a handshake may take. Five seconds by default. */
        public Options handshakeTimeout(Duration value) {
            handshakeTimeout = Objects.requireNonNull(value);
            return this;
        }

        /** How long to wait for the person to confirm. 120 seconds by default. */
        public Options approvalTimeout(Duration value) {
            approvalTimeout = Objects.requireNonNull(value);
            return this;
        }

        /**
         * Shows a person the receiver's key, and returns whether it matches the key on the receiver's screen. Needed for
         * a typed code, because a code proves only that the other side knows it, and not called for any other ticket.
         */
        public Options confirmKey(ConfirmReceiverKey value) {
            confirmKey = value;
            return this;
        }
    }

    /**
     * Pairs with a receiver and returns what to store about it.
     *
     * <p>A ticket with a fingerprint is compared here, and pairing stops on a mismatch. For a typed code,
     * {@code confirmKey} runs before the receiver hears anything about this device, so the receiver still shows its key.
     * If it returns false, this throws {@link Kind#KEY_NOT_CONFIRMED}, the code stays valid, and the receiver has seen
     * neither this device's key nor its name. If it returns true, the key the person confirmed is compared as if it came
     * from a QR code.
     *
     * @param target where to find the receiver
     * @param ticket the code, and the receiver's key fingerprint when it comes from the QR code
     * @param identity this device's key
     * @param deviceName shown to the person who confirms the request; treated as untrusted by the receiver
     * @param kinds what this device wants to send; the person may allow fewer
     */
    public static ReceiverPairing pair(
            ReceiverTarget target,
            PairingTicket ticket,
            DeviceIdentity identity,
            String deviceName,
            Collection<ItemKind> kinds,
            Options options) {
        Objects.requireNonNull(target, "target");
        Objects.requireNonNull(ticket, "ticket");
        Objects.requireNonNull(identity, "identity");
        Objects.requireNonNull(deviceName, "deviceName");
        Objects.requireNonNull(kinds, "kinds");
        Options settings = options == null ? new Options() : options;
        String name = TextRules.sanitizeDeviceName(deviceName);
        if (name.isEmpty()) {
            throw new ErdigoException(Kind.INVALID_ARGUMENT, "the device name cannot be empty");
        }
        if (kinds.isEmpty()) {
            throw new ErdigoException(Kind.INVALID_ARGUMENT, "choose at least one item kind");
        }
        if (settings.label != null) {
            validateLabel(settings.label);
        }
        boolean confirmedByAPerson = false;
        if (ticket.fingerprint() == null) {
            if (settings.confirmKey == null) {
                throw new ErdigoException(
                        Kind.INVALID_ARGUMENT, "a typed code needs confirmKey, because a person has to compare the receiver's key");
            }
            String found = receiverFingerprint(target, ticket.code(), settings.handshakeTimeout);
            if (!settings.confirmKey.confirm(IntegrationV1.shortFingerprint(found))) {
                throw new ErdigoException(Kind.KEY_NOT_CONFIRMED);
            }
            ticket = new PairingTicket(ticket.code(), found);
            confirmedByAPerson = true;
        }
        PairRequest request = new PairRequest(List.of(IntegrationV1.VERSION), SDK_NAME, name, ItemKind.canonicalList(kinds));
        ErdigoException lastError = new ErdigoException(Kind.NO_ANSWER);
        search:
        for (ReceiverEndpoint endpoint : Discovery.candidates(target)) {
            // After a person has confirmed the key, the receiver has just answered with this code, so silence now is
            // most likely the receiver still closing the connection that showed the key: a receiver limits how many
            // unproven connections one address may have at once. Trying again is safe, because nothing about this
            // device is sent until the receiver has proven the code and its key matches the one confirmed.
            int tries = confirmedByAPerson ? CONFIRMED_ATTEMPTS : 1;
            for (int round = 0; round < tries; round++) {
                if (round > 0) {
                    pause(CONFIRMED_RETRY_WAIT.multipliedBy(round));
                }
                try {
                    return attempt(endpoint, ticket, identity, request, settings);
                } catch (ErdigoException error) {
                    if (error.kind() == Kind.INTERRUPTED) {
                        throw error;
                    }
                    if (error.kind() != Kind.NO_ANSWER) {
                        // A definite answer from a receiver holding this code ends the search.
                        lastError = error;
                        break search;
                    }
                }
            }
        }
        throwIfInterrupted();
        throw lastError;
    }

    /** How many times the pairing connection is tried after a person confirmed the key. */
    static final int CONFIRMED_ATTEMPTS = 3;

    private static final Duration CONFIRMED_RETRY_WAIT = Duration.ofMillis(500);

    private static void pause(Duration time) {
        try {
            Thread.sleep(time.toMillis());
        } catch (InterruptedException error) {
            Thread.currentThread().interrupt();
            throw new ErdigoException(Kind.INTERRUPTED);
        }
    }

    /** Pairs with default options and the given {@code confirmKey}. */
    public static ReceiverPairing pair(
            ReceiverTarget target,
            PairingTicket ticket,
            DeviceIdentity identity,
            String deviceName,
            Collection<ItemKind> kinds,
            ConfirmReceiverKey confirmKey) {
        return pair(target, ticket, identity, deviceName, kinds, new Options().confirmKey(confirmKey));
    }

    static void throwIfInterrupted() {
        if (Thread.currentThread().isInterrupted()) {
            throw new ErdigoException(Kind.INTERRUPTED);
        }
    }

    /**
     * Finds the key of the receiver that holds this code, and stops there, so that a person can compare it. This runs
     * the exchange up to message 2, as spec section 4.1 describes.
     */
    private static String receiverFingerprint(ReceiverTarget target, PairingCode code, Duration handshakeTimeout) {
        // The handshake needs a key, but uses it only in message 3, which is never sent here.
        try (DeviceIdentity unused = DeviceIdentity.generate()) {
            ErdigoException lastError = new ErdigoException(Kind.NO_ANSWER);
            for (ReceiverEndpoint endpoint : Discovery.candidates(target)) {
                try (Handshake started = readReceiverKey(endpoint, code, unused, handshakeTimeout)) {
                    return IntegrationV1.fingerprint(started.receiverKey);
                } catch (ErdigoException error) {
                    if (error.kind() == Kind.INTERRUPTED) {
                        throw error;
                    }
                    if (error.kind() != Kind.NO_ANSWER) {
                        lastError = error;
                        break;
                    }
                }
            }
            throwIfInterrupted();
            throw lastError;
        }
    }

    static void validateLabel(String label) {
        try {
            TextRules.validateTitle(label);
        } catch (WireException error) {
            throw new ErdigoException(Kind.INVALID_ARGUMENT, "the receiver label cannot be empty or contain control characters");
        }
    }

    /** A connection whose handshake has reached the receiver's answer. This device's key and name are not sent yet. */
    private static final class Handshake implements AutoCloseable {
        final FramedConnection connection;
        final NoiseHandshake handshake;
        final byte[] receiverKey;

        Handshake(FramedConnection connection, NoiseHandshake handshake, byte[] receiverKey) {
            this.connection = connection;
            this.handshake = handshake;
            this.receiverKey = receiverKey;
        }

        @Override
        public void close() {
            handshake.close();
            connection.close();
        }
    }

    private static ErdigoException noAnswer(Throwable cause) {
        if (Thread.currentThread().isInterrupted()) {
            return ErdigoException.builder(Kind.INTERRUPTED).cause(cause).build();
        }
        return ErdigoException.builder(Kind.NO_ANSWER).cause(cause).build();
    }

    private static Handshake readReceiverKey(
            ReceiverEndpoint endpoint, PairingCode code, DeviceIdentity identity, Duration handshakeTimeout) {
        byte[] prologue = IntegrationV1.preamble(IntegrationV1.Mode.PAIR);
        FramedConnection connection;
        try {
            connection = FramedConnection.connect(endpoint.host(), endpoint.port(), handshakeTimeout);
        } catch (ConnectionException error) {
            throw noAnswer(error);
        }
        NoiseHandshake handshake = null;
        byte[] psk = code.presharedKey();
        try {
            handshake = new NoiseHandshake(NoisePattern.XX_PSK0, true, prologue, identity.privateKey(), null, psk);
            byte[] first = handshake.writeMessage(new byte[0]);
            connection.armDeadline(handshakeTimeout);
            connection.send(concat(prologue, FramedConnection.frame(first)));
            byte[] second = connection.readFrame(IntegrationV1.MAX_HANDSHAKE_FRAME_LENGTH);
            // Reading this message proves the receiver knows the code too.
            handshake.readMessage(second);
        } catch (ConnectionException | NoiseException | IllegalArgumentException error) {
            if (handshake != null) {
                handshake.close();
            }
            connection.close();
            throw noAnswer(error);
        } finally {
            Arrays.fill(psk, (byte) 0);
        }
        byte[] receiverKey = handshake.remoteStaticKey();
        if (receiverKey == null || !Noise.isCanonicalPublicKey(receiverKey)) {
            handshake.close();
            connection.close();
            throw new ErdigoException(Kind.PROTOCOL_VIOLATION, "the receiver sent an unusable key");
        }
        return new Handshake(connection, handshake, receiverKey);
    }

    private static ReceiverPairing attempt(
            ReceiverEndpoint endpoint, PairingTicket ticket, DeviceIdentity identity, PairRequest request, Options settings) {
        try (Handshake started = readReceiverKey(endpoint, ticket.code(), identity, settings.handshakeTimeout)) {
            String fingerprint = IntegrationV1.fingerprint(started.receiverKey);
            if (ticket.fingerprint() != null && !ticket.fingerprint().equals(fingerprint)) {
                throw new ErdigoException(Kind.FINGERPRINT_MISMATCH);
            }
            NoiseTransport transport;
            try {
                byte[] third = started.handshake.writeMessage(Wire.encode(request));
                started.connection.sendFrame(third);
                transport = started.handshake.split();
            } catch (ConnectionException | NoiseException error) {
                throw noAnswer(error);
            }
            try (NoiseTransport owned = transport) {
                // The person now decides, which can take a while.
                started.connection.armDeadline(settings.approvalTimeout);
                while (true) {
                    ReceiverMessage message;
                    try {
                        message = Wire.decodeReceiverMessage(owned.open(started.connection.readFrame()));
                    } catch (ConnectionException error) {
                        if (error.failure() == ConnectionException.Failure.TIMED_OUT) {
                            throw ErdigoException.builder(Kind.PAIRING_TIMED_OUT).cause(error).build();
                        }
                        throw noAnswer(error);
                    } catch (WireException error) {
                        throw ErdigoException.builder(Kind.PROTOCOL_VIOLATION).detail(error.detail()).cause(error).build();
                    } catch (NoiseException error) {
                        throw noAnswer(error);
                    }
                    if (message instanceof ReceiverMessage.PairStatusMessage status) {
                        if (status.status() instanceof PairStatus.Pending) {
                            continue;
                        }
                        if (status.status() instanceof PairStatus.Denied) {
                            throw new ErdigoException(Kind.PAIRING_DENIED);
                        }
                        if (status.status() instanceof PairStatus.Approved approved) {
                            InetSocketAddress address = started.connection.remoteAddress();
                            String label = settings.label != null ? settings.label : "receiver-" + fingerprint.substring(0, 8);
                            return new ReceiverPairing(
                                    label,
                                    started.receiverKey,
                                    approved.kinds(),
                                    address == null ? null : FramedConnection.addressText(address.getAddress()),
                                    address == null ? null : address.getPort(),
                                    null,
                                    approved.counter());
                        }
                    }
                    if (message instanceof ReceiverMessage.Error answer) {
                        ReceiverError error = answer.error();
                        if (error.is(ErrorCode.CODE_REUSED)) {
                            throw ErdigoException.builder(Kind.CODE_REUSED).receiverError(error).build();
                        }
                        if (error.is(ErrorCode.VERSION_UNSUPPORTED)) {
                            throw ErdigoException.builder(Kind.VERSION_UNSUPPORTED)
                                    .receiverError(error)
                                    .supported(error.supported() == null ? List.of() : error.supported())
                                    .build();
                        }
                        throw ErdigoException.builder(Kind.REJECTED).receiverError(error).build();
                    }
                    throw new ErdigoException(Kind.PROTOCOL_VIOLATION, "unexpected message during pairing");
                }
            }
        }
    }

    static byte[] concat(byte[] first, byte[] second) {
        byte[] output = Arrays.copyOf(first, first.length + second.length);
        System.arraycopy(second, 0, output, first.length, second.length);
        return output;
    }
}
