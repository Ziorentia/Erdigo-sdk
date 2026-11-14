package com.ziorentia.erdigo.core;

import java.io.IOException;

/** Why a {@link FramedConnection} operation failed. */
public final class ConnectionException extends IOException {
    private static final long serialVersionUID = 1L;

    /** The kinds of failure. */
    public enum Failure {
        CLOSED,
        TIMED_OUT,
        FRAME_TOO_LARGE,
        CONNECTION_FAILED,
        /** The thread was interrupted. Its interrupt flag is still set. */
        INTERRUPTED,
    }

    private final Failure failure;

    public ConnectionException(Failure failure) {
        super("connection: " + failure);
        this.failure = failure;
    }

    public Failure failure() {
        return failure;
    }
}
