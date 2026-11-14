<?php

declare(strict_types=1);

namespace Erdigo;

/**
 * How long a request keeps trying while the receiver cannot be reached, for example while the computer is asleep or
 * the app is closed.
 *
 * The time allowed covers one outage: it counts from when the connection was lost, and starts again once a connection
 * is made. Time spent connected, such as waiting to stay within the receiver's limits, does not count. So this limits
 * how long a request waits for a receiver that cannot be reached, not how long it takes in all. To limit the whole
 * time, give the client a `requestTimeout`.
 */
final class RetryPolicy
{
    private function __construct(public readonly ?float $seconds, public readonly bool $retries)
    {
    }

    /** Fail at the first connection problem. */
    public static function none(): self
    {
        return new self(0.0, false);
    }

    /** Keep trying without end. Give the client a `requestTimeout` to stop it. */
    public static function forever(): self
    {
        return new self(null, true);
    }

    /** Keep trying for this many seconds, counted from when the connection was lost. */
    public static function upTo(float $seconds): self
    {
        if (!is_finite($seconds) || $seconds < 0) {
            throw new ErdigoException(ErrorKind::InvalidArgument, 'a retry time is a number of seconds, 0 or more');
        }
        return $seconds == 0.0 ? self::none() : new self($seconds, true);
    }
}
