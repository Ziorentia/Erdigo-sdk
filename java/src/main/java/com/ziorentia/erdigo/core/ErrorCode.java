package com.ziorentia.erdigo.core;

/** The error codes of spec section 10. */
public enum ErrorCode {
    VERSION_UNSUPPORTED("version_unsupported"),
    INVALID_MESSAGE("invalid_message"),
    INVALID_ID("invalid_id"),
    INVALID_COORDINATE("invalid_coordinate"),
    INVALID_TEXT("invalid_text"),
    NOT_PERMITTED("not_permitted"),
    NOT_FOUND("not_found"),
    KIND_CONFLICT("kind_conflict"),
    INVALID_STATE("invalid_state"),
    STALE_SEQ("stale_seq"),
    SEQ_CONFLICT("seq_conflict"),
    LIMIT_ITEMS("limit_items"),
    LIMIT_PHOTOS("limit_photos"),
    LIMIT_TRACKS("limit_tracks"),
    LIMIT_DISK("limit_disk"),
    TOO_LARGE("too_large"),
    PHOTO_INVALID("photo_invalid"),
    RATE_LIMITED("rate_limited"),
    TOO_MANY_ERRORS("too_many_errors"),
    PAUSED("paused"),
    REPLACED("replaced"),
    CODE_REUSED("code_reused"),
    INTERNAL_ERROR("internal_error");

    private final String wire;

    ErrorCode(String wire) {
        this.wire = wire;
    }

    /** The spelling on the wire, such as {@code invalid_message}. */
    public String wire() {
        return wire;
    }

    /** Reads a code from the wire. A code this SDK does not know returns null. */
    public static ErrorCode fromWire(String text) {
        for (ErrorCode code : values()) {
            if (code.wire.equals(text)) {
                return code;
            }
        }
        return null;
    }

    /** Retryable errors do not use up the sequence number. */
    public boolean isRetryable() {
        return this == RATE_LIMITED || this == PAUSED || this == INTERNAL_ERROR;
    }
}
