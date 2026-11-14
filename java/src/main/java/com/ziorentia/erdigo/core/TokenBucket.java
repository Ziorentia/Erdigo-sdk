package com.ziorentia.erdigo.core;

/** A token bucket, used by receivers to enforce rates and by the SDK to stay below them. Times are in seconds. */
public final class TokenBucket {
    private final double capacity;
    private final double refillPerSecond;
    private double tokens;
    private double updatedAt;

    public TokenBucket(int capacity, int refillPerSecond, double now) {
        this.capacity = capacity;
        this.refillPerSecond = refillPerSecond;
        this.tokens = capacity;
        this.updatedAt = now;
    }

    private void refill(double now) {
        double elapsed = Math.max(0, now - updatedAt);
        tokens = Math.min(capacity, tokens + elapsed * refillPerSecond);
        updatedAt = now;
    }

    /**
     * Takes {@code count} tokens if possible and returns 0. Otherwise takes nothing and returns how long to wait, in
     * seconds, which is always more than 0. More than the bucket holds counts as a full bucket, so it can be met.
     */
    public double take(double now, int count) {
        refill(now);
        double wanted = Math.min(count, capacity);
        if (tokens >= wanted) {
            tokens -= wanted;
            return 0;
        }
        if (refillPerSecond <= 0) {
            return Double.POSITIVE_INFINITY;
        }
        return (wanted - tokens) / refillPerSecond;
    }

    /** A monotonic clock in seconds. */
    public static double now() {
        return System.nanoTime() / 1e9;
    }
}
