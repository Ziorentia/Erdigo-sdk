package com.ziorentia.erdigo.core;

import java.util.Objects;

/** An item identifier: 1 to 64 ASCII letters, digits, '-' or '_'. */
public record ItemId(String value) {
    public ItemId {
        Objects.requireNonNull(value, "value");
        boolean allowed = value.length() >= 1 && value.length() <= 64;
        for (int index = 0; index < value.length() && allowed; index++) {
            char character = value.charAt(index);
            allowed = (character >= 'a' && character <= 'z') || (character >= 'A' && character <= 'Z')
                    || (character >= '0' && character <= '9') || character == '-' || character == '_';
        }
        if (!allowed) {
            throw new WireException(ErrorCode.INVALID_ID, "an item id is 1 to 64 ASCII letters, digits, '-' or '_'");
        }
    }

    @Override
    public String toString() {
        return value;
    }
}
