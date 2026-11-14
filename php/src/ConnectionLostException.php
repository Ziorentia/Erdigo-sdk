<?php

declare(strict_types=1);

namespace Erdigo;

/**
 * A problem with the connection, as opposed to an answer from the receiver. The client retries after it.
 *
 * @internal
 */
final class ConnectionLostException extends \RuntimeException
{
}
