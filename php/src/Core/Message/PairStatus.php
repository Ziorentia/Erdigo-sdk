<?php

declare(strict_types=1);

namespace Erdigo\Core\Message;

use Erdigo\Core\ItemKind;

/**
 * Where a pairing request stands. `$counter` is the highest connect counter the receiver has accepted from this
 * device, given when it is approved.
 */
final class PairStatus implements ReceiverMessage
{
    public const PENDING = 'pending';
    public const APPROVED = 'approved';
    public const DENIED = 'denied';

    /** @param list<ItemKind> $kinds */
    private function __construct(public readonly string $status, public readonly array $kinds = [], public readonly int $counter = 0)
    {
    }

    public static function pending(): self
    {
        return new self(self::PENDING);
    }

    public static function denied(): self
    {
        return new self(self::DENIED);
    }

    /** @param list<ItemKind> $kinds */
    public static function approved(array $kinds, int $counter): self
    {
        return new self(self::APPROVED, $kinds, $counter);
    }
}
