<?php

declare(strict_types=1);

namespace Erdigo;

/**
 * A store shared by several processes, which can keep them from using one device key on two connections at once. A
 * receiver allows one connection per device, so a second one would end the first with `replaced`.
 */
interface LockingDeviceStore extends DeviceStore
{
    /**
     * Waits up to `$wait` seconds to be the only holder of the connection to this receiver. Returns null if another
     * process still holds it then.
     */
    public function acquireConnectionLock(string $receiverFingerprint, float $wait): ?ConnectionLock;
}
