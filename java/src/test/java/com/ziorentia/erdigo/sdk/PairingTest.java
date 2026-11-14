package com.ziorentia.erdigo.sdk;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.ziorentia.erdigo.core.IntegrationV1;
import com.ziorentia.erdigo.core.ItemKind;
import com.ziorentia.erdigo.core.PairingCode;
import com.ziorentia.erdigo.core.PairingTicket;
import java.time.Duration;
import java.util.ArrayList;
import java.util.List;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;

class PairingTest {
    private ScriptedReceiver receiver;
    private DeviceIdentity identity;

    @BeforeEach
    void start() throws Exception {
        receiver = new ScriptedReceiver();
        identity = DeviceIdentity.generate();
    }

    @AfterEach
    void stop() {
        receiver.close();
        identity.close();
    }

    private ReceiverPairing pair(PairingTicket ticket, Pairing.Options options) {
        return Pairing.pair(receiver.target(), ticket, identity, "Test device", List.of(ItemKind.PIN, ItemKind.TRACK), options);
    }

    @Test
    void qrTextPairsWithoutAskingAnyone() {
        List<String> asked = new ArrayList<>();
        ReceiverPairing pairing = pair(receiver.qrTicket(), new Pairing.Options().confirmKey(key -> asked.add(key)));
        assertTrue(asked.isEmpty());
        assertEquals(receiver.fingerprint, pairing.fingerprint());
        assertEquals(List.of(ItemKind.PIN, ItemKind.TRACK), pairing.kinds());
        assertEquals("receiver-" + receiver.fingerprint.substring(0, 8), pairing.label());
        assertEquals("127.0.0.1", pairing.lastHost());
        assertEquals(receiver.port(), (int) pairing.lastPort());
        assertTrue(receiver.events().contains("pair request: Test device"));
    }

    @Test
    void typedCodeAsksAboutTheKeyBeforeTheReceiverHearsAnything() {
        List<String> asked = new ArrayList<>();
        ReceiverPairing pairing = pair(receiver.typedTicket(), new Pairing.Options().label("home").confirmKey(key -> {
            asked.add(key);
            // When the question is asked, the receiver has not been sent this device's key or name.
            assertFalse(receiver.events().stream().anyMatch(event -> event.startsWith("pair request")));
            return true;
        }));
        assertEquals(List.of(IntegrationV1.shortFingerprint(receiver.fingerprint)), asked);
        assertEquals("home", pairing.label());
        assertEquals(1, receiver.count("pair request: Test device"));
    }

    @Test
    void typedCodeAnsweredNoTellsTheReceiverNothing() {
        ErdigoException error = assertThrows(ErdigoException.class,
                () -> pair(receiver.typedTicket(), new Pairing.Options().confirmKey(key -> false)));
        assertEquals(ErdigoException.Kind.KEY_NOT_CONFIRMED, error.kind());
        assertFalse(receiver.events().stream().anyMatch(event -> event.startsWith("pair request")));
        // The code is still good: a later pairing with the QR text works.
        pair(receiver.qrTicket(), null);
    }

    @Test
    void typedCodeWithoutAQuestionIsRefusedBeforeConnecting() {
        ErdigoException error = assertThrows(ErdigoException.class, () -> pair(receiver.typedTicket(), null));
        assertEquals(ErdigoException.Kind.INVALID_ARGUMENT, error.kind());
        assertTrue(receiver.events().isEmpty());
    }

    /** The receiver may still be closing the connection that showed the key, and so drop the next one unanswered. */
    @Test
    void afterAConfirmedKeyASilentConnectionIsTriedAgain() {
        ReceiverPairing pairing = pair(receiver.typedTicket(), new Pairing.Options().confirmKey(key -> {
            receiver.silentConnections.set(2);
            return true;
        }));
        assertEquals(receiver.fingerprint, pairing.fingerprint());
        assertEquals(2, receiver.count("silent"));
        assertEquals(1, receiver.count("pair request: Test device"));
    }

    /** Without a key a person confirmed a moment ago, silence is reported at once, as by the other SDKs. */
    @Test
    void qrTextMeetingSilenceIsNotTriedAgain() {
        receiver.silentConnections.set(1);
        assertEquals(ErdigoException.Kind.NO_ANSWER, assertThrows(ErdigoException.class, () -> pair(receiver.qrTicket(), null)).kind());
        assertEquals(1, receiver.count("connection"));
    }

    @Test
    void wrongFingerprintStopsBeforeMessageThree() {
        PairingTicket ticket = new PairingTicket(receiver.code, "0".repeat(64));
        ErdigoException error = assertThrows(ErdigoException.class, () -> pair(ticket, null));
        assertEquals(ErdigoException.Kind.FINGERPRINT_MISMATCH, error.kind());
        assertFalse(receiver.events().stream().anyMatch(event -> event.startsWith("pair request")));
    }

    @Test
    void wrongCodeGetsNoAnswer() {
        PairingTicket ticket = new PairingTicket(new PairingCode("ZZZZZ-FGHJK-MNPQR-STVWX"), receiver.fingerprint);
        ErdigoException error = assertThrows(ErdigoException.class, () -> pair(ticket, new Pairing.Options().handshakeTimeout(Duration.ofSeconds(2))));
        assertEquals(ErdigoException.Kind.NO_ANSWER, error.kind());
        assertTrue(receiver.events().contains("silent: wrong code"));
    }

    @Test
    void denialIsReported() {
        receiver.approve = false;
        assertEquals(ErdigoException.Kind.PAIRING_DENIED, assertThrows(ErdigoException.class, () -> pair(receiver.qrTicket(), null)).kind());
    }

    @Test
    void personMayAllowFewerKinds() {
        receiver.approvedKinds = List.of(ItemKind.PIN);
        assertEquals(List.of(ItemKind.PIN), pair(receiver.qrTicket(), null).kinds());
    }

    @Test
    void argumentsAreChecked() {
        assertEquals(ErdigoException.Kind.INVALID_ARGUMENT, assertThrows(ErdigoException.class,
                () -> Pairing.pair(receiver.target(), receiver.qrTicket(), identity, "‮\u0000 ", List.of(ItemKind.PIN), new Pairing.Options())).kind());
        assertEquals(ErdigoException.Kind.INVALID_ARGUMENT, assertThrows(ErdigoException.class,
                () -> Pairing.pair(receiver.target(), receiver.qrTicket(), identity, "x", List.of(), new Pairing.Options())).kind());
        assertEquals(ErdigoException.Kind.INVALID_ARGUMENT, assertThrows(ErdigoException.class,
                () -> pair(receiver.qrTicket(), new Pairing.Options().label("a\nb"))).kind());
        assertTrue(receiver.events().isEmpty());
    }
}
