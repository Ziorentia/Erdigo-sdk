package com.ziorentia.erdigo.core;

import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.Arrays;

/** The SymmetricState of the Noise specification. */
final class NoiseSymmetricState {
    private final NoiseCipherState cipher = new NoiseCipherState();
    private byte[] chainingKey;
    private byte[] hash;

    NoiseSymmetricState(String protocolName) {
        byte[] name = protocolName.getBytes(StandardCharsets.US_ASCII);
        if (name.length <= Noise.HASH_LENGTH) {
            hash = Arrays.copyOf(name, Noise.HASH_LENGTH);
        } else {
            hash = IntegrationV1.sha256(name);
        }
        chainingKey = hash.clone();
    }

    byte[] hash() {
        return hash;
    }

    boolean hasKey() {
        return cipher.hasKey();
    }

    void mixHash(byte[] data) {
        MessageDigest digest = IntegrationV1.sha256();
        digest.update(hash);
        digest.update(data);
        hash = digest.digest();
    }

    void mixKey(byte[] inputKeyMaterial) {
        byte[][] output = Noise.hkdf(chainingKey, inputKeyMaterial, 2);
        Arrays.fill(chainingKey, (byte) 0);
        chainingKey = output[0];
        cipher.initialize(output[1]);
        Arrays.fill(output[1], (byte) 0);
    }

    void mixKeyAndHash(byte[] inputKeyMaterial) {
        byte[][] output = Noise.hkdf(chainingKey, inputKeyMaterial, 3);
        Arrays.fill(chainingKey, (byte) 0);
        chainingKey = output[0];
        mixHash(output[1]);
        cipher.initialize(output[2]);
        Arrays.fill(output[1], (byte) 0);
        Arrays.fill(output[2], (byte) 0);
    }

    byte[] encryptAndHash(byte[] plaintext) {
        byte[] ciphertext = cipher.encrypt(hash, plaintext);
        mixHash(ciphertext);
        return ciphertext;
    }

    byte[] decryptAndHash(byte[] ciphertext) {
        byte[] plaintext = cipher.decrypt(hash, ciphertext);
        mixHash(ciphertext);
        return plaintext;
    }

    NoiseCipherState[] split() {
        byte[][] output = Noise.hkdf(chainingKey, new byte[0], 2);
        NoiseCipherState first = new NoiseCipherState();
        NoiseCipherState second = new NoiseCipherState();
        first.initialize(output[0]);
        second.initialize(output[1]);
        Arrays.fill(output[0], (byte) 0);
        Arrays.fill(output[1], (byte) 0);
        return new NoiseCipherState[] {first, second};
    }

    void clear() {
        Arrays.fill(chainingKey, (byte) 0);
        cipher.clear();
    }
}
