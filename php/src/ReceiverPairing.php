<?php

declare(strict_types=1);

namespace Erdigo;

use Erdigo\Core\IntegrationV1;
use Erdigo\Core\ItemKind;

/**
 * What a device remembers about one receiver. A device can be paired with several receivers.
 */
final class ReceiverPairing
{
    public readonly \DateTimeImmutable $pairedAt;

    /**
     * @param string $label A local name for this receiver. It is never sent anywhere.
     * @param string $receiverKey The receiver's public key, 32 bytes.
     * @param list<ItemKind> $kinds What the person allowed.
     * @param int $counterFloor The highest connect counter the receiver had accepted when it approved this pairing. A
     *     later counter must be greater, even if this device has lost its own count.
     */
    public function __construct(
        public readonly string $label,
        public readonly string $receiverKey,
        public readonly array $kinds,
        public readonly ?string $lastHost = null,
        public readonly ?int $lastPort = null,
        ?\DateTimeImmutable $pairedAt = null,
        public readonly int $counterFloor = 0,
    ) {
        $this->pairedAt = $pairedAt ?? new \DateTimeImmutable('now', new \DateTimeZone('UTC'));
    }

    public function fingerprint(): string
    {
        return IntegrationV1::fingerprint($this->receiverKey);
    }

    public function withAddress(string $host, int $port): self
    {
        return new self($this->label, $this->receiverKey, $this->kinds, $host, $port, $this->pairedAt, $this->counterFloor);
    }
}
