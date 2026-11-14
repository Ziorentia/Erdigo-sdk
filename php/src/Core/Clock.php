<?php

declare(strict_types=1);

namespace Erdigo\Core;

/**
 * A monotonic clock in seconds, which a change of the system time does not move.
 */
final class Clock
{
    private function __construct()
    {
    }

    public static function now(): float
    {
        return hrtime(true) / 1e9;
    }
}
