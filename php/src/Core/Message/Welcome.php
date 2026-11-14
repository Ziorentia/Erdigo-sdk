<?php

declare(strict_types=1);

namespace Erdigo\Core\Message;

use Erdigo\Core\ItemKind;
use Erdigo\Core\Limits;

/** The receiver's answer to `hello`. */
final class Welcome implements ReceiverMessage
{
    /** @param list<ItemKind> $kinds */
    public function __construct(
        public readonly int $version,
        public readonly int $lastSeq,
        public readonly array $kinds,
        public readonly Limits $limits,
    ) {
    }
}
