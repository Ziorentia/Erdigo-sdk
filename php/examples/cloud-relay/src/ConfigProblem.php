<?php

declare(strict_types=1);

namespace Erdigo\Examples\CloudRelay;

/**
 * A setting of the relay is missing or wrong. The message says which, and never holds a token.
 */
final class ConfigProblem extends \RuntimeException
{
}
