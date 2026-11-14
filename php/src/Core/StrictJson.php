<?php

declare(strict_types=1);

namespace Erdigo\Core;

/**
 * An RFC 8259 parser that refuses input ordinary parsers tolerate: duplicate keys, invalid UTF-8, a byte order mark,
 * unpaired surrogates, non-finite numbers, anything but white space after the value, and more than six levels of
 * nesting.
 *
 * A JSON object becomes a {@see JsonObject}, an array a PHP list, and a number an `int` when it is written as an
 * integer that fits in 64 bits, a `float` otherwise. So a field documented as an integer cannot silently accept `1.5`.
 *
 * It is hand-written, as in the other SDKs, so that every SDK reads the same bytes the same way. `json_decode` takes
 * the last of two duplicate keys and accepts a lone surrogate escape.
 */
final class StrictJson
{
    public const DEFAULT_MAX_DEPTH = 6;

    private int $index = 0;
    private readonly int $length;

    private function __construct(private readonly string $bytes, private readonly int $maxDepth)
    {
        $this->length = strlen($bytes);
    }

    /** @return JsonObject|list<mixed>|string|int|float|bool|null */
    public static function parse(string $data, int $maxDepth = self::DEFAULT_MAX_DEPTH): mixed
    {
        if (!Utf8::isValid($data)) {
            throw new JsonParseException('invalid UTF-8');
        }
        $parser = new self($data, $maxDepth);
        $parser->skipWhitespace();
        $value = $parser->parseValue(1);
        $parser->skipWhitespace();
        if ($parser->index !== $parser->length) {
            throw new JsonParseException('trailing data', $parser->index);
        }
        return $value;
    }

    private function skipWhitespace(): void
    {
        $this->index += strspn($this->bytes, " \t\n\r", $this->index);
    }

    private function peek(): string
    {
        if ($this->index >= $this->length) {
            throw new JsonParseException('unexpected end', $this->index);
        }
        return $this->bytes[$this->index];
    }

    private function expect(string $literal): void
    {
        if (substr($this->bytes, $this->index, strlen($literal)) !== $literal) {
            throw new JsonParseException('unexpected byte', $this->index);
        }
        $this->index += strlen($literal);
    }

    private function parseValue(int $depth): mixed
    {
        $current = $this->peek();
        switch ($current) {
            case '{':
                if ($depth > $this->maxDepth) {
                    throw new JsonParseException('too deep', $this->index);
                }
                return $this->parseObject($depth);
            case '[':
                if ($depth > $this->maxDepth) {
                    throw new JsonParseException('too deep', $this->index);
                }
                return $this->parseArray($depth);
            case '"':
                return $this->parseString();
            case 't':
                $this->expect('true');
                return true;
            case 'f':
                $this->expect('false');
                return false;
            case 'n':
                $this->expect('null');
                return null;
            default:
                if ($current === '-' || self::isDigit($current)) {
                    return $this->parseNumber();
                }
                throw new JsonParseException('unexpected byte', $this->index);
        }
    }

    private function parseObject(int $depth): JsonObject
    {
        $this->index += 1;
        $fields = [];
        $this->skipWhitespace();
        if ($this->peek() === '}') {
            $this->index += 1;
            return new JsonObject($fields);
        }
        while (true) {
            $this->skipWhitespace();
            if ($this->peek() !== '"') {
                throw new JsonParseException('unexpected byte', $this->index);
            }
            $key = $this->parseString();
            // Every key the protocol allows is ASCII, so a key that the other SDKs would see as a duplicate in another
            // Unicode spelling is refused as unknown here in any case.
            if (array_key_exists($key, $fields)) {
                throw new JsonParseException('duplicate key', $this->index);
            }
            $this->skipWhitespace();
            if ($this->peek() !== ':') {
                throw new JsonParseException('unexpected byte', $this->index);
            }
            $this->index += 1;
            $this->skipWhitespace();
            $fields[$key] = $this->parseValue($depth + 1);
            $this->skipWhitespace();
            $next = $this->peek();
            $this->index += 1;
            if ($next === '}') {
                return new JsonObject($fields);
            }
            if ($next !== ',') {
                throw new JsonParseException('unexpected byte', $this->index - 1);
            }
        }
    }

    /** @return list<mixed> */
    private function parseArray(int $depth): array
    {
        $this->index += 1;
        $values = [];
        $this->skipWhitespace();
        if ($this->peek() === ']') {
            $this->index += 1;
            return $values;
        }
        while (true) {
            $this->skipWhitespace();
            $values[] = $this->parseValue($depth + 1);
            $this->skipWhitespace();
            $next = $this->peek();
            $this->index += 1;
            if ($next === ']') {
                return $values;
            }
            if ($next !== ',') {
                throw new JsonParseException('unexpected byte', $this->index - 1);
            }
        }
    }

    private function parseHex4(): int
    {
        $digits = substr($this->bytes, $this->index, 4);
        if (strlen($digits) !== 4) {
            throw new JsonParseException('unexpected end', $this->index);
        }
        if (strspn($digits, '0123456789abcdefABCDEF') !== 4) {
            throw new JsonParseException('invalid escape', $this->index);
        }
        $this->index += 4;
        return (int) hexdec($digits);
    }

    private function parseString(): string
    {
        $this->index += 1;
        $output = '';
        while (true) {
            // Runs of ordinary bytes are copied at once. Every byte here was checked as UTF-8 already.
            $run = strcspn($this->bytes, "\"\\\x00\x01\x02\x03\x04\x05\x06\x07\x08\x09\x0A\x0B\x0C\x0D\x0E\x0F"
                . "\x10\x11\x12\x13\x14\x15\x16\x17\x18\x19\x1A\x1B\x1C\x1D\x1E\x1F", $this->index);
            $output .= substr($this->bytes, $this->index, $run);
            $this->index += $run;
            $current = $this->peek();
            if ($current === '"') {
                $this->index += 1;
                return $output;
            }
            if ($current !== '\\') {
                throw new JsonParseException('unexpected byte', $this->index);
            }
            $this->index += 1;
            $escape = $this->peek();
            $this->index += 1;
            switch ($escape) {
                case '"':
                    $output .= '"';
                    break;
                case '\\':
                    $output .= '\\';
                    break;
                case '/':
                    $output .= '/';
                    break;
                case 'b':
                    $output .= "\x08";
                    break;
                case 'f':
                    $output .= "\x0C";
                    break;
                case 'n':
                    $output .= "\n";
                    break;
                case 'r':
                    $output .= "\r";
                    break;
                case 't':
                    $output .= "\t";
                    break;
                case 'u':
                    $scalar = $this->parseHex4();
                    if ($scalar >= 0xDC00 && $scalar <= 0xDFFF) {
                        throw new JsonParseException('invalid escape', $this->index);
                    }
                    if ($scalar >= 0xD800 && $scalar <= 0xDBFF) {
                        if (substr($this->bytes, $this->index, 2) !== '\\u') {
                            throw new JsonParseException('invalid escape', $this->index);
                        }
                        $this->index += 2;
                        $low = $this->parseHex4();
                        if ($low < 0xDC00 || $low > 0xDFFF) {
                            throw new JsonParseException('invalid escape', $this->index);
                        }
                        $scalar = 0x10000 + (($scalar - 0xD800) << 10) + ($low - 0xDC00);
                    }
                    $output .= Utf8::encode($scalar);
                    break;
                default:
                    throw new JsonParseException('invalid escape', $this->index - 1);
            }
        }
    }

    private function parseNumber(): int|float
    {
        $start = $this->index;
        $isInteger = true;
        if ($this->bytes[$this->index] === '-') {
            $this->index += 1;
        }
        $first = $this->peek();
        if (!self::isDigit($first)) {
            throw new JsonParseException('unexpected byte', $this->index);
        }
        $this->index += 1;
        if ($first === '0' && $this->index < $this->length && self::isDigit($this->bytes[$this->index])) {
            throw new JsonParseException('unexpected byte', $this->index);
        }
        $this->consumeDigits();
        if ($this->index < $this->length && $this->bytes[$this->index] === '.') {
            $isInteger = false;
            $this->index += 1;
            $this->requireDigit();
            $this->consumeDigits();
        }
        if ($this->index < $this->length && ($this->bytes[$this->index] === 'e' || $this->bytes[$this->index] === 'E')) {
            $isInteger = false;
            $this->index += 1;
            if ($this->index < $this->length && ($this->bytes[$this->index] === '+' || $this->bytes[$this->index] === '-')) {
                $this->index += 1;
            }
            $this->requireDigit();
            $this->consumeDigits();
        }
        $text = substr($this->bytes, $start, $this->index - $start);
        // An integer too long for 64 bits is still a number. A field that needs an integer refuses it later.
        if ($isInteger && self::fitsInInt64($text)) {
            return (int) $text;
        }
        $value = (float) $text;
        if (!is_finite($value)) {
            throw new JsonParseException('number out of range', $start);
        }
        return $value;
    }

    /** Whether integer text with an optional minus sign and no leading zeros fits in a signed 64-bit integer. */
    private static function fitsInInt64(string $text): bool
    {
        $negative = $text[0] === '-';
        $digits = $negative ? substr($text, 1) : $text;
        $limit = $negative ? '9223372036854775808' : '9223372036854775807';
        if (strlen($digits) !== strlen($limit)) {
            return strlen($digits) < strlen($limit);
        }
        return strcmp($digits, $limit) <= 0;
    }

    private static function isDigit(string $byte): bool
    {
        $value = ord($byte);
        return $value >= 0x30 && $value <= 0x39;
    }

    private function consumeDigits(): void
    {
        $this->index += strspn($this->bytes, '0123456789', $this->index);
    }

    private function requireDigit(): void
    {
        if (!self::isDigit($this->peek())) {
            throw new JsonParseException('unexpected byte', $this->index);
        }
    }
}
