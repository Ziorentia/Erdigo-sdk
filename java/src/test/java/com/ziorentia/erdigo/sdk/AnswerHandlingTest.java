package com.ziorentia.erdigo.sdk;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.ziorentia.erdigo.core.ErrorCode;
import com.ziorentia.erdigo.core.Messages.ReceiverError;
import com.ziorentia.erdigo.core.Messages.ReceiverMessage;
import com.ziorentia.erdigo.core.Messages.RequestEnvelope;
import java.time.Duration;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.function.Function;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.CsvSource;
import org.junit.jupiter.params.provider.ValueSource;

/**
 * How the client takes error answers, including ones a receiver would not give by itself. Each test replaces the
 * receiver's answer to chosen requests and records the numbers it was sent.
 */
class AnswerHandlingTest {
    private ScriptedReceiver receiver;
    private TestDevice device;

    @BeforeEach
    void start() throws Exception {
        receiver = new ScriptedReceiver();
    }

    @AfterEach
    void stop() {
        if (device != null) {
            device.close();
        }
        receiver.close();
    }

    /** Answers the first {@code times} requests as told, then lets requests run. */
    private void script(Function<RequestEnvelope, ReceiverError> answer, int times, RetryPolicy retry) {
        // Retrying for a while means a wrongly classified answer would show up as a second connection.
        device = new TestDevice(receiver, retry == null ? RetryPolicy.upTo(Duration.ofSeconds(10)) : retry);
        List<Long> counted = Collections.synchronizedList(new ArrayList<>());
        receiver.override = envelope -> {
            counted.add(envelope.seq());
            return counted.size() <= times ? new ReceiverMessage.Error(answer.apply(envelope)) : null;
        };
    }

    private List<Long> seen() {
        return receiver.seen(device.fingerprint());
    }

    private long lastSeq() {
        return receiver.device(device.fingerprint()).lastSeq;
    }

    // Answers that end the connection

    @Test
    void fatalRateLimitedEndsTheConnectionWithOneError() {
        script(request -> new ReceiverError(ErrorCode.RATE_LIMITED, request.seq(), null, 50L, null, true), 1, null);
        ErdigoException error = assertThrows(ErdigoException.class, device.client::ping);
        assertEquals(ErdigoException.Kind.REJECTED, error.kind());
        assertEquals("rate_limited", error.receiverError().code());
        assertTrue(error.receiverError().fatal());
        // Sent once, never resent on the closed connection or on a new one.
        assertEquals(List.of(1L), seen());
        assertEquals(1, receiver.count("connected"));
        assertNull(device.client.limits());
        assertEquals(0, lastSeq());
    }

    /** A fatal answer is about the connection, so a number it carries for another request changes nothing. */
    @ParameterizedTest
    @CsvSource({
        "rate_limited, REJECTED, false",
        "rate_limited, REJECTED, true",
        "too_many_errors, REJECTED, false",
        "too_many_errors, REJECTED, true",
        "internal_error, REJECTED, false",
        "replaced, REPLACED, false",
        "replaced, REPLACED, true",
        "seq_conflict, SEQUENCE_CONFLICT, false",
        "stale_seq, SEQUENCE_CONFLICT, true",
        "future_code, REJECTED, true",
    })
    void fatalAnswersEndTheConnectionAsTheirCodeSays(String code, ErdigoException.Kind expected, boolean numberForAnotherRequest) {
        script(request -> new ReceiverError(code, numberForAnotherRequest ? request.seq() + 7 : request.seq(),
                numberForAnotherRequest ? 1000L : null, 50L, null, true), 1, null);
        ErdigoException error = assertThrows(ErdigoException.class, device.client::ping);
        assertEquals(expected, error.kind(), error.getMessage());
        assertEquals(List.of(1L), seen());
        assertEquals(1, receiver.count("connected"));
        assertNull(device.client.limits());
        if (expected == ErdigoException.Kind.SEQUENCE_CONFLICT) {
            // The number in an answer to another request is not taken as where the receiver stands.
            assertEquals(0L, error.lastSeq());
        }
    }

    @Test
    void pausedStillMeansReconnectAndResendWithTheSameNumber() {
        script(request -> new ReceiverError(ErrorCode.PAUSED, null, null, null, null, true), 1, null);
        device.client.ping();
        assertEquals(List.of(1L, 1L), seen());
        assertEquals(2, receiver.count("connected"));
        assertEquals(1, lastSeq());
    }

    @Test
    void pausedWithoutRetryingReportsNoAnswer() {
        script(request -> new ReceiverError(ErrorCode.PAUSED, null, null, null, null, true), 1, RetryPolicy.none());
        assertEquals(ErdigoException.Kind.NO_ANSWER, assertThrows(ErdigoException.class, device.client::ping).kind());
        assertEquals(List.of(1L), seen());
    }

    // Answers that keep the connection

    @ParameterizedTest
    @ValueSource(booleans = {true, false})
    void rateLimitedIsResentWithTheSameNumberOnTheSameConnection(boolean answerCarriesTheNumber) {
        // A receiver leaves out the number when it limits a message before reading it.
        script(request -> new ReceiverError(ErrorCode.RATE_LIMITED, answerCarriesTheNumber ? request.seq() : null, null, 20L, null, false), 2, null);
        device.client.ping();
        device.client.ping();
        assertEquals(List.of(1L, 1L, 1L, 2L), seen());
        assertEquals(1, receiver.count("connected"));
        assertEquals(2, lastSeq());
    }

    @Test
    void receiverThatNeverStopsLimitingIsGivenUpOn() {
        script(request -> new ReceiverError(ErrorCode.RATE_LIMITED, request.seq(), null, 1L, null, false), Integer.MAX_VALUE, null);
        ErdigoException error = assertThrows(ErdigoException.class, device.client::ping);
        assertEquals(ErdigoException.Kind.REJECTED, error.kind());
        assertEquals("rate_limited", error.receiverError().code());
        // The first answer and twenty more, all for the same number.
        assertEquals(Collections.nCopies(21, 1L), seen());
        assertEquals(1, receiver.count("connected"));
    }

    @ParameterizedTest
    @ValueSource(strings = {"not_found", "rate_limited", "stale_seq"})
    void answerForAnotherNumberIsAProtocolViolationAndChangesNoNumbers(String code) {
        script(request -> new ReceiverError(code, request.seq() + 5, 1000L, 20L, null, false), 1, null);
        assertEquals(ErdigoException.Kind.PROTOCOL_VIOLATION, assertThrows(ErdigoException.class, device.client::ping).kind());
        assertNull(device.client.limits());
        // The next request starts from what the receiver says on a new connection, not from the stray number.
        device.client.ping();
        assertEquals(List.of(1L, 1L), seen());
        assertEquals(1, lastSeq());
    }

    @Test
    void staleNumberForThisRequestMovesTheNumberOn() {
        script(request -> new ReceiverError(ErrorCode.STALE_SEQ, request.seq(), 10L, null, null, false), 1, null);
        ErdigoException error = assertThrows(ErdigoException.class, device.client::ping);
        assertEquals(ErdigoException.Kind.SEQUENCE_CONFLICT, error.kind());
        assertEquals(10L, error.lastSeq());
        device.client.ping();
        assertEquals(List.of(1L, 11L), seen());
        assertEquals(1, receiver.count("connected"));
        assertEquals(11, lastSeq());
    }

    @ParameterizedTest
    @ValueSource(booleans = {true, false})
    void rejectionKeepsTheConnectionAndUsesUpTheNumberOnlyWhenItSaysWhichRequest(boolean answerCarriesTheNumber) {
        script(request -> new ReceiverError(ErrorCode.NOT_FOUND, answerCarriesTheNumber ? request.seq() : null, null, null, null, false), 1, null);
        ErdigoException error = assertThrows(ErdigoException.class, () -> device.client.move("missing", ClientTest.PALACE));
        assertEquals(ErdigoException.Kind.REJECTED, error.kind());
        assertEquals("not_found", error.receiverError().code());
        assertNotNull(device.client.limits());
        device.client.ping();
        assertEquals(answerCarriesTheNumber ? List.of(1L, 2L) : List.of(1L, 1L), seen());
        assertEquals(1, receiver.count("connected"));
    }

    @Test
    void versionUnsupportedInPlaceOfWelcomeIsReported() {
        device = new TestDevice(receiver, RetryPolicy.upTo(Duration.ofSeconds(10)));
        receiver.helloAnswer = new ReceiverMessage.Error(new ReceiverError(ErrorCode.VERSION_UNSUPPORTED, null, null, null, List.of(2), true));
        ErdigoException error = assertThrows(ErdigoException.class, device.client::ping);
        assertEquals(ErdigoException.Kind.VERSION_UNSUPPORTED, error.kind());
        assertEquals(List.of(2), error.supported());
        // A definite answer, so it is not retried.
        assertEquals(1, receiver.count("hello answered"));
    }
}
