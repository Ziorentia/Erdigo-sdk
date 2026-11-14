<?php

declare(strict_types=1);

namespace Erdigo\Core\Noise;

/**
 * The Noise symmetric state: the chaining key, the handshake hash and the cipher state of the handshake.
 *
 * @internal
 */
final class SymmetricState
{
    private readonly CipherState $cipher;
    private ?string $chainingKey;
    private string $hash;

    public function __construct(string $protocolName)
    {
        $this->cipher = new CipherState();
        $this->hash = strlen($protocolName) <= Noise::HASH_LENGTH
            ? str_pad($protocolName, Noise::HASH_LENGTH, "\0")
            : hash('sha256', $protocolName, true);
        $this->chainingKey = $this->hash;
    }

    public function hash(): string
    {
        return $this->hash;
    }

    public function hasKey(): bool
    {
        return $this->cipher->hasKey();
    }

    public function mixHash(string $data): void
    {
        $this->hash = hash('sha256', $this->hash . $data, true);
    }

    public function mixKey(#[\SensitiveParameter] string $inputKeyMaterial): void
    {
        [$chainingKey, $key] = Noise::hkdf($this->chainingKey, $inputKeyMaterial, 2);
        sodium_memzero($this->chainingKey);
        $this->chainingKey = $chainingKey;
        $this->cipher->initialize($key);
        sodium_memzero($key);
    }

    public function mixKeyAndHash(#[\SensitiveParameter] string $inputKeyMaterial): void
    {
        [$chainingKey, $hashInput, $key] = Noise::hkdf($this->chainingKey, $inputKeyMaterial, 3);
        sodium_memzero($this->chainingKey);
        $this->chainingKey = $chainingKey;
        $this->mixHash($hashInput);
        $this->cipher->initialize($key);
        sodium_memzero($hashInput);
        sodium_memzero($key);
    }

    public function encryptAndHash(#[\SensitiveParameter] string $plaintext): string
    {
        $ciphertext = $this->cipher->encrypt($this->hash, $plaintext);
        $this->mixHash($ciphertext);
        return $ciphertext;
    }

    public function decryptAndHash(string $ciphertext): string
    {
        $plaintext = $this->cipher->decrypt($this->hash, $ciphertext);
        $this->mixHash($ciphertext);
        return $plaintext;
    }

    /** @return array{CipherState, CipherState} */
    public function split(): array
    {
        [$firstKey, $secondKey] = Noise::hkdf($this->chainingKey, '', 2);
        $first = new CipherState();
        $second = new CipherState();
        $first->initialize($firstKey);
        $second->initialize($secondKey);
        sodium_memzero($firstKey);
        sodium_memzero($secondKey);
        return [$first, $second];
    }

    public function clear(): void
    {
        sodium_memzero($this->chainingKey);
        $this->chainingKey = '';
        $this->cipher->clear();
    }

    /** @return array<string, string> */
    public function __debugInfo(): array
    {
        return ['hash' => bin2hex($this->hash), 'chainingKey' => '<secret>'];
    }
}
