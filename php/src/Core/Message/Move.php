<?php

declare(strict_types=1);

namespace Erdigo\Core\Message;

use Erdigo\Core\Coordinate;

/** Moves a pin, marker, note or photo. */
final class Move implements Request
{
    public function __construct(public readonly string $id, public readonly Coordinate $to)
    {
    }
}
