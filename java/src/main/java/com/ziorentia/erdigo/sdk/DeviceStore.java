package com.ziorentia.erdigo.sdk;

import java.util.List;

/** Persistent device state: its key, its pairings and the connect counter for each receiver. */
public interface DeviceStore {
    DeviceIdentity loadOrCreateIdentity();

    List<ReceiverPairing> pairings();

    /** Adds a pairing, or replaces the one for the same receiver. Its counter floor takes effect here. */
    void save(ReceiverPairing pairing);

    /** Remembers where a receiver was found. Does nothing if the pairing has since been forgotten. */
    void updateAddress(String receiverFingerprint, String host, int port);

    void removePairing(String fingerprint);

    /**
     * Returns a value strictly greater than any previous result for this receiver. The value must be durable before it
     * is returned, because a receiver ignores a counter it has seen.
     */
    long nextConnectCounter(String receiverFingerprint, long nowMs);
}
