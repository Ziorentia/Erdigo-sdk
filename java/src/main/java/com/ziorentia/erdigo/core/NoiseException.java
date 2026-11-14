package com.ziorentia.erdigo.core;

/** Failures raised by the Noise layer. They deliberately carry no key material. */
public final class NoiseException extends RuntimeException {
    private static final long serialVersionUID = 1L;

    /** What went wrong. */
    public enum Failure {
        INVALID_KEY,
        WEAK_SHARED_SECRET,
        TRUNCATED_MESSAGE,
        DECRYPTION_FAILED,
        NONCE_EXHAUSTED,
        MESSAGE_TOO_LARGE,
        WRONG_HANDSHAKE_STATE,
        MISSING_KEY,
        /** A handshake was split a second time. A second pair of cipher states would repeat the first one's nonces. */
        ALREADY_SPLIT,
    }

    private final Failure failure;

    public NoiseException(Failure failure) {
        super("Noise: " + failure);
        this.failure = failure;
    }

    public Failure failure() {
        return failure;
    }
}
