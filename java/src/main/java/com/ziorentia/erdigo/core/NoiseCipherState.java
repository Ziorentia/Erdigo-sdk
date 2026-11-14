package com.ziorentia.erdigo.core;

import java.util.Arrays;

/** One direction of a Noise session: a key and the counter that makes each message's nonce. */
final class NoiseCipherState {
    /** 2^64-1, which the Noise specification reserves. The counter is unsigned, so this is -1 as a Java long. */
    static final long EXHAUSTED = -1L;

    private byte[] key;
    private long counter;

    boolean hasKey() {
        return key != null;
    }

    /** {@code start} is not zero only in tests of what happens as the counter runs out. */
    void initialize(byte[] raw, long start) {
        clear();
        key = Arrays.copyOf(raw, 32);
        counter = start;
    }

    void initialize(byte[] raw) {
        initialize(raw, 0);
    }

    byte[] encrypt(byte[] associatedData, byte[] plaintext) {
        if (key == null) {
            return plaintext.clone();
        }
        if (counter == EXHAUSTED) {
            throw new NoiseException(NoiseException.Failure.NONCE_EXHAUSTED);
        }
        byte[] output = Noise.encrypt(key, counter, associatedData, plaintext);
        counter += 1;
        return output;
    }

    byte[] decrypt(byte[] associatedData, byte[] ciphertext) {
        if (key == null) {
            return ciphertext.clone();
        }
        if (counter == EXHAUSTED) {
            throw new NoiseException(NoiseException.Failure.NONCE_EXHAUSTED);
        }
        if (ciphertext.length < Noise.TAG_LENGTH) {
            throw new NoiseException(NoiseException.Failure.TRUNCATED_MESSAGE);
        }
        byte[] output = Noise.decrypt(key, counter, associatedData, ciphertext);
        counter += 1;
        return output;
    }

    void clear() {
        if (key != null) {
            Arrays.fill(key, (byte) 0);
            key = null;
        }
    }
}
