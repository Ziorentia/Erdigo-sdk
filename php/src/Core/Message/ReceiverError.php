<?php

declare(strict_types=1);

namespace Erdigo\Core\Message;

use Erdigo\Core\ErrorCode;

/** An error answer. */
final class ReceiverError implements ReceiverMessage
{
    /**
     * @param string $code Kept as text: a device must treat a code it does not know as a permanent failure.
     * @param list<int>|null $supported
     */
    public function __construct(
        public readonly string $code,
        public readonly ?int $seq = null,
        public readonly ?int $lastSeq = null,
        public readonly ?int $retryAfterMs = null,
        public readonly ?array $supported = null,
        public readonly bool $fatal = false,
    ) {
    }

    public static function of(
        ErrorCode $code,
        ?int $seq = null,
        ?int $lastSeq = null,
        ?int $retryAfterMs = null,
        ?array $supported = null,
        bool $fatal = false,
    ): self {
        return new self($code->value, $seq, $lastSeq, $retryAfterMs, $supported, $fatal);
    }

    public function is(ErrorCode $code): bool
    {
        return $this->code === $code->value;
    }
}
