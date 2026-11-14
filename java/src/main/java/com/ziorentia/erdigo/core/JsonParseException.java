package com.ziorentia.erdigo.core;

/** Why {@link StrictJson#parse} refused its input. */
public final class JsonParseException extends RuntimeException {
    private static final long serialVersionUID = 1L;

    /** The kinds of refusal. */
    public enum Failure {
        INVALID_UTF8,
        UNEXPECTED_BYTE,
        UNEXPECTED_END,
        DUPLICATE_KEY,
        TOO_DEEP,
        NUMBER_OUT_OF_RANGE,
        INVALID_ESCAPE,
        TRAILING_DATA,
    }

    private final Failure failure;

    public JsonParseException(Failure failure) {
        super("strict JSON: " + failure);
        this.failure = failure;
    }

    public Failure failure() {
        return failure;
    }
}
