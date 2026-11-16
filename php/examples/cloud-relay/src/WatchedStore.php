<?php

declare(strict_types=1);

namespace Erdigo\Examples\CloudRelay;

use Erdigo\ConnectionLock;
use Erdigo\DeviceIdentity;
use Erdigo\FileDeviceStore;
use Erdigo\LockingDeviceStore;
use Erdigo\ReceiverPairing;

/**
 * The gateway's device store as the SDK client sees it: the FileDeviceStore, and a call before each attempt to connect.
 * The client takes the connection lock at the start of every attempt, so this is how the gateway notices the SDK
 * trying again while it holds a request with its sequence number, and how a stop, or with --once giving up, reaches it
 * in the meantime.
 */
final class WatchedStore implements LockingDeviceStore
{
    /** @param \Closure(): void $beforeConnecting May throw, which ends the SDK call. */
    public function __construct(private readonly FileDeviceStore $store, private readonly \Closure $beforeConnecting)
    {
    }

    public function acquireConnectionLock(string $receiverFingerprint, float $wait): ?ConnectionLock
    {
        ($this->beforeConnecting)();
        return $this->store->acquireConnectionLock($receiverFingerprint, $wait);
    }

    public function loadOrCreateIdentity(): DeviceIdentity
    {
        return $this->store->loadOrCreateIdentity();
    }

    /** @return list<ReceiverPairing> */
    public function pairings(): array
    {
        return $this->store->pairings();
    }

    public function save(ReceiverPairing $pairing): void
    {
        $this->store->save($pairing);
    }

    public function updateAddress(string $receiverFingerprint, string $host, int $port): void
    {
        $this->store->updateAddress($receiverFingerprint, $host, $port);
    }

    public function removePairing(string $receiverFingerprint): void
    {
        $this->store->removePairing($receiverFingerprint);
    }

    public function nextConnectCounter(string $receiverFingerprint, int $nowMs): int
    {
        return $this->store->nextConnectCounter($receiverFingerprint, $nowMs);
    }
}
