<?php

declare(strict_types=1);

namespace Erdigo\Core\Message;

/** The receiver applied a request, or had applied it already. */
final class Ack implements ReceiverMessage
{
    public function __construct(public readonly int $seq, public readonly bool $duplicate)
    {
    }
}
