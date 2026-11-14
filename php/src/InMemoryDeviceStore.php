<?php

declare(strict_types=1);

namespace Erdigo;

/**
 * Keeps everything in memory. Intended for tests and short-lived programs.
 */
final class InMemoryDeviceStore implements DeviceStore
{
    /** @var list<ReceiverPairing> */
    private array $stored = [];
    /** @var array<string, int> */
    private array $counters = [];

    public function __construct(private ?DeviceIdentity $identity = null)
    {
    }

    public function loadOrCreateIdentity(): DeviceIdentity
    {
        return $this->identity ??= DeviceIdentity::generate();
    }

    public function pairings(): array
    {
        return $this->stored;
    }

    public function save(ReceiverPairing $pairing): void
    {
        $this->stored = array_values(array_filter(
            $this->stored,
            static fn (ReceiverPairing $existing): bool => $existing->receiverKey !== $pairing->receiverKey,
        ));
        $this->stored[] = $pairing;
        $fingerprint = $pairing->fingerprint();
        $this->counters[$fingerprint] = max($this->counters[$fingerprint] ?? 0, $pairing->counterFloor);
    }

    public function updateAddress(string $receiverFingerprint, string $host, int $port): void
    {
        foreach ($this->stored as $index => $pairing) {
            if ($pairing->fingerprint() === $receiverFingerprint) {
                $this->stored[$index] = $pairing->withAddress($host, $port);
            }
        }
    }

    public function removePairing(string $receiverFingerprint): void
    {
        $this->stored = array_values(array_filter(
            $this->stored,
            static fn (ReceiverPairing $pairing): bool => $pairing->fingerprint() !== $receiverFingerprint,
        ));
        unset($this->counters[$receiverFingerprint]);
    }

    public function nextConnectCounter(string $receiverFingerprint, int $nowMs): int
    {
        $next = max($nowMs, ($this->counters[$receiverFingerprint] ?? 0) + 1);
        $this->counters[$receiverFingerprint] = $next;
        return $next;
    }
}
