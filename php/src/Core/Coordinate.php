<?php

declare(strict_types=1);

namespace Erdigo\Core;

/**
 * A validated coordinate: finite, latitude within -90...90 and longitude within -180...180 (WGS 84).
 */
final class Coordinate
{
    public readonly float $latitude;
    public readonly float $longitude;

    public function __construct(float|int $latitude, float|int $longitude)
    {
        $latitude = (float) $latitude;
        $longitude = (float) $longitude;
        if (!is_finite($latitude) || !is_finite($longitude) || $latitude < -90.0 || $latitude > 90.0
            || $longitude < -180.0 || $longitude > 180.0) {
            throw new WireException(
                ErrorCode::InvalidCoordinate,
                'latitude must be within -90...90 and longitude within -180...180',
            );
        }
        $this->latitude = $latitude;
        $this->longitude = $longitude;
    }
}
