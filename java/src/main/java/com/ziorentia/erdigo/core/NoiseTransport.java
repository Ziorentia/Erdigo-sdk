package com.ziorentia.erdigo.core;

/**
 * Encrypts and decrypts messages after a handshake has finished.
 *
 * <p>A handshake yields one transport, because {@link NoiseHandshake#split()} succeeds only once. A lock covers each
 * whole operation, including the counter increment, so no key and nonce pair is used twice and a message that has been
 * opened cannot be opened again. The lock orders the sealing, not the sending: the caller still has to send frames in
 * the order they were sealed, because the peer can only open them in that order.
 */
public final class NoiseTransport implements AutoCloseable {
    private final Object sync = new Object();
    private final NoiseCipherState sendCipher;
    private final NoiseCipherState receiveCipher;
    private final byte[] handshakeHash;

    NoiseTransport(NoiseCipherState sendCipher, NoiseCipherState receiveCipher, byte[] handshakeHash) {
        this.sendCipher = sendCipher;
        this.receiveCipher = receiveCipher;
        this.handshakeHash = handshakeHash.clone();
    }

    public byte[] handshakeHash() {
        return handshakeHash.clone();
    }

    public byte[] seal(byte[] plaintext) {
        if (plaintext.length + Noise.TAG_LENGTH > Noise.MAX_MESSAGE_LENGTH) {
            throw new NoiseException(NoiseException.Failure.MESSAGE_TOO_LARGE);
        }
        synchronized (sync) {
            return sendCipher.encrypt(new byte[0], plaintext);
        }
    }

    public byte[] open(byte[] ciphertext) {
        if (ciphertext.length > Noise.MAX_MESSAGE_LENGTH) {
            throw new NoiseException(NoiseException.Failure.MESSAGE_TOO_LARGE);
        }
        synchronized (sync) {
            return receiveCipher.decrypt(new byte[0], ciphertext);
        }
    }

    /** Clears both keys. Every later operation fails, rather than falling back to no encryption. */
    @Override
    public void close() {
        synchronized (sync) {
            sendCipher.initialize(new byte[32], NoiseCipherState.EXHAUSTED);
            receiveCipher.initialize(new byte[32], NoiseCipherState.EXHAUSTED);
        }
    }
}
