package com.ziorentia.erdigo.sdk;

import com.ziorentia.erdigo.core.IntegrationV1;
import com.ziorentia.erdigo.core.Noise;
import java.util.Arrays;

/**
 * The device's one secret: an X25519 key used only for this protocol.
 *
 * <p>{@link #close()} clears this object's copy of the private key. Java gives no stronger guarantee than that: the JDK's
 * own key objects, made for each handshake, keep short-lived copies until they are collected. See the README.
 */
public final class DeviceIdentity implements AutoCloseable {
    private final byte[] privateKey;
    private final byte[] publicKey;
    private final String fingerprint;
    private volatile boolean closed;

    private DeviceIdentity(byte[] privateKey) {
        this.privateKey = privateKey;
        this.publicKey = Noise.publicKey(privateKey);
        this.fingerprint = IntegrationV1.fingerprint(publicKey);
    }

    public static DeviceIdentity generate() {
        return new DeviceIdentity(Noise.newPrivateKey());
    }

    /** Restores a key read from protected storage. The caller should clear {@code rawPrivateKey} afterwards. */
    public static DeviceIdentity fromPrivateKey(byte[] rawPrivateKey) {
        if (rawPrivateKey == null || rawPrivateKey.length != Noise.DH_LENGTH) {
            throw ErdigoException.builder(ErdigoException.Kind.STORAGE)
                    .detail("a device key is 32 bytes")
                    .storageProblem(ErdigoException.StorageProblem.IDENTITY_DAMAGED)
                    .build();
        }
        return new DeviceIdentity(rawPrivateKey.clone());
    }

    /** The public key, which identifies this device to a receiver. */
    public byte[] publicKey() {
        return publicKey.clone();
    }

    public String fingerprint() {
        return fingerprint;
    }

    /** The private key, for the handshake. Not a copy. */
    byte[] privateKey() {
        if (closed) {
            throw new IllegalStateException("the device identity was closed");
        }
        return privateKey;
    }

    /** A copy of the private key for storage. The caller should clear it. */
    byte[] exportPrivateKeyForStorage() {
        return privateKey().clone();
    }

    /** Clears the private key. The identity cannot be used afterwards. */
    @Override
    public void close() {
        closed = true;
        Arrays.fill(privateKey, (byte) 0);
    }

    @Override
    public String toString() {
        return "<device identity " + IntegrationV1.shortFingerprint(fingerprint) + ">";
    }
}
