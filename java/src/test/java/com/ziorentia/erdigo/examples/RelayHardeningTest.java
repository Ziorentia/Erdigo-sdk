package com.ziorentia.erdigo.examples;

import static com.ziorentia.erdigo.examples.CloudRelayExample.HTTP;
import static com.ziorentia.erdigo.examples.CloudRelayExample.RELAY;
import static com.ziorentia.erdigo.examples.CloudRelayExample.discard;
import static com.ziorentia.erdigo.examples.CloudRelayExample.launch;
import static com.ziorentia.erdigo.examples.CloudRelayExample.post;
import static com.ziorentia.erdigo.examples.CloudRelayExample.relayEnvironment;
import static com.ziorentia.erdigo.examples.CloudRelayExample.request;
import static com.ziorentia.erdigo.examples.CloudRelayExample.token;
import static com.ziorentia.erdigo.examples.CloudRelayExample.waitForLine;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.ziorentia.erdigo.examples.CloudRelayExample.InProcessRelay;
import java.net.Socket;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;
import org.junit.jupiter.api.Test;

/** What slow or heavy requests cost the example relay: neither may take the whole relay down. */
class RelayHardeningTest {
    private final String source = token();
    private final String gateway = token();

    private static HttpRequest health(String url) {
        return request(url + "/v1/health", null).timeout(Duration.ofSeconds(10)).GET().build();
    }

    @Test
    void answersOthersWhileClientsSendTheirHeadersSlowly() throws Exception {
        try (InProcessRelay relay = InProcessRelay.start(relayEnvironment(source, gateway), discard())) {
            // Each holds a worker while the server waits for the rest of its headers: more than a small pool has.
            List<Socket> slow = new ArrayList<>();
            try {
                for (int index = 0; index < 40; index++) {
                    Socket socket = new Socket("127.0.0.1", relay.port);
                    slow.add(socket);
                    socket.getOutputStream().write("GET /v1/health HTTP/1.1\r\nHost: relay\r\n".getBytes(StandardCharsets.US_ASCII));
                    socket.getOutputStream().flush();
                }
                Thread.sleep(500);
                long started = System.nanoTime();
                HttpResponse<String> answer = HTTP.send(health(relay.url), HttpResponse.BodyHandlers.ofString());
                long waited = (System.nanoTime() - started) / 1_000_000;
                assertEquals(200, answer.statusCode());
                assertTrue(waited < 5_000, "health answered after " + waited + " ms");
                assertEquals(200, post(relay.url, source, "{\"ops\":[{\"op\":\"delete\",\"id\":\"a\"}]}").status());
            } finally {
                for (Socket socket : slow) {
                    socket.close();
                }
            }
        }
    }

    /** The relay as a program, with the heap a small server gives it by default. */
    @Test
    void keepsWithinASmallHeapWhenManyLargeBodiesArriveAtOnce() throws Exception {
        // Not in a folder the test must remove: on Windows a stopped program can hold its output file a little longer.
        Path output = Files.createTempFile("erdigo-relay-hardening", ".txt");
        Process process = launch(List.of("-Xmx256m"), RELAY, List.of("--listen", "127.0.0.1:0"),
                relayEnvironment(source, gateway), output);
        try {
            String line = waitForLine(output, "Relay listening on ", Duration.ofSeconds(60));
            assertNotNull(line, "the relay did not start: " + Files.readString(output));
            String url = line.substring("Relay listening on ".length());

            // Exactly 2 MiB of empty objects, which make a large tree for their size: about 75 MB each.
            String text = "{\"ops\":[[" + "{},".repeat(699_046) + "{}]]}";
            byte[] body = text.getBytes(StandardCharsets.US_ASCII);
            assertEquals(2_097_152, body.length);

            List<CompletableFuture<HttpResponse<String>>> sent = new ArrayList<>();
            for (int index = 0; index < 32; index++) {
                sent.add(HTTP.sendAsync(request(url + "/v1/ops", source).header("Content-Type", "application/json")
                        .POST(HttpRequest.BodyPublishers.ofByteArray(body)).build(), HttpResponse.BodyHandlers.ofString()));
            }
            int checked = 0;
            for (CompletableFuture<HttpResponse<String>> each : sent) {
                HttpResponse<String> answer = each.get(120, TimeUnit.SECONDS);
                // Checked and refused, or asked to come back later when too many arrive at once; never dropped.
                if (answer.statusCode() == 400) {
                    assertTrue(answer.body().contains("\"invalid_op\""), answer.body());
                    checked += 1;
                } else {
                    assertEquals(503, answer.statusCode(), answer.body());
                    assertEquals("30", answer.headers().firstValue("Retry-After").orElse(""));
                }
            }
            assertTrue(checked > 0, "no body was checked");
            assertEquals(200, HTTP.send(health(url), HttpResponse.BodyHandlers.ofString()).statusCode());
            assertEquals(200, post(url, source, "{\"ops\":[{\"op\":\"delete\",\"id\":\"a\"}]}").status());

            String written = Files.readString(output, StandardCharsets.UTF_8);
            assertFalse(written.contains("OutOfMemoryError"), written);
            assertFalse(written.contains("aborted"), written);
            assertTrue(process.isAlive(), written);
        } finally {
            process.destroy();
            if (!process.waitFor(15, TimeUnit.SECONDS)) {
                process.destroyForcibly().waitFor(15, TimeUnit.SECONDS);
            }
            deleteWhenLetGo(output);
        }
    }

    /** Removes a file once no program holds it, trying for a few seconds; one that is still held goes when the tests end. */
    private static void deleteWhenLetGo(Path file) throws InterruptedException {
        for (int attempt = 0; attempt < 50; attempt++) {
            try {
                Files.deleteIfExists(file);
                return;
            } catch (java.io.IOException held) {
                Thread.sleep(100);
            }
        }
        file.toFile().deleteOnExit();
    }
}
