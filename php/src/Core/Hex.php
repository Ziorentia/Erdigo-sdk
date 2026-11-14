<?php

declare(strict_types=1);

namespace Erdigo\Core;

/**
 * Hexadecimal text, the way the protocol writes it.
 */
final class Hex
{
    private function __construct()
    {
    }

    /** Lowercase hexadecimal. */
    public static function encode(string $data): string
    {
        return bin2hex($data);
    }

    /** Accepts lowercase or uppercase hexadecimal digits only, in pairs. Returns null for anything else. */
    public static function decode(string $text): ?string
    {
        if (preg_match('/^(?:[0-9a-fA-F]{2})*$/D', $text) !== 1) {
            return null;
        }
        $data = hex2bin($text);
        return $data === false ? null : $data;
    }

    /** ASCII letters in lowercase, whatever the locale. Everything else is left as it is. */
    public static function lowercase(string $text): string
    {
        return strtr($text, 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz');
    }

    /** True for exactly 64 lowercase hexadecimal digits, the one spelling a fingerprint has (spec section 3). */
    public static function isFingerprint(?string $text): bool
    {
        return $text !== null && preg_match('/^[0-9a-f]{64}$/D', $text) === 1;
    }
}
