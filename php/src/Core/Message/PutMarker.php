<?php

declare(strict_types=1);

namespace Erdigo\Core\Message;

use Erdigo\Core\Coordinate;
use Erdigo\Core\MarkerColor;

/** Creates or replaces a marker. */
final class PutMarker implements Request
{
    public function __construct(
        public readonly string $id,
        public readonly Coordinate $at,
        public readonly ?string $title,
        public readonly ?MarkerColor $color,
    ) {
    }
}
