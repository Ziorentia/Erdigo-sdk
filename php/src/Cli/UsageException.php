<?php

declare(strict_types=1);

namespace Erdigo\Cli;

/**
 * A mistake in how the tool was called. Its message never repeats what was typed, which might be a pairing code.
 */
final class UsageException extends \RuntimeException
{
}
