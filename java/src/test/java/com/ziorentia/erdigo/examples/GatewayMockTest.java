package com.ziorentia.erdigo.examples;

import static com.ziorentia.erdigo.examples.CloudRelayExample.GATEWAY;
import static com.ziorentia.erdigo.examples.CloudRelayExample.PNG_1X1;
import static com.ziorentia.erdigo.examples.CloudRelayExample.discard;
import static com.ziorentia.erdigo.examples.CloudRelayExample.fetch;
import static com.ziorentia.erdigo.examples.CloudRelayExample.gateway;
import static com.ziorentia.erdigo.examples.CloudRelayExample.launch;
import static com.ziorentia.erdigo.examples.CloudRelayExample.post;
import static com.ziorentia.erdigo.examples.CloudRelayExample.relayEnvironment;
import static com.ziorentia.erdigo.examples.CloudRelayExample.token;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;
import static org.junit.jupiter.api.Assumptions.assumeTrue;

import com.ziorentia.erdigo.core.JsonValue;
import com.ziorentia.erdigo.core.StrictJson;
import com.ziorentia.erdigo.examples.CloudRelayExample.InProcessRelay;
import com.ziorentia.erdigo.examples.CloudRelayExample.Mock;
import com.ziorentia.erdigo.examples.CloudRelayExample.Result;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.BindException;
import java.net.InetAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Map;
import java.util.concurrent.TimeUnit;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

/**
 * The example end to end: devices post to the relay, and the gateway, paired with a mock receiver from another SDK
 * through the code file it writes, fetches and writes to it. {@code ERDIGO_MOCK} names the mock, as for
 * {@code MockInteropTest}; without it these tests are skipped, unless {@code ERDIGO_REQUIRE_MOCK=1}.
 */
class GatewayMockTest {
    @TempDir
    Path work;

    private final String source = token();
    private final String gatewayToken = token();
    private final List<AutoCloseable> running = new ArrayList<>();

    @BeforeEach
    void needsTheMock() {
        assumeTrue(Mock.available(), "ERDIGO_MOCK is not set");
    }

    @AfterEach
    void stop() throws Exception {
        for (AutoCloseable each : running) {
            each.close();
        }
    }

    private Mock mock(String name) throws Exception {
        Mock mock = Mock.start(work.resolve(name));
        running.add(mock);
        return mock;
    }

    private InProcessRelay relay(int port) throws Exception {
        for (int attempt = 0; ; attempt++) {
            try {
                InProcessRelay relay = InProcessRelay.start(List.of("--listen", "127.0.0.1:" + port),
                        relayEnvironment(source, gatewayToken), System::currentTimeMillis, 10_000, 64L * 1024 * 1024, discard());
                running.add(relay);
                return relay;
            } catch (BindException busy) {
                // A relay started again on its old port, which may take a moment to be free.
                if (attempt > 50) {
                    throw busy;
                }
                Thread.sleep(100);
            }
        }
    }

    private Result pair(Mock mock, Path state, String kinds) throws Exception {
        return gateway(work, Map.of(), "pair", "--name", "Cloud gateway", "--kinds", kinds, "--host", "127.0.0.1",
                "--port", String.valueOf(mock.port), "--code-file", mock.codeFile.toString(), "--state-dir", state.toString());
    }

    private Result run(Mock mock, Path state, String relay, String token) throws Exception {
        return run(mock.port, state, relay, token);
    }

    private Result run(int port, Path state, String relay, String token) throws Exception {
        return gateway(work, Map.of("ERDIGO_RELAY_TOKEN", token), "run", "--relay", relay, "--host", "127.0.0.1",
                "--port", String.valueOf(port), "--state-dir", state.toString(), "--once");
    }

    @Test
    void deliversEveryKindOfOperationOnceAndSkipsWhatTheAppRefuses() throws Exception {
        Mock mock = mock("mock");
        InProcessRelay relay = relay(0);
        Path state = work.resolve("gateway");
        Result paired = pair(mock, state, "pin,marker,note,photo,track");
        assertEquals(0, paired.status(), paired.output());
        assertTrue(paired.output().contains("Allowed: pin, marker, note, photo, track."), paired.output());
        // A gateway's profile holds one pairing, and the receiver is not asked for a second.
        mock.newCode();
        Result again = pair(mock, state, "pin");
        assertEquals(1, again.status(), again.output());
        assertTrue(again.output().contains("already holds a pairing"), again.output());

        String ops = String.join(",",
                "{\"op\":\"pin\",\"id\":\"drone-1\",\"lat\":37.5663,\"lon\":126.9779,\"title\":\"Drone 1\"}",
                "{\"op\":\"marker\",\"id\":\"gate\",\"lat\":37.5,\"lon\":127,\"title\":\"Gate\",\"color\":\"blue\"}",
                "{\"op\":\"note\",\"id\":\"memo\",\"lat\":0,\"lon\":0,\"text\":\"line one\\nline\\ttwo\"}",
                "{\"op\":\"photo\",\"id\":\"view\",\"lat\":1.5,\"lon\":2.5,\"type\":\"image/png\",\"data\":\"" + PNG_1X1 + "\",\"title\":\"View\"}",
                "{\"op\":\"track\",\"id\":\"flight-7\",\"title\":\"Flight 7\",\"points\":[{\"lat\":37.5663,\"lon\":126.9779,\"ts\":1700000000000,\"alt\":120},"
                        + "{\"lat\":37.567,\"lon\":126.98,\"ts\":1700000001000}]}",
                "{\"op\":\"move\",\"id\":\"drone-1\",\"lat\":37.57,\"lon\":126.99}",
                "{\"op\":\"pin\",\"id\":\"passing\",\"lat\":0,\"lon\":0}",
                "{\"op\":\"delete\",\"id\":\"passing\"}",
                "{\"op\":\"finish_track\",\"id\":\"flight-7\"}");
        assertEquals(200, post(relay.url, source, "{\"ops\":[" + ops + "]}").status());
        Result result = run(mock, state, relay.url, gatewayToken);
        assertEquals(0, result.status(), result.output());
        assertEquals("Done with 9 operations, up to 9", result.output().strip());
        // The place is saved as each operation is finished.
        JsonValue.Object place = (JsonValue.Object) StrictJson.parse(Files.readAllBytes(state.resolve("relay-state.json")));
        assertEquals(9L, ((JsonValue.Integer) place.fields().get("seq")).value());
        assertEquals(relay.url, ((JsonValue.Text) place.fields().get("relay")).value());
        List<String> items = mock.items();
        assertEquals(List.of(
                "marker gate at 37.5,127.0 title=Gate points=0",
                "note memo at 0.0,0.0 title=- points=0",
                "photo view at 1.5,2.5 title=View points=0",
                "pin drone-1 at 37.57,126.99 title=Drone 1 points=0",
                "track flight-7 at -,- title=Flight 7 points=2"), items.stream().sorted().toList());

        // Running again applies nothing twice, and the relay has forgotten what was done.
        int requests = mock.requests();
        result = run(mock, state, relay.url, gatewayToken);
        assertEquals(0, result.status(), result.output());
        assertEquals("", result.output());
        assertEquals(requests, mock.requests());
        assertEquals(items, mock.items());
        assertTrue(fetch(relay.url, gatewayToken, "").ops().isEmpty());

        // The app refuses points for a finished track: printed, skipped, and the next one goes through.
        post(relay.url, source, "{\"ops\":[{\"op\":\"track\",\"id\":\"flight-7\",\"points\":[{\"lat\":1,\"lon\":1}]},"
                + "{\"op\":\"pin\",\"id\":\"after-refusal\",\"lat\":1,\"lon\":1}]}");
        result = run(mock, state, relay.url, gatewayToken);
        assertEquals(0, result.status(), result.output());
        assertEquals(List.of("Refused operation 10 (track): invalid_state", "Done with 2 operations, up to 11 (1 refused)"),
                result.output().lines().toList());
        assertTrue(mock.items().contains("pin after-refusal at 1.0,1.0 title=- points=0"));
        assertTrue(mock.items().contains("track flight-7 at -,- title=Flight 7 points=2"));

        // The relay starts again at the same address with a new numbering.
        int port = relay.port;
        relay.close();
        InProcessRelay restarted = relay(port);
        post(restarted.url, source, "{\"ops\":[{\"op\":\"pin\",\"id\":\"after-restart\",\"lat\":2,\"lon\":2}]}");
        result = run(mock, state, restarted.url, gatewayToken);
        assertEquals(0, result.status(), result.output());
        assertEquals(List.of("The relay started a new numbering; starting from its oldest operation.", "Done with 1 operation, up to 1"),
                result.output().lines().toList());
        assertTrue(mock.items().contains("pin after-restart at 2.0,2.0 title=- points=0"));

        // A wrong token ends the gateway, and the operations wait.
        post(restarted.url, source, "{\"ops\":[{\"op\":\"pin\",\"id\":\"waiting\",\"lat\":3,\"lon\":3}]}");
        result = run(mock, state, restarted.url, token());
        assertEquals(2, result.status(), result.output());
        assertFalse(mock.items().stream().anyMatch(line -> line.startsWith("pin waiting ")));
        assertEquals(1, fetch(restarted.url, gatewayToken, "").ops().size());
    }

    @Test
    void aGatewayAllowedSomeKindsRefusesTheOthersAndGoesOn() throws Exception {
        Mock mock = mock("mock");
        InProcessRelay relay = relay(0);
        Path state = work.resolve("pins-only");
        Result paired = pair(mock, state, "pin");
        assertEquals(0, paired.status(), paired.output());
        post(relay.url, source, "{\"ops\":["
                + "{\"op\":\"marker\",\"id\":\"m1\",\"lat\":1,\"lon\":1},"
                + "{\"op\":\"pin\",\"id\":\"p1\",\"lat\":1,\"lon\":1},"
                + "{\"op\":\"note\",\"id\":\"n1\",\"lat\":1,\"lon\":1,\"text\":\"hello\"},"
                + "{\"op\":\"track\",\"id\":\"t1\",\"points\":[{\"lat\":1,\"lon\":1}]},"
                + "{\"op\":\"pin\",\"id\":\"p2\",\"lat\":2,\"lon\":2}]}");
        Result result = run(mock, state, relay.url, gatewayToken);
        assertEquals(0, result.status(), result.output());
        assertEquals(List.of(
                "Refused operation 1 (marker): not_permitted",
                "Refused operation 3 (note): not_permitted",
                "Refused operation 4 (track): not_permitted",
                "Done with 5 operations, up to 5 (3 refused)"), result.output().lines().toList());
        assertEquals(List.of("pin p1 at 1.0,1.0 title=- points=0", "pin p2 at 2.0,2.0 title=- points=0"),
                mock.items().stream().sorted().toList());
        assertTrue(fetch(relay.url, gatewayToken, "").ops().isEmpty());
    }

    @Test
    void anAnswerLostOnTheWayIsSentAgainSoTheAppAppliesItOnce() throws Exception {
        Mock mock = mock("mock");
        InProcessRelay relay = relay(0);
        Path state = work.resolve("gateway");
        Result paired = pair(mock, state, "pin,track");
        assertEquals(0, paired.status(), paired.output());
        AnswerLosingProxy proxy = new AnswerLosingProxy(mock.port, Duration.ofSeconds(3));
        running.add(proxy);
        assertEquals(200, post(relay.url, source, "{\"ops\":["
                + "{\"op\":\"track\",\"id\":\"walk\",\"points\":[{\"lat\":1,\"lon\":1,\"ts\":1700000000000},"
                + "{\"lat\":1.001,\"lon\":1.001,\"ts\":1700000001000}]},"
                + "{\"op\":\"pin\",\"id\":\"p1\",\"lat\":2,\"lon\":2}]}").status());

        Result result = run(proxy.port(), state, relay.url, gatewayToken);
        assertEquals(0, result.status(), result.output());
        assertTrue(proxy.lostAnAnswer(), "the proxy never lost an answer");
        // The app applied the track's points before its answer was lost. The SDK sent the same request again with the
        // same sequence number, which the app recognised and did not apply a second time.
        assertEquals(List.of("pin p1 at 2.0,2.0 title=- points=0", "track walk at -,- title=- points=2"),
                mock.items().stream().sorted().toList());
        assertEquals(List.of("No answer from the Erdigo app; trying again.", "Done with 2 operations, up to 2"),
                result.output().lines().toList());
        assertTrue(fetch(relay.url, gatewayToken, "").ops().isEmpty());
    }

    /**
     * With --once the gateway gives up on an operation about 30 seconds after the first sign of trouble with it, however
     * many connections the app takes: the SDK starts its own count again at each new connection, so it would keep
     * trying. Two gateways at once: one whose every answer is lost on the way, and one whose app takes the connection
     * and never answers, which shows trouble only once the SDK stops waiting for the answer, after 15 seconds.
     */
    @Test
    void givesUpWithOnceWhenTheAppTakesEveryConnectionButNoAnswerComesBack() throws Exception {
        record Run(AnswerLosingProxy.Mode mode, Mock mock, InProcessRelay relay, Path state, AnswerLosingProxy proxy, Path output) {
        }
        List<Run> runs = new ArrayList<>();
        for (AnswerLosingProxy.Mode mode : List.of(AnswerLosingProxy.Mode.LOSE_EVERY, AnswerLosingProxy.Mode.HOLD_EVERY)) {
            String name = mode.name().toLowerCase(java.util.Locale.ROOT);
            Mock mock = mock("mock-" + name);
            InProcessRelay relay = relay(0);
            Path state = work.resolve("gateway-" + name);
            Result paired = pair(mock, state, "pin,track");
            assertEquals(0, paired.status(), paired.output());
            AnswerLosingProxy proxy = new AnswerLosingProxy(mock.port, mode);
            running.add(proxy);
            assertEquals(200, post(relay.url, source, "{\"ops\":[{\"op\":\"track\",\"id\":\"walk\",\"points\":["
                    + "{\"lat\":1,\"lon\":1},{\"lat\":1.5,\"lon\":1.5}]}]}").status());
            runs.add(new Run(mode, mock, relay, state, proxy, Files.createTempFile(work, "output", ".txt")));
        }
        long started = System.nanoTime();
        List<Process> processes = new ArrayList<>();
        running.add(() -> {
            for (Process process : processes) {
                if (process.isAlive()) {
                    process.destroyForcibly();
                }
            }
        });
        for (Run run : runs) {
            processes.add(launch(GATEWAY, List.of("run", "--relay", run.relay().url, "--host", "127.0.0.1",
                    "--port", String.valueOf(run.proxy().port()), "--state-dir", run.state().toString(), "--once"),
                    Map.of("ERDIGO_RELAY_TOKEN", gatewayToken), run.output()));
        }
        for (int index = 0; index < runs.size(); index++) {
            Run run = runs.get(index);
            Process process = processes.get(index);
            boolean ended = process.waitFor(100, TimeUnit.SECONDS);
            long seconds = (System.nanoTime() - started) / 1_000_000_000L;
            if (!ended) {
                process.destroyForcibly();
            }
            String output = Files.readString(run.output(), StandardCharsets.UTF_8);
            assertTrue(ended, run.mode() + ": it did not give up: " + output);
            assertEquals(3, process.exitValue(), run.mode() + ": " + output);
            assertEquals(List.of("No answer from the Erdigo app; trying again.",
                    "The Erdigo app could not be reached. The operations stay on the relay."), output.lines().toList(), run.mode().name());
            if (run.mode() == AnswerLosingProxy.Mode.LOSE_EVERY) {
                assertTrue(seconds >= 25 && seconds < 55, "lost answers: gave up after " + seconds + " seconds");
            } else {
                assertTrue(seconds >= 40 && seconds < 75, "no answers: gave up after " + seconds + " seconds");
            }
            // The SDK connected again and again, and sent the same request each time with the same sequence number, so
            // the app applied its points once. The gateway finished nothing, so the operation waits on the relay.
            assertTrue(run.proxy().answersHeldBack() >= 2, run.mode() + ": " + run.proxy().answersHeldBack() + " answers held back");
            assertEquals(List.of("track walk at -,- title=- points=2"), run.mock().items(), run.mode().name());
            assertEquals(1, fetch(run.relay().url, gatewayToken, "").ops().size(), run.mode().name());
            JsonValue.Object place = (JsonValue.Object) StrictJson.parse(Files.readAllBytes(run.state().resolve("relay-state.json")));
            assertEquals(0L, ((JsonValue.Integer) place.fields().get("seq")).value(), run.mode().name());
        }
    }

    /**
     * Stands between the gateway and the receiver, and keeps back the receiver's answer to the first request on a
     * connection. {@link Mode#LOSE_ONCE} does so once and cuts both connections before the answer reaches the gateway, as
     * when the network drops at the wrong moment, and then turns connections away for a while, so the SDK waits before
     * it sends the request again. {@link Mode#LOSE_EVERY} cuts every connection so, and {@link Mode#HOLD_EVERY} keeps
     * every connection open and the answer to itself, as an app that never answers.
     */
    private static final class AnswerLosingProxy implements AutoCloseable {
        enum Mode {
            LOSE_ONCE,
            LOSE_EVERY,
            HOLD_EVERY
        }

        private final ServerSocket listener = new ServerSocket(0, 50, InetAddress.getLoopbackAddress());
        private final int target;
        private final Mode mode;
        private final long downtimeNanos;
        private final List<Socket> sockets = Collections.synchronizedList(new ArrayList<>());
        private int heldBack;
        private long refuseUntil;

        AnswerLosingProxy(int target, Duration downtime) throws IOException {
            this(target, Mode.LOSE_ONCE, downtime);
        }

        AnswerLosingProxy(int target, Mode mode) throws IOException {
            this(target, mode, Duration.ZERO);
        }

        private AnswerLosingProxy(int target, Mode mode, Duration downtime) throws IOException {
            this.target = target;
            this.mode = mode;
            this.downtimeNanos = downtime.toNanos();
            Thread acceptor = new Thread(this::accept, "answer-losing-proxy");
            acceptor.setDaemon(true);
            acceptor.start();
        }

        int port() {
            return listener.getLocalPort();
        }

        synchronized boolean lostAnAnswer() {
            return heldBack > 0;
        }

        synchronized int answersHeldBack() {
            return heldBack;
        }

        private synchronized boolean refusing() {
            return mode == Mode.LOSE_ONCE && heldBack > 0 && System.nanoTime() - refuseUntil < 0;
        }

        /** True for an answer to a request that is then kept back. */
        private synchronized boolean keepThisOne() {
            if (mode == Mode.LOSE_ONCE && heldBack > 0) {
                return false;
            }
            heldBack += 1;
            refuseUntil = System.nanoTime() + downtimeNanos;
            return true;
        }

        private void accept() {
            try {
                while (true) {
                    Socket client = listener.accept();
                    if (refusing()) {
                        client.close();
                        continue;
                    }
                    sockets.add(client);
                    Thread forwarder = new Thread(() -> forward(client), "answer-losing-proxy-forward");
                    forwarder.setDaemon(true);
                    forwarder.start();
                }
            } catch (IOException closed) {
                // The test is over.
            }
        }

        private void forward(Socket client) {
            try (client; Socket server = new Socket(InetAddress.getLoopbackAddress(), target)) {
                sockets.add(server);
                Thread up = new Thread(() -> pump(client, server), "answer-losing-proxy-up");
                up.setDaemon(true);
                up.start();
                InputStream from = server.getInputStream();
                OutputStream to = client.getOutputStream();
                // Frames are a two-byte length and a body. The receiver's third, after the handshake and the welcome,
                // answers the first request.
                for (int frame = 1; ; frame++) {
                    byte[] header = from.readNBytes(2);
                    if (header.length < 2) {
                        return;
                    }
                    byte[] body = from.readNBytes(((header[0] & 0xFF) << 8) | (header[1] & 0xFF));
                    if (frame == 3 && keepThisOne()) {
                        if (mode == Mode.HOLD_EVERY) {
                            // Silent until the gateway closes the connection.
                            from.transferTo(OutputStream.nullOutputStream());
                        }
                        return;
                    }
                    to.write(header);
                    to.write(body);
                    to.flush();
                }
            } catch (IOException ended) {
                // Either side closed the connection.
            }
        }

        private static void pump(Socket from, Socket to) {
            try {
                from.getInputStream().transferTo(to.getOutputStream());
            } catch (IOException ended) {
                // Either side closed the connection.
            } finally {
                try {
                    to.close();
                    from.close();
                } catch (IOException ignored) {
                    // Closed already.
                }
            }
        }

        @Override
        public void close() throws IOException {
            listener.close();
            synchronized (sockets) {
                for (Socket socket : sockets) {
                    socket.close();
                }
            }
        }
    }
}
