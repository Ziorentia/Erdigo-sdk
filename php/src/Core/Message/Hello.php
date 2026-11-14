<?php

declare(strict_types=1);

namespace Erdigo\Core\Message;

/** The first message on a connection, sent by the device. */
final class Hello
{
    /** @param list<int> $versions */
    public function __construct(public readonly array $versions, public readonly string $sdk)
    {
    }
}
