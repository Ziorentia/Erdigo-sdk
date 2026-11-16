<?php

declare(strict_types=1);

namespace Erdigo\Examples\CloudRelay;

/**
 * Ends `gateway run` with an exit code, and a line saying why unless the code is 0.
 */
final class GatewayExit extends \RuntimeException
{
    public function __construct(public readonly int $exitCode, string $message = '')
    {
        parent::__construct($message);
    }
}
