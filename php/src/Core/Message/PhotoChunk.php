<?php

declare(strict_types=1);

namespace Erdigo\Core\Message;

/** One piece of a photo, at most 48000 bytes. */
final class PhotoChunk implements Request
{
    public function __construct(public readonly string $id, public readonly int $index, public readonly string $data)
    {
    }
}
