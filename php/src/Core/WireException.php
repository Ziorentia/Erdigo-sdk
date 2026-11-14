<?php

declare(strict_types=1);

namespace Erdigo\Core;

/**
 * A wire-level rejection, carrying the error code a receiver would report.
 */
final class WireException extends \RuntimeException
{
    public function __construct(public readonly ErrorCode $errorCode, public readonly string $detail)
    {
        parent::__construct($detail);
    }
}
