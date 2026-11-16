<?php

declare(strict_types=1);

namespace Erdigo\Examples\CloudRelay;

/**
 * The body of an answer to `GET /v1/ops`, and what the relay's log says about it.
 */
final class FetchAnswer
{
    public function __construct(
        public readonly string $json,
        public readonly int $count,
        public readonly bool $reset,
        public readonly bool $missed,
    ) {
    }
}
