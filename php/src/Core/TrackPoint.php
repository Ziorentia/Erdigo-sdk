<?php

declare(strict_types=1);

namespace Erdigo\Core;

/**
 * One point of a track.
 */
final class TrackPoint
{
    public readonly ?float $altitudeM;

    /**
     * @param int|null $timestampMs Milliseconds since 1970, within 0...2^53-1.
     * @param float|int|null $altitudeM Metres, within -1000...100000.
     */
    public function __construct(
        public readonly Coordinate $coordinate,
        public readonly ?int $timestampMs = null,
        float|int|null $altitudeM = null,
    ) {
        if ($timestampMs !== null && ($timestampMs < 0 || $timestampMs > IntegrationV1::MAX_SEQUENCE)) {
            throw new WireException(
                ErrorCode::InvalidMessage,
                'a track point timestamp is milliseconds since 1970, within 0...2^53-1',
            );
        }
        if ($altitudeM !== null) {
            $altitudeM = (float) $altitudeM;
            if (!is_finite($altitudeM) || $altitudeM < -1000.0 || $altitudeM > 100000.0) {
                throw new WireException(ErrorCode::InvalidMessage, 'a track point altitude is within -1000...100000 metres');
            }
        }
        $this->altitudeM = $altitudeM;
    }
}
