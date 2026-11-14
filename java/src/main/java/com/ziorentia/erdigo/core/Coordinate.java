package com.ziorentia.erdigo.core;

/** A validated coordinate: finite, latitude within -90...90 and longitude within -180...180 (WGS 84). */
public record Coordinate(double latitude, double longitude) {
    public Coordinate {
        if (!Double.isFinite(latitude) || !Double.isFinite(longitude) || latitude < -90.0 || latitude > 90.0
                || longitude < -180.0 || longitude > 180.0) {
            throw new WireException(ErrorCode.INVALID_COORDINATE, "latitude must be within -90...90 and longitude within -180...180");
        }
    }
}
