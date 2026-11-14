package com.ziorentia.erdigo.sdk;

import java.util.Objects;

/** Where to look for a receiver. */
public sealed interface ReceiverTarget {
    /**
     * Try the remembered address, then up to eight receivers advertised on the local network. A receiver this device is
     * not paired with stays silent. An advertisement proves nothing about who is behind it, so use {@link #address}
     * where that matters. See the threat model.
     */
    static ReceiverTarget discover() {
        return Discover.INSTANCE;
    }

    /** Try this address only. */
    static ReceiverTarget address(String host, int port) {
        return new Address(host, port);
    }

    /** See {@link ReceiverTarget#discover()}. */
    enum Discover implements ReceiverTarget {
        INSTANCE
    }

    /** See {@link ReceiverTarget#address}. */
    record Address(String host, int port) implements ReceiverTarget {
        public Address {
            Objects.requireNonNull(host, "host");
            if (port < 1 || port > 65535) {
                throw new ErdigoException(ErdigoException.Kind.INVALID_ARGUMENT, "the port must be between 1 and 65535");
            }
        }
    }
}
