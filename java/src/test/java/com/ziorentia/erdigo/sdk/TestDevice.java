package com.ziorentia.erdigo.sdk;

import com.ziorentia.erdigo.core.ItemKind;
import java.time.Duration;
import java.util.List;

/** A device paired with a {@link ScriptedReceiver}, with a client ready to write. */
final class TestDevice implements AutoCloseable {
    final DeviceIdentity identity = DeviceIdentity.generate();
    final InMemoryDeviceStore store = new InMemoryDeviceStore(identity);
    final ReceiverPairing pairing;
    final ErdigoClient client;

    TestDevice(ScriptedReceiver receiver, RetryPolicy retry) {
        this(receiver, retry, List.of(ItemKind.values()));
    }

    TestDevice(ScriptedReceiver receiver, RetryPolicy retry, List<ItemKind> kinds) {
        pairing = Pairing.pair(receiver.target(), receiver.qrTicket(), identity, "Test device", kinds, new Pairing.Options());
        store.save(pairing);
        client = new ErdigoClient(identity, pairing, store, receiver.target(), retry, Duration.ofSeconds(2), Duration.ofSeconds(5));
    }

    String fingerprint() {
        return identity.fingerprint();
    }

    @Override
    public void close() {
        client.close();
        identity.close();
    }
}
