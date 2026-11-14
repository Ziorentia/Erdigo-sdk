package com.ziorentia.erdigo.core;

import static com.ziorentia.erdigo.core.VectorFiles.array;
import static com.ziorentia.erdigo.core.VectorFiles.bytes;
import static com.ziorentia.erdigo.core.VectorFiles.load;
import static com.ziorentia.erdigo.core.VectorFiles.optionalBytes;
import static com.ziorentia.erdigo.core.VectorFiles.text;
import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.ziorentia.erdigo.core.NoisePattern.Token;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.ValueSource;

/** The official cacophony vectors, run through both roles, byte for byte. */
class NoiseVectorTest {
    private static final Map<String, NoisePattern> PATTERNS = Map.of(
            "Noise_IK_25519_ChaChaPoly_SHA256", NoisePattern.IK,
            "Noise_XXpsk3_25519_ChaChaPoly_SHA256",
            new NoisePattern("XXpsk3", false, List.of(List.of(Token.E), List.of(Token.E, Token.EE, Token.S, Token.ES), List.of(Token.S, Token.SE, Token.PSK))),
            "Noise_NNpsk0_25519_ChaChaPoly_SHA256",
            new NoisePattern("NNpsk0", false, List.of(List.of(Token.PSK, Token.E), List.of(Token.E, Token.EE))));

    private static List<byte[]> psks(JsonValue.Object vector, String name) {
        List<byte[]> keys = new ArrayList<>();
        if (vector.fields().containsKey(name)) {
            for (JsonValue value : array(vector, name)) {
                keys.add(bytes(((JsonValue.Text) value).value()));
            }
        }
        return keys;
    }

    private static void run(JsonValue.Object vector) {
        NoisePattern pattern = PATTERNS.get(text(vector, "protocol_name"));
        assertEquals(text(vector, "protocol_name"), pattern.protocolName());
        NoiseHandshake initiator = new NoiseHandshake(pattern, true, bytes(vector, "init_prologue"), optionalBytes(vector, "init_static"),
                optionalBytes(vector, "init_remote_static"), psks(vector, "init_psks"), optionalBytes(vector, "init_ephemeral"));
        NoiseHandshake responder = new NoiseHandshake(pattern, false, bytes(vector, "resp_prologue"), optionalBytes(vector, "resp_static"),
                optionalBytes(vector, "resp_remote_static"), psks(vector, "resp_psks"), optionalBytes(vector, "resp_ephemeral"));
        List<JsonValue> messages = array(vector, "messages");
        int index = 0;
        while (!initiator.isComplete()) {
            JsonValue.Object message = (JsonValue.Object) messages.get(index);
            byte[] payload = bytes(message, "payload");
            byte[] expected = bytes(message, "ciphertext");
            NoiseHandshake writer = index % 2 == 0 ? initiator : responder;
            NoiseHandshake reader = index % 2 == 0 ? responder : initiator;
            assertEquals(Hex.encode(expected), Hex.encode(writer.writeMessage(payload)));
            assertEquals(Hex.encode(payload), Hex.encode(reader.readMessage(expected)));
            index += 1;
        }
        assertTrue(responder.isComplete());
        NoiseTransport initiatorTransport = initiator.split();
        NoiseTransport responderTransport = responder.split();
        if (vector.fields().containsKey("handshake_hash")) {
            assertEquals(text(vector, "handshake_hash"), Hex.encode(initiatorTransport.handshakeHash()));
            assertEquals(text(vector, "handshake_hash"), Hex.encode(responderTransport.handshakeHash()));
        }
        // Continue alternating, as the official vectors do.
        while (index < messages.size()) {
            JsonValue.Object message = (JsonValue.Object) messages.get(index);
            byte[] payload = bytes(message, "payload");
            byte[] expected = bytes(message, "ciphertext");
            NoiseTransport sender = index % 2 == 0 ? initiatorTransport : responderTransport;
            NoiseTransport receiver = index % 2 == 0 ? responderTransport : initiatorTransport;
            assertEquals(Hex.encode(expected), Hex.encode(sender.seal(payload)));
            assertEquals(Hex.encode(payload), Hex.encode(receiver.open(expected)));
            index += 1;
        }
    }

    @Test
    void officialVectors() {
        List<JsonValue> vectors = array(load("spec/vectors/noise/cacophony-subset.json"), "vectors");
        assertEquals(3, vectors.size());
        for (JsonValue vector : vectors) {
            run((JsonValue.Object) vector);
        }
    }

    static NoiseTransport[] establishedPair() {
        byte[] deviceKey = Noise.newPrivateKey();
        byte[] receiverKey = Noise.newPrivateKey();
        byte[] prologue = "test".getBytes(StandardCharsets.US_ASCII);
        NoiseHandshake device = new NoiseHandshake(NoisePattern.IK, true, prologue, deviceKey, Noise.publicKey(receiverKey), null);
        NoiseHandshake receiver = new NoiseHandshake(NoisePattern.IK, false, prologue, receiverKey, null, null);
        receiver.readMessage(device.writeMessage(null));
        device.readMessage(receiver.writeMessage(null));
        return new NoiseTransport[] {device.split(), receiver.split()};
    }

    @Test
    void tamperedTransportMessageIsRejected() {
        NoiseTransport[] pair = establishedPair();
        byte[] sealed = pair[0].seal("hello".getBytes(StandardCharsets.US_ASCII));
        sealed[0] ^= 0x01;
        assertEquals(NoiseException.Failure.DECRYPTION_FAILED, assertThrows(NoiseException.class, () -> pair[1].open(sealed)).failure());
    }

    @Test
    void replayedTransportMessageIsRejected() {
        NoiseTransport[] pair = establishedPair();
        byte[] sealed = pair[0].seal("hello".getBytes(StandardCharsets.US_ASCII));
        assertEquals("hello", new String(pair[1].open(sealed), StandardCharsets.US_ASCII));
        assertThrows(NoiseException.class, () -> pair[1].open(sealed));
    }

    @Test
    void eachMessageGetsTheNextNonce() {
        NoiseTransport[] pair = establishedPair();
        byte[] first = pair[0].seal(new byte[4]);
        byte[] second = pair[0].seal(new byte[4]);
        assertFalse(java.util.Arrays.equals(first, second));
        assertArrayEquals(new byte[4], pair[1].open(first));
        assertArrayEquals(new byte[4], pair[1].open(second));
    }

    @Test
    void nonceIsFourZeroBytesThenTheLittleEndianCounter() {
        assertEquals("000000000100000000000000", Hex.encode(Noise.nonce(1)));
        assertEquals("000000000807060504030201", Hex.encode(Noise.nonce(0x0102030405060708L)));
        assertEquals("00000000feffffffffffffff", Hex.encode(Noise.nonce(-2L)));
    }

    @Test
    void closedTransportRefusesRatherThanSendingInTheClear() {
        NoiseTransport[] pair = establishedPair();
        pair[0].close();
        assertEquals(NoiseException.Failure.NONCE_EXHAUSTED, assertThrows(NoiseException.class, () -> pair[0].seal(new byte[1])).failure());
    }

    @Test
    void handshakeSplitsOnce() {
        byte[] receiverKey = Noise.newPrivateKey();
        NoiseHandshake device = new NoiseHandshake(NoisePattern.IK, true, new byte[0], Noise.newPrivateKey(), Noise.publicKey(receiverKey), null);
        NoiseHandshake receiver = new NoiseHandshake(NoisePattern.IK, false, new byte[0], receiverKey, null, null);
        receiver.readMessage(device.writeMessage(null));
        device.readMessage(receiver.writeMessage(null));
        device.split();
        assertEquals(NoiseException.Failure.ALREADY_SPLIT, assertThrows(NoiseException.class, device::split).failure());
    }

    @Test
    void outOfOrderUseIsRejected() {
        NoiseHandshake handshake = new NoiseHandshake(NoisePattern.IK, false, new byte[0], Noise.newPrivateKey(), null, null);
        assertEquals(NoiseException.Failure.WRONG_HANDSHAKE_STATE, assertThrows(NoiseException.class, () -> handshake.writeMessage(null)).failure());
        assertEquals(NoiseException.Failure.WRONG_HANDSHAKE_STATE, assertThrows(NoiseException.class, handshake::split).failure());
    }

    @Test
    void prologueMismatchFailsTheHandshake() {
        byte[] receiverKey = Noise.newPrivateKey();
        NoiseHandshake device = new NoiseHandshake(NoisePattern.IK, true, IntegrationV1.preamble(IntegrationV1.Mode.CONNECT),
                Noise.newPrivateKey(), Noise.publicKey(receiverKey), null);
        NoiseHandshake receiver = new NoiseHandshake(NoisePattern.IK, false, IntegrationV1.preamble(IntegrationV1.Mode.PAIR), receiverKey, null, null);
        assertThrows(NoiseException.class, () -> receiver.readMessage(device.writeMessage(null)));
    }

    @Test
    void pairingFirstMessageFailsWithoutTheCode() {
        byte[] prologue = IntegrationV1.preamble(IntegrationV1.Mode.PAIR);
        PairingCode right = new PairingCode("ABCDE-FGHJK-MNPQR-STVWX");
        PairingCode wrong = new PairingCode("ABCDE-FGHJK-MNPQR-STVWY");
        NoiseHandshake device = new NoiseHandshake(NoisePattern.XX_PSK0, true, prologue, Noise.newPrivateKey(), null, wrong.presharedKey());
        NoiseHandshake receiver = new NoiseHandshake(NoisePattern.XX_PSK0, false, prologue, Noise.newPrivateKey(), null, right.presharedKey());
        assertEquals(NoiseException.Failure.DECRYPTION_FAILED,
                assertThrows(NoiseException.class, () -> receiver.readMessage(device.writeMessage(null))).failure());
    }

    @Test
    void patternWithAPreSharedKeyNeedsOne() {
        assertEquals(NoiseException.Failure.MISSING_KEY, assertThrows(NoiseException.class,
                () -> new NoiseHandshake(NoisePattern.XX_PSK0, true, new byte[0], Noise.newPrivateKey(), null, null)).failure());
    }

    /** Public keys of small order give an all-zero secret. Without this check an attacker could force a key everyone knows. */
    @ParameterizedTest
    @ValueSource(strings = {
        "0000000000000000000000000000000000000000000000000000000000000000",
        "0100000000000000000000000000000000000000000000000000000000000000",
        "e0eb7a7c3b41b8ae1656e3faf19fc46ada098deb9c32b1fd866205165f49b800",
        "5f9c95bca3508c24b1d0b1559c83ef5b04445cc4581c8e86d8224eddd09f1157",
        "ecffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f",
        "edffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f",
        "eeffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f",
    })
    void lowOrderPublicKeysAreRejected(String point) {
        byte[] key = Noise.newPrivateKey();
        assertEquals(NoiseException.Failure.WEAK_SHARED_SECRET, assertThrows(NoiseException.class, () -> Noise.dh(key, bytes(point))).failure());
    }

    /** RFC 7748 section 5.2, so that the conversion to and from the JDK's key specifications is checked on its own. */
    @Test
    void rfc7748Vectors() {
        assertEquals("c3da55379de9c6908e94ea4df28d084f32eccf03491c71f754b4075577a28552",
                Hex.encode(Noise.dh(bytes("a546e36bf0527c9d3b16154b82465edd62144c0ac1fc5a18506a2244ba449ac4"),
                        bytes("e6db6867583030db3594c1a424b15f7c726624ec26b3353b10a903a6d0ab1c4c"))));
        // The u-coordinate here has its top bit set, which X25519 ignores.
        assertEquals("95cbde9476e8907d7aade45cb4b873f88b595a68799fa152e6f8f7647aac7957",
                Hex.encode(Noise.dh(bytes("4b66e9d4d1b4673c5ad22691957d6af5c11b6421e0ea01d42ca4169e7918ba0d"),
                        bytes("e5210f12786811d3f4b7959d0538ae2c31dbe7106fc03c3efc4cd549c715a493"))));
        // RFC 7748 section 6.1.
        assertEquals("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a",
                Hex.encode(Noise.publicKey(bytes("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a"))));
    }

    @Test
    void canonicalKeyRule() {
        byte[] key = Noise.publicKey(Noise.newPrivateKey());
        assertTrue(Noise.isCanonicalPublicKey(key));
        key[31] |= (byte) 0x80;
        assertFalse(Noise.isCanonicalPublicKey(key));
        assertFalse(Noise.isCanonicalPublicKey(bytes("edffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f")));
        assertTrue(Noise.isCanonicalPublicKey(bytes("ecffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f")));
        assertFalse(Noise.isCanonicalPublicKey(new byte[31]));
    }
}
