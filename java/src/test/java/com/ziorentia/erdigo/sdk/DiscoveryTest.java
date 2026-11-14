package com.ziorentia.erdigo.sdk;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.io.ByteArrayOutputStream;
import java.net.InetAddress;
import java.net.InterfaceAddress;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.Random;
import org.junit.jupiter.api.Test;

class DiscoveryTest {
    private static void name(ByteArrayOutputStream out, String dotted) {
        for (String label : dotted.split("\\.")) {
            byte[] bytes = label.getBytes(StandardCharsets.UTF_8);
            out.write(bytes.length);
            out.writeBytes(bytes);
        }
        out.write(0);
    }

    private static void u16(ByteArrayOutputStream out, int value) {
        out.write(value >> 8);
        out.write(value);
    }

    private static void record(ByteArrayOutputStream out, byte[] owner, int type, long ttl, byte[] data) {
        out.writeBytes(owner);
        u16(out, type);
        u16(out, 1);
        u16(out, (int) (ttl >> 16));
        u16(out, (int) ttl);
        u16(out, data.length);
        out.writeBytes(data);
    }

    private static byte[] encoded(String dotted) {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        name(out, dotted);
        return out.toByteArray();
    }

    /** An answer as a receiver's responder gives it: the instance, its service and the host's address. */
    static byte[] answer(int id, String instance, int port, byte[] address) {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        u16(out, id);
        u16(out, 0x8400);
        u16(out, 0);
        u16(out, 1);
        u16(out, 0);
        u16(out, 2);
        record(out, encoded("_erdigo-int._tcp.local"), Dns.TYPE_PTR, 120, encoded(instance + "._erdigo-int._tcp.local"));
        ByteArrayOutputStream srv = new ByteArrayOutputStream();
        u16(srv, 0);
        u16(srv, 0);
        u16(srv, port);
        name(srv, "host-1.local");
        record(out, encoded(instance + "._erdigo-int._tcp.local"), Dns.TYPE_SRV, 120, srv.toByteArray());
        record(out, encoded("host-1.local"), Dns.TYPE_A, 120, address);
        return out.toByteArray();
    }

    @Test
    void queryAsksForTheService() {
        byte[] query = Dns.encodeQuery(0x1234, List.of(new Dns.Question(Dns.SERVICE_NAME, Dns.TYPE_PTR)));
        ByteArrayOutputStream expected = new ByteArrayOutputStream();
        u16(expected, 0x1234);
        u16(expected, 0);
        u16(expected, 1);
        u16(expected, 0);
        u16(expected, 0);
        u16(expected, 0);
        name(expected, "_erdigo-int._tcp.local");
        u16(expected, Dns.TYPE_PTR);
        u16(expected, 1);
        assertEquals(java.util.HexFormat.of().formatHex(expected.toByteArray()), java.util.HexFormat.of().formatHex(query));
    }

    @Test
    void answerLeadsToAnEndpoint() {
        byte[] data = answer(77, "Erdigo 7F3A", 4242, new byte[] {(byte) 192, (byte) 168, 1, 20});
        Dns.Message message = Dns.decode(data, data.length);
        assertNotNull(message);
        assertTrue(message.isResponse());
        assertEquals(77, message.id());
        Discovery.DiscoveredServices found = new Discovery.DiscoveredServices(8);
        found.add(message.records());
        assertEquals(List.of(new ReceiverEndpoint("192.168.1.20", 4242)), found.endpoints());
    }

    @Test
    void compressedNamesAreFollowedBackwards() {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        u16(out, 1);
        u16(out, 0x8400);
        u16(out, 0);
        u16(out, 1);
        u16(out, 0);
        u16(out, 0);
        int serviceAt = out.size();
        out.writeBytes(encoded("_erdigo-int._tcp.local"));
        u16(out, Dns.TYPE_PTR);
        u16(out, 1);
        u16(out, 0);
        u16(out, 120);
        // "Receiver" followed by a pointer back to the service name.
        byte[] label = "Receiver".getBytes(StandardCharsets.US_ASCII);
        u16(out, 1 + label.length + 2);
        out.write(label.length);
        out.writeBytes(label);
        out.write(0xC0);
        out.write(serviceAt);
        byte[] data = out.toByteArray();
        Dns.Message message = Dns.decode(data, data.length);
        assertNotNull(message);
        assertEquals(List.of("Receiver", "_erdigo-int", "_tcp", "local"), message.records().get(0).target().labels());
    }

    @Test
    void malformedAnswersAreRefusedWithoutReadingOutsideThem() {
        byte[] good = answer(5, "Erdigo", 4242, new byte[] {10, 0, 0, 2});
        // Every truncation fails cleanly.
        for (int length = 0; length < good.length; length++) {
            assertNull(Dns.decode(good, length), "length " + length);
        }
        // A pointer to itself, or forwards, is refused.
        byte[] loop = {0, 1, (byte) 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0, (byte) 0xC0, 12, 0, 12, 0, 1, 0, 0, 0, 1, 0, 0};
        assertNull(Dns.decode(loop, loop.length));
        // Random bytes never break the reader.
        Random random = new Random(1);
        for (int index = 0; index < 20000; index++) {
            byte[] noise = good.clone();
            noise[random.nextInt(noise.length)] = (byte) random.nextInt(256);
            noise[random.nextInt(noise.length)] = (byte) random.nextInt(256);
            Dns.decode(noise, noise.length);
        }
    }

    @Test
    void whatIsKeptStaysBounded() {
        Discovery.DiscoveredServices found = new Discovery.DiscoveredServices(8);
        for (int index = 0; index < 500; index++) {
            byte[] data = answer(1, "Receiver " + index, 1000 + index, new byte[] {10, 0, (byte) (index >> 8), (byte) index});
            found.add(Dns.decode(data, data.length).records());
        }
        assertEquals(8, found.instances().size());
        assertTrue(found.keptRecords() <= 8 * (2 + Discovery.DiscoveredServices.ADDRESSES_PER_HOST), "kept " + found.keptRecords());
        found.closeInstances();
        byte[] late = answer(1, "Latecomer", 9999, new byte[] {10, 9, 9, 9});
        found.add(Dns.decode(late, late.length).records());
        assertFalse(found.endpoints().stream().anyMatch(endpoint -> endpoint.port() == 9999));
    }

    @Test
    void onlyAnswersFromTheLocalNetworkCount() throws Exception {
        List<InterfaceAddress> none = new ArrayList<>();
        assertTrue(Discovery.isLocal(InetAddress.getByName("169.254.10.20"), none));
        assertFalse(Discovery.isLocal(InetAddress.getByName("8.8.8.8"), none));
        assertFalse(Discovery.isLocal(InetAddress.getByName("::1"), none));
    }

    @Test
    void anAddressGivenIsTheOnlyOneTried() {
        ReceiverPairing pairing = new ReceiverPairing("home", new byte[32], List.of(), "10.0.0.5", 4000, null, 0);
        assertEquals(List.of(new ReceiverEndpoint("10.0.0.9", 5000)), Discovery.knownEndpoints(ReceiverTarget.address("10.0.0.9", 5000), pairing));
        assertEquals(List.of(new ReceiverEndpoint("10.0.0.5", 4000)), Discovery.knownEndpoints(ReceiverTarget.discover(), pairing));
    }
}
