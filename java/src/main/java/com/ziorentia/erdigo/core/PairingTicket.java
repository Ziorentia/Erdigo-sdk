package com.ziorentia.erdigo.core;

import java.nio.charset.StandardCharsets;
import java.util.Locale;
import java.util.Objects;

/**
 * What a person gives the SDK to pair: the code, and the receiver's key fingerprint when it comes from the QR code. A
 * typed code has no fingerprint, and the person then has to compare the key.
 */
public final class PairingTicket {
    private static final String PREFIX = "erdigo-pair:1?";

    /** Much longer than any valid text, which is 110 bytes without white space around it. */
    private static final int MAX_TEXT_LENGTH = 1024;

    private final PairingCode code;
    private final String fingerprint;

    public PairingTicket(PairingCode code, String fingerprint) {
        this.code = Objects.requireNonNull(code, "code");
        this.fingerprint = fingerprint;
    }

    public PairingCode code() {
        return code;
    }

    /** The receiver's fingerprint from the QR text, or null for a typed code. */
    public String fingerprint() {
        return fingerprint;
    }

    /**
     * Accepts either a bare code or the QR form {@code erdigo-pair:1?code=...&fp=...}. The QR form needs exactly one
     * code and exactly one valid fingerprint, because the key comparison is what makes it stronger than a typed code.
     * Nothing else may appear in it. Text over 1024 bytes is refused before it is looked at, because it may come from
     * anywhere.
     */
    public static PairingTicket parse(String text) {
        Objects.requireNonNull(text, "text");
        if (text.getBytes(StandardCharsets.UTF_8).length > MAX_TEXT_LENGTH) {
            throw new WireException(ErrorCode.INVALID_MESSAGE, "the pairing text is too long");
        }
        String trimmed = TextRules.trim(text);
        if (!trimmed.startsWith(PREFIX)) {
            return new PairingTicket(new PairingCode(trimmed), null);
        }
        PairingCode code = null;
        String fingerprint = null;
        // Empty pieces are kept, so that '&&', a trailing '&' and a field starting with '=' are refused below.
        for (String part : trimmed.substring(PREFIX.length()).split("&", -1)) {
            int equals = part.indexOf('=');
            if (equals < 0) {
                throw new WireException(ErrorCode.INVALID_MESSAGE, "the pairing text is malformed");
            }
            String name = part.substring(0, equals);
            String value = part.substring(equals + 1);
            if (name.equals("code") && code == null) {
                code = new PairingCode(value);
            } else if (name.equals("fp") && fingerprint == null) {
                String lowered = value.toLowerCase(Locale.ROOT);
                if (!Hex.isFingerprint(lowered)) {
                    throw new WireException(ErrorCode.INVALID_MESSAGE, "the fingerprint in the pairing text is malformed");
                }
                fingerprint = lowered;
            } else {
                throw new WireException(ErrorCode.INVALID_MESSAGE, "the pairing text is malformed");
            }
        }
        if (code == null) {
            throw new WireException(ErrorCode.INVALID_MESSAGE, "the pairing text has no code");
        }
        if (fingerprint == null) {
            throw new WireException(ErrorCode.INVALID_MESSAGE, "the pairing text has no fingerprint");
        }
        return new PairingTicket(code, fingerprint);
    }

    /**
     * The text of the QR code, or null without a valid fingerprint, because QR text without one would look stronger
     * than it is.
     */
    public String uri() {
        return Hex.isFingerprint(fingerprint) ? PREFIX + "code=" + code.display() + "&fp=" + fingerprint : null;
    }

    @Override
    public boolean equals(Object other) {
        return other instanceof PairingTicket ticket && ticket.code.equals(code) && Objects.equals(ticket.fingerprint, fingerprint);
    }

    @Override
    public int hashCode() {
        return Objects.hash(code, fingerprint);
    }

    @Override
    public String toString() {
        return "<pairing ticket>";
    }
}
