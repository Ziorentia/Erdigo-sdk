package com.ziorentia.erdigo.sdk;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.time.Duration;
import org.junit.jupiter.api.Test;

class RetryPolicyTest {
    @Test
    void backoffDoublesToAMinuteWithTwentyPercentEitherWay() {
        for (int attempt = 0; attempt < 12; attempt++) {
            double base = Math.min(60, Math.pow(2, Math.min(attempt, 6)));
            for (int sample = 0; sample < 200; sample++) {
                double delay = ErdigoClient.backoff(attempt);
                assertTrue(delay >= base * 0.8 && delay <= base * 1.2, "attempt " + attempt + ": " + delay);
            }
        }
    }

    /** However long the time allowed, a request with a silent receiver still fails cleanly, not with an overflow. */
    @Test
    void anyLengthOfTimeIsAccepted() throws Exception {
        try (ScriptedReceiver receiver = new ScriptedReceiver();
                TestDevice device = new TestDevice(receiver, RetryPolicy.upTo(Duration.ofSeconds(Long.MAX_VALUE)))) {
            receiver.silentConnections.set(1);
            device.client.ping();
            assertEquals(1, receiver.count("silent"));
        }
        assertThrows(IllegalArgumentException.class, () -> RetryPolicy.upTo(Duration.ofSeconds(-1)));
    }
}
