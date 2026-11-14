package com.ziorentia.erdigo.core;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;

import java.util.List;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.ValueSource;

class ModelTest {
    private static final String FP = "61a262dfb9af8d6247ef8ecf537e73b6d182e8eb2ce070930e706186bd1bed22";

    @Test
    void codesAreReadAsAPersonMayTypeThem() {
        assertEquals("TEST00N1YC0DE0000000", new PairingCode("TEST0-0NLYC-0DE00-00000").canonical());
        assertEquals("TEST00N1YC0DE0000000", new PairingCode(" test0 0nlyc\t0de00-00000\n").canonical());
        assertEquals("01111ABCDE0123456789", new PairingCode("oIiLl-abcde-01234-56789").canonical());
        assertEquals("TEST0-0N1YC-0DE00-00000", new PairingCode("TEST00N1YC0DE0000000").display());
        assertEquals("<pairing code>", new PairingCode("TEST00N1YC0DE0000000").toString());
    }

    @ParameterizedTest
    @ValueSource(strings = {"", "TEST0-0N1YC-0DE00-0000", "TEST0-0N1YC-0DE00-000000", "TEST0-0N1YC-0DE00-0000U", "TEST0-0N1YC-0DE00-0000\u00e9",
        "TEST0-0N1YC-0DE00-0000\ud800"})
    void malformedCodesAreRefused(String text) {
        assertThrows(WireException.class, () -> new PairingCode(text));
    }

    @Test
    void qrTextNeedsExactlyOneCodeAndOneFingerprint() {
        PairingTicket ticket = PairingTicket.parse("erdigo-pair:1?code=TEST0-0N1YC-0DE00-00000&fp=" + FP.toUpperCase());
        assertEquals(FP, ticket.fingerprint());
        assertNull(PairingTicket.parse("TEST0-0N1YC-0DE00-00000").fingerprint());
        String code = "code=TEST0-0N1YC-0DE00-00000";
        for (String bad : List.of(
                "erdigo-pair:1?" + code,
                "erdigo-pair:1?fp=" + FP,
                "erdigo-pair:1?" + code + "&fp=" + FP + "&fp=" + FP,
                "erdigo-pair:1?" + code + "&" + code + "&fp=" + FP,
                "erdigo-pair:1?" + code + "&fp=" + FP + "&host=192.168.1.2",
                "erdigo-pair:1?" + code + "&fp=" + FP + "&",
                "erdigo-pair:1?" + code + "&&fp=" + FP,
                "erdigo-pair:1?" + code + "&fp=",
                "erdigo-pair:1?" + code + "&fp=" + FP.substring(1),
                "erdigo-pair:1?" + code + "&=x&fp=" + FP,
                "erdigo-pair:1?" + code + "&fp",
                "erdigo-pair:1?" + "x".repeat(1100))) {
            assertThrows(WireException.class, () -> PairingTicket.parse(bad), bad);
        }
        assertNull(new PairingTicket(new PairingCode("TEST00N1YC0DE0000000"), null).uri());
    }

    @Test
    void textRules() {
        TextRules.validateTitle("Palace gate \ud83c\udff0");
        TextRules.validateNote("Line one\nLine two\twith a tab");
        assertEquals(ErrorCode.INVALID_TEXT, assertThrows(WireException.class, () -> TextRules.validateTitle("")).code());
        assertEquals(ErrorCode.INVALID_TEXT, assertThrows(WireException.class, () -> TextRules.validateTitle("a\nb")).code());
        assertEquals(ErrorCode.INVALID_TEXT, assertThrows(WireException.class, () -> TextRules.validateTitle("evil\u202egnp.exe")).code());
        assertEquals(ErrorCode.INVALID_TEXT, assertThrows(WireException.class, () -> TextRules.validateTitle("lone \ud800")).code());
        assertEquals(ErrorCode.TOO_LARGE, assertThrows(WireException.class, () -> TextRules.validateTitle("a".repeat(201))).code());
        TextRules.validateTitle("\ud83d\ude00".repeat(200));
        assertEquals(ErrorCode.TOO_LARGE, assertThrows(WireException.class, () -> TextRules.validateNote("\u00e9".repeat(2049))).code());
        TextRules.validateNote("\u00e9".repeat(2048));
        assertEquals(ErrorCode.INVALID_TEXT, assertThrows(WireException.class, () -> TextRules.validateNote("bell\u0007")).code());
    }

    @Test
    void deviceNamesAreCleanedAsAReceiverWould() {
        assertEquals("Drone A", TextRules.sanitizeDeviceName("  Drone\u202e A\u0000 \u00a0"));
        assertEquals(40, TextRules.sanitizeDeviceName("x".repeat(100)).length());
        assertEquals("ab", TextRules.sanitizeDeviceName("a\ud800b"));
        assertEquals("", TextRules.sanitizeDeviceName("\u200e\u200f"));
    }

    @Test
    void valuesAreChecked() {
        assertThrows(WireException.class, () -> new Coordinate(90.1, 0));
        assertThrows(WireException.class, () -> new Coordinate(0, Double.NaN));
        assertThrows(WireException.class, () -> new ItemId(""));
        assertThrows(WireException.class, () -> new ItemId("a b"));
        assertThrows(WireException.class, () -> new ItemId("x".repeat(65)));
        assertThrows(WireException.class, () -> new TrackPoint(new Coordinate(0, 0), -1L, null));
        assertThrows(WireException.class, () -> new TrackPoint(new Coordinate(0, 0), null, 100_000.5));
        assertEquals(PhotoType.JPEG, PhotoType.detect(new byte[] {(byte) 0xFF, (byte) 0xD8, (byte) 0xFF, 0}));
        assertNull(PhotoType.detect("GIF89a".getBytes(java.nio.charset.StandardCharsets.US_ASCII)));
    }

    @Test
    void shortFingerprintIsGroupedInFours() {
        assertEquals("61a2-62df-b9af-8d62", IntegrationV1.shortFingerprint(FP));
    }

    @Test
    void tokenBucketAlwaysAsksForAPositiveWait() {
        TokenBucket bucket = new TokenBucket(2, 1, 0);
        assertEquals(0, bucket.take(0, 1));
        assertEquals(0, bucket.take(0, 1));
        assertEquals(1.0, bucket.take(0, 1), 1e-9);
        assertEquals(0, bucket.take(1, 1));
        // More than the bucket holds counts as a full bucket, so it can be met.
        assertEquals(2.0, bucket.take(1, 5), 1e-9);
    }
}
