<?php

declare(strict_types=1);

namespace Erdigo\Core\Message;

/** Starts a photo upload. */
final class PhotoBegin implements Request
{
    public function __construct(public readonly PhotoHeader $header)
    {
    }
}
