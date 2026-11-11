using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Erdigo.Core;

/// <summary>
/// A decoded JSON value. Integers and fractional numbers are kept apart so that a field documented as an integer
/// cannot silently accept <c>1.5</c>.
/// </summary>
public abstract class JsonValue : IEquatable<JsonValue>
{
    private protected JsonValue()
    {
    }

    public sealed class Null : JsonValue
    {
        public static Null Instance { get; } = new();

        public override bool Equals(JsonValue? other) => other is Null;

        public override int GetHashCode() => 0;
    }

    public sealed class Bool : JsonValue
    {
        public Bool(bool value) => Value = value;

        public bool Value { get; }

        public override bool Equals(JsonValue? other) => other is Bool flag && flag.Value == Value;

        public override int GetHashCode() => Value.GetHashCode();
    }

    public sealed class Integer : JsonValue
    {
        public Integer(long value) => Value = value;

        public long Value { get; }

        public override bool Equals(JsonValue? other) => other is Integer number && number.Value == Value;

        public override int GetHashCode() => Value.GetHashCode();
    }

    public sealed class Number : JsonValue
    {
        public Number(double value) => Value = value;

        public double Value { get; }

        public override bool Equals(JsonValue? other) => other is Number number && number.Value.Equals(Value);

        public override int GetHashCode() => Value.GetHashCode();
    }

    public sealed class String : JsonValue
    {
        public String(string value) => Value = value;

        public string Value { get; }

        public override bool Equals(JsonValue? other) => other is String text && string.Equals(text.Value, Value, StringComparison.Ordinal);

        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
    }

    public sealed class Array : JsonValue
    {
        public Array(IReadOnlyList<JsonValue> values) => Values = values;

        public IReadOnlyList<JsonValue> Values { get; }

        public override bool Equals(JsonValue? other) => other is Array array && array.Values.SequenceEqual(Values);

        public override int GetHashCode() => Values.Count;
    }

    public sealed class Object : JsonValue
    {
        public Object(IReadOnlyDictionary<string, JsonValue> fields) => Fields = fields;

        public IReadOnlyDictionary<string, JsonValue> Fields { get; }

        public override bool Equals(JsonValue? other) =>
            other is Object map && map.Fields.Count == Fields.Count
            && Fields.All(field => map.Fields.TryGetValue(field.Key, out var value) && value.Equals(field.Value));

        public override int GetHashCode() => Fields.Count;
    }

    public abstract bool Equals(JsonValue? other);

    public override bool Equals(object? obj) => Equals(obj as JsonValue);

    public abstract override int GetHashCode();
}

/// <summary>Why <see cref="StrictJson.Parse"/> refused its input.</summary>
public enum JsonParseError
{
    InvalidUtf8,
    UnexpectedByte,
    UnexpectedEnd,
    DuplicateKey,
    TooDeep,
    NumberOutOfRange,
    InvalidEscape,
    TrailingData,
}

public sealed class JsonParseException : Exception
{
    public JsonParseException(JsonParseError error, int offset = -1)
        : base($"strict JSON: {error}")
    {
        Error = error;
        Offset = offset;
    }

    public JsonParseError Error { get; }

    public int Offset { get; }
}

/// <summary>
/// An RFC 8259 parser that refuses input ordinary parsers tolerate: duplicate keys, invalid UTF-8, a byte order mark,
/// unpaired surrogates, non-finite numbers, anything but white space after the value, and more than six levels of
/// nesting.
/// </summary>
/// <remarks>
/// A hand-written parser is used, as in the Swift SDK, so that every SDK reads the same bytes the same way.
/// Keys that are the same text in different Unicode spellings count as duplicates, because Swift compares strings
/// that way, and two SDKs must not disagree about whether a message has a duplicate key.
/// </remarks>
public static class StrictJson
{
    public const int DefaultMaxDepth = 6;

    public static JsonValue Parse(ReadOnlySpan<byte> data, int maxDepth = DefaultMaxDepth)
    {
        if (!IsValidUtf8(data))
        {
            throw new JsonParseException(JsonParseError.InvalidUtf8);
        }
        var parser = new Parser(data.ToArray(), maxDepth);
        return parser.ParseDocument();
    }

    /// <summary>Rejects overlong forms, encoded surrogates and values above U+10FFFF.</summary>
    public static bool IsValidUtf8(ReadOnlySpan<byte> bytes)
    {
        var index = 0;
        while (index < bytes.Length)
        {
            var first = bytes[index];
            int length;
            byte minimumSecond = 0x80;
            byte maximumSecond = 0xBF;
            switch (first)
            {
                case <= 0x7F:
                    index += 1;
                    continue;
                case >= 0xC2 and <= 0xDF:
                    length = 2;
                    break;
                case 0xE0:
                    length = 3;
                    minimumSecond = 0xA0;
                    break;
                case (>= 0xE1 and <= 0xEC) or 0xEE or 0xEF:
                    length = 3;
                    break;
                case 0xED:
                    length = 3;
                    maximumSecond = 0x9F;
                    break;
                case 0xF0:
                    length = 4;
                    minimumSecond = 0x90;
                    break;
                case >= 0xF1 and <= 0xF3:
                    length = 4;
                    break;
                case 0xF4:
                    length = 4;
                    maximumSecond = 0x8F;
                    break;
                default:
                    return false;
            }
            if (index + length > bytes.Length)
            {
                return false;
            }
            if (bytes[index + 1] < minimumSecond || bytes[index + 1] > maximumSecond)
            {
                return false;
            }
            for (var offset = 2; offset < length; offset++)
            {
                if ((bytes[index + offset] & 0xC0) != 0x80)
                {
                    return false;
                }
            }
            index += length;
        }
        return true;
    }

    private sealed class Parser
    {
        private readonly byte[] bytes;
        private readonly int maxDepth;
        private int index;

        public Parser(byte[] bytes, int maxDepth)
        {
            this.bytes = bytes;
            this.maxDepth = maxDepth;
        }

        public JsonValue ParseDocument()
        {
            SkipWhitespace();
            var value = ParseValue(depth: 1);
            SkipWhitespace();
            if (index != bytes.Length)
            {
                throw new JsonParseException(JsonParseError.TrailingData, index);
            }
            return value;
        }

        private void SkipWhitespace()
        {
            while (index < bytes.Length && bytes[index] is 0x20 or 0x09 or 0x0A or 0x0D)
            {
                index += 1;
            }
        }

        private byte Peek()
        {
            if (index >= bytes.Length)
            {
                throw new JsonParseException(JsonParseError.UnexpectedEnd, index);
            }
            return bytes[index];
        }

        private void Expect(string literal)
        {
            if (index + literal.Length > bytes.Length)
            {
                throw new JsonParseException(JsonParseError.UnexpectedEnd, index);
            }
            for (var offset = 0; offset < literal.Length; offset++)
            {
                if (bytes[index + offset] != literal[offset])
                {
                    throw new JsonParseException(JsonParseError.UnexpectedByte, index);
                }
            }
            index += literal.Length;
        }

        private JsonValue ParseValue(int depth)
        {
            switch (Peek())
            {
                case (byte)'{':
                    if (depth > maxDepth)
                    {
                        throw new JsonParseException(JsonParseError.TooDeep, index);
                    }
                    return ParseObject(depth);
                case (byte)'[':
                    if (depth > maxDepth)
                    {
                        throw new JsonParseException(JsonParseError.TooDeep, index);
                    }
                    return ParseArray(depth);
                case (byte)'"':
                    return new JsonValue.String(ParseString());
                case (byte)'t':
                    Expect("true");
                    return new JsonValue.Bool(true);
                case (byte)'f':
                    Expect("false");
                    return new JsonValue.Bool(false);
                case (byte)'n':
                    Expect("null");
                    return JsonValue.Null.Instance;
                case (byte)'-' or (>= (byte)'0' and <= (byte)'9'):
                    return ParseNumber();
                default:
                    throw new JsonParseException(JsonParseError.UnexpectedByte, index);
            }
        }

        private JsonValue ParseObject(int depth)
        {
            index += 1;
            var result = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            var canonicalKeys = new HashSet<string>(StringComparer.Ordinal);
            SkipWhitespace();
            if (Peek() == (byte)'}')
            {
                index += 1;
                return new JsonValue.Object(result);
            }
            while (true)
            {
                SkipWhitespace();
                if (Peek() != (byte)'"')
                {
                    throw new JsonParseException(JsonParseError.UnexpectedByte, index);
                }
                var key = ParseString();
                // The same key in another Unicode spelling is a duplicate too, as it is for Swift.
                if (result.ContainsKey(key) || !canonicalKeys.Add(key.Normalize(NormalizationForm.FormC)))
                {
                    throw new JsonParseException(JsonParseError.DuplicateKey, index);
                }
                SkipWhitespace();
                if (Peek() != (byte)':')
                {
                    throw new JsonParseException(JsonParseError.UnexpectedByte, index);
                }
                index += 1;
                SkipWhitespace();
                result[key] = ParseValue(depth + 1);
                SkipWhitespace();
                var next = Peek();
                index += 1;
                if (next == (byte)'}')
                {
                    return new JsonValue.Object(result);
                }
                if (next != (byte)',')
                {
                    throw new JsonParseException(JsonParseError.UnexpectedByte, index - 1);
                }
            }
        }

        private JsonValue ParseArray(int depth)
        {
            index += 1;
            var result = new List<JsonValue>();
            SkipWhitespace();
            if (Peek() == (byte)']')
            {
                index += 1;
                return new JsonValue.Array(result);
            }
            while (true)
            {
                SkipWhitespace();
                result.Add(ParseValue(depth + 1));
                SkipWhitespace();
                var next = Peek();
                index += 1;
                if (next == (byte)']')
                {
                    return new JsonValue.Array(result);
                }
                if (next != (byte)',')
                {
                    throw new JsonParseException(JsonParseError.UnexpectedByte, index - 1);
                }
            }
        }

        private uint ParseHex4()
        {
            if (index + 4 > bytes.Length)
            {
                throw new JsonParseException(JsonParseError.UnexpectedEnd, index);
            }
            uint value = 0;
            for (var count = 0; count < 4; count++)
            {
                var digit = bytes[index];
                index += 1;
                uint number = digit switch
                {
                    >= (byte)'0' and <= (byte)'9' => (uint)(digit - '0'),
                    >= (byte)'a' and <= (byte)'f' => (uint)(digit - 'a' + 10),
                    >= (byte)'A' and <= (byte)'F' => (uint)(digit - 'A' + 10),
                    _ => throw new JsonParseException(JsonParseError.InvalidEscape, index - 1),
                };
                value = (value << 4) | number;
            }
            return value;
        }

        private string ParseString()
        {
            index += 1;
            var output = new List<byte>();
            Span<byte> encoded = stackalloc byte[4];
            while (true)
            {
                var current = Peek();
                switch (current)
                {
                    case (byte)'"':
                        index += 1;
                        // Every byte here was checked as UTF-8, and every escape is a scalar value, so this loses nothing.
                        return Encoding.UTF8.GetString(output.ToArray());
                    case (byte)'\\':
                        index += 1;
                        var escape = Peek();
                        index += 1;
                        switch (escape)
                        {
                            case (byte)'"': output.Add(0x22); break;
                            case (byte)'\\': output.Add(0x5C); break;
                            case (byte)'/': output.Add(0x2F); break;
                            case (byte)'b': output.Add(0x08); break;
                            case (byte)'f': output.Add(0x0C); break;
                            case (byte)'n': output.Add(0x0A); break;
                            case (byte)'r': output.Add(0x0D); break;
                            case (byte)'t': output.Add(0x09); break;
                            case (byte)'u':
                                var scalar = ParseHex4();
                                if (scalar is >= 0xDC00 and <= 0xDFFF)
                                {
                                    throw new JsonParseException(JsonParseError.InvalidEscape, index);
                                }
                                if (scalar is >= 0xD800 and <= 0xDBFF)
                                {
                                    if (index + 2 > bytes.Length || bytes[index] != (byte)'\\' || bytes[index + 1] != (byte)'u')
                                    {
                                        throw new JsonParseException(JsonParseError.InvalidEscape, index);
                                    }
                                    index += 2;
                                    var low = ParseHex4();
                                    if (low is < 0xDC00 or > 0xDFFF)
                                    {
                                        throw new JsonParseException(JsonParseError.InvalidEscape, index);
                                    }
                                    scalar = 0x10000 + ((scalar - 0xD800) << 10) + (low - 0xDC00);
                                }
                                if (!Rune.TryCreate(scalar, out var rune))
                                {
                                    throw new JsonParseException(JsonParseError.InvalidEscape, index);
                                }
                                var written = rune.EncodeToUtf8(encoded);
                                for (var offset = 0; offset < written; offset++)
                                {
                                    output.Add(encoded[offset]);
                                }
                                break;
                            default:
                                throw new JsonParseException(JsonParseError.InvalidEscape, index - 1);
                        }
                        break;
                    case <= 0x1F:
                        throw new JsonParseException(JsonParseError.UnexpectedByte, index);
                    default:
                        output.Add(current);
                        index += 1;
                        break;
                }
            }
        }

        private JsonValue ParseNumber()
        {
            var start = index;
            var isInteger = true;
            if (bytes[index] == (byte)'-')
            {
                index += 1;
            }
            var first = Peek();
            if (first is < (byte)'0' or > (byte)'9')
            {
                throw new JsonParseException(JsonParseError.UnexpectedByte, index);
            }
            index += 1;
            if (first == (byte)'0' && index < bytes.Length && bytes[index] is >= (byte)'0' and <= (byte)'9')
            {
                throw new JsonParseException(JsonParseError.UnexpectedByte, index);
            }
            ConsumeDigits();

            if (index < bytes.Length && bytes[index] == (byte)'.')
            {
                isInteger = false;
                index += 1;
                RequireDigit();
                ConsumeDigits();
            }
            if (index < bytes.Length && bytes[index] is (byte)'e' or (byte)'E')
            {
                isInteger = false;
                index += 1;
                if (index < bytes.Length && bytes[index] is (byte)'+' or (byte)'-')
                {
                    index += 1;
                }
                RequireDigit();
                ConsumeDigits();
            }

            var text = Encoding.ASCII.GetString(bytes, start, index - start);
            // An integer too long for 64 bits is still a number. A field that needs an integer refuses it later.
            if (isInteger && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
            {
                return new JsonValue.Integer(integer);
            }
            var value = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (!double.IsFinite(value))
            {
                throw new JsonParseException(JsonParseError.NumberOutOfRange, start);
            }
            return new JsonValue.Number(value);
        }

        private void ConsumeDigits()
        {
            while (index < bytes.Length && bytes[index] is >= (byte)'0' and <= (byte)'9')
            {
                index += 1;
            }
        }

        private void RequireDigit()
        {
            var digit = Peek();
            if (digit is < (byte)'0' or > (byte)'9')
            {
                throw new JsonParseException(JsonParseError.UnexpectedByte, index);
            }
        }
    }
}

/// <summary>Writes JSON with a stable field order, so a retried message is byte-for-byte identical.</summary>
public static class JsonWriter
{
    /// <summary>A value to write. Objects keep their fields in the order given.</summary>
    public abstract class Value
    {
        private protected Value()
        {
        }

        public static Value Of(bool flag) => new BoolValue(flag);

        public static Value Of(long number) => new IntegerValue(number);

        public static Value Of(double number) => new NumberValue(number);

        public static Value Of(string text) => new StringValue(text);

        public static Value Array(IEnumerable<Value> values) => new ArrayValue(values.ToArray());

        public static Value Object(IEnumerable<(string Key, Value Value)> fields) => new ObjectValue(fields.ToArray());

        internal sealed class BoolValue : Value
        {
            public BoolValue(bool flag) => Flag = flag;

            public bool Flag { get; }
        }

        internal sealed class IntegerValue : Value
        {
            public IntegerValue(long number) => Number = number;

            public long Number { get; }
        }

        internal sealed class NumberValue : Value
        {
            public NumberValue(double number) => Number = number;

            public double Number { get; }
        }

        internal sealed class StringValue : Value
        {
            public StringValue(string text) => Text = text;

            public string Text { get; }
        }

        internal sealed class ArrayValue : Value
        {
            public ArrayValue(Value[] values) => Values = values;

            public Value[] Values { get; }
        }

        internal sealed class ObjectValue : Value
        {
            public ObjectValue((string Key, Value Value)[] fields) => Fields = fields;

            public (string Key, Value Value)[] Fields { get; }
        }
    }

    /// <summary>A UTF-8 encoder that refuses a lone surrogate instead of quietly changing the text.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static byte[] Encode(Value value)
    {
        var output = new StringBuilder();
        Write(value, output);
        return StrictUtf8.GetBytes(output.ToString());
    }

    private static void Write(Value value, StringBuilder output)
    {
        switch (value)
        {
            case Value.BoolValue flag:
                output.Append(flag.Flag ? "true" : "false");
                break;
            case Value.IntegerValue integer:
                output.Append(integer.Number.ToString(CultureInfo.InvariantCulture));
                break;
            case Value.NumberValue number:
                output.Append(FormatNumber(number.Number));
                break;
            case Value.StringValue text:
                WriteString(text.Text, output);
                break;
            case Value.ArrayValue array:
                output.Append('[');
                for (var index = 0; index < array.Values.Length; index++)
                {
                    if (index > 0)
                    {
                        output.Append(',');
                    }
                    Write(array.Values[index], output);
                }
                output.Append(']');
                break;
            case Value.ObjectValue map:
                output.Append('{');
                for (var index = 0; index < map.Fields.Length; index++)
                {
                    if (index > 0)
                    {
                        output.Append(',');
                    }
                    WriteString(map.Fields[index].Key, output);
                    output.Append(':');
                    Write(map.Fields[index].Value, output);
                }
                output.Append('}');
                break;
            default:
                throw new ArgumentException("unknown JSON value", nameof(value));
        }
    }

    private static void WriteString(string text, StringBuilder output)
    {
        output.Append('"');
        foreach (var character in text)
        {
            switch (character)
            {
                case '"':
                    output.Append("\\\"");
                    break;
                case '\\':
                    output.Append("\\\\");
                    break;
                case '\n':
                    output.Append("\\n");
                    break;
                case '\t':
                    output.Append("\\t");
                    break;
                case '\r':
                    output.Append("\\r");
                    break;
                case < (char)0x20:
                    output.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    break;
                default:
                    output.Append(character);
                    break;
            }
        }
        output.Append('"');
    }

    /// <summary>
    /// The shortest text that reads back as the same number, spelled the way the Swift SDK spells it, so that both
    /// SDKs write the same bytes for the same request: <c>37.0</c>, <c>0.0001</c>, <c>1e-05</c>, <c>1e+16</c>.
    /// </summary>
    public static string FormatNumber(double number)
    {
        if (!double.IsFinite(number))
        {
            throw new ArgumentException("non-finite numbers cannot be written as JSON", nameof(number));
        }
        if (number == 0)
        {
            return double.IsNegative(number) ? "-0.0" : "0.0";
        }
        // .NET writes the shortest round-trip digits too. Only the layout differs from Swift's.
        var shortest = number.ToString("R", CultureInfo.InvariantCulture);
        var negative = shortest[0] == '-';
        if (negative)
        {
            shortest = shortest[1..];
        }
        var exponent = 0;
        var exponentAt = shortest.IndexOfAny(new[] { 'E', 'e' });
        if (exponentAt >= 0)
        {
            exponent = int.Parse(shortest[(exponentAt + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            shortest = shortest[..exponentAt];
        }
        var point = shortest.IndexOf('.');
        var whole = point >= 0 ? shortest[..point] : shortest;
        var fraction = point >= 0 ? shortest[(point + 1)..] : string.Empty;
        var allDigits = whole + fraction;
        var leadingZeros = allDigits.Length - allDigits.TrimStart('0').Length;
        var digits = allDigits.TrimStart('0').TrimEnd('0');
        // The power of ten of the first significant digit.
        var scientific = whole.Length - 1 - leadingZeros + exponent;

        var output = new StringBuilder();
        if (negative)
        {
            output.Append('-');
        }
        if (scientific < -4 || Math.Abs(number) > 9007199254740992.0)
        {
            output.Append(digits[0]);
            if (digits.Length > 1)
            {
                output.Append('.').Append(digits, 1, digits.Length - 1);
            }
            output.Append('e').Append(scientific < 0 ? '-' : '+');
            output.Append(Math.Abs(scientific).ToString("00", CultureInfo.InvariantCulture));
        }
        else if (scientific >= 0)
        {
            var integerDigits = scientific + 1;
            if (digits.Length <= integerDigits)
            {
                output.Append(digits).Append('0', integerDigits - digits.Length).Append(".0");
            }
            else
            {
                output.Append(digits, 0, integerDigits).Append('.').Append(digits, integerDigits, digits.Length - integerDigits);
            }
        }
        else
        {
            output.Append("0.").Append('0', -scientific - 1).Append(digits);
        }
        return output.ToString();
    }
}
