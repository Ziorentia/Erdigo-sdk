<?php

declare(strict_types=1);

namespace Erdigo\Examples\CloudRelay;

/**
 * The relay answered with something that does not follow the relay API. The message says what, and never repeats
 * what the relay sent.
 */
final class RelayBroken extends \RuntimeException
{
}
