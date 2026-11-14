package com.ziorentia.erdigo.core;

/** Limits a receiver enforces per device. The receiver announces them in {@code welcome}. */
public record Limits(
        int items,
        int photos,
        int photoBytes,
        int noteBytes,
        int titleScalars,
        int trackPoints,
        int openTracks,
        int tracks,
        int messagesPerSecond,
        int messageBurst,
        int pointsPerSecond,
        int pointBurst,
        long diskBytes) {
    /** The values of spec section 11. */
    public static final Limits V1 = new Limits(500, 50, 1_048_576, 4096, 200, 3000, 5, 200, 5, 20, 1, 100, 31_457_280L);
}
