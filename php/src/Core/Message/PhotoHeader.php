<?php

declare(strict_types=1);

namespace Erdigo\Core\Message;

use Erdigo\Core\Coordinate;
use Erdigo\Core\PhotoType;

/** What `photo_begin` says about a photo. */
final class PhotoHeader
{
    public function __construct(
        public readonly string $id,
        public readonly Coordinate $at,
        public readonly ?string $title,
        public readonly PhotoType $type,
        public readonly int $size,
        public readonly string $sha256,
        public readonly int $chunks,
    ) {
    }
}
