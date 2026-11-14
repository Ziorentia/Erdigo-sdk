<?php

declare(strict_types=1);

namespace Erdigo\Core\Message;

/** Finishes a track. */
final class TrackFinish implements Request
{
    public function __construct(public readonly string $id)
    {
    }
}
