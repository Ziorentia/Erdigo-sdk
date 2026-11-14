package com.ziorentia.erdigo.core;

import java.util.ArrayList;
import java.util.Collection;
import java.util.EnumSet;
import java.util.List;

/** The kinds of item a device can write, in the order the protocol lists them. */
public enum ItemKind {
    PIN("pin"),
    MARKER("marker"),
    NOTE("note"),
    PHOTO("photo"),
    TRACK("track");

    private final String wire;

    ItemKind(String wire) {
        this.wire = wire;
    }

    public String wire() {
        return wire;
    }

    public static ItemKind fromWire(String text) {
        for (ItemKind kind : values()) {
            if (kind.wire.equals(text)) {
                return kind;
            }
        }
        return null;
    }

    /** The kinds given, without repeats, in the protocol's order. */
    public static List<ItemKind> canonicalList(Collection<ItemKind> kinds) {
        EnumSet<ItemKind> set = kinds.isEmpty() ? EnumSet.noneOf(ItemKind.class) : EnumSet.copyOf(kinds);
        return List.copyOf(new ArrayList<>(set));
    }
}
