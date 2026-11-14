<?php

declare(strict_types=1);

namespace Erdigo\Core\Message;

use Erdigo\Core\TrackPoint;

/** Adds 1 to 100 points to a track, opening it if needed. */
final class TrackAppend implements Request
{
    /** @param list<TrackPoint> $points */
    public function __construct(public readonly string $id, public readonly ?string $title, public readonly array $points)
    {
    }
}
