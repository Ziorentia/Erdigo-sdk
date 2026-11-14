package com.ziorentia.erdigo.send;

/** A mistake in how the tool was called. Its message never repeats what was typed. */
final class UsageException extends RuntimeException {
    private static final long serialVersionUID = 1L;

    UsageException(String message) {
        super(message);
    }
}
