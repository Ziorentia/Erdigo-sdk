package com.ziorentia.erdigo.core;

import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.Objects;

/** Constants of the integration-v1 protocol. See spec/integration-v1.md. */
public final class IntegrationV1 {
    public static final int VERSION = 1;
    public static final String SERVICE_TYPE = "_erdigo-int._tcp";
    public static final int MAX_FRAME_LENGTH = 65535;
    public static final int MAX_PLAINTEXT_LENGTH = 65519;
    public static final int MAX_HANDSHAKE_FRAME_LENGTH = 1024;
    public static final int MAX_CHUNK_BYTES = 48_000;
    public static final int MAX_POINTS_PER_MESSAGE = 100;
    public static final long MAX_SEQUENCE = (1L << 53) - 1;
    public static final int MAX_DEVICE_NAME_SCALARS = 40;
    public static final int CONNECT_FIRST_MESSAGE_LENGTH = 32 + 48 + 8 + 16;
    public static final int PAIR_FIRST_MESSAGE_LENGTH = 32 + 16;

    private static final byte[] FINGERPRINT_LABEL = "erdigo-integration-v1/fingerprint".getBytes(StandardCharsets.US_ASCII);

    private IntegrationV1() {
    }

    /** The fifth byte of the preamble. */
    public enum Mode {
        CONNECT((byte) 0x01),
        PAIR((byte) 0x02);

        private final byte value;

        Mode(byte value) {
            this.value = value;
        }

        public byte value() {
            return value;
        }
    }

    /**
     * Five bytes sent in the clear before the handshake. They are also the Noise prologue, so tampering with them makes
     * the handshake fail.
     */
    public static byte[] preamble(Mode mode) {
        return new byte[] {0x45, 0x49, 0x4E, 0x31, mode.value()};
    }

    /** The receiver's or a device's key fingerprint, as 64 lowercase hexadecimal digits. */
    public static String fingerprint(byte[] publicKey) {
        Objects.requireNonNull(publicKey, "publicKey");
        MessageDigest digest = sha256();
        digest.update(FINGERPRINT_LABEL);
        digest.update(publicKey);
        return Hex.encode(digest.digest());
    }

    /** The first 16 hexadecimal digits in groups of four, for showing to people. */
    public static String shortFingerprint(String fingerprint) {
        Objects.requireNonNull(fingerprint, "fingerprint");
        String head = fingerprint.length() > 16 ? fingerprint.substring(0, 16) : fingerprint;
        StringBuilder builder = new StringBuilder();
        for (int index = 0; index < head.length(); index += 4) {
            if (index > 0) {
                builder.append('-');
            }
            builder.append(head, index, Math.min(index + 4, head.length()));
        }
        return builder.toString();
    }

    static MessageDigest sha256() {
        try {
            return MessageDigest.getInstance("SHA-256");
        } catch (NoSuchAlgorithmException error) {
            throw new IllegalStateException("SHA-256 is missing from this Java runtime", error);
        }
    }

    /** SHA-256 of {@code data}. */
    public static byte[] sha256(byte[] data) {
        return sha256().digest(data);
    }
}
