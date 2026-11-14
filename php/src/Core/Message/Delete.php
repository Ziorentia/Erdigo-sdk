<?php

declare(strict_types=1);

namespace Erdigo\Core\Message;

/** Deletes one item of this device. */
final class Delete implements Request
{
    public function __construct(public readonly string $id)
    {
    }
}
