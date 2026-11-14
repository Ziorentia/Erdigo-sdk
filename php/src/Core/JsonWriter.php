<?php

declare(strict_types=1);

namespace Erdigo\Core;

/**
 * Writes JSON with a stable field order and the same bytes as the other SDKs, so that a retried message is
 * byte-for-byte identical and every SDK writes the published vectors exactly.
 *
 * A PHP list becomes an array, any other PHP array or a {@see JsonObject} an object, with its fields in order. Text
 * must be valid UTF-8, and is written as it is apart from `"`, `\` and control characters. `json_encode` would also
 * escape `/` and, without flags, every non-ASCII character, and its numbers depend on an ini setting.
 */
final class JsonWriter
{
    private const ESCAPES = [
        '"' => '\\"',
        '\\' => '\\\\',
        "\n" => '\\n',
        "\t" => '\\t',
        "\r" => '\\r',
    ];

    private function __construct()
    {
    }

    public static function encode(mixed $value): string
    {
        if ($value === null) {
            return 'null';
        }
        if (is_bool($value)) {
            return $value ? 'true' : 'false';
        }
        if (is_int($value)) {
            return (string) $value;
        }
        if (is_float($value)) {
            return self::formatNumber($value);
        }
        if (is_string($value)) {
            return self::encodeString($value);
        }
        if ($value instanceof JsonObject) {
            $value = $value->fields;
            $isObject = true;
        } elseif (is_array($value)) {
            $isObject = !array_is_list($value);
        } else {
            throw new \InvalidArgumentException('this value cannot be written as JSON');
        }
        $parts = [];
        if ($isObject) {
            foreach ($value as $key => $field) {
                $parts[] = self::encodeString((string) $key) . ':' . self::encode($field);
            }
            return '{' . implode(',', $parts) . '}';
        }
        foreach ($value as $item) {
            $parts[] = self::encode($item);
        }
        return '[' . implode(',', $parts) . ']';
    }

    private static function encodeString(string $text): string
    {
        if (!Utf8::isValid($text)) {
            throw new \InvalidArgumentException('JSON text must be valid UTF-8');
        }
        $escaped = preg_replace_callback(
            '/[\x00-\x1F"\\\\]/',
            static fn (array $match): string => self::ESCAPES[$match[0]] ?? sprintf('\\u%04x', ord($match[0])),
            $text,
        );
        return '"' . $escaped . '"';
    }

    /**
     * The shortest text that reads back as the same number, spelled the way the Swift SDK spells it, so that every
     * SDK writes the same bytes for the same request: `37.0`, `0.0001`, `1e-05`, `1e+16`.
     */
    public static function formatNumber(float $number): string
    {
        if (!is_finite($number)) {
            throw new \InvalidArgumentException('non-finite numbers cannot be written as JSON');
        }
        if ($number == 0.0) {
            return fdiv(1.0, $number) < 0 ? '-0.0' : '0.0';
        }
        $shortest = self::shortestDigits($number);
        $negative = $shortest[0] === '-';
        if ($negative) {
            $shortest = substr($shortest, 1);
        }
        $exponent = 0;
        $exponentAt = strcspn($shortest, 'eE');
        if ($exponentAt < strlen($shortest)) {
            $exponent = (int) substr($shortest, $exponentAt + 1);
            $shortest = substr($shortest, 0, $exponentAt);
        }
        $point = strpos($shortest, '.');
        $whole = $point === false ? $shortest : substr($shortest, 0, $point);
        $fraction = $point === false ? '' : substr($shortest, $point + 1);
        $allDigits = $whole . $fraction;
        $leadingZeros = strlen($allDigits) - strlen(ltrim($allDigits, '0'));
        $digits = rtrim(ltrim($allDigits, '0'), '0');
        // The power of ten of the first significant digit.
        $scientific = strlen($whole) - 1 - $leadingZeros + $exponent;

        $output = $negative ? '-' : '';
        if ($scientific < -4 || abs($number) > 9007199254740992.0) {
            $output .= $digits[0];
            if (strlen($digits) > 1) {
                $output .= '.' . substr($digits, 1);
            }
            $output .= 'e' . ($scientific < 0 ? '-' : '+') . sprintf('%02d', abs($scientific));
        } elseif ($scientific >= 0) {
            $integerDigits = $scientific + 1;
            if (strlen($digits) <= $integerDigits) {
                $output .= $digits . str_repeat('0', $integerDigits - strlen($digits)) . '.0';
            } else {
                $output .= substr($digits, 0, $integerDigits) . '.' . substr($digits, $integerDigits);
            }
        } else {
            $output .= '0.' . str_repeat('0', -$scientific - 1) . $digits;
        }
        return $output;
    }

    /**
     * PHP's shortest round-trip spelling of a number, such as `126.977` or `1.0E-5`. It comes from `var_export` with
     * `serialize_precision` set to -1, which selects the shortest digits that read back as the same number.
     */
    private static function shortestDigits(float $number): string
    {
        $previous = ini_get('serialize_precision');
        ini_set('serialize_precision', '-1');
        try {
            return var_export($number, true);
        } finally {
            ini_set('serialize_precision', $previous === false ? '-1' : $previous);
        }
    }
}
