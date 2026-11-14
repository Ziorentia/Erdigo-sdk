package com.ziorentia.erdigo.sdk;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.ziorentia.erdigo.core.Coordinate;
import com.ziorentia.erdigo.core.Hex;
import com.ziorentia.erdigo.core.IntegrationV1;
import com.ziorentia.erdigo.core.ItemKind;
import com.ziorentia.erdigo.core.Limits;
import com.ziorentia.erdigo.core.MarkerColor;
import com.ziorentia.erdigo.core.Request;
import com.ziorentia.erdigo.core.TrackPoint;
import java.time.Duration;
import java.util.ArrayList;
import java.util.List;
import java.util.Random;
import java.util.concurrent.atomic.AtomicReference;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;

class ClientTest {
    static final Coordinate PALACE = new Coordinate(37.5796, 126.977);

    private ScriptedReceiver receiver;

    @BeforeEach
    void start() throws Exception {
        receiver = new ScriptedReceiver();
    }

    @AfterEach
    void stop() {
        receiver.close();
    }

    static byte[] fakePng(int size) {
        byte[] data = new byte[size];
        new Random(7).nextBytes(data);
        byte[] signature = {(byte) 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A};
        System.arraycopy(signature, 0, data, 0, signature.length);
        return data;
    }

    @Test
    void writesEveryKindOfItem() {
        try (TestDevice device = new TestDevice(receiver, RetryPolicy.none())) {
            ErdigoClient client = device.client;
            client.ping();
            client.putPin("gate", PALACE, "Palace gate");
            client.putMarker("tower", new Coordinate(48.8584, 2.2945), "Tower", MarkerColor.BLUE);
            client.putNote("meet", PALACE, "Meet at the gate\nat noon");
            byte[] photo = fakePng(70_000);
            client.putPhoto("view", PALACE, photo, "View");
            client.appendTrack("walk", List.of(new TrackPoint(PALACE, 1_700_000_000_000L, 38.5)), "Walk");
            client.finishTrack("walk");
            client.move("gate", new Coordinate(37.58, 126.98));
            client.delete("meet");
            assertNotNull(client.limits());
            assertEquals(List.of(ItemKind.values()), client.allowedKinds());

            ScriptedReceiver.Device stored = receiver.device(device.fingerprint());
            assertTrue(stored.items.containsKey("gate"));
            assertTrue(stored.items.get("tower") instanceof Request.PutMarker marker && marker.color() == MarkerColor.BLUE);
            assertFalse(stored.items.containsKey("meet"));
            assertTrue(stored.items.containsKey("view"));
            assertTrue(stored.items.containsKey("walk#finished"));
            assertTrue(stored.items.containsKey("gate#moved"));
            // One connection for everything, and every number used once, in order: the photo took four.
            assertEquals(1, receiver.count("connected"));
            List<Long> expected = new ArrayList<>();
            for (long seq = 1; seq <= 12; seq++) {
                expected.add(seq);
            }
            assertEquals(expected, receiver.seen(device.fingerprint()));

            client.deleteAll();
            assertTrue(receiver.device(device.fingerprint()).items.isEmpty());
        }
    }

    @Test
    void photoGoesInChunksOfAtMost48000Bytes() {
        AtomicReference<Request.PhotoBegin> begin = new AtomicReference<>();
        List<Integer> chunkSizes = new ArrayList<>();
        receiver.override = envelope -> {
            if (envelope.request() instanceof Request.PhotoBegin header) {
                begin.set(header);
            } else if (envelope.request() instanceof Request.PhotoChunk chunk) {
                chunkSizes.add(chunk.data().length);
            }
            return null;
        };
        try (TestDevice device = new TestDevice(receiver, RetryPolicy.none())) {
            byte[] photo = fakePng(100_001);
            device.client.putPhoto("view", PALACE, photo);
            assertEquals(List.of(48_000, 48_000, 4_001), chunkSizes);
            assertEquals(3, begin.get().header().chunks());
            assertEquals(100_001, begin.get().header().size());
            assertEquals(Hex.encode(IntegrationV1.sha256(photo)), begin.get().header().sha256());
        }
    }

    @Test
    void longTracksGoInBatchesWithTheTitleOnlyOnTheFirst() {
        List<Request.TrackAppend> appends = new ArrayList<>();
        receiver.override = envelope -> {
            if (envelope.request() instanceof Request.TrackAppend append) {
                appends.add(append);
            }
            return null;
        };
        // A burst large enough that pacing does not slow the test down.
        receiver.limits = new Limits(500, 50, 1_048_576, 4096, 200, 3000, 5, 200, 50, 50, 1000, 1000, 31_457_280L);
        try (TestDevice device = new TestDevice(receiver, RetryPolicy.none())) {
            List<TrackPoint> points = new ArrayList<>();
            for (int index = 0; index < 250; index++) {
                points.add(new TrackPoint(new Coordinate(37.5 + index * 1e-4, 127.0)));
            }
            device.client.appendTrack("walk", points, "Walk");
            assertEquals(3, appends.size());
            assertEquals(List.of(100, 100, 50), appends.stream().map(append -> append.points().size()).toList());
            assertEquals("Walk", appends.get(0).title());
            assertNull(appends.get(1).title());
            assertEquals(250, receiver.device(device.fingerprint()).trackPoints.get("walk"));
        }
    }

    @Test
    void kindsThePersonDidNotAllowAreRefusedWithoutSending() {
        try (TestDevice device = new TestDevice(receiver, RetryPolicy.none(), List.of(ItemKind.PIN))) {
            ErdigoException error = assertThrows(ErdigoException.class, () -> device.client.putNote("n", PALACE, "text"));
            assertEquals(ErdigoException.Kind.NOT_PERMITTED, error.kind());
            assertEquals(ItemKind.NOTE, error.itemKind());
            assertEquals(ErdigoException.Kind.NOT_PERMITTED,
                    assertThrows(ErdigoException.class, () -> device.client.putPhoto("p", PALACE, fakePng(10))).kind());
            assertTrue(receiver.seen(device.fingerprint()).isEmpty());
        }
    }

    @Test
    void badArgumentsAreRefusedBeforeConnecting() {
        try (TestDevice device = new TestDevice(receiver, RetryPolicy.none())) {
            ErdigoClient client = device.client;
            assertEquals(ErdigoException.Kind.INVALID_ARGUMENT, assertThrows(ErdigoException.class, () -> client.putPin("a b", PALACE)).kind());
            assertEquals(ErdigoException.Kind.INVALID_ARGUMENT, assertThrows(ErdigoException.class, () -> client.putPin("a", PALACE, "")).kind());
            assertEquals(ErdigoException.Kind.INVALID_ARGUMENT, assertThrows(ErdigoException.class, () -> client.putNote("a", PALACE, "‮")).kind());
            assertEquals(ErdigoException.Kind.INVALID_ARGUMENT,
                    assertThrows(ErdigoException.class, () -> client.putPhoto("a", PALACE, "GIF89a".getBytes())).kind());
            assertEquals(ErdigoException.Kind.INVALID_ARGUMENT,
                    assertThrows(ErdigoException.class, () -> client.putPhoto("a", PALACE, fakePng(1_048_577))).kind());
            assertEquals(ErdigoException.Kind.INVALID_ARGUMENT, assertThrows(ErdigoException.class, () -> client.appendTrack("a", List.of())).kind());
            assertEquals(0, receiver.count("connected"));
        }
    }

    @Test
    void lostAnswerIsResentWithTheSameNumberAndAppliedOnce() {
        try (TestDevice device = new TestDevice(receiver, RetryPolicy.upTo(Duration.ofSeconds(10)))) {
            receiver.answersToLose.set(1);
            device.client.putPin("gate", PALACE, "Palace gate");
            assertEquals(List.of(1L, 1L), receiver.seen(device.fingerprint()));
            assertEquals(1, receiver.events().stream().filter(event -> event.startsWith("applied 1 ")).count());
            assertEquals(2, receiver.count("connected"));
            device.client.ping();
            assertEquals(List.of(1L, 1L, 2L), receiver.seen(device.fingerprint()));
        }
    }

    @Test
    void silentReceiverIsRetriedUntilItAnswers() {
        try (TestDevice device = new TestDevice(receiver, RetryPolicy.upTo(Duration.ofSeconds(20)))) {
            receiver.silentConnections.set(2);
            long start = System.nanoTime();
            device.client.ping();
            double seconds = (System.nanoTime() - start) / 1e9;
            // Waits of about 1 and 2 seconds, with 20 percent either way.
            assertTrue(seconds > 2.0 && seconds < 6.0, "took " + seconds);
            assertEquals(2, receiver.count("silent"));
            assertEquals(List.of(1L), receiver.seen(device.fingerprint()));
        }
    }

    @Test
    void withoutRetryingASilentReceiverIsNoAnswer() {
        try (TestDevice device = new TestDevice(receiver, RetryPolicy.none())) {
            receiver.silentConnections.set(1);
            assertEquals(ErdigoException.Kind.NO_ANSWER, assertThrows(ErdigoException.class, device.client::ping).kind());
            assertEquals(1, receiver.count("silent"));
        }
    }

    @Test
    void retryingStopsWhenTheTimeAllowedRunsOut() {
        try (TestDevice device = new TestDevice(receiver, RetryPolicy.upTo(Duration.ofMillis(1500)))) {
            receiver.silentConnections.set(100);
            long start = System.nanoTime();
            assertEquals(ErdigoException.Kind.NO_ANSWER, assertThrows(ErdigoException.class, device.client::ping).kind());
            double seconds = (System.nanoTime() - start) / 1e9;
            assertTrue(seconds >= 1.4 && seconds < 4.0, "took " + seconds);
        }
    }

    @Test
    void eachConnectionUsesAGreaterCounterSavedFirst() {
        try (TestDevice device = new TestDevice(receiver, RetryPolicy.none())) {
            device.client.ping();
            long first = receiver.device(device.fingerprint()).counter;
            device.client.close();
            device.client.ping();
            long second = receiver.device(device.fingerprint()).counter;
            assertTrue(second > first);
            // The store already holds it, so the next one is greater still.
            assertTrue(device.store.nextConnectCounter(receiver.fingerprint, 0) > second);
        }
    }

    @Test
    void anInterruptedRequestStopsAndKeepsTheFlag() throws Exception {
        try (TestDevice device = new TestDevice(receiver, RetryPolicy.forever())) {
            receiver.silentConnections.set(1000);
            AtomicReference<Throwable> thrown = new AtomicReference<>();
            AtomicReference<Boolean> flag = new AtomicReference<>();
            Thread worker = new Thread(() -> {
                try {
                    device.client.ping();
                } catch (Throwable error) {
                    thrown.set(error);
                    flag.set(Thread.currentThread().isInterrupted());
                }
            });
            worker.start();
            Thread.sleep(500);
            worker.interrupt();
            worker.join(10_000);
            assertFalse(worker.isAlive());
            assertTrue(thrown.get() instanceof ErdigoException error && error.kind() == ErdigoException.Kind.INTERRUPTED, String.valueOf(thrown.get()));
            assertTrue(flag.get());
        }
    }

    @Test
    void addressFoundIsRemembered() {
        try (TestDevice device = new TestDevice(receiver, RetryPolicy.none())) {
            // As if the receiver had moved since this device last reached it.
            ReceiverPairing moved = device.pairing.withAddress("127.0.0.2", 1);
            device.store.save(moved);
            try (ErdigoClient client = new ErdigoClient(device.identity, moved, device.store, receiver.target(), RetryPolicy.none())) {
                client.ping();
            }
            assertEquals("127.0.0.1", device.store.pairings().get(0).lastHost());
            assertEquals(receiver.port(), (int) device.store.pairings().get(0).lastPort());
        }
    }
}
