package com.ziorentia.erdigo.core;

/** The only photo formats the protocol allows. */
public enum PhotoType {
    JPEG("image/jpeg"),
    PNG("image/png");

    private static final byte[] PNG_SIGNATURE = {(byte) 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A};

    private final String wire;

    PhotoType(String wire) {
        this.wire = wire;
    }

    public String wire() {
        return wire;
    }

    public static PhotoType fromWire(String text) {
        for (PhotoType type : values()) {
            if (type.wire.equals(text)) {
                return type;
            }
        }
        return null;
    }

    /** Detects the format from the first bytes. Anything but JPEG or PNG returns null. */
    public static PhotoType detect(byte[] data) {
        if (data.length >= 3 && data[0] == (byte) 0xFF && data[1] == (byte) 0xD8 && data[2] == (byte) 0xFF) {
            return JPEG;
        }
        if (data.length >= 8) {
            for (int index = 0; index < 8; index++) {
                if (data[index] != PNG_SIGNATURE[index]) {
                    return null;
                }
            }
            return PNG;
        }
        return null;
    }
}
