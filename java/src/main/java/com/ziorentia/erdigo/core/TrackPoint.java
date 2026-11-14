package com.ziorentia.erdigo.core;

import java.util.Objects;

/**
 * One point of a track. {@code timestampMs} (milliseconds since 1970) and {@code altitudeM} (metres) may be null.
 */
public record TrackPoint(Coordinate coordinate, Long timestampMs, Double altitudeM) {
    public TrackPoint {
        Objects.requireNonNull(coordinate, "coordinate");
        if (timestampMs != null && (timestampMs < 0 || timestampMs > IntegrationV1.MAX_SEQUENCE)) {
            throw new WireException(ErrorCode.INVALID_MESSAGE, "a track point timestamp is milliseconds since 1970, within 0...2^53-1");
        }
        if (altitudeM != null && (!Double.isFinite(altitudeM) || altitudeM < -1000.0 || altitudeM > 100_000.0)) {
            throw new WireException(ErrorCode.INVALID_MESSAGE, "a track point altitude is within -1000...100000 metres");
        }
    }

    public TrackPoint(Coordinate coordinate) {
        this(coordinate, null, null);
    }
}
