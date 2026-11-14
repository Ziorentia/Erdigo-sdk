<?php

declare(strict_types=1);

namespace Erdigo\Core;

/**
 * The one-time code a person reads from the receiver: 100 random bits as 20 Crockford base32 characters.
 *
 * It is a secret for ten minutes, so it never shows itself in `var_dump`, `print_r` or a string conversion.
 */
final class PairingCode
{
    private const ALPHABET = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';
    private const PSK_SALT = 'erdigo-integration-v1/pairing-psk';

    /** The 20 characters without separators. */
    public readonly string $canonical;

    /**
     * Reads a code as a person may type it: hyphens and white space are ignored, letters are uppercased, O is read as
     * 0, and I and L as 1. Anything else, or any length but 20, is an error.
     */
    public function __construct(#[\SensitiveParameter] string $text)
    {
        $scalars = Utf8::codePoints($text);
        if ($scalars === null) {
            throw new WireException(ErrorCode::InvalidMessage, 'the pairing code has invalid characters');
        }
        $output = '';
        foreach ($scalars as $scalar) {
            if ($scalar === 0x2D || Utf8::isWhiteSpace($scalar)) {
                continue;
            }
            if ($scalar >= 0x80) {
                throw new WireException(ErrorCode::InvalidMessage, 'the pairing code has invalid characters');
            }
            // Only ASCII letters change case, whatever the locale.
            $character = strtr(chr($scalar), 'abcdefghijklmnopqrstuvwxyz', 'ABCDEFGHIJKLMNOPQRSTUVWXYZ');
            $character = match ($character) {
                'O' => '0',
                'I', 'L' => '1',
                default => $character,
            };
            if (!str_contains(self::ALPHABET, $character)) {
                throw new WireException(ErrorCode::InvalidMessage, 'the pairing code has invalid characters');
            }
            $output .= $character;
        }
        if (strlen($output) !== 20) {
            throw new WireException(ErrorCode::InvalidMessage, 'the pairing code has 20 characters');
        }
        $this->canonical = $output;
    }

    /** Creates a new code from the system's secure random source. */
    public static function generate(): self
    {
        $random = random_bytes(20);
        $text = '';
        // Each of the 20 bytes contributes exactly 5 unbiased bits.
        for ($index = 0; $index < 20; $index++) {
            $text .= self::ALPHABET[ord($random[$index]) & 0x1F];
        }
        return new self($text);
    }

    /** `XXXXX-XXXXX-XXXXX-XXXXX`, for showing on the receiver's screen. */
    public function display(): string
    {
        return implode('-', str_split($this->canonical, 5));
    }

    /** The Noise pre-shared key derived from the code. */
    public function presharedKey(): string
    {
        return hash_hkdf('sha256', $this->canonical, 32, '', self::PSK_SALT);
    }

    public function equals(self $other): bool
    {
        return hash_equals($this->canonical, $other->canonical);
    }

    public function __toString(): string
    {
        return '<pairing code>';
    }

    /** @return array<string, string> */
    public function __debugInfo(): array
    {
        return ['canonical' => '<pairing code>'];
    }
}
