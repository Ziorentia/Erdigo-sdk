<?php

declare(strict_types=1);

namespace Erdigo\Core;

/**
 * Rules for text that a person will eventually read on the receiver's screen.
 */
final class TextRules
{
    private function __construct()
    {
    }

    /** Control characters and invisible direction overrides, which can make text read differently from what it is. */
    public static function isDisallowed(int $scalar, bool $allowLineBreaks): bool
    {
        if ($scalar === 0x0A || $scalar === 0x09) {
            return !$allowLineBreaks;
        }
        if ($scalar <= 0x1F || ($scalar >= 0x7F && $scalar <= 0x9F)) {
            return true;
        }
        return $scalar === 0x061C || $scalar === 0x200E || $scalar === 0x200F
            || ($scalar >= 0x202A && $scalar <= 0x202E) || ($scalar >= 0x2066 && $scalar <= 0x2069)
            || $scalar === 0x2028 || $scalar === 0x2029 || $scalar === 0xFEFF;
    }

    public static function validateTitle(string $text, ?Limits $limits = null): void
    {
        $limits ??= Limits::v1();
        $scalars = Utf8::codePoints($text);
        if ($scalars === null) {
            throw new WireException(ErrorCode::InvalidText, 'a title must be valid UTF-8 text');
        }
        if ($scalars === []) {
            throw new WireException(ErrorCode::InvalidText, 'a title cannot be empty; leave it out instead');
        }
        if (count($scalars) > $limits->titleScalars) {
            throw new WireException(ErrorCode::TooLarge, "a title is at most {$limits->titleScalars} characters");
        }
        foreach ($scalars as $scalar) {
            if (self::isDisallowed($scalar, false)) {
                throw new WireException(ErrorCode::InvalidText, 'a title cannot contain control or text-direction characters');
            }
        }
    }

    public static function validateNote(string $text, ?Limits $limits = null): void
    {
        $limits ??= Limits::v1();
        $scalars = Utf8::codePoints($text);
        if ($scalars === null) {
            throw new WireException(ErrorCode::InvalidText, 'a note must be valid UTF-8 text');
        }
        if ($scalars === []) {
            throw new WireException(ErrorCode::InvalidText, 'a note cannot be empty');
        }
        if (strlen($text) > $limits->noteBytes) {
            throw new WireException(ErrorCode::TooLarge, "a note is at most {$limits->noteBytes} bytes of UTF-8");
        }
        foreach ($scalars as $scalar) {
            if (self::isDisallowed($scalar, true)) {
                throw new WireException(ErrorCode::InvalidText, 'a note cannot contain control or text-direction characters');
            }
        }
    }

    /**
     * What a receiver shows of a device name, which it treats as untrusted: unsafe characters and bytes that are not
     * UTF-8 are removed, white space is trimmed, and it is cut to 40 characters.
     */
    public static function sanitizeDeviceName(string $text): string
    {
        $kept = array_values(array_filter(
            Utf8::decodeLenient($text),
            static fn (int $scalar): bool => !self::isDisallowed($scalar, false),
        ));
        $start = 0;
        $end = count($kept);
        while ($start < $end && Utf8::isWhiteSpace($kept[$start])) {
            $start += 1;
        }
        while ($end > $start && Utf8::isWhiteSpace($kept[$end - 1])) {
            $end -= 1;
        }
        $length = min($end - $start, IntegrationV1::MAX_DEVICE_NAME_SCALARS);
        return Utf8::fromCodePoints(array_slice($kept, $start, $length));
    }
}
