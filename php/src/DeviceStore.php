<?php

declare(strict_types=1);

namespace Erdigo;

/**
 * Persistent device state: its key, its pairings and the per-receiver connect counter.
 */
interface DeviceStore
{
    public function loadOrCreateIdentity(): DeviceIdentity;

    /** @return list<ReceiverPairing> */
    public function pairings(): array;

    /** Adds a pairing, or replaces the one for the same receiver. Its counter floor takes effect here. */
    public function save(ReceiverPairing $pairing): void;

    /** Remembers where a receiver was found. Does nothing if the pairing has since been forgotten. */
    public function updateAddress(string $receiverFingerprint, string $host, int $port): void;

    public function removePairing(string $receiverFingerprint): void;

    /**
     * Returns a value strictly greater than any previous result for this receiver. The value must be durable before
     * it is returned, because a receiver ignores a counter it has seen.
     */
    public function nextConnectCounter(string $receiverFingerprint, int $nowMs): int;
}
