<?php

declare(strict_types=1);

namespace Erdigo\Core;

/**
 * A token bucket, used by receivers to enforce rates and by the SDK to stay below them.
 */
final class TokenBucket
{
    private float $tokens;
    private float $updatedAt;

    public function __construct(public readonly float $capacity, public readonly float $refillPerSecond, float $now)
    {
        $this->tokens = $capacity;
        $this->updatedAt = $now;
    }

    /**
     * Takes `$count` tokens if possible and returns null. Otherwise takes nothing and returns how long to wait, in
     * seconds.
     */
    public function take(float $now, int $count = 1): ?float
    {
        $elapsed = max(0.0, $now - $this->updatedAt);
        $this->tokens = min($this->capacity, $this->tokens + $elapsed * $this->refillPerSecond);
        $this->updatedAt = $now;
        if ($this->tokens >= $count) {
            $this->tokens -= $count;
            return null;
        }
        if ($this->refillPerSecond <= 0) {
            return INF;
        }
        return (min((float) $count, $this->capacity) - $this->tokens) / $this->refillPerSecond;
    }
}
