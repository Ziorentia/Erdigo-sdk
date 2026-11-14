<?php

declare(strict_types=1);

namespace Erdigo\Core;

/**
 * A decoded JSON object. PHP arrays cannot tell an empty object from an empty array, so objects are kept apart.
 */
final class JsonObject
{
    /** @param array<string|int, mixed> $fields Keys that look like integers become integers, as PHP does with arrays. */
    public function __construct(public readonly array $fields)
    {
    }
}
