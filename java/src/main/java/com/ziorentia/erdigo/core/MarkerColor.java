package com.ziorentia.erdigo.core;

/** The colours a marker can have. */
public enum MarkerColor {
    RED("red"),
    ORANGE("orange"),
    YELLOW("yellow"),
    GREEN("green"),
    BLUE("blue"),
    PURPLE("purple"),
    GRAY("gray");

    private final String wire;

    MarkerColor(String wire) {
        this.wire = wire;
    }

    public String wire() {
        return wire;
    }

    public static MarkerColor fromWire(String text) {
        for (MarkerColor color : values()) {
            if (color.wire.equals(text)) {
                return color;
            }
        }
        return null;
    }
}
