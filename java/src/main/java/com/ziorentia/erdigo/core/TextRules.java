package com.ziorentia.erdigo.core;

import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.Objects;

/** Rules for text that a person will eventually read on the receiver's screen. */
public final class TextRules {
    private TextRules() {
    }

    /** Control characters and invisible direction overrides, which can make text read differently from what it is. */
    public static boolean isDisallowed(int scalar, boolean allowLineBreaks) {
        if (scalar == 0x0A || scalar == 0x09) {
            return !allowLineBreaks;
        }
        return scalar <= 0x1F
                || (scalar >= 0x7F && scalar <= 0x9F)
                || scalar == 0x061C || scalar == 0x200E || scalar == 0x200F
                || (scalar >= 0x202A && scalar <= 0x202E)
                || (scalar >= 0x2066 && scalar <= 0x2069)
                || scalar == 0x2028 || scalar == 0x2029 || scalar == 0xFEFF;
    }

    /**
     * White space as the other SDKs read it: Unicode space, line and paragraph separators, U+0009 to U+000D and U+0085.
     */
    public static boolean isWhiteSpace(int scalar) {
        if ((scalar >= 0x09 && scalar <= 0x0D) || scalar == 0x85) {
            return true;
        }
        int type = Character.getType(scalar);
        return type == Character.SPACE_SEPARATOR || type == Character.LINE_SEPARATOR || type == Character.PARAGRAPH_SEPARATOR;
    }

    /** Removes white space, as {@link #isWhiteSpace} reads it, from both ends. */
    public static String trim(String text) {
        int start = 0;
        int end = text.length();
        while (start < end && isWhiteSpace(text.charAt(start))) {
            start += 1;
        }
        while (end > start && isWhiteSpace(text.charAt(end - 1))) {
            end -= 1;
        }
        return text.substring(start, end);
    }

    /**
     * The Unicode scalars of a string, or null if it holds a lone surrogate, which no other platform's strings can hold
     * and which UTF-8 cannot carry.
     */
    public static List<Integer> scalars(String text) {
        Objects.requireNonNull(text, "text");
        List<Integer> scalars = new ArrayList<>(text.length());
        int index = 0;
        while (index < text.length()) {
            char character = text.charAt(index);
            if (Character.isHighSurrogate(character) && index + 1 < text.length() && Character.isLowSurrogate(text.charAt(index + 1))) {
                scalars.add(Character.toCodePoint(character, text.charAt(index + 1)));
                index += 2;
            } else if (Character.isSurrogate(character)) {
                return null;
            } else {
                scalars.add((int) character);
                index += 1;
            }
        }
        return scalars;
    }

    public static void validateTitle(String text) {
        validateTitle(text, Limits.V1);
    }

    public static void validateTitle(String text, Limits limits) {
        Objects.requireNonNull(text, "text");
        List<Integer> scalars = scalars(text);
        if (scalars == null) {
            throw new WireException(ErrorCode.INVALID_TEXT, "a title must be valid Unicode text");
        }
        if (scalars.isEmpty()) {
            throw new WireException(ErrorCode.INVALID_TEXT, "a title cannot be empty; leave it out instead");
        }
        if (scalars.size() > limits.titleScalars()) {
            throw new WireException(ErrorCode.TOO_LARGE, "a title is at most " + limits.titleScalars() + " characters");
        }
        for (int scalar : scalars) {
            if (isDisallowed(scalar, false)) {
                throw new WireException(ErrorCode.INVALID_TEXT, "a title cannot contain control or text-direction characters");
            }
        }
    }

    public static void validateNote(String text) {
        validateNote(text, Limits.V1);
    }

    public static void validateNote(String text, Limits limits) {
        Objects.requireNonNull(text, "text");
        List<Integer> scalars = scalars(text);
        if (scalars == null) {
            throw new WireException(ErrorCode.INVALID_TEXT, "a note must be valid Unicode text");
        }
        if (scalars.isEmpty()) {
            throw new WireException(ErrorCode.INVALID_TEXT, "a note cannot be empty");
        }
        if (text.getBytes(StandardCharsets.UTF_8).length > limits.noteBytes()) {
            throw new WireException(ErrorCode.TOO_LARGE, "a note is at most " + limits.noteBytes() + " bytes of UTF-8");
        }
        for (int scalar : scalars) {
            if (isDisallowed(scalar, true)) {
                throw new WireException(ErrorCode.INVALID_TEXT, "a note cannot contain control or text-direction characters");
            }
        }
    }

    /** Receivers treat a device name as untrusted: unsafe characters are removed and it is cut to 40 characters. */
    public static String sanitizeDeviceName(String text) {
        Objects.requireNonNull(text, "text");
        StringBuilder kept = new StringBuilder();
        int index = 0;
        while (index < text.length()) {
            char character = text.charAt(index);
            if (Character.isHighSurrogate(character) && index + 1 < text.length() && Character.isLowSurrogate(text.charAt(index + 1))) {
                int scalar = Character.toCodePoint(character, text.charAt(index + 1));
                if (!isDisallowed(scalar, false)) {
                    kept.appendCodePoint(scalar);
                }
                index += 2;
            } else {
                // A lone surrogate is dropped like any other character that cannot be shown safely.
                if (!Character.isSurrogate(character) && !isDisallowed(character, false)) {
                    kept.append(character);
                }
                index += 1;
            }
        }
        String cleaned = trim(kept.toString());
        StringBuilder limited = new StringBuilder();
        int count = 0;
        for (int offset = 0; offset < cleaned.length() && count < IntegrationV1.MAX_DEVICE_NAME_SCALARS; count++) {
            int scalar = cleaned.codePointAt(offset);
            limited.appendCodePoint(scalar);
            offset += Character.charCount(scalar);
        }
        return limited.toString();
    }
}
