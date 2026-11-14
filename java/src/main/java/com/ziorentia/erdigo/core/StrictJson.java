package com.ziorentia.erdigo.core;

import java.io.ByteArrayOutputStream;
import java.nio.charset.StandardCharsets;
import java.text.Normalizer;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;

/**
 * An RFC 8259 parser that refuses input ordinary parsers tolerate: duplicate keys, invalid UTF-8, a byte order mark,
 * unpaired surrogates, non-finite numbers, anything but white space after the value, and more than six levels of
 * nesting.
 *
 * <p>It is written by hand, as in the Swift and .NET SDKs, so that every SDK reads the same bytes the same way. Keys
 * that are the same text in different Unicode spellings count as duplicates, because Swift compares strings that way,
 * and two SDKs must not disagree about whether a message has a duplicate key.
 */
public final class StrictJson {
    public static final int DEFAULT_MAX_DEPTH = 6;

    private final byte[] bytes;
    private final int maxDepth;
    private int index;

    private StrictJson(byte[] bytes, int maxDepth) {
        this.bytes = bytes;
        this.maxDepth = maxDepth;
    }

    public static JsonValue parse(byte[] data) {
        return parse(data, DEFAULT_MAX_DEPTH);
    }

    public static JsonValue parse(byte[] data, int maxDepth) {
        if (!isValidUtf8(data)) {
            throw new JsonParseException(JsonParseException.Failure.INVALID_UTF8);
        }
        return new StrictJson(data.clone(), maxDepth).parseDocument();
    }

    /** Rejects overlong forms, encoded surrogates and values above U+10FFFF. */
    public static boolean isValidUtf8(byte[] bytes) {
        int index = 0;
        while (index < bytes.length) {
            int first = bytes[index] & 0xFF;
            int length;
            int minimumSecond = 0x80;
            int maximumSecond = 0xBF;
            if (first <= 0x7F) {
                index += 1;
                continue;
            } else if (first >= 0xC2 && first <= 0xDF) {
                length = 2;
            } else if (first == 0xE0) {
                length = 3;
                minimumSecond = 0xA0;
            } else if ((first >= 0xE1 && first <= 0xEC) || first == 0xEE || first == 0xEF) {
                length = 3;
            } else if (first == 0xED) {
                length = 3;
                maximumSecond = 0x9F;
            } else if (first == 0xF0) {
                length = 4;
                minimumSecond = 0x90;
            } else if (first >= 0xF1 && first <= 0xF3) {
                length = 4;
            } else if (first == 0xF4) {
                length = 4;
                maximumSecond = 0x8F;
            } else {
                return false;
            }
            if (index + length > bytes.length) {
                return false;
            }
            int second = bytes[index + 1] & 0xFF;
            if (second < minimumSecond || second > maximumSecond) {
                return false;
            }
            for (int offset = 2; offset < length; offset++) {
                if ((bytes[index + offset] & 0xC0) != 0x80) {
                    return false;
                }
            }
            index += length;
        }
        return true;
    }

    private JsonValue parseDocument() {
        skipWhitespace();
        JsonValue value = parseValue(1);
        skipWhitespace();
        if (index != bytes.length) {
            throw new JsonParseException(JsonParseException.Failure.TRAILING_DATA);
        }
        return value;
    }

    private void skipWhitespace() {
        while (index < bytes.length && (bytes[index] == 0x20 || bytes[index] == 0x09 || bytes[index] == 0x0A || bytes[index] == 0x0D)) {
            index += 1;
        }
    }

    private int peek() {
        if (index >= bytes.length) {
            throw new JsonParseException(JsonParseException.Failure.UNEXPECTED_END);
        }
        return bytes[index] & 0xFF;
    }

    private void expect(String literal) {
        if (index + literal.length() > bytes.length) {
            throw new JsonParseException(JsonParseException.Failure.UNEXPECTED_END);
        }
        for (int offset = 0; offset < literal.length(); offset++) {
            if (bytes[index + offset] != literal.charAt(offset)) {
                throw new JsonParseException(JsonParseException.Failure.UNEXPECTED_BYTE);
            }
        }
        index += literal.length();
    }

    private JsonValue parseValue(int depth) {
        int next = peek();
        switch (next) {
            case '{':
                if (depth > maxDepth) {
                    throw new JsonParseException(JsonParseException.Failure.TOO_DEEP);
                }
                return parseObject(depth);
            case '[':
                if (depth > maxDepth) {
                    throw new JsonParseException(JsonParseException.Failure.TOO_DEEP);
                }
                return parseArray(depth);
            case '"':
                return new JsonValue.Text(parseString());
            case 't':
                expect("true");
                return new JsonValue.Bool(true);
            case 'f':
                expect("false");
                return new JsonValue.Bool(false);
            case 'n':
                expect("null");
                return new JsonValue.Null();
            default:
                if (next == '-' || (next >= '0' && next <= '9')) {
                    return parseNumber();
                }
                throw new JsonParseException(JsonParseException.Failure.UNEXPECTED_BYTE);
        }
    }

    private JsonValue parseObject(int depth) {
        index += 1;
        Map<String, JsonValue> result = new LinkedHashMap<>();
        Set<String> canonicalKeys = new HashSet<>();
        skipWhitespace();
        if (peek() == '}') {
            index += 1;
            return new JsonValue.Object(result);
        }
        while (true) {
            skipWhitespace();
            if (peek() != '"') {
                throw new JsonParseException(JsonParseException.Failure.UNEXPECTED_BYTE);
            }
            String key = parseString();
            // The same key in another Unicode spelling is a duplicate too, as it is for Swift.
            if (result.containsKey(key) || !canonicalKeys.add(Normalizer.normalize(key, Normalizer.Form.NFC))) {
                throw new JsonParseException(JsonParseException.Failure.DUPLICATE_KEY);
            }
            skipWhitespace();
            if (peek() != ':') {
                throw new JsonParseException(JsonParseException.Failure.UNEXPECTED_BYTE);
            }
            index += 1;
            skipWhitespace();
            result.put(key, parseValue(depth + 1));
            skipWhitespace();
            int next = peek();
            index += 1;
            if (next == '}') {
                return new JsonValue.Object(result);
            }
            if (next != ',') {
                throw new JsonParseException(JsonParseException.Failure.UNEXPECTED_BYTE);
            }
        }
    }

    private JsonValue parseArray(int depth) {
        index += 1;
        List<JsonValue> result = new ArrayList<>();
        skipWhitespace();
        if (peek() == ']') {
            index += 1;
            return new JsonValue.Array(result);
        }
        while (true) {
            skipWhitespace();
            result.add(parseValue(depth + 1));
            skipWhitespace();
            int next = peek();
            index += 1;
            if (next == ']') {
                return new JsonValue.Array(result);
            }
            if (next != ',') {
                throw new JsonParseException(JsonParseException.Failure.UNEXPECTED_BYTE);
            }
        }
    }

    private int parseHex4() {
        if (index + 4 > bytes.length) {
            throw new JsonParseException(JsonParseException.Failure.UNEXPECTED_END);
        }
        int value = 0;
        for (int count = 0; count < 4; count++) {
            int digit = bytes[index] & 0xFF;
            index += 1;
            int number;
            if (digit >= '0' && digit <= '9') {
                number = digit - '0';
            } else if (digit >= 'a' && digit <= 'f') {
                number = digit - 'a' + 10;
            } else if (digit >= 'A' && digit <= 'F') {
                number = digit - 'A' + 10;
            } else {
                throw new JsonParseException(JsonParseException.Failure.INVALID_ESCAPE);
            }
            value = (value << 4) | number;
        }
        return value;
    }

    private String parseString() {
        index += 1;
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        while (true) {
            int current = peek();
            if (current == '"') {
                index += 1;
                // Every byte here was checked as UTF-8, and every escape is a scalar value, so this loses nothing.
                return output.toString(StandardCharsets.UTF_8);
            }
            if (current == '\\') {
                index += 1;
                int escape = peek();
                index += 1;
                switch (escape) {
                    case '"':
                        output.write(0x22);
                        break;
                    case '\\':
                        output.write(0x5C);
                        break;
                    case '/':
                        output.write(0x2F);
                        break;
                    case 'b':
                        output.write(0x08);
                        break;
                    case 'f':
                        output.write(0x0C);
                        break;
                    case 'n':
                        output.write(0x0A);
                        break;
                    case 'r':
                        output.write(0x0D);
                        break;
                    case 't':
                        output.write(0x09);
                        break;
                    case 'u': {
                        int scalar = parseHex4();
                        if (scalar >= 0xDC00 && scalar <= 0xDFFF) {
                            throw new JsonParseException(JsonParseException.Failure.INVALID_ESCAPE);
                        }
                        if (scalar >= 0xD800 && scalar <= 0xDBFF) {
                            if (index + 2 > bytes.length || bytes[index] != '\\' || bytes[index + 1] != 'u') {
                                throw new JsonParseException(JsonParseException.Failure.INVALID_ESCAPE);
                            }
                            index += 2;
                            int low = parseHex4();
                            if (low < 0xDC00 || low > 0xDFFF) {
                                throw new JsonParseException(JsonParseException.Failure.INVALID_ESCAPE);
                            }
                            scalar = 0x10000 + ((scalar - 0xD800) << 10) + (low - 0xDC00);
                        }
                        output.writeBytes(new String(Character.toChars(scalar)).getBytes(StandardCharsets.UTF_8));
                        break;
                    }
                    default:
                        throw new JsonParseException(JsonParseException.Failure.INVALID_ESCAPE);
                }
                continue;
            }
            if (current <= 0x1F) {
                throw new JsonParseException(JsonParseException.Failure.UNEXPECTED_BYTE);
            }
            output.write(current);
            index += 1;
        }
    }

    private JsonValue parseNumber() {
        int start = index;
        boolean isInteger = true;
        if (bytes[index] == '-') {
            index += 1;
        }
        int first = peek();
        if (first < '0' || first > '9') {
            throw new JsonParseException(JsonParseException.Failure.UNEXPECTED_BYTE);
        }
        index += 1;
        if (first == '0' && index < bytes.length && bytes[index] >= '0' && bytes[index] <= '9') {
            throw new JsonParseException(JsonParseException.Failure.UNEXPECTED_BYTE);
        }
        consumeDigits();
        if (index < bytes.length && bytes[index] == '.') {
            isInteger = false;
            index += 1;
            requireDigit();
            consumeDigits();
        }
        if (index < bytes.length && (bytes[index] == 'e' || bytes[index] == 'E')) {
            isInteger = false;
            index += 1;
            if (index < bytes.length && (bytes[index] == '+' || bytes[index] == '-')) {
                index += 1;
            }
            requireDigit();
            consumeDigits();
        }
        String text = new String(bytes, start, index - start, StandardCharsets.US_ASCII);
        // An integer too long for 64 bits is still a number. A field that needs an integer refuses it later.
        if (isInteger) {
            try {
                return new JsonValue.Integer(Long.parseLong(text));
            } catch (NumberFormatException tooLong) {
                // Read below as a number.
            }
        }
        double value = Double.parseDouble(text);
        if (!Double.isFinite(value)) {
            throw new JsonParseException(JsonParseException.Failure.NUMBER_OUT_OF_RANGE);
        }
        return new JsonValue.Number(value);
    }

    private void consumeDigits() {
        while (index < bytes.length && bytes[index] >= '0' && bytes[index] <= '9') {
            index += 1;
        }
    }

    private void requireDigit() {
        int digit = peek();
        if (digit < '0' || digit > '9') {
            throw new JsonParseException(JsonParseException.Failure.UNEXPECTED_BYTE);
        }
    }
}
