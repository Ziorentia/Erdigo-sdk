<?php

declare(strict_types=1);

namespace Erdigo\Core\Noise;

/**
 * One direction of a Noise session: a ChaCha20-Poly1305 key and its counter nonce.
 *
 * @internal
 */
final class CipherState
{
    private ?string $key = null;
    private int $counter = 0;

    public function hasKey(): bool
    {
        return $this->key !== null;
    }

    /**
     * `$start` is not zero only in tests of what happens as the counter runs out. PHP integers stop at 2^63-1, so the
     * counter is refused there, well before the 2^64-1 that Noise reserves.
     */
    public function initialize(#[\SensitiveParameter] string $key, int $start = 0): void
    {
        $this->clear();
        $this->key = substr($key, 0, 32);
        $this->counter = $start;
    }

    /** Noise: 32 bits of zeros followed by the little-endian 64-bit counter. */
    private function nonce(): string
    {
        return "\0\0\0\0" . pack('P', $this->counter);
    }

    public function encrypt(string $associatedData, #[\SensitiveParameter] string $plaintext): string
    {
        if ($this->key === null) {
            return $plaintext;
        }
        if ($this->counter >= PHP_INT_MAX) {
            throw new NoiseException(NoiseException::NONCE_EXHAUSTED);
        }
        $output = sodium_crypto_aead_chacha20poly1305_ietf_encrypt($plaintext, $associatedData, $this->nonce(), $this->key);
        $this->counter += 1;
        return $output;
    }

    public function decrypt(string $associatedData, string $ciphertext): string
    {
        if ($this->key === null) {
            return $ciphertext;
        }
        if ($this->counter >= PHP_INT_MAX) {
            throw new NoiseException(NoiseException::NONCE_EXHAUSTED);
        }
        if (strlen($ciphertext) < Noise::TAG_LENGTH) {
            throw new NoiseException(NoiseException::TRUNCATED_MESSAGE);
        }
        $output = sodium_crypto_aead_chacha20poly1305_ietf_decrypt($ciphertext, $associatedData, $this->nonce(), $this->key);
        if ($output === false) {
            throw new NoiseException(NoiseException::DECRYPTION_FAILED);
        }
        $this->counter += 1;
        return $output;
    }

    /** Clears the key. Every later operation fails, rather than falling back to no encryption. */
    public function exhaust(): void
    {
        $this->initialize(str_repeat("\0", 32), PHP_INT_MAX);
    }

    public function clear(): void
    {
        if ($this->key !== null) {
            sodium_memzero($this->key);
            $this->key = null;
        }
    }

    /** @return array<string, string> */
    public function __debugInfo(): array
    {
        return ['key' => $this->key === null ? 'none' : '<secret>'];
    }
}
