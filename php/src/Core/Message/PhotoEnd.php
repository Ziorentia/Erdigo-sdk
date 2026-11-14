<?php

declare(strict_types=1);

namespace Erdigo\Core\Message;

/** Ends a photo upload. */
final class PhotoEnd implements Request
{
    public function __construct(public readonly string $id)
    {
    }
}
