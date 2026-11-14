<?php

declare(strict_types=1);

namespace Erdigo\Core;

/**
 * UTF-8 without the mbstring or intl extensions, which not every PHP has.
 */
final class Utf8
{
    /** The White_Space characters of Unicode, which .NET's Trim and Rune.IsWhiteSpace use too. */
    private const WHITE_SPACE = [
        0x09 => true, 0x0A => true, 0x0B => true, 0x0C => true, 0x0D => true, 0x20 => true, 0x85 => true, 0xA0 => true,
        0x1680 => true, 0x2000 => true, 0x2001 => true, 0x2002 => true, 0x2003 => true, 0x2004 => true, 0x2005 => true,
        0x2006 => true, 0x2007 => true, 0x2008 => true, 0x2009 => true, 0x200A => true, 0x2028 => true, 0x2029 => true,
        0x202F => true, 0x205F => true, 0x3000 => true,
    ];

    private function __construct()
    {
    }

    /** Strict UTF-8: no overlong forms, no encoded surrogates and nothing above U+10FFFF. */
    public static function isValid(string $bytes): bool
    {
        return preg_match('//u', $bytes) === 1;
    }

    /**
     * The Unicode scalar values of valid UTF-8 text, or null if it is not valid UTF-8.
     *
     * @return list<int>|null
     */
    public static function codePoints(string $text): ?array
    {
        return self::isValid($text) ? self::decodeLenient($text) : null;
    }

    /**
     * The scalar values of text that may hold invalid UTF-8. Each byte that does not start a valid sequence is
     * dropped, like any other character that cannot be shown safely.
     *
     * @return list<int>
     */
    public static function decodeLenient(string $text): array
    {
        $points = [];
        $length = strlen($text);
        $index = 0;
        while ($index < $length) {
            $first = ord($text[$index]);
            if ($first < 0x80) {
                $points[] = $first;
                $index += 1;
                continue;
            }
            $minimumSecond = 0x80;
            $maximumSecond = 0xBF;
            if ($first >= 0xC2 && $first <= 0xDF) {
                $size = 2;
                $value = $first & 0x1F;
            } elseif ($first === 0xE0) {
                $size = 3;
                $minimumSecond = 0xA0;
                $value = $first & 0x0F;
            } elseif (($first >= 0xE1 && $first <= 0xEC) || $first === 0xEE || $first === 0xEF) {
                $size = 3;
                $value = $first & 0x0F;
            } elseif ($first === 0xED) {
                $size = 3;
                $maximumSecond = 0x9F;
                $value = $first & 0x0F;
            } elseif ($first === 0xF0) {
                $size = 4;
                $minimumSecond = 0x90;
                $value = $first & 0x07;
            } elseif ($first >= 0xF1 && $first <= 0xF3) {
                $size = 4;
                $value = $first & 0x07;
            } elseif ($first === 0xF4) {
                $size = 4;
                $maximumSecond = 0x8F;
                $value = $first & 0x07;
            } else {
                $index += 1;
                continue;
            }
            if ($index + $size > $length) {
                $index += 1;
                continue;
            }
            $second = ord($text[$index + 1]);
            $valid = $second >= $minimumSecond && $second <= $maximumSecond;
            for ($offset = 2; $valid && $offset < $size; $offset++) {
                $valid = (ord($text[$index + $offset]) & 0xC0) === 0x80;
            }
            if (!$valid) {
                $index += 1;
                continue;
            }
            for ($offset = 1; $offset < $size; $offset++) {
                $value = ($value << 6) | (ord($text[$index + $offset]) & 0x3F);
            }
            $points[] = $value;
            $index += $size;
        }
        return $points;
    }

    /** The UTF-8 bytes of one scalar value. */
    public static function encode(int $scalar): string
    {
        if ($scalar < 0 || $scalar > 0x10FFFF || ($scalar >= 0xD800 && $scalar <= 0xDFFF)) {
            throw new \InvalidArgumentException('not a Unicode scalar value');
        }
        if ($scalar < 0x80) {
            return chr($scalar);
        }
        if ($scalar < 0x800) {
            return chr(0xC0 | ($scalar >> 6)) . chr(0x80 | ($scalar & 0x3F));
        }
        if ($scalar < 0x10000) {
            return chr(0xE0 | ($scalar >> 12)) . chr(0x80 | (($scalar >> 6) & 0x3F)) . chr(0x80 | ($scalar & 0x3F));
        }
        return chr(0xF0 | ($scalar >> 18)) . chr(0x80 | (($scalar >> 12) & 0x3F))
            . chr(0x80 | (($scalar >> 6) & 0x3F)) . chr(0x80 | ($scalar & 0x3F));
    }

    /** @param list<int> $scalars */
    public static function fromCodePoints(array $scalars): string
    {
        $text = '';
        foreach ($scalars as $scalar) {
            $text .= self::encode($scalar);
        }
        return $text;
    }

    public static function isWhiteSpace(int $scalar): bool
    {
        return isset(self::WHITE_SPACE[$scalar]);
    }

    /** Removes Unicode white space from both ends of valid UTF-8 text. Text that is not valid UTF-8 is returned as it is. */
    public static function trim(string $text): string
    {
        $points = self::codePoints($text);
        if ($points === null) {
            return $text;
        }
        $start = 0;
        $end = count($points);
        while ($start < $end && self::isWhiteSpace($points[$start])) {
            $start += 1;
        }
        while ($end > $start && self::isWhiteSpace($points[$end - 1])) {
            $end -= 1;
        }
        return self::fromCodePoints(array_slice($points, $start, $end - $start));
    }
}
