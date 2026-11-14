<?php

declare(strict_types=1);

namespace Erdigo\Core\Message;

use Erdigo\Core\ItemKind;

/** The payload of the third pairing message. */
final class PairRequest
{
    /**
     * @param list<int> $versions
     * @param list<ItemKind> $kinds
     */
    public function __construct(
        public readonly array $versions,
        public readonly string $sdk,
        public readonly string $name,
        public readonly array $kinds,
    ) {
    }
}
