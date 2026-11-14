<?php

declare(strict_types=1);

namespace Erdigo;

use Erdigo\Core\IntegrationV1;
use Erdigo\Core\Noise\Noise;

/**
 * The device's one secret: an X25519 key used only for this protocol.
 *
 * It never shows the private key in `var_dump`, `print_r`, `var_export` or a string conversion, and cannot be
 * serialized: the key is kept outside the object's own properties. PHP gives no guarantee that a key is wiped from
 * memory; see the README.
 */
final class DeviceIdentity
{
    /** The public key, which identifies this device to a receiver. */
    public readonly string $publicKey;
    public readonly string $fingerprint;

    /** @var \WeakMap<self, string>|null */
    private static ?\WeakMap $privateKeys = null;

    private function __construct(#[\SensitiveParameter] string $privateKey)
    {
        self::$privateKeys ??= new \WeakMap();
        self::$privateKeys[$this] = $privateKey;
        $this->publicKey = Noise::publicKey($privateKey);
        $this->fingerprint = IntegrationV1::fingerprint($this->publicKey);
    }

    public static function generate(): self
    {
        return new self(Noise::newPrivateKey());
    }

    /** Restores a key read from storage. */
    public static function fromPrivateKey(#[\SensitiveParameter] string $rawPrivateKey): self
    {
        if (strlen($rawPrivateKey) !== Noise::DH_LENGTH) {
            throw new ErdigoException(ErrorKind::Storage, 'a device key is 32 bytes', storageProblem: StorageProblem::IdentityDamaged);
        }
        return new self($rawPrivateKey);
    }

    /** The private key, for the handshake and for writing it to storage. */
    public function privateKey(): string
    {
        return self::$privateKeys[$this] ?? throw new \LogicException('this device identity was cleared');
    }

    /** Clears the private key. The identity cannot be used afterwards. */
    public function clear(): void
    {
        if (isset(self::$privateKeys[$this])) {
            $key = self::$privateKeys[$this];
            unset(self::$privateKeys[$this]);
            sodium_memzero($key);
        }
    }

    public function __toString(): string
    {
        return '<device identity ' . IntegrationV1::shortFingerprint($this->fingerprint) . '>';
    }

    /** @return array<string, string> */
    public function __debugInfo(): array
    {
        return ['fingerprint' => $this->fingerprint];
    }

    /** @return array<string, string> */
    public function __serialize(): array
    {
        throw new \LogicException('a device identity cannot be serialized; keep its key in a FileDeviceStore');
    }

    /** @param array<string, string> $data */
    public function __unserialize(array $data): void
    {
        throw new \LogicException('a device identity cannot be unserialized');
    }
}
