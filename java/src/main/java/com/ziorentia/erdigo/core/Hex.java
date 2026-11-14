package com.ziorentia.erdigo.core;

import java.util.Objects;

/** Hexadecimal text, the way the protocol writes it. */
public final class Hex {
    private static final char[] DIGITS = "0123456789abcdef".toCharArray();

    private Hex() {
    }

    /** Lowercase hexadecimal. */
    public static String encode(byte[] data) {
        Objects.requireNonNull(data, "data");
        char[] output = new char[data.length * 2];
        for (int index = 0; index < data.length; index++) {
            output[index * 2] = DIGITS[(data[index] >> 4) & 0x0F];
            output[index * 2 + 1] = DIGITS[data[index] & 0x0F];
        }
        return new String(output);
    }

    /** Accepts lowercase or uppercase hexadecimal digits only, in pairs. Returns null for anything else. */
    public static byte[] decode(String text) {
        Objects.requireNonNull(text, "text");
        if (text.length() % 2 != 0) {
            return null;
        }
        byte[] output = new byte[text.length() / 2];
        for (int index = 0; index < text.length(); index += 2) {
            int high = value(text.charAt(index));
            int low = value(text.charAt(index + 1));
            if (high < 0 || low < 0) {
                return null;
            }
            output[index / 2] = (byte) ((high << 4) | low);
        }
        return output;
    }

    private static int value(char digit) {
        if (digit >= '0' && digit <= '9') {
            return digit - '0';
        }
        if (digit >= 'a' && digit <= 'f') {
            return digit - 'a' + 10;
        }
        if (digit >= 'A' && digit <= 'F') {
            return digit - 'A' + 10;
        }
        return -1;
    }

    /** True for exactly 64 lowercase hexadecimal digits, the one spelling a fingerprint has (spec section 3). */
    public static boolean isFingerprint(String text) {
        if (text == null || text.length() != 64) {
            return false;
        }
        for (int index = 0; index < text.length(); index++) {
            char digit = text.charAt(index);
            if (!((digit >= '0' && digit <= '9') || (digit >= 'a' && digit <= 'f'))) {
                return false;
            }
        }
        return true;
    }
}
