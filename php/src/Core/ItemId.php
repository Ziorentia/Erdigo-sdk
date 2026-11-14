<?php

declare(strict_types=1);

namespace Erdigo\Core;

/**
 * An item identifier: 1 to 64 ASCII letters, digits, '-' or '_'.
 */
final class ItemId
{
    private function __construct()
    {
    }

    /** Returns the id if it is valid. */
    public static function validate(string $id): string
    {
        if (preg_match('/^[A-Za-z0-9_-]{1,64}$/D', $id) !== 1) {
            throw new WireException(ErrorCode::InvalidId, "an item id is 1 to 64 ASCII letters, digits, '-' or '_'");
        }
        return $id;
    }
}
