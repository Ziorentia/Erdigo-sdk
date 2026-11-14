package com.ziorentia.erdigo.sdk;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;
import static org.junit.jupiter.api.Assertions.fail;
import static org.junit.jupiter.api.Assumptions.assumeTrue;

import com.ziorentia.erdigo.core.Coordinate;
import com.ziorentia.erdigo.core.IntegrationV1;
import com.ziorentia.erdigo.core.ItemKind;
import com.ziorentia.erdigo.core.MarkerColor;
import com.ziorentia.erdigo.core.PairingTicket;
import com.ziorentia.erdigo.core.TrackPoint;
import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.TimeUnit;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import java.util.zip.CRC32;
import java.util.zip.Deflater;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

/**
 * Pairs with and writes every kind of item to a mock receiver from another SDK in this repository, run as a separate
 * process: {@code ERDIGO_MOCK} names its {@code erdigo-send} executable (the .NET one in CI, the Swift one on a Mac).
 * Without it these tests are skipped, unless {@code ERDIGO_REQUIRE_MOCK=1}, which CI sets so that they cannot be
 * skipped by accident.
 */
class MockInteropTest {
    private static final Coordinate PALACE = new Coordinate(37.5796, 126.977);

    @TempDir
    Path temporary;

    private Process mock;
    private OutputStream commands;
    private final List<String> output = new ArrayList<>();
    private int port;
    private Path codeFile;

    @BeforeEach
    void startMock() throws Exception {
        String executable = System.getenv("ERDIGO_MOCK");
        if (executable == null || executable.isEmpty()) {
            if ("1".equals(System.getenv("ERDIGO_REQUIRE_MOCK"))) {
                fail("ERDIGO_REQUIRE_MOCK is set, but ERDIGO_MOCK does not name a mock receiver");
            }
            assumeTrue(false, "ERDIGO_MOCK is not set");
        }
        // The mock writes the code file only into a folder only its owner can read.
        Path folder = temporary.resolve("mock");
        PrivateFile.ensurePrivateDirectory(folder);
        codeFile = folder.resolve("code");
        mock = new ProcessBuilder(executable, "serve", "--mock", "--code-file", codeFile.toString()).redirectErrorStream(true).start();
        commands = mock.getOutputStream();
        Thread reader = new Thread(() -> {
            try (BufferedReader lines = new BufferedReader(new InputStreamReader(mock.getInputStream(), StandardCharsets.UTF_8))) {
                String line;
                while ((line = lines.readLine()) != null) {
                    synchronized (output) {
                        output.add(line);
                        output.notifyAll();
                    }
                }
            } catch (IOException ended) {
                // The mock was stopped.
            }
        });
        reader.setDaemon(true);
        reader.start();
        String ready = waitFor(line -> line.startsWith("Type 'help'"), Duration.ofSeconds(120));
        assertNotNull(ready, "the mock did not start: " + snapshot());
        for (String line : snapshot()) {
            Matcher matcher = Pattern.compile("port (\\d+)").matcher(line);
            if (matcher.find()) {
                port = Integer.parseInt(matcher.group(1));
            }
        }
        assertTrue(port > 0, "no port in " + snapshot());
    }

    @AfterEach
    void stopMock() throws Exception {
        if (mock != null) {
            mock.destroy();
            if (!mock.waitFor(10, TimeUnit.SECONDS)) {
                mock.destroyForcibly();
            }
        }
    }

    private List<String> snapshot() {
        synchronized (output) {
            return List.copyOf(output);
        }
    }

    private interface LineTest {
        boolean matches(String line);
    }

    /** Waits for a line, counting only lines after the first {@code skip}. */
    private String waitFor(LineTest test, Duration time, int skip) throws InterruptedException {
        long deadline = System.nanoTime() + time.toNanos();
        synchronized (output) {
            while (true) {
                for (int index = skip; index < output.size(); index++) {
                    if (test.matches(output.get(index))) {
                        return output.get(index);
                    }
                }
                long remaining = deadline - System.nanoTime();
                if (remaining <= 0) {
                    return null;
                }
                output.wait(Math.max(1, remaining / 1_000_000));
            }
        }
    }

    private String waitFor(LineTest test, Duration time) throws InterruptedException {
        return waitFor(test, time, 0);
    }

    private void command(String line) throws IOException {
        commands.write((line + "\n").getBytes(StandardCharsets.UTF_8));
        commands.flush();
    }

    private PairingTicket readTicket() throws Exception {
        return PairingTicket.parse(new String(PrivateFile.readAllBytes(codeFile, 1024), StandardCharsets.UTF_8));
    }

    /** Asks the mock for its items and returns the lines it prints. */
    private List<String> items() throws Exception {
        int before = snapshot().size();
        command("items 1");
        command("help");
        assertNotNull(waitFor(line -> line.startsWith("code | devices"), Duration.ofSeconds(10), before));
        List<String> lines = snapshot().subList(before, snapshot().size());
        List<String> items = new ArrayList<>();
        for (String line : lines) {
            if (line.matches("^(pin|marker|note|photo|track) .*")) {
                items.add(line);
            }
        }
        return items;
    }

    /** A real PNG of 2 by 2 pixels, which the mock checks by its structure. */
    static byte[] tinyPng() throws IOException {
        java.io.ByteArrayOutputStream png = new java.io.ByteArrayOutputStream();
        png.write(new byte[] {(byte) 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A});
        java.nio.ByteBuffer header = java.nio.ByteBuffer.allocate(13).putInt(2).putInt(2).put((byte) 8).put((byte) 2).put((byte) 0).put((byte) 0).put((byte) 0);
        chunk(png, "IHDR", header.array());
        byte[] raw = new byte[2 * (1 + 2 * 3)];
        for (int row = 0; row < 2; row++) {
            for (int pixel = 0; pixel < 2; pixel++) {
                raw[row * 7 + 1 + pixel * 3] = (byte) 0xFF;
            }
        }
        Deflater deflater = new Deflater();
        deflater.setInput(raw);
        deflater.finish();
        byte[] compressed = new byte[256];
        int length = deflater.deflate(compressed);
        deflater.end();
        chunk(png, "IDAT", java.util.Arrays.copyOf(compressed, length));
        chunk(png, "IEND", new byte[0]);
        return png.toByteArray();
    }

    private static void chunk(java.io.ByteArrayOutputStream png, String type, byte[] data) throws IOException {
        byte[] name = type.getBytes(StandardCharsets.US_ASCII);
        png.write(java.nio.ByteBuffer.allocate(4).putInt(data.length).array());
        png.write(name);
        png.write(data);
        CRC32 crc = new CRC32();
        crc.update(name);
        crc.update(data);
        png.write(java.nio.ByteBuffer.allocate(4).putInt((int) crc.getValue()).array());
    }

    @Test
    void pairsWithTheQrTextAndWritesEveryKindOfItem() throws Exception {
        PairingTicket ticket = readTicket();
        assertNotNull(ticket.fingerprint());
        FileDeviceStore store = new FileDeviceStore(temporary.resolve("state"));
        try (DeviceIdentity identity = store.loadOrCreateIdentity()) {
            ReceiverTarget target = ReceiverTarget.address("127.0.0.1", port);
            ReceiverPairing pairing = Pairing.pair(target, ticket, identity, "Java interop", List.of(ItemKind.values()), new Pairing.Options());
            store.save(pairing);
            assertEquals(List.of(ItemKind.values()), pairing.kinds());
            assertEquals(ticket.fingerprint(), pairing.fingerprint());
            assertNotNull(waitFor(line -> line.startsWith("[mock] paired "), Duration.ofSeconds(10)));

            try (ErdigoClient client = new ErdigoClient(identity, pairing, store, target, RetryPolicy.upTo(Duration.ofSeconds(10)))) {
                client.ping();
                client.putPin("gate", PALACE, "Palace gate");
                client.putMarker("tower", new Coordinate(48.8584, 2.2945), "Tower", MarkerColor.BLUE);
                client.putNote("meet", PALACE, "Meet at the gate");
                client.putPhoto("view", new Coordinate(37.5, 127.0), tinyPng(), "View");
                client.appendTrack("walk", List.of(new TrackPoint(PALACE, 1_700_000_000_000L, 38.5), new TrackPoint(new Coordinate(37.58, 126.98))), "Walk");
                client.finishTrack("walk");
                client.move("gate", new Coordinate(37.58, 126.98));
                List<String> items = items();
                assertTrue(items.contains("pin gate at 37.58,126.98 title=Palace gate points=0"), items.toString());
                assertTrue(items.contains("marker tower at 48.8584,2.2945 title=Tower points=0"), items.toString());
                assertTrue(items.contains("note meet at 37.5796,126.977 title=- points=0"), items.toString());
                assertTrue(items.contains("photo view at 37.5,127.0 title=View points=0"), items.toString());
                assertTrue(items.contains("track walk at -,- title=Walk points=2"), items.toString());

                // Finished tracks take no more points, and the mock says so with an error this SDK reports.
                ErdigoException finished = assertThrows(ErdigoException.class,
                        () -> client.appendTrack("walk", List.of(new TrackPoint(PALACE))));
                assertEquals("invalid_state", finished.receiverError().code());
                ErdigoException missing = assertThrows(ErdigoException.class, () -> client.move("missing", PALACE));
                assertEquals("not_found", missing.receiverError().code());

                client.delete("meet");
                assertFalse(items().stream().anyMatch(line -> line.startsWith("note meet")));
                client.deleteAll();
                assertTrue(items().isEmpty());
            }

            // Removed on the receiver, the device meets silence.
            command("remove 1");
            assertNotNull(waitFor(line -> line.startsWith("[mock] removed "), Duration.ofSeconds(10)));
            try (ErdigoClient client = new ErdigoClient(identity, pairing, store, target, RetryPolicy.none())) {
                assertEquals(ErdigoException.Kind.NO_ANSWER, assertThrows(ErdigoException.class, client::ping).kind());
            }
        }
    }

    @Test
    void typedCodeShowsTheMocksKeyAndPairsOnceConfirmed() throws Exception {
        PairingTicket qr = readTicket();
        PairingTicket typed = PairingTicket.parse(qr.code().display());
        String expected = IntegrationV1.shortFingerprint(qr.fingerprint());
        try (DeviceIdentity identity = DeviceIdentity.generate()) {
            ReceiverTarget target = ReceiverTarget.address("127.0.0.1", port);
            List<String> shown = new ArrayList<>();
            // Answered no first: the mock hears no request, and the code stays good.
            assertEquals(ErdigoException.Kind.KEY_NOT_CONFIRMED, assertThrows(ErdigoException.class,
                    () -> Pairing.pair(target, typed, identity, "Typed", List.of(ItemKind.PIN), key -> shown.add(key) && false)).kind());
            assertFalse(snapshot().stream().anyMatch(line -> line.startsWith("[mock] pairing request")), snapshot().toString());
            ReceiverPairing pairing = Pairing.pair(target, typed, identity, "Typed", List.of(ItemKind.PIN), key -> shown.add(key));
            assertEquals(List.of(expected, expected), shown);
            assertEquals(qr.fingerprint(), pairing.fingerprint());
            InMemoryDeviceStore store = new InMemoryDeviceStore(identity);
            store.save(pairing);
            try (ErdigoClient client = new ErdigoClient(identity, pairing, store, target, RetryPolicy.none())) {
                client.putPin("typed", PALACE);
                ErdigoException error = assertThrows(ErdigoException.class, () -> client.putNote("n", PALACE, "not allowed"));
                assertEquals(ErdigoException.Kind.NOT_PERMITTED, error.kind());
            }
        }
    }

    /**
     * Finds a mock that advertises itself on the local network, over real multicast DNS, and pairs with it. Networks
     * that drop multicast cannot run this, so it runs only with {@code ERDIGO_MULTICAST_TESTS=1}. It starts a second
     * mock, which listens on the local network and allows every request, for as long as the test runs.
     */
    @Test
    void findsAnAdvertisedMockOnTheLocalNetwork() throws Exception {
        assumeTrue("1".equals(System.getenv("ERDIGO_MULTICAST_TESTS")), "ERDIGO_MULTICAST_TESTS is not set");
        Path folder = temporary.resolve("lan");
        PrivateFile.ensurePrivateDirectory(folder);
        Path lanCode = folder.resolve("code");
        Process lan = new ProcessBuilder(System.getenv("ERDIGO_MOCK"), "serve", "--mock", "--lan", "--bonjour", "--auto-approve",
                "--code-file", lanCode.toString()).redirectErrorStream(true).redirectOutput(ProcessBuilder.Redirect.DISCARD).start();
        try {
            long deadline = System.nanoTime() + Duration.ofSeconds(60).toNanos();
            while (!Files.exists(lanCode) && System.nanoTime() < deadline) {
                Thread.sleep(100);
            }
            PairingTicket ticket = PairingTicket.parse(new String(PrivateFile.readAllBytes(lanCode, 1024), StandardCharsets.UTF_8));
            List<ReceiverEndpoint> found = Discovery.browse(3000, 8);
            assertFalse(found.isEmpty(), "nothing found on the local network");
            try (DeviceIdentity identity = DeviceIdentity.generate()) {
                ReceiverPairing pairing = Pairing.pair(ReceiverTarget.discover(), ticket, identity, "Found", List.of(ItemKind.PIN), new Pairing.Options());
                assertEquals(ticket.fingerprint(), pairing.fingerprint());
                InMemoryDeviceStore store = new InMemoryDeviceStore(identity);
                store.save(pairing.withAddress(null, null));
                try (ErdigoClient client = new ErdigoClient(identity, pairing.withAddress(null, null), store, ReceiverTarget.discover(), RetryPolicy.none())) {
                    client.putPin("found", PALACE);
                }
            }
        } finally {
            lan.destroy();
            lan.waitFor(10, TimeUnit.SECONDS);
        }
    }

    @Test
    void wrongFingerprintStopsBeforeTheMockHearsARequest() throws Exception {
        PairingTicket qr = readTicket();
        PairingTicket wrong = new PairingTicket(qr.code(), "0".repeat(64));
        try (DeviceIdentity identity = DeviceIdentity.generate()) {
            assertEquals(ErdigoException.Kind.FINGERPRINT_MISMATCH, assertThrows(ErdigoException.class,
                    () -> Pairing.pair(ReceiverTarget.address("127.0.0.1", port), wrong, identity, "Wrong", List.of(ItemKind.PIN), new Pairing.Options())).kind());
        }
        Thread.sleep(300);
        assertFalse(snapshot().stream().anyMatch(line -> line.startsWith("[mock] pairing request")), snapshot().toString());
        assertTrue(Files.exists(codeFile));
    }
}
