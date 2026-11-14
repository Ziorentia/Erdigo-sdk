package com.ziorentia.erdigo.sdk;

import java.time.Duration;
import java.util.Objects;

/**
 * How long a request keeps trying while the receiver cannot be reached, for example while the computer is asleep or the
 * app is closed.
 *
 * <p>The time allowed covers one outage: it counts from when the connection was lost, and starts again once a
 * connection is made. Time spent connected, such as waiting to stay within the receiver's limits, does not count. So
 * this limits how long a request waits for a receiver that cannot be reached, not how long it takes in all. To limit
 * the whole time, interrupt the thread that made the request, for example with {@code Future.cancel(true)} after
 * {@code Future.get(timeout)}.
 */
public sealed interface RetryPolicy {
    /** Fail at the first connection problem. */
    static RetryPolicy none() {
        return NoRetry.INSTANCE;
    }

    /** Keep trying until the thread is interrupted. */
    static RetryPolicy forever() {
        return Forever.INSTANCE;
    }

    /** Keep trying for this long, counted from when the connection was lost. The count starts again each time a connection is made. */
    static RetryPolicy upTo(Duration time) {
        return new UpTo(time);
    }

    /** See {@link RetryPolicy#none()}. */
    enum NoRetry implements RetryPolicy {
        INSTANCE
    }

    /** See {@link RetryPolicy#forever()}. */
    enum Forever implements RetryPolicy {
        INSTANCE
    }

    /** See {@link RetryPolicy#upTo}. */
    record UpTo(Duration time) implements RetryPolicy {
        public UpTo {
            Objects.requireNonNull(time, "time");
            if (time.isNegative()) {
                throw new IllegalArgumentException("the time must not be negative");
            }
        }
    }
}
