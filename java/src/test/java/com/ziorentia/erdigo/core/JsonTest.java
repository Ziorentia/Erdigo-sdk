package com.ziorentia.erdigo.core;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertInstanceOf;
import static org.junit.jupiter.api.Assertions.assertThrows;

import java.nio.charset.StandardCharsets;
import java.util.Random;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.CsvSource;
import org.junit.jupiter.params.provider.ValueSource;

class JsonTest {
    private static JsonParseException.Failure refusal(String json) {
        return assertThrows(JsonParseException.class, () -> StrictJson.parse(json.getBytes(StandardCharsets.UTF_8))).failure();
    }

    @Test
    void refusesWhatOrdinaryParsersTolerate() {
        assertEquals(JsonParseException.Failure.DUPLICATE_KEY, refusal("{\"a\":1,\"a\":2}"));
        // The same key in two Unicode spellings, as Swift compares strings.
        assertEquals(JsonParseException.Failure.DUPLICATE_KEY, refusal("{\"\u00e9\":1,\"e\u0301\":2}"));
        assertEquals(JsonParseException.Failure.TRAILING_DATA, refusal("{} {}"));
        assertEquals(JsonParseException.Failure.TOO_DEEP, refusal("[[[[[[[1]]]]]]]"));
        assertEquals(JsonParseException.Failure.INVALID_ESCAPE, refusal("\"\\ud800\""));
        assertEquals(JsonParseException.Failure.INVALID_ESCAPE, refusal("\"\\udc00\""));
        assertEquals(JsonParseException.Failure.NUMBER_OUT_OF_RANGE, refusal("1e400"));
        assertEquals(JsonParseException.Failure.UNEXPECTED_BYTE, refusal("01"));
        assertEquals(JsonParseException.Failure.UNEXPECTED_BYTE, refusal("\"a\tb\""));
        assertEquals(JsonParseException.Failure.UNEXPECTED_BYTE, refusal("NaN"));
        // A byte order mark is valid UTF-8, but not JSON.
        assertEquals(JsonParseException.Failure.UNEXPECTED_BYTE,
                assertThrows(JsonParseException.class, () -> StrictJson.parse(new byte[] {(byte) 0xEF, (byte) 0xBB, (byte) 0xBF, '{', '}'})).failure());
        assertEquals(JsonParseException.Failure.INVALID_UTF8,
                assertThrows(JsonParseException.class, () -> StrictJson.parse(new byte[] {'"', (byte) 0xC0, (byte) 0xAF, '"'})).failure());
    }

    @Test
    void keepsIntegersApartFromFractions() {
        assertInstanceOf(JsonValue.Integer.class, StrictJson.parse("12".getBytes(StandardCharsets.US_ASCII)));
        assertInstanceOf(JsonValue.Number.class, StrictJson.parse("12.0".getBytes(StandardCharsets.US_ASCII)));
        assertInstanceOf(JsonValue.Number.class, StrictJson.parse("1e2".getBytes(StandardCharsets.US_ASCII)));
        // Too long for 64 bits: still a number, but not an integer.
        assertInstanceOf(JsonValue.Number.class, StrictJson.parse("123456789012345678901234567890".getBytes(StandardCharsets.US_ASCII)));
        assertEquals(6, StrictJson.DEFAULT_MAX_DEPTH);
        StrictJson.parse("[[[[[[1]]]]]]".getBytes(StandardCharsets.US_ASCII));
    }

    @Test
    void readsEscapesAndSurrogatePairs() {
        JsonValue value = StrictJson.parse("\"\\u00e9\\ud83d\\ude00\\n\\/\"".getBytes(StandardCharsets.US_ASCII));
        assertEquals("\u00e9\ud83d\ude00\n/", ((JsonValue.Text) value).value());
    }

    @Test
    void writesStringsAsTheOtherSdksDo() {
        String written = new String(JsonWriter.encode(JsonWriter.of("a\"b\\c\nd\te\rf\u0001g\u007f\u00e9")), StandardCharsets.UTF_8);
        assertEquals("\"a\\\"b\\\\c\\nd\\te\\rf\\u0001g\u007f\u00e9\"", written);
        assertThrows(IllegalArgumentException.class, () -> JsonWriter.encode(JsonWriter.of("\ud800")));
    }

    /** The spellings the Swift SDK gives these numbers, which the .NET SDK reproduces too. */
    @ParameterizedTest
    @CsvSource({
        "37.0, 37.0",
        "126.977, 126.977",
        "-33.8568, -33.8568",
        "0.0001, 0.0001",
        "0.00001, 1e-05",
        "1e16, 1e+16",
        "9007199254740992, 9007199254740992.0",
        "9007199254740994, 9.007199254740994e+15",
        "0.1, 0.1",
        "100, 100.0",
        "123456.789, 123456.789",
        "-0.0, -0.0",
        "0, 0.0",
        "5e-324, 5e-324",
        "1.7976931348623157e308, 1.7976931348623157e+308",
        // Double.toString in Java 17 gives more digits than needed for these two.
        "2.82879384806159e17, 2.82879384806159e+17",
        "1.0e23, 1e+23",
        "38.5, 38.5",
        "-1000, -1000.0",
    })
    void numbersAreSpelledLikeTheSwiftSdk(double number, String expected) {
        assertEquals(expected, JsonWriter.formatNumber(number));
    }

    @Test
    void numbersReadBackExactlyAndUseTheFewestDigits() {
        Random random = new Random(42);
        for (int index = 0; index < 20000; index++) {
            double number = Double.longBitsToDouble(random.nextLong());
            if (!Double.isFinite(number)) {
                continue;
            }
            String text = JsonWriter.formatNumber(number);
            assertEquals(number, Double.parseDouble(text), text);
            JsonValue parsed = StrictJson.parse(text.getBytes(StandardCharsets.US_ASCII));
            assertEquals(number, ((JsonValue.Number) parsed).value(), text);
            // No shorter spelling reads back as the same number.
            String digits = text.replaceFirst("^-", "").replaceFirst("e.*$", "").replace(".", "").replaceFirst("^0+", "");
            digits = digits.replaceFirst("0+$", "");
            if (digits.length() > 1) {
                double shorter = new java.math.BigDecimal(Math.abs(number))
                        .round(new java.math.MathContext(digits.length() - 1, java.math.RoundingMode.HALF_EVEN)).doubleValue();
                assertEquals(false, shorter == Math.abs(number), text);
            }
        }
    }

    @ParameterizedTest
    @ValueSource(doubles = {Double.NaN, Double.POSITIVE_INFINITY, Double.NEGATIVE_INFINITY})
    void nonFiniteNumbersAreRefused(double number) {
        assertThrows(IllegalArgumentException.class, () -> JsonWriter.formatNumber(number));
    }
}
