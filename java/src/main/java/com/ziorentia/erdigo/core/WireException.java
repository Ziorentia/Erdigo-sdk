package com.ziorentia.erdigo.core;

/** A wire-level rejection, carrying the error code a receiver reports. */
public final class WireException extends RuntimeException {
    private static final long serialVersionUID = 1L;

    private final ErrorCode code;

    public WireException(ErrorCode code, String detail) {
        super(detail);
        this.code = code;
    }

    public ErrorCode code() {
        return code;
    }

    public String detail() {
        return getMessage();
    }
}
