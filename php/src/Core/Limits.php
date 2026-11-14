<?php

declare(strict_types=1);

namespace Erdigo\Core;

/**
 * Limits a receiver enforces per device. The receiver announces them in `welcome`.
 */
final class Limits
{
    public function __construct(
        public readonly int $items,
        public readonly int $photos,
        public readonly int $photoBytes,
        public readonly int $noteBytes,
        public readonly int $titleScalars,
        public readonly int $trackPoints,
        public readonly int $openTracks,
        public readonly int $tracks,
        public readonly int $messagesPerSecond,
        public readonly int $messageBurst,
        public readonly int $pointsPerSecond,
        public readonly int $pointBurst,
        public readonly int $diskBytes,
    ) {
    }

    /** The values of spec section 11. */
    public static function v1(): self
    {
        return new self(500, 50, 1048576, 4096, 200, 3000, 5, 200, 5, 20, 1, 100, 31457280);
    }

    public function equals(self $other): bool
    {
        return $this == $other;
    }
}
