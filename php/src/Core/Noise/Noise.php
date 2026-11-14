<?php

declare(strict_types=1);

namespace Erdigo\Core\Noise;

/**
 * Constants and primitives of the Noise layer: X25519, ChaCha20-Poly1305 and SHA-256, from PHP's bundled sodium and
 * hash extensions.
 */
final class Noise
{
    public const DH_LENGTH = 32;
    public const HASH_LENGTH = 32;
    public const TAG_LENGTH = 16;
    public const MAX_MESSAGE_LENGTH = 65535;

    private function __construct()
    {
    }

    /**
     * X25519 ignores the top bit of a public key, so one private key has more than one spelling. A receiver identifies
     * devices by these bytes, so only the canonical spelling is accepted: below 2^255-19, top bit clear.
     */
    public static function isCanonicalPublicKey(string $key): bool
    {
        if (strlen($key) !== self::DH_LENGTH || (ord($key[31]) & 0x80) !== 0) {
            return false;
        }
        // Values from 2^255-19 up to 2^255-1 are not canonical either.
        $isAtLeastPrime = ord($key[31]) === 0x7F && ord($key[0]) >= 0xED;
        for ($index = 1; $index <= 30 && $isAtLeastPrime; $index++) {
            $isAtLeastPrime = ord($key[$index]) === 0xFF;
        }
        return !$isAtLeastPrime;
    }

    public static function hmac(#[\SensitiveParameter] string $key, #[\SensitiveParameter] string $data): string
    {
        return hash_hmac('sha256', $data, $key, true);
    }

    /**
     * The Noise HKDF. Note that the input keying material is the HMAC message, keyed by the chaining key.
     *
     * @return list<string>
     */
    public static function hkdf(#[\SensitiveParameter] string $chainingKey, #[\SensitiveParameter] string $inputKeyMaterial, int $outputs): array
    {
        $temporary = self::hmac($chainingKey, $inputKeyMaterial);
        $results = [];
        $previous = '';
        for ($index = 1; $index <= $outputs; $index++) {
            $previous = self::hmac($temporary, $previous . chr($index));
            $results[] = $previous;
        }
        sodium_memzero($temporary);
        return $results;
    }

    /** A new X25519 private key from the system's secure random source. */
    public static function newPrivateKey(): string
    {
        return random_bytes(self::DH_LENGTH);
    }

    /** The public key of an X25519 private key. */
    public static function publicKey(#[\SensitiveParameter] string $privateKey): string
    {
        if (strlen($privateKey) !== self::DH_LENGTH) {
            throw new NoiseException(NoiseException::INVALID_KEY);
        }
        return sodium_crypto_scalarmult_base($privateKey);
    }

    /** X25519 with an explicit rejection of low-order public keys, which produce an all-zero secret. */
    public static function dh(#[\SensitiveParameter] string $privateKey, string $publicKey): string
    {
        if (strlen($privateKey) !== self::DH_LENGTH || strlen($publicKey) !== self::DH_LENGTH) {
            throw new NoiseException(NoiseException::INVALID_KEY);
        }
        try {
            $secret = sodium_crypto_scalarmult($privateKey, $publicKey);
        } catch (\SodiumException) {
            // libsodium itself refuses an all-zero result.
            throw new NoiseException(NoiseException::WEAK_SHARED_SECRET);
        }
        // Checked here as well, in constant time, whatever the libsodium version does.
        if (hash_equals(str_repeat("\0", self::DH_LENGTH), $secret)) {
            throw new NoiseException(NoiseException::WEAK_SHARED_SECRET);
        }
        return $secret;
    }
}
