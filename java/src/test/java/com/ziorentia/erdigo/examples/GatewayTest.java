package com.ziorentia.erdigo.examples;

import static com.ziorentia.erdigo.examples.CloudRelayExample.GATEWAY;
import static com.ziorentia.erdigo.examples.CloudRelayExample.discard;
import static com.ziorentia.erdigo.examples.CloudRelayExample.fetch;
import static com.ziorentia.erdigo.examples.CloudRelayExample.gateway;
import static com.ziorentia.erdigo.examples.CloudRelayExample.launch;
import static com.ziorentia.erdigo.examples.CloudRelayExample.post;
import static com.ziorentia.erdigo.examples.CloudRelayExample.relayEnvironment;
import static com.ziorentia.erdigo.examples.CloudRelayExample.token;
import static com.ziorentia.erdigo.examples.CloudRelayExample.waitForLine;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertTrue;
import static org.junit.jupiter.api.Assumptions.assumeFalse;

import com.sun.net.httpserver.HttpServer;
import com.ziorentia.erdigo.core.ItemKind;
import com.ziorentia.erdigo.core.JsonValue;
import com.ziorentia.erdigo.core.StrictJson;
import com.ziorentia.erdigo.examples.CloudRelayExample.InProcessRelay;
import com.ziorentia.erdigo.examples.CloudRelayExample.Result;
import com.ziorentia.erdigo.sdk.DeviceIdentity;
import com.ziorentia.erdigo.sdk.FileDeviceStore;
import com.ziorentia.erdigo.sdk.PrivateFile;
import com.ziorentia.erdigo.sdk.ReceiverPairing;
import java.io.IOException;
import java.io.OutputStream;
import java.net.BindException;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.attribute.PosixFilePermissions;
import java.time.Duration;
import java.time.Instant;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Map;
import java.util.concurrent.TimeUnit;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

/**
 * The example gateway as a program, without a receiver: a pairing is made up, at an address where nothing listens, so
 * everything that happens before the app is contacted, or because it cannot be, can be checked anywhere.
 */
class GatewayTest {
    private static final String EPOCH = "EpochOfTheFakeRelay1";

    @TempDir
    Path work;

    private final String source = token();
    private final String gatewayToken = token();
    private final List<AutoCloseable> running = new ArrayList<>();

    @AfterEach
    void stop() throws Exception {
        for (AutoCloseable each : running) {
            each.close();
        }
    }

    private Map<String, String> withToken(String token) {
        return Map.of("ERDIGO_RELAY_TOKEN", token);
    }

    private static int unusedPort() throws IOException {
        try (ServerSocket socket = new ServerSocket(0, 1, InetAddress.getLoopbackAddress())) {
            return socket.getLocalPort();
        }
    }

    /** A state folder paired with a receiver that is not there. */
    private Path madeUpPairing(String name, int port) {
        Path folder = work.resolve(name);
        FileDeviceStore store = new FileDeviceStore(folder);
        store.loadOrCreateIdentity().close();
        try (DeviceIdentity receiver = DeviceIdentity.generate()) {
            store.save(new ReceiverPairing("fake", receiver.publicKey(), List.of(ItemKind.values()), "127.0.0.1", port, Instant.now(), 0));
        }
        return folder;
    }

    private InProcessRelay relay() throws Exception {
        InProcessRelay relay = InProcessRelay.start(relayEnvironment(source, gatewayToken), discard());
        running.add(relay);
        return relay;
    }

    private static String batch(String... ops) {
        return "{\"ops\":[" + String.join(",", ops) + "]}";
    }

    private static String pin(String id) {
        return "{\"op\":\"pin\",\"id\":\"" + id + "\",\"lat\":37.5663,\"lon\":126.9779}";
    }

    private static JsonValue.Object stateOf(Path folder) throws IOException {
        return (JsonValue.Object) StrictJson.parse(Files.readAllBytes(folder.resolve("relay-state.json")));
    }

    /** Gives the gateway a place, as it would have saved one. */
    private static void place(Path folder, String relay, String epoch, long seq) throws IOException {
        String text = "{\"version\":1,\"relay\":\"" + relay + "\",\"epoch\":\"" + epoch + "\",\"seq\":" + seq + "}";
        PrivateFile.writeAllBytes(folder.resolve("relay-state.json"), text.getBytes(StandardCharsets.UTF_8));
    }

    // A relay of the test's own, which answers what each test needs

    private static final class FakeRelay implements AutoCloseable {
        final HttpServer server;
        final List<String> queries = Collections.synchronizedList(new ArrayList<>());
        final List<String> tokens = Collections.synchronizedList(new ArrayList<>());
        volatile List<Answer> answers = List.of();

        record Answer(int status, String type, byte[] body) {
        }

        FakeRelay() throws IOException {
            server = HttpServer.create(new InetSocketAddress(InetAddress.getLoopbackAddress(), 0), 0);
            server.createContext("/", exchange -> {
                queries.add(String.valueOf(exchange.getRequestURI().getRawQuery()));
                tokens.add(exchange.getRequestHeaders().getFirst("Authorization"));
                List<Answer> script = answers;
                Answer answer = script.get(Math.min(queries.size(), script.size()) - 1);
                if (answer.type() != null) {
                    exchange.getResponseHeaders().set("Content-Type", answer.type());
                }
                exchange.sendResponseHeaders(answer.status(), answer.body().length == 0 ? -1 : answer.body().length);
                try (OutputStream output = exchange.getResponseBody()) {
                    output.write(answer.body());
                }
            });
            server.start();
        }

        String url() {
            return "http://127.0.0.1:" + server.getAddress().getPort();
        }

        void answer(String... bodies) {
            List<Answer> script = new ArrayList<>();
            for (String body : bodies) {
                script.add(new Answer(200, "application/json", body.getBytes(StandardCharsets.UTF_8)));
            }
            answers = script;
            queries.clear();
            tokens.clear();
        }

        @Override
        public void close() {
            server.stop(0);
        }
    }

    private FakeRelay fakeRelay() throws IOException {
        FakeRelay fake = new FakeRelay();
        running.add(fake);
        return fake;
    }

    // Before the relay is asked

    @Test
    void refusesMistakesInTheCommandLineAndTheEnvironment() throws Exception {
        Path paired = madeUpPairing("paired", unusedPort());
        String state = paired.toString();
        Path damaged = work.resolve("damaged");
        PrivateFile.ensurePrivateDirectory(damaged);
        PrivateFile.writeAllBytes(damaged.resolve("pairings.json"), "garbage".getBytes(StandardCharsets.UTF_8));
        Map<String, String> token = withToken(gatewayToken);
        record Mistake(String expected, Map<String, String> env, String... args) {
        }
        String secret = token();
        List<Mistake> mistakes = List.of(
                new Mistake("--relay is required", token, "run", "--state-dir", state),
                new Mistake("must start with https://", token, "run", "--relay", "http://relay.example.com", "--state-dir", state),
                new Mistake("must start with https://", token, "run", "--relay", "http://192.0.2.1:8080", "--state-dir", state),
                new Mistake("must start with https://", token, "run", "--relay", "ftp://relay.example.com", "--state-dir", state),
                new Mistake("must start with https://", token, "run", "--relay", "relay.example.com", "--state-dir", state),
                new Mistake("no user name", token, "run", "--relay", "https://name@relay.example.com", "--state-dir", state),
                new Mistake("no user name", token, "run", "--relay", "https://relay.example.com/?x=1", "--state-dir", state),
                new Mistake("port outside 1 to 65535", token, "run", "--relay", "http://127.0.0.1:99999", "--state-dir", state),
                new Mistake("port outside 1 to 65535", token, "run", "--relay", "http://127.0.0.1:0", "--state-dir", state),
                new Mistake("port outside 1 to 65535", token, "run", "--relay", "https://relay.example.com:65536", "--state-dir", state),
                new Mistake("ERDIGO_RELAY_TOKEN is not set", Map.of(), "run", "--relay", "https://relay.example.com", "--state-dir", state),
                new Mistake("ERDIGO_RELAY_TOKEN must hold", withToken("short"), "run", "--relay", "https://relay.example.com", "--state-dir", state),
                new Mistake("ERDIGO_RELAY_TOKEN must hold", withToken(secret + " "), "run", "--relay", "https://relay.example.com", "--state-dir", state),
                new Mistake("no pairing", token, "run", "--relay", "https://relay.example.com", "--state-dir", work.resolve("empty").toString()),
                new Mistake("not accepted as arguments", token, "run", "--relay", "https://relay.example.com", "--token", secret),
                new Mistake("not accepted as arguments", Map.of(), "pair", "--name", "Gateway", "--code", secret),
                new Mistake("unexpected argument", token, "run", secret),
                new Mistake("unknown option", token, "run", "--relay", "https://relay.example.com", "--" + secret, "x"),
                new Mistake("cannot be used together", token, "run", "--relay", "https://relay.example.com", "--profile", "x", "--state-dir", state),
                new Mistake("--host and --port go together", token, "run", "--relay", "https://relay.example.com", "--host", "127.0.0.1", "--state-dir", state),
                new Mistake("does not apply", token, "run", "--relay", "https://relay.example.com", "--name", "x", "--state-dir", state),
                new Mistake("unknown option", Map.of(), "pair", "--name", "x", "--once", "--state-dir", state),
                new Mistake("--name is required", Map.of(), "pair", "--state-dir", work.resolve("unpaired").toString()),
                new Mistake("already holds a pairing", Map.of(), "pair", "--name", "Again", "--state-dir", state),
                new Mistake("pairings.json could not be read", Map.of(), "pair", "--name", "x", "--host", "127.0.0.1", "--port", "1",
                        "--state-dir", damaged.toString()),
                new Mistake("pairings.json could not be read", token, "run", "--relay", "https://relay.example.com",
                        "--state-dir", damaged.toString()),
                new Mistake("unknown command", Map.of(), secret));
        for (Mistake mistake : mistakes) {
            Result result = gateway(work, mistake.env(), mistake.args());
            String shown = String.join(" ", mistake.args());
            assertEquals(1, result.status(), shown + ": " + result.output());
            assertTrue(result.output().contains(mistake.expected()), shown + ": " + result.output());
            assertFalse(result.output().contains(secret), "a secret was repeated: " + result.output());
            assertFalse(result.output().contains("\tat "), "a stack trace: " + result.output());
        }
        Result help = gateway(work, Map.of(), "help");
        assertEquals(0, help.status());
        assertTrue(help.output().contains("gateway run  --relay URL"));
    }

    @Test
    void endsWithCode2WhenTheRelayRefusesTheToken() throws Exception {
        InProcessRelay relay = relay();
        Path paired = madeUpPairing("paired", unusedPort());
        Result result = gateway(work, withToken(token()), "run", "--relay", relay.url, "--state-dir", paired.toString(), "--once");
        assertEquals(2, result.status(), result.output());
        assertTrue(result.output().contains("the relay refused the token"), result.output());
        // A source token is no gateway token.
        result = gateway(work, withToken(source), "run", "--relay", relay.url, "--state-dir", paired.toString(), "--once");
        assertEquals(2, result.status(), result.output());
    }

    @Test
    void endsWithCode2WhenTheRelayBreaksItsApi() throws Exception {
        FakeRelay fake = fakeRelay();
        Path paired = madeUpPairing("paired", unusedPort());
        String good = "{\"epoch\":\"" + EPOCH + "\",\"reset\":true,\"missed\":false,\"ops\":[]}";
        String entry = "{\"seq\":1,\"received_ms\":0,\"op\":{\"op\":\"delete\",\"id\":\"a\"}}";
        List<String> broken = List.of(
                "not json",
                "[]",
                "{\"epoch\":\"" + EPOCH + "\",\"reset\":true,\"ops\":[]}",
                "{\"epoch\":\"" + EPOCH + "\",\"reset\":true,\"missed\":false,\"ops\":[],\"more\":1}",
                "{\"epoch\":\"short\",\"reset\":true,\"missed\":false,\"ops\":[]}",
                "{\"epoch\":\"" + EPOCH + "!\",\"reset\":true,\"missed\":false,\"ops\":[]}",
                "{\"epoch\":\"" + EPOCH + "\",\"reset\":\"true\",\"missed\":false,\"ops\":[]}",
                // Without an epoch from the gateway, the relay must start over.
                "{\"epoch\":\"" + EPOCH + "\",\"reset\":false,\"missed\":false,\"ops\":[]}",
                "{\"epoch\":\"" + EPOCH + "\",\"reset\":true,\"missed\":false,\"ops\":[" + entry + "," + entry + "]}",
                "{\"epoch\":\"" + EPOCH + "\",\"reset\":true,\"missed\":false,\"ops\":[" + entry.replace("\"seq\":1", "\"seq\":0") + "]}",
                "{\"epoch\":\"" + EPOCH + "\",\"reset\":true,\"missed\":false,\"ops\":[" + entry.replace("\"seq\":1", "\"seq\":1.5") + "]}",
                "{\"epoch\":\"" + EPOCH + "\",\"reset\":true,\"missed\":false,\"ops\":[{\"seq\":1,\"received_ms\":0,\"op\":[]}]}",
                "{\"epoch\":\"" + EPOCH + "\",\"reset\":true,\"missed\":false,\"ops\":[{\"seq\":1,\"op\":{\"op\":\"delete\",\"id\":\"a\"}}]}",
                "{\"epoch\":\"" + EPOCH + "\",\"reset\":true,\"missed\":false,\"ops\":[" + (entry + ",").repeat(100)
                        .replace("\"seq\":1,", "\"seq\":SEQ,") + entry.replace("\"seq\":1", "\"seq\":101") + "]}");
        for (String body : broken) {
            String numbered = body;
            for (int seq = 1; numbered.contains("SEQ"); seq++) {
                numbered = numbered.replaceFirst("SEQ", String.valueOf(seq));
            }
            fake.answer(numbered);
            Result result = gateway(work, withToken(gatewayToken), "run", "--relay", fake.url(), "--state-dir", paired.toString(), "--once");
            assertEquals(2, result.status(), body + ": " + result.output());
        }

        // With a place at seq 5 of EPOCH: going back, a numbering kept under another epoch, or a new one under the same.
        String other = "OtherEpochOfTheRelay";
        List<String> afterPlace = List.of(
                "{\"epoch\":\"" + EPOCH + "\",\"reset\":false,\"missed\":false,\"ops\":[" + entry.replace("\"seq\":1", "\"seq\":5") + "]}",
                "{\"epoch\":\"" + other + "\",\"reset\":false,\"missed\":false,\"ops\":[]}",
                "{\"epoch\":\"" + EPOCH + "\",\"reset\":true,\"missed\":false,\"ops\":[]}");
        Result result = null;
        for (String body : afterPlace) {
            place(paired, fake.url(), EPOCH, 5);
            fake.answer(body);
            result = gateway(work, withToken(gatewayToken), "run", "--relay", fake.url(), "--state-dir", paired.toString(), "--once");
            assertEquals(2, result.status(), body + ": " + result.output());
            assertEquals(List.of("epoch=" + EPOCH + "&after=5&wait=0"), fake.queries);
        }
        Files.delete(paired.resolve("relay-state.json"));

        // Answers that are not the relay's: other statuses, other content, and too much of it.
        List<FakeRelay.Answer> others = List.of(
                new FakeRelay.Answer(200, "text/html", good.getBytes(StandardCharsets.UTF_8)),
                new FakeRelay.Answer(200, null, good.getBytes(StandardCharsets.UTF_8)),
                new FakeRelay.Answer(404, "application/json", "{\"error\":\"not_found\"}".getBytes(StandardCharsets.UTF_8)),
                new FakeRelay.Answer(400, "application/json", "{\"error\":\"invalid_query\"}".getBytes(StandardCharsets.UTF_8)),
                new FakeRelay.Answer(302, "text/html", new byte[0]),
                new FakeRelay.Answer(200, "application/json", new byte[8 * 1024 * 1024 + 1]));
        for (FakeRelay.Answer answer : others) {
            fake.answers = List.of(answer);
            result = gateway(work, withToken(gatewayToken), "run", "--relay", fake.url(), "--state-dir", paired.toString(), "--once");
            assertEquals(2, result.status(), answer.status() + " " + answer.type() + ": " + result.output());
        }
        assertTrue(result.output().contains("larger than"), result.output());
    }

    // What the gateway does with the operations

    @Test
    void refusesWhatItCannotMapOrTheSdkRefusesAndSavesItsPlace() throws Exception {
        FakeRelay fake = fakeRelay();
        Path paired = madeUpPairing("paired", unusedPort());
        String ops = String.join(",",
                "{\"seq\":3,\"received_ms\":1,\"op\":{\"op\":\"circle\",\"id\":\"a\"}}",
                "{\"seq\":5,\"received_ms\":1,\"op\":{\"op\":\"pin\",\"id\":\"bad id\",\"lat\":0,\"lon\":0}}",
                "{\"seq\":6,\"received_ms\":1,\"op\":{\"op\":\"pin\",\"id\":\"a\",\"lat\":91,\"lon\":0}}",
                "{\"seq\":7,\"received_ms\":1,\"op\":{\"op\":\"photo\",\"id\":\"a\",\"lat\":0,\"lon\":0,\"type\":\"image/jpeg\",\"data\":\""
                        + CloudRelayExample.PNG_1X1 + "\"}}",
                "{\"seq\":8,\"received_ms\":1,\"op\":{\"op\":\"marker\",\"id\":\"a\",\"lat\":0,\"lon\":0,\"color\":\"pink\"}}",
                "{\"seq\":9,\"received_ms\":1,\"op\":{\"op\":\"note\",\"id\":\"a\",\"lat\":0,\"lon\":0,\"text\":\"a\\rb\"}}",
                "{\"seq\":10,\"received_ms\":1,\"op\":{\"op\":\"track\",\"id\":\"a\",\"points\":[{\"lat\":0,\"lon\":0,\"ts\":1.5}]}}");
        fake.answer("{\"epoch\":\"" + EPOCH + "\",\"reset\":true,\"missed\":true,\"ops\":[" + ops + "]}",
                "{\"epoch\":\"" + EPOCH + "\",\"reset\":false,\"missed\":false,\"ops\":[]}");
        Result result = gateway(work, withToken(gatewayToken), "run", "--relay", fake.url() + "/", "--state-dir", paired.toString(), "--once");
        assertEquals(0, result.status(), result.output());
        List<String> lines = result.output().lines().toList();
        assertEquals(List.of(
                "Some operations expired on the relay before they were fetched.",
                "Refused operation 3 (unknown): invalid_op",
                "Refused operation 5 (pin): invalid_argument",
                "Refused operation 6 (pin): invalid_coordinate",
                "Refused operation 7 (photo): invalid_op",
                "Refused operation 8 (marker): invalid_op",
                "Refused operation 9 (note): invalid_argument",
                "Refused operation 10 (track): invalid_op",
                "Done with 7 operations, up to 10 (7 refused)"), lines);
        assertEquals(List.of("wait=0", "epoch=" + EPOCH + "&after=10&wait=0"), fake.queries);
        assertEquals("Bearer " + gatewayToken, fake.tokens.get(0));

        JsonValue.Object state = stateOf(paired);
        assertEquals(fake.url(), ((JsonValue.Text) state.fields().get("relay")).value());
        assertEquals(EPOCH, ((JsonValue.Text) state.fields().get("epoch")).value());
        assertEquals(10L, ((JsonValue.Integer) state.fields().get("seq")).value());
        if (!CloudRelayExample.WINDOWS) {
            assertEquals("rw-------", PosixFilePermissions.toString(Files.getPosixFilePermissions(paired.resolve("relay-state.json"))));
        }
        try (var leftovers = Files.list(paired)) {
            assertTrue(leftovers.noneMatch(path -> path.getFileName().toString().startsWith(".relay-state.json.")));
        }

        // A damaged place is reported, not guessed at.
        PrivateFile.writeAllBytes(paired.resolve("relay-state.json"), "{\"version\":1}".getBytes(StandardCharsets.UTF_8));
        result = gateway(work, withToken(gatewayToken), "run", "--relay", fake.url(), "--state-dir", paired.toString(), "--once");
        assertEquals(1, result.status(), result.output());
        assertTrue(result.output().contains("relay-state.json is damaged"), result.output());
    }

    @Test
    void saysWhenItsRelayStartedAgainButNotForAnotherRelay() throws Exception {
        InProcessRelay first = relay();
        Path paired = madeUpPairing("paired", unusedPort());
        Result result = gateway(work, withToken(gatewayToken), "run", "--relay", first.url, "--state-dir", paired.toString(), "--once");
        assertEquals(0, result.status(), result.output());
        assertEquals("", result.output());
        String firstEpoch = ((JsonValue.Text) stateOf(paired).fields().get("epoch")).value();

        // The same address, a new start.
        int port = first.port;
        first.close();
        InProcessRelay again = null;
        for (int attempt = 0; again == null; attempt++) {
            try {
                again = InProcessRelay.start(List.of("--listen", "127.0.0.1:" + port), relayEnvironment(source, gatewayToken),
                        System::currentTimeMillis, 10_000, 64L * 1024 * 1024, discard());
            } catch (BindException busy) {
                if (attempt > 50) {
                    throw busy;
                }
                Thread.sleep(100);
            }
        }
        running.add(again);
        result = gateway(work, withToken(gatewayToken), "run", "--relay", again.url, "--state-dir", paired.toString(), "--once");
        assertEquals(0, result.status(), result.output());
        assertEquals("The relay started a new numbering; starting from its oldest operation.", result.output().strip());
        assertFalse(firstEpoch.equals(((JsonValue.Text) stateOf(paired).fields().get("epoch")).value()));

        // Another relay is not this one starting again.
        InProcessRelay other = relay();
        result = gateway(work, withToken(gatewayToken), "run", "--relay", other.url, "--state-dir", paired.toString(), "--once");
        assertEquals(0, result.status(), result.output());
        assertEquals("", result.output());
    }

    /** Takes about 30 seconds, as --once does when the app cannot be reached. */
    @Test
    void keepsTheOperationsOnTheRelayWhenTheAppCannotBeReached() throws Exception {
        InProcessRelay relay = relay();
        assertEquals(200, post(relay.url, source, batch(pin("drone-1"), pin("drone-2"))).status());
        int port = unusedPort();
        Path paired = madeUpPairing("paired", port);
        long started = System.nanoTime();
        Result result = gateway(work, withToken(gatewayToken), "run", "--relay", relay.url, "--state-dir", paired.toString(),
                "--host", "127.0.0.1", "--port", String.valueOf(port), "--once");
        long seconds = (System.nanoTime() - started) / 1_000_000_000L;
        assertEquals(3, result.status(), result.output());
        // The SDK sends the first operation again and again. The gateway says so once, with no time, since the SDK
        // does not say when it tries next.
        assertEquals(List.of("No answer from the Erdigo app; trying again.",
                "The Erdigo app could not be reached. The operations stay on the relay."), result.output().lines().toList());
        assertTrue(seconds >= 25 && seconds < 50, "gave up after " + seconds + " seconds");
        // Nothing was finished, so the relay holds both, and the place is at the start.
        assertEquals(2, fetch(relay.url, gatewayToken, "").ops().size());
        assertEquals(0L, ((JsonValue.Integer) stateOf(paired).fields().get("seq")).value());
    }

    /**
     * Takes about 30 seconds as well: two gateways at once, one with nothing at the relay's address, and one with a relay
     * that takes the request and never answers. Each attempt's time counts, so neither takes much longer.
     */
    @Test
    void givesUpOnARelayThatCannotBeReachedWithOnce() throws Exception {
        List<Socket> held = Collections.synchronizedList(new ArrayList<>());
        ServerSocket silent = new ServerSocket(0, 50, InetAddress.getLoopbackAddress());
        running.add(silent);
        running.add(() -> {
            synchronized (held) {
                for (Socket socket : held) {
                    socket.close();
                }
            }
        });
        Thread acceptor = new Thread(() -> {
            try {
                while (true) {
                    held.add(silent.accept());
                }
            } catch (IOException closed) {
                // The test is over.
            }
        });
        acceptor.setDaemon(true);
        acceptor.start();

        long started = System.nanoTime();
        List<Process> processes = new ArrayList<>();
        List<Path> outputs = new ArrayList<>();
        for (String relayUrl : List.of("http://127.0.0.1:" + unusedPort(), "http://127.0.0.1:" + silent.getLocalPort())) {
            Path output = Files.createTempFile(work, "output", ".txt");
            Path paired = madeUpPairing("paired-" + processes.size(), unusedPort());
            processes.add(launch(GATEWAY, List.of("run", "--relay", relayUrl, "--state-dir", paired.toString(), "--once"),
                    withToken(gatewayToken), output));
            outputs.add(output);
        }
        for (int index = 0; index < processes.size(); index++) {
            Process process = processes.get(index);
            assertTrue(process.waitFor(90, TimeUnit.SECONDS), "it did not give up");
            String output = Files.readString(outputs.get(index), StandardCharsets.UTF_8);
            assertEquals(3, process.exitValue(), output);
            List<String> lines = output.lines().toList();
            assertTrue(lines.get(0).startsWith("The relay could not be reached; trying again in "), output);
            assertEquals("The relay could not be reached.", lines.get(lines.size() - 1));
        }
        long seconds = (System.nanoTime() - started) / 1_000_000_000L;
        assertTrue(seconds < 50, "gave up after " + seconds + " seconds");
        assertFalse(held.isEmpty(), "the silent relay was never asked");
    }

    @Test
    void pausesWhenARelayAnswersAtOnceWithNothing() throws Exception {
        FakeRelay fake = fakeRelay();
        // A relay that cannot hold a fetch open: every answer comes at once, empty.
        fake.answer("{\"epoch\":\"" + EPOCH + "\",\"reset\":true,\"missed\":false,\"ops\":[]}",
                "{\"epoch\":\"" + EPOCH + "\",\"reset\":false,\"missed\":false,\"ops\":[]}");
        Path paired = madeUpPairing("paired", unusedPort());
        Path output = work.resolve("output.txt");
        Process process = launch(GATEWAY, List.of("run", "--relay", fake.url(), "--state-dir", paired.toString()),
                withToken(gatewayToken), output);
        try {
            long deadline = System.nanoTime() + Duration.ofSeconds(30).toNanos();
            while (fake.queries.isEmpty() && System.nanoTime() < deadline) {
                Thread.sleep(20);
            }
            assertFalse(fake.queries.isEmpty(), "the gateway did not fetch: " + Files.readString(output));
            // About 5 seconds between fetches: two in 7 seconds, perhaps three, and never a flood.
            Thread.sleep(7_000);
            List<String> queries = List.copyOf(fake.queries);
            assertTrue(queries.size() >= 2 && queries.size() <= 3, queries.size() + " fetches in 7 seconds");
            assertEquals("wait=25", queries.get(0));
            assertEquals("epoch=" + EPOCH + "&after=0&wait=25", queries.get(1));
            assertTrue(process.isAlive(), Files.readString(output));
        } finally {
            process.destroy();
            if (!process.waitFor(15, TimeUnit.SECONDS)) {
                process.destroyForcibly();
            }
        }
        assertEquals("", Files.readString(output).strip());
    }

    /**
     * Two runs with one key would take each other's connection to the app and repeat each other's work, so a run holds
     * a lock in the state folder while it runs, and a second run on that folder ends at once with code 1.
     */
    @Test
    void refusesASecondRunOnAProfileInUse() throws Exception {
        InProcessRelay relay = relay();
        Path paired = madeUpPairing("paired", unusedPort());
        Path output = work.resolve("output.txt");
        Process first = launch(GATEWAY, List.of("run", "--relay", relay.url, "--state-dir", paired.toString()),
                withToken(gatewayToken), output);
        try {
            // It locks the folder before it fetches, and saves its place after the first fetch.
            long deadline = System.nanoTime() + Duration.ofSeconds(30).toNanos();
            while (!Files.exists(paired.resolve("relay-state.json")) && System.nanoTime() < deadline) {
                Thread.sleep(50);
            }
            assertTrue(Files.exists(paired.resolve("relay-state.json")), Files.readString(output));
            if (!CloudRelayExample.WINDOWS) {
                assertEquals("rw-------", PosixFilePermissions.toString(Files.getPosixFilePermissions(paired.resolve(".gateway.lock"))));
            }
            long started = System.nanoTime();
            Result second = gateway(work, withToken(gatewayToken), "run", "--relay", relay.url, "--state-dir", paired.toString(), "--once");
            long seconds = (System.nanoTime() - started) / 1_000_000_000L;
            assertEquals(1, second.status(), second.output());
            assertEquals(List.of("gateway: another gateway is running with this profile or state folder. Run one gateway per profile."),
                    second.output().lines().toList());
            assertTrue(seconds < 15, "ended after " + seconds + " seconds");
            assertTrue(first.isAlive(), Files.readString(output));
        } finally {
            first.destroy();
            if (!first.waitFor(15, TimeUnit.SECONDS)) {
                first.destroyForcibly();
            }
        }
        if (!CloudRelayExample.WINDOWS) {
            // SIGTERM. Windows ends the process without a signal, and so with another code.
            assertEquals(0, first.exitValue(), Files.readString(output));
        }
        assertEquals("", Files.readString(output).strip());
        // Once the first has ended, the folder is free again.
        Result after = gateway(work, withToken(gatewayToken), "run", "--relay", relay.url, "--state-dir", paired.toString(), "--once");
        assertEquals(0, after.status(), after.output());
        assertEquals("", after.output());
    }

    @Test
    void stopsCleanlyOnSigterm() throws Exception {
        assumeFalse(CloudRelayExample.WINDOWS, "Windows has no SIGTERM");
        InProcessRelay relay = relay();
        Path paired = madeUpPairing("paired", unusedPort());
        Path output = work.resolve("output.txt");
        Process process = launch(GATEWAY, List.of("run", "--relay", relay.url, "--state-dir", paired.toString()),
                withToken(gatewayToken), output);
        // Fetching with wait=25, so it is waiting on the relay.
        long deadline = System.nanoTime() + Duration.ofSeconds(30).toNanos();
        while (!Files.exists(paired.resolve("relay-state.json")) && System.nanoTime() < deadline) {
            Thread.sleep(50);
        }
        assertTrue(Files.exists(paired.resolve("relay-state.json")));
        Thread.sleep(500);
        process.destroy();
        assertTrue(process.waitFor(15, TimeUnit.SECONDS), "it did not stop");
        assertEquals(0, process.exitValue(), Files.readString(output));
    }

    /**
     * Without --once the SDK waits for the app until the thread is interrupted, and SIGTERM does that. The operation it
     * was sending is neither saved nor acknowledged, so it stays on the relay.
     */
    @Test
    void stopsCleanlyOnSigtermWhileTheAppCannotBeReached() throws Exception {
        assumeFalse(CloudRelayExample.WINDOWS, "Windows has no SIGTERM");
        InProcessRelay relay = relay();
        assertEquals(200, post(relay.url, source, batch(pin("drone-1"))).status());
        int port = unusedPort();
        Path paired = madeUpPairing("paired", port);
        Path output = work.resolve("output.txt");
        Process process = launch(GATEWAY, List.of("run", "--relay", relay.url, "--state-dir", paired.toString(),
                "--host", "127.0.0.1", "--port", String.valueOf(port)), withToken(gatewayToken), output);
        try {
            assertNotNull(waitForLine(output, "No answer from the Erdigo app; trying again.", Duration.ofSeconds(30)),
                    Files.readString(output));
            // Well into the SDK's waits between attempts.
            Thread.sleep(3_000);
            assertTrue(process.isAlive(), Files.readString(output));
            long stopped = System.nanoTime();
            process.destroy();
            assertTrue(process.waitFor(15, TimeUnit.SECONDS), "it did not stop");
            long seconds = (System.nanoTime() - stopped) / 1_000_000_000L;
            assertTrue(seconds < 5, "stopped after " + seconds + " seconds");
        } finally {
            if (process.isAlive()) {
                process.destroyForcibly();
            }
        }
        assertEquals(0, process.exitValue(), Files.readString(output));
        assertEquals(List.of("No answer from the Erdigo app; trying again."), Files.readAllLines(output, StandardCharsets.UTF_8));
        assertEquals(1, fetch(relay.url, gatewayToken, "").ops().size());
        assertEquals(0L, ((JsonValue.Integer) stateOf(paired).fields().get("seq")).value());
    }
}
