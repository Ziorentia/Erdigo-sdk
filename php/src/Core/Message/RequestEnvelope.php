<?php

declare(strict_types=1);

namespace Erdigo\Core\Message;

/** A request with its sequence number. */
final class RequestEnvelope
{
    public function __construct(public readonly int $seq, public readonly Request $request)
    {
    }
}
