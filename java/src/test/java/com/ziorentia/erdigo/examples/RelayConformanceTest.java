package com.ziorentia.erdigo.examples;

import static com.ziorentia.erdigo.examples.CloudRelayExample.HTTP;
import static com.ziorentia.erdigo.examples.CloudRelayExample.fetch;
import static com.ziorentia.erdigo.examples.CloudRelayExample.json;
import static com.ziorentia.erdigo.examples.CloudRelayExample.parse;
import static com.ziorentia.erdigo.examples.CloudRelayExample.post;
import static com.ziorentia.erdigo.examples.CloudRelayExample.rawStatus;
import static com.ziorentia.erdigo.examples.CloudRelayExample.relayEnvironment;
import static com.ziorentia.erdigo.examples.CloudRelayExample.request;
import static com.ziorentia.erdigo.examples.CloudRelayExample.send;
import static com.ziorentia.erdigo.examples.CloudRelayExample.text;
import static com.ziorentia.erdigo.examples.CloudRelayExample.token;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.ziorentia.erdigo.core.JsonValue;
import com.ziorentia.erdigo.core.StrictJson;
import com.ziorentia.erdigo.examples.CloudRelayExample.InProcessRelay;
import com.ziorentia.erdigo.examples.CloudRelayExample.Reply;
import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.PrintStream;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Base64;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.atomic.AtomicLong;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;

/**
 * The example relay against docs/cloud-relay-cases.json and the rest of the relay API in docs/cloud-relay.md. The relay
 * runs in this process, with a clock the tests can move and, where a test says so, smaller limits.
 */
class RelayConformanceTest {
    private static final long HOUR = 3_600_000L;

    private final String source = token();
    private final String otherSource = token();
    private final String gateway = token();
    private final AtomicLong offset = new AtomicLong();
    private final ByteArrayOutputStream logBytes = new ByteArrayOutputStream();
    private final PrintStream log = new PrintStream(logBytes, true, StandardCharsets.UTF_8);
    private final List<InProcessRelay> started = new ArrayList<>();
    private InProcessRelay relay;
    private String url;

    @BeforeEach
    void startRelay() throws Exception {
        relay = start(List.of("--listen", "127.0.0.1:0"), 10_000, 64L * 1024 * 1024);
        url = relay.url;
    }

    @AfterEach
    void stopRelays() throws Exception {
        for (InProcessRelay each : started) {
            each.close();
        }
        // Whatever happened, the log holds neither a token nor what an operation held.
        String written = logBytes.toString(StandardCharsets.UTF_8);
        for (String secret : List.of(source, otherSource, gateway, "Seoul", "drone", "37.5663", "iVBOR")) {
            assertFalse(written.contains(secret), "the log holds " + secret);
        }
    }

    private InProcessRelay start(List<String> args, int maxOps, long maxBytes) throws Exception {
        InProcessRelay started = InProcessRelay.start(args, relayEnvironment(source + "," + otherSource, gateway),
                () -> System.currentTimeMillis() + offset.get(), maxOps, maxBytes, log);
        this.started.add(started);
        return started;
    }

    private static String batch(String... ops) {
        return "{\"ops\":[" + String.join(",", ops) + "]}";
    }

    private static String pin(String id) {
        return "{\"op\":\"pin\",\"id\":\"" + id + "\",\"lat\":37.5663,\"lon\":126.9779}";
    }

    private static String photo(String id, int bytes) {
        byte[] data = new byte[bytes];
        System.arraycopy(new byte[] {(byte) 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A}, 0, data, 0, 8);
        return "{\"op\":\"photo\",\"id\":\"" + id + "\",\"lat\":0,\"lon\":0,\"type\":\"image/png\",\"data\":\""
                + Base64.getEncoder().encodeToString(data) + "\"}";
    }

    /** Every operation the relay holds, without making it forget any. */
    private List<JsonValue> held(String relayUrl) throws Exception {
        Reply reply = fetch(relayUrl, gateway, "");
        assertEquals(200, reply.status());
        assertTrue(reply.flag("reset"));
        return reply.ops();
    }

    private static long seqOf(JsonValue entry) {
        return ((JsonValue.Integer) ((JsonValue.Object) entry).fields().get("seq")).value();
    }

    // The shared cases

    private JsonValue.Object cases() throws Exception {
        return (JsonValue.Object) StrictJson.parse(Files.readAllBytes(CloudRelayExample.casesFile()), 64);
    }

    @Test
    void takesEveryValidOperationAndGivesItBackUnchanged() throws Exception {
        List<JsonValue> valid = ((JsonValue.Array) cases().fields().get("valid_ops")).values();
        long expectedSeq = 0;
        for (JsonValue element : valid) {
            JsonValue.Object testCase = (JsonValue.Object) element;
            JsonValue op = testCase.fields().get("op");
            Reply reply = post(url, source, batch(json(op)));
            assertEquals(200, reply.status(), text(testCase, "name") + ": " + new String(reply.body(), StandardCharsets.UTF_8));
            expectedSeq += 1;
            assertEquals(1L, ((JsonValue.Integer) reply.json().fields().get("accepted")).value());
            assertEquals(expectedSeq, ((JsonValue.Integer) reply.json().fields().get("last_seq")).value());
        }
        List<JsonValue> held = held(url);
        assertEquals(valid.size(), held.size());
        for (int index = 0; index < valid.size(); index++) {
            JsonValue.Object entry = (JsonValue.Object) held.get(index);
            assertEquals(index + 1L, seqOf(entry));
            assertEquals(((JsonValue.Object) valid.get(index)).fields().get("op"), entry.fields().get("op"),
                    text((JsonValue.Object) valid.get(index), "name"));
            long received = ((JsonValue.Integer) entry.fields().get("received_ms")).value();
            assertTrue(Math.abs(received - System.currentTimeMillis()) < 60_000);
        }
    }

    @Test
    void refusesEveryInvalidOperationAndTakesNothing() throws Exception {
        for (JsonValue element : ((JsonValue.Array) cases().fields().get("invalid_ops")).values()) {
            JsonValue.Object testCase = (JsonValue.Object) element;
            Reply reply = post(url, source, batch(json(testCase.fields().get("op"))));
            String name = text(testCase, "name");
            assertEquals(400, reply.status(), name);
            assertEquals("invalid_op", reply.error(), name);
            assertEquals(0L, ((JsonValue.Integer) reply.json().fields().get("index")).value(), name);
            assertFalse(text(reply.json(), "reason").isEmpty(), name);
        }
        assertTrue(held(url).isEmpty());
    }

    @Test
    void refusesEveryRawBodyWithOneOfItsErrors() throws Exception {
        for (JsonValue element : ((JsonValue.Array) cases().fields().get("raw_bodies")).values()) {
            JsonValue.Object testCase = (JsonValue.Object) element;
            byte[] body = testCase.fields().containsKey("body_base64")
                    ? Base64.getDecoder().decode(text(testCase, "body_base64"))
                    : text(testCase, "body").getBytes(StandardCharsets.UTF_8);
            Reply reply = post(url, source, body);
            String name = text(testCase, "name");
            assertEquals(400, reply.status(), name);
            List<String> errors = new ArrayList<>();
            for (JsonValue error : ((JsonValue.Array) testCase.fields().get("errors")).values()) {
                errors.add(((JsonValue.Text) error).value());
            }
            assertTrue(errors.contains(reply.error()), name + ": " + reply.error());
        }
        assertTrue(held(url).isEmpty());
    }

    // Limits of one POST

    @Test
    void takesOneHundredOperationsButNotOneMore() throws Exception {
        String[] hundred = new String[100];
        Arrays.fill(hundred, pin("p1"));
        Reply reply = post(url, source, batch(hundred));
        assertEquals(200, reply.status());
        assertEquals(100L, ((JsonValue.Integer) reply.json().fields().get("accepted")).value());
        assertEquals(100L, ((JsonValue.Integer) reply.json().fields().get("last_seq")).value());
        String[] more = Arrays.copyOf(hundred, 101);
        more[100] = pin("p1");
        reply = post(url, source, batch(more));
        assertEquals(400, reply.status());
        assertEquals("invalid_body", reply.error());
    }

    @Test
    void takesAPhotoOfExactlyOneMebibyteButNotOneByteMore() throws Exception {
        Reply reply = post(url, source, batch(photo("big", 1_048_576)));
        assertEquals(200, reply.status(), new String(reply.body(), StandardCharsets.UTF_8));
        reply = post(url, source, batch(photo("big", 1_048_577)));
        assertEquals(400, reply.status());
        assertEquals("invalid_op", reply.error());
        assertEquals(1, held(url).size());
    }

    @Test
    void takesABatchWholeOrNotAtAll() throws Exception {
        Reply reply = post(url, source, batch(pin("a"), pin("b"), "{\"op\":\"pin\",\"id\":\"c\",\"lat\":91,\"lon\":0}"));
        assertEquals(400, reply.status());
        assertEquals("invalid_op", reply.error());
        assertEquals(2L, ((JsonValue.Integer) reply.json().fields().get("index")).value());
        assertTrue(held(url).isEmpty());
        reply = post(url, source, batch(pin("a"), pin("b")));
        assertEquals(2L, ((JsonValue.Integer) reply.json().fields().get("last_seq")).value());
    }

    @Test
    void refusesABodyOverTwoMebibytesAndTakesOneOfExactlyThat() throws Exception {
        // Announced as too large: refused before any of it is sent.
        assertEquals(413, rawStatus(relay.port, "POST /v1/ops HTTP/1.1\r\nHost: relay\r\nAuthorization: Bearer " + source
                + "\r\nContent-Type: application/json\r\nContent-Length: 2097153\r\n\r\n"));
        // Without a length, it is read up to the limit.
        byte[] tooLong = new byte[2_097_153];
        Arrays.fill(tooLong, (byte) ' ');
        Reply reply = send(request(url + "/v1/ops", source).header("Content-Type", "application/json")
                .POST(HttpRequest.BodyPublishers.ofInputStream(() -> new ByteArrayInputStream(tooLong))).build());
        assertEquals(413, reply.status());
        assertEquals("too_large", reply.error());
        // White space after the object is allowed, so this is a body of exactly the limit.
        byte[] exact = new byte[2_097_152];
        Arrays.fill(exact, (byte) ' ');
        byte[] body = batch(pin("edge")).getBytes(StandardCharsets.UTF_8);
        System.arraycopy(body, 0, exact, 0, body.length);
        reply = post(url, source, exact);
        assertEquals(200, reply.status());
        // Neither a length nor chunks: the relay asks for a length.
        assertEquals(411, rawStatus(relay.port, "POST /v1/ops HTTP/1.1\r\nHost: relay\r\nAuthorization: Bearer " + source
                + "\r\nContent-Type: application/json\r\n\r\n"));
    }

    @Test
    void takesOnlyJson() throws Exception {
        byte[] body = batch(pin("a")).getBytes(StandardCharsets.UTF_8);
        for (String type : List.of("text/plain", "application/x-www-form-urlencoded", "application/json; charset=latin1",
                "application/json; charset=utf-8; x=1", "application/jsonx")) {
            Reply reply = send(request(url + "/v1/ops", source).header("Content-Type", type)
                    .POST(HttpRequest.BodyPublishers.ofByteArray(body)).build());
            assertEquals(415, reply.status(), type);
            assertEquals("unsupported_media_type", reply.error());
        }
        Reply missing = send(request(url + "/v1/ops", source).POST(HttpRequest.BodyPublishers.ofByteArray(body)).build());
        assertEquals(415, missing.status());
        for (String type : List.of("application/json", "application/json; charset=utf-8", "Application/JSON;charset=UTF-8")) {
            Reply reply = send(request(url + "/v1/ops", source).header("Content-Type", type)
                    .POST(HttpRequest.BodyPublishers.ofByteArray(body)).build());
            assertEquals(200, reply.status(), type);
        }
    }

    // Order of refusals and tokens

    @Test
    void answersUnknownPathsWrongMethodsAndTokensInThatOrder() throws Exception {
        for (String path : List.of("/", "/v1", "/v1/ops/", "/v1/opsx", "/v2/ops", "/V1/OPS")) {
            Reply reply = send(request(url + path, source).GET().build());
            assertEquals(404, reply.status(), path);
            assertEquals("not_found", reply.error());
            reply = send(request(url + path, null).DELETE().build());
            assertEquals(404, reply.status(), path);
        }
        for (String method : List.of("PUT", "DELETE", "PATCH")) {
            Reply reply = send(request(url + "/v1/ops", null).method(method, HttpRequest.BodyPublishers.noBody()).build());
            assertEquals(405, reply.status(), method);
            assertEquals("method_not_allowed", reply.error());
        }
        Reply reply = send(request(url + "/v1/health", null).POST(HttpRequest.BodyPublishers.noBody()).build());
        assertEquals(405, reply.status());
        // The token comes before the content type.
        reply = send(request(url + "/v1/ops", null).header("Content-Type", "text/plain").POST(HttpRequest.BodyPublishers.noBody()).build());
        assertEquals(401, reply.status());
        assertEquals("unauthorized", reply.error());
    }

    @Test
    void letsEachTokenDoOnlyWhatItIsFor() throws Exception {
        String body = batch(pin("a"));
        assertEquals(200, post(url, source, body).status());
        assertEquals(200, post(url, otherSource, body).status());
        for (String wrong : new String[] {null, gateway, token(), source + "x", source.substring(1)}) {
            Reply reply = post(url, wrong, body);
            assertEquals(401, reply.status());
            assertEquals("unauthorized", reply.error());
        }
        assertEquals(401, send(request(url + "/v1/ops", null).header("Authorization", "Basic " + source)
                .header("Content-Type", "application/json").POST(HttpRequest.BodyPublishers.ofString(body)).build()).status());
        assertEquals(401, send(request(url + "/v1/ops", source).header("Authorization", "Bearer " + source)
                .header("Content-Type", "application/json").POST(HttpRequest.BodyPublishers.ofString(body)).build()).status());
        assertEquals(200, send(request(url + "/v1/ops", null).header("Authorization", "bearer " + source)
                .header("Content-Type", "application/json").POST(HttpRequest.BodyPublishers.ofString(body)).build()).status());

        assertEquals(200, fetch(url, gateway, "").status());
        for (String wrong : new String[] {null, source, otherSource, token()}) {
            Reply reply = fetch(url, wrong, "");
            assertEquals(401, reply.status());
            assertEquals("unauthorized", reply.error());
        }
    }

    @Test
    void answersHealthWithoutAToken() throws Exception {
        Reply reply = send(request(url + "/v1/health", null).GET().build());
        assertEquals(200, reply.status());
        assertEquals(parse("{\"ok\":true}"), reply.json());
    }

    // Fetching

    @Test
    void forgetsOnlyWhatTheGatewayIsDoneWith() throws Exception {
        post(url, source, batch(pin("a"), pin("b"), pin("c")));
        Reply first = fetch(url, gateway, "");
        assertTrue(first.flag("reset"));
        assertFalse(first.flag("missed"));
        String epoch = first.epoch();
        assertTrue(epoch.matches("[A-Za-z0-9_-]{16,64}"));
        assertEquals(3, first.ops().size());

        // Another epoch, or none: nothing is forgotten, and the answer starts from the oldest.
        Reply other = fetch(url, gateway, "epoch=AAAAAAAAAAAAAAAAAAAA&after=2");
        assertTrue(other.flag("reset"));
        assertEquals(epoch, other.epoch());
        assertEquals(3, other.ops().size());

        Reply matching = fetch(url, gateway, "epoch=" + epoch + "&after=2");
        assertFalse(matching.flag("reset"));
        assertEquals(1, matching.ops().size());
        assertEquals(3, seqOf(matching.ops().get(0)));
        List<JsonValue> held = held(url);
        assertEquals(1, held.size());
        assertEquals(3, seqOf(held.get(0)));

        Reply done = fetch(url, gateway, "epoch=" + epoch + "&after=3");
        assertTrue(done.ops().isEmpty());
        assertTrue(held(url).isEmpty());
        // Numbering goes on in the same epoch.
        assertEquals(4L, ((JsonValue.Integer) post(url, source, batch(pin("d"))).json().fields().get("last_seq")).value());

        Reply tooFar = fetch(url, gateway, "epoch=" + epoch + "&after=5");
        assertEquals(400, tooFar.status());
        assertEquals("invalid_query", tooFar.error());
    }

    @Test
    void refusesABadQuery() throws Exception {
        for (String query : List.of("after=1&after=1", "x=1", "after", "after=-1", "after=1.5", "after=", "after=9007199254740992",
                "limit=0", "limit=101", "limit=a", "wait=31", "wait=-1", "epoch=short", "epoch=AAAAAAAAAAAAAAAA!", "epoch=" + "A".repeat(65),
                "epoch=AAAAAAAAAAAAAAAA&epoch=AAAAAAAAAAAAAAAA", "&", "wait=1&")) {
            Reply reply = fetch(url, gateway, query);
            assertEquals(400, reply.status(), query);
            assertEquals("invalid_query", reply.error(), query);
        }
        for (String query : List.of("after=9007199254740991", "limit=1", "limit=100", "wait=0", "epoch=" + "A".repeat(64))) {
            assertEquals(200, fetch(url, gateway, query).status(), query);
        }
    }

    @Test
    void givesPagesByLimitAndByFourMebibytes() throws Exception {
        post(url, source, batch(pin("a"), pin("b"), pin("c"), pin("d"), pin("e")));
        Reply page = fetch(url, gateway, "limit=2");
        assertEquals(List.of(1L, 2L), page.ops().stream().map(RelayConformanceTest::seqOf).toList());
        String epoch = page.epoch();
        page = fetch(url, gateway, "epoch=" + epoch + "&after=2&limit=2");
        assertEquals(List.of(3L, 4L), page.ops().stream().map(RelayConformanceTest::seqOf).toList());
        page = fetch(url, gateway, "epoch=" + epoch + "&after=4&limit=2");
        assertEquals(List.of(5L), page.ops().stream().map(RelayConformanceTest::seqOf).toList());
        page = fetch(url, gateway, "epoch=" + epoch + "&after=5");
        assertTrue(page.ops().isEmpty());

        // Four photos of a mebibyte each are about 5.6 MB of JSON: no answer holds them all.
        for (int index = 0; index < 4; index++) {
            assertEquals(200, post(url, source, batch(photo("big" + index, 1_048_576))).status());
        }
        long after = 5;
        List<Long> seen = new ArrayList<>();
        int pages = 0;
        while (seen.size() < 4) {
            page = fetch(url, gateway, "epoch=" + epoch + "&after=" + after);
            assertTrue(page.body().length <= 4 * 1024 * 1024, "an answer of " + page.body().length + " bytes");
            assertFalse(page.ops().isEmpty());
            for (JsonValue entry : page.ops()) {
                seen.add(seqOf(entry));
                after = seqOf(entry);
            }
            pages += 1;
        }
        assertEquals(List.of(6L, 7L, 8L, 9L), seen);
        assertTrue(pages >= 2);
    }

    @Test
    void holdsAFetchOpenUntilSomethingArrives() throws Exception {
        String epoch = fetch(url, gateway, "").epoch();
        long started = System.nanoTime();
        Reply empty = fetch(url, gateway, "epoch=" + epoch + "&after=0&wait=1");
        long waited = (System.nanoTime() - started) / 1_000_000;
        assertTrue(empty.ops().isEmpty());
        assertTrue(waited >= 900, "answered after " + waited + " ms");

        started = System.nanoTime();
        CompletableFuture<HttpResponse<byte[]>> waiting = HTTP.sendAsync(
                request(url + "/v1/ops?epoch=" + epoch + "&after=0&wait=20", gateway).GET().build(), HttpResponse.BodyHandlers.ofByteArray());
        Thread.sleep(300);
        // A source is not held up by the waiting fetch.
        assertEquals(200, post(url, source, batch(pin("a"))).status());
        HttpResponse<byte[]> answer = waiting.get();
        waited = (System.nanoTime() - started) / 1_000_000;
        assertTrue(waited < 10_000, "answered after " + waited + " ms");
        Reply reply = new Reply(answer.statusCode(), answer.headers(), answer.body());
        assertEquals(1, reply.ops().size());
    }

    @Test
    void answersAtOnceWhenTooManyFetchesAreWaiting() throws Exception {
        String epoch = fetch(url, gateway, "").epoch();
        List<CompletableFuture<HttpResponse<byte[]>>> waiting = new ArrayList<>();
        for (int index = 0; index < 4; index++) {
            waiting.add(HTTP.sendAsync(request(url + "/v1/ops?epoch=" + epoch + "&after=0&wait=3", gateway).GET().build(),
                    HttpResponse.BodyHandlers.ofByteArray()));
        }
        Thread.sleep(500);
        long started = System.nanoTime();
        assertEquals(200, fetch(url, gateway, "epoch=" + epoch + "&after=0&wait=3").status());
        assertTrue((System.nanoTime() - started) / 1_000_000 < 2000);
        for (CompletableFuture<HttpResponse<byte[]>> each : waiting) {
            assertEquals(200, each.get().statusCode());
        }
    }

    // Limits of the queue, and time

    @Test
    void refusesABatchThatWouldOverfillTheQueue() throws Exception {
        InProcessRelay small = start(List.of("--listen", "127.0.0.1:0"), 3, 64L * 1024 * 1024);
        assertEquals(200, post(small.url, source, batch(pin("a"), pin("b"))).status());
        Reply full = post(small.url, source, batch(pin("c"), pin("d")));
        assertEquals(503, full.status());
        assertEquals("queue_full", full.error());
        assertEquals("30", full.headers().firstValue("Retry-After").orElse(""));
        assertEquals(2, held(small.url).size());
        assertEquals(200, post(small.url, source, batch(pin("c"))).status());
        String epoch = fetch(small.url, gateway, "").epoch();
        fetch(small.url, gateway, "epoch=" + epoch + "&after=3");
        assertEquals(200, post(small.url, source, batch(pin("e"), pin("f"), pin("g"))).status());

        // The size of the operations counts too, as their JSON.
        int pinBytes = pin("a").getBytes(StandardCharsets.UTF_8).length;
        InProcessRelay narrow = start(List.of("--listen", "127.0.0.1:0"), 10_000, pinBytes * 2L);
        assertEquals(200, post(narrow.url, source, batch(pin("a"), pin("b"))).status());
        assertEquals(503, post(narrow.url, source, batch(pin("c"))).status());
    }

    @Test
    void dropsWhatWaitedTooLongAndSaysSo() throws Exception {
        InProcessRelay hourly = start(List.of("--listen", "127.0.0.1:0", "--keep-hours", "1"), 10_000, 64L * 1024 * 1024);
        String relayUrl = hourly.url;
        // Done with before it expires: does not count as missed.
        post(relayUrl, source, batch(pin("a")));
        String epoch = fetch(relayUrl, gateway, "").epoch();
        assertFalse(fetch(relayUrl, gateway, "epoch=" + epoch + "&after=1").flag("missed"));

        post(relayUrl, source, batch(pin("b"), pin("c")));
        offset.addAndGet(HOUR - 60_000);
        Reply young = fetch(relayUrl, gateway, "epoch=" + epoch + "&after=1");
        assertFalse(young.flag("missed"));
        assertEquals(2, young.ops().size());

        offset.addAndGet(120_000);
        Reply old = fetch(relayUrl, gateway, "epoch=" + epoch + "&after=1");
        assertTrue(old.flag("missed"));
        assertTrue(old.ops().isEmpty());
        // Above 0 when the epoch does not match.
        assertTrue(fetch(relayUrl, gateway, "").flag("missed"));

        post(relayUrl, source, batch(pin("d")));
        Reply after = fetch(relayUrl, gateway, "epoch=" + epoch + "&after=1");
        assertTrue(after.flag("missed"));
        assertEquals(List.of(4L), after.ops().stream().map(RelayConformanceTest::seqOf).toList());
        // Once the gateway is past them, the expired ones are no longer news.
        assertFalse(fetch(relayUrl, gateway, "epoch=" + epoch + "&after=4").flag("missed"));
    }

    @Test
    void writesOneLineForEachRequest() throws Exception {
        int before = logBytes.toString(StandardCharsets.UTF_8).split("\n", -1).length;
        post(url, source, batch(pin("drone-1")));
        fetch(url, gateway, "");
        send(request(url + "/v1/health", null).GET().build());
        send(request(url + "/elsewhere?token=" + source, null).GET().build());
        Thread.sleep(200);
        String[] lines = logBytes.toString(StandardCharsets.UTF_8).split("\n", -1);
        assertEquals(before + 4, lines.length);
        assertTrue(lines[lines.length - 2].endsWith(" GET (other) 404"), lines[lines.length - 2]);
    }

    @Test
    void refusesStartingWithoutGoodTokens() throws Exception {
        String good = token();
        for (Map<String, String> env : List.of(
                Map.<String, String>of(),
                Map.of("ERDIGO_RELAY_GATEWAY_TOKEN", good),
                Map.of("ERDIGO_RELAY_SOURCE_TOKENS", good),
                relayEnvironment("short", good),
                relayEnvironment(source + ",", good),
                relayEnvironment(source + ", " + otherSource, good),
                relayEnvironment(source + "!", good),
                relayEnvironment("a".repeat(257), good),
                relayEnvironment(source, "short"),
                relayEnvironment(source + "," + good, good))) {
            Exception error = org.junit.jupiter.api.Assertions.assertThrows(Exception.class,
                    () -> InProcessRelay.start(List.of("--listen", "127.0.0.1:0"), env, System::currentTimeMillis, 10, 10, log));
            assertEquals("UsageError", error.getClass().getSimpleName());
            assertFalse(error.getMessage().contains(source) || error.getMessage().contains(good));
        }
        // 32 and 256 characters, from the whole alphabet.
        String shortest = "Aa0-_.~" + "b".repeat(25);
        String longest = "c".repeat(256);
        InProcessRelay edges = InProcessRelay.start(List.of("--listen", "127.0.0.1:0"), relayEnvironment(shortest, longest),
                System::currentTimeMillis, 10, 10_000, log);
        started.add(edges);
        assertEquals(200, post(edges.url, shortest, batch(pin("a"))).status());
        assertEquals(200, fetch(edges.url, longest, "").status());
    }
}
