<?php

declare(strict_types=1);

namespace Erdigo\Examples\CloudRelay;

/**
 * The relay could not be reached, or the connection failed before its whole answer came.
 */
final class RelayUnreachable extends \RuntimeException
{
    /** @param bool $certificate Whether the relay's certificate or name could not be verified. */
    public function __construct(public readonly bool $certificate)
    {
        parent::__construct($certificate ? "the relay's certificate could not be verified" : 'the relay could not be reached');
    }
}
