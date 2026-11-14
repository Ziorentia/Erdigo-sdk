<?php

declare(strict_types=1);

namespace Erdigo\Core\Message;

use Erdigo\Core\Coordinate;

/** Creates or replaces a pin. */
final class PutPin implements Request
{
    public function __construct(public readonly string $id, public readonly Coordinate $at, public readonly ?string $title)
    {
    }
}
