package com.ziorentia.erdigo.core;

import java.util.List;
import java.util.Map;

/**
 * A decoded JSON value. Integers and fractional numbers are kept apart, so that a field documented as an integer cannot
 * silently accept {@code 1.5}.
 */
public sealed interface JsonValue {
    /** {@code null}. */
    record Null() implements JsonValue {
    }

    /** {@code true} or {@code false}. */
    record Bool(boolean value) implements JsonValue {
    }

    /** A number written without a fraction or exponent that fits in 64 bits. */
    record Integer(long value) implements JsonValue {
    }

    /** Any other finite number. */
    record Number(double value) implements JsonValue {
    }

    /** A string. */
    record Text(String value) implements JsonValue {
    }

    /** An array. */
    record Array(List<JsonValue> values) implements JsonValue {
        public Array {
            values = List.copyOf(values);
        }
    }

    /** An object, with its fields in the order they were written. */
    record Object(Map<String, JsonValue> fields) implements JsonValue {
        public Object {
            fields = java.util.Collections.unmodifiableMap(new java.util.LinkedHashMap<>(fields));
        }
    }
}
