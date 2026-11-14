package com.ziorentia.erdigo.core;

import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.Objects;

/** The one-time code a person reads from the receiver: 100 random bits as 20 Crockford base32 characters. */
public final class PairingCode {
    private static final String ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private static final byte[] PSK_SALT = "erdigo-integration-v1/pairing-psk".getBytes(StandardCharsets.US_ASCII);

    private final String canonical;

    /**
     * Reads a code as a person may type it: hyphens and white space are ignored, letters are uppercased, O is read as 0,
     * and I and L as 1. Anything else, or any length but 20, is an error.
     */
    public PairingCode(String text) {
        Objects.requireNonNull(text, "text");
        StringBuilder output = new StringBuilder(20);
        int index = 0;
        while (index < text.length()) {
            int scalar = text.codePointAt(index);
            index += Character.charCount(scalar);
            if (scalar == '-' || TextRules.isWhiteSpace(scalar)) {
                continue;
            }
            // A lone surrogate is refused here like anything else that is not ASCII.
            if (scalar > 0x7F) {
                throw new WireException(ErrorCode.INVALID_MESSAGE, "the pairing code has invalid characters");
            }
            char character = (char) scalar;
            if (character >= 'a' && character <= 'z') {
                character = (char) (character - 0x20);
            }
            if (character == 'O') {
                character = '0';
            } else if (character == 'I' || character == 'L') {
                character = '1';
            }
            if (ALPHABET.indexOf(character) < 0) {
                throw new WireException(ErrorCode.INVALID_MESSAGE, "the pairing code has invalid characters");
            }
            output.append(character);
        }
        if (output.length() != 20) {
            throw new WireException(ErrorCode.INVALID_MESSAGE, "the pairing code has 20 characters");
        }
        canonical = output.toString();
    }

    /** The 20 characters without separators. */
    public String canonical() {
        return canonical;
    }

    /** {@code XXXXX-XXXXX-XXXXX-XXXXX}, for showing on the receiver's screen. */
    public String display() {
        return canonical.substring(0, 5) + "-" + canonical.substring(5, 10) + "-" + canonical.substring(10, 15) + "-"
                + canonical.substring(15, 20);
    }

    /** The Noise pre-shared key derived from the code. The caller owns the result and should clear it. */
    public byte[] presharedKey() {
        byte[] material = canonical.getBytes(StandardCharsets.US_ASCII);
        try {
            return Noise.hkdfSha256(material, PSK_SALT, 32);
        } finally {
            Arrays.fill(material, (byte) 0);
        }
    }

    @Override
    public boolean equals(Object other) {
        return other instanceof PairingCode code && code.canonical.equals(canonical);
    }

    @Override
    public int hashCode() {
        return canonical.hashCode();
    }

    /** Avoids writing the code to logs by accident. */
    @Override
    public String toString() {
        return "<pairing code>";
    }
}
