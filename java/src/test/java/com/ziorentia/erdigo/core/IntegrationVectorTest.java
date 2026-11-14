package com.ziorentia.erdigo.core;

import static com.ziorentia.erdigo.core.VectorFiles.array;
import static com.ziorentia.erdigo.core.VectorFiles.bytes;
import static com.ziorentia.erdigo.core.VectorFiles.load;
import static com.ziorentia.erdigo.core.VectorFiles.text;
import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.ziorentia.erdigo.core.Messages.Hello;
import com.ziorentia.erdigo.core.Messages.PairRequest;
import com.ziorentia.erdigo.core.Messages.PairStatus;
import com.ziorentia.erdigo.core.Messages.ReceiverMessage;
import com.ziorentia.erdigo.core.Messages.RequestEnvelope;
import com.ziorentia.erdigo.core.Messages.Welcome;
import java.nio.charset.StandardCharsets;
import java.util.List;
import org.junit.jupiter.api.Test;

/** Every integration-v1 vector in spec/vectors/integration-v1, byte for byte. */
class IntegrationVectorTest {
    private static void replay(String name, NoisePattern pattern, boolean psk) {
        JsonValue.Object vector = load("spec/vectors/integration-v1/" + name);
        assertEquals(text(vector, "protocol_name"), pattern.protocolName());
        byte[] prologue = bytes(vector, "preamble_and_prologue");
        List<byte[]> psks = psk ? List.of(bytes(vector, "psk")) : List.of();
        if (psk) {
            PairingCode code = new PairingCode(text(vector, "pairing_code_as_typed"));
            assertEquals(text(vector, "pairing_code_canonical"), code.canonical());
            assertEquals(Hex.encode(psks.get(0)), Hex.encode(code.presharedKey()));
            PairingTicket ticket = PairingTicket.parse(text(vector, "qr_text"));
            assertEquals(code, ticket.code());
            assertEquals(IntegrationV1.fingerprint(bytes(vector, "receiver_static_public")), ticket.fingerprint());
            assertEquals(text(vector, "qr_text"), ticket.uri());
        }
        assertEquals(text(vector, "device_static_public"), Hex.encode(Noise.publicKey(bytes(vector, "device_static_private"))));
        assertEquals(text(vector, "receiver_static_public"), Hex.encode(Noise.publicKey(bytes(vector, "receiver_static_private"))));

        NoiseHandshake device = new NoiseHandshake(pattern, true, prologue, bytes(vector, "device_static_private"),
                psk ? null : bytes(vector, "receiver_static_public"), psks, bytes(vector, "device_ephemeral_private"));
        NoiseHandshake receiver = new NoiseHandshake(pattern, false, prologue, bytes(vector, "receiver_static_private"),
                null, psks, bytes(vector, "receiver_ephemeral_private"));
        NoiseTransport deviceTransport = null;
        NoiseTransport receiverTransport = null;
        for (JsonValue item : array(vector, "frames")) {
            JsonValue.Object frame = (JsonValue.Object) item;
            String stage = text(frame, "stage");
            byte[] wire = bytes(frame, "wire");
            if (stage.equals("preamble")) {
                assertArrayEquals(prologue, wire);
                continue;
            }
            byte[] payload = bytes(frame, "payload");
            byte[] noise = bytes(frame, "noise_message");
            assertArrayEquals(wire, FramedConnection.frame(noise));
            boolean fromDevice = text(frame, "direction").equals("device_to_receiver");
            if (!device.isComplete() || !receiver.isComplete()) {
                NoiseHandshake writer = fromDevice ? device : receiver;
                NoiseHandshake reader = fromDevice ? receiver : device;
                assertEquals(Hex.encode(noise), Hex.encode(writer.writeMessage(payload)));
                assertEquals(Hex.encode(payload), Hex.encode(reader.readMessage(noise)));
                if (device.isComplete() && receiver.isComplete()) {
                    deviceTransport = device.split();
                    receiverTransport = receiver.split();
                    assertEquals(text(vector, "handshake_hash"), Hex.encode(deviceTransport.handshakeHash()));
                    assertEquals(text(vector, "handshake_hash"), Hex.encode(receiverTransport.handshakeHash()));
                }
                if (stage.equals("handshake_3")) {
                    Wire.decodePairRequest(payload);
                }
            } else if (fromDevice) {
                assertEquals(Hex.encode(noise), Hex.encode(deviceTransport.seal(payload)));
                assertEquals(Hex.encode(payload), Hex.encode(receiverTransport.open(noise)));
                if (stage.equals("hello")) {
                    Wire.decodeHello(payload);
                } else {
                    Wire.decodeRequest(payload);
                }
            } else {
                assertEquals(Hex.encode(noise), Hex.encode(receiverTransport.seal(payload)));
                assertEquals(Hex.encode(payload), Hex.encode(deviceTransport.open(noise)));
                Wire.decodeReceiverMessage(payload);
            }
        }
        assertTrue(device.isComplete());
    }

    @Test
    void connectVector() {
        replay("handshake-connect.json", NoisePattern.IK, false);
    }

    @Test
    void pairVector() {
        replay("handshake-pair.json", NoisePattern.XX_PSK0, true);
    }

    @Test
    void vectorKeysAreDerivedFromPublicLabels() {
        JsonValue.Object vector = load("spec/vectors/integration-v1/handshake-connect.json");
        String expected = Hex.encode(IntegrationV1.sha256("erdigo-integration-v1 test-only key: device static".getBytes(StandardCharsets.UTF_8)));
        assertEquals(expected, text(vector, "device_static_private"));
        assertEquals(text(vector, "device_fingerprint"), IntegrationV1.fingerprint(bytes(vector, "device_static_public")));
        assertEquals(text(vector, "receiver_fingerprint"), IntegrationV1.fingerprint(bytes(vector, "receiver_static_public")));
    }

    @Test
    void firstConnectMessageCarriesTheCounterAndHasTheRequiredLength() {
        JsonValue.Object vector = load("spec/vectors/integration-v1/handshake-connect.json");
        JsonValue.Object first = (JsonValue.Object) array(vector, "frames").get(1);
        assertEquals(IntegrationV1.CONNECT_FIRST_MESSAGE_LENGTH, bytes(first, "noise_message").length);
        byte[] counter = bytes(first, "payload");
        long value = 0;
        for (byte part : counter) {
            value = (value << 8) | (part & 0xFF);
        }
        assertEquals(((JsonValue.Integer) vector.fields().get("connect_counter")).value(), value);
        JsonValue.Object pair = load("spec/vectors/integration-v1/handshake-pair.json");
        assertEquals(IntegrationV1.PAIR_FIRST_MESSAGE_LENGTH, bytes((JsonValue.Object) array(pair, "frames").get(1), "noise_message").length);
    }

    private static String payload(String file, String stage) {
        for (JsonValue item : array(load("spec/vectors/integration-v1/" + file), "frames")) {
            JsonValue.Object frame = (JsonValue.Object) item;
            if (text(frame, "stage").equals(stage)) {
                return text(frame, "payload_text");
            }
        }
        throw new AssertionError("no stage " + stage);
    }

    private static String utf8(byte[] data) {
        return new String(data, StandardCharsets.UTF_8);
    }

    /** This SDK writes the vectors' messages byte for byte, as the Swift and .NET SDKs do. */
    @Test
    void messagesAreEncodedByteForByteLikeTheVectors() {
        assertEquals(payload("handshake-connect.json", "hello"), utf8(Wire.encode(new Hello(List.of(1), "erdigo-test/1.0"))));
        assertEquals(payload("handshake-connect.json", "welcome"), utf8(Wire.encode(
                new ReceiverMessage.WelcomeMessage(new Welcome(1, 41, List.of(ItemKind.PIN, ItemKind.TRACK), Limits.V1)))));
        assertEquals(payload("handshake-connect.json", "request"), utf8(Wire.encode(new RequestEnvelope(42,
                new Request.PutPin(new ItemId("gate"), new Coordinate(37.5796, 126.977), "Palace gate")))));
        assertEquals(payload("handshake-connect.json", "ack"), utf8(Wire.encode(new ReceiverMessage.Ack(42, false))));
        assertEquals(payload("handshake-pair.json", "handshake_3"), utf8(Wire.encode(
                new PairRequest(List.of(1), "erdigo-test/1.0", "Test device", List.of(ItemKind.PIN, ItemKind.TRACK)))));
        assertEquals(payload("handshake-pair.json", "pair_status_pending"),
                utf8(Wire.encode(new ReceiverMessage.PairStatusMessage(new PairStatus.Pending()))));
        assertEquals(payload("handshake-pair.json", "pair_status_approved"),
                utf8(Wire.encode(new ReceiverMessage.PairStatusMessage(new PairStatus.Approved(List.of(ItemKind.PIN), 0)))));
    }

    @Test
    void messageVectors() {
        JsonValue.Object file = load("spec/vectors/integration-v1/messages.json");
        List<JsonValue> valid = array(file, "valid");
        assertFalse(valid.isEmpty());
        for (JsonValue item : valid) {
            JsonValue.Object entry = (JsonValue.Object) item;
            RequestEnvelope envelope = Wire.decodeRequest(text(entry, "json").getBytes(StandardCharsets.UTF_8));
            assertTrue(envelope.seq() >= 1, text(entry, "name"));
            // What this SDK reads, it writes back the same way, so a resend is byte for byte the same.
            RequestEnvelope again = Wire.decodeRequest(Wire.encode(envelope));
            assertArrayEquals(Wire.encode(envelope), Wire.encode(again), text(entry, "name"));
        }
        List<JsonValue> invalid = array(file, "invalid");
        assertFalse(invalid.isEmpty());
        for (JsonValue item : invalid) {
            JsonValue.Object entry = (JsonValue.Object) item;
            WireException error = assertThrows(WireException.class,
                    () -> Wire.decodeRequest(text(entry, "json").getBytes(StandardCharsets.UTF_8)), text(entry, "name"));
            assertEquals(text(entry, "code"), error.code().wire(), text(entry, "name"));
        }
        for (JsonValue item : array(file, "invalid_bytes")) {
            JsonValue.Object entry = (JsonValue.Object) item;
            WireException error = assertThrows(WireException.class, () -> Wire.decodeRequest(bytes(entry, "hex")), text(entry, "name"));
            assertEquals(text(entry, "code"), error.code().wire(), text(entry, "name"));
        }
    }
}
