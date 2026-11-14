<?php

declare(strict_types=1);

namespace Erdigo\Core;

/**
 * Why {@see StrictJson::parse()} refused its input.
 */
final class JsonParseException extends \RuntimeException
{
    public function __construct(string $reason, public readonly int $offset = -1)
    {
        parent::__construct("strict JSON: {$reason}");
    }
}
