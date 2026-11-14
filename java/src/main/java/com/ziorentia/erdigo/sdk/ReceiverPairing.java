package com.ziorentia.erdigo.sdk;

import com.ziorentia.erdigo.core.IntegrationV1;
import com.ziorentia.erdigo.core.ItemKind;
import java.time.Instant;
import java.util.Arrays;
import java.util.List;
import java.util.Objects;

/** What a device remembers about one receiver. A device can be paired with several receivers. */
public final class ReceiverPairing {
    private final String label;
    private final byte[] receiverKey;
    private final List<ItemKind> kinds;
    private final String lastHost;
    private final Integer lastPort;
    private final Instant pairedAt;
    private final long counterFloor;

    public ReceiverPairing(
            String label, byte[] receiverKey, List<ItemKind> kinds, String lastHost, Integer lastPort, Instant pairedAt, long counterFloor) {
        this.label = Objects.requireNonNull(label, "label");
        this.receiverKey = Objects.requireNonNull(receiverKey, "receiverKey").clone();
        this.kinds = List.copyOf(kinds);
        this.lastHost = lastHost;
        this.lastPort = lastPort;
        this.pairedAt = pairedAt == null ? Instant.now() : pairedAt;
        this.counterFloor = counterFloor;
    }

    /** A local name for this receiver. It is never sent anywhere. */
    public String label() {
        return label;
    }

    public byte[] receiverKey() {
        return receiverKey.clone();
    }

    /** The item kinds the person allowed when pairing. */
    public List<ItemKind> kinds() {
        return kinds;
    }

    public String lastHost() {
        return lastHost;
    }

    public Integer lastPort() {
        return lastPort;
    }

    public Instant pairedAt() {
        return pairedAt;
    }

    /**
     * The highest connect counter the receiver had accepted when it approved this pairing. A later counter must be
     * greater, even if this device has lost its own count.
     */
    public long counterFloor() {
        return counterFloor;
    }

    public String fingerprint() {
        return IntegrationV1.fingerprint(receiverKey);
    }

    public ReceiverPairing withAddress(String host, Integer port) {
        return new ReceiverPairing(label, receiverKey, kinds, host, port, pairedAt, counterFloor);
    }

    boolean hasKey(byte[] key) {
        return Arrays.equals(receiverKey, key);
    }

    @Override
    public boolean equals(Object other) {
        return other instanceof ReceiverPairing pairing
                && pairing.label.equals(label)
                && Arrays.equals(pairing.receiverKey, receiverKey)
                && pairing.kinds.equals(kinds)
                && Objects.equals(pairing.lastHost, lastHost)
                && Objects.equals(pairing.lastPort, lastPort)
                && pairing.pairedAt.equals(pairedAt)
                && pairing.counterFloor == counterFloor;
    }

    @Override
    public int hashCode() {
        return Objects.hash(label, fingerprint());
    }

    @Override
    public String toString() {
        return "<pairing '" + label + "' " + IntegrationV1.shortFingerprint(fingerprint()) + ">";
    }
}
