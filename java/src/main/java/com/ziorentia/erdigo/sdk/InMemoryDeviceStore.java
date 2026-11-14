package com.ziorentia.erdigo.sdk;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.Objects;

/** Keeps everything in memory. Intended for tests and short-lived programs. */
public final class InMemoryDeviceStore implements DeviceStore {
    private final List<ReceiverPairing> stored = new ArrayList<>();
    private final Map<String, Long> counters = new HashMap<>();
    private DeviceIdentity identity;

    public InMemoryDeviceStore() {
        this(null);
    }

    public InMemoryDeviceStore(DeviceIdentity identity) {
        this.identity = identity;
    }

    @Override
    public synchronized DeviceIdentity loadOrCreateIdentity() {
        if (identity == null) {
            identity = DeviceIdentity.generate();
        }
        return identity;
    }

    @Override
    public synchronized List<ReceiverPairing> pairings() {
        return List.copyOf(stored);
    }

    @Override
    public synchronized void save(ReceiverPairing pairing) {
        Objects.requireNonNull(pairing, "pairing");
        stored.removeIf(existing -> existing.hasKey(pairing.receiverKey()));
        stored.add(pairing);
        counters.merge(pairing.fingerprint(), pairing.counterFloor(), Math::max);
    }

    @Override
    public synchronized void updateAddress(String receiverFingerprint, String host, int port) {
        for (int index = 0; index < stored.size(); index++) {
            if (stored.get(index).fingerprint().equals(receiverFingerprint)) {
                stored.set(index, stored.get(index).withAddress(host, port));
                return;
            }
        }
    }

    @Override
    public synchronized void removePairing(String fingerprint) {
        stored.removeIf(pairing -> pairing.fingerprint().equals(fingerprint));
        counters.remove(fingerprint);
    }

    @Override
    public synchronized long nextConnectCounter(String receiverFingerprint, long nowMs) {
        long next = Math.max(nowMs, counters.getOrDefault(receiverFingerprint, 0L) + 1);
        counters.put(receiverFingerprint, next);
        return next;
    }
}
