package com.ziorentia.erdigo.core;

import java.math.BigDecimal;
import java.math.MathContext;
import java.math.RoundingMode;
import java.nio.ByteBuffer;
import java.nio.CharBuffer;
import java.nio.charset.CharacterCodingException;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.Locale;

/** Writes JSON with a stable field order, so that a resent message is byte-for-byte identical. */
public final class JsonWriter {
    private JsonWriter() {
    }

    /** A value to write. Objects keep their fields in the order given. */
    public sealed interface Value {
    }

    record BoolValue(boolean flag) implements Value {
    }

    record IntegerValue(long number) implements Value {
    }

    record NumberValue(double number) implements Value {
    }

    record StringValue(String text) implements Value {
    }

    record ArrayValue(List<Value> values) implements Value {
    }

    record ObjectValue(List<Field> fields) implements Value {
    }

    /** One field of an object. */
    public record Field(String key, Value value) {
    }

    public static Value of(boolean flag) {
        return new BoolValue(flag);
    }

    public static Value of(long number) {
        return new IntegerValue(number);
    }

    public static Value of(double number) {
        return new NumberValue(number);
    }

    public static Value of(String text) {
        return new StringValue(text);
    }

    public static Value array(List<Value> values) {
        return new ArrayValue(List.copyOf(values));
    }

    public static Field field(String key, Value value) {
        return new Field(key, value);
    }

    /** An object being built, field by field. */
    public static final class ObjectBuilder {
        private final List<Field> fields = new ArrayList<>();

        public ObjectBuilder put(String key, Value value) {
            fields.add(new Field(key, value));
            return this;
        }

        public ObjectBuilder put(String key, String text) {
            return put(key, of(text));
        }

        public ObjectBuilder put(String key, long number) {
            return put(key, of(number));
        }

        public ObjectBuilder put(String key, double number) {
            return put(key, of(number));
        }

        public ObjectBuilder put(String key, boolean flag) {
            return put(key, of(flag));
        }

        public Value build() {
            return new ObjectValue(List.copyOf(fields));
        }
    }

    public static ObjectBuilder object() {
        return new ObjectBuilder();
    }

    /** UTF-8 bytes. A lone surrogate is refused instead of being quietly replaced. */
    public static byte[] encode(Value value) {
        StringBuilder output = new StringBuilder();
        write(value, output);
        try {
            ByteBuffer buffer = StandardCharsets.UTF_8.newEncoder()
                    .onMalformedInput(CodingErrorAction.REPORT)
                    .onUnmappableCharacter(CodingErrorAction.REPORT)
                    .encode(CharBuffer.wrap(output));
            return Arrays.copyOfRange(buffer.array(), buffer.arrayOffset() + buffer.position(), buffer.arrayOffset() + buffer.limit());
        } catch (CharacterCodingException error) {
            throw new IllegalArgumentException("text with a lone surrogate cannot be written as JSON", error);
        }
    }

    private static void write(Value value, StringBuilder output) {
        if (value instanceof BoolValue flag) {
            output.append(flag.flag() ? "true" : "false");
        } else if (value instanceof IntegerValue integer) {
            output.append(integer.number());
        } else if (value instanceof NumberValue number) {
            output.append(formatNumber(number.number()));
        } else if (value instanceof StringValue text) {
            writeString(text.text(), output);
        } else if (value instanceof ArrayValue array) {
            output.append('[');
            for (int index = 0; index < array.values().size(); index++) {
                if (index > 0) {
                    output.append(',');
                }
                write(array.values().get(index), output);
            }
            output.append(']');
        } else if (value instanceof ObjectValue map) {
            output.append('{');
            for (int index = 0; index < map.fields().size(); index++) {
                if (index > 0) {
                    output.append(',');
                }
                writeString(map.fields().get(index).key(), output);
                output.append(':');
                write(map.fields().get(index).value(), output);
            }
            output.append('}');
        } else {
            throw new IllegalArgumentException("unknown JSON value");
        }
    }

    private static void writeString(String text, StringBuilder output) {
        output.append('"');
        for (int index = 0; index < text.length(); index++) {
            char character = text.charAt(index);
            switch (character) {
                case '"':
                    output.append("\\\"");
                    break;
                case '\\':
                    output.append("\\\\");
                    break;
                case '\n':
                    output.append("\\n");
                    break;
                case '\t':
                    output.append("\\t");
                    break;
                case '\r':
                    output.append("\\r");
                    break;
                default:
                    if (character < 0x20) {
                        output.append(String.format(Locale.ROOT, "\\u%04x", (int) character));
                    } else {
                        output.append(character);
                    }
                    break;
            }
        }
        output.append('"');
    }

    /**
     * The shortest text that reads back as the same number, spelled the way the Swift SDK spells it, so that every SDK
     * writes the same bytes for the same request: {@code 37.0}, {@code 0.0001}, {@code 1e-05}, {@code 1e+16}.
     *
     * <p>{@link Double#toString} is not used for the digits, because before Java 19 it sometimes gives more digits
     * than needed.
     */
    public static String formatNumber(double number) {
        if (!Double.isFinite(number)) {
            throw new IllegalArgumentException("non-finite numbers cannot be written as JSON");
        }
        if (number == 0) {
            return (Double.doubleToRawLongBits(number) < 0) ? "-0.0" : "0.0";
        }
        boolean negative = number < 0;
        BigDecimal exact = new BigDecimal(Math.abs(number));
        String digits = null;
        int scientific = 0;
        // The fewest significant digits that read back as this number and, among those, the nearest to it.
        for (int precision = 1; precision <= 17 && digits == null; precision++) {
            BigDecimal best = null;
            for (RoundingMode mode : new RoundingMode[] {RoundingMode.HALF_EVEN, RoundingMode.FLOOR, RoundingMode.CEILING}) {
                BigDecimal candidate = exact.round(new MathContext(precision, mode));
                if (Double.parseDouble(candidate.toString()) != Math.abs(number)) {
                    continue;
                }
                if (best == null || candidate.subtract(exact).abs().compareTo(best.subtract(exact).abs()) < 0) {
                    best = candidate;
                }
            }
            if (best != null) {
                BigDecimal stripped = best.stripTrailingZeros();
                digits = stripped.unscaledValue().toString();
                // The power of ten of the first significant digit.
                scientific = digits.length() - 1 - stripped.scale();
            }
        }
        if (digits == null) {
            throw new IllegalStateException("no decimal form found");
        }
        StringBuilder output = new StringBuilder();
        if (negative) {
            output.append('-');
        }
        if (scientific < -4 || Math.abs(number) > 9007199254740992.0) {
            output.append(digits.charAt(0));
            if (digits.length() > 1) {
                output.append('.').append(digits, 1, digits.length());
            }
            output.append('e').append(scientific < 0 ? '-' : '+');
            output.append(String.format(Locale.ROOT, "%02d", Math.abs(scientific)));
        } else if (scientific >= 0) {
            int integerDigits = scientific + 1;
            if (digits.length() <= integerDigits) {
                output.append(digits).append("0".repeat(integerDigits - digits.length())).append(".0");
            } else {
                output.append(digits, 0, integerDigits).append('.').append(digits, integerDigits, digits.length());
            }
        } else {
            output.append("0.").append("0".repeat(-scientific - 1)).append(digits);
        }
        return output.toString();
    }
}
