package com.ziorentia.erdigo.core;

import java.io.ByteArrayOutputStream;
import java.util.ArrayDeque;
import java.util.Arrays;
import java.util.Deque;
import java.util.List;

/** The Noise handshake state machine. */
public final class NoiseHandshake implements AutoCloseable {
    private final NoisePattern pattern;
    private final boolean isInitiator;
    private final NoiseSymmetricState symmetric;
    private final byte[] staticKey;
    private final byte[] fixedEphemeral;
    private final Deque<byte[]> psks = new ArrayDeque<>();
    private final Object splitLock = new Object();
    private byte[] remoteStaticKey;
    private byte[] ephemeralKey;
    private byte[] remoteEphemeralKey;
    private int messageIndex;
    private boolean hasSplit;

    /**
     * Starts a handshake. {@code staticKey} is this side's X25519 private key; the handshake keeps a reference to it
     * rather than a copy. {@code remoteStaticKey} is the responder's public key, for a pattern that needs it in advance,
     * and {@code psk} the pre-shared key, for a pattern that uses one.
     */
    public NoiseHandshake(
            NoisePattern pattern, boolean isInitiator, byte[] prologue, byte[] staticKey, byte[] remoteStaticKey, byte[] psk) {
        this(pattern, isInitiator, prologue, staticKey, remoteStaticKey, psk == null ? List.of() : List.of(psk), null);
    }

    /**
     * {@code fixedEphemeral} exists only so that published test vectors can be reproduced. Reusing an ephemeral key in
     * real traffic breaks the protocol, so this constructor is not public and the SDK never passes one.
     */
    NoiseHandshake(
            NoisePattern pattern,
            boolean isInitiator,
            byte[] prologue,
            byte[] staticKey,
            byte[] remoteStaticKey,
            List<byte[]> psks,
            byte[] fixedEphemeral) {
        for (byte[] key : psks) {
            if (key.length != 32) {
                throw new NoiseException(NoiseException.Failure.INVALID_KEY);
            }
        }
        if (pattern.usesPsk() && psks.isEmpty()) {
            throw new NoiseException(NoiseException.Failure.MISSING_KEY);
        }
        if (remoteStaticKey != null && remoteStaticKey.length != Noise.DH_LENGTH) {
            throw new NoiseException(NoiseException.Failure.INVALID_KEY);
        }
        if ((staticKey != null && staticKey.length != Noise.DH_LENGTH)
                || (fixedEphemeral != null && fixedEphemeral.length != Noise.DH_LENGTH)) {
            throw new NoiseException(NoiseException.Failure.INVALID_KEY);
        }
        this.pattern = pattern;
        this.isInitiator = isInitiator;
        this.staticKey = staticKey;
        this.remoteStaticKey = remoteStaticKey == null ? null : remoteStaticKey.clone();
        // Copies, so that the caller may clear its own.
        for (byte[] key : psks) {
            this.psks.addLast(key.clone());
        }
        this.fixedEphemeral = fixedEphemeral == null ? null : fixedEphemeral.clone();
        symmetric = new NoiseSymmetricState(pattern.protocolName());
        symmetric.mixHash(prologue);
        if (pattern.responderStaticIsPreMessage()) {
            if (isInitiator) {
                if (this.remoteStaticKey == null) {
                    throw new NoiseException(NoiseException.Failure.MISSING_KEY);
                }
                symmetric.mixHash(this.remoteStaticKey);
            } else {
                if (staticKey == null) {
                    throw new NoiseException(NoiseException.Failure.MISSING_KEY);
                }
                symmetric.mixHash(Noise.publicKey(staticKey));
            }
        }
    }

    /** The other side's static public key, once the handshake has carried it. */
    public byte[] remoteStaticKey() {
        return remoteStaticKey == null ? null : remoteStaticKey.clone();
    }

    public boolean isComplete() {
        return messageIndex == pattern.messages().size();
    }

    public byte[] handshakeHash() {
        return symmetric.hash().clone();
    }

    private boolean isMyTurn() {
        return (messageIndex % 2 == 0) == isInitiator;
    }

    private static byte[] require(byte[] key) {
        if (key == null) {
            throw new NoiseException(NoiseException.Failure.MISSING_KEY);
        }
        return key;
    }

    private void mixDh(NoisePattern.Token token) {
        byte[] secret;
        switch (token) {
            case EE:
                secret = Noise.dh(require(ephemeralKey), require(remoteEphemeralKey));
                break;
            case ES:
                secret = isInitiator
                        ? Noise.dh(require(ephemeralKey), require(remoteStaticKey))
                        : Noise.dh(require(staticKey), require(remoteEphemeralKey));
                break;
            case SE:
                secret = isInitiator
                        ? Noise.dh(require(staticKey), require(remoteEphemeralKey))
                        : Noise.dh(require(ephemeralKey), require(remoteStaticKey));
                break;
            case SS:
                secret = Noise.dh(require(staticKey), require(remoteStaticKey));
                break;
            default:
                throw new NoiseException(NoiseException.Failure.WRONG_HANDSHAKE_STATE);
        }
        try {
            symmetric.mixKey(secret);
        } finally {
            Arrays.fill(secret, (byte) 0);
        }
    }

    private void mixPsk() {
        byte[] psk = psks.pollFirst();
        if (psk == null) {
            throw new NoiseException(NoiseException.Failure.MISSING_KEY);
        }
        try {
            symmetric.mixKeyAndHash(psk);
        } finally {
            Arrays.fill(psk, (byte) 0);
        }
    }

    public byte[] writeMessage(byte[] payload) {
        if (payload == null) {
            payload = new byte[0];
        }
        if (isComplete() || !isMyTurn()) {
            throw new NoiseException(NoiseException.Failure.WRONG_HANDSHAKE_STATE);
        }
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        for (NoisePattern.Token token : pattern.messages().get(messageIndex)) {
            switch (token) {
                case E: {
                    byte[] key = fixedEphemeral != null ? fixedEphemeral.clone() : Noise.newPrivateKey();
                    if (ephemeralKey != null) {
                        Arrays.fill(ephemeralKey, (byte) 0);
                    }
                    ephemeralKey = key;
                    byte[] publicKey = Noise.publicKey(key);
                    output.writeBytes(publicKey);
                    symmetric.mixHash(publicKey);
                    if (pattern.usesPsk()) {
                        symmetric.mixKey(publicKey);
                    }
                    break;
                }
                case S:
                    output.writeBytes(symmetric.encryptAndHash(Noise.publicKey(require(staticKey))));
                    break;
                case PSK:
                    mixPsk();
                    break;
                default:
                    mixDh(token);
                    break;
            }
        }
        output.writeBytes(symmetric.encryptAndHash(payload));
        if (output.size() > Noise.MAX_MESSAGE_LENGTH) {
            throw new NoiseException(NoiseException.Failure.MESSAGE_TOO_LARGE);
        }
        messageIndex += 1;
        return output.toByteArray();
    }

    public byte[] readMessage(byte[] message) {
        if (isComplete() || isMyTurn()) {
            throw new NoiseException(NoiseException.Failure.WRONG_HANDSHAKE_STATE);
        }
        if (message.length > Noise.MAX_MESSAGE_LENGTH) {
            throw new NoiseException(NoiseException.Failure.MESSAGE_TOO_LARGE);
        }
        int offset = 0;
        for (NoisePattern.Token token : pattern.messages().get(messageIndex)) {
            switch (token) {
                case E: {
                    byte[] publicKey = take(message, offset, Noise.DH_LENGTH);
                    offset += Noise.DH_LENGTH;
                    remoteEphemeralKey = publicKey;
                    symmetric.mixHash(publicKey);
                    if (pattern.usesPsk()) {
                        symmetric.mixKey(publicKey);
                    }
                    break;
                }
                case S: {
                    int length = symmetric.hasKey() ? Noise.DH_LENGTH + Noise.TAG_LENGTH : Noise.DH_LENGTH;
                    byte[] sealed = take(message, offset, length);
                    offset += length;
                    remoteStaticKey = symmetric.decryptAndHash(sealed);
                    break;
                }
                case PSK:
                    mixPsk();
                    break;
                default:
                    mixDh(token);
                    break;
            }
        }
        byte[] payload = symmetric.decryptAndHash(Arrays.copyOfRange(message, offset, message.length));
        messageIndex += 1;
        return payload;
    }

    private static byte[] take(byte[] message, int offset, int count) {
        if (message.length - offset < count) {
            throw new NoiseException(NoiseException.Failure.TRUNCATED_MESSAGE);
        }
        return Arrays.copyOfRange(message, offset, offset + count);
    }

    /**
     * Call once {@link #isComplete()} is true. This succeeds only once, even if called from several threads, because a
     * second transport would have the same keys and start from the same nonces.
     */
    public NoiseTransport split() {
        if (!isComplete()) {
            throw new NoiseException(NoiseException.Failure.WRONG_HANDSHAKE_STATE);
        }
        synchronized (splitLock) {
            if (hasSplit) {
                throw new NoiseException(NoiseException.Failure.ALREADY_SPLIT);
            }
            hasSplit = true;
            NoiseCipherState[] pair = symmetric.split();
            byte[] hash = symmetric.hash().clone();
            clearEphemeral();
            return isInitiator ? new NoiseTransport(pair[0], pair[1], hash) : new NoiseTransport(pair[1], pair[0], hash);
        }
    }

    private void clearEphemeral() {
        if (ephemeralKey != null) {
            Arrays.fill(ephemeralKey, (byte) 0);
            ephemeralKey = null;
        }
    }

    /** Clears the ephemeral key and the handshake's own key material. The static key belongs to the caller. */
    @Override
    public void close() {
        synchronized (splitLock) {
            clearEphemeral();
            byte[] psk;
            while ((psk = psks.pollFirst()) != null) {
                Arrays.fill(psk, (byte) 0);
            }
            symmetric.clear();
        }
    }
}
